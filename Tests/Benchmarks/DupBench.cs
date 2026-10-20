using System.Diagnostics;
using System.Text;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Замер ДУБЛЯ большой коллекции тем же путем, что у боковой панели (отдельное подключение с
/// объемными прагмами, шелл + <see cref="ConstructorRepository.CopyCollectionData"/>), с выбором
/// размера пакета, и удаления полученной копии (<see cref="ConstructorRepository.DeleteCollectionIsolated"/>).
/// База подключается через <see cref="DatabaseManager.Open"/> — работает и фоновый перенос журнала,
/// как в программе. Файл меняется (копия добавляется и удаляется) — запускать на копии стенда.
///
/// Запуск: dotnet run -c Release --project Tests/Benchmarks -- dup --db PATH [--col ИМЯ] [--batch N] [--keep]
/// Только удаление коллекции ИМЯ (например, копии, оставленной с --keep): ... dup --db PATH --delete ИМЯ
/// Стенд с данными вразнобой (как в живой базе): Tests/DbGenerator с ключом --shuffle.
/// </summary>
internal static class DupBench
{
    public static int Run(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        Console.OutputEncoding = Encoding.UTF8;

        var path    = BenchUtil.ArgStr(args, "--db") ?? "";
        var colName = BenchUtil.ArgStr(args, "--col") ?? "Задачи";
        var batch   = BenchUtil.ArgInt(args, "--batch");
        if (!File.Exists(path)) { Console.WriteLine($"Нет файла {path}"); return 1; }

        // Первая ошибка SQLite — в отчет: откат транзакции после сбоя (диск, память) прячет ее за
        // вторичной «cannot rollback - no transaction is active» при закрытии транзакции.
        string? firstError = null;
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception is Microsoft.Data.Sqlite.SqliteException se && firstError is null)
                firstError = $"{se.SqliteErrorCode}/{se.SqliteExtendedErrorCode}: {se.Message}";
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
            Console.WriteLine($"Первая ошибка SQLite: {firstError}; свободно на диске {new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace / 1048576.0:N0} МБ");

        using var m = DatabaseManager.Open(7101, "dupbench", path);
        var col = m.Constructor.GetAllCollections().FirstOrDefault(c => c.Name == colName);
        if (col?.Id is not int colId) { Console.WriteLine($"Нет коллекции «{colName}»"); return 1; }
        var n = m.Constructor.CountRecords(colId);
        Console.WriteLine($"Коллекция «{colName}»: {n:N0} записей; пакет {(batch is int b ? $"{b:N0}" : "как в программе")}");

        long maxWal = 0;
        using var stop = new CancellationTokenSource();
        var deleteOnly = BenchUtil.ArgStr(args, "--delete");
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var wal = path + "-wal";
                if (File.Exists(wal)) maxWal = Math.Max(maxWal, new FileInfo(wal).Length);
                try { await Task.Delay(200, stop.Token); } catch (OperationCanceledException) { }
            }
        });

        var sw = Stopwatch.StartNew();
        if (deleteOnly is not null)
        {
            var victim = m.Constructor.GetAllCollections().FirstOrDefault(c => c.Name == deleteOnly);
            if (victim?.Id is not int vid) { Console.WriteLine($"Нет коллекции «{deleteOnly}»"); return 1; }
            var vn = m.Constructor.CountRecords(vid);
            m.Constructor.DeleteCollectionIsolated(vid);
            var ms = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"Удаление «{deleteOnly}»: {BenchUtil.Fmt(ms)} ({vn / Math.Max(ms / 1000, 0.001):N0} записей/с), -wal до {maxWal / 1048576.0:N0} МБ");
            stop.Cancel();
            sampler.Wait();
            return 0;
        }
        int newId;
        using (var ctx = new AppDbContext(path))
        {
            ctx.ApplyBulkPerformancePragmas();
            var repo  = new ConstructorRepository(ctx);
            var shell = repo.CreateCollectionShell(colId);
            newId = shell.Collection.Id!.Value;
            if (batch is int bs) repo.CopyCollectionData(colId, newId, null, bs);
            else                 repo.CopyCollectionData(colId, newId);
        }
        var dupMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"Дубль: {BenchUtil.Fmt(dupMs)} ({n / Math.Max(dupMs / 1000, 0.001):N0} записей/с), -wal до {maxWal / 1048576.0:N0} МБ");

        if (!args.Contains("--keep"))
        {
            maxWal = 0;
            sw.Restart();
            m.Constructor.DeleteCollectionIsolated(newId);
            var delMs = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"Удаление копии: {BenchUtil.Fmt(delMs)} ({n / Math.Max(delMs / 1000, 0.001):N0} записей/с), -wal до {maxWal / 1048576.0:N0} МБ");
        }
        stop.Cancel();
        sampler.Wait();
        return 0;
    }
}
