using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core;
using SmartBase.Database;
using SmartBase.Database.Backup;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>Настоящий дочерний процесс и файлы SQLite. Ни убийство процесса, ни потеря
/// временного файла до публикации не должны выдавать успех или вытеснять готовую копию.</summary>
public sealed class BackupInterruptionTests
{
    private static readonly DateTime PreviousSuccess = new(2026, 1, 1, 3, 0, 0);

    [Fact]
    public async Task Crash_after_publication_does_not_claim_that_completed_file_is_missing()
    {
        using var db = new TempDatabase();
        var collection = SampleData.AddCollection(db.Constructor);
        SampleData.AddRecords(db.Constructor, collection.Id!.Value, 7, 10);
        using var dir = new TempDir();
        var (app, folder, previous, id) = Prepare(db, dir);
        var ready = dir.File("ready");
        var release = dir.File("release");
        using var worker = Spawn(db.Path, app, ready, release);
        var output = worker.StandardOutput.ReadToEndAsync();
        var errors = worker.StandardError.ReadToEndAsync();
        try
        {
            await WaitForFile(ready, worker);
            var part = Assert.Single(Directory.GetFiles(folder, "*.part"));
            var completed = part[..^DatabaseManager.BackupPartSuffix.Length];
            // Блокируем только сохранение метаданных после публикации: сама проверка и
            // File.Move выполняются настоящим кодом, искусственного успеха нет.
            using (var metadata = new SystemDbContext(app))
            using (var transaction = metadata.Database.BeginTransaction())
            {
                metadata.Database.ExecuteSqlRaw("UPDATE app_settings SET Value=Value WHERE Key='backupFolder';");
                File.WriteAllText(release, "continue");
                await WaitForFile(completed, worker);
                Assert.False(worker.HasExited);
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                transaction.Rollback();
            }
            Assert.False(File.Exists(part));
            using var registry = DatabaseRegistry.Open(db.Path, app);
            AppSettings.AttachStore(registry);
            Assert.Equal(BackupRunOutcome.Running, AppSettings.LoadBackupRunState(id).Outcome);
            using var restarted = new BackupService(registry);
            string? result = null;
            restarted.BackupFinished += (_, _, message) => result = message;
            restarted.RecoverInterrupted();
            Assert.Equal(BackupService.InterruptedMessage, result);
            Assert.Contains("Результат не подтвержден", result);
            Assert.Equal(PreviousSuccess, AppSettings.LoadBackupRunState(id).LastSuccess);
            Assert.True(File.Exists(completed));
            Assert.True(File.Exists(previous));
            using var copy = DatabaseManager.Open(92, "copy", completed);
            Assert.Equal(7, copy.Constructor.CountRecords(collection.Id.Value));
            CheckSource(db, collection.Id.Value);
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Task.WhenAll(output, errors);
            SqliteGate.ClearAllPools();
        }
    }

