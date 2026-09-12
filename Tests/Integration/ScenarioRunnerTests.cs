using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Выполнение сценария (<see cref="ScenarioRunner"/>) на настоящем сокете и настоящей базе:
/// запрос уходит, ответ ложится в коллекцию, шаги идут по порядку.
///
/// Проверяется не «работает ли обмен» — это дело <c>RequestRunnerTests</c> и
/// <c>CollectionBindingTests</c>, — а обещания сценария: раскладку он берет у карточки запроса и
/// не выдумывает своей, первый неудавшийся шаг останавливает цепочку, а то, что мешает запуску,
/// называется словами и одинаково для службы и для человека.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class ScenarioRunnerTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly string       _dir;
    private readonly string       _appDb;
    private readonly string       _otherDb;
    private readonly DatabaseRegistry _registry;

    private readonly List<string> _notices = [];

    public ScenarioRunnerTests()
    {
        _dir      = Directory.CreateTempSubdirectory("sbrun_").FullName;
        _appDb    = Path.Combine(_dir, "app.db");
        _otherDb  = Path.Combine(_dir, "database.db");
        _registry = DatabaseRegistry.Open(_otherDb, _appDb);
    }

    public void Dispose()
    {
        EgressGate.Close();
        RunJournal.Attach(null);
        SecurityJournal.Attach(null);
        _registry.Dispose();
        _db.Dispose();
        TempDatabase.Cleanup(_appDb);
        TempDatabase.Cleanup(_otherDb);
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    private IntegrationsRegistry Cards => _registry.Integrations;

    private const string Master = "секрет-организации";

    private static void AllowLoopback(int maxRows = 1000, bool master = false) =>
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal,
            Hosts:           ["127.0.0.1"],
            Networks:        ["127.0.0.0/8"],
            Folders:         [],
            MaxRowsPerRun:   maxRows,
            MaxBytesPerDay:  1024 * 1024,
            MaxRunsPerHour:  100,
            ValidUntil:      null,
            UserMayAddHosts: false,
            Source:          "тест",
            MasterHash:      master ? MasterPassword.Hash(Master) : "")));

    /// <summary>Коллекция «Заказы» со свойством «Номер».</summary>
    private (int CollectionId, int NumberId) Collection()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Заказы");
        var number     = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Номер");

        return (collection.Id!.Value, number.Id!.Value);
    }

    /// <summary>Соединение на тестовый сервер и запрос к нему.</summary>
    private int NewRequest(TinyServer server, string name = "Заказы")
    {
        var connection = Cards.Save(IntegrationConnection.New() with
        {
            Name           = "Учетная система",
            Address        = $"http://127.0.0.1:{server.Port}",
            // Запас времени намеренно большой: на загруженной машине полный прогон отнимает
            // потоки у тестового сервера, а проверяем мы не скорость петли.
            TimeoutSeconds = 30,
            Retries        = 0,
        });

        return Cards.SaveRequest(IntegrationRequest.New() with
        {
            Name         = name,
            ConnectionId = connection.Id,
            Path         = "/orders",
        }).Id;
    }

    private void BindTo(int requestId, int collectionId, int numberId) =>
        Cards.SaveBinding(requestId, new CollectionBinding(_db.Manager.DbId, collectionId,
        [
            new BindingColumn("Название", null),
            new BindingColumn("Номер",    numberId),
        ]));

    private Scenario Save(params ScenarioStep[] steps) =>
        Cards.SaveScenario(Scenario.New() with
        {
            Name  = "Заказы каждые полчаса",
            Steps = steps,
        });

    private Task<ScenarioOutcome> Run(Scenario scenario) =>
        ScenarioRunner.RunAsync(scenario, Cards, [_db.Manager], [], DateTimeOffset.Now, _notices.Add);

    private static string Items(params string[] names) =>
        "[" + string.Join(",", names.Select(n => $"{{\"Название\":\"{n}\",\"Номер\":\"{n}-1\"}}")) + "]";

    // ── Обычный запуск ────────────────────────────────────────────────────────

    /// <summary>Весь путь: запрос ушел, ответ разобран, записи легли в коллекцию.</summary>
    [Fact]
    public async Task Request_step_puts_the_answer_into_the_collection()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("Молоток", "Отвертка")));

        var (collectionId, numberId) = Collection();
        var requestId = NewRequest(server);
        BindTo(requestId, collectionId, numberId);

        var outcome = await Run(Save(ScenarioStep.OfRequest(requestId)));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Equal(1, outcome.Done);
        Assert.Equal(2, outcome.Rows);
        Assert.Equal(2, _db.Constructor.GetRecords(collectionId).Count);
    }

    /// <summary>
    /// Повторный запуск не плодит дубли — по ключу сопоставления из ПРИВЯЗКИ. Ради этого
    /// сценарий и берет чужую раскладку, а не составляет свою.
    /// </summary>
    [Fact]
    public async Task A_second_run_updates_instead_of_duplicating()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("Молоток")));

        var (collectionId, numberId) = Collection();
        var requestId = NewRequest(server);

        Cards.SaveBinding(requestId, new CollectionBinding(_db.Manager.DbId, collectionId,
            [new BindingColumn("Название", null), new BindingColumn("Номер", numberId)],
            MatchColumn: "Номер"));

        var scenario = Save(ScenarioStep.OfRequest(requestId));

        await Run(scenario);
        await Run(scenario);

        Assert.Single(_db.Constructor.GetRecords(collectionId));
    }

    /// <summary>Шаги идут по порядку, и сообщение доходит до человека.</summary>
    [Fact]
    public async Task Steps_run_in_order()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("Молоток")));

        var (collectionId, numberId) = Collection();
        var requestId = NewRequest(server);
        BindTo(requestId, collectionId, numberId);

        var outcome = await Run(Save(
            ScenarioStep.OfRequest(requestId),
            ScenarioStep.OfNotice("Заказы обновлены")));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Equal(2, outcome.Done);
        Assert.Equal(["Заказы обновлены"], _notices);
        Assert.Equal(2, outcome.Lines.Count);
        Assert.Contains("создано 1", outcome.Lines[0], StringComparison.Ordinal);
    }

    // ── Что останавливает сценарий ────────────────────────────────────────────

    /// <summary>
    /// Первый неудавшийся шаг останавливает цепочку: следующий работал бы с данными, которых не
    /// пришло. Сообщение после неудачного запроса не показывается — иначе оно уверяло бы, что
    /// все хорошо.
    /// </summary>
    [Fact]
    public async Task The_first_failed_step_stops_the_chain()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(500, "beda"));

        var (collectionId, numberId) = Collection();
        var requestId = NewRequest(server);
        BindTo(requestId, collectionId, numberId);

        var outcome = await Run(Save(
            ScenarioStep.OfRequest(requestId),
            ScenarioStep.OfNotice("Заказы обновлены")));

        Assert.False(outcome.Ok);
        Assert.Equal(0, outcome.Done);
        Assert.Contains("500", outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(_notices);
        Assert.Contains("остановлен на шаге 1 из 2", outcome.Summary(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Запрос без привязки сценарий не выполняет и говорит почему: угаданное соответствие
    /// колонок видно только по пустой коллекции через неделю.
    /// </summary>
    [Fact]
    public async Task A_request_without_a_binding_is_refused_with_the_reason()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("Молоток")));

        Collection();
        var requestId = NewRequest(server);   // привязку не сохраняем

        var scenario = Save(ScenarioStep.OfRequest(requestId));

        Assert.Contains("не настроена привязка",
                        ScenarioRunner.Blocker(scenario, Cards, [_db.Manager], []) ?? "",
                        StringComparison.Ordinal);

        var outcome = await Run(scenario);

        Assert.False(outcome.Ok);
        Assert.Equal(0, outcome.Done);
        Assert.Empty(server.Seen);   // до сокета дело не дошло
    }

    /// <summary>
    /// База закрыта — сценарий не идет. Служба на этом молча пропустит тик (база откроется, и
    /// пропущенное догонится), а человеку скажут причину.
    /// </summary>
    [Fact]
    public async Task A_closed_database_stops_the_scenario()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("Молоток")));

        var (collectionId, numberId) = Collection();
        var requestId = NewRequest(server);
        BindTo(requestId, collectionId, numberId);

        var scenario = Save(ScenarioStep.OfRequest(requestId));

        // Открытых баз нет вовсе — так это выглядит после размонтирования.
        Assert.Contains("не открыта", ScenarioRunner.Blocker(scenario, Cards, [], []) ?? "",
                        StringComparison.Ordinal);

        var outcome = await ScenarioRunner.RunAsync(
            scenario, Cards, [], [], DateTimeOffset.Now, _notices.Add);

        Assert.False(outcome.Ok);
        Assert.Empty(server.Seen);
        Assert.Contains("не запускался", outcome.Summary(), StringComparison.Ordinal);
    }

    // ── Журнал ────────────────────────────────────────────────────────────────

    /// <summary>
    /// В журнале две строки: шаг и сценарий целиком. Строка сценария в накопительный расход не
    /// идет — его уже заняла строка запроса, а посчитать одно дважды значит вдвое занизить
    /// потолок.
    /// </summary>
    [Fact]
    public async Task The_journal_gets_a_line_for_the_step_and_for_the_scenario()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("Молоток")));

        var journal = new RunJournal(Path.Combine(_dir, "integrations.db"));
        RunJournal.Attach(journal);

        var (collectionId, numberId) = Collection();
        var requestId = NewRequest(server);
        BindTo(requestId, collectionId, numberId);

        await Run(Save(ScenarioStep.OfRequest(requestId)));

        var rows = journal.Recent();

        var scenario = Assert.Single(rows, r => r.Channel == RunChannel.Scenario);
        var step     = Assert.Single(rows, r => r.Channel == RunChannel.Request);

        Assert.True(scenario.Ok);
        Assert.False(scenario.Counts);
        Assert.Equal("Заказы каждые полчаса", scenario.Name);

        Assert.True(step.Ok);
        Assert.True(step.Counts);            // обмен состоялся — место в потолке занято
        Assert.Equal(_db.Manager.DbId, step.DbId);

        // Обе строки называют сценарий: отбор по нему должен доставать и итог, и его шаги.
        Assert.Equal("Заказы каждые полчаса", scenario.Scenario);
        Assert.Equal("Заказы каждые полчаса", step.Scenario);

        // Номер и род шага — у шага; у итоговой строки шага нет, она про сценарий целиком.
        Assert.Equal("шаг 1 из 1 · запрос", step.Step);
        Assert.Equal("", scenario.Step);
    }

    /// <summary>
    /// Неудача ДО первого запроса тоже попадает в журнал: своей строки у нее нет вовсе, и без
    /// строки сценария причина не видна нигде.
    /// </summary>
    [Fact]
    public async Task A_failure_before_the_first_request_is_journalled()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("Молоток")));

        var journal = new RunJournal(Path.Combine(_dir, "integrations.db"));
        RunJournal.Attach(journal);

        Collection();
        var requestId = NewRequest(server);   // без привязки

        await Run(Save(ScenarioStep.OfRequest(requestId)));

        var row = Assert.Single(journal.Recent());

        Assert.Equal(RunChannel.Scenario, row.Channel);
        Assert.False(row.Ok);
        Assert.Contains("привязка", row.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Запуск, остановленный политикой ДО сокета, места в часовом потолке не занимает. Иначе
    /// сценарий по расписанию продлевал бы отказ сам себе: часовое окно не опустело бы никогда.
    /// </summary>
    [Fact]
    public async Task A_run_stopped_before_the_socket_does_not_count()
    {
        // Потолок запусков в час — ноль: любой обмен запрещен накопительной квотой.
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal, ["127.0.0.1"], ["127.0.0.0/8"], [], 1000, 1024 * 1024, 0,
            null, false, "тест")));

        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("Молоток")));

        var journal = new RunJournal(Path.Combine(_dir, "integrations.db"));
        RunJournal.Attach(journal);

        // Считает расход журнал, а решает шлюз — так это и связано в приложении.
        EgressGate.AttachUsage(journal.AsUsage());

        var (collectionId, numberId) = Collection();
        var requestId = NewRequest(server);
        BindTo(requestId, collectionId, numberId);

        var outcome = await Run(Save(ScenarioStep.OfRequest(requestId)));

        Assert.False(outcome.Ok);
        Assert.Empty(server.Seen);

        var step = Assert.Single(journal.Recent(), r => r.Channel == RunChannel.Request);

        Assert.False(step.Ok);
        Assert.False(step.Counts);
    }

    // ── Замок на исходящем ────────────────────────────────────────────────────

    /// <summary>Запрос-отправитель на тестовый сервер: такому нужно подтверждение направления.</summary>
    private int NewPush(TinyServer server)
    {
        var connection = Cards.Save(IntegrationConnection.New() with
        {
            Name           = "Учетная система",
            Address        = $"http://127.0.0.1:{server.Port}",
            TimeoutSeconds = 30,
            Retries        = 0,
        });

        return Cards.SaveRequest(IntegrationRequest.New() with
        {
            Name         = "Отдать итог",
            ConnectionId = connection.Id,
            Method       = RequestMethod.Post,
            Path         = "/result",
            Body         = "{\"итог\":1}",
        }).Id;
    }

    private SecurityJournal Security()
    {
        var journal = new SecurityJournal(Path.Combine(_dir, "security.db"));
        SecurityJournal.Attach(journal);
        return journal;
    }

    /// <summary>
    /// Сценарий по расписанию — единственное место, где отправка уходит ночью и без человека, и
    /// замок на ней держит так же, как на кнопке: неподтвержденный шаг не доходит до сокета.
    /// </summary>
    [Fact]
    public async Task An_unconfirmed_outgoing_step_stops_before_the_socket()
    {
        AllowLoopback(master: true);
        Security();

        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("Молоток")));

        var (collectionId, numberId) = Collection();
        var requestId = NewPush(server);
        BindTo(requestId, collectionId, numberId);

        var outcome = await Run(Save(ScenarioStep.OfRequest(requestId)));

        Assert.False(outcome.Ok);
        Assert.Contains("направление", outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(server.Seen);
    }

    /// <summary>
    /// Заблокированный шаг оставляет в журнале безопасности ИМЯ СЦЕНАРИЯ. Отправку, ушедшую
    /// сама собой в три часа ночи, проверяющий обязан уметь привязать к тому, кто ее завел, —
    /// иначе строка журнала говорит «программа попробовала» и больше ничего.
    /// </summary>
    [Fact]
    public async Task The_security_row_names_the_scenario()
    {
        AllowLoopback(master: true);
        var journal = Security();

        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("Молоток")));

        var (collectionId, numberId) = Collection();
        var requestId = NewPush(server);
        BindTo(requestId, collectionId, numberId);

        await Run(Save(ScenarioStep.OfRequest(requestId)));

        var row = journal.Recent().Single();

        Assert.Equal(SecurityEvent.Send,          row.Event);
        Assert.False(row.Allowed);
        Assert.Equal("Заказы каждые полчаса",     row.Scenario);
        Assert.Equal(_db.Manager.DbId,            row.DbId);
    }

    /// <summary>
    /// Подтвержденный шаг проходит целиком — и отмечается использованным: обмен, работающий
    /// каждый день, не должен отключиться по простою посреди работы.
    /// </summary>
    [Fact]
    public async Task A_confirmed_outgoing_step_goes_through_and_marks_the_direction()
    {
        AllowLoopback(master: true);
        Security();

        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("Молоток")));

        var (collectionId, numberId) = Collection();
        var requestId = NewPush(server);
        BindTo(requestId, collectionId, numberId);

        var request = Cards.FindRequest(requestId)!;

        Cards.ConfirmDirection(request, Master, "Петров П. П.", days: 90,
                               DateTimeOffset.Now, out var trouble);
        Assert.Equal("", trouble);

        var outcome = await Run(Save(ScenarioStep.OfRequest(requestId)));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Single(server.Seen);
        Assert.NotNull(Cards.Direction(requestId)!.UsedAt);
    }

    /// <summary>
    /// Правка запроса после подтверждения останавливает отправку. Подтвердить безобидный запрос
    /// и дописать в него выгрузку — самый дешевый обход согласования, и закрыт он должен быть не
    /// на экране, а здесь, где отправка происходит на самом деле.
    /// </summary>
    [Fact]
    public async Task An_edited_request_stops_even_with_a_confirmation_in_place()
    {
        AllowLoopback(master: true);
        Security();

        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("Молоток")));

        var (collectionId, numberId) = Collection();
        var requestId = NewPush(server);
        BindTo(requestId, collectionId, numberId);

        var request = Cards.FindRequest(requestId)!;

        Cards.ConfirmDirection(request, Master, "Петров П. П.", days: 90,
                               DateTimeOffset.Now, out _);

        // Тело поменялось — отпечаток разошелся с подтвержденным.
        Cards.SaveRequest(request with { Body = "{\"итог\":1,\"клиенты\":\"все\"}" });

        var outcome = await Run(Save(ScenarioStep.OfRequest(requestId)));

        Assert.False(outcome.Ok);
        Assert.Contains("правили", outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(server.Seen);
    }
}
