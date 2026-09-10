using SmartBase.Database;
using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Шаг «файл из папки» и весь путь сообщения: файл в папке → разбор → регистрация → квитанция
/// отправителю (docs/ROADMAP-API.md, раздел 9, «как обкатать», шаг 1 — без сети вообще).
///
/// Проверяется не файловый протокол — он свой у <see cref="InboxFolder"/> и закрыт
/// <c>InboxFolderTests</c>, — а то, что добавляет сценарий: единица работы здесь СООБЩЕНИЕ
/// целиком, ответ отправителю пишется по итогу всех шагов, а пустая папка не оставляет следов.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class ScenarioIntakeTests : IDisposable
{
    private readonly TempDatabase     _db = new();
    private readonly string           _dir;
    private readonly string           _root;
    private readonly string           _appDb;
    private readonly string           _otherDb;
    private readonly string           _journalDb;
    private readonly DatabaseRegistry _registry;

    private readonly List<string> _notices = [];

    public ScenarioIntakeTests()
    {
        _dir       = Directory.CreateTempSubdirectory("sbtake_").FullName;
        _root      = Path.Combine(_dir, "Обмен");
        _appDb     = Path.Combine(_dir, "app.db");
        _otherDb   = Path.Combine(_dir, "database.db");
        _journalDb = Path.Combine(_dir, "integrations.db");
        _registry  = DatabaseRegistry.Open(_otherDb, _appDb);
    }

    public void Dispose()
    {
        EgressGate.Close();
        RunJournal.Attach(null);
        _registry.Dispose();
        _db.Dispose();
        TempDatabase.Cleanup(_appDb);
        TempDatabase.Cleanup(_otherDb);
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { /* best-effort */ }
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private IntegrationsRegistry Cards => _registry.Integrations;

    private int DbId => _db.Manager.DbId;

    // ── Обвязка ───────────────────────────────────────────────────────────────

    /// <summary>Политика, разрешающая ровно этот корень обмена: шаг спрашивает ее сам.</summary>
    private void AllowRoot(string? root = null) =>
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal,
            Hosts:           [],
            Networks:        [],
            Folders:         [root ?? _root],
            MaxRowsPerRun:   1000,
            MaxBytesPerDay:  1024 * 1024,
            MaxRunsPerHour:  100,
            ValidUntil:      null,
            UserMayAddHosts: false,
            Source:          "тест")));

    private InboxLayout Layout()
    {
        var layout = InboxFolder.Open(_root, out var explanation);
        Assert.NotNull(layout);
        Assert.Equal("", explanation);
        return layout!;
    }

    /// <summary>Файл во «Входящих», пролежавший достаточно, чтобы его брали.</summary>
    private string Put(string name, string text)
    {
        var layout = Layout();
        var path   = Path.Combine(layout.Incoming, name);

        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, (Now - TimeSpan.FromMinutes(1)).UtcDateTime);

        return path;
    }

    private static Scenario Of(params ScenarioStep[] steps) =>
        Scenario.New() with { Name = "Регистрация из папки", Steps = steps };

    private Task<ScenarioOutcome> Run(Scenario scenario, params IExtension[] extensions) =>
        ScenarioRunner.RunAsync(scenario, Cards, [_db.Manager], extensions, Now, _notices.Add);

    private string Blocker(Scenario scenario, params IExtension[] extensions) =>
        ScenarioRunner.Blocker(scenario, Cards, [_db.Manager], extensions) ?? "";

    private RunJournal Journal()
    {
        var journal = new RunJournal(_journalDb);
        RunJournal.Attach(journal);
        return journal;
    }

    /// <summary>Стендовое расширение: одно действие, которое запоминает поданный текст.</summary>
    private sealed class Stand(IExtensionAction action) : IExtension
    {
        public string Id                  => "stand";
        public string Name                => "Стенд";
        public string MarkdownDescription => "";
        public bool   CanRun              => false;

        public IReadOnlyList<IExtensionAction> Actions { get; } = [action];

        public Avalonia.Controls.Window? CreateWindow() => null;
    }

    /// <summary>Преобразователь: запоминает текст и отдает его одной строкой таблицы.</summary>
    private sealed class Echo : IExtensionAction
    {
        public string              Key         => "echo";
        public string              Name        => "Повторить";
        public string              Description => "Стендовый преобразователь";
        public ExtensionActionKind Kind        => ExtensionActionKind.Transformer;

        public string Seen { get; private set; } = "";

        /// <summary>Чем ответить; <c>null</c> — отдать текст строкой.</summary>
        public ActionResult? Answer { get; init; }

        public ActionResult Run(ActionContext context, ActionInput input)
        {
            Seen = input.Text;

            return Answer ?? ActionResult.Table(
                $"[{{\"Текст\":{System.Text.Json.JsonSerializer.Serialize(input.Text)}}}]", 1);
        }
    }

    private const string Message = """
        Наименование: Кронштейн
        Проект: Молоток
        Децимальный номер: АБВГ.301121
        ФИО регистрирующего: Иванов Иван Иванович
        """;

    // ── Пустая папка ──────────────────────────────────────────────────────────

    /// <summary>
    /// Брать нечего — сценарий кончается ТИХО: ни строки в журнале, ни следа в отчете. Папка,
    /// проверяемая каждые пять минут, иначе оставила бы к утру три сотни одинаковых строк, и
    /// настоящую среди них потом не найти.
    /// </summary>
    [Fact]
    public async Task An_empty_folder_leaves_no_trace()
    {
        AllowRoot();
        Layout();                      // папки созданы, но пусты
        var journal = Journal();

        var outcome = await Run(Of(ScenarioStep.OfIntake(_root)), new Stand(new Echo()));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.True(outcome.Idle);
        Assert.Empty(journal.Recent());
        Assert.Equal("делать было нечего", outcome.Summary());
    }

    /// <summary>
    /// Свежий файл не берется: отправитель мог его еще не дописать. Тот же тихий исход —
    /// подождем следующего запуска.
    /// </summary>
    [Fact]
    public async Task A_file_just_written_is_left_alone()
    {
        AllowRoot();
        var layout = Layout();
        var path   = Path.Combine(layout.Incoming, "kd-001.txt");

        File.WriteAllText(path, Message);
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime);   // только что

        var outcome = await Run(Of(ScenarioStep.OfIntake(_root)), new Stand(new Echo()));

        Assert.True(outcome.Idle);
        Assert.True(File.Exists(path));
    }

    // ── Файл доезжает до шагов ────────────────────────────────────────────────

    /// <summary>
    /// Текст файла доезжает до преобразователя целиком, файл уезжает в «Обработанные», а рядом
    /// ложится ответ отправителю: у папки-приемника нет обратной связи по своей природе.
    /// </summary>
    [Fact]
    public async Task The_file_reaches_the_next_step_and_gets_an_answer()
    {
        AllowRoot();
        Put("kd-001.txt", Message);

        var echo = new Echo();

        var outcome = await Run(
            Of(ScenarioStep.OfIntake(_root), ScenarioStep.OfAction("stand", "echo", 0, 0)),
            new Stand(echo));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Contains("Кронштейн", echo.Seen, StringComparison.Ordinal);

        var layout = Layout();
        Assert.Empty(Directory.GetFiles(layout.Incoming));
        Assert.Single(Directory.GetFiles(Path.Combine(layout.Processed, "2026-09")));

        var receipt = InboxReceipt.TryParse(
            File.ReadAllText(Path.Combine(layout.Receipts, "kd-001.json")));

        Assert.NotNull(receipt);
        Assert.True(receipt!.Accepted);
        Assert.Equal("kd-001.txt", receipt.File);
    }

    /// <summary>
    /// Сценарий не дошел до конца — файл уезжает в «Ошибки», а причина едет отправителю. Иначе
    /// он остался бы во «Входящих» навсегда и спотыкался бы о себя каждый запуск.
    /// </summary>
    [Fact]
    public async Task A_failed_scenario_answers_with_the_reason()
    {
        AllowRoot();
        Put("kd-002.txt", Message);

        var echo = new Echo { Answer = ActionResult.Failed("это не то сообщение") };

        var outcome = await Run(
            Of(ScenarioStep.OfIntake(_root), ScenarioStep.OfAction("stand", "echo", 0, 0)),
            new Stand(echo));

        Assert.False(outcome.Ok);

        var layout = Layout();
        Assert.Single(Directory.GetFiles(layout.Failed));
        Assert.Empty(Directory.GetFiles(layout.Incoming));

        var receipt = InboxReceipt.TryParse(
            File.ReadAllText(Path.Combine(layout.Receipts, "kd-002.json")));

        Assert.False(receipt!.Accepted);
        Assert.Contains("это не то сообщение", receipt.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// За один запуск берется ОДИН файл: единица работы сценария — сообщение целиком, одна
    /// цепочка шагов и одна квитанция. Второй дождется следующего запуска.
    /// </summary>
    [Fact]
    public async Task One_run_takes_one_file()
    {
        AllowRoot();
        Put("kd-003.txt", Message);
        Put("kd-004.txt", Message);

        var scenario = Of(ScenarioStep.OfIntake(_root), ScenarioStep.OfAction("stand", "echo", 0, 0));

        Assert.True((await Run(scenario, new Stand(new Echo()))).Ok);
        Assert.Single(Directory.GetFiles(Layout().Incoming));

        Assert.True((await Run(scenario, new Stand(new Echo()))).Ok);
        Assert.Empty(Directory.GetFiles(Layout().Incoming));
    }

    // ── Повтор ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Отправитель при обрыве пришлет файл дважды. Прежняя квитанция НЕ переписывается —
    /// перечитав ее, он обязан получить тот же ответ, — а файл убирается из «Входящих».
    /// Строка журнала при этом пишется: повтор — единственный случай, когда файл был, а работы
    /// не случилось, и без строки это выглядело бы как пропажа сообщения.
    /// </summary>
    [Fact]
    public async Task A_repeat_is_answered_by_the_old_receipt()
    {
        AllowRoot();
        var layout = Layout();
        var scenario = Of(ScenarioStep.OfIntake(_root), ScenarioStep.OfAction("stand", "echo", 0, 0));

        Put("kd-005.txt", Message);
        await Run(scenario, new Stand(new Echo()));

        var first = File.ReadAllText(Path.Combine(layout.Receipts, "kd-005.json"));

        var journal = Journal();
        Put("kd-005.txt", "Наименование: Другое");

        var outcome = await Run(scenario, new Stand(new Echo()));

        Assert.True(outcome.Idle);
        Assert.Equal(first, File.ReadAllText(Path.Combine(layout.Receipts, "kd-005.json")));
        Assert.Empty(Directory.GetFiles(layout.Incoming));

        var row = Assert.Single(journal.Recent());
        Assert.Equal(RunChannel.Inbox, row.Channel);
        Assert.Contains("повтор", row.Explanation, StringComparison.Ordinal);
    }

    // ── Что не пропускается ───────────────────────────────────────────────────

    /// <summary>
    /// Папка обмена — такая же дверь, как сетевой адрес, и открыть ее себе самому пользователь
    /// не может: разрешает политика организации.
    /// </summary>
    [Fact]
    public void A_folder_outside_the_policy_is_named_by_the_blocker()
    {
        AllowRoot(Path.Combine(_dir, "чужая"));

        Assert.Contains("папка обмена", Blocker(Of(ScenarioStep.OfIntake(_root)), new Stand(new Echo())),
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// Два шага «файл из папки» взяли бы два файла, а квитанция у сценария одна: на два файла ее
    /// не разделить, и один отправитель остался бы без ответа.
    /// </summary>
    [Fact]
    public async Task Two_intake_steps_are_refused()
    {
        AllowRoot();
        Put("kd-006.txt", Message);

        var outcome = await Run(
            Of(ScenarioStep.OfIntake(_root), ScenarioStep.OfIntake(_root)), new Stand(new Echo()));

        Assert.False(outcome.Ok);
        Assert.Contains("два шага", outcome.Problem, StringComparison.Ordinal);
    }

    // ── Весь путь ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Пилот целиком, без сети вообще.</b> Файл кладут в папку — сценарий разбирает
    /// сообщение, регистрирует документ по ГОСТ и отвечает отправителю квитанцией с
    /// присвоенным обозначением.
    ///
    /// Ни одной строчки кода про регистрацию в оболочке при этом нет, а расширение ничего не
    /// знает ни про папку, ни про сценарий: какая колонка сообщения в какое поле ложится,
    /// решает настройка шага. Это и есть критерий приемки из плана — при переходе к другому
    /// каналу меняется канал, а не расширение и не сопоставление.
    /// </summary>
    [Fact]
    public async Task A_message_in_a_folder_becomes_a_registered_document()
    {
        AllowRoot();
        Put("kd-100.txt", Message);

        var journalId = RegistryStand.Create(_db.Constructor);

        var outcome = await Run(
            Of(ScenarioStep.OfIntake(_root),
               ScenarioStep.OfAction(DocRegistryExtension.ExtensionId, ParseMessageAction.ActionKey,
                                     0, 0),
               ScenarioStep.OfAction(DocRegistryExtension.ExtensionId, RegisterAction.ActionKey,
                                     DbId, journalId,
                                     new Dictionary<string, string>
                                     {
                                         ["name"]    = "{{Наименование}}",
                                         ["org"]     = $"{{{{{ParseMessageAction.OrgColumn}}}}}",
                                         ["eskd"]    = $"{{{{{ParseMessageAction.EskdColumn}}}}}",
                                         ["project"] = "{{Проект}}",
                                         ["fio"]     = "{{ФИО регистрирующего}}",
                                     })),
            new DocRegistryExtension());

        Assert.True(outcome.Ok, outcome.Problem);

        var record = Assert.Single(_db.Constructor.GetRecords(journalId));
        Assert.Equal("Кронштейн", record.Title);

        var receipt = InboxReceipt.TryParse(
            File.ReadAllText(Path.Combine(Layout().Receipts, "kd-100.json")));

        Assert.True(receipt!.Accepted);
        Assert.Contains("АБВГ.301121.001", receipt.Explanation, StringComparison.Ordinal);
        Assert.Equal(1, receipt.Created);
    }
}