    [Fact]
    public async Task Deleting_part_while_sqlite_is_writing_is_denied_or_fails_without_publishing()
    {
        using var db = new TempDatabase();
        var collection = SampleData.AddCollection(db.Constructor);
        SampleData.AddRecords(db.Constructor, collection.Id!.Value, 7, 10);
        // Достаточно большой настоящий файл: проверяем открытый писателем .part,
        // а не попадаем случайно в барьер уже законченной записи перед проверкой.
        db.Context.Database.ExecuteSqlRaw("CREATE TABLE backup_fault_payload (value BLOB NOT NULL);");
        db.Context.Database.ExecuteSqlRaw("WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<64) " +
            "INSERT INTO backup_fault_payload SELECT zeroblob(1048576) FROM n;");
        using var dir = new TempDir();
        var (app, folder, previous, id) = Prepare(db, dir);
        var previousHash = Hash(previous);
        var ready = dir.File("ready");
        var release = dir.File("release");
        using var worker = Spawn(db.Path, app, ready, release);
        var output = worker.StandardOutput.ReadToEndAsync();
        var errors = worker.StandardError.ReadToEndAsync();
        try
        {
            var watch = Stopwatch.StartNew();
            string part;
            while (true)
            {
                Assert.False(worker.HasExited, "Процесс завершился до удаления открытого файла");
                Assert.False(File.Exists(ready), "Тест должен вмешаться во время записи, до проверки");
                var candidates = Directory.GetFiles(folder, "*.part");
                if (candidates.Length == 1 && new FileInfo(candidates[0]).Length > 0)
                {
                    part = candidates[0];
                    break;
                }
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "Писатель не создал .part");
                await Task.Delay(1);
            }

            var deleted = false;
            try { File.Delete(part); deleted = true; }
            catch (IOException) { Assert.True(File.Exists(part)); } // ОС запретила удалить открытый файл
            catch (UnauthorizedAccessException) { Assert.True(File.Exists(part)); }
            File.WriteAllText(release, "continue");
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Assert.Equal(deleted ? 1 : 0, worker.ExitCode);
            Assert.Contains(deleted ? "ERROR:" : "SUCCESS", await output);
            Assert.Equal(previousHash, Hash(previous));
            Assert.Empty(Directory.GetFiles(folder, "*.part"));
            CheckSource(db, collection.Id.Value);
            using var registry = DatabaseRegistry.Open(db.Path, app);
            AppSettings.AttachStore(registry);
            Assert.Equal(deleted ? BackupRunOutcome.Failed : BackupRunOutcome.Succeeded,
                AppSettings.LoadBackupRunState(id).Outcome);
            if (deleted)
            {
                Assert.Equal([previous], Directory.GetFiles(folder, "*.db"));
                Assert.Equal(PreviousSuccess, AppSettings.LoadBackupRunState(id).LastSuccess);
            }
            else Assert.Equal(2, Directory.GetFiles(folder, "*.db").Length);
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Task.WhenAll(output, errors);
            SqliteGate.ClearAllPools();
        }
    }

    [Fact]
    public async Task Killed_process_leaves_unpublished_part_and_restart_reports_interruption_then_retries()
    {
        using var db = new TempDatabase();
        var collection = SampleData.AddCollection(db.Constructor);
        SampleData.AddRecords(db.Constructor, collection.Id!.Value, 7, 10);
        using var dir = new TempDir();
        var (app, folder, previous, id) = Prepare(db, dir);
        var previousHash = Hash(previous);
        var ready = dir.File("ready");
        var release = dir.File("release");
        using var worker = Spawn(db.Path, app, ready, release);
        var output = worker.StandardOutput.ReadToEndAsync();
        var errors = worker.StandardError.ReadToEndAsync();
        try
        {
            await WaitForFile(ready, worker);
            var part = Assert.Single(Directory.GetFiles(folder, "*.part"));
            Assert.True(new FileInfo(part).Length > 0);
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotEqual(0, worker.ExitCode);
            Assert.True(File.Exists(part));
            Assert.Equal([previous], Directory.GetFiles(folder, "*.db"));
            Assert.Equal(previousHash, Hash(previous));
            CheckSource(db, collection.Id.Value);

            using var registry = DatabaseRegistry.Open(db.Path, app);
            AppSettings.AttachStore(registry);
            Assert.Equal(BackupRunOutcome.Running, AppSettings.LoadBackupRunState(id).Outcome);
            Assert.Equal(PreviousSuccess, AppSettings.LoadBackupRunState(id).LastSuccess);
            using var restarted = new BackupService(registry);
            var results = new List<bool>();
            restarted.BackupFinished += (_, ok, _) => results.Add(ok);
            restarted.RecoverInterrupted();
            Assert.Equal([false], results);
            var state = AppSettings.LoadBackupRunState(id);
            Assert.Equal(BackupRunOutcome.Interrupted, state.Outcome);
            Assert.Equal(PreviousSuccess, state.LastSuccess);
            var failed = Assert.Single(BackgroundJobs.All.Where(j => j.Id == restarted.BackgroundJobId(id)));
            Assert.True(failed.Failed);
            Assert.Equal("IconArchive", failed.Icon);
            Assert.Contains("Копирование прервано", failed.Detail);
            restarted.RecoverInterrupted();
            Assert.Single(results); // повторный запуск службы не дублирует сообщение

            await restarted.RunNowAsync(id);
            Assert.Equal([false, true], results);
            Assert.Equal(BackupRunOutcome.Succeeded, AppSettings.LoadBackupRunState(id).Outcome);
            Assert.True(AppSettings.LoadBackupRunState(id).LastSuccess > PreviousSuccess);
            Assert.False(File.Exists(part));
            Assert.DoesNotContain(BackgroundJobs.All, j => j.Id == restarted.BackgroundJobId(id));
            Assert.Equal(previousHash, Hash(previous));
            CheckSource(db, collection.Id.Value);
            var current = Assert.Single(Directory.GetFiles(folder, "*.db").Where(f => f != previous));
            using var copy = DatabaseManager.Open(92, "copy", current);
            Assert.Equal(7, copy.Constructor.CountRecords(collection.Id.Value));
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Task.WhenAll(output, errors);
            SqliteGate.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("corrupt")]
    [InlineData("block-rename")]
    public async Task Damaged_or_missing_work_file_never_reports_success_or_rotates_previous_copy(string fault)
    {
        using var db = new TempDatabase();
        var collection = SampleData.AddCollection(db.Constructor);
        SampleData.AddRecords(db.Constructor, collection.Id!.Value, 7, 10);
        using var dir = new TempDir();
        var (app, folder, previous, id) = Prepare(db, dir);
        var previousHash = Hash(previous);
        var ready = dir.File("ready");
        var release = dir.File("release");
        using var worker = Spawn(db.Path, app, ready, release);
        var output = worker.StandardOutput.ReadToEndAsync();
        var errors = worker.StandardError.ReadToEndAsync();
        string? blockedTarget = null;
        try
        {
            await WaitForFile(ready, worker);
            var part = Assert.Single(Directory.GetFiles(folder, "*.part"));
            if (fault == "delete") File.Delete(part);
            else if (fault == "corrupt") File.WriteAllText(part, "поврежденная копия");
            else
            {
                blockedTarget = part[..^DatabaseManager.BackupPartSuffix.Length];
                Directory.CreateDirectory(blockedTarget);
            }
            File.WriteAllText(release, "continue");
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotEqual(0, worker.ExitCode);
            Assert.Contains("ERROR:", await output);
            Assert.DoesNotContain("SUCCESS", await output);
            Assert.Empty(Directory.GetFiles(folder, "*.part"));
            Assert.Equal([previous], Directory.GetFiles(folder, "*.db"));
            Assert.Equal(previousHash, Hash(previous));
            CheckSource(db, collection.Id.Value);

            using var registry = DatabaseRegistry.Open(db.Path, app);
            AppSettings.AttachStore(registry);
            Assert.Equal(BackupRunOutcome.Failed, AppSettings.LoadBackupRunState(id).Outcome);
            Assert.Equal(PreviousSuccess, AppSettings.LoadBackupRunState(id).LastSuccess);
            Assert.Equal(PreviousSuccess, AppSettings.LoadBackupLastRun(id));
            if (blockedTarget is not null) Directory.Delete(blockedTarget);
            using var retry = new BackupService(registry);
            var results = new List<bool>();
            retry.BackupFinished += (_, ok, _) => results.Add(ok);
            await retry.RunNowAsync(id);
            Assert.Equal([true], results);
            Assert.Equal(previousHash, Hash(previous));
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Task.WhenAll(output, errors);
            SqliteGate.ClearAllPools();
        }
    }

    private static (string App, string Folder, string Previous, int Id) Prepare(TempDatabase db, TempDir dir)
    {
        var app = dir.File("app.db");
        var folder = dir.File("copies");
        Directory.CreateDirectory(folder);
        using var registry = DatabaseRegistry.Open(db.Path, app);
        AppSettings.AttachStore(registry);
        var id = registry.GetAll().Single().Id;
        AppSettings.SaveBackupFolder(folder);
        AppSettings.SaveBackupKeep(2);
        AppSettings.SaveBackupLastRun(id, PreviousSuccess);
        AppSettings.SaveBackupRunState(id, new(PreviousSuccess, PreviousSuccess, BackupRunOutcome.Succeeded));
        var previous = Path.Combine(folder, BackupRotation.CopyFileName("Основная", PreviousSuccess, "db"));
        DatabaseManager.BackupTo(db.Path, "Основная", previous, null);
        return (app, folder, previous, id);
    }

    private static Process Spawn(string source, string app, string ready, string release)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SmartBase.csproj"))) root = root.Parent;
        Assert.NotNull(root);
#if DEBUG
        const string configuration = "debug";
#else
        const string configuration = "release";
#endif
        var executable = Path.Combine(root.FullName, "artifacts", "bin", "MultiProcess", configuration, "MultiProcess.dll");
        Assert.True(File.Exists(executable), "Дочерний стенд должен быть собран ProjectReference до начала теста");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var argument in new[] { executable, "--backup-worker", source, app, ready, release })
            start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static async Task WaitForFile(string marker, Process worker)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(marker))
        {
            Assert.False(worker.HasExited, "Дочерний процесс завершился до проверяемого этапа");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "Дочерний процесс не дошел до этапа проверки");
            await Task.Delay(10);
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void CheckSource(TempDatabase db, int collectionId)
    {
        Assert.True(File.Exists(db.Path), "Файл исходной базы должен сохраниться, а не только открытое подключение");
        Assert.Equal(7, db.Constructor.CountRecords(collectionId));
        Assert.Equal(Enumerable.Range(1, 7).Select(i => $"R{i:D4}"),
            db.Context.DbRecords.Where(r => r.CollectionId == collectionId).OrderBy(r => r.Id).Select(r => r.Title).ToList());
        db.Context.Database.OpenConnection();
        using var command = db.Context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", command.ExecuteScalar());
        db.Context.Database.CloseConnection();
    }
}
