using System.Globalization;
using Microsoft.Data.Sqlite;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>Каким каналом шел обмен — колонка журнала и разрез в отчете.</summary>
public enum RunChannel
{
    /// <summary>Запрос к чужой программе по сети.</summary>
    Request = 0,

    /// <summary>Файл из папки обмена.</summary>
    Inbox,
}

/// <summary>
/// Строка журнала запусков: что дергали, чем кончилось, сколько прошло данных.
/// </summary>
/// <param name="Id">Ключ строки; 0 — строка еще не записана.</param>
/// <param name="At">Когда (ISO-8601, UTC).</param>
/// <param name="Channel">Канал.</param>
/// <param name="Name">Что запускали: имя запроса либо имя файла.</param>
/// <param name="Address">Куда ходили — БЕЗ секрета. В журнал попадает только замаскированный
/// адрес: журнал экспортируют и пересылают, и токен в нем — это утечка.</param>
/// <param name="Ok">Обмен удался.</param>
/// <param name="Explanation">Что случилось, человеческим языком.</param>
/// <param name="Rows">Сколько записей прошло.</param>
/// <param name="Bytes">Сколько байт получено или отправлено.</param>
/// <param name="Ms">Сколько заняло, миллисекунд.</param>
/// <param name="DbId">Ключ базы, которую задел запуск; 0 — ни одной.</param>
/// <param name="Counts">Считается ли запуск в накопительные квоты политики. Обмен файлами не
/// считается: квоты ограничивают то, что уходит наружу, а файл из папки идет внутрь.</param>
public sealed record RunEntry(
    long       Id,
    string     At,
    RunChannel Channel,
    string     Name,
    string     Address,
    bool       Ok,
    string     Explanation,
    int        Rows,
    long       Bytes,
    long       Ms,
    int        DbId,
    bool       Counts)
{
    /// <summary>Новая строка «сейчас»; момент передается явно — тест не должен зависеть от часов.</summary>
    public static RunEntry New(RunChannel channel, string name, string address, bool ok,
                               string explanation, int rows, long bytes, TimeSpan elapsed,
                               int dbId, DateTimeOffset at, bool counts) =>
        new(0, at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            channel, name, address, ok, explanation, rows, bytes,
            (long)elapsed.TotalMilliseconds, dbId, counts);

    /// <summary>Момент записи; <c>null</c> — строка испорчена правкой файла руками.</summary>
    public DateTimeOffset? Moment =>
        DateTimeOffset.TryParse(At, CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out var moment)
            ? moment
            : null;
}

/// <summary>
/// Журнал запусков обмена — рабочий журнал для отладки интеграций (docs/ROADMAP-API.md, 10.10).
///
/// Свой файл <c>integrations.db</c> рядом с <c>app.db</c> и <c>logs.db</c>, а не таблица внутри
/// файла базы данных, и это решение, а не случайность: файл базы ездит по сетевой папке, и тела
/// запросов раздули бы его на всех машинах разом. И не «Диагностика»: Serilog хранит текст, а
/// здесь нужны числа, по которым считаются квоты.
///
/// <b>Секрета в журнале нет ни при каких настройках.</b> Адрес записывается замаскированным —
/// тем самым <c>SafeAddress</c>, который готовит отправляющий слой. Журнал выгружают и
/// пересылают, и токен в нем — это утечка, которую потом не отозвать.
///
/// <b>Журнал общий на все базы</b>, а не по базе: соединения и запросы тоже локальные, а один
/// запуск может задеть несколько баз. Ключ базы пишется полем строки.
///
/// Схема заводится аддитивно (<c>CREATE TABLE IF NOT EXISTS</c>): файл никогда не
/// пересоздается, иначе обновление программы стирало бы историю обмена.
/// </summary>
public sealed class RunJournal
{
    /// <summary>Имя файла журнала в папке данных приложения.</summary>
    public const string FileName = "integrations.db";

    /// <summary>
    /// Сколько строк хранить. Журнал чистит сам себя: обмен, идущий каждые полчаса, за год дает
    /// десятки тысяч строк, и разбираться в них все равно никто не будет, а файл вырастет.
    /// </summary>
    public const int Keep = 20_000;

    private readonly string _path;
    private readonly object _lock = new();

    public RunJournal(string path)
    {
        _path = path;
        Ensure();
    }

    /// <summary>Путь к файлу — для экрана «где это лежит» и для тестов.</summary>
    public string Path => _path;

