using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Из чего состоит файл базы SQLite: живые данные, свободные страницы, накладные B-деревьев,
/// объем каждой таблицы и индекса, разрез по коллекциям и полям. Команда
/// <c>dbsize --db PATH [--top N] [--fast]</c>.
///
/// Файл открывается ТОЛЬКО ДЛЯ ЧТЕНИЯ (Mode=ReadOnly, без миграций) — годится для настоящей
/// общей базы в сетевой папке прямо во время работы людей: ни одной записи в файл нет.
///
/// Сборка SQLite в приложении (e_sqlite3) собрана без DBSTAT, поэтому размер объектов
/// считается по содержимому: сумма байт значений колонок по правилам формата записи SQLite
/// (varint для целых, длина текста/блоба, заголовок записи ≈ байт на колонку, накладные
/// ячейки и rowid). Разница между занятыми страницами и суммой нагрузок — накладные
/// B-деревьев (недозаполненные страницы, внутренние узлы, хвосты overflow-страниц).
///
/// <c>--fast</c> пропускает самое дорогое — <c>COUNT(DISTINCT Value)</c> по полям (оценка
/// потенциала словарного сжатия): на сотнях миллионов значений это минуты и временный файл.
/// </summary>
internal static class DbSizeBench
{
    private const int LongValueBytes = 120;   // порог «длинного» значения (см. план: частичный индекс)

    private sealed record ObjectSize(string Type, string Name, string Table, long Rows, long Payload);

