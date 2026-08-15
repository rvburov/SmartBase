using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SmartBase.Core.Exporters;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Logging;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Замер выгрузки в CSV журнала изменений и диагностики — кнопка «Выгрузить в CSV»
/// в навбарах «История изменений» и «Диагностика». Проверяет главное обещание:
/// поток keyset-страниц пишется в файл с постоянной памятью, поэтому миллионы строк
/// выгружаются линейно по времени и не поднимают RSS.
///
/// Строки журнала сеются сырым SQL (в бою их пишет приложение по ходу работы —
/// генерировать их настоящими операциями было бы на порядок дольше самого замера).
/// </summary>
internal static class LogExportBench
{
    public static int Run(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();

        var dir  = BenchUtil.ArgStr(args, "--dir") ?? Path.Combine(Path.GetTempPath(), "csv-export-perf");
        var rows = BenchUtil.ArgInt(args, "--rows") ?? 1_000_000;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        ChangeLog(dir, rows);
        Console.WriteLine();
        Diagnostics(dir, rows);
        return 0;
    }

    private static void ChangeLog(string dir, int rows)
    {
        var dbPath = Path.Combine(dir, "changelog.db");
        using var m = DatabaseManager.Open(0, "csv-perf", dbPath);

        Console.WriteLine($"Журнал изменений: генерация {rows:N0} строк…");
        var seed = Stopwatch.StartNew();
        SeedChangeLog(dbPath, rows);
        seed.Stop();
        Console.WriteLine($"  засев: {seed.Elapsed.TotalSeconds:0.0} с");

        var repo  = m.Constructor;
        var query = new ConstructorRepository.ChangeLogQuery();

        var count = Stopwatch.StartNew();
        var total = repo.CountChangeLogAsync(query).GetAwaiter().GetResult();
        count.Stop();
        Console.WriteLine($"  счёт строк (окно подтверждения): {count.Elapsed.TotalMilliseconds:N0} мс → {total:N0}");

        Measure("  выгрузка", Path.Combine(dir, "журнал.csv"), total,
                path => CsvDump.Write(path, ConstructorRepository.ChangeLogCsvHeaders,
                                      repo.ReadChangeLogCsvRowsIsolated(query)));

        // Худший случай: пользователь отсортировал таблицу по строковой колонке — курсор
        // идёт не по PK, а по «значение + Id» поверх неиндексированной колонки.
        var sorted = new ConstructorRepository.ChangeLogQuery { SortColumn = "user", SortAsc = true };
        Measure("  выгрузка (сортировка по колонке)", Path.Combine(dir, "журнал-сорт.csv"), total,
                path => CsvDump.Write(path, ConstructorRepository.ChangeLogCsvHeaders,
                                      repo.ReadChangeLogCsvRowsIsolated(sorted)));
    }

    private static void Diagnostics(string dir, int rows)
    {
        var logsPath = Path.Combine(dir, "logs.db");
        Console.WriteLine($"Диагностика: генерация {rows:N0} строк…");
        var seed = Stopwatch.StartNew();
        SeedLogs(logsPath, rows);
        seed.Stop();
        Console.WriteLine($"  засев: {seed.Elapsed.TotalSeconds:0.0} с");

        var reader = new LogReader(logsPath);
        var query  = new LogReader.LogQuery();

        var count = Stopwatch.StartNew();
        var total = reader.CountAsync(query).GetAwaiter().GetResult();
        count.Stop();
        Console.WriteLine($"  счёт строк (окно подтверждения): {count.Elapsed.TotalMilliseconds:N0} мс → {total:N0}");

        Measure("  выгрузка", Path.Combine(dir, "диагностика.csv"), total,
                path => CsvDump.Write(path, LogReader.CsvHeaders, reader.ReadCsvRows(query)));
    }

