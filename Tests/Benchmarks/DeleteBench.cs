using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Замер УДАЛЕНИЯ большой коллекции (DeleteCollectionIsolated / DeleteCollectionData):
/// время целиком, темп по пакетам, размеры .db и -wal ДО/ВО ВРЕМЯ/ПОСЛЕ, память процесса.
///
/// Запуск: dotnet run -c Release --project Tests/Benchmarks -- delete --db PATH [--batch N] [--suicide SEC]
/// --suicide SEC — процесс убивает сам себя (Environment.FailFast нет: просто abort) на N-й секунде
/// удаления: моделирует обрыв на середине.
/// </summary>
internal static class DeleteBench
{
    public static int Run(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        Console.OutputEncoding = Encoding.UTF8;

        var path    = BenchUtil.ArgStr(args, "--db") ?? "";
        var batch   = BenchUtil.ArgInt(args, "--batch") ?? 10_000;
        var suicide = BenchUtil.ArgInt(args, "--suicide");
        var colName = BenchUtil.ArgStr(args, "--col") ?? "Задачи";

        if (!File.Exists(path)) { Console.WriteLine($"Нет файла {path}"); return 1; }

        var proc = Process.GetCurrentProcess();
        Console.WriteLine($"Файл: {path}");
        Console.WriteLine($"ДО:   .db {Mb(path):N1} МБ, -wal {Mb(path + "-wal"):N1} МБ, -shm {Mb(path + "-shm"):N1} МБ");
        Console.WriteLine($"ДО:   GC {Mb2(GC.GetTotalMemory(false)):N0} МБ, рабочий набор {Mb2(proc.WorkingSet64):N0} МБ");

        using var m = DatabaseManager.Open(7001, "delbench", path);
        var repo = m.Constructor;
        var col  = repo.GetAllCollections().FirstOrDefault(c => c.Name == colName);
        if (col?.Id is not int colId) { Console.WriteLine($"Нет коллекции «{colName}»"); return 1; }

        proc.Refresh();
        Console.WriteLine($"После DatabaseManager.Open: GC {Mb2(GC.GetTotalMemory(false)):N0} МБ, " +
                          $"рабочий набор {Mb2(proc.WorkingSet64):N0} МБ, " +
                          $"-wal {Mb(path + "-wal"):N1} МБ");

        // --dup: сначала сделать дубль коллекции, потом удалять ОРИГИНАЛ — так глобальные индексы
        // (db_field_values, calendar_index) вдвое больше удаляемого среза, как у живого пользователя,
        // у которого в базе не одна коллекция.
        if (args.Contains("--dup"))
        {
            var dsw = Stopwatch.StartNew();
            var newId = repo.DuplicateCollectionIsolated(colId);
            dsw.Stop();
            Console.WriteLine($"Дубль коллекции (id {newId}) для фона: {BenchUtil.Fmt(dsw.Elapsed.TotalMilliseconds)}, " +
                              $".db {Mb(path):N1} МБ");
        }

        var cntSw = Stopwatch.StartNew();
        var rc    = repo.CountRecords(colId);
        cntSw.Stop();
        Console.WriteLine($"CountRecords(«{colName}») = {rc:N0} за {BenchUtil.Fmt(cntSw.Elapsed.TotalMilliseconds)}");
        Console.WriteLine();

        // Семплер: раз в 200 мс пишет время, размеры файлов и память.
        var stop     = new CancellationTokenSource();
        var samples  = new List<(double Ms, long Db, long Wal, long Gc, long Ws)>();
        var swAll    = Stopwatch.StartNew();
        var sampler  = new Thread(() =>
        {
            var p = Process.GetCurrentProcess();
            while (!stop.IsCancellationRequested)
            {
                p.Refresh();
                lock (samples)
                    samples.Add((swAll.Elapsed.TotalMilliseconds, Len(path), Len(path + "-wal"),
                                 GC.GetTotalMemory(false), p.WorkingSet64));
                Thread.Sleep(200);
            }
        }) { IsBackground = true };
        sampler.Start();

        if (suicide is int sec)
        {
            new Thread(() =>
            {
                Thread.Sleep(sec * 1000);
                Console.WriteLine($"!!! УБИВАЮ ПРОЦЕСС на {sec} с удаления (SIGKILL самому себе)");
                Console.Out.Flush();
                Process.GetCurrentProcess().Kill();   // без закрытия SQLite — как обрыв питания/диспетчер задач
            }) { IsBackground = true }.Start();
        }

        var points = new List<(double Ms, int Value)>();
        IProgress<int> probe = new Prog(swAll, points);

        // Ровно то, что делает сайдбар: отдельное подключение, объемные прагмы, пакетное удаление.
        var tune = BenchUtil.ArgStr(args, "--tune");   // null = как в приложении; "modest" = mmap 0 + кеш 64 МБ
        var sw = Stopwatch.StartNew();
        if (args.Contains("--sync"))
        {
            // Путь малого объема из сайдбара: DeleteCollection на ОБЩЕМ контексте, синхронно,
            // без окна прогресса — в приложении это UI-поток (порог BulkProgressThreshold = 20 000).
            repo.DeleteCollection(colId);
        }
        else if (batch == 10_000 && tune is null)
        {
            repo.DeleteCollectionIsolated(colId, probe);
        }
        else
        {
            using var ctx = new AppDbContext(path);
            ctx.ApplyBulkPerformancePragmas();
            if (tune is not null)
            {
                var sql = tune switch
                {
                    "modest" => "PRAGMA mmap_size=0; PRAGMA cache_size=-65536;",
                    "nommap" => "PRAGMA mmap_size=0;",
                    "nocache" => "PRAGMA cache_size=-65536;",
                    _ => throw new ArgumentException("--tune: modest | nommap | nocache"),
                };
                ctx.Database.OpenConnection();   // держим соединение — прагмы ниже переживут операцию
                ctx.Database.ExecuteSqlRaw(sql);
                Console.WriteLine($"Прагмы понижены ({tune}): {sql}");
            }
            new ConstructorRepository(ctx).DeleteCollectionData(colId, probe, batch);
            if (tune is not null) ctx.Database.CloseConnection();
        }
        sw.Stop();
        stop.Cancel();
        Thread.Sleep(250);

        proc.Refresh();
        Console.WriteLine($"УДАЛЕНИЕ ЦЕЛИКОМ: {BenchUtil.Fmt(sw.Elapsed.TotalMilliseconds)} " +
                          $"({rc / Math.Max(sw.Elapsed.TotalSeconds, 0.001):N0} записей/с), пакет {batch:N0}");
        Console.WriteLine();

        Console.WriteLine("── Прогресс по пакетам (отчет = конец пакета) ───────────────────");
        var prev = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var (ms, v) = points[i];
            Console.WriteLine($"  #{i + 1,3}  {v,9:N0} записей   t={ms / 1000,7:0.00} с   Δ={(ms - prev) / 1000,6:0.00} с");
            prev = ms;
        }
        Console.WriteLine($"  хвост после последнего пакета (UI-строки + строка коллекции + журнал): " +
                          $"{BenchUtil.Fmt(sw.Elapsed.TotalMilliseconds - prev)}");
        Console.WriteLine();

