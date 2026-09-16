using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Extensions.DocRegistry;
using SmartBase.Integrations;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Регистрация КД в РАБОТАЮЩЕМ сценарии (<see cref="ScenarioRunner"/>): весь путь на настоящем
/// расширении — от ответа сторонней программы и от файла в папке обмена до строки журнала с
/// присвоенным обозначением.
///
/// Договор между сценарием и любым расширением проверяют тесты ядра на стендовых расширениях
/// (<c>ScenarioActionTests</c> и <c>ScenarioIntakeTests</c> в <c>Tests/Integration</c>), а сами
/// действия по отдельности — <see cref="RegisterActionTests"/> и <see cref="ParseMessageActionTests"/>.
/// Здесь — то, что видно только вместе: подстановки из строк предыдущего шага доезжают до
/// регистрации, а оболочка при этом не знает о ней ничего.
///
/// Раньше эти два теста жили в <c>Tests/Integration</c> и привязывали тесты ядра к расширению:
/// удаление расширения роняло бы сборку тестов программы. Здесь они уезжают вместе с ним.
///
/// Шлюз и журнал запусков — глобальное состояние, но общая очередь, как в тестах ядра, здесь не
/// нужна: набор и так идет последовательно (<c>AssemblyInfo.cs</c>).
/// </summary>
public sealed class ScenarioRunTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDatabase     _db  = new("scenario");
    private readonly TempDir          _dir = new("scenario");
    private readonly string           _root;
    private readonly DatabaseRegistry _registry;

    public ScenarioRunTests()
    {
        _root     = Path.Combine(_dir.Path, "Обмен");
        _registry = DatabaseRegistry.Open(Path.Combine(_dir.Path, "database.db"),
                                          Path.Combine(_dir.Path, "app.db"));
    }

    public void Dispose()
    {
        EgressGate.Close();
        RunJournal.Attach(null);
        _registry.Dispose();
        _db.Dispose();    // сбрасывает пул подключений — после него отпущен и app.db в папке
        _dir.Dispose();
    }

    private IntegrationsRegistry Cards => _registry.Integrations;

    private int DbId => _db.Manager.DbId;

    // ── Обвязка ───────────────────────────────────────────────────────────────

    /// <summary>Политика, пускающая запрос на петлевой адрес стенда.</summary>
    private static void AllowLoopback() =>
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal,
            Hosts:           ["127.0.0.1"],
            Networks:        ["127.0.0.0/8"],
            Folders:         [],
            MaxRowsPerRun:   1000,
            MaxBytesPerDay:  8 * 1024 * 1024,
            MaxRunsPerHour:  100,
            ValidUntil:      null,
            UserMayAddHosts: false,
            Source:          "тест")));

    /// <summary>Политика, разрешающая ровно этот корень обмена: шаг спрашивает ее сам.</summary>
    private void AllowRoot() =>
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal,
            Hosts:           [],
            Networks:        [],
            Folders:         [_root],
            MaxRowsPerRun:   1000,
            MaxBytesPerDay:  1024 * 1024,
            MaxRunsPerHour:  100,
            ValidUntil:      null,
            UserMayAddHosts: false,
            Source:          "тест")));

    /// <summary>Соединение на стенд, запрос к нему и привязка ответа к коллекции.</summary>
    private int NewRequest(HttpStand server, int collectionId)
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

    private InboxLayout Layout()
    {
        var layout = InboxFolder.Open(_root, out var explanation);
        Assert.NotNull(layout);
        Assert.Equal("", explanation);
        return layout!;
    }

    /// <summary>Файл во «Входящих», пролежавший достаточно, чтобы его брали.</summary>
    private void Put(string name, string text)
    {
        var path = Path.Combine(Layout().Incoming, name);

        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, (Now - TimeSpan.FromMinutes(1)).UtcDateTime);
    }

    private static Scenario Of(string name, params ScenarioStep[] steps) =>
        Scenario.New() with { Name = name, Steps = steps };

    private Task<ScenarioOutcome> Run(Scenario scenario, DateTimeOffset now) =>
        ScenarioRunner.RunAsync(scenario, Cards, [_db.Manager], [new DocRegistryExtension()],
                                now, _ => { });

    private const string Message = """
        Наименование: Кронштейн
        Проект: Молоток
        Децимальный номер: АБВГ.301121
        ФИО регистрирующего: Иванов Иван Иванович
        """;

    // ── Ответ сторонней программы ─────────────────────────────────────────────────

    /// <summary>
    /// Ответ сторонней программы становится строкой журнала регистрации с присвоенным по ГОСТ
    /// обозначением. Постоянные значения шага (код организации, код ЕСКД) и подстановки из строки
    /// ответа доезжают до регистрации вместе, а оболочка при этом не знает о регистрации ничего.
    /// </summary>
    [Fact]
    public async Task A_real_registration_runs_as_a_scenario_step()
    {
        AllowLoopback();
        using var server = new HttpStand("""[{"Название":"Втулка","Проект":"Молоток"}]""");

        var orders    = _db.Constructor.AddCollection(new DbCollection { Name = "Заявки" }).Id!.Value;
        var journal   = JournalStand.Create(_db.Constructor);
        var requestId = NewRequest(server, orders);

        var outcome = await Run(
            Of("Заявки в журнал",
               ScenarioStep.OfRequest(requestId),
               ScenarioStep.OfAction(DocRegistryExtension.ExtensionId, RegisterAction.ActionKey,
                                     DbId, journal,
                                     new Dictionary<string, string>
                                     {
                                         ["name"]    = "{{Название}}",
                                         ["org"]     = "АБВГ",
                                         ["eskd"]    = "301121",
                                         ["project"] = "{{Проект}}",
                                     })),
            DateTimeOffset.Now);

        Assert.True(outcome.Ok, outcome.Problem);

        var record = Assert.Single(_db.Constructor.GetRecords(journal));
        Assert.Equal("Втулка", record.Title);
        Assert.Contains("АБВГ.301121.001", string.Join(" ", outcome.Lines), StringComparison.Ordinal);
    }

    // ── Файл в папке обмена ───────────────────────────────────────────────────

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

        var journalId = JournalStand.Create(_db.Constructor);

        var outcome = await Run(
            Of("Регистрация из папки",
               ScenarioStep.OfIntake(_root),
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
            Now);

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
