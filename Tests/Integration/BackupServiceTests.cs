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
        var service  = new BackupService(registry);
        service.BackupFinished += (_, ok, msg) => finished.Add((ok, msg));

        await service.RunNowAsync(entry.Id);

        Assert.Equal([(true, "")], finished);
        Assert.NotNull(AppSettings.LoadBackupLastRun(entry.Id));

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

        var service = new BackupService(registry);

        // Первый тик после включения — только якорь «сейчас», копия не снимается.
        await service.TickNowAsync();
        Assert.NotNull(AppSettings.LoadBackupLastRun(id));
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

        var service = new BackupService(registry);
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

        var service = new BackupService(registry);
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
    public async Task Missing_source_file_reports_error_and_keeps_no_last_run()
    {
        using var dir      = new TempDir();
        using var registry = DatabaseRegistry.Open(dir.File("нет-такого-файла.db"), dir.File("app.db"));
        AppSettings.AttachStore(registry);
        AppSettings.SaveBackupFolder(dir.File("copies"));

        var id       = registry.GetAll().Single().Id;
        var finished = new List<(bool Ok, string Message)>();
        var service  = new BackupService(registry);
        service.BackupFinished += (_, ok, msg) => finished.Add((ok, msg));

        await service.RunNowAsync(id);

        var (ok, message) = Assert.Single(finished);
        Assert.False(ok);
        Assert.Contains("не найден", message);
        Assert.Null(AppSettings.LoadBackupLastRun(id));
        Assert.Empty(Directory.GetFiles(dir.File("copies")));   // папка создана, но копии нет
    }
}
