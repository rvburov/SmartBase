using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core;
using SmartBase.Database;
using SmartBase.Database.Backup;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>Управление настоящей копией: нехватка места и явная отмена не публикуют
/// недописанный файл, не запускают ротацию и не стирают предыдущий подтвержденный успех.</summary>
public sealed class BackupControlTests
{
    private static readonly DateTime PreviousSuccess = new(2026, 1, 1, 3, 0, 0);

    [Fact]
    public async Task Canceling_sqlite_while_the_part_is_being_written_cleans_up_without_damaging_the_source()
    {
        using var db = new TempDatabase();
        db.Context.Database.ExecuteSqlRaw("CREATE TABLE backup_cancel_payload (value BLOB NOT NULL);");
        db.Context.Database.ExecuteSqlRaw("WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<32) " +
            "INSERT INTO backup_cancel_payload SELECT zeroblob(1048576) FROM n;");
        using var dir = new TempDir();
        var target = dir.File("copy.db");
        var previous = "previous-confirmed-copy"u8.ToArray();
        File.WriteAllBytes(target, previous);
        var part = target + DatabaseManager.BackupPartSuffix;
        using var cancellation = new CancellationTokenSource();
        var copying = Task.Run(() => DatabaseManager.BackupTo(db.Path, "source", target, null, cancellation.Token));
        var watch = Stopwatch.StartNew();
        var observedWriting = false;
        try
        {
            while (!copying.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(15))
            {
                try { observedWriting = File.Exists(part) && new FileInfo(part).Length >= 1048576; }
                catch (IOException) { }
                if (observedWriting) break;
                await Task.Yield();
            }
            Assert.True(observedWriting && !copying.IsCompleted, "Тест не застал реальную запись .part");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copying.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            cancellation.Cancel();
            try { await copying.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
        Assert.Equal(previous, File.ReadAllBytes(target));
        Assert.False(File.Exists(part));
        using var count = db.Context.Database.GetDbConnection().CreateCommand();
        db.Context.Database.OpenConnection();
        count.CommandText = "SELECT count(*) FROM backup_cancel_payload WHERE length(value)=1048576;";
        Assert.Equal(32L, count.ExecuteScalar());
        db.Context.Database.CloseConnection();
        Assert.Null(DatabaseManager.VerifyCopy(db.Path, "source", null));
    }

    [Fact]
    public async Task Insufficient_target_space_rejects_before_writing_and_preserves_the_previous_copy()
    {
        using var db = new TempDatabase();
        var collection = SampleData.AddCollection(db.Constructor);
        SampleData.AddRecords(db.Constructor, collection.Id!.Value, 7, 10);
        using var dir = new TempDir();
        using var registry = DatabaseRegistry.Open(db.Path, dir.File("app.db"));
        var (id, folder, previous, bytes) = Prepare(db, dir, registry);
        using var service = new BackupService(registry);
        var results = new List<(bool Ok, string Message)>();
        service.BackupFinished += (_, ok, message) => results.Add((ok, message));
        var checkedFolders = new List<string>();
        var previousProbe = BackupSpace.AvailableBytesOverride.Value;
        BackupSpace.AvailableBytesOverride.Value = path =>
        {
            lock (checkedFolders) checkedFolders.Add(Path.GetFullPath(path));
            return 0;
        };
        try { await service.RunNowAsync(id); }
        finally { BackupSpace.AvailableBytesOverride.Value = previousProbe; }

        var failed = Assert.Single(results);
        Assert.False(failed.Ok);
        Assert.Contains("мест", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFullPath(folder), checkedFolders);
        Assert.Equal([previous], Directory.GetFiles(folder));
        Assert.Equal(bytes, File.ReadAllBytes(previous));
        Assert.Equal(PreviousSuccess, AppSettings.LoadBackupLastRun(id));
        var state = AppSettings.LoadBackupRunState(id);
        Assert.Equal(PreviousSuccess, state.LastSuccess);
        Assert.Equal(BackupRunOutcome.Failed, state.Outcome);
        Assert.Equal(7, db.Constructor.CountRecords(collection.Id.Value));

        // Освободили место: ручной повтор реально создает проверяемый файл и запускает ротацию.
        await service.RunNowAsync(id);
        Assert.Equal([false, true], results.Select(r => r.Ok));
        Assert.Equal(BackupRunOutcome.Succeeded, AppSettings.LoadBackupRunState(id).Outcome);
        var current = Assert.Single(Directory.GetFiles(folder));
        Assert.NotEqual(previous, current);
        using var copy = DatabaseManager.Open(91, "copy", current);
        Assert.Equal(7, copy.Constructor.CountRecords(collection.Id.Value));
    }

    [Fact]
    public async Task Unknown_target_capacity_reports_that_the_check_failed_without_writing_a_copy()
    {
        using var db = new TempDatabase();
        var collection = SampleData.AddCollection(db.Constructor);
        SampleData.AddRecords(db.Constructor, collection.Id!.Value, 3, 10);
        using var dir = new TempDir();
        var target = dir.File("copy.db");
        var previousProbe = BackupSpace.AvailableBytesOverride.Value;
        BackupSpace.AvailableBytesOverride.Value = _ => null;
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => Task.Run(() => DatabaseManager.BackupTo(db.Path, "source", target, null)));
            Assert.Contains("проверить", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { BackupSpace.AvailableBytesOverride.Value = previousProbe; }
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + DatabaseManager.BackupPartSuffix));
        Assert.Equal(3, db.Constructor.CountRecords(collection.Id.Value));
    }

