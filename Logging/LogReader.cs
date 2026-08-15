using Microsoft.Data.Sqlite;

namespace SmartBase.Logging;

/// <summary>
/// Чтение диагностического лога <c>logs.db</c> для таблицы «Профиль → Логи»
/// (ProfileLogsTable). Схему таблицы <c>logs</c> задаёт приёмник Serilog.Sinks.SQLite:
/// столбцы <c>id</c> (INTEGER PRIMARY KEY AUTOINCREMENT), <c>Timestamp</c> (TEXT, UTC),
/// <c>Level</c>, <c>Exception</c>, <c>RenderedMessage</c>, <c>Properties</c>. Пишет туда
/// только Serilog — здесь мы лишь читаем (raw SQL, БЕЗ EF: у logs.db нет модели EF).
///
/// Аналог <c>ConstructorRepository.GetChangeLogPageAsync</c> для таблицы истории: фильтры
/// и сортировка считаются в SQL, страницы — keyset (по <c>id</c>, при строковой сортировке —
/// по паре «значение + id»), поэтому большой журнал листается без загрузки в память.
/// Соединение — отдельное read-only на фоновом потоке, UI не фризит.
///
/// Ключи колонок (как в <see cref="LogQuery"/>): date/level/message/exception.
/// </summary>
public sealed class LogReader
{
    /// <summary>Строка журнала для таблицы логов.</summary>
    public sealed record LogRow(long Id, string Timestamp, string Level, string Message, string? Exception);

    /// <summary>
    /// Параметры страницы лога. Filters — колонка → допустимые СЫРЫЕ значения (пустой
    /// словарь = без фильтра). Сортировка — по одной колонке; для date хронология =
    /// порядок id (журнал только дописывается), отдельного ORDER BY по строке Timestamp
    /// не нужно. AfterValue/AfterId — keyset-курсор последней показанной строки.
    /// </summary>
    public sealed record LogQuery
    {
        public IReadOnlyDictionary<string, IReadOnlyCollection<string>> Filters { get; init; }
            = new Dictionary<string, IReadOnlyCollection<string>>();
        public string? SortColumn { get; init; }
        public bool    SortAsc    { get; init; }
        public string? AfterValue { get; init; }
        public long?   AfterId    { get; init; }
    }

    private readonly string _path;

    public LogReader(string path) => _path = path;

    /// <summary>Файл лога ещё не создан (Serilog пишет его при первой записи).</summary>
    public bool Exists => File.Exists(_path);

    // SQL-выражение колонки по ключу — для WHERE-фильтров, keyset и агрегатов фильтра.
    // exception нормализуем через IFNULL: NULL и '' в фильтре — одно значение «(Пусто)».
    private static string ColumnExpr(string key) => key switch
    {
        "date"      => "substr(Timestamp,1,10)",
        "level"     => "Level",
        "message"   => "RenderedMessage",
        "exception" => "IFNULL(Exception,'')",
        _           => "Level",
    };

