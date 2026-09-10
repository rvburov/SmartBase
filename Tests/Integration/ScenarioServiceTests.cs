using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Служба сценариев (<see cref="ScenarioService"/>) — расписание, а не выполнение: само
/// выполнение проверяется в <c>ScenarioRunnerTests</c>.
///
/// Проверяется то, из-за чего служба может навредить: запуск не вовремя, потерянный догон и
/// журнал, забитый одинаковыми строками каждые полминуты. Таймер здесь не заводится — тик
/// вызывается напрямую с явным моментом, тем же приемом, что у планировщика копий.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class ScenarioServiceTests : IDisposable
{
    private readonly TempDatabase     _db = new();
    private readonly string           _dir;
    private readonly string           _appDb;
    private readonly string           _otherDb;
    private readonly DatabaseRegistry _registry;
    private readonly RunJournal       _journal;

    private readonly List<string> _notices = [];

    public ScenarioServiceTests()
    {
        _dir      = Directory.CreateTempSubdirectory("sbsvc_").FullName;
        _appDb    = Path.Combine(_dir, "app.db");
        _otherDb  = Path.Combine(_dir, "database.db");
        _registry = DatabaseRegistry.Open(_otherDb, _appDb);
        _journal  = new RunJournal(Path.Combine(_dir, "integrations.db"));

        RunJournal.Attach(_journal);

        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal, ["127.0.0.1"], ["127.0.0.0/8"], [], 1000, 1024 * 1024, 100,
            null, false, "тест")));
    }

    public void Dispose()
    {
        EgressGate.Close();
        RunJournal.Attach(null);
        _registry.Dispose();
        _db.Dispose();
        TempDatabase.Cleanup(_appDb);
        TempDatabase.Cleanup(_otherDb);
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    private IntegrationsRegistry Cards => _registry.Integrations;

    private ScenarioService Service() =>
        new(Cards, () => [_db.Manager], [], _notices.Add);

    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Local));

    /// <summary>Коллекция, запрос на тестовый сервер и привязка к ней — все, что нужно шагу.</summary>
    private int ReadyRequest(TinyServer server, bool withBinding = true)
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Заказы");
        var number     = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Номер");

        var connection = Cards.Save(IntegrationConnection.New() with
        {
            Name           = "Учетная система",
            Address        = $"http://127.0.0.1:{server.Port}",
            // Запас времени намеренно большой: на загруженной машине полный прогон отнимает
            // потоки у тестового сервера, а проверяем мы не скорость петли.
            TimeoutSeconds = 30,
            Retries        = 0,
        });

        var requestId = Cards.SaveRequest(IntegrationRequest.New() with
        {
            Name         = "Заказы",
            ConnectionId = connection.Id,
            Path         = "/orders",
        }).Id;

        if (withBinding)
            Cards.SaveBinding(requestId, new CollectionBinding(_db.Manager.DbId, collection.Id!.Value,
            [
                new BindingColumn("Название", null),
                new BindingColumn("Номер",    number.Id!.Value),
            ]));

        return requestId;
    }

    /// <summary>Сценарий с одним шагом-запросом; якорь ставится указанным моментом.</summary>
    private Scenario Ready(int requestId, ScenarioTrigger trigger, DateTimeOffset? anchor = null,
                           bool enabled = true)
    {
        var scenario = Cards.SaveScenario(Scenario.New() with
        {
            Name    = "Заказы по расписанию",
            Enabled = enabled,
            Trigger = trigger,
            Steps   = [ScenarioStep.OfRequest(requestId)],
        });

        if (anchor is { } moment) Cards.EnableScenario(scenario.Id, enabled, moment);

        return Cards.FindScenario(scenario.Id)!;
    }

    private static TinyServer Server() =>
        new(_ => TinyServer.Reply(200, """[{"Название":"Молоток","Номер":"А-1"}]"""));

    // ── Кому пора ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Срок наступил — сценарий идет, и отметка встает моментом НАЧАЛА: иначе долгий обмен
    /// растягивал бы период, а падение посреди работы повторяло бы запуск немедленно.
    /// </summary>
    [Fact]
    public async Task A_due_scenario_runs_and_the_mark_moves_to_the_start()
    {
        using var server = Server();
        var scenario = Ready(ReadyRequest(server), ScenarioTrigger.EveryMinutes(30),
                             anchor: Now.AddMinutes(-40));

        var ran = await Service().TickAsync(Now);

        Assert.Equal(1, ran);
        Assert.Equal(Now.ToUniversalTime(),
                     Cards.ScenarioLastRun(scenario.Id)!.Value.ToUniversalTime());
        Assert.Single(_db.Constructor.GetRecords(
            _db.Constructor.GetAllCollections().Single(c => c.Name == "Заказы").Id!.Value));
    }

    [Fact]
    public async Task A_scenario_that_is_not_due_is_left_alone()
    {
        using var server = Server();
        Ready(ReadyRequest(server), ScenarioTrigger.EveryMinutes(30), anchor: Now.AddMinutes(-10));

        Assert.Equal(0, await Service().TickAsync(Now));
        Assert.Empty(server.Seen);
    }

    /// <summary>Выключенный сценарий расписание не трогает — в этом весь смысл тумблера.</summary>
    [Fact]
    public async Task A_disabled_scenario_never_runs()
    {
        using var server = Server();
        Ready(ReadyRequest(server), ScenarioTrigger.EveryMinutes(30),
              anchor: Now.AddMinutes(-40), enabled: false);

        Assert.Equal(0, await Service().TickAsync(Now));
        Assert.Empty(server.Seen);
    }

    /// <summary>
    /// «Еще ни разу» — не повод срываться с места: якорь ставится при включении, а без него
    /// сценарий «ежедневно в 07:00», заведенный вечером, пошел бы немедленно.
    /// </summary>
    [Fact]
    public async Task Without_an_anchor_nothing_runs()
    {
        using var server = Server();
        Ready(ReadyRequest(server), ScenarioTrigger.DailyAt(new TimeOnly(7, 0)));   // без якоря

        Assert.Equal(0, await Service().TickAsync(Now));
        Assert.Empty(server.Seen);
    }

    // ── Что мешает ────────────────────────────────────────────────────────────

    /// <summary>
    /// Помеха — не событие: тик пропускается молча, отметка не двигается, и, когда база
    /// вернется, пропущенное догонится. Иначе журнал за ночь заполнился бы одинаковыми
    /// строками каждые полминуты.
    /// </summary>
    [Fact]
    public async Task A_blocked_scenario_is_skipped_without_a_mark_and_without_a_journal_line()
    {
        using var server = Server();
        var anchor   = Now.AddMinutes(-40);
        var scenario = Ready(ReadyRequest(server, withBinding: false),
                             ScenarioTrigger.EveryMinutes(30), anchor);

        Assert.Equal(0, await Service().TickAsync(Now));

        Assert.Equal(anchor.ToUniversalTime(),
                     Cards.ScenarioLastRun(scenario.Id)!.Value.ToUniversalTime());
        Assert.Empty(_journal.Recent());
        Assert.Empty(server.Seen);
    }

    /// <summary>
    /// Помеха ушла — пропущенное догоняется тем же тиком: отметка все это время стояла на
    /// месте, и срок так и остался наступившим.
    /// </summary>
    [Fact]
    public async Task The_missed_run_happens_once_the_obstacle_is_gone()
    {
        using var server = Server();
        var requestId = ReadyRequest(server, withBinding: false);
        var scenario  = Ready(requestId, ScenarioTrigger.EveryMinutes(30), Now.AddMinutes(-40));
        var service   = Service();

        Assert.Equal(0, await service.TickAsync(Now));

        // Человек разложил колонки кнопкой «В коллекцию» — привязка появилась.
        var collection = _db.Constructor.GetAllCollections().Single(c => c.Name == "Заказы");
        Cards.SaveBinding(requestId, new CollectionBinding(_db.Manager.DbId, collection.Id!.Value,
            [new BindingColumn("Название", null)]));

        Assert.Equal(1, await service.TickAsync(Now.AddMinutes(1)));
        Assert.NotNull(Cards.ScenarioLastRun(scenario.Id));
    }

    // ── Запуск руками ─────────────────────────────────────────────────────────

    /// <summary>
    /// Нажатие получает ответ, даже когда сценарий не может работать: молчаливое бездействие
    /// хуже отказа. Расписание при этом не двигается — оно принадлежит сценарию, а не тому, кто
    /// проверил настройку в обед.
    /// </summary>
    [Fact]
    public async Task Running_by_hand_explains_and_does_not_move_the_schedule()
    {
        using var server = Server();
        var anchor   = Now.AddMinutes(-10);
        var scenario = Ready(ReadyRequest(server, withBinding: false),
                             ScenarioTrigger.EveryMinutes(30), anchor);

        var outcome = await Service().RunNowAsync(scenario);

        Assert.False(outcome.Ok);
        Assert.Contains("привязка", outcome.Problem, StringComparison.Ordinal);

        Assert.Equal(anchor.ToUniversalTime(),
                     Cards.ScenarioLastRun(scenario.Id)!.Value.ToUniversalTime());

        // А вот в журнале строка есть: человек просил, значит, событие было.
        var row = Assert.Single(_journal.Recent());
        Assert.Equal(RunChannel.Scenario, row.Channel);
    }

    /// <summary>Руками запускается и выключенный сценарий: тумблер про расписание, а не про запрет.</summary>
    [Fact]
    public async Task A_disabled_scenario_still_runs_by_hand()
    {
        using var server = Server();
        var scenario = Ready(ReadyRequest(server), ScenarioTrigger.EveryMinutes(30), enabled: false);

        var outcome = await Service().RunNowAsync(scenario);

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Single(server.Seen);
    }
}
