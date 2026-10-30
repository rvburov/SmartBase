using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core;
using SmartBase.Database;
using SmartBase.Database.Backup;
using SmartBase.Logging;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>Реальный SQLite с большим freelist: свободные страницы не уменьшают
/// обязательный запас в папке назначения до начала копирования.</summary>
public sealed class BackupSpacePreflightTests
{
    private static readonly DateTime PreviousSuccess = new(2026, 1, 1, 3, 0, 0);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_and_scheduled_runs_reject_a_bloated_source_before_copying_or_rotation(bool scheduled)
    {
        using var db = new TempDatabase();
        MakeBloated(db, megabytes: 216);
        using var dir = new TempDir();
        using var registry = DatabaseRegistry.Open(db.Path, dir.File("app.db"));
        AppSettings.AttachStore(registry);
        var entry = registry.GetAll().Single();
        var folder = dir.File("copies");
        Directory.CreateDirectory(folder);
        AppSettings.SaveBackupFolder(folder);
        AppSettings.SaveBackupKeep(1);
        var previous = Path.Combine(folder, BackupRotation.CopyFileName(entry.Name, PreviousSuccess, "db"));
        DatabaseManager.BackupTo(db.Path, entry.Name, previous, null);
        var previousHash = Hash(previous);
        var leftover = Path.Combine(folder,
            BackupRotation.CopyFileName(entry.Name, PreviousSuccess.AddDays(1), "db")) + DatabaseManager.BackupPartSuffix;
        var leftoverBytes = "unfinished-earlier-attempt"u8.ToArray();
        File.WriteAllBytes(leftover, leftoverBytes);
        AppSettings.SaveBackupLastRun(entry.Id, PreviousSuccess);
        AppSettings.SaveBackupRunState(entry.Id,
            new(PreviousSuccess, PreviousSuccess, BackupRunOutcome.Succeeded));
        AppSettings.SaveBackupEnabled(entry.Id, scheduled);
        AppSettings.SaveBackupSchedule(entry.Id, "daily 00:00");

        // Сохраняем пропорцию пользовательского случая 15 ГБ свободно / 27 ГБ файл,
        // не создавая многогигабайтную базу в тестовой среде.
        var sourceBytes = new FileInfo(db.Path).Length;
        var available = sourceBytes * 15 / 27;
        Assert.True(BackupSpace.RequiredBytes(LiveBytes(db)) < available,
            "Фикстура должна проходить прежнюю оценку по живым страницам");
        Assert.True(BackupSpace.RequiredBytes(sourceBytes) > available);
        var sourceHash = Hash(db.Path);
        var phases = new List<string>();
        var results = new List<(bool Ok, string Message)>();
        var checkedFolders = new List<string>();
        using var service = new BackupService(registry);
        service.BackupProgress += (_, phase, _) => phases.Add(phase);
        service.BackupFinished += (_, ok, message) => results.Add((ok, message));
        var previousProbe = BackupSpace.AvailableBytesOverride.Value;
        BackupSpace.AvailableBytesOverride.Value = path =>
        {
            checkedFolders.Add(path);
            return path == Path.GetFullPath(folder) ? available : long.MaxValue;
        };
        try
        {
            if (scheduled) await service.TickNowAsync();
            else await service.RunNowAsync(entry.Id);
        }
        finally { BackupSpace.AvailableBytesOverride.Value = previousProbe; }

        var result = Assert.Single(results);
        Assert.False(result.Ok);
        Assert.Contains("Недостаточно", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFullPath(folder), result.Message);
        Assert.Contains(Diag.Size(BackupSpace.RequiredBytes(sourceBytes)), result.Message);
        Assert.Contains(Diag.Size(available), result.Message);
        Assert.Contains("Нужно", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("свободно", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFullPath(folder), checkedFolders);
        Assert.DoesNotContain(phases, IsCopyPhase);
        var blocked = Assert.Single(BackgroundJobs.All, job => job.Id == service.BackgroundJobId(entry.Id));
        Assert.True(blocked.Failed);
        Assert.Contains(result.Message, blocked.Detail);
        Assert.Null(blocked.Stop);
        Assert.Equal(0, blocked.Total);
        Assert.Equal(PreviousSuccess, AppSettings.LoadBackupLastRun(entry.Id));
        var state = AppSettings.LoadBackupRunState(entry.Id);
        Assert.Equal(BackupRunOutcome.Failed, state.Outcome);
        Assert.Equal(PreviousSuccess, state.LastSuccess);
        Assert.Equal(new[] { previous, leftover }.Order(StringComparer.Ordinal),
            Directory.GetFiles(folder).Order(StringComparer.Ordinal));
        Assert.Equal(previousHash, Hash(previous));
        Assert.Equal(leftoverBytes, File.ReadAllBytes(leftover));
        Assert.Equal(sourceHash, Hash(db.Path));
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM backup_preflight_payload;"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void All_volume_paths_require_the_whole_source_before_touching_a_previous_part(bool networkSource, bool networkTarget)
    {
        using var db = new TempDatabase();
        MakeBloated(db, megabytes: 96);
        using var dir = new TempDir();
        var target = dir.File("copy.db");
        var part = target + DatabaseManager.BackupPartSuffix;
        var prior = "prior-part-must-not-be-removed-by-a-refused-attempt"u8.ToArray();
        File.WriteAllBytes(part, prior);
        var sourceBytes = new FileInfo(db.Path).Length;
        var available = 80L << 20;
        Assert.True(BackupSpace.RequiredBytes(LiveBytes(db)) < available);
        Assert.True(BackupSpace.RequiredBytes(sourceBytes) > available);
        var original = Hash(db.Path);
        var phases = new List<string>();
        var previousProbe = BackupSpace.AvailableBytesOverride.Value;
        BackupSpace.AvailableBytesOverride.Value = folder =>
            folder == Path.GetFullPath(dir.Path) ? available : long.MaxValue;
        if (networkSource) NetworkPath.ForceNetwork(db.Path);
        if (networkTarget) NetworkPath.ForceNetwork(target);
        try
        {
            var error = Assert.Throws<IOException>(() =>
                DatabaseManager.BackupTo(db.Path, "source", target, (phase, _) => phases.Add(phase)));
            Assert.Contains(Path.GetFullPath(dir.Path), error.Message);
            Assert.Contains(Diag.Size(BackupSpace.RequiredBytes(sourceBytes)), error.Message);
        }
        finally
        {
            BackupSpace.AvailableBytesOverride.Value = previousProbe;
            NetworkPath.Forget(db.Path);
            NetworkPath.Forget(target);
        }
        Assert.False(File.Exists(target));
        Assert.Equal(prior, File.ReadAllBytes(part));
        Assert.DoesNotContain(phases, IsCopyPhase);
        Assert.Equal(original, Hash(db.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Zero_or_unknown_capacity_does_not_delete_a_previous_part(bool unknown)
    {
        using var db = new TempDatabase();
        db.Context.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");
        using var dir = new TempDir();
        var target = dir.File("copy.db");
        var part = target + DatabaseManager.BackupPartSuffix;
        var prior = "existing-part"u8.ToArray();
        File.WriteAllBytes(part, prior);
        var sourceHash = Hash(db.Path);
        var previousProbe = BackupSpace.AvailableBytesOverride.Value;
        BackupSpace.AvailableBytesOverride.Value = _ => unknown ? null : 0;
        try
        {
            var error = Assert.Throws<IOException>(() => DatabaseManager.BackupTo(db.Path, "source", target, null));
            Assert.Contains(Path.GetFullPath(dir.Path), error.Message);
        }
        finally { BackupSpace.AvailableBytesOverride.Value = previousProbe; }
        Assert.False(File.Exists(target));
        Assert.Equal(prior, File.ReadAllBytes(part));
        Assert.Equal(sourceHash, Hash(db.Path));
    }

    [Fact]
    public void Uncheckpointed_wal_pages_are_counted_when_the_main_file_itself_would_fit()
    {
        using var db = new TempDatabase();
        db.Context.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");
        using var keeper = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = db.Path, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        keeper.Open();
        using var snapshot = keeper.BeginTransaction(deferred: true);
        using (var read = keeper.CreateCommand())
        {
            read.Transaction = snapshot;
            read.CommandText = "SELECT COUNT(*) FROM sqlite_schema;";
            read.ExecuteScalar();
        }
        // Читатель держит старый снимок: файл остается маленьким, новые и освобожденные
        // страницы находятся в WAL, но входят в логический page_count свежего снимка.
        MakeBloated(db, megabytes: 96, checkpoint: false);
        var mainBytes = new FileInfo(db.Path).Length;
        var logicalBytes = Scalar(db, "SELECT page_count * page_size FROM pragma_page_count, pragma_page_size;");
        var available = 80L << 20;
        Assert.True(BackupSpace.RequiredBytes(mainBytes) < available);
        Assert.True(BackupSpace.RequiredBytes(LiveBytes(db)) < available);
        Assert.True(BackupSpace.RequiredBytes(logicalBytes) > available);
        var wal = db.Path + "-wal";
        Assert.True(new FileInfo(wal).Length > 0);
        var mainHash = Hash(db.Path);
        var walHash = Hash(wal);
        using var dir = new TempDir();
        var target = dir.File("copy.db");
        var part = target + DatabaseManager.BackupPartSuffix;
        var prior = "unfinished-before-wal-preflight"u8.ToArray();
        File.WriteAllBytes(part, prior);
        var phases = new List<string>();
        var previousProbe = BackupSpace.AvailableBytesOverride.Value;
        BackupSpace.AvailableBytesOverride.Value = _ => available;
        try
        {
            var error = Assert.Throws<IOException>(() =>
                DatabaseManager.BackupTo(db.Path, "source", target, (phase, _) => phases.Add(phase)));
            Assert.Contains(Diag.Size(BackupSpace.RequiredBytes(logicalBytes)), error.Message);
            Assert.Contains(Path.GetFullPath(dir.Path), error.Message);
        }
        finally { BackupSpace.AvailableBytesOverride.Value = previousProbe; }
        Assert.False(File.Exists(target));
        Assert.Equal(prior, File.ReadAllBytes(part));
        Assert.DoesNotContain(phases, IsCopyPhase);
        Assert.Equal(mainHash, Hash(db.Path));
        Assert.Equal(walHash, Hash(wal));
    }

    [Fact]
    public void Only_the_destination_volume_capacity_is_used_for_the_final_copy()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var folder = Path.GetFullPath(dir.Path);
        var target = dir.File("copy.db");
        var checkedFolders = new List<string>();
        var previousProbe = BackupSpace.AvailableBytesOverride.Value;
        BackupSpace.AvailableBytesOverride.Value = path =>
        {
            checkedFolders.Add(path);
            // Имитация разных томов: у источника места нет, у выбранной папки копий есть.
            return path == folder ? 1L << 40 : 0;
        };
        try { DatabaseManager.BackupTo(db.Path, "source", target, null); }
        finally { BackupSpace.AvailableBytesOverride.Value = previousProbe; }
        Assert.True(File.Exists(target));
        Assert.NotEmpty(checkedFolders);
        Assert.All(checkedFolders, path => Assert.Equal(folder, path));
        Assert.Null(DatabaseManager.VerifyCopy(target, "copy", null));
    }

    private static void MakeBloated(TempDatabase db, int megabytes, bool checkpoint = true)
    {
        // Фиксируем размер исходника для проверки сообщения. Фоновый PASSIVE мог
        // пересечься с TRUNCATE и продолжить рост файла уже после снятия его длины.
        WalCheckpointer.Unwatch(db.Path);
        db.Context.Database.ExecuteSqlRaw("CREATE TABLE backup_preflight_payload (value BLOB NOT NULL);");
        db.Context.Database.ExecuteSqlRaw(
            "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x < {0}) " +
            "INSERT INTO backup_preflight_payload SELECT zeroblob(1048576) FROM n;", megabytes);
        db.Context.Database.ExecuteSqlRaw("DELETE FROM backup_preflight_payload WHERE rowid > 1;");
        if (checkpoint)
        {
            Assert.Equal(0L, Scalar(db, "PRAGMA wal_checkpoint(TRUNCATE);"));
            Assert.Equal(Scalar(db, "SELECT page_count * page_size FROM pragma_page_count, pragma_page_size;"),
                new FileInfo(db.Path).Length);
        }
        Assert.True(Scalar(db, "PRAGMA freelist_count;") > 0, "В фикстуре нет освобожденных страниц");
    }

    private static long LiveBytes(TempDatabase db) => Scalar(db,
        "SELECT (page_count - freelist_count) * page_size " +
        "FROM pragma_page_count, pragma_freelist_count, pragma_page_size;");

    private static long Scalar(TempDatabase db, string sql)
    {
        var connection = db.Context.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        finally { if (opened) connection.Close(); }
    }

    private static string Hash(string path)
    {
        // SQLite может держать RW-дескриптор файла и WAL даже между запросами.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsCopyPhase(string phase) =>
        phase.StartsWith("Копирую", StringComparison.Ordinal)
        || phase.StartsWith("Читаю", StringComparison.Ordinal)
        || phase.StartsWith("Сохраняю", StringComparison.Ordinal);
}
