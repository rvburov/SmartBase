using System.Diagnostics;
using System.Text;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Удаление большой коллекции на PostgreSQL (стенд sb-pg): заливка N записей через
/// BulkAddRecords, затем DeleteCollectionData тем же пакетным путем, что и SQLite.
/// Считает время, транзакции/удаленные кортежи (pg_stat_database), размер базы и таблиц
/// ДО/ПОСЛЕ, раздувание от мертвых кортежей (VACUUM не зовем — как в приложении).
///
/// Запуск: dotnet run -c Release --project Tests/Benchmarks -- pgdelete [--rows N] [--db smartbase2] [--keep]
/// </summary>
internal static class PgDeleteBench
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var rows  = BenchUtil.ArgInt(args, "--rows") ?? 300_000;
        var dbName = BenchUtil.ArgStr(args, "--db") ?? "smartbase2";
        var batch = BenchUtil.ArgInt(args, "--batch") ?? 10_000;
        var keep  = args.Contains("--keep");
        var cs    = $"Host=localhost;Port=5455;Username=postgres;Password=postgres;Database={dbName};Timeout=5;CommandTimeout=0";

        Console.WriteLine($"PostgreSQL {dbName}: {rows:N0} записей × 6 полей, пакет удаления {batch:N0}");
        try { using var probe = new NpgsqlConnection(cs); probe.Open(); }
        catch (Exception ex) { Console.WriteLine($"Стенд недоступен: {ex.Message}"); return 1; }

        ResetSchema(cs);
        try
        {
            using var m = DatabaseManager.Open(9201, "pgdel", cs, provider: "postgres");
            var repo = m.Constructor;

            var col   = repo.AddCollection(new DbCollection { Name = "Удаляемая" }).Id!.Value;
            var f = new[]
            {
                AddField(repo, col, "Название объекта", "text",   10),
                AddField(repo, col, "Комментарий",      "text",   20),
                AddField(repo, col, "Количество",       "number", 30),
                AddField(repo, col, "Дата",             "date",   40),
                AddField(repo, col, "Статус",           "select", 50),
                AddField(repo, col, "Ответственный",    "text",   60),
            };

            var sw = Stopwatch.StartNew();
            repo.BulkAddRecords(col, f, Rows(rows), rows, CancellationToken.None);
            sw.Stop();
            Console.WriteLine($"Заливка: {BenchUtil.Fmt(sw.Elapsed.TotalMilliseconds)} " +
                              $"({rows / Math.Max(sw.Elapsed.TotalSeconds, 0.001):N0} записей/с)");

            Vacuum(cs, "ANALYZE");
            var before = Snap(cs, dbName);
            Console.WriteLine($"ДО удаления: база {before.DbMb:N0} МБ; db_records {before.RecMb:N0} МБ " +
                              $"(живых {before.RecLive:N0}, мертвых {before.RecDead:N0}); " +
                              $"db_field_values {before.ValMb:N0} МБ (живых {before.ValLive:N0}, мертвых {before.ValDead:N0})");
            var st0 = Stats(cs, dbName);

            var points = new List<(double Ms, int Value)>();
            var swAll  = Stopwatch.StartNew();
            IProgress<int> prog = new Prog(swAll, points);

            var proc = Process.GetCurrentProcess();
            var del  = Stopwatch.StartNew();
            using (var ctx = new AppDbContext(cs))
                new ConstructorRepository(ctx).DeleteCollectionData(col, prog, batch);
            del.Stop();
            proc.Refresh();

            var st1 = Stats(cs, dbName);
            Console.WriteLine();
            Console.WriteLine($"УДАЛЕНИЕ ЦЕЛИКОМ: {BenchUtil.Fmt(del.Elapsed.TotalMilliseconds)} " +
                              $"({rows / Math.Max(del.Elapsed.TotalSeconds, 0.001):N0} записей/с)");
            Console.WriteLine($"  транзакций на сервере: {st1.Commits - st0.Commits:N0}, " +
                              $"кортежей удалено: {st1.TupDeleted - st0.TupDeleted:N0}, " +
                              $"прочитано с диска блоков: {st1.BlksRead - st0.BlksRead:N0}, " +
                              $"попаданий в кеш: {st1.BlksHit - st0.BlksHit:N0}");
            Console.WriteLine($"  память клиента: GC {GC.GetTotalMemory(false) / 1024 / 1024:N0} МБ, " +
                              $"рабочий набор {proc.WorkingSet64 / 1024 / 1024:N0} МБ");
            Console.WriteLine();
            Console.WriteLine("── Прогресс по пакетам ──────────────────────────────────────────");
            var prev = 0.0;
            for (var i = 0; i < points.Count; i++)
            {
                var (ms, v) = points[i];
                Console.WriteLine($"  #{i + 1,3}  {v,9:N0} записей   t={ms / 1000,7:0.00} с   Δ={(ms - prev) / 1000,6:0.00} с");
                prev = ms;
            }
            Console.WriteLine($"  хвост: {BenchUtil.Fmt(del.Elapsed.TotalMilliseconds - prev)}");
            Console.WriteLine();

            var after = Snap(cs, dbName);
            Console.WriteLine($"ПОСЛЕ (без VACUUM): база {after.DbMb:N0} МБ; db_records {after.RecMb:N0} МБ " +
                              $"(живых {after.RecLive:N0}, мертвых {after.RecDead:N0}); " +
                              $"db_field_values {after.ValMb:N0} МБ (живых {after.ValLive:N0}, мертвых {after.ValDead:N0})");

            var vsw = Stopwatch.StartNew();
            Vacuum(cs, "VACUUM (ANALYZE)");
            vsw.Stop();
            var vac = Snap(cs, dbName);
            Console.WriteLine($"VACUUM ANALYZE вручную: {BenchUtil.Fmt(vsw.Elapsed.TotalMilliseconds)} → " +
                              $"база {vac.DbMb:N0} МБ, db_records {vac.RecMb:N0} МБ, db_field_values {vac.ValMb:N0} МБ");
            return 0;
        }
        finally
        {
            if (!keep) ResetSchema(cs);
        }
    }

    private sealed class Prog(Stopwatch sw, List<(double, int)> pts) : IProgress<int>
    {
        public void Report(int value) { lock (pts) pts.Add((sw.Elapsed.TotalMilliseconds, value)); }
    }

    private static IEnumerable<BulkRecordRow> Rows(int rows)
    {
        string[] statuses = ["Новая", "В работе", "На проверке", "Готово", "Отменена"];
        string[] owners   = Enumerable.Range(1, 20).Select(i => $"Сотрудник {i:D2}").ToArray();
        for (var i = 1; i <= rows; i++)
            yield return new BulkRecordRow($"Запись {i:D6}",
            [
                $"Объект {i}",
                $"Комментарий к записи {i}: длинное поле, похожее на реальные данные",
                (i % 1000).ToString(),
                new DateTime(2026, 1, 1).AddDays(i % 365).ToString("yyyy-MM-dd"),
                statuses[i % statuses.Length],
                owners[i % owners.Length],
            ]);
    }

    private static int AddField(ConstructorRepository repo, int col, string name, string type, int pos) =>
        repo.AddField(new DbField { CollectionId = col, Name = name, FieldType = type, Position = pos }).Id!.Value;

    private sealed record Sizes(long DbMb, long RecMb, long ValMb, long RecLive, long RecDead, long ValLive, long ValDead);

    private static Sizes Snap(string cs, string dbName)
    {
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        long dbMb = Scalar(conn, $"SELECT pg_database_size('{dbName}')/1024/1024");
        long recMb = Scalar(conn, "SELECT pg_total_relation_size('db_records')/1024/1024");
        long valMb = Scalar(conn, "SELECT pg_total_relation_size('db_field_values')/1024/1024");
        long recLive = Scalar(conn, "SELECT coalesce(n_live_tup,0) FROM pg_stat_user_tables WHERE relname='db_records'");
        long recDead = Scalar(conn, "SELECT coalesce(n_dead_tup,0) FROM pg_stat_user_tables WHERE relname='db_records'");
        long valLive = Scalar(conn, "SELECT coalesce(n_live_tup,0) FROM pg_stat_user_tables WHERE relname='db_field_values'");
        long valDead = Scalar(conn, "SELECT coalesce(n_dead_tup,0) FROM pg_stat_user_tables WHERE relname='db_field_values'");
        return new Sizes(dbMb, recMb, valMb, recLive, recDead, valLive, valDead);
    }

    private sealed record StatRow(long Commits, long TupDeleted, long BlksRead, long BlksHit);

    private static StatRow Stats(string cs, string dbName)
    {
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT xact_commit, tup_deleted, blks_read, blks_hit FROM pg_stat_database WHERE datname='{dbName}'";
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? new StatRow(rd.GetInt64(0), rd.GetInt64(1), rd.GetInt64(2), rd.GetInt64(3))
                         : new StatRow(0, 0, 0, 0);
    }

    private static long Scalar(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    private static void Vacuum(string cs, string what)
    {
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = what == "ANALYZE" ? "ANALYZE" : what;
        cmd.ExecuteNonQuery();
    }

    private static void ResetSchema(string cs)
    {
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
        cmd.ExecuteNonQuery();
    }
}
