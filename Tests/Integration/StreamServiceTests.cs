using Avalonia.Controls;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Extensions;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Служба потоков событий (<see cref="StreamService"/>) на настоящем соединении: стенд открывает
/// поток, шлет событие, служба запускает сценарий и отвечает стенду запросом.
///
/// Проверяется то, что видно только на живой связи: подписка уходит той стороне, событие
/// доезжает до шага строкой, ответ уходит через замок направлений, чужое событие следа не
/// оставляет, оборванный поток открывается заново.
///
/// Стенд и события выдуманные: служба не знает ни одной сторонней программы, и тест, написанный
/// под чужую документацию, проверял бы не ее.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class StreamServiceTests : IDisposable
{
    private const string Master = "секрет-организации";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly TempDatabase     _db = new();
    private readonly string           _dir;
    private readonly DatabaseRegistry _registry;
    private readonly RunJournal       _journal;

    private readonly List<string> _notices = [];

    public StreamServiceTests()
    {
        _dir      = Directory.CreateTempSubdirectory("sbstream_").FullName;
        _registry = DatabaseRegistry.Open(Path.Combine(_dir, "database.db"), Path.Combine(_dir, "app.db"));
        _journal  = new RunJournal(Path.Combine(_dir, "integrations.db"));

        RunJournal.Attach(_journal);

    }

    public void Dispose()
    {

        RunJournal.Attach(null);

        _registry.Dispose();
        _db.Dispose();
        try { SqliteGate.ClearAllPools(); } catch { /* best-effort */ }
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    private IntegrationsRegistry Cards => _registry.Integrations;

    // ── Стендовое расширение ──────────────────────────────────────────────────

    private const string StandId = "stand";

    private sealed class Stand(params IExtensionAction[] actions) : IExtension
    {
        public string Id                  => StandId;
        public string Name                => "Стенд";
        public string MarkdownDescription => "";
        public bool   CanRun              => false;

        public IReadOnlyList<IExtensionAction> Actions { get; } = actions;

        public Window? CreateWindow() => null;
    }

    /// <summary>Операция: заводит запись и отвечает отправителю — либо тем, что велел тест.</summary>
    private sealed class Clerk : IExtensionAction
    {
        public string              Key         => "accept";
        public string              Name        => "Принять заявку";
        public string              Description => "Стендовое действие";
        public ExtensionActionKind Kind        => ExtensionActionKind.Operation;

        public IReadOnlyList<ExtensionActionField> Fields =>
        [
            new("text",   "Текст", Required: false),
            new("sender", "Отправитель"),
        ];

        /// <summary>Чем ответить вместо записи; <c>null</c> — записать и согласиться.</summary>
        public Func<ActionInput, ActionResult?>? Answer { get; init; }

        public List<(string Text, string Sender)> Seen { get; } = [];

        public ActionResult Run(ActionContext context, ActionInput input)
        {
            lock (Seen) Seen.Add((input.Value("text"), input.Value("sender")));

            if (Answer?.Invoke(input) is { } answer) return answer;

            context.Repo!.AddRecord(new DbRecord
            {
                CollectionId = context.CollectionId,
                Title        = input.Value("text"),
            });

            return ActionResult.Done("Принято: " + input.Value("text"));
        }
    }

    // ── Обвязка ───────────────────────────────────────────────────────────────

    private int Collection() =>
        SampleData.AddCollection(_db.Constructor, "Заявки").Id!.Value;

    /// <summary>Соединение со стендом, поток на нем и запрос ответа с подтвержденным направлением.</summary>
    private (IntegrationStream Stream, IntegrationRequest Reply) Cabling(
        StreamStand stand, StreamProtocol protocol = StreamProtocol.Plain, string key = "id",
        bool confirm = true)
    {
        var connection = Cards.Save(IntegrationConnection.New() with
        {
            Name           = "Сторонняя программа",
            Address        = stand.Address,
            TimeoutSeconds = 30,
            Retries        = 0,
        });

        var stream = Cards.SaveStream(IntegrationStream.New() with
        {
            Name         = "События",
            ConnectionId = connection.Id,
            Path         = "/events",
            Protocol     = protocol,
            Topics       = protocol == StreamProtocol.Stomp ? ["/topic/requests"] : [],
            ItemsPath    = "data",
            KeyField     = key,
        });

        var reply = Cards.SaveRequest(IntegrationRequest.New() with
        {
            Name         = "Ответ на событие",
            ConnectionId = connection.Id,
            Method       = RequestMethod.Put,
            Path         = "/reply",
            Body         = """{"to": {{поле.author}}, "message": "{{ответ.текст}}"}""",
        });

        return (stream, reply);
    }

    private Scenario Save(IntegrationStream stream, IntegrationRequest? reply, int collection,
                          string failText = "Сервер не работает", bool enabled = true,
                          int dbId = 0, string replyBreak = "") =>
        Cards.SaveScenario(Scenario.New() with
        {
            Name    = "Заявки по событию",
            Enabled = enabled,
            Trigger = ScenarioTrigger.OnStream(stream.Id, reply?.Id ?? 0, reply is null ? "" : failText,
                                               reply is null ? "" : replyBreak),
            Steps   =
            [
                ScenarioStep.OfFilter(new ScenarioCondition("kind", ScenarioTest.Equals, "request")),
                ScenarioStep.OfAction(StandId, "accept", dbId > 0 ? dbId : _db.Manager.DbId, collection,
                                      new Dictionary<string, string>
                                      {
                                          ["text"]   = "{{body}}",
                                          ["sender"] = "{{author}}",
                                      }),
            ],
        }, DateTimeOffset.Now);

    private StreamService Service(Clerk clerk) =>
        new(Cards, () => [_db.Manager], [new Stand(clerk)], text => { lock (_notices) _notices.Add(text); });

    private static string Event(int id, string body, int author = 1001, string kind = "request") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            kind,
            data = new[] { new { id, body, author } },
        });

    /// <summary>Ждать, пока условие не выполнится: служба работает в фоне, и момент ее не назначить.</summary>
    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.Now + Patience;

        while (!condition())
        {
            if (DateTimeOffset.Now > deadline) throw new TimeoutException("не дождались: " + what);
            await Task.Delay(50);
        }
    }

    private IReadOnlyList<RunEntry> Journal(RunChannel channel) =>
        _journal.Page(new RunFilter(Channel: channel));

    // ── Событие → сценарий → ответ ────────────────────────────────────────────

    /// <summary>
    /// Весь путь: событие пришло — запись легла — ответ ушел. В ответе получатель взят из
    /// колонки события, а текст — слова действия.
    /// </summary>
    [Fact]
    public async Task An_event_runs_the_scenario_and_the_reply_goes_back()
    {
        using var stand = new StreamStand();
        var (stream, reply) = Cabling(stand);
        var collection = Collection();
        Save(stream, reply, collection);

        var clerk = new Clerk();
        using var service = Service(clerk);

        service.Begin();
        await stand.WaitConnectedAsync(Patience);
        await Until(() => service.State(stream.Id)?.Connected == true, "поток подключен");

        await stand.SendAsync(Event(1, "Нужна втулка"));

        await Until(() => stand.Requests.Count == 1, "ответ на событие");
        await service.SettleAsync(Patience);

        Assert.Equal(("Нужна втулка", "1001"), Assert.Single(clerk.Seen));
        Assert.Equal("Нужна втулка", Assert.Single(_db.Constructor.GetRecords(collection)).Title);

        var sent = Assert.Single(stand.Requests);
        Assert.Equal("PUT",    sent.Method);
        Assert.Equal("/reply", sent.Path);
        Assert.Equal("""{"to": 1001, "message": "Принято: Нужна втулка"}""", sent.Body);

        // Ответ — отправка: у нее след в журнале безопасности и отметка в журнале запусков.

        var run = Assert.Single(Journal(RunChannel.Request));
        Assert.True(run.Ok);
        Assert.Equal(ScenarioRunner.ReplyMark, run.Step);
    }

    /// <summary>
    /// Подписка уходит той стороне сразу после соединения — без нее поток с темами молчал бы, и
    /// отличить это от «событий не было» нельзя.
    /// </summary>
    [Fact]
    public async Task A_subscription_is_sent_on_connect_and_events_come_in_frames()
    {
        using var stand = new StreamStand();
        var (stream, reply) = Cabling(stand, StreamProtocol.Stomp);
        Save(stream, reply, Collection());

        var clerk = new Clerk();
        using var service = Service(clerk);

        service.Begin();
        await stand.WaitConnectedAsync(Patience);
        await Until(() => stand.Greeting.Count >= 2, "кадры подписки");

        Assert.StartsWith("CONNECT\n", stand.Greeting[0], StringComparison.Ordinal);
        Assert.Contains("destination:/topic/requests\n", stand.Greeting[1], StringComparison.Ordinal);

        await stand.SendAsync("CONNECTED\nversion:1.2\n\n\0");
        await stand.SendAsync("MESSAGE\ndestination:/topic/requests\nmessage-id:1\n\n"
                            + Event(1, "Нужна втулка") + "\0");

        await Until(() => stand.Requests.Count == 1, "ответ на событие");
        await service.SettleAsync(Patience);

        Assert.Equal(("Нужна втулка", "1001"), Assert.Single(clerk.Seen));
    }

    /// <summary>
    /// Отказ, который действие адресовало отправителю, уходит его словами — со всеми
    /// переводами строк, и тело запроса при этом остается годным.
    /// </summary>
    [Fact]
    public async Task A_refusal_goes_back_in_the_words_of_the_action()
    {
        using var stand = new StreamStand();
        var (stream, reply) = Cabling(stand);
        var collection = Collection();
        Save(stream, reply, collection);

        var clerk = new Clerk { Answer = _ => ActionResult.Refused("Номер: не указан\nПроект: \"не указан\"") };
        using var service = Service(clerk);

        service.Begin();
        await stand.WaitConnectedAsync(Patience);
        await stand.SendAsync(Event(1, "что-то не то"));

        await Until(() => stand.Requests.Count == 1, "ответ на событие");
        await service.SettleAsync(Patience);

        using var body = System.Text.Json.JsonDocument.Parse(stand.Requests[0].Body);

        Assert.Equal("Номер: не указан\nПроект: \"не указан\"",
                     body.RootElement.GetProperty("message").GetString());

        // Разговор с отправителем — не поломка: человеку за этим компьютером о нем не говорят.
        lock (_notices) Assert.Empty(_notices);
    }

    /// <summary>
    /// Та сторона показывает текст разметкой и перевода строки не видит: строки ответа
    /// отделяются тем, что на этот случай ввел человек.
    /// </summary>
    [Fact]
    public async Task The_reply_lines_are_separated_by_the_break_of_the_other_side()
    {
        using var stand = new StreamStand();
        var (stream, reply) = Cabling(stand);
        var collection = Collection();
        Save(stream, reply, collection, replyBreak: "<br>");

        var clerk = new Clerk { Answer = _ => ActionResult.Refused("Номер: не указан\nПроект: не указан") };
        using var service = Service(clerk);

        service.Begin();
        await stand.WaitConnectedAsync(Patience);
        await stand.SendAsync(Event(1, "что-то не то"));

        await Until(() => stand.Requests.Count == 1, "ответ на событие");
        await service.SettleAsync(Patience);

        using var body = System.Text.Json.JsonDocument.Parse(stand.Requests[0].Body);

        Assert.Equal("Номер: не указан<br>Проект: не указан",
                     body.RootElement.GetProperty("message").GetString());
    }

    /// <summary>
    /// Сценарий не отработал по причине не в событии — отправителю уходит текст, который на этот
    /// случай назначил человек, а человеку за компьютером — причина.
    /// </summary>
    [Fact]
    public async Task A_breakdown_is_answered_with_the_fail_text()
    {
        using var stand = new StreamStand();
        var (stream, reply) = Cabling(stand);
        Save(stream, reply, Collection());

        var clerk = new Clerk { Answer = _ => ActionResult.Failed("журнал не тот") };
        using var service = Service(clerk);

        service.Begin();
        await stand.WaitConnectedAsync(Patience);
        await stand.SendAsync(Event(1, "Нужна втулка"));

        await Until(() => stand.Requests.Count == 1, "ответ на событие");
        await service.SettleAsync(Patience);

        using var body = System.Text.Json.JsonDocument.Parse(stand.Requests[0].Body);
        Assert.Equal("Сервер не работает", body.RootElement.GetProperty("message").GetString());

        lock (_notices)
            Assert.Contains(_notices, n => n.Contains("журнал не тот", StringComparison.Ordinal));
    }

    /// <summary>
    /// База сценария не открыта — сценарий не запускается вовсе, а отправитель все равно получает
    /// ответ: молчание он принял бы за «сообщение дошло и обрабатывается».
    /// </summary>
    [Fact]
    public async Task A_closed_database_is_answered_with_the_fail_text()
    {
        using var stand = new StreamStand();
        var (stream, reply) = Cabling(stand);
        Save(stream, reply, Collection(), dbId: 77);   // такой базы среди открытых нет

        var clerk = new Clerk();
        using var service = Service(clerk);

        service.Begin();
        await stand.WaitConnectedAsync(Patience);
        await stand.SendAsync(Event(1, "Нужна втулка"));

        await Until(() => stand.Requests.Count == 1, "ответ на событие");
        await service.SettleAsync(Patience);

        using var body = System.Text.Json.JsonDocument.Parse(stand.Requests[0].Body);
        Assert.Equal("Сервер не работает", body.RootElement.GetProperty("message").GetString());
        Assert.Empty(clerk.Seen);
    }

    /// <summary>
    /// Событие другого вида отбор не проходит и следа не оставляет: ни запуска, ни ответа, ни
    /// строки журнала. Следующее за ним, нужное, обрабатывается как обычно.
    /// </summary>
    [Fact]
    public async Task An_event_of_another_kind_is_passed_in_silence()
    {
        using var stand = new StreamStand();
        var (stream, reply) = Cabling(stand);
        Save(stream, reply, Collection());

        var clerk = new Clerk();
        using var service = Service(clerk);

        service.Begin();
        await stand.WaitConnectedAsync(Patience);

        await stand.SendAsync(Event(1, "статус сменился", kind: "status"));
        await stand.SendAsync(Event(2, "Нужна втулка"));

        await Until(() => stand.Requests.Count == 1, "ответ на событие");
        await service.SettleAsync(Patience);

        Assert.Equal(("Нужна втулка", "1001"), Assert.Single(clerk.Seen));
        Assert.Single(stand.Requests);
        Assert.Single(Journal(RunChannel.Scenario));
    }

    /// <summary>
    /// Ключ запоминается только у события, которое кому-то подошло. По одному потоку об одном и
    /// том же приходят события разного вида с одним ключом, и запомни служба ключ у первого, не
    /// подошедшего, — второе, нужное, отсеклось бы как повтор.
    /// </summary>
    [Fact]
    public async Task A_repeated_event_is_handled_once_but_a_skipped_one_does_not_block_its_twin()
    {
        using var stand = new StreamStand();
        var (stream, reply) = Cabling(stand);
        var collection = Collection();
        Save(stream, reply, collection);

        var clerk = new Clerk();
        using var service = Service(clerk);

        service.Begin();
        await stand.WaitConnectedAsync(Patience);

        await stand.SendAsync(Event(5, "уведомление о том же", kind: "notice"));
        await stand.SendAsync(Event(5, "Нужна втулка"));
        await stand.SendAsync(Event(5, "Нужна втулка"));   // повтор той же доставки

        await Until(() => stand.Requests.Count >= 1, "ответ на событие");
        await service.SettleAsync(Patience);

        Assert.Single(clerk.Seen);
        Assert.Single(stand.Requests);
        Assert.Single(_db.Constructor.GetRecords(collection));
    }

    /// <summary>Одинаковый текст разных сообщений не означает повтор доставки одного события.</summary>
    [Fact]
    public async Task Identical_message_texts_with_distinct_ids_each_get_a_reply()
    {
        using var stand = new StreamStand();
        var (stream, reply) = Cabling(stand);
        Save(stream, reply, Collection());
        var clerk = new Clerk { Answer = _ => ActionResult.Done("Ответ получен") };
        using var service = Service(clerk);
        service.Begin();
        await stand.WaitConnectedAsync(Patience);

        await stand.SendAsync(Event(1, "да"));
        await stand.SendAsync(Event(2, "да"));
        await stand.SendAsync(Event(3, "нет"));
        await stand.SendAsync(Event(4, "нет"));
        await stand.SendAsync(Event(4, "нет")); // повтор доставки последнего сообщения
        await Until(() => stand.Requests.Count >= 4, "ответы на четыре разных сообщения");
        await service.SettleAsync(Patience);

        Assert.Equal(new[] { "да", "да", "нет", "нет" }, clerk.Seen.Select(s => s.Text));
        Assert.Equal(4, stand.Requests.Count);
        Assert.Equal(4, Journal(RunChannel.Scenario).Count);
    }

    /// <summary>Сценарий без запроса ответа события принимает, а отвечать не берется.</summary>
    [Fact]
    public async Task A_scenario_without_a_reply_request_stays_silent()
    {
        using var stand = new StreamStand();
        var (stream, _) = Cabling(stand);
        var collection = Collection();
        Save(stream, reply: null, collection);

        var clerk = new Clerk();
        using var service = Service(clerk);

        service.Begin();
        await stand.WaitConnectedAsync(Patience);
        await stand.SendAsync(Event(1, "Нужна втулка"));

        await Until(() => clerk.Seen.Count == 1, "сценарий отработал");
        await service.SettleAsync(Patience);

        Assert.Empty(stand.Requests);
        Assert.Single(_db.Constructor.GetRecords(collection));
    }

    // ── Слушание ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Поток слушается, только пока есть включенный сценарий, который заводят его события:
    /// держать открытое соединение, на события которого некому ответить, незачем.
    /// </summary>
    [Fact]
    public async Task A_stream_is_listened_only_while_a_scenario_is_enabled()
    {
        using var stand = new StreamStand();
        var (stream, reply) = Cabling(stand);
        var scenario = Save(stream, reply, Collection(), enabled: false);

        using var service = Service(new Clerk());

        service.Begin();
        await Task.Delay(300);

        Assert.Null(service.State(stream.Id));
        Assert.Equal(0, stand.Connections);

        Cards.EnableScenario(scenario.Id, true, DateTimeOffset.Now);
        service.Sync();

        await stand.WaitConnectedAsync(Patience);
        await Until(() => service.State(stream.Id)?.Connected == true, "поток подключен");

        Cards.EnableScenario(scenario.Id, false, DateTimeOffset.Now);
        service.Sync();

        Assert.Null(service.State(stream.Id));
    }

    /// <summary>
    /// Оборванный поток открывается заново сам, и события после обрыва принимаются. Об обрыве
    /// человеку карточкой не говорят: он проходит сам, а строка о нем лежит в журнале.
    /// </summary>
    [Fact]
    public async Task A_dropped_stream_is_reopened()
    {
        using var stand = new StreamStand();
        var (stream, reply) = Cabling(stand);
        Save(stream, reply, Collection());

        var clerk = new Clerk();
        using var service = Service(clerk);

        service.Begin();
        await stand.WaitConnectedAsync(Patience);
        await Until(() => service.State(stream.Id)?.Connected == true, "поток подключен");

        stand.Drop();

        await stand.WaitConnectedAsync(Patience);
        await Until(() => service.State(stream.Id)?.Connected == true, "поток подключен снова");

        await stand.SendAsync(Event(1, "Нужна втулка"));
        await Until(() => stand.Requests.Count == 1, "ответ на событие");
        await service.SettleAsync(Patience);

        Assert.Equal(2, stand.Connections);

        var rows = Journal(RunChannel.Stream);
        Assert.Contains(rows, r => !r.Ok && r.Explanation.StartsWith("связь потеряна", StringComparison.Ordinal));
        Assert.Equal(2, rows.Count(r => r.Ok && r.Explanation == "поток подключен"));

        lock (_notices) Assert.Empty(_notices);
    }

    /// <summary>
    /// Поток, который не открывается вовсе, назван человеку и журналу ОДИН раз: та сторона,
    /// лежащая всю ночь, иначе оставила бы по строке на каждую попытку.
    /// </summary>
    [Fact]
    public async Task A_stream_that_never_opens_is_reported_once()
    {
        using var stand = new StreamStand(path: "/elsewhere");   // по пути карточки потока нет
        var (stream, reply) = Cabling(stand);
        Save(stream, reply, Collection());

        using var service = Service(new Clerk());

        service.Begin();

        await Until(() => service.State(stream.Id)?.Trouble.Length > 0, "отказ потока");
        await Task.Delay(TimeSpan.FromSeconds(6));   // за это время служба попробует еще раз
        await service.SettleAsync(Patience);

        Assert.False(service.State(stream.Id)!.Connected);
        Assert.StartsWith("нет связи", service.State(stream.Id)!.Trouble, StringComparison.Ordinal);

        Assert.Single(Journal(RunChannel.Stream));
        lock (_notices) Assert.Single(_notices);
    }

}
