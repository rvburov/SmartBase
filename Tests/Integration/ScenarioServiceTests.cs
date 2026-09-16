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

    /// <summary>
    /// Сценарий с одним шагом-запросом. Якорь ставит САМО сохранение — моментом, который передан
    /// в <paramref name="anchor"/> (по умолчанию «сейчас»): включенным сценарий становится в
    /// окне правки, и якорь встает там же.
    ///
    /// Раньше помощник ставил якорь вторым шагом, через тумблер, и оттого молча держался за
    /// поведение, которого у программы больше нет: сохранение включенным якоря не ставило.
    /// </summary>
    private Scenario Ready(int requestId, ScenarioTrigger trigger, DateTimeOffset? anchor = null,
                           bool enabled = true)
    {
        var scenario = Cards.SaveScenario(Scenario.New() with
        {
            Name    = "Заказы по расписанию",
            Enabled = enabled,
            Trigger = trigger,
            Steps   = [ScenarioStep.OfRequest(requestId)],
        }, anchor ?? Now);

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
    /// Заведенный только что — не повод срываться с места. Якорь встает моментом включения,
    /// поэтому «ежедневно в 07:00», заведенное в полдень, ждет завтрашнего утра, а не идет
    /// немедленно. Ради этого якорь и существует.
    ///
    /// Обратная сторона того же правила — сценарий БЕЗ якоря вовсе (так лежат строки в базах,
    /// заведенных до того, как якорь стал ставиться при сохранении): <c>IsDue(null, …)</c>
    /// отвечает «не пора», и это закреплено юнит-тестами модели (<c>ScenarioTests</c>).
    /// </summary>
    [Fact]
    public async Task A_scenario_created_after_its_hour_waits_for_the_next_day()
    {
        using var server = Server();
        Ready(ReadyRequest(server), ScenarioTrigger.DailyAt(new TimeOnly(7, 0)));

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

    // ── Триггер «изменение данных» ────────────────────────────────────────────

    /// <summary>
    /// Коллекция, за которой следит триггер, — ОТДЕЛЬНАЯ от той, куда сценарий пишет: иначе он
    /// заводил бы сам себя, и этот случай проверяется своим тестом.
    /// </summary>
    private (int Id, string Name) Watched(string name = "Клиенты") =>
        (SampleData.AddCollection(_db.Constructor, name).Id!.Value, name);

    /// <summary>Сценарий, который заводится правками в указанной коллекции.</summary>
    private Scenario ByChange(int requestId, int collectionId, int settle = 15) =>
        Ready(requestId, ScenarioTrigger.OnChange(_db.Manager.DbId, collectionId, settle));

    private void Note(ScenarioService service, string collection, DateTimeOffset at) =>
        service.Changes.Note(_db.Manager.Path, collection, at);

    /// <summary>Правок не было — сценарий стоит. Ни часов, ни повода.</summary>
    [Fact]
    public async Task Without_a_change_a_data_scenario_stays_put()
    {
        using var server = Server();
        var watched = Watched();

        ByChange(ReadyRequest(server), watched.Id);

        Assert.Equal(0, await Service().TickAsync(Now.AddHours(5)));
        Assert.Empty(server.Seen);
    }

    /// <summary>
    /// Правка была — но обмен уходит не сразу, а когда правки утихнут. Это и есть весь смысл
    /// времени тишины: правка десяти строк подряд дает один запуск, а не десять.
    /// </summary>
    [Fact]
    public async Task A_change_starts_the_scenario_only_after_the_quiet_time()
    {
        using var server = Server();
        var watched = Watched();
        var service = Service();

        ByChange(ReadyRequest(server), watched.Id, settle: 15);

        Note(service, watched.Name, Now);

        Assert.Equal(0, await service.TickAsync(Now.AddMinutes(14)));
        Assert.Empty(server.Seen);

        Assert.Equal(1, await service.TickAsync(Now.AddMinutes(16)));
        Assert.Single(server.Seen);
    }

    /// <summary>
    /// Новая правка во время ожидания отсчет НАЧИНАЕТ ЗАНОВО. Иначе человек, правящий таблицу
    /// полчаса, получил бы обмен посреди работы — с половиной своих правок.
    /// </summary>
    [Fact]
    public async Task A_fresh_change_restarts_the_quiet_time()
    {
        using var server = Server();
        var watched = Watched();
        var service = Service();

        ByChange(ReadyRequest(server), watched.Id, settle: 15);

        Note(service, watched.Name, Now);
        Note(service, watched.Name, Now.AddMinutes(10));

        // С первой правки прошло 16 минут, а с последней — только 6.
        Assert.Equal(0, await service.TickAsync(Now.AddMinutes(16)));

        Assert.Equal(1, await service.TickAsync(Now.AddMinutes(26)));
    }

    /// <summary>Отработал — правка забыта: второй раз по одной и той же правке сценарий не идет.</summary>
    [Fact]
    public async Task A_change_starts_the_scenario_only_once()
    {
        using var server = Server();
        var watched = Watched();
        var service = Service();

        ByChange(ReadyRequest(server), watched.Id, settle: 15);

        Note(service, watched.Name, Now);

        Assert.Equal(1, await service.TickAsync(Now.AddMinutes(16)));
        Assert.Equal(0, await service.TickAsync(Now.AddHours(5)));

        Assert.Single(server.Seen);
    }

    /// <summary>
    /// А новая правка заводит его снова. Правки забываются В НАЧАЛЕ запуска именно ради этого:
    /// то, что пришло после чтения данных, обязано уйти следующим обменом, а не потеряться.
    /// </summary>
    [Fact]
    public async Task A_change_after_the_run_starts_it_again()
    {
        using var server = Server();
        var watched = Watched();
        var service = Service();

        ByChange(ReadyRequest(server), watched.Id, settle: 15);

        Note(service, watched.Name, Now);
        Assert.Equal(1, await service.TickAsync(Now.AddMinutes(16)));

        Note(service, watched.Name, Now.AddMinutes(20));
        Assert.Equal(1, await service.TickAsync(Now.AddMinutes(40)));

        Assert.Equal(2, server.Seen.Count);
    }

    /// <summary>Правка в чужой коллекции сценарий не заводит: следят за одной, названной.</summary>
    [Fact]
    public async Task A_change_in_another_collection_does_not_start_it()
    {
        using var server = Server();
        var watched = Watched();
        var service = Service();

        ByChange(ReadyRequest(server), watched.Id, settle: 15);

        Note(service, "Поставщики", Now);

        Assert.Equal(0, await service.TickAsync(Now.AddHours(1)));
        Assert.Empty(server.Seen);
    }

    /// <summary>
    /// Настоящая запись в базу доезжает до копилки через шину. Остальные тесты подкладывают
    /// правки руками — быстро и без часов, — но проверить сам стык надо хотя бы раз: разойдись
    /// он, и триггер молчал бы, а все проверки оставались бы зелеными.
    /// </summary>
    [Fact]
    public void A_real_commit_reaches_the_watch()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Клиенты");

        var watch = new ChangeWatch();
        watch.Attach();

        try
        {
            SampleData.AddRecord(_db.Constructor, collection.Id!.Value, "Ромашка");

            Assert.NotNull(watch.LastChange(_db.Manager.Path, "Клиенты"));
        }
        finally { watch.Dispose(); }
    }

    /// <summary>Отписались — правки больше не копятся: подписка живет ровно столько же, сколько служба.</summary>
    [Fact]
    public void A_disposed_watch_stops_listening()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Клиенты");

        var watch = new ChangeWatch();
        watch.Attach();
        watch.Dispose();

        SampleData.AddRecord(_db.Constructor, collection.Id!.Value, "Ромашка");

        Assert.Equal(0, watch.Count);
    }

    /// <summary>
    /// Структурная правка — не «данные изменились». Добавленная колонка не меняет того, что
    /// уйдет, и гонять из-за нее обмен незачем.
    /// </summary>
    [Fact]
    public void A_structural_change_is_not_data()
    {
        Assert.True(ChangeWatch.IsData("record"));
        Assert.True(ChangeWatch.IsData("records"));

        Assert.False(ChangeWatch.IsData("field"));
        Assert.False(ChangeWatch.IsData("collection"));
        Assert.False(ChangeWatch.IsData("group"));
        Assert.False(ChangeWatch.IsData("user"));
    }

    /// <summary>
    /// Круг: сценарий заводится правками в коллекции и сам же в нее пишет. Здесь его ловит
    /// проверка выполнимости — коллекция приезжает из привязки карточки запроса, о которой
    /// модель не знает.
    /// </summary>
    [Fact]
    public async Task A_scenario_that_feeds_its_own_trigger_never_runs()
    {
        using var server = Server();
        var requestId = ReadyRequest(server);

        // Привязка запроса смотрит в «Заказы» — за ней же и следим.
        var binding  = Cards.Binding(requestId)!;
        var service  = Service();
        var scenario = ByChange(requestId, binding.CollectionId, settle: 15);

        Note(service, "Заказы", Now);

        Assert.Equal(0, await service.TickAsync(Now.AddHours(1)));
        Assert.Empty(server.Seen);

        Assert.Contains("сам себя",
                        ScenarioRunner.Blocker(scenario, Cards, [_db.Manager], [])!,
                        StringComparison.Ordinal);
    }


    // ── При запуске программы ─────────────────────────────────────────────────

    /// <summary>
    /// Сценарий «при запуске программы» идет на первом же тике — и ровно ОДИН раз за заход.
    /// Ради этого он и заведен: забрать накопившееся у той стороны, пока компьютер был
    /// выключен. Расписанием это не выражается — «каждые 30 минут» после включения ждет
    /// полчаса, а «ежедневно в 07:00» не сработает вовсе, если в семь машина не работала.
    /// </summary>
    [Fact]
    public async Task A_startup_scenario_runs_once_per_launch()
    {
        using var server  = Server();
        Ready(ReadyRequest(server), ScenarioTrigger.OnStartup);

        var service = Service();
        service.PrimeStartup();

        Assert.Equal(1, await service.TickAsync(Now));
        Assert.Single(server.Seen);

        // Второй тик того же захода его не трогает.
        Assert.Equal(0, await service.TickAsync(Now.AddMinutes(1)));
        Assert.Single(server.Seen);
    }

    /// <summary>
    /// Заведенный ПОСЛЕ старта сценарий в этот заход не идет: «при запуске» с того момента еще
    /// не случалось. Иначе сохранение карточки означало бы немедленный обмен — ровно того, чего
    /// человек не просил, и того же, от чего бережет якорь расписания.
    /// </summary>
    [Fact]
    public async Task A_startup_scenario_created_later_waits_for_the_next_launch()
    {
        using var server = Server();

        var service = Service();
        service.PrimeStartup();               // сценариев «при запуске» еще нет

        Ready(ReadyRequest(server), ScenarioTrigger.OnStartup);

        Assert.Equal(0, await service.TickAsync(Now));
        Assert.Empty(server.Seen);
    }

    /// <summary>
    /// Помеха не съедает запуск: сценарий остается в наборе и идет, когда помеха уйдет. Догон
    /// тот же, что у расписания, — иначе единственный шанс отработать за заход терялся бы
    /// из-за базы, которая смонтировалась на секунду позже службы.
    /// </summary>
    [Fact]
    public async Task A_blocked_startup_scenario_keeps_its_turn()
    {
        using var server = Server();
        var request = ReadyRequest(server, withBinding: false);   // привязки нет — помеха
        var scenario = Ready(request, ScenarioTrigger.OnStartup);

        var service = Service();
        service.PrimeStartup();

        Assert.Equal(0, await service.TickAsync(Now));
        Assert.Empty(server.Seen);

        // Помеха ушла — тот же заход, тот же набор.
        var collection = _db.Constructor.GetAllCollections().Single(c => c.Name == "Заказы");
        Cards.SaveBinding(request, new CollectionBinding(_db.Manager.DbId, collection.Id!.Value,
            [new BindingColumn("Название", null)]));

        Assert.Equal(1, await service.TickAsync(Now.AddMinutes(1)));
        Assert.Single(server.Seen);
        Assert.NotNull(Cards.ScenarioLastRun(scenario.Id));
    }

    /// <summary>Выключенный «при запуске» не идет: тумблер главнее триггера.</summary>
    [Fact]
    public async Task A_disabled_startup_scenario_stays_put()
    {
        using var server = Server();
        Ready(ReadyRequest(server), ScenarioTrigger.OnStartup, enabled: false);

        var service = Service();
        service.PrimeStartup();

        Assert.Equal(0, await service.TickAsync(Now));
        Assert.Empty(server.Seen);
    }

    // ── Молчаливых поломок нет ────────────────────────────────────────────────

    /// <summary>
    /// Сценарий, который перестал отрабатывать, ГОВОРИТ об этом человеку.
    ///
    /// Это и есть жалоба «обмен перестал работать без причины»: причина ложилась в
    /// «Диагностику», но туда не заходят, пока не заподозрят неладное, — а заподозрить нечего,
    /// программа молчит. Особенно верно для отказов по потолкам политики: они приходят на
    /// исправно настроенный обмен, который вчера работал.
    ///
    /// Повтор отсекается: одна и та же беда сообщается один раз, иначе ночь без сетевой базы
    /// оставила бы к утру тысячу карточек.
    /// </summary>
    [Fact]
    public async Task A_scenario_that_stopped_working_says_so_once()
    {
        using var server = Server();
        var request = ReadyRequest(server, withBinding: false);   // привязки нет — помеха
        Ready(request, ScenarioTrigger.EveryMinutes(5), anchor: Now.AddMinutes(-10));

        var service = Service();

        Assert.Equal(0, await service.TickAsync(Now));

        var said = Assert.Single(_notices);
        Assert.Contains("не отработал", said, StringComparison.Ordinal);

        // Та же беда на следующем тике — второй карточки нет.
        Assert.Equal(0, await service.TickAsync(Now.AddMinutes(10)));
        Assert.Single(_notices);
    }
}