    public static int Run(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        Console.OutputEncoding = Encoding.UTF8;   // вывод в файл (> log) читаем как UTF-8, а не в OEM-кодировке консоли

        var path = BenchUtil.ArgStr(args, "--db");
        var top  = BenchUtil.ArgInt(args, "--top") ?? 20;
        var fast = args.Contains("--fast");

        if (string.IsNullOrWhiteSpace(path))
        {
            Console.WriteLine("Использование: dbsize --db PATH [--top N] [--fast]");
            return 1;
        }
        if (path.Contains("Host=", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("dbsize работает только с файлами SQLite (для PostgreSQL — pg_total_relation_size на сервере).");
            return 1;
        }
        if (!File.Exists(path))
        {
            Console.WriteLine($"Файл не найден: {path}");
            return 1;
        }

        var total = Stopwatch.StartNew();
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode       = SqliteOpenMode.ReadOnly,
            Pooling    = false,
        }.ToString();

        using var con = new SqliteConnection(cs);
        con.Open();
        // Чтение — только на своем подключении; кеш побольше, чтобы сканы гигабайтных таблиц
        // не упирались в дефолтные 2 МБ. Файл при этом не меняется.
        Exec(con, "PRAGMA cache_size=-262144;");
        Exec(con, "PRAGMA busy_timeout=15000;");

        // ── 1. Итог по файлу ─────────────────────────────────────────────────────
        var pageSize  = Long(con, "PRAGMA page_size;");
        var pageCount = Long(con, "PRAGMA page_count;");
        var freelist  = Long(con, "PRAGMA freelist_count;");
        var journal   = Str(con, "PRAGMA journal_mode;");
        var autoVac   = Long(con, "PRAGMA auto_vacuum;");

        var fileBytes = new FileInfo(path).Length;
        var walBytes  = Len(path + "-wal");
        var jrnBytes  = Len(path + "-journal");
        var usedBytes = (pageCount - freelist) * pageSize;
        var freeBytes = freelist * pageSize;

        Console.WriteLine($"Файл:            {path}");
        Console.WriteLine($"Размер файла:    {Size(fileBytes)}" +
                          (walBytes > 0 ? $"  (+ -wal {Size(walBytes)})" : "") +
                          (jrnBytes > 0 ? $"  (+ -journal {Size(jrnBytes)})" : ""));
        Console.WriteLine($"Страница:        {pageSize} байт · страниц {pageCount:N0} · журнал {journal} · auto_vacuum {AutoVacName(autoVac)}");
        Console.WriteLine($"Занято:          {Size(usedBytes)} ({Pct(usedBytes, pageCount * pageSize)})");
        Console.WriteLine($"Свободно:        {Size(freeBytes)} ({Pct(freeBytes, pageCount * pageSize)}) — {freelist:N0} страниц в freelist");
        Console.WriteLine();

        // ── 2. Таблицы и индексы ────────────────────────────────────────────────
        var objects = MeasureObjects(con);
        var payload = objects.Sum(o => o.Payload);
        var overhead = Math.Max(0, usedBytes - payload);

        Console.WriteLine("Таблицы и индексы (полезная нагрузка по содержимому, без накладных страниц):");
        Console.WriteLine($"  {"объект",-62} {"строк",14} {"байт",12} {"доля",7}");
        foreach (var o in objects.OrderByDescending(o => o.Payload))
        {
            if (o.Payload == 0 && o.Rows == 0) continue;
            var label = o.Type == "index" ? $"  └ {o.Name}" : o.Name;
            Console.WriteLine($"  {Trunc(label, 62),-62} {o.Rows,14:N0} {Size(o.Payload),12} {Pct(o.Payload, usedBytes),7}");
        }
        Console.WriteLine($"  {"— накладные B-деревьев (занято - нагрузка)",-62} {"",14} {Size(overhead),12} {Pct(overhead, usedBytes),7}");
        Console.WriteLine();

        // ── 3. Коллекции ────────────────────────────────────────────────────────
        if (HasTable(con, "db_collections") && HasTable(con, "db_field_values"))
        {
            PrintCollections(con, objects);
            PrintFields(con, top, fast);
        }

        // ── 4. Выводы ───────────────────────────────────────────────────────────
        Console.WriteLine("Выводы:");
        var freeRatio = pageCount == 0 ? 0 : (double)freelist / pageCount;
        if (freeBytes > 64L * 1024 * 1024 && (freeRatio >= 0.25 || freeBytes >= 512L * 1024 * 1024))
            Console.WriteLine($"  • свободно {Pct(freeBytes, pageCount * pageSize)} файла — после удалений место не возвращено, файл стоит сжать (VACUUM / «Сжать файл»).");
        else
            Console.WriteLine($"  • свободных страниц немного ({Pct(freeBytes, pageCount * pageSize)}) — раздувания за счет удалений нет.");
        Console.WriteLine($"  • накладные B-деревьев: {Pct(overhead, usedBytes)} занятого места" +
                          (overhead > usedBytes * 0.35 ? " — много: страницы недозаполнены (случайные вставки в индексы), VACUUM уплотнит." : "."));
        if (walBytes > 64L * 1024 * 1024)
            Console.WriteLine($"  • -wal {Size(walBytes)} — журнал не усечен после чекпоинта (journal_size_limit не задан или база открыта читателями).");
        var valIdx = objects.FirstOrDefault(o => o.Type == "index" && o.Table == "db_field_values");
        var valTbl = objects.FirstOrDefault(o => o.Type == "table" && o.Name == "db_field_values");
        if (valIdx is not null && valTbl is not null && valTbl.Payload > 0)
            Console.WriteLine($"  • индекс значений {valIdx.Name}: {Size(valIdx.Payload)} — {Pct(valIdx.Payload, valTbl.Payload)} от самой таблицы значений (он дублирует каждое значение).");
        Console.WriteLine();
        Console.WriteLine($"Готово за {BenchUtil.Fmt(total.Elapsed.TotalMilliseconds)}.");
        return 0;
    }

    // ── Объекты: таблицы и индексы ────────────────────────────────────────────

