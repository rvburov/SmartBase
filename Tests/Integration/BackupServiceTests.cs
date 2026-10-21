using SmartBase.Database;
using SmartBase.Database.Backup;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Служба резервного копирования на настоящих файлах: ручной запуск снимает валидную
/// копию (открывается, данные на месте) и двигает отметку lastRun; плановый тик сперва
/// ставит якорь, а копию делает только когда расписание наступило; ротация убирает
/// старые копии ТОЛЬКО своей базы; сбой (пропавший файл-источник) не двигает отметку
/// и честно сообщает об ошибке.
/// </summary>
public sealed class BackupServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Default_database_backup_follows_moved_data_directory(bool oldFileStillExists)
    {
        using var dir = new TempDir();
        var legacyDir = dir.File("DataBase");
        var currentDir = dir.File("SmartBase");
        var legacyPath = Path.Combine(legacyDir, "database.db");
        var currentPath = Path.Combine(currentDir, "database.db");
        var extraPath = dir.File("extra.db");
        Directory.CreateDirectory(legacyDir);

        int id, collectionId, extraId;
        using (var registry = DatabaseRegistry.Open(legacyPath, Path.Combine(legacyDir, "app.db")))
        {
            AppSettings.AttachStore(registry);
            id = registry.GetAll().Single().Id;
            registry.Rename(id, "Моя основная");
            extraId = registry.Add("Другая", extraPath).Id;
            AppSettings.SaveBackupEnabled(id, true);
            AppSettings.SaveBackupSchedule(id, "weekly mon,thu 03:00");

            using var database = DatabaseManager.Open(id, "Моя основная", legacyPath);
            var collection = SampleData.AddCollection(database.Constructor);
            collectionId = collection.Id!.Value;
            SampleData.AddRecords(database.Constructor, collectionId, count: 7, step: 10);
        }

        // Переносится вся папка, включая app.db со старым абсолютным путем основной базы.
        SqliteGate.ClearAllPools();
        Directory.Move(legacyDir, currentDir);

        using (var registry = DatabaseRegistry.Open(currentPath, Path.Combine(currentDir, "app.db")))
        {
            AppSettings.AttachStore(registry);
            AppSettings.SaveBackupFolder(dir.File("copies"));

            // Даже если по старому адресу осталась другая база, копировать нужно актуальную.
            if (oldFileStillExists)
            {
                Directory.CreateDirectory(legacyDir);
                using var oldDatabase = DatabaseManager.Open(91, "Старый файл", legacyPath);
            }

            var finished = new List<(bool Ok, string Message)>();
            using var service = new BackupService(registry);
            service.BackupFinished += (_, ok, msg) => finished.Add((ok, msg));
            await service.RunNowAsync(id);

            Assert.Equal([(true, "")], finished);
            var copy = Assert.Single(Directory.GetFiles(dir.File("copies")));
            try
            {
                using var copyDb = DatabaseManager.Open(90, "copy", copy);
                Assert.Equal(7, copyDb.Constructor.CountRecords(collectionId));
            }
            finally { TempDatabase.Cleanup(copy); }

            var main = registry.GetAll().Single(d => d.IsDefault);
            Assert.Equal(id, main.Id);
            Assert.Equal("Моя основная", main.Name);
            Assert.Equal(currentPath, main.Path);
            Assert.Equal(extraPath, registry.GetAll().Single(d => d.Id == extraId).Path);
            Assert.True(AppSettings.LoadBackupEnabled(id));
            Assert.Equal("weekly mon,thu 03:00", AppSettings.LoadBackupSchedule(id));
            Assert.Equal(oldFileStillExists, File.Exists(legacyPath));
        }

        // Путь записан в app.db, а не подменен только в памяти открытого реестра.
        using (var stored = new SystemDbContext(Path.Combine(currentDir, "app.db")))
            Assert.Equal(currentPath, stored.Databases.Single(d => d.IsDefault).Path);

        // Повторный запуск не создает новую запись основной базы.
        using (var again = DatabaseRegistry.Open(currentPath, Path.Combine(currentDir, "app.db")))
        {
            Assert.Equal(2, again.GetAll().Count);
            Assert.Equal(id, again.GetAll().Single(d => d.IsDefault).Id);
            Assert.Equal(currentPath, again.GetAll().Single(d => d.IsDefault).Path);
        }
        SqliteGate.ClearAllPools();
    }

    [Fact]
    public async Task Run_now_creates_valid_copy_and_updates_last_run()
    {
        using var db  = new TempDatabase();
        var col = SampleData.AddCollection(db.Constructor);
        SampleData.AddRecords(db.Constructor, col.Id!.Value, count: 25, step: 10);

        using var dir      = new TempDir();
        using var registry = DatabaseRegistry.Open(db.Path, dir.File("app.db"));
        AppSettings.AttachStore(registry);
        AppSettings.SaveBackupFolder(dir.File("copies"));

        var entry    = registry.GetAll().Single();
        var finished = new List<(bool Ok, string Message)>();
        using var service  = new BackupService(registry);
        service.BackupFinished += (_, ok, msg) => finished.Add((ok, msg));

        await service.RunNowAsync(entry.Id);

        Assert.Equal([(true, "")], finished);
        Assert.NotNull(AppSettings.LoadBackupLastRun(entry.Id));
        Assert.NotNull(AppSettings.LoadBackupRunState(entry.Id).LastSuccess);

        var copy = Assert.Single(Directory.GetFiles(dir.File("copies")));
        Assert.True(BackupRotation.IsCopyOf(Path.GetFileName(copy), entry.Name, "db"));

        // Копия целая и с теми же данными — открываем как обычную базу.
        try
        {
            using var copyDb = DatabaseManager.Open(90, "copy", copy);
            Assert.Equal(25, copyDb.Constructor.CountRecords(col.Id!.Value));
        }
        finally { TempDatabase.Cleanup(copy); }
    }

    [Fact]
    public async Task Scheduled_tick_sets_anchor_first_then_backs_up_when_due()
    {
        using var db       = new TempDatabase();
        using var dir      = new TempDir();
        using var registry = DatabaseRegistry.Open(db.Path, dir.File("app.db"));
        AppSettings.AttachStore(registry);
        AppSettings.SaveBackupFolder(dir.File("copies"));

        var id = registry.GetAll().Single().Id;
        AppSettings.SaveBackupEnabled(id, true);

        using var service = new BackupService(registry);

        // Первый тик после включения — только якорь «сейчас», копия не снимается.
        await service.TickNowAsync();
        Assert.NotNull(AppSettings.LoadBackupLastRun(id));
        Assert.Null(AppSettings.LoadBackupRunState(id).LastSuccess);
        Assert.False(Directory.Exists(dir.File("copies")));

        // Отметка позавчера → расписание (daily 03:00) давно наступило: тик догоняет копию.
        AppSettings.SaveBackupLastRun(id, DateTime.Now.AddDays(-2));
        await service.TickNowAsync();
        Assert.Single(Directory.GetFiles(dir.File("copies")));

        // Отметка сдвинута на момент копии — повторный тик копию не дублирует.
        await service.TickNowAsync();
        Assert.Single(Directory.GetFiles(dir.File("copies")));
    }

    [Fact]
    public async Task Disabled_database_is_not_backed_up_by_tick()
    {
        using var db       = new TempDatabase();
        using var dir      = new TempDir();
        using var registry = DatabaseRegistry.Open(db.Path, dir.File("app.db"));
        AppSettings.AttachStore(registry);
        AppSettings.SaveBackupFolder(dir.File("copies"));
        AppSettings.SaveBackupLastRun(registry.GetAll().Single().Id, DateTime.Now.AddDays(-2));

        using var service = new BackupService(registry);
        await service.TickNowAsync();

        Assert.False(Directory.Exists(dir.File("copies")));
    }

    [Fact]
    public async Task Rotation_removes_old_copies_of_this_database_only()
    {
        using var db       = new TempDatabase();
        using var dir      = new TempDir();
        using var registry = DatabaseRegistry.Open(db.Path, dir.File("app.db"));
        AppSettings.AttachStore(registry);

        var folder = dir.File("copies");
        Directory.CreateDirectory(folder);
        AppSettings.SaveBackupFolder(folder);
        AppSettings.SaveBackupKeep(AppSettings.BackupKeepMin);   // хранить одну

        // Старые копии этой базы («Основная» — имя дефолтной записи реестра), копия
        // соседней базы и посторонний файл.
        File.WriteAllText(Path.Combine(folder, "Основная (копия 2020-01-01_03-00).db"), "x");
        File.WriteAllText(Path.Combine(folder, "Основная (копия 2020-01-02_03-00).db"), "x");
        File.WriteAllText(Path.Combine(folder, "Соседняя (копия 2020-01-01_03-00).db"), "x");
        File.WriteAllText(Path.Combine(folder, "заметки.txt"), "x");

        using var service = new BackupService(registry);
        await service.RunNowAsync(registry.GetAll().Single().Id);

        var names = Directory.GetFiles(folder).Select(Path.GetFileName).ToList();
        Assert.DoesNotContain("Основная (копия 2020-01-01_03-00).db", names);
        Assert.DoesNotContain("Основная (копия 2020-01-02_03-00).db", names);
        Assert.Contains("Соседняя (копия 2020-01-01_03-00).db", names);
        Assert.Contains("заметки.txt", names);
        // Из копий «Основной» осталась ровно одна — свежая.
        Assert.Single(names, n => BackupRotation.IsCopyOf(n!, "Основная", "db"));
    }

    [Fact]
    public async Task Stale_unfinished_copies_of_this_database_are_removed()
    {
        using var db       = new TempDatabase();
        using var dir      = new TempDir();
        using var registry = DatabaseRegistry.Open(db.Path, dir.File("app.db"));
        AppSettings.AttachStore(registry);

        var folder = dir.File("copies");
        Directory.CreateDirectory(folder);
        AppSettings.SaveBackupFolder(folder);

        // Брошенные оборванным запуском недописанные копии: своей базы и соседней.
        File.WriteAllText(Path.Combine(folder, "Основная (копия 2020-01-01_03-00).db.part"), "x");
        File.WriteAllText(Path.Combine(folder, "Соседняя (копия 2020-01-01_03-00).db.part"), "x");

        using var service = new BackupService(registry);
        await service.RunNowAsync(registry.GetAll().Single().Id);

        var names = Directory.GetFiles(folder).Select(Path.GetFileName).ToList();
        Assert.DoesNotContain("Основная (копия 2020-01-01_03-00).db.part", names);
        Assert.Contains("Соседняя (копия 2020-01-01_03-00).db.part", names);
        Assert.Single(names, n => BackupRotation.IsCopyOf(n!, "Основная", "db"));
    }

    [Fact]
    public async Task Missing_source_file_reports_error_and_keeps_no_last_run()
    {
        using var dir      = new TempDir();
        using var registry = DatabaseRegistry.Open(dir.File("нет-такого-файла.db"), dir.File("app.db"));
        AppSettings.AttachStore(registry);
        AppSettings.SaveBackupFolder(dir.File("copies"));

        var id       = registry.GetAll().Single().Id;
        var finished = new List<(bool Ok, string Message)>();
        using var service  = new BackupService(registry);
        service.BackupFinished += (_, ok, msg) => finished.Add((ok, msg));

        await service.RunNowAsync(id);

        var (ok, message) = Assert.Single(finished);
        Assert.False(ok);
        Assert.Contains("не найден", message);
        Assert.Null(AppSettings.LoadBackupLastRun(id));
        Assert.Empty(Directory.GetFiles(dir.File("copies")));   // папка создана, но копии нет
    }

    [Fact]
    public async Task Background_job_keeps_failure_then_shows_retry_progress_with_its_icon_and_finishes()
    {
        using var dir = new TempDir();
        var path = dir.File("source.db");
        using var registry = DatabaseRegistry.Open(path, dir.File("app.db"));
        AppSettings.AttachStore(registry);
        AppSettings.SaveBackupFolder(dir.File("copies"));
        var id = registry.GetAll().Single().Id;
        using var service = new BackupService(registry);
        await service.RunNowAsync(id);
        var failed = Assert.Single(SmartBase.Core.BackgroundJobs.All.Where(j => j.Id == service.BackgroundJobId(id)));
        Assert.True(failed.Failed);
        Assert.Equal("IconArchive", failed.Icon);
        Assert.Null(AppSettings.LoadBackupRunState(id).LastSuccess);

        using var source = DatabaseManager.Open(id, "source", path);
        var running = false;
        var verifying = false;
        service.BackupProgress += (_, phase, _) =>
        {
            var job = SmartBase.Core.BackgroundJobs.All.Single(j => j.Id == service.BackgroundJobId(id));
            running |= !job.Failed && job.Icon == "IconArchive" && job.Detail.Contains("Основная");
            verifying |= phase.StartsWith("Проверяю копию", StringComparison.Ordinal) && job.Detail.Contains("Проверяю копию");
        };
        await service.RunNowAsync(id);
        Assert.True(running);
        Assert.True(verifying);
        Assert.DoesNotContain(SmartBase.Core.BackgroundJobs.All, j => j.Id == service.BackgroundJobId(id));
        Assert.NotNull(AppSettings.LoadBackupRunState(id).LastSuccess);
    }
}