    [Fact]
    public async Task Background_stop_during_verification_keeps_source_previous_success_and_allows_retry()
    {
        using var db = new TempDatabase();
        var collection = SampleData.AddCollection(db.Constructor);
        SampleData.AddRecords(db.Constructor, collection.Id!.Value, 7, 10);
        using var dir = new TempDir();
        using var registry = DatabaseRegistry.Open(db.Path, dir.File("app.db"));
        var (id, folder, previous, bytes) = Prepare(db, dir, registry);
        using var service = new BackupService(registry);
        var results = new List<(bool Ok, string Message)>();
        service.BackupFinished += (_, ok, message) => results.Add((ok, message));
        var stopped = 0;
        service.BackupProgress += (_, phase, fraction) =>
        {
            if (!phase.StartsWith("Проверяю копию", StringComparison.Ordinal)
                || fraction is not null || Interlocked.Exchange(ref stopped, 1) != 0) return;
            Assert.Single(Directory.GetFiles(folder, "*.part"));
            var job = Assert.Single(BackgroundJobs.All.Where(j => j.Id == service.BackgroundJobId(id)));
            Assert.NotNull(job.Stop);
            job.Stop!();
        };

        await service.RunNowAsync(id);
        Assert.Equal(1, stopped);
        var canceled = Assert.Single(results);
        Assert.False(canceled.Ok);
        Assert.Equal(BackupService.CanceledMessage, canceled.Message);
        Assert.Equal([previous], Directory.GetFiles(folder));
        Assert.Equal(bytes, File.ReadAllBytes(previous));
        Assert.Equal(PreviousSuccess, AppSettings.LoadBackupLastRun(id));
        var state = AppSettings.LoadBackupRunState(id);
        Assert.Equal(PreviousSuccess, state.LastSuccess);
        Assert.Equal(BackupRunOutcome.Canceled, state.Outcome);
        Assert.DoesNotContain(BackgroundJobs.All, j => j.Id == service.BackgroundJobId(id));
        Assert.Equal(7, db.Constructor.CountRecords(collection.Id.Value));

        await service.RunNowAsync(id);
        Assert.Equal([false, true], results.Select(r => r.Ok));
        Assert.Equal(BackupRunOutcome.Succeeded, AppSettings.LoadBackupRunState(id).Outcome);
        var current = Assert.Single(Directory.GetFiles(folder));
        using var copy = DatabaseManager.Open(91, "copy", current);
        Assert.Equal(7, copy.Constructor.CountRecords(collection.Id.Value));
    }