    private static List<ObjectSize> MeasureObjects(SqliteConnection con)
    {
        var result = new List<ObjectSize>();
        var tables = new List<(string Name, string Sql)>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) tables.Add((rd.GetString(0), rd.IsDBNull(1) ? "" : rd.GetString(1)));
        }

        foreach (var (table, sql) in tables)
        {
            var withoutRowid = Regex.IsMatch(sql, @"WITHOUT\s+ROWID", RegexOptions.IgnoreCase);
            var cols = TableInfo(con, table);
            var pkCols = cols.Where(c => c.Pk > 0).OrderBy(c => c.Pk).Select(c => c.Name).ToList();

            // Таблица: все колонки + заголовок записи (байт на колонку) + ячейка (2 байта
            // указатель + varint размера) + rowid (≈3 байта) у rowid-таблиц.
            var rowFixed = cols.Count + 1 + 3 + (withoutRowid ? 0 : 3);
            var (rows, bytes) = SumBytes(con, table, cols.Select(c => c.Name), where: null);
            result.Add(new ObjectSize("table", table, table, rows, bytes + rows * rowFixed));

            // Индексы таблицы (включая автоиндексы UNIQUE/PK у rowid-таблиц; у WITHOUT ROWID
            // PK — это сама таблица, ее автоиндекс не считаем).
            foreach (var (idxName, origin, idxSql) in IndexList(con, table))
            {
                if (withoutRowid && origin == "pk") continue;
                var idxCols = IndexInfo(con, idxName);
                if (idxCols.Count == 0) continue;   // индекс по выражению — не оцениваем

                // Ключ строки в индексе: rowid (≈3 байта) либо PK-колонки WITHOUT ROWID-таблицы,
                // которых еще нет среди колонок индекса.
                var keyCols = withoutRowid ? pkCols.Where(p => !idxCols.Contains(p)).ToList() : [];
                var allCols = idxCols.Concat(keyCols).ToList();
                var idxFixed = allCols.Count + 1 + 3 + (withoutRowid ? 0 : 3);
                var partialWhere = PartialWhere(idxSql);
                var (irows, ibytes) = SumBytes(con, table, allCols, partialWhere);
                result.Add(new ObjectSize("index", idxName, table, irows, ibytes + irows * idxFixed));
            }
        }
        return result;
    }

    private sealed record ColumnInfo(string Name, string Type, int Pk);

    private static List<ColumnInfo> TableInfo(SqliteConnection con, string table)
    {
        var list = new List<ColumnInfo>();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            list.Add(new ColumnInfo(rd.GetString(1), rd.IsDBNull(2) ? "" : rd.GetString(2), rd.GetInt32(5)));
        return list;
    }

    private static List<(string Name, string Origin, string Sql)> IndexList(SqliteConnection con, string table)
    {
        var list = new List<(string, string, string)>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA index_list(\"{table}\");";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add((rd.GetString(1), rd.GetString(3), ""));
        }
        for (var i = 0; i < list.Count; i++)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name=$n";
            cmd.Parameters.AddWithValue("$n", list[i].Item1);
            var sql = cmd.ExecuteScalar();
            list[i] = (list[i].Item1, list[i].Item2, sql is string s ? s : "");
        }
        return list;
    }

    private static List<string> IndexInfo(SqliteConnection con, string index)
    {
        var list = new List<string>();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"PRAGMA index_info(\"{index}\");";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            if (rd.IsDBNull(2)) return [];   // cid = -2: выражение, имени колонки нет
            list.Add(rd.GetString(2));
        }
        return list;
    }

    private static string? PartialWhere(string indexSql)
    {
        var m = Regex.Match(indexSql, @"\sWHERE\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>Байты хранения значений колонок по формату записи SQLite: NULL — 0, целое —
    /// 1–8 байт по величине, real — 8, текст/блоб — длина в байтах.</summary>
    private static string BytesExpr(string col)
    {
        var c = $"\"{col}\"";
        return $"(CASE typeof({c}) WHEN 'null' THEN 0" +
               $" WHEN 'integer' THEN (CASE WHEN {c} BETWEEN 0 AND 1 THEN 0" +
               $" WHEN {c} BETWEEN -128 AND 127 THEN 1 WHEN {c} BETWEEN -32768 AND 32767 THEN 2" +
               $" WHEN {c} BETWEEN -8388608 AND 8388607 THEN 3 WHEN {c} BETWEEN -2147483648 AND 2147483647 THEN 4 ELSE 8 END)" +
               $" WHEN 'real' THEN 8 ELSE length(CAST({c} AS BLOB)) END)";
    }

    private static (long Rows, long Bytes) SumBytes(SqliteConnection con, string table, IEnumerable<string> cols, string? where)
    {
        var expr = string.Join(" + ", cols.Select(BytesExpr));
        if (expr.Length == 0) expr = "0";
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*), COALESCE(SUM({expr}), 0) FROM \"{table}\"" +
                          (where is null ? "" : $" WHERE {where}");
        cmd.CommandTimeout = 24 * 3600;
        using var rd = cmd.ExecuteReader();
        rd.Read();
        return (rd.GetInt64(0), rd.GetInt64(1));
    }

    // ── Разрез по коллекциям и полям ──────────────────────────────────────────

    private sealed record FieldStat(int FieldId, string Name, string Type, int CollectionId, string Collection,
                                    long Rows, long Bytes, long MaxLen, long LongBytes, long Distinct);

    private static List<FieldStat> _fieldStats = [];

    private static void PrintCollections(SqliteConnection con, List<ObjectSize> objects)
    {
        // Записи и их байты по коллекциям — из db_records напрямую.
        var recCols = TableInfo(con, "db_records").Select(c => c.Name).ToList();
        var recExpr = string.Join(" + ", recCols.Select(BytesExpr));
        var recFixed = recCols.Count + 1 + 3 + 3;
        var records = new Dictionary<int, (long Rows, long Bytes)>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = $"SELECT CollectionId, COUNT(*), COALESCE(SUM({recExpr}),0) FROM db_records GROUP BY CollectionId";
            cmd.CommandTimeout = 24 * 3600;
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) records[rd.GetInt32(0)] = (rd.GetInt64(1), rd.GetInt64(2) + rd.GetInt64(1) * recFixed);
        }

        // Значения — по полям (индекс (FieldId, Value) покрывает запрос), поле → коллекция.
        var fields = new Dictionary<int, (string Name, string Type, int Col)>();
        var colNames = new Dictionary<int, string>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name FROM db_collections";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) colNames[rd.GetInt32(0)] = rd.GetString(1);
        }
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name, FieldType, CollectionId FROM db_fields";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) fields[rd.GetInt32(0)] = (rd.GetString(1), rd.GetString(2), rd.GetInt32(3));
        }

        var perField = new Dictionary<int, (long Rows, long Bytes, long MaxLen, long LongBytes)>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText =
                "SELECT FieldId, COUNT(*), COALESCE(SUM(length(CAST(Value AS BLOB))),0), COALESCE(MAX(length(CAST(Value AS BLOB))),0), " +
                $"COALESCE(SUM(CASE WHEN length(CAST(Value AS BLOB)) > {LongValueBytes} THEN length(CAST(Value AS BLOB)) ELSE 0 END),0) " +
                "FROM db_field_values GROUP BY FieldId";
            cmd.CommandTimeout = 24 * 3600;
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) perField[rd.GetInt32(0)] = (rd.GetInt64(1), rd.GetInt64(2), rd.GetInt64(3), rd.GetInt64(4));
        }

        // Строка значения в таблице: RecordId (≈3) + FieldId (≈2) + заголовок 4 + ячейка 3;
        // в индексе — то же плюс ключ. Считаем обе копии, чтобы «байт на запись» был честным.
        const int valueFixedTable = 3 + 2 + 4 + 3;
        const int valueFixedIndex = 3 + 2 + 4 + 3;

        var calendar = new Dictionary<int, long>();
        if (HasTable(con, "calendar_index"))
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT CollectionId, COUNT(*) FROM calendar_index GROUP BY CollectionId";
                cmd.CommandTimeout = 24 * 3600;
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) calendar[rd.GetInt32(0)] = rd.GetInt64(1);
            }

        _fieldStats = perField.Select(kv =>
        {
            var f = fields.GetValueOrDefault(kv.Key, (Name: "(удаленное поле)", Type: "?", Col: 0));
            return new FieldStat(kv.Key, f.Name, f.Type, f.Col, colNames.GetValueOrDefault(f.Col, "?"),
                                 kv.Value.Rows, kv.Value.Bytes, kv.Value.MaxLen, kv.Value.LongBytes, -1);
        }).ToList();

        var byCol = _fieldStats.GroupBy(s => s.CollectionId)
            .ToDictionary(g => g.Key, g => (Rows: g.Sum(s => s.Rows), Bytes: g.Sum(s => s.Bytes)));

        var allIds = records.Keys.Union(byCol.Keys).ToList();
        if (allIds.Count == 0) { Console.WriteLine("Коллекций нет."); Console.WriteLine(); return; }

        Console.WriteLine("Коллекции (байты — нагрузка записей + значений в таблице и в индексе значений):");
        Console.WriteLine($"  {"коллекция",-40} {"записей",12} {"значений",13} {"календ.",10} {"байт",12} {"байт/зап.",10}");
        var rowsByCol = allIds.Select(id =>
        {
            var r = records.GetValueOrDefault(id);
            var v = byCol.GetValueOrDefault(id);
            var bytes = r.Bytes + v.Bytes * 2 + v.Rows * (valueFixedTable + valueFixedIndex);
            return (Id: id, Name: colNames.GetValueOrDefault(id, id == 0 ? "(поля без коллекции)" : $"(удаленная #{id})"),
                    Records: r.Rows, Values: v.Rows, Cal: calendar.GetValueOrDefault(id), Bytes: bytes);
        }).OrderByDescending(x => x.Bytes).ToList();
        foreach (var x in rowsByCol)
            Console.WriteLine($"  {Trunc(x.Name, 40),-40} {x.Records,12:N0} {x.Values,13:N0} {x.Cal,10:N0} {Size(x.Bytes),12} " +
                              $"{(x.Records > 0 ? (x.Bytes / (double)x.Records).ToString("N0") : "—"),10}");
        Console.WriteLine();
    }

    private static void PrintFields(SqliteConnection con, int top, bool fast)
    {
        if (_fieldStats.Count == 0) return;

        if (!fast)
        {
            // Уникальные значения по полям — потенциал словарного сжатия. Идет по индексу
            // (FieldId, Value): внутри поля значения уже отсортированы. Дорого на сотнях
            // миллионов строк — отключается ключом --fast.
            var distinct = new Dictionary<int, long>();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT FieldId, COUNT(DISTINCT Value) FROM db_field_values GROUP BY FieldId";
            cmd.CommandTimeout = 24 * 3600;
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) distinct[rd.GetInt32(0)] = rd.GetInt64(1);
            _fieldStats = _fieldStats.Select(s => s with { Distinct = distinct.GetValueOrDefault(s.FieldId, -1) }).ToList();
        }

        var totalBytes = Math.Max(1, _fieldStats.Sum(s => s.Bytes));
        var totalLong  = _fieldStats.Sum(s => s.LongBytes);
        Console.WriteLine($"Поля — top {top} по объему значений (байты — только значения, в таблице; индекс хранит столько же еще раз):");
        Console.WriteLine($"  {"поле",-30} {"тип",-11} {"коллекция",-22} {"строк",12} {"байт",10} {"ср.",6} {"макс",7} {"длинных",8} {"уникальных",11} {"словарь",8}");
        foreach (var s in _fieldStats.OrderByDescending(s => s.Bytes).Take(top))
        {
            var avg = s.Rows > 0 ? s.Bytes / (double)s.Rows : 0;
            // Словарь: значения один раз (distinct × средняя длина) + ссылка ≈ 4 байта на строку,
            // и так в таблице и в индексе → экономия относительно двух копий значений.
            var dict = s.Distinct < 0 ? "—"
                : s.Bytes == 0 ? "0 %"
                : $"-{Math.Max(0, 1 - (s.Distinct * avg + s.Rows * 4.0 * 2) / (s.Bytes * 2.0)):P0}";
            Console.WriteLine($"  {Trunc(s.Name, 30),-30} {Trunc(s.Type, 11),-11} {Trunc(s.Collection, 22),-22} {s.Rows,12:N0} {Size(s.Bytes),10} " +
                              $"{avg,6:N0} {s.MaxLen,7:N0} {Pct(s.LongBytes, Math.Max(1, s.Bytes)),8} " +
                              $"{(s.Distinct < 0 ? "—" : s.Distinct.ToString("N0")),11} {dict,8}");
        }
        Console.WriteLine($"  Значения длиннее {LongValueBytes} байт держат {Pct(totalLong, totalBytes)} нагрузки значений (и столько же в индексе).");
        Console.WriteLine();
    }

    // ── Мелочи ────────────────────────────────────────────────────────────────

    private static bool HasTable(SqliteConnection con, string name)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    private static void Exec(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Long(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    private static string Str(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static long Len(string f) => File.Exists(f) ? new FileInfo(f).Length : 0;

    private static string AutoVacName(long v) => v switch { 1 => "FULL", 2 => "INCREMENTAL", _ => "NONE" };

    private static string Size(long bytes) => bytes switch
    {
        < 1024L * 1024                => $"{bytes / 1024.0:N0} КБ",
        < 1024L * 1024 * 1024         => $"{bytes / 1024.0 / 1024:N1} МБ",
        _                             => $"{bytes / 1024.0 / 1024 / 1024:N2} ГБ",
    };

    private static string Pct(long part, long whole) =>
        whole <= 0 ? "—" : $"{part * 100.0 / whole:N1} %";

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
