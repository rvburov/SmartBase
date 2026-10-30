using SmartBase.Database;
using SmartBase.Extensions;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Сообщение без события (<see cref="ActionReplies"/>): расширение пишет человеку первым тем же
/// путем, каким сценарий отвечает на события его действия. Проверяется на настоящем соединении:
/// получатель встает в колонку отправителя, текст — в ответ, перенос строки — сценария, отправка
/// идет через замок направления и оставляет след в обоих журналах.
///
/// Стенд выдуманный, как и у службы потоков: ни одну стороннюю программу он не изображает.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class ActionRepliesTests : IDisposable
{
    private const string Master = "секрет-организации";
    private const string StandId = "stand";

    private readonly TempDatabase     _db = new();
    private readonly string           _dir;
    private readonly DatabaseRegistry _registry;
    private readonly RunJournal       _journal;

    public ActionRepliesTests()
    {
        _dir      = Directory.CreateTempSubdirectory("sbreplies_").FullName;
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

    private ActionReplies Replies => new(Cards);

    /// <summary>Действие, которое ведет переписку: текст и отправитель приходят полями шага.</summary>
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

        public ActionResult Run(ActionContext context, ActionInput input) => ActionResult.Done("");
    }

    private static readonly Clerk Action = new();

    /// <summary>Соединение со стендом, поток на нем и запрос ответа с подтвержденным направлением.</summary>
    private (IntegrationStream Stream, IntegrationRequest Reply) Cabling(StreamStand stand, bool confirm = true)
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
            ItemsPath    = "data",
            KeyField     = "id",
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

    private Scenario Save(IntegrationStream stream, IntegrationRequest? reply, string name = "Заявки по событию",
                          bool enabled = true, string sender = "{{author}}", string replyBreak = "") =>
        Cards.SaveScenario(Scenario.New() with
        {
            Name    = name,
            Enabled = enabled,
            Trigger = ScenarioTrigger.OnStream(stream.Id, reply?.Id ?? 0, "", replyBreak),
            Steps   =
            [
                ScenarioStep.OfAction(StandId, Action.Key, _db.Manager.DbId,
                                      SampleData.AddCollection(_db.Constructor, name).Id!.Value,
                                      new Dictionary<string, string>
                                      {
                                          ["text"]   = "{{body}}",
                                          ["sender"] = sender,
                                      }),
            ],
        }, DateTimeOffset.Now);

    private string? Route(out string problem) => Replies.Route(StandId, Action, "sender", out problem);

    // ── Отправка ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Получатель встает в колонку, из которой шаг берет отправителя, — ее же читает запрос
    /// ответа; строки текста разделены переносом сценария. След — как у ответа на событие, но с
    /// отметкой «сообщение без события».
    /// </summary>
    [Fact]
    public async Task A_message_goes_to_the_recipient_by_the_reply_request()
    {
        using var stand = new StreamStand();
        var (stream, reply) = Cabling(stand);
        Save(stream, reply, replyBreak: "<br>");

        var result = await Replies.SendAsync(StandId, Action, "sender", "1002", "Привет\nвторая строка");

        Assert.True(result.Ok, result.Explanation);

        var sent = Assert.Single(stand.Requests);
        Assert.Equal("PUT",    sent.Method);
        Assert.Equal("/reply", sent.Path);
        Assert.Equal("""{"to": 1002, "message": "Привет<br>вторая строка"}""", sent.Body);

        var run = Assert.Single(_journal.Page(new RunFilter(Channel: RunChannel.Request)));
        Assert.True(run.Ok);
        Assert.Equal(ActionReplies.Mark, run.Step);
    }

    // ── Путь ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_route_is_the_scenario_of_the_action()
    {
        using var stand = new StreamStand();
        var (stream, reply) = Cabling(stand);
        Save(stream, reply);

        Assert.Equal("Заявки по событию", Route(out var problem));
        Assert.Equal("", problem);
    }

    /// <summary>Выключенный сценарий — не путь: им не отвечают и на события.</summary>
    [Fact]
    public async Task A_disabled_scenario_is_no_route()
    {
        using var stand = new StreamStand();
        var (stream, reply) = Cabling(stand);
        Save(stream, reply, enabled: false);

        Assert.Null(Route(out var problem));
        Assert.Contains("нет включенного сценария", problem);

        Assert.False((await Replies.SendAsync(StandId, Action, "sender", "1002", "Привет")).Ok);
        Assert.Empty(stand.Requests);
    }

    /// <summary>Сценарий без запроса ответа — не путь: отвечать ему нечем.</summary>
    [Fact]
    public void A_scenario_without_a_reply_is_no_route()
    {
        using var stand = new StreamStand();
        var (stream, _) = Cabling(stand);
        Save(stream, reply: null);

        Assert.Null(Route(out var problem));
        Assert.Contains("отвечает запросом", problem);
    }

    /// <summary>Два сценария — два пути, и выбрать за человека нельзя: отказ называет оба.</summary>
    [Fact]
    public void Two_scenarios_are_refused_by_name()
    {
        using var stand = new StreamStand();
        var (stream, reply) = Cabling(stand);
        Save(stream, reply, name: "Первый");
        Save(stream, reply, name: "Второй");

        Assert.Null(Route(out var problem));
        Assert.Contains("«Первый»", problem);
        Assert.Contains("«Второй»", problem);
    }

    /// <summary>Отправитель собран из двух колонок — получателя поставить некуда; отказ
    /// называет поле шага словами человека.</summary>
    [Fact]
    public void A_composite_sender_is_refused()
    {
        using var stand = new StreamStand();
        var (stream, reply) = Cabling(stand);
        Save(stream, reply, sender: "{{author}}-{{dept}}");

        Assert.Null(Route(out var problem));
        Assert.Contains("«Отправитель»", problem);
    }

    [Theory]
    [InlineData("{{author}}",     "author")]
    [InlineData("  {{ author }} ", "author")]
    [InlineData("{{author}}-{{dept}}", null)]
    [InlineData("автор",          null)]
    [InlineData("{{}}",           null)]
    [InlineData("",               null)]
    public void Sender_column_is_one_substitution(string expression, string? column) =>
        Assert.Equal(column, ActionReplies.Column(expression));
}