    /// <summary>
    /// Страница лога: фильтры и сортировка в SQL, keyset-срез после курсора, LIMIT take.
    /// Открывается мгновенно, читается на фоне (отдельное read-only подключение).
    /// </summary>
    public Task<List<LogRow>> GetPageAsync(LogQuery query, int take)
    {
        var path = _path;
        return Task.Run(() =>
        {
            var rows = new List<LogRow>();
            if (!File.Exists(path)) return rows;

            using var conn = OpenRead(path);
            using var cmd  = conn.CreateCommand();

            var where = new List<string>();
            AppendFilters(cmd, query.Filters, where);
            AppendKeyset(cmd, query, where);

            var orderBy = BuildOrderBy(query);
            cmd.CommandText =
                "SELECT id, Timestamp, Level, RenderedMessage, Exception FROM logs" +
                (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                $" ORDER BY {orderBy} LIMIT @take";
            cmd.Parameters.AddWithValue("@take", take);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new LogRow(
                    r.GetInt64(0),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? "" : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4)));
            }
            return rows;
        });
    }

    /// <summary>Заголовки CSV-выгрузки — в порядке колонок таблицы «Диагностика».</summary>
    public static readonly string[] CsvHeaders = ["Время", "Уровень", "Сообщение", "Ошибка"];

    /// <summary>
    /// Сколько строк отдаст выгрузка с этими фильтрами — число для окна подтверждения
    /// и знаменатель прогресса. Считается в SQL (COUNT(*) с теми же WHERE), сортировка
    /// и курсор роли не играют.
    /// </summary>
    public Task<long> CountAsync(LogQuery query)
    {
        var path = _path;
        return Task.Run(() =>
        {
            if (!File.Exists(path)) return 0L;

            using var conn = OpenRead(path);
            using var cmd  = conn.CreateCommand();
            var where = new List<string>();
            AppendFilters(cmd, query.Filters, where);
            cmd.CommandText = "SELECT COUNT(*) FROM logs" +
                              (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "");
            return Convert.ToInt64(cmd.ExecuteScalar());
        });
    }

    /// <summary>
    /// Ленивый поток строк лога для выгрузки в CSV: значения уже человеческие (уровень
    /// по-русски, время локальное), порядок и фильтры — как в таблице. Метод-итератор
    /// (yield) — вызывать из фонового потока.
    ///
    /// Два режима, как у выгрузки журнала изменений. Хронология (без сортировки и «Время») —
    /// keyset-страницы по <paramref name="pageSize"/>: срез по <c>id</c>, короткие
    /// транзакции, приёмник Serilog продолжает писать лог во время выгрузки. Сортировка
    /// по строковой колонке — ОДИН запрос с ORDER BY и потоковое чтение: индексов на
    /// Level/RenderedMessage нет, и страницами база пересортировывала бы всю таблицу
    /// на каждую страницу.
    /// </summary>
    public IEnumerable<string[]> ReadCsvRows(LogQuery query, CancellationToken ct = default,
                                             int pageSize = 5000)
    {
        foreach (var row in CsvSource(query, ct, pageSize))
        {
            ct.ThrowIfCancellationRequested();
            yield return
            [
                LogLabels.FormatTs(row.Timestamp),
                LogLabels.LevelLabel(row.Level),
                row.Message,
                row.Exception ?? "",
            ];
        }
    }

    private IEnumerable<LogRow> CsvSource(LogQuery query, CancellationToken ct, int pageSize)
    {
        if (!File.Exists(_path)) yield break;

        if (!IsIdOrder(query))
        {
            using var conn = OpenRead(_path);
            using var cmd  = conn.CreateCommand();
            var where = new List<string>();
            AppendFilters(cmd, query.Filters, where);
            cmd.CommandText =
                "SELECT id, Timestamp, Level, RenderedMessage, Exception FROM logs" +
                (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                $" ORDER BY {BuildOrderBy(query)}";

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                yield return new LogRow(
                    r.GetInt64(0),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? "" : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4));
            }
            yield break;
        }

        var cursor = query;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // GetPageAsync уже умеет фильтры + keyset; ждём синхронно — мы и так в фоне.
            var page = GetPageAsync(cursor, pageSize).GetAwaiter().GetResult();
            if (page.Count == 0) yield break;

            foreach (var row in page)
            {
                ct.ThrowIfCancellationRequested();
                yield return row;
            }

            if (page.Count < pageSize) yield break;
            cursor = cursor with { AfterId = page[^1].Id, AfterValue = null };
        }
    }

    /// <summary>
    /// Значения колонки с количеством — для окна фильтра таблицы логов (SQL-агрегат на
    /// фоне). Аналог <c>GetChangeLogCountsAsync</c>. date группируется по дню.
    /// </summary>
    public Task<List<(string Value, int Count)>> GetCountsAsync(string column)
    {
        var path = _path;
        return Task.Run(() =>
        {
            var result = new List<(string, int)>();
            if (!File.Exists(path)) return result;

            using var conn = OpenRead(path);
            using var cmd  = conn.CreateCommand();
            var expr = ColumnExpr(column);
            cmd.CommandText = $"SELECT {expr} AS v, COUNT(*) FROM logs GROUP BY v";

            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add((r.IsDBNull(0) ? "" : r.GetString(0), r.GetInt32(1)));
            return result;
        });
    }

    // ── SQL-хелперы ──────────────────────────────────────────────────────────

    private static SqliteConnection OpenRead(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode       = SqliteOpenMode.ReadOnly,
            Cache      = SqliteCacheMode.Shared,
        }.ToString());
        conn.Open();
        // Параллельная запись приёмника не должна ронять чтение с SQLITE_BUSY.
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=3000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    // col IN (@f0,@f1,…) для каждого активного фильтра. Значения — СЫРЫЕ (как в GetCounts).
    private static void AppendFilters(SqliteCommand cmd,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> filters, List<string> where)
    {
        var i = 0;
        foreach (var (column, values) in filters)
        {
            if (values.Count == 0) continue;
            var names = new List<string>();
            foreach (var v in values)
            {
                var p = $"@f{i++}";
                names.Add(p);
                cmd.Parameters.AddWithValue(p, v);
            }
            where.Add($"{ColumnExpr(column)} IN ({string.Join(",", names)})");
        }
    }

    // Keyset-срез после курсора. Для id-порядка (без сортировки / date) — по id; для
    // строковой сортировки — «(значение после курсора) ИЛИ (то же значение И id < курсора)».
    private static void AppendKeyset(SqliteCommand cmd, LogQuery query, List<string> where)
    {
        if (query.AfterId is not long id) return;

        if (IsIdOrder(query))
        {
            var dateAsc = query.SortColumn == "date" && query.SortAsc;
            where.Add(dateAsc ? "id > @afterId" : "id < @afterId");
        }
        else
        {
            var col = ColumnExpr(query.SortColumn!);
            var cmp = query.SortAsc ? ">" : "<";
            where.Add($"({col} COLLATE NOCASE {cmp} @afterValue " +
                      $"OR ({col} COLLATE NOCASE = @afterValue AND id < @afterId))");
            cmd.Parameters.AddWithValue("@afterValue", query.AfterValue ?? "");
        }
        cmd.Parameters.AddWithValue("@afterId", id);
    }

    // ORDER BY. Хронология (date/без сортировки) = порядок id; строковые колонки —
    // NOCASE + добор равных по id DESC (как ThenByDescending(Id) в таблице истории).
    private static string BuildOrderBy(LogQuery query)
    {
        if (IsIdOrder(query))
            return query.SortColumn == "date" && query.SortAsc ? "id ASC" : "id DESC";

        var col = ColumnExpr(query.SortColumn!);
        var dir = query.SortAsc ? "ASC" : "DESC";
        return $"{col} COLLATE NOCASE {dir}, id DESC";
    }

    private static bool IsIdOrder(LogQuery query) => query.SortColumn is null or "date";
}
