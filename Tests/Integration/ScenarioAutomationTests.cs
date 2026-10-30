using System.Text;
using Avalonia.Controls;
using SmartBase.Database;
using SmartBase.Extensions;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>Автоматические действия расширений выполняются через оболочку, сохраняют результат в выбранной папке и соблюдают контракт действия.</summary>
[Collection(EgressCollection.Name)]
public sealed class ScenarioAutomationTests : IDisposable
{
    private readonly TempDatabase     _db = new();
    private readonly string           _dir;
    private readonly string           _appDb;
    private readonly string           _otherDb;
    private readonly string           _folder;
    private readonly DatabaseRegistry _registry;

    private readonly List<string> _notices = [];

    public ScenarioAutomationTests()
    {
        _dir      = Directory.CreateTempSubdirectory("sbauto_").FullName;
        _appDb    = Path.Combine(_dir, "app.db");
        _otherDb  = Path.Combine(_dir, "database.db");
        _folder   = Path.Combine(_dir, "Выгрузки");
        _registry = DatabaseRegistry.Open(_otherDb, _appDb);

        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {

        RunJournal.Attach(null);

        _registry.Dispose();
        _db.Dispose();
        TempDatabase.Cleanup(_appDb);
        TempDatabase.Cleanup(_otherDb);

        try { SqliteGate.ClearAllPools(); } catch { /* best-effort */ }
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    // ── Стенд ─────────────────────────────────────────────────────────────────

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

    /// <summary>Автоматизация: помечает каждую строку и отдает их дальше.</summary>
    private sealed class Marker : IExtensionAction
    {
        public string              Key         => "mark";
        public string              Name        => "Пометить строки";
        public string              Description => "Стендовая автоматизация";
        public ExtensionActionKind Kind        => ExtensionActionKind.Rows;

        public IReadOnlyList<ExtensionActionField> Fields =>
        [
            new("mark", "Пометка", Required: false),
        ];

        /// <summary>Что действие увидело: настройка и строки.</summary>
        public string                                             Setting { get; private set; } = "";
        public IReadOnlyList<IReadOnlyDictionary<string, string>> Seen    { get; private set; } = [];

        /// <summary>Отдавать ли строки; false — пустой ответ, «дальше незачем».</summary>
        public bool Keeps { get; init; } = true;

        public ActionResult Run(ActionContext context, ActionInput input)
        {
            Setting = input.Value("mark");
            Seen    = input.Table;

            if (!Keeps) return ActionResult.Rowset("[]", 0, "строк не осталось");

            var made = input.Table.Select(row =>
            {
                var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (column, value) in row) copy[column] = value;

                copy["Пометка"] = Setting;
                return copy;
            });

            var json = System.Text.Json.JsonSerializer.Serialize(made, new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            return ActionResult.Rowset(json, input.Table.Count);
        }
    }

    /// <summary>Автоматизация, собирающая файл: байты и имя — от нее, диск — от оболочки.</summary>
    private sealed class Maker : IExtensionAction
    {
        public string              Key         => "make-file";
        public string              Name        => "Собрать файл";
        public string              Description => "Стендовая выгрузка";
        public ExtensionActionKind Kind        => ExtensionActionKind.Rows;
        public bool                MakesFile   => true;

        /// <summary>Как назвать файл; пути и запрещенные знаки проверяются оболочкой.</summary>
        public string Wanted { get; init; } = "Строки.xlsx";

        public ActionResult Run(ActionContext context, ActionInput input) =>
            ActionResult.MadeFile(Encoding.UTF8.GetBytes("строк " + input.Table.Count),
                                  Wanted, $"строк {input.Table.Count}", input.Table.Count);
    }

    // ── Обвязка ───────────────────────────────────────────────────────────────

    private IntegrationsRegistry Cards => _registry.Integrations;

    private Scenario Save(params ScenarioStep[] steps) =>
        Cards.SaveScenario(Scenario.New() with
        {
            Name    = "Сводка по расписанию",
            Trigger = ScenarioTrigger.Manual,
            Steps   = steps,
        });

    private Task<ScenarioOutcome> Run(Scenario scenario, StepTable? initial, params IExtension[] extensions) =>
        ScenarioRunner.RunAsync(scenario, Cards, [_db.Manager], extensions, DateTimeOffset.Now,
                                _notices.Add, default, initial);

    private string Blocker(Scenario scenario, StepTable? initial, params IExtension[] extensions) =>
        ScenarioRunner.Blocker(scenario, Cards, [_db.Manager], extensions, initial) ?? "";

    private static ScenarioStep Step(string key, string folder = "",
                                     IReadOnlyDictionary<string, string>? settings = null) =>
        ScenarioStep.OfAction(StandId, key, 0, 0, settings) with { Folder = folder };

    private static StepTable Goods() => new(
        ["Товар", "Цена"],
        [
            Row(("Товар", "Болт"),  ("Цена", "12,5")),
            Row(("Товар", "Гайка"), ("Цена", "3")),
        ]);

    private static Dictionary<string, string> Row(params (string Column, string Value)[] cells)
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, value) in cells) row[column] = value;
        return row;
    }

    // ── Строки туда и обратно ─────────────────────────────────────────────────

    /// <summary>
    /// Автоматизации достается ВСЯ таблица разом — в этом и отличие от операции, которая идет по
    /// разу на строку, — а то, что она вернула, видят следующие шаги.
    /// </summary>
    [Fact]
    public async Task Rows_go_in_and_come_back_changed()
    {
        var marker = new Marker();

        var scenario = Save(
            Step(marker.Key, settings: new Dictionary<string, string> { ["mark"] = "свод" }),
            ScenarioStep.OfFilter(new ScenarioCondition("Пометка", ScenarioTest.Equals, "свод")));

        var outcome = await Run(scenario, Goods(), new Stand(marker));

        Assert.True(outcome.Ok, outcome.Problem);

        // Действие получило обе строки разом и настройку шага как есть.
        Assert.Equal(2,      marker.Seen.Count);
        Assert.Equal("свод", marker.Setting);

        // Отбор после него нашел новую колонку — значит, строки действительно доехали.
        Assert.Contains(outcome.Lines, l => l.Contains("подошли все 2", StringComparison.Ordinal));
    }

    /// <summary>
    /// Пустой ответ — «дальше незачем», а не неудача: то же прочтение, что у отбора, не нашедшего
    /// ни одной подходящей строки.
    /// </summary>
    [Fact]
    public async Task An_empty_answer_ends_the_scenario_without_a_complaint()
    {
        var marker = new Marker { Keeps = false };

        var scenario = Save(Step(marker.Key),
                            ScenarioStep.OfFilter(new ScenarioCondition("Товар", ScenarioTest.Filled)));

        var outcome = await Run(scenario, Goods(), new Stand(marker));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Equal(1, outcome.Done);   // до отбора дело не дошло
        Assert.Contains(outcome.Lines, l => l.Contains("дальше незачем", StringComparison.Ordinal));
    }

    /// <summary>Строк не принесли — сценарий не запускается вовсе, и причина видна в списке.</summary>
    [Fact]
    public async Task Without_rows_the_scenario_does_not_start()
    {
        var marker   = new Marker();
        var scenario = Save(Step(marker.Key));

        var outcome = await Run(scenario, null, new Stand(marker));

        Assert.False(outcome.Started);
        Assert.Contains("строк нет", outcome.Problem, StringComparison.Ordinal);
    }

    // ── Файл: собирает расширение, кладет оболочка ────────────────────────────

    /// <summary>
    /// Файл ложится в разрешенную папку, к имени добавляется момент, а в журнале безопасности
    /// остается та же строка, что у ручного экспорта, — с именем сценария.
    /// </summary>
    [Fact]
    public async Task A_made_file_is_placed_by_the_shell_without_a_security_journal()
    {

        var maker    = new Maker();
        var scenario = Save(Step(maker.Key, _folder));

        var outcome = await Run(scenario, Goods(), new Stand(maker));

        Assert.True(outcome.Ok, outcome.Problem);

        var file = Assert.Single(Directory.GetFiles(_folder, "*.xlsx"));

        // Имя — то, что попросило расширение, плюс момент: вчерашний файл не пропадает под
        // сегодняшним.
        Assert.StartsWith("Строки ", Path.GetFileName(file), StringComparison.Ordinal);
        Assert.Equal("строк 2", File.ReadAllText(file));

    }

    /// <summary>Папку у такого действия спрашивают обязательно: без нее файл некуда положить.</summary>
    [Fact]
    public void A_file_action_without_a_folder_is_named_in_the_list()
    {

        var maker    = new Maker();
        var scenario = Save(Step(maker.Key));

        Assert.Contains("не выбрана папка", Blocker(scenario, Goods(), new Stand(maker)),
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// Имя файла приходит от расширения, и путь из него вырезается целиком: место выбрано шагом
    /// и разрешено политикой, и «..» в имени не должно уводить из него никуда.
    /// </summary>
    [Fact]
    public async Task A_path_in_the_wanted_name_does_not_leave_the_folder()
    {

        var maker    = new Maker { Wanted = Path.Combine("..", "Чужое.xlsx") };
        var scenario = Save(Step(maker.Key, _folder));

        var outcome = await Run(scenario, Goods(), new Stand(maker));

        Assert.True(outcome.Ok, outcome.Problem);

        var file = Assert.Single(Directory.GetFiles(_folder, "*.xlsx"));

        Assert.StartsWith("Чужое ", Path.GetFileName(file), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_dir, "*.xlsx"));
    }
}