    [Fact]
    public async Task Canceling_a_duplicate_queued_attempt_preserves_the_active_attempt_and_its_status()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        using var registry = DatabaseRegistry.Open(db.Path, dir.File("app.db"));
        var (id, folder, previous, bytes) = Prepare(db, dir, registry);
        using var service = new BackupService(registry);
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        service.BackupProgress += (dbId, phase, fraction) =>
        {
            if (dbId != id || !phase.StartsWith("Проверяю копию", StringComparison.Ordinal)
                || fraction is not null || Interlocked.Exchange(ref held, 1) != 0) return;
            ready.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(20)), "Тест не освободил активную копию");
        };
        var active = service.RunNowAsync(id);
        Task? queued = null;
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var activeJob = Assert.Single(BackgroundJobs.All.Where(j => j.Id == service.BackgroundJobId(id)));
            queued = service.RunNowAsync(id);
            Assert.False(queued.IsCompleted);
            var queuedJob = Assert.Single(BackgroundJobs.All.Where(j => j.Id.StartsWith(service.BackgroundJobId(id), StringComparison.Ordinal)
                && j.Id != activeJob.Id));
            Assert.Contains("Ожидаю", queuedJob.Detail);
            Assert.NotNull(queuedJob.Stop);
            queuedJob.Stop!();
            await queued.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(active.IsCompleted);
            Assert.Contains(BackgroundJobs.All, j => j.Id == activeJob.Id && !j.Failed && j.Stop is not null);
            Assert.DoesNotContain(BackgroundJobs.All, j => j.Id == queuedJob.Id);
            var state = AppSettings.LoadBackupRunState(id);
            Assert.Equal(BackupRunOutcome.Running, state.Outcome);
            Assert.Equal(PreviousSuccess, state.LastSuccess);
            Assert.Equal(PreviousSuccess, AppSettings.LoadBackupLastRun(id));
            Assert.Equal(bytes, File.ReadAllBytes(previous));
        }
        finally
        {
            release.Set();
            await active.WaitAsync(TimeSpan.FromSeconds(15));
            if (queued is not null) await queued.WaitAsync(TimeSpan.FromSeconds(15));
        }
        Assert.Equal(BackupRunOutcome.Succeeded, AppSettings.LoadBackupRunState(id).Outcome);
        Assert.True(AppSettings.LoadBackupRunState(id).LastSuccess > PreviousSuccess);
        Assert.DoesNotContain(BackgroundJobs.All, j => j.Id.StartsWith(service.BackgroundJobId(id), StringComparison.Ordinal));
        Assert.Single(Directory.GetFiles(folder));
        // Отмена попытки не оставляет занятый слот и не отменяет следующий ручной запуск.
        await service.RunNowAsync(id);
        Assert.Equal(BackupRunOutcome.Succeeded, AppSettings.LoadBackupRunState(id).Outcome);
    }

    [Fact]
    public async Task A_queued_backup_can_be_canceled_without_stopping_the_active_database()
    {
        using var firstDb = new TempDatabase();
        using var secondDb = new TempDatabase(2, "second");
        using var dir = new TempDir();
        using var registry = DatabaseRegistry.Open(firstDb.Path, dir.File("app.db"));
        AppSettings.AttachStore(registry);
        var firstId = registry.GetAll().Single().Id;
        var second = registry.Add("Вторая", secondDb.Path);
        var folder = dir.File("copies");
        AppSettings.SaveBackupFolder(folder);
        using var service = new BackupService(registry);
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BackupProgress += (id, phase, fraction) =>
        {
            if (id != firstId || !phase.StartsWith("Проверяю копию", StringComparison.Ordinal) || fraction is not null) return;
            ready.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(20)), "Тест не освободил активную копию");
        };
        var active = service.RunNowAsync(firstId);
        Task? queued = null;
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            queued = service.RunNowAsync(second.Id);
            var job = Assert.Single(BackgroundJobs.All.Where(j => j.Id == service.BackgroundJobId(second.Id)));
            Assert.NotNull(job.Stop);
            job.Stop!();
            await queued.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(active.IsCompleted);
            Assert.Null(AppSettings.LoadBackupRunState(second.Id).LastSuccess);
            Assert.DoesNotContain(Directory.GetFiles(folder), f => BackupRotation.IsCopyOf(Path.GetFileName(f), second.Name, "db"));
        }
        finally
        {
            release.Set();
            await active.WaitAsync(TimeSpan.FromSeconds(15));
            if (queued is not null) await queued.WaitAsync(TimeSpan.FromSeconds(15));
        }
        Assert.Equal(BackupRunOutcome.Succeeded, AppSettings.LoadBackupRunState(firstId).Outcome);
        Assert.Single(Directory.GetFiles(folder));
        Assert.DoesNotContain(BackgroundJobs.All, j => j.Id == service.BackgroundJobId(firstId) || j.Id == service.BackgroundJobId(second.Id));
    }

    [Fact]
    public void Verification_can_be_canceled_after_real_checks_without_damaging_the_complete_file()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var target = dir.File("copy.db");
        DatabaseManager.BackupTo(db.Path, "source", target, null);
        var original = File.ReadAllBytes(target);
        using var cancellation = new CancellationTokenSource();
        var checkedParts = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => DatabaseManager.VerifyCopy(target, "source", (_, fraction) =>
        {
            if (fraction is not > 0) return;
            Interlocked.Increment(ref checkedParts);
            cancellation.Cancel();
        }, cancellation.Token));
        Assert.True(checkedParts > 0);
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Null(DatabaseManager.VerifyCopy(target, "source", null));
    }

    private static (int Id, string Folder, string Previous, byte[] Bytes) Prepare(
        TempDatabase db, TempDir dir, DatabaseRegistry registry)
    {
        AppSettings.AttachStore(registry);
        var id = registry.GetAll().Single().Id;
        var folder = dir.File("copies");
        Directory.CreateDirectory(folder);
        AppSettings.SaveBackupFolder(folder);
        AppSettings.SaveBackupKeep(1);
        var previous = Path.Combine(folder, BackupRotation.CopyFileName("Основная", PreviousSuccess, "db"));
        DatabaseManager.BackupTo(db.Path, "Основная", previous, null);
        AppSettings.SaveBackupLastRun(id, PreviousSuccess);
        AppSettings.SaveBackupRunState(id, new(PreviousSuccess, PreviousSuccess, BackupRunOutcome.Succeeded));
        return (id, folder, previous, File.ReadAllBytes(previous));
    }
}