    // ── Схема ─────────────────────────────────────────────────────────────────

    private void Ensure()
    {
        using var connection = Open();
        using var command    = connection.CreateCommand();

        command.CommandText = """
            CREATE TABLE IF NOT EXISTS runs (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                At          TEXT    NOT NULL,
                Channel     INTEGER NOT NULL,
                Name        TEXT    NOT NULL,
                Address     TEXT    NOT NULL,
                Ok          INTEGER NOT NULL,
                Explanation TEXT    NOT NULL,
                Rows        INTEGER NOT NULL,
                Bytes       INTEGER NOT NULL,
                Ms          INTEGER NOT NULL,
                DbId        INTEGER NOT NULL,
                Counts      INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS runs_at ON runs (At);
            """;

        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();

        using var pragma = connection.CreateCommand();
        // WAL — по той же причине, что и у остальных файлов: журнал пишется в фоне, а читается
        // экраном, и блокировать одно другим незачем.
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    // ── Запись ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Записать строку. Возвращает ее ключ; 0 — записать не вышло.
    ///
    /// Сбой записи журнала НЕ роняет обмен: запись уже уложена, и падать из-за того, что не
    /// удалось об этом рассказать, значит терять сделанную работу. Про сбой узнает
    /// «Диагностика».
    /// </summary>
    public long Write(RunEntry entry)
    {
        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                command.CommandText = """
                    INSERT INTO runs (At, Channel, Name, Address, Ok, Explanation, Rows, Bytes, Ms, DbId, Counts)
                    VALUES ($at, $channel, $name, $address, $ok, $explanation, $rows, $bytes, $ms, $db, $counts);
                    SELECT last_insert_rowid();
                    """;

                command.Parameters.AddWithValue("$at",          entry.At);
                command.Parameters.AddWithValue("$channel",     (int)entry.Channel);
                command.Parameters.AddWithValue("$name",        entry.Name ?? "");
                command.Parameters.AddWithValue("$address",     entry.Address ?? "");
                command.Parameters.AddWithValue("$ok",          entry.Ok ? 1 : 0);
                command.Parameters.AddWithValue("$explanation", entry.Explanation ?? "");
                command.Parameters.AddWithValue("$rows",        entry.Rows);
                command.Parameters.AddWithValue("$bytes",       entry.Bytes);
                command.Parameters.AddWithValue("$ms",          entry.Ms);
                command.Parameters.AddWithValue("$db",          entry.DbId);
                command.Parameters.AddWithValue("$counts",      entry.Counts ? 1 : 0);

                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
        catch (SqliteException failure)
        {
            Serilog.Log.Warning("Журнал обмена: строка не записалась — {Reason:l}", failure.Message);
            return 0;
        }
    }

    // ── Чтение ────────────────────────────────────────────────────────────────

    /// <summary>Последние строки, свежие сверху.</summary>
    public IReadOnlyList<RunEntry> Recent(int take = 200)
    {
        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                command.CommandText = """
                    SELECT Id, At, Channel, Name, Address, Ok, Explanation, Rows, Bytes, Ms, DbId, Counts
                    FROM runs ORDER BY Id DESC LIMIT $take;
                    """;

                command.Parameters.AddWithValue("$take", Math.Max(1, take));

                var rows = new List<RunEntry>();
                using var reader = command.ExecuteReader();

                while (reader.Read())
                    rows.Add(new RunEntry(
                        reader.GetInt64(0), reader.GetString(1), (RunChannel)reader.GetInt32(2),
                        reader.GetString(3), reader.GetString(4), reader.GetInt32(5) != 0,
                        reader.GetString(6), reader.GetInt32(7), reader.GetInt64(8),
                        reader.GetInt64(9), reader.GetInt32(10), reader.GetInt32(11) != 0));

                return rows;
            }
        }
        catch (SqliteException failure)
        {
            Serilog.Log.Warning("Журнал обмена: не читается — {Reason:l}", failure.Message);
            return [];
        }
    }

    // ── Накопительные счетчики ────────────────────────────────────────────────

    /// <summary>
    /// Сколько запусков и байт засчитано с указанного момента. Считаются только строки с
    /// <see cref="RunEntry.Counts"/>: квоты ограничивают то, что уходит НАРУЖУ, а файл из папки
    /// идет внутрь и к ним отношения не имеет.
    ///
    /// Считаются и НЕудачные запуски тоже: отказ чужой программы все равно был обращением к
    /// ней, и не считать его значило бы разрешить бесконечный перебор в ответ на ошибки.
    /// </summary>
    public (int Runs, long Bytes) Since(DateTimeOffset moment)
    {
        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                command.CommandText = """
                    SELECT COUNT(*), IFNULL(SUM(Bytes), 0) FROM runs
                    WHERE Counts = 1 AND At >= $since;
                    """;

                command.Parameters.AddWithValue("$since",
                    moment.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));

                using var reader = command.ExecuteReader();

                return reader.Read()
                    ? (reader.GetInt32(0), reader.GetInt64(1))
                    : (0, 0L);
            }
        }
        catch (SqliteException failure)
        {
            // Счетчик не прочитался — считаем, что квота выбрана. Отсутствие настройки обязано
            // быть безопасным состоянием: битый журнал не должен снимать ограничения.
            Serilog.Log.Warning("Журнал обмена: счетчики не читаются — {Reason:l}", failure.Message);
            return (int.MaxValue, long.MaxValue);
        }
    }

    // ── Уборка ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Оставить последние <see cref="Keep"/> строк. Возвращает, сколько удалено.
    ///
    /// Уборка идет по КЛЮЧУ, а не по дате: часы на машине переводят, а ключ растет всегда.
    /// </summary>
    public int Trim(int keep = Keep)
    {
        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();

                command.CommandText = """
                    DELETE FROM runs WHERE Id <= (
                        SELECT IFNULL(MAX(Id), 0) - $keep FROM runs
                    );
                    """;

                command.Parameters.AddWithValue("$keep", Math.Max(1, keep));

                return command.ExecuteNonQuery();
            }
        }
        catch (SqliteException failure)
        {
            Serilog.Log.Warning("Журнал обмена: уборка не удалась — {Reason:l}", failure.Message);
            return 0;
        }
    }

    /// <summary>
    /// Очистить журнал целиком. Рабочий журнал чистить пользователю МОЖНО — в отличие от
    /// журнала безопасности, который заводится отдельным файлом и не чистится никем.
    /// </summary>
    public void Clear()
    {
        try
        {
            lock (_lock)
            {
                using var connection = Open();
                using var command    = connection.CreateCommand();
                command.CommandText = "DELETE FROM runs;";
                command.ExecuteNonQuery();
            }
        }
        catch (SqliteException failure)
        {
            Serilog.Log.Warning("Журнал обмена: очистка не удалась — {Reason:l}", failure.Message);
        }
    }

    // ── Подключение к шлюзу ───────────────────────────────────────────────────

    /// <summary>
    /// Счетчик расхода для <see cref="EgressGate"/>: шлюз решает, а считает журнал. Разделение
    /// то же, что у политики, — в шлюзе нет ввода-вывода, и проверять его решения можно без
    /// файлов.
    /// </summary>
    public sealed class Usage(RunJournal journal) : IEgressUsage
    {
        public int RunsSince(DateTimeOffset moment) => journal.Since(moment).Runs;

        public long BytesSince(DateTimeOffset moment) => journal.Since(moment).Bytes;
    }

    /// <summary>Счетчик расхода поверх этого журнала.</summary>
    public IEgressUsage AsUsage() => new Usage(this);

    // ── Единственный экземпляр на приложение ──────────────────────────────────

    /// <summary>
    /// Журнал приложения; <c>null</c> — еще не подключен. Прием тот же, что у
    /// <c>AppSettings.AttachStore</c> и <c>EgressGate.AttachPolicy</c>: контейнера зависимостей
    /// в приложении нет, а тащить журнал параметром через десяток экранов незачем.
    ///
    /// Писать через <c>null</c> безопасно — <see cref="Add"/> просто ничего не сделает. Обмен
    /// не должен падать из-за того, что о нем не удалось рассказать.
    /// </summary>
    public static RunJournal? Current { get; private set; }

    /// <summary>Подключить журнал приложения; <c>null</c> — отключить (тесты, разбор).</summary>
    public static void Attach(RunJournal? journal) => Current = journal;

    /// <summary>
    /// Записать строку в журнал приложения, если он подключен. Ради этой строчки холдер и
    /// заведен: места записи разбросаны по каналам, и в каждом проверять наличие журнала
    /// значило бы повторить одно и то же условие пять раз.
    /// </summary>
    public static void Add(RunEntry entry) => Current?.Write(entry);
}
