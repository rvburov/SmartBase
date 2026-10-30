using SmartBase.Database;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Служба приема (<see cref="InboxService"/>) — правила боевой среды, а не сам разбор: разбор
/// проверяется в <c>InboxReceiverTests</c>.
///
/// Проверяется то, из-за чего служба может навредить больше, чем помочь: обход недоступной базы,
/// обход без настроек и молчаливое «новых файлов нет» вместо честной причины. Таймер здесь не
/// заводится — тик проверяется через <c>RunNowAsync</c>, который идет тем же путем.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class InboxServiceTests : IDisposable
{
    private readonly string       _root = Path.Combine(Path.GetTempPath(), $"sbinbox_{Guid.NewGuid():N}");
    private readonly TempDatabase _db   = new();

    public InboxServiceTests() => AppSettings.AttachStore(new FakeSettingsDatabase());

    public void Dispose()
    {

        _db.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* временная папка */ }
    }

    /// <summary>Служба, видящая ровно одну открытую базу — временную.</summary>
    private InboxService Service(bool databaseAvailable = true) =>
        new(() => databaseAvailable ? [_db.Manager] : []);

    private void Configure(string? folder = null, int dbId = 1)
    {
        AppSettings.SaveInboxFolder(folder ?? _root);
        AppSettings.SaveInboxDb(dbId);
    }

    // ── Без настроек ──────────────────────────────────────────────────────────

    /// <summary>
    /// Молчаливое «новых файлов нет» в ответ на ненастроенный прием — ровно то, из-за чего
    /// потом неделю ищут, почему обмен «не работает». Причина называется.
    /// </summary>
    [Fact]
    public async Task Without_a_folder_the_reason_is_named()
    {

        AppSettings.SaveInboxFolder("");
        AppSettings.SaveInboxDb(1);

        var run = await Service().RunNowAsync();

        Assert.Contains("папка обмена не выбрана", run.Problem, StringComparison.Ordinal);
        Assert.Equal(0, run.Files);
    }

    [Fact]
    public async Task Without_a_database_the_reason_is_named()
    {

        AppSettings.SaveInboxFolder(_root);
        AppSettings.SaveInboxDb(0);

        var run = await Service().RunNowAsync();

        Assert.Contains("база данных", run.Problem, StringComparison.Ordinal);
    }

    // ── База недоступна — не запускаемся ──────────────────────────────────────

    /// <summary>
    /// Потерянную базу монитор доступности размонтирует, и она пропадает из списка открытых.
    /// Ходить в папку, не имея куда положить, незачем: файлы только зря уехали бы в «Ошибки».
    /// </summary>
    [Fact]
    public async Task Unavailable_database_stops_the_sweep()
    {

        Configure();

        var layout = InboxFolder.Open(_root, out _)!;
        var path   = Path.Combine(layout.Incoming, "заказы.json");
        File.WriteAllText(path, """{"collection":"Заказы","mode":"add","id":"m1","rows":[{"а":1}]}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(1));

        var run = await Service(databaseAvailable: false).RunNowAsync();

        Assert.Contains("недоступна", run.Problem, StringComparison.Ordinal);
        Assert.Equal(0, run.Files);

        // Файл не тронут: ни захвата, ни переноса в «Ошибки».
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(layout.Failed));
    }

    // ── Обычный обход ─────────────────────────────────────────────────────────

    /// <summary>Кнопка «Проверить папку» ведет ту же службу, что ходит по расписанию.</summary>
    [Fact]
    public async Task Manual_sweep_processes_the_folder()
    {

        Configure();

        var collection = SampleData.AddCollection(_db.Constructor, "Заказы");
        SampleData.AddField(_db.Constructor, collection.Id!.Value, "Номер");

        var layout = InboxFolder.Open(_root, out _)!;
        var path   = Path.Combine(layout.Incoming, "заказы.json");
        File.WriteAllText(path, """
            {"collection":"Заказы","mode":"add","id":"m1",
             "rows":[{"Название":"Молоток","Номер":"А-1"}]}
            """);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(1));

        var run = await Service().RunNowAsync();

        Assert.Equal("", run.Problem);
        Assert.Equal(1, run.Accepted);
        Assert.Single(_db.Constructor.GetRecords(collection.Id!.Value));
    }

    /// <summary>Пустая папка — не повод для тревоги, и в итоге это сказано словами.</summary>
    [Fact]
    public async Task Empty_folder_is_not_a_problem()
    {

        Configure();
        InboxFolder.Open(_root, out _);

        var run = await Service().RunNowAsync();

        Assert.Equal("", run.Problem);
        Assert.Equal("новых файлов нет", run.Summary());
    }
}
