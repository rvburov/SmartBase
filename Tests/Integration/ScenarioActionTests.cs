using Avalonia.Controls;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Шаг «действие расширения» в сценарии (<see cref="ScenarioRunner"/>): строки предыдущего шага
/// доезжают до расширения, а оболочка не берет на себя ничего, кроме подстановки значений,
/// отдельного подключения и обхода строк.
///
/// Расширения здесь стендовые, а не настоящие, и это не экономия: тест с зашитым расширением
/// проверял бы заодно и его правила, а проверяется договор между сценарием и любым расширением
/// — <c>Extensions/ExtensionAction.cs</c>. Настоящее расширение появляется в последнем тесте, и
/// ровно затем, чтобы показать весь путь целиком.
///
/// Набор идет в общей очереди шлюза: он ходит на сокет за ответом запроса и пишет в журнал
/// запусков, один на приложение.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class ScenarioActionTests : IDisposable
{
    private readonly TempDatabase     _db = new();
    private readonly string           _dir;
    private readonly string           _appDb;
    private readonly string           _otherDb;
    private readonly DatabaseRegistry _registry;

    private readonly List<string> _notices = [];

    public ScenarioActionTests()
    {
        _dir      = Directory.CreateTempSubdirectory("sbact_").FullName;
        _appDb    = Path.Combine(_dir, "app.db");
        _otherDb  = Path.Combine(_dir, "database.db");
        _registry = DatabaseRegistry.Open(_otherDb, _appDb);
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

    private int DbId => _db.Manager.DbId;

    // ── Стендовые расширения ──────────────────────────────────────────────────

    private const string StandId = "stand";

    /// <summary>Расширение-стенд: ничего не умеет, кроме объявленных действий.</summary>
    private sealed class Stand : IExtension
    {
        public Stand(params IExtensionAction[] actions) => Actions = actions;

        public string Id                  => StandId;
        public string Name                => "Стенд";
        public string MarkdownDescription => "";
        public bool   CanRun              => false;

        public IReadOnlyList<IExtensionAction> Actions { get; }

        public Window? CreateWindow() => null;
    }

    /// <summary>Операция: запоминает, с чем ее позвали, и заводит запись.</summary>
    private sealed class Writer : IExtensionAction
    {
        public string              Key         => "write";
        public string              Name        => "Записать заявку";
        public string              Description => "Стендовое действие";
        public ExtensionActionKind Kind        => ExtensionActionKind.Operation;

        public IReadOnlyList<ExtensionActionField> Fields =>
        [
            new("title", "Название"),
            new("note",  "Примечание", Required: false),
        ];

        /// <summary>Куда писать; ставит тест, потому что коллекцию выбирает шаг, а не действие.</summary>
        public int CollectionId { get; init; }

        /// <summary>Чем ответить на N-й вызов; <c>null</c> — записать и согласиться.</summary>
        public Func<int, ActionResult?>? Answer { get; init; }

        public List<(string Title, string Note)> Seen { get; } = [];

        public int SawCollection { get; private set; }

        public ActionResult Run(ActionContext context, ActionInput input)
        {
            Seen.Add((input.Value("title"), input.Value("note")));
            SawCollection = context.CollectionId;

            if (Answer?.Invoke(Seen.Count) is { } answer) return answer;

            // Операции подключение дает оболочка, и оно заполнено всегда; пусто оно бывает
            // только у преобразователя.
            if (context.Repo is not { } repo)
                return ActionResult.Failed("действию не дали подключения к базе данных");

            repo.AddRecord(new DbRecord
            {
                CollectionId = context.CollectionId,
                Title        = input.Value("title"),
            });

            return ActionResult.Done("записано: " + input.Value("title"));
        }
    }

    /// <summary>Преобразователь формата: без текста от предыдущего шага ему нечего разбирать.</summary>
    private sealed class Parser : IExtensionAction
    {
        public string              Key         => "parse";
        public string              Name        => "Разобрать сообщение";
        public string              Description => "Стендовый преобразователь";
        public ExtensionActionKind Kind        => ExtensionActionKind.Transformer;

        public ActionResult Run(ActionContext context, ActionInput input) =>
            ActionResult.Table("[]", 0);
    }

    // ── Обвязка ───────────────────────────────────────────────────────────────

    private static void AllowLoopback(int maxRows = 1000) =>
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal,
            Hosts:           ["127.0.0.1"],
            Networks:        ["127.0.0.0/8"],
            Folders:         [],
            MaxRowsPerRun:   maxRows,
            MaxBytesPerDay:  8 * 1024 * 1024,
            MaxRunsPerHour:  100,
            ValidUntil:      null,
            UserMayAddHosts: false,
            Source:          "тест")));

    /// <summary>Коллекция, куда укладывает ответ шаг «запрос».</summary>
    private int Orders() => SampleData.AddCollection(_db.Constructor, "Заявки").Id!.Value;

    /// <summary>Соединение на тестовый сервер, запрос к нему и привязка к коллекции.</summary>
    private int NewRequest(TinyServer server, int collectionId)
    {
        var connection = Cards.Save(IntegrationConnection.New() with
        {
            Name           = "Учетная система",
            Address        = $"http://127.0.0.1:{server.Port}",
            TimeoutSeconds = 30,
            Retries        = 0,
        });

        var request = Cards.SaveRequest(IntegrationRequest.New() with
        {
            Name         = "Заявки",
            ConnectionId = connection.Id,
            Path         = "/orders",
        });

        Cards.SaveBinding(request.Id, new CollectionBinding(DbId, collectionId,
            [new BindingColumn("Название", null)]));

        return request.Id;
    }

    private static Scenario Of(params ScenarioStep[] steps) =>
        Scenario.New() with { Name = "Заявки в журнал", Steps = steps };

    private Task<ScenarioOutcome> Run(Scenario scenario, params IExtension[] extensions) =>
        ScenarioRunner.RunAsync(scenario, Cards, [_db.Manager], extensions,
                                DateTimeOffset.Now, _notices.Add);

    private string Blocker(Scenario scenario, params IExtension[] extensions) =>
        ScenarioRunner.Blocker(scenario, Cards, [_db.Manager], extensions) ?? "";

    private static string Items(params (string Name, string Project)[] rows) =>
        "[" + string.Join(",", rows.Select(
            r => $"{{\"Название\":\"{r.Name}\",\"Проект\":\"{r.Project}\"}}")) + "]";

    private static Dictionary<string, string> Values(string title, string note = "") =>
        new(StringComparer.Ordinal) { ["title"] = title, ["note"] = note };

    // ── Действие без входящих строк ───────────────────────────────────────────

    /// <summary>
    /// Предыдущий шаг строк не передал — действие выполняется ОДИН раз с постоянными значениями.
    /// Так выглядит сценарий, который сам ничего не приносит: обмену это редкость, но случай
    /// определенный, и вести себя он обязан определенно.
    /// </summary>
    [Fact]
    public async Task Without_incoming_rows_the_action_runs_once()
    {
        var journal = Orders();
        var writer  = new Writer { CollectionId = journal };

        var outcome = await Run(
            Of(ScenarioStep.OfAction(StandId, "write", DbId, journal, Values("Разовая"))),
            new Stand(writer));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Equal([("Разовая", "")], writer.Seen);
        Assert.Equal(journal, writer.SawCollection);
        Assert.Single(_db.Constructor.GetRecords(journal));
    }

    // ── Строки из предыдущего шага ────────────────────────────────────────────

    /// <summary>
    /// Весь путь: ответ чужой программы разобран, лег в коллекцию — и КАЖДАЯ его строка ушла в
    /// расширение с подставленными значениями. Постоянный текст и колонка живут в одном поле.
    /// </summary>
    [Fact]
    public async Task Every_answer_row_reaches_the_extension_with_its_values()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200,
            Items(("Втулка", "Молоток"), ("Кронштейн", "Рубанок"))));

        var orders    = Orders();
        var journal   = SampleData.AddCollection(_db.Constructor, "Журнал").Id!.Value;
        var requestId = NewRequest(server, orders);
        var writer    = new Writer { CollectionId = journal };

        var outcome = await Run(
            Of(ScenarioStep.OfRequest(requestId),
               ScenarioStep.OfAction(StandId, "write", DbId, journal,
                                     new Dictionary<string, string>
                                     {
                                         ["title"] = "{{Название}}",
                                         ["note"]  = "проект {{Проект}}",
                                     })),
            new Stand(writer));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Equal(2, outcome.Done);

        Assert.Equal([("Втулка", "проект Молоток"), ("Кронштейн", "проект Рубанок")], writer.Seen);
        Assert.Equal(2, _db.Constructor.GetRecords(journal).Count);
        Assert.Equal(2, _db.Constructor.GetRecords(orders).Count);
    }

    /// <summary>
    /// Сообщение между шагами не обрывает поток: оно ничего не меняет, и терять из-за него
    /// строки было бы неожиданностью.
    /// </summary>
    [Fact]
    public async Task A_notice_between_the_steps_does_not_lose_the_rows()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items(("Втулка", "Молоток"))));

        var orders    = Orders();
        var journal   = SampleData.AddCollection(_db.Constructor, "Журнал").Id!.Value;
        var requestId = NewRequest(server, orders);
        var writer    = new Writer { CollectionId = journal };

        var outcome = await Run(
            Of(ScenarioStep.OfRequest(requestId),
               ScenarioStep.OfNotice("Заявки пришли"),
               ScenarioStep.OfAction(StandId, "write", DbId, journal,
                                     new Dictionary<string, string> { ["title"] = "{{Название}}" })),
            new Stand(writer));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Equal(["Заявки пришли"], _notices);
        Assert.Equal([("Втулка", "")], writer.Seen);
    }

    // ── Отказы до первой записи ───────────────────────────────────────────────

    /// <summary>
    /// Опечатка в имени колонки останавливает шаг ДО первой записи — и говорит, какие колонки в
    /// строке есть. Найденная на сотой строке, она оставила бы девяносто девять готовых записей.
    /// </summary>
    [Fact]
    public async Task An_unknown_column_stops_the_step_before_the_first_record()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items(("Втулка", "Молоток"))));

        var orders    = Orders();
        var journal   = SampleData.AddCollection(_db.Constructor, "Журнал").Id!.Value;
        var requestId = NewRequest(server, orders);
        var writer    = new Writer { CollectionId = journal };

        var outcome = await Run(
            Of(ScenarioStep.OfRequest(requestId),
               ScenarioStep.OfAction(StandId, "write", DbId, journal,
                                     new Dictionary<string, string> { ["title"] = "{{Названье}}" })),
            new Stand(writer));

        Assert.False(outcome.Ok);
        Assert.Equal(1, outcome.Done);                       // запрос отработал, действие — нет
        Assert.Contains("Названье", outcome.Problem, StringComparison.Ordinal);
        Assert.Contains("Проект",   outcome.Problem, StringComparison.Ordinal);   // что есть в строке
        Assert.Empty(writer.Seen);
        Assert.Empty(_db.Constructor.GetRecords(journal));
    }

    /// <summary>
    /// Обязательное поле без выражения видно ЗАРАНЕЕ — в списке сценариев, а не в отчете через
    /// сутки: оно не зависит от того, что придет в строке.
    /// </summary>
    [Fact]
    public async Task A_required_field_without_an_expression_is_named_in_advance()
    {
        var journal  = Orders();
        var writer   = new Writer { CollectionId = journal };
        var scenario = Of(ScenarioStep.OfAction(StandId, "write", DbId, journal));

        Assert.Contains("Название", Blocker(scenario, new Stand(writer)), StringComparison.Ordinal);

        var outcome = await Run(scenario, new Stand(writer));

        Assert.False(outcome.Ok);
        Assert.Contains("не запускался", outcome.Summary(), StringComparison.Ordinal);
        Assert.Empty(writer.Seen);
    }

    /// <summary>Выражение есть, а значение по нему выходит пустым — тот же отказ, но на строке.</summary>
    [Fact]
    public async Task A_required_field_that_comes_out_empty_is_refused()
    {
        var journal = Orders();
        var writer  = new Writer { CollectionId = journal };

        var outcome = await Run(
            Of(ScenarioStep.OfAction(StandId, "write", DbId, journal, Values("   "))),
            new Stand(writer));

        Assert.False(outcome.Ok);
        Assert.Contains("не заполнено", outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(writer.Seen);
    }

    /// <summary>
    /// Расширения нет, действия нет — обе беды называются одинаково и в списке сценариев, и в
    /// отчете о запуске: два ответа на один вопрос разошлись бы на первой же правке.
    /// </summary>
    [Fact]
    public async Task A_missing_extension_or_action_is_refused_by_name()
    {
        var journal = Orders();
        var stand   = new Stand(new Writer { CollectionId = journal });

        var noExtension = Of(ScenarioStep.OfAction("чужое", "write", DbId, journal, Values("Х")));
        var noAction    = Of(ScenarioStep.OfAction(StandId, "чужое", DbId, journal, Values("Х")));

        Assert.Contains("расширение шага не найдено", Blocker(noExtension, stand), StringComparison.Ordinal);
        Assert.Contains("нет действия",               Blocker(noAction,    stand), StringComparison.Ordinal);

        Assert.False((await Run(noExtension, stand)).Ok);
        Assert.False((await Run(noAction,    stand)).Ok);
    }

    /// <summary>
    /// Преобразователь разбирает то, что пришло текстом, а текст в сценарий приносит шаг «файл
    /// из папки». Поставленный первым, он говорит об этом словами — и говорит еще в списке
    /// сценариев, а не в отчете через сутки.
    /// </summary>
    [Fact]
    public async Task A_transformer_without_text_is_refused_by_name()
    {
        var scenario = Of(ScenarioStep.OfAction(StandId, "parse", 0, 0));

        Assert.Contains("шаг «файл из папки»", Blocker(scenario, new Stand(new Parser())),
                        StringComparison.Ordinal);

        Assert.False((await Run(scenario, new Stand(new Parser()))).Ok);
    }

    /// <summary>Операция без коллекции не выполнится, и сказано об этом заранее.</summary>
    [Fact]
    public void An_operation_without_a_collection_is_refused()
    {
        var journal  = Orders();
        var scenario = Of(ScenarioStep.OfAction(StandId, "write", DbId, 0, Values("Х")));

        Assert.Contains("не выбрана коллекция",
                        Blocker(scenario, new Stand(new Writer { CollectionId = journal })),
                        StringComparison.Ordinal);
    }

    /// <summary>База закрыта — шаг не идет; служба на этом пропустит тик и догонит позже.</summary>
    [Fact]
    public void A_closed_database_stops_the_action_step()
    {
        var journal  = Orders();
        var writer   = new Writer { CollectionId = journal };
        var scenario = Of(ScenarioStep.OfAction(StandId, "write", 777, journal, Values("Х")));

        Assert.Contains("не открыта", Blocker(scenario, new Stand(writer)), StringComparison.Ordinal);
    }

    /// <summary>
    /// Строк больше потолка — отказ ДО первой записи, и ответ в коллекцию тоже не ложится:
    /// цепочка все равно оборвется, а полдела хуже, чем ничего.
    /// </summary>
    [Fact]
    public async Task Too_many_rows_are_refused_before_anything_is_written()
    {
        AllowLoopback(maxRows: StepTable.MaxRows + 100);

        var many = Enumerable.Range(1, StepTable.MaxRows + 1)
                             .Select(i => ($"Строка {i}", "Молоток")).ToArray();

        using var server = new TinyServer(_ => TinyServer.Reply(200, Items(many)));

        var orders    = Orders();
        var journal   = SampleData.AddCollection(_db.Constructor, "Журнал").Id!.Value;
        var requestId = NewRequest(server, orders);
        var writer    = new Writer { CollectionId = journal };

        var outcome = await Run(
            Of(ScenarioStep.OfRequest(requestId),
               ScenarioStep.OfAction(StandId, "write", DbId, journal,
                                     new Dictionary<string, string> { ["title"] = "{{Название}}" })),
            new Stand(writer));

        Assert.False(outcome.Ok);
        Assert.Contains(StepTable.MaxRows.ToString(), outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(writer.Seen);
        Assert.Empty(_db.Constructor.GetRecords(orders));
    }

    // ── Отказ посреди работы ──────────────────────────────────────────────────

    /// <summary>
    /// Первая отказавшая строка останавливает шаг и называет свой номер. Уже сделанное остается
    /// сделанным: операция расширения — не транзакция обмена, и откатывать чужую работу оболочка
    /// не берется.
    /// </summary>
    [Fact]
    public async Task The_first_refused_row_stops_the_step_and_says_which_one()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200,
            Items(("Втулка", "Молоток"), ("Кронштейн", "Рубанок"), ("Ось", "Рубанок"))));

        var orders    = Orders();
        var journal   = SampleData.AddCollection(_db.Constructor, "Журнал").Id!.Value;
        var requestId = NewRequest(server, orders);

        var writer = new Writer
        {
            CollectionId = journal,
            Answer       = n => n == 2 ? ActionResult.Failed("исполнение уже занято") : null,
        };

        var outcome = await Run(
            Of(ScenarioStep.OfRequest(requestId),
               ScenarioStep.OfAction(StandId, "write", DbId, journal,
                                     new Dictionary<string, string> { ["title"] = "{{Название}}" })),
            new Stand(writer));

        Assert.False(outcome.Ok);
        Assert.Contains("строка 2 из 3",       outcome.Problem, StringComparison.Ordinal);
        Assert.Contains("исполнение уже занято", outcome.Problem, StringComparison.Ordinal);
        Assert.Contains("выполнено 1",         outcome.Problem, StringComparison.Ordinal);

        Assert.Equal(2, writer.Seen.Count);                          // до третьей не дошли
        Assert.Single(_db.Constructor.GetRecords(journal));          // первая осталась
    }

    /// <summary>
    /// Расширение обязано возвращать отказ результатом, но обязанность — не гарантия. Упавшее
    /// расширение не должно уносить с собой службу сценариев.
    /// </summary>
    [Fact]
    public async Task A_crashing_extension_does_not_take_the_scenario_with_it()
    {
        var journal = Orders();

        var writer = new Writer
        {
            CollectionId = journal,
            Answer       = _ => throw new InvalidOperationException("расширению поплохело"),
        };

        var outcome = await Run(
            Of(ScenarioStep.OfAction(StandId, "write", DbId, journal, Values("Х"))),
            new Stand(writer));

        Assert.False(outcome.Ok);
        Assert.Contains("сорвалось",           outcome.Problem, StringComparison.Ordinal);
        Assert.Contains("расширению поплохело", outcome.Problem, StringComparison.Ordinal);
    }

    // ── Настоящее расширение ──────────────────────────────────────────────────

    /// <summary>
    /// Весь путь целиком на НАСТОЯЩЕМ расширении: ответ чужой программы становится строкой
    /// журнала регистрации с присвоенным по ГОСТ обозначением, и оболочка при этом не знает о
    /// регистрации ничего.
    ///
    /// Коллекция-журнал собирается здесь же: стенд из тестов расширения лежит в другом проекте,
    /// а имена столбцов — открытая часть расширения, и повторяются они по ней.
    /// </summary>
    [Fact]
    public async Task A_real_registration_runs_as_a_scenario_step()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items(("Втулка", "Молоток"))));

        var orders    = Orders();
        var journal   = RegistryStand.Create(_db.Constructor);
        var requestId = NewRequest(server, orders);

        var outcome = await Run(
            Of(ScenarioStep.OfRequest(requestId),
               ScenarioStep.OfAction(DocRegistryExtension.ExtensionId, RegisterAction.ActionKey,
                                     DbId, journal,
                                     new Dictionary<string, string>
                                     {
                                         ["name"]    = "{{Название}}",
                                         ["org"]     = "АБВГ",
                                         ["eskd"]    = "301121",
                                         ["project"] = "{{Проект}}",
                                     })),
            new DocRegistryExtension());

        Assert.True(outcome.Ok, outcome.Problem);

        var record = Assert.Single(_db.Constructor.GetRecords(journal));
        Assert.Equal("Втулка", record.Title);
        Assert.Contains("АБВГ.301121.001", string.Join(" ", outcome.Lines), StringComparison.Ordinal);
    }

    // ── Отбор строк ───────────────────────────────────────────────────────────

    /// <summary>
    /// «ЕСЛИ» из плана: до действия доходят только подходящие строки. Отброшенные названы
    /// ЧИСЛОМ в отчете — отбор, молча теряющий половину выгрузки, неотличим от поломки.
    /// </summary>
    [Fact]
    public async Task Only_the_rows_that_pass_reach_the_action()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200,
            Items(("Втулка", "Молоток"), ("Кронштейн", ""), ("Шайба", "Рубанок"))));

        var orders    = Orders();
        var journal   = SampleData.AddCollection(_db.Constructor, "Журнал").Id!.Value;
        var requestId = NewRequest(server, orders);
        var writer    = new Writer { CollectionId = journal };

        var outcome = await Run(
            Of(ScenarioStep.OfRequest(requestId),
               ScenarioStep.OfFilter(new ScenarioCondition("Проект", ScenarioTest.Filled)),
               ScenarioStep.OfAction(StandId, "write", DbId, journal, Values("{{Название}}"))),
            new Stand(writer));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Equal([("Втулка", ""), ("Шайба", "")], writer.Seen);
        Assert.Contains("подошло 2 из 3", string.Join(" ", outcome.Lines), StringComparison.Ordinal);
    }

    /// <summary>
    /// Не подошла ни одна строка — сценарий кончается, и это НЕ неудача: данные пришли, просто
    /// делать по ним нечего. Жаловаться тут не на что, а рассказать надо.
    /// </summary>
    [Fact]
    public async Task Nothing_passing_stops_the_scenario_without_a_complaint()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items(("Втулка", ""))));

        var orders    = Orders();
        var journal   = SampleData.AddCollection(_db.Constructor, "Журнал").Id!.Value;
        var requestId = NewRequest(server, orders);
        var writer    = new Writer { CollectionId = journal };

        var outcome = await Run(
            Of(ScenarioStep.OfRequest(requestId),
               ScenarioStep.OfFilter(new ScenarioCondition("Проект", ScenarioTest.Filled)),
               ScenarioStep.OfAction(StandId, "write", DbId, journal, Values("{{Название}}"))),
            new Stand(writer));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Empty(writer.Seen);
        Assert.Equal(2, outcome.Done);   // запрос и отбор, а действие не тронуто
        Assert.Contains("не подошла ни одна", string.Join(" ", outcome.Lines), StringComparison.Ordinal);
    }

    /// <summary>
    /// Колонка, которой в строках нет, ОСТАНАВЛИВАЕТ шаг. Считать ее пустой значило бы отобрать
    /// ноль строк и не сказать почему: опечатка выглядела бы точно так же, как честно не
    /// подошедшие данные.
    /// </summary>
    [Fact]
    public async Task A_column_that_is_not_there_stops_the_filter()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items(("Втулка", "Молоток"))));

        var orders    = Orders();
        var requestId = NewRequest(server, orders);

        var outcome = await Run(
            Of(ScenarioStep.OfRequest(requestId),
               ScenarioStep.OfFilter(new ScenarioCondition("Заказчик", ScenarioTest.Filled))),
            new Stand(new Writer()));

        Assert.False(outcome.Ok);
        Assert.Contains("Заказчик", outcome.Problem, StringComparison.Ordinal);
        Assert.Contains("в строке есть", outcome.Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Отбор первым шагом отбирать нечего, и сказано об этом ЗАРАНЕЕ — в списке сценариев, а не
    /// в отчете через сутки.
    /// </summary>
    [Fact]
    public void A_filter_without_incoming_rows_is_refused_by_name()
    {
        var scenario = Of(ScenarioStep.OfFilter(
            new ScenarioCondition("Проект", ScenarioTest.Filled)));

        Assert.Contains("отбирать нечего", Blocker(scenario, new Stand(new Writer())),
                        StringComparison.Ordinal);
    }
}