        Console.WriteLine("── Файлы и память во время удаления (семплы 200 мс) ─────────────");
        Console.WriteLine("      t, с       .db, МБ     -wal, МБ      GC, МБ     RSS, МБ");
        lock (samples)
        {
            var step = Math.Max(1, samples.Count / 40);
            for (var i = 0; i < samples.Count; i += step)
            {
                var s = samples[i];
                Console.WriteLine($"  {s.Ms / 1000,8:0.00}  {Mb2(s.Db),12:N1} {Mb2(s.Wal),12:N1} {Mb2(s.Gc),11:N0} {Mb2(s.Ws),11:N0}");
            }
            if (samples.Count > 0)
            {
                var s = samples[^1];
                Console.WriteLine($"  {s.Ms / 1000,8:0.00}  {Mb2(s.Db),12:N1} {Mb2(s.Wal),12:N1} {Mb2(s.Gc),11:N0} {Mb2(s.Ws),11:N0}   ← последний");
                Console.WriteLine($"  МАКСИМУМЫ: -wal {Mb2(samples.Max(x => x.Wal)):N1} МБ, " +
                                  $".db {Mb2(samples.Max(x => x.Db)):N1} МБ, " +
                                  $"GC {Mb2(samples.Max(x => x.Gc)):N0} МБ, RSS {Mb2(samples.Max(x => x.Ws)):N0} МБ");
            }
        }
        Console.WriteLine();
        Console.WriteLine($"ПОСЛЕ: .db {Mb(path):N1} МБ, -wal {Mb(path + "-wal"):N1} МБ, -shm {Mb(path + "-shm"):N1} МБ");
        Console.WriteLine($"ПОСЛЕ: GC {Mb2(GC.GetTotalMemory(false)):N0} МБ, рабочий набор {Mb2(proc.WorkingSet64):N0} МБ");

        m.Dispose();
        GC.Collect();
        proc.Refresh();
        Console.WriteLine($"ПОСЛЕ закрытия БД: .db {Mb(path):N1} МБ, -wal {Mb(path + "-wal"):N1} МБ, " +
                          $"рабочий набор {Mb2(proc.WorkingSet64):N0} МБ");
        return 0;
    }

    private sealed class Prog(Stopwatch sw, List<(double, int)> pts) : IProgress<int>
    {
        public void Report(int value) { lock (pts) pts.Add((sw.Elapsed.TotalMilliseconds, value)); }
    }

    private static long Len(string p) { try { return File.Exists(p) ? new FileInfo(p).Length : 0; } catch { return 0; } }
    private static double Mb(string p)  => Len(p) / 1024.0 / 1024.0;
    private static double Mb2(long b)   => b / 1024.0 / 1024.0;
}
