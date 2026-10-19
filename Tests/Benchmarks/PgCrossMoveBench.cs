using System.Diagnostics;
using System.Text;
using Npgsql;
using SmartBase.Database;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Перенос коллекции между SQLite и PostgreSQL (generic-путь, ConstructorRepository.CrossMove):
/// большая коллекция стенда SQLite → база PostgreSQL стенда и обратно, время и темп каждого
/// направления. Источник — копия файла: исходный стенд не меняется.
///
/// Запуск: dotnet run -c Release --project Tests/Benchmarks -- pgmove --db PATH [--pg ИМЯ] [--col ИМЯ]
/// </summary>
internal static class PgCrossMoveBench
{
    public static int Run(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        Console.OutputEncoding = Encoding.UTF8;

        var src     = BenchUtil.ArgStr(args, "--db") ?? "";
        var pgName  = BenchUtil.ArgStr(args, "--pg") ?? "smartbase2";
        var colName = BenchUtil.ArgStr(args, "--col") ?? "Задачи";
        var cs      = $"Host=localhost;Port=5455;Username=postgres;Password=postgres;Database={pgName};Timeout=5;CommandTimeout=0";
        if (!File.Exists(src)) { Console.WriteLine($"Нет файла {src}"); return 1; }
        try { using var probe = new NpgsqlConnection(cs); probe.Open(); }
        catch (Exception ex) { Console.WriteLine($"Стенд PostgreSQL недоступен: {ex.Message}"); return 1; }

        using (var conn = new NpgsqlConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
            cmd.ExecuteNonQuery();
        }

        var copy = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(src))!, "pgmove-src.db");
        foreach (var s in new[] { "", "-wal", "-shm" })
            if (File.Exists(copy + s)) File.Delete(copy + s);
        File.Copy(src, copy);

        using var lite = DatabaseManager.Open(9301, "lite", copy);
        using var pg   = DatabaseManager.Open(9302, "pg", cs, provider: "postgres");
        var col = lite.Constructor.GetAllCollections().First(c => c.Name == colName).Id!.Value;
        var n   = lite.Constructor.CountRecords(col);
        Console.WriteLine($"Коллекция «{colName}»: {n:N0} записей");

        var sw = Stopwatch.StartNew();
        var pgCol = pg.Constructor.MoveCollectionIsolated(copy, col, null, sourceDbId: 9301, targetDbId: 9302);
        Report("SQLite → PostgreSQL", sw.Elapsed.TotalMilliseconds, n);

        sw.Restart();
        var back = lite.Constructor.MoveCollectionIsolated(cs, pgCol, null, sourceDbId: 9302, targetDbId: 9301);
        Report("PostgreSQL → SQLite", sw.Elapsed.TotalMilliseconds, n);
        Console.WriteLine($"Обратно в SQLite: {lite.Constructor.CountRecords(back):N0} записей");

        lite.Dispose();
        AppDbContext.ResetPool(copy);
        foreach (var s in new[] { "", "-wal", "-shm" })
            if (File.Exists(copy + s)) File.Delete(copy + s);
        return 0;
    }

    private static void Report(string what, double ms, long n) =>
        Console.WriteLine($"{what}: {BenchUtil.Fmt(ms)} ({n / Math.Max(ms / 1000, 0.001):N0} записей/с; копия и удаление источника)");
}
