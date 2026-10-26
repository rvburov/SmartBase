using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Backup;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>Живые писатели во время копии, очередь копий и завершение процесса внутри write-транзакции.</summary>
public sealed class ConcurrentBackupTests
{
    [SkippableFact]
    public async Task Postgres_backup_with_a_live_writer_restores_a_consistent_committed_snapshot()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Локальный тестовый PostgreSQL недоступен");
        Skip.If(PgDump.FindBinary() is null, "Для проверки восстановления нужны pg_dump и pg_restore");
        using var fixture = new MultiUserTestDatabase("postgres");
        using var directory = new TempDir();
        var collection = SampleData.AddCollection(fixture.Repo, "Снимок PG").Id!.Value;
        var text = SampleData.AddField(fixture.Repo, collection, "Версия").Id!.Value;
        var date = SampleData.AddCalendarDateField(fixture.Repo, collection).Id!.Value;
        var ready = new ManualResetEventSlim(false);
        var stop = new CancellationTokenSource();
        Exception? writerError = null;
        var committed = 0;
        var writer = new Thread(() =>
        {
            try
            {
                using var context = fixture.OpenContext();
                var repo = new ConstructorRepository(context);
                var watch = Stopwatch.StartNew();
                for (var sequence = 1; !stop.IsCancellationRequested; sequence++)
                {
                    if (watch.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Писатель дампа не остановлен");
                    var title = $"pg-epoch-{sequence}";
                    using (var transaction = context.Database.BeginTransaction())
                    {
                        var id = repo.AddRecord(new DbRecord { CollectionId = collection, Title = title, Position = sequence * 1000 }).Id!.Value;
                        repo.SetFieldValue(id, text, title);
                        repo.SetFieldValue(id, date, "2026-10-26");
                        transaction.Commit();
                    }
                    context.ChangeTracker.Clear();
                    Interlocked.Increment(ref committed);
                    ready.Set();
                    Thread.Yield();
                }
            }
            catch (Exception ex) { writerError = ex; ready.Set(); }
        }) { IsBackground = true, Name = "pg-backup-live-writer" };
        var dump = directory.File("concurrent.dump");
        Process? worker = null;
        Task<string>? output = null, errors = null;
        writer.Start();
        var joined = false;
        var beforeDump = 0;
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(20)), "Писатель PG не выполнил первый commit");
            Assert.Null(writerError);
            beforeDump = Volatile.Read(ref committed);
            worker = Spawn("--pg-backup-worker", new NpgsqlConnectionStringBuilder(fixture.Path).Database!, dump);
            output = worker.StandardOutput.ReadToEndAsync();
            errors = worker.StandardError.ReadToEndAsync();
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
            Assert.Equal(0, worker.ExitCode);
            Assert.Contains("SUCCESS", await output);
        }
        finally
        {
            stop.Cancel();
            try
            {
                if (worker is not null)
                {
                    if (!worker.HasExited) worker.Kill(entireProcessTree: true);
                    await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    if (output is not null && errors is not null) await Task.WhenAll(output, errors);
                    worker.Dispose();
                }
            }
            finally
            {
                joined = writer.Join(TimeSpan.FromSeconds(30));
                if (joined) { ready.Dispose(); stop.Dispose(); }
            }
        }
        Assert.True(joined, "Писатель PG не завершился");
        Assert.Null(writerError);
        Assert.True(committed > beforeDump, "Писатель должен продолжать commit во время запуска дампа");
        var restoredName = $"sb_backup_restore_{Guid.NewGuid():N}";
        var created = false;
        void Admin(string sql)
        {
            using var connection = new NpgsqlConnection(PgTestServer.ConnectionString("postgres") + ";Pooling=false");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        try
        {
            Admin($"CREATE DATABASE \"{restoredName}\"");
            created = true;
            var dumpBinary = PgDump.FindBinary()!;
            var restoreBinary = Path.GetDirectoryName(dumpBinary) is { Length: > 0 } parent
                ? Path.Combine(parent, OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore")
                : OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore";
            var start = new ProcessStartInfo(restoreBinary)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (var argument in new[] { "--exit-on-error", "--no-owner", "--no-privileges", "--no-password",
                         "--host", "127.0.0.1", "--port", "5455", "--username", "postgres", "--dbname", restoredName, dump })
                start.ArgumentList.Add(argument);
            start.Environment["PGPASSWORD"] = PgTestServer.Password;
            start.Environment["PGSSLMODE"] = PgConnectionInfo.DefaultSslMode.ToLowerInvariant();
            using var restore = Process.Start(start)!;
            var restoreOutput = restore.StandardOutput.ReadToEndAsync();
            var restoreError = restore.StandardError.ReadToEndAsync();
            try { await restore.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40)); }
            finally
            {
                if (!restore.HasExited) restore.Kill(entireProcessTree: true);
                await restore.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            await restoreOutput;
            Assert.True(restore.ExitCode == 0, await restoreError);
            using var check = new AppDbContext(new NpgsqlConnectionStringBuilder(fixture.Path) { Database = restoredName }.ConnectionString);
            var records = check.DbRecords.AsNoTracking().Where(r => r.CollectionId == collection).ToList();
            Assert.InRange(records.Count, 1, committed);
            Assert.Equal(records.Count * 2, check.DbFieldValues.Count());
            foreach (var record in records)
            {
                var values = check.DbFieldValues.Where(v => v.RecordId == record.Id).ToDictionary(v => v.FieldId, v => v.Value);
                Assert.Equal(record.Title, values[text]);
                Assert.Equal("2026-10-26", values[date]);
            }
            MultiUserTestDatabase.AssertHealthy(check);
        }
        finally { if (created) Admin($"DROP DATABASE \"{restoredName}\" WITH (FORCE)"); }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    public async Task Backup_with_live_transactional_writers_contains_only_complete_committed_records(string profile)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        using var directory = new TempDir();
        var collection = SampleData.AddCollection(fixture.Repo, "Снимок").Id!.Value;
        var text = SampleData.AddField(fixture.Repo, collection, "Версия").Id!.Value;
        var date = SampleData.AddCalendarDateField(fixture.Repo, collection).Id!.Value;
        var app = directory.File("app.db");
        var folder = directory.File("copies");
        using (var registry = DatabaseRegistry.Open(fixture.Path, app))
        {
            AppSettings.AttachStore(registry);
            AppSettings.SaveBackupFolder(folder);
        }
        var ready = new CountdownEvent(2);
        var afterSnapshot = new ManualResetEventSlim(false);
        var stop = new CancellationTokenSource();
        var errors = new ConcurrentQueue<Exception>();
        var snapshotTaken = 0;
        var committed = 0;
        var writers = Enumerable.Range(0, 2).Select(writer => new Thread(() =>
        {
            var announced = false;
            try
            {
                using var context = fixture.OpenContext();
                var repo = new ConstructorRepository(context);
                var watch = Stopwatch.StartNew();
                for (var sequence = 1; !stop.IsCancellationRequested; sequence++)
                {
                    if (watch.Elapsed > TimeSpan.FromSeconds(60))
                        throw new TimeoutException("Родитель не остановил писателя резервной копии");
                    var title = $"writer-{writer}-record-{sequence}";
                    using (var transaction = context.Database.BeginTransaction())
                    {
                        var id = repo.AddRecord(new DbRecord
                        {
                            CollectionId = collection, Title = title, Position = sequence * 1000,
                        }).Id!.Value;
                        repo.SetFieldValue(id, text, title);
                        repo.SetFieldValue(id, date, "2026-10-26");
                        transaction.Commit();
                    }
                    context.ChangeTracker.Clear();
                    Interlocked.Increment(ref committed);
                    if (!announced) { ready.Signal(); announced = true; }
                    if (Volatile.Read(ref snapshotTaken) != 0) afterSnapshot.Set();
                    Thread.Yield();
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
            finally { if (!announced) ready.Signal(); }
        }) { IsBackground = true, Name = $"backup-live-writer-{writer}" }).ToArray();
        foreach (var writer in writers) writer.Start();
        var joined = true;
        var writersAtSnapshot = 0;
        Process? worker = null;
        Task<string>? output = null, workerErrors = null;
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(20)), "Писатели не выполнили первые commit");
            Assert.True(errors.IsEmpty, string.Join(Environment.NewLine, errors));
            Assert.True(Volatile.Read(ref committed) >= 2);
            var marker = directory.File("snapshot-ready");
            var release = directory.File("snapshot-release");
            worker = Spawn("--backup-live-worker", fixture.Path, app, marker, release, profile);
            output = worker.StandardOutput.ReadToEndAsync();
            workerErrors = worker.StandardError.ReadToEndAsync();
            await WaitForFile(marker, worker);
            writersAtSnapshot = Volatile.Read(ref committed);
            Interlocked.Exchange(ref snapshotTaken, 1);
            Assert.True(afterSnapshot.Wait(TimeSpan.FromSeconds(20)),
                "Во время проверки готового снимка писатели должны продолжить работу");
            File.WriteAllText(release, "continue");
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, worker.ExitCode);
            Assert.Contains("SUCCESS", await output);
        }
        finally
        {
            stop.Cancel();
            try
            {
                if (worker is not null)
                {
                    if (!worker.HasExited) worker.Kill(entireProcessTree: true);
                    await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    if (output is not null && workerErrors is not null) await Task.WhenAll(output, workerErrors);
                    worker.Dispose();
                }
            }
            finally
            {
                var watch = Stopwatch.StartNew();
                foreach (var writer in writers)
                {
                    var remaining = TimeSpan.FromSeconds(30) - watch.Elapsed;
                    joined &= writer.Join(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                }
                if (joined) { ready.Dispose(); afterSnapshot.Dispose(); stop.Dispose(); }
            }
        }

        Assert.True(joined, "Писатели не завершились после остановки");
        Assert.True(errors.IsEmpty, string.Join(Environment.NewLine, errors));
        Assert.True(Volatile.Read(ref committed) > writersAtSnapshot);
        var target = Assert.Single(Directory.GetFiles(folder, "*.db"));
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(target + DatabaseManager.BackupPartSuffix));
        using var source = fixture.OpenContext();
        using var copy = new AppDbContext(target);
        copy.ApplyPerformancePragmas();
        var rows = copy.DbRecords.AsNoTracking().Where(r => r.CollectionId == collection).ToList();
        Assert.NotEmpty(rows);
        Assert.InRange(rows.Count, 1, committed);
        Assert.Equal(committed, source.DbRecords.Count(r => r.CollectionId == collection));
        foreach (var row in rows)
        {
            var values = copy.DbFieldValues.AsNoTracking().Where(v => v.RecordId == row.Id).ToDictionary(v => v.FieldId, v => v.Value);
            Assert.Equal(2, values.Count);
            Assert.Equal(row.Title, values[text]);
            Assert.Equal("2026-10-26", values[date]);
            Assert.Single(copy.CalendarIndex.Where(i => i.RecordId == row.Id && i.FieldId == date));
            Assert.True(source.DbRecords.Any(r => r.Id == row.Id && r.Title == row.Title));
        }
        MultiUserTestDatabase.AssertHealthy(copy);
        MultiUserTestDatabase.AssertHealthy(source);
        AppDbContext.ResetPool(target);
    }

    [Fact]
    public async Task Two_manual_runs_are_queued_and_the_second_snapshot_includes_the_intermediate_commit()
    {
        using var database = new TempDatabase();
        var collection = SampleData.AddCollection(database.Constructor).Id!.Value;
        SampleData.AddRecords(database.Constructor, collection, 3);
        using var directory = new TempDir();
        using var registry = DatabaseRegistry.Open(database.Path, directory.File("app.db"));
        AppSettings.AttachStore(registry);
        var folder = directory.File("copies");
        AppSettings.SaveBackupFolder(folder);
        AppSettings.SaveBackupKeep(4);
        var id = registry.GetAll().Single().Id;
        using var service = new BackupService(registry);
        var snapshotReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);
        var firstVerification = 0;
        var active = 0;
        var maximum = 0;
        var started = 0;
        var finished = new ConcurrentQueue<(bool Success, string Message, int[] CopyCounts)>();
        var callbackErrors = new ConcurrentQueue<Exception>();
        service.BackupStarted += _ =>
        {
            Interlocked.Increment(ref started);
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
        };
        service.BackupProgress += (_, message, fraction) =>
        {
            if (fraction is not null || !message.StartsWith("Проверяю копию", StringComparison.Ordinal)
                || Interlocked.Exchange(ref firstVerification, 1) != 0) return;
            snapshotReady.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Не освобожден первый снимок");
        };
        service.BackupFinished += (_, success, message) =>
        {
            try
            {
                var counts = Directory.GetFiles(folder, "*.db").Select(path =>
                {
                    int count;
                    using (var copy = new AppDbContext(path))
                        count = copy.DbRecords.Count(r => r.CollectionId == collection);
                    // Наблюдатель теста не должен сам держать копию открытой в пуле,
                    // иначе Windows запретит службе опубликовать следующую копию того же имени.
                    AppDbContext.ResetPool(path);
                    return count;
                }).ToArray();
                finished.Enqueue((success, message, counts));
            }
            catch (Exception ex) { callbackErrors.Enqueue(ex); }
            finally { Interlocked.Decrement(ref active); }
        };

        Task? first = null, second = null;
        try
        {
            first = service.RunNowAsync(id);
            await snapshotReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
            second = service.RunNowAsync(id);
            Assert.False(second.IsCompleted, "Второй ручной запуск должен ждать занятую очередь");
            Assert.Equal(1, Volatile.Read(ref started));
            using (var writer = new AppDbContext(database.Path))
            {
                writer.ApplyPerformancePragmas();
                new ConstructorRepository(writer).AddRecord(new DbRecord
                {
                    CollectionId = collection, Title = "Между двумя копиями", Position = 4000,
                });
            }
        }
        finally
        {
            release.Set();
            var pending = new[] { first, second }.Where(t => t is not null).Cast<Task>().ToArray();
            if (pending.Length > 0) await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(40));
            release.Dispose();
        }

        Assert.True(callbackErrors.IsEmpty, string.Join(Environment.NewLine, callbackErrors));
        Assert.Equal(2, started);
        Assert.Equal(0, active);
        Assert.Equal(1, maximum);
        var outcomes = finished.ToArray();
        Assert.Equal(2, outcomes.Length);
        Assert.All(outcomes, result => { Assert.True(result.Success, result.Message); Assert.Equal("", result.Message); });
        Assert.Equal([3], outcomes[0].CopyCounts);
        Assert.Contains(4, outcomes[1].CopyCounts);
        Assert.Empty(Directory.GetFiles(folder, "*.part"));
        Assert.Equal(BackupRunOutcome.Succeeded, AppSettings.LoadBackupRunState(id).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleting_the_completed_copy_or_directory_does_not_prevent_a_fresh_manual_backup(bool deleteDirectory)
    {
        using var database = new TempDatabase();
        var collection = SampleData.AddCollection(database.Constructor).Id!.Value;
        SampleData.AddRecords(database.Constructor, collection, 4);
        using var directory = new TempDir();
        using var registry = DatabaseRegistry.Open(database.Path, directory.File("app.db"));
        AppSettings.AttachStore(registry);
        var folder = directory.File("copies");
        AppSettings.SaveBackupFolder(folder);
        var id = registry.GetAll().Single().Id;
        using var service = new BackupService(registry);
        var outcomes = new List<bool>();
        service.BackupFinished += (_, success, _) => outcomes.Add(success);
        await service.RunNowAsync(id);
        Assert.Equal([true], outcomes);
        var previousSuccess = AppSettings.LoadBackupRunState(id).LastSuccess;
        var copy = Assert.Single(Directory.GetFiles(folder, "*.db"));
        if (deleteDirectory) Directory.Delete(folder, true); else File.Delete(copy);
        await service.RunNowAsync(id);
        Assert.Equal([true, true], outcomes);
        Assert.Equal(BackupRunOutcome.Succeeded, AppSettings.LoadBackupRunState(id).Outcome);
        Assert.True(AppSettings.LoadBackupRunState(id).LastSuccess >= previousSuccess);
        var restored = Assert.Single(Directory.GetFiles(folder, "*.db"));
        using var check = new AppDbContext(restored);
        Assert.Equal(4, check.DbRecords.Count(r => r.CollectionId == collection));
        MultiUserTestDatabase.AssertHealthy(check);
        Assert.Empty(Directory.GetFiles(folder, "*.part"));
        Assert.True(File.Exists(database.Path));
        Assert.Equal(4, database.Constructor.CountRecords(collection));
        AppDbContext.ResetPool(restored);
    }

    [SkippableTheory]
    [InlineData("sqlite", true)]
    [InlineData("sqlite-shared", true)]
    [InlineData("postgres", true)]
    [InlineData("sqlite", false)]
    [InlineData("sqlite-shared", false)]
    [InlineData("postgres", false)]
    public async Task A_killed_client_rolls_back_a_whole_transaction_and_a_released_client_commits_it(string profile, bool kill)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        using var directory = new TempDir();
        var collection = SampleData.AddCollection(fixture.Repo, "Аварийная транзакция").Id!.Value;
        var text = SampleData.AddField(fixture.Repo, collection, "Текст").Id!.Value;
        var date = SampleData.AddCalendarDateField(fixture.Repo, collection).Id!.Value;
        var edited = SampleData.AddRecord(fixture.Repo, collection, "Изменяемая", 1000).Id!.Value;
        var deleted = SampleData.AddRecord(fixture.Repo, collection, "Удаляемая", 2000).Id!.Value;
        foreach (var record in new[] { edited, deleted })
        {
            fixture.Repo.SetFieldValue(record, text, "committed-value");
            fixture.Repo.SetFieldValue(record, date, "2026-10-25");
        }
        fixture.Manager.Context.ChangeTracker.Clear();
        long logBefore;
        string calendarBefore;
        using (var before = fixture.OpenContext())
        {
            logBefore = before.ChangeLog.LongCount();
            calendarBefore = CalendarSnapshot(before);
        }
        var ready = directory.File("transaction-ready.json");
        var release = directory.File("transaction-release");
        var target = profile == "postgres" ? new NpgsqlConnectionStringBuilder(fixture.Path).Database! : fixture.Path;
        using var worker = Spawn("--transaction-crash-worker", profile, target, collection.ToString(), text.ToString(),
            date.ToString(), edited.ToString(), deleted.ToString(), ready, release);
        var output = worker.StandardOutput.ReadToEndAsync();
        var errors = worker.StandardError.ReadToEndAsync();
        try
        {
            await WaitForFile(ready, worker);
            using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(ready));
            var created = marker.RootElement.GetProperty("NewRecordId").GetInt32();
            Assert.Equal("child-value", marker.RootElement.GetProperty("EditedValue").GetString());
            Assert.Equal("2026-10-26", marker.RootElement.GetProperty("EditedDate").GetString());
            Assert.False(marker.RootElement.GetProperty("DeletedVisible").GetBoolean());
            Assert.Equal("child-created-value", marker.RootElement.GetProperty("CreatedValue").GetString());
            // Другой клиент до commit не видит ни создание, ни правку, ни удаление, ни журнал.
            using (var observer = fixture.OpenContext())
            {
                Assert.Equal(2, observer.DbRecords.Count(r => r.CollectionId == collection));
                Assert.False(observer.DbRecords.Any(r => r.Id == created));
                Assert.True(observer.DbRecords.Any(r => r.Id == deleted));
                Assert.Equal("committed-value", observer.DbFieldValues.Single(v => v.RecordId == edited && v.FieldId == text).Value);
                Assert.Equal(logBefore, observer.ChangeLog.LongCount());
                Assert.Equal(calendarBefore, CalendarSnapshot(observer));
            }
            if (kill) worker.Kill(entireProcessTree: true); else File.WriteAllText(release, "commit");
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (kill) Assert.NotEqual(0, worker.ExitCode); else Assert.Equal(0, worker.ExitCode);
            using (var check = fixture.OpenContext())
            {
                Assert.Equal(2, check.DbRecords.Count(r => r.CollectionId == collection));
                Assert.Equal(!kill, check.DbRecords.Any(r => r.Id == created));
                Assert.Equal(kill, check.DbRecords.Any(r => r.Id == deleted));
                Assert.Equal(kill ? "committed-value" : "child-value",
                    check.DbFieldValues.Single(v => v.RecordId == edited && v.FieldId == text).Value);
                Assert.Equal(kill ? "2026-10-25" : "2026-10-26",
                    check.DbFieldValues.Single(v => v.RecordId == edited && v.FieldId == date).Value);
                if (kill)
                {
                    Assert.Equal(logBefore, check.ChangeLog.LongCount());
                    Assert.Equal(calendarBefore, CalendarSnapshot(check));
                }
                else
                {
                    Assert.True(check.ChangeLog.LongCount() > logBefore);
                    Assert.Equal("child-created-value", check.DbFieldValues.Single(v => v.RecordId == created && v.FieldId == text).Value);
                    Assert.Equal("2026-10-27", check.DbFieldValues.Single(v => v.RecordId == created && v.FieldId == date).Value);
                    Assert.Contains("COMMITTED", await output);
                }
                MultiUserTestDatabase.AssertHealthy(check);
            }
            // После аварии новый клиент может снова получить write-lock и сохранить команду.
            using (var writer = fixture.OpenContext())
                new ConstructorRepository(writer).InWriteTransaction(() =>
                {
                    var repo = new ConstructorRepository(writer);
                    repo.SetFieldValue(edited, text, "after-restart");
                    return repo.AddRecord(new DbRecord { CollectionId = collection, Title = "Повтор после остановки", Position = 4000 });
                });
            using var final = fixture.OpenContext();
            Assert.Equal(3, final.DbRecords.Count(r => r.CollectionId == collection));
            Assert.Equal("after-restart", final.DbFieldValues.Single(v => v.RecordId == edited && v.FieldId == text).Value);
            MultiUserTestDatabase.AssertHealthy(final);
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Task.WhenAll(output, errors);
        }
    }

    private static string CalendarSnapshot(AppDbContext context) => JsonSerializer.Serialize(context.CalendarIndex.AsNoTracking()
        .OrderBy(i => i.RecordId).ThenBy(i => i.FieldId)
        .Select(i => new { i.RecordId, i.FieldId, i.CollectionId, i.StartIso, i.EndIso, i.AllDay }).ToList());

    private static Process Spawn(params string[] arguments)
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
        Assert.True(File.Exists(executable), "Дочерний стенд должен быть собран ProjectReference");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        start.ArgumentList.Add(executable);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static async Task WaitForFile(string marker, Process worker)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(marker))
        {
            Assert.False(worker.HasExited, "Дочерний процесс завершился до готовности транзакции");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "Дочерний процесс не дошел до барьера");
            await Task.Delay(10);
        }
    }
}