    // Замер с памятью: живой размер кучи важнее секунд — именно он вырастет,
    // если выгрузка начнёт материализовать строки вместо потока. Общий объём
    // выделений (мусор страниц) при этом растёт всегда — он ни о чём не говорит.
    private static void Measure(string label, string path, long total, Func<string, long> write)
    {
        var before = GC.GetTotalAllocatedBytes();
        var sw = Stopwatch.StartNew();
        var written = write(path);
        sw.Stop();

        var mb    = new FileInfo(path).Length / 1024.0 / 1024.0;
        var alloc = (GC.GetTotalAllocatedBytes() - before) / 1024.0 / 1024.0;
        var live  = GC.GetTotalMemory(forceFullCollection: true) / 1024.0 / 1024.0;
        Console.WriteLine($"{label}: {sw.Elapsed.TotalSeconds,6:0.0} с " +
                          $"({written / Math.Max(0.001, sw.Elapsed.TotalSeconds),10:N0} строк/с), " +
                          $"файл {mb,7:0.0} МБ, выделено {alloc,8:N0} МБ, " +
                          $"куча после {live,6:N0} МБ, рабочий набор {Environment.WorkingSet / 1024.0 / 1024.0:N0} МБ");
        if (written != total) Console.WriteLine($"  ВНИМАНИЕ: записано {written:N0}, ожидалось {total:N0}");
    }

    private static void SeedChangeLog(string dbPath, int rows)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        Pragmas(conn);

        using var tx  = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO change_log (Ts, UserKey, UserName, Action, TargetType, TargetName, CollectionName, Count, Detail) " +
            "VALUES (@ts, 'бенч@стенд', 'Бенчмарк', @a, @tt, @tn, @cn, NULL, @d)";
        var ts = cmd.Parameters.Add("@ts", SqliteType.Text);
        var a  = cmd.Parameters.Add("@a",  SqliteType.Text);
        var tt = cmd.Parameters.Add("@tt", SqliteType.Text);
        var tn = cmd.Parameters.Add("@tn", SqliteType.Text);
        var cn = cmd.Parameters.Add("@cn", SqliteType.Text);
        var d  = cmd.Parameters.Add("@d",  SqliteType.Text);

        string[] actions = ["create", "update", "delete", "duplicate", "move"];
        string[] types   = ["record", "collection", "field", "records"];
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < rows; i++)
        {
            ts.Value = start.AddSeconds(i).ToString("o");
            a.Value  = actions[i % actions.Length];
            tt.Value = types[i % types.Length];
            tn.Value = $"Объект {i}";
            cn.Value = $"Коллекция {i % 50}";
            d.Value  = i % 3 == 0 ? $"«старое {i}» → «новое {i}»" : (object)DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void SeedLogs(string path, int rows)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        Pragmas(conn);

        using (var create = conn.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE logs (id INTEGER PRIMARY KEY AUTOINCREMENT, " +
                "Timestamp TEXT, Level TEXT, Exception TEXT, RenderedMessage TEXT, Properties TEXT)";
            create.ExecuteNonQuery();
        }

        using var tx  = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO logs (Timestamp, Level, Exception, RenderedMessage, Properties) " +
            "VALUES (@t, @l, @e, @m, '{}')";
        var t = cmd.Parameters.Add("@t", SqliteType.Text);
        var l = cmd.Parameters.Add("@l", SqliteType.Text);
        var e = cmd.Parameters.Add("@e", SqliteType.Text);
        var m = cmd.Parameters.Add("@m", SqliteType.Text);

        string[] levels = ["Information", "Warning", "Error", "Debug"];
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < rows; i++)
        {
            t.Value = start.AddSeconds(i).ToString("yyyy-MM-dd HH:mm:ss");
            l.Value = levels[i % levels.Length];
            e.Value = i % 10 == 0 ? $"System.Exception: сбой {i}\n   в методе" : (object)DBNull.Value;
            m.Value = $"Сообщение {i}; с точкой с запятой и «кавычками»";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void Pragmas(SqliteConnection conn)
    {
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA cache_size=-524288;";
        pragma.ExecuteNonQuery();
    }
}
