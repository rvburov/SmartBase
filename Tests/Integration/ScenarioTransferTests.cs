using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Перенос настроек обмена между машинами (<see cref="ScenarioTransfer"/>,
/// docs/ROADMAP-API.md, раздел 3 и шаг 9).
///
/// Проверяется то, ради чего перенос и сделан, и то, чего он не должен делать никогда:
///
/// <list type="bullet">
///   <item>секрет не уезжает в файл — файл пересылают письмом;</item>
///   <item>связи идут ИМЕНАМИ: ключи строк у каждой машины свои;</item>
///   <item>приехавший сценарий выключен;</item>
///   <item>уже заведенное не переписывается, а тезка приезжает рядом;</item>
///   <item>чего не нашлось — названо в отчете, а не потеряно молча.</item>
/// </list>
/// </summary>
public sealed class ScenarioTransferTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("sbmove_").FullName;

    public void Dispose()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { /* best-effort */ }
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    /// <summary>Отдельная машина: свой app.db, свой реестр.</summary>
    private DatabaseRegistry Machine(string name) =>
        DatabaseRegistry.Open(Path.Combine(_dir, name + ".db"), Path.Combine(_dir, name + "-app.db"));

    /// <summary>Соединение, запрос и сценарий — то, что переносят.</summary>
    private static Scenario Setup(IntegrationsRegistry cards, string scenario = "Заказы за сутки")
    {
        var connection = cards.Save(IntegrationConnection.New() with
        {
            Name           = "Учетная система",
            Address        = "https://api.zavod.local",
            Auth           = ConnectionAuth.Bearer,
            TimeoutSeconds = 30,
        }, secret: "очень-секретный-токен");

        var request = cards.SaveRequest(IntegrationRequest.New() with
        {
            Name         = "Забрать заказы",
            ConnectionId = connection.Id,
            Method       = RequestMethod.Get,
            Path         = "/orders",
        });

        return cards.SaveScenario(new Scenario(0, scenario, false,
            ScenarioTrigger.EveryMinutes(30),
            [new ScenarioStep(ScenarioStepKind.Request, request.Id, "")]));
    }

    // ── Выгрузка ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Секрета в файле нет. Это главное требование к переносу: файл пересылают письмом, кладут
    /// в общую папку и прикладывают к заявке.
    /// </summary>
    [Fact]
    public void The_secret_never_leaves_the_machine()
    {
        using var machine = Machine("один");
        Setup(machine.Integrations);

        var bundle = ScenarioTransfer.Export(machine.Integrations,
                                             machine.Integrations.Scenarios(), [], "ПК17", Now);

        Assert.DoesNotContain("очень-секретный-токен", bundle.ToJson(), StringComparison.Ordinal);
    }

    /// <summary>Вместе со сценарием уезжают его запросы и их соединения — иначе он не соберется.</summary>
    [Fact]
    public void A_scenario_travels_with_its_requests_and_connections()
    {
        using var machine = Machine("один");
        Setup(machine.Integrations);

        var bundle = ScenarioTransfer.Export(machine.Integrations,
                                             machine.Integrations.Scenarios(), [], "ПК17", Now);

        Assert.Single(bundle.Scenarios);
        Assert.Single(bundle.Requests);
        Assert.Single(bundle.Connections);

        Assert.Equal("Учетная система", bundle.Requests[0].Connection);
    }

    // ── Разбор ────────────────────────────────────────────────────────────────

    /// <summary>Полный круг: выгрузили здесь — завелось там, со всеми связями.</summary>
    [Fact]
    public void A_bundle_rebuilds_the_whole_chain_on_another_machine()
    {
        ScenarioBundle bundle;

        using (var first = Machine("один"))
        {
            Setup(first.Integrations);
            bundle = ScenarioTransfer.Export(first.Integrations, first.Integrations.Scenarios(),
                                             [], "ПК17", Now);
        }

        using var second = Machine("два");
        var report = ScenarioTransfer.Import(bundle, second.Integrations, [], Now);

        Assert.Equal(1, report.Connections);
        Assert.Equal(1, report.Requests);
        Assert.Equal(1, report.Scenarios);

        var scenario = Assert.Single(second.Integrations.Scenarios());
        var step     = Assert.Single(scenario.Steps);

        Assert.Equal("Заказы за сутки", scenario.Name);
        Assert.Equal(ScenarioStepKind.Request, step.Kind);

        // Ключ запроса на второй машине СВОЙ — связь восстановлена по имени, а не по номеру.
        var request = second.Integrations.FindRequest(step.RequestId);
        Assert.NotNull(request);
        Assert.Equal("Забрать заказы", request!.Name);
    }

    /// <summary>
    /// Шаг «преобразовать строки» переезжает целиком — вместе с выражениями и их порядком. Ни
    /// баз, ни коллекций он не называет, поэтому на чужой машине работает сразу: считать колонки
    /// из строк можно где угодно.
    /// </summary>
    [Fact]
    public void A_compute_step_travels_with_its_expressions()
    {
        ScenarioBundle bundle;

        using (var first = Machine("один"))
        {
            first.Integrations.SaveScenario(new Scenario(0, "Пересчет", false,
                ScenarioTrigger.EveryMinutes(30),
                [
                    ScenarioStep.OfIntake(@"P:\Обмен\Заказы"),
                    ScenarioStep.OfCompute(
                        new ScenarioFormula("Сумма", "{{Цена}} * {{Количество}}"),
                        new ScenarioFormula("С НДС", "ОКРУГЛ({{Сумма}} * 1.2; 2)")),
                ]));

            bundle = ScenarioTransfer.Export(first.Integrations, first.Integrations.Scenarios(),
                                             [], "ПК17", Now);
        }

        using var second = Machine("два");
        Assert.Equal(1, ScenarioTransfer.Import(bundle, second.Integrations, [], Now).Scenarios);

        var step = Assert.Single(second.Integrations.Scenarios()).Steps[1];

        Assert.Equal(ScenarioStepKind.Compute, step.Kind);
        Assert.Equal(["Сумма", "С НДС"], step.Formulas.Select(f => f.Column).ToArray());
        Assert.Equal("ОКРУГЛ({{Сумма}} * 1.2; 2)", step.Formulas[1].Expression);
    }

    /// <summary>
    /// Приехавший сценарий выключен всегда: файл открывают, чтобы посмотреть, а не чтобы
    /// немедленно начать обмен с чужой системой.
    /// </summary>
    [Fact]
    public void An_imported_scenario_never_starts_by_itself()
    {
        ScenarioBundle bundle;

        using (var first = Machine("один"))
        {
            var scenario = Setup(first.Integrations);
            first.Integrations.EnableScenario(scenario.Id, enabled: true, Now);

            bundle = ScenarioTransfer.Export(first.Integrations, first.Integrations.Scenarios(),
                                             [], "ПК17", Now);
        }

        using var second = Machine("два");
        ScenarioTransfer.Import(bundle, second.Integrations, [], Now);

        Assert.False(second.Integrations.Scenarios().Single().Enabled);
    }

    /// <summary>
    /// Соединение-тезка не переписывается: у здешней карточки есть секрет, которого в файле
    /// нет, и перезапись отняла бы у человека рабочий обмен.
    /// </summary>
    [Fact]
    public void An_existing_connection_is_kept_as_it_was()
    {
        ScenarioBundle bundle;

        using (var first = Machine("один"))
        {
            Setup(first.Integrations);
            bundle = ScenarioTransfer.Export(first.Integrations, first.Integrations.Scenarios(),
                                             [], "ПК17", Now);
        }

        using var second = Machine("два");

        // На второй машине соединение с тем же именем уже есть — и со своим секретом.
        second.Integrations.Save(IntegrationConnection.New() with
        {
            Name           = "Учетная система",
            Address        = "https://свой.адрес.local",
            Auth           = ConnectionAuth.Bearer,
            TimeoutSeconds = 30,
        }, secret: "здешний-секрет");

        var report = ScenarioTransfer.Import(bundle, second.Integrations, [], Now);

        Assert.Equal(0, report.Connections);   // ничего не заведено — взято существующее

        var kept = Assert.Single(second.Integrations.Connections());
        Assert.Equal("https://свой.адрес.local", kept.Address);
        Assert.Equal("здешний-секрет", second.Integrations.Secret(kept.Id));

        Assert.Contains(report.Notes, n => n.Contains("уже заведено", StringComparison.Ordinal));
    }

    /// <summary>
    /// Сценарий-тезка не заменяется, а приезжает рядом: молча заменить чужую настройку, которая
    /// ходит по расписанию, — худшее, что может сделать разбор файла.
    /// </summary>
    [Fact]
    public void A_namesake_scenario_arrives_beside_the_old_one()
    {
        ScenarioBundle bundle;

        using (var first = Machine("один"))
        {
            Setup(first.Integrations);
            bundle = ScenarioTransfer.Export(first.Integrations, first.Integrations.Scenarios(),
                                             [], "ПК17", Now);
        }

        using var second = Machine("два");
        ScenarioTransfer.Import(bundle, second.Integrations, [], Now);
        ScenarioTransfer.Import(bundle, second.Integrations, [], Now);

        var names = second.Integrations.Scenarios().Select(s => s.Name).ToList();

        Assert.Equal(2, names.Count);
        Assert.Contains("Заказы за сутки",     names);
        Assert.Contains("Заказы за сутки (2)", names);
    }

    /// <summary>
    /// Шаг, чьего запроса на этой машине нет, не привозится молча: сценарий, делающий не то,
    /// что у соседа, хуже отсутствующего шага, и об этом сказано в отчете.
    /// </summary>
    [Fact]
    public void A_step_whose_request_is_missing_is_named_rather_than_invented()
    {
        var bundle = new ScenarioBundle(
            ScenarioBundle.SupportedVersion, "", "ПК17", [], [],
            [new BundleScenario("Сирота",
                new BundleTrigger("manual", 0, "00:00", "", ""),
                [new BundleStep("request", "Нет такого", "", "", "", "", "", null, "", null)])]);

        using var machine = Machine("два");
        var report = ScenarioTransfer.Import(bundle, machine.Integrations, [], Now);

        Assert.Equal(0, report.Scenarios);
        Assert.Contains(report.Notes, n => n.Contains("Нет такого", StringComparison.Ordinal));
    }

    // ── Формат ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_file_from_a_newer_program_is_not_guessed_at()
    {
        var text = new ScenarioBundle(ScenarioBundle.SupportedVersion + 1, "", "", [], [],
            [new BundleScenario("Х", new BundleTrigger("manual", 0, "", "", ""), [])]).ToJson();

        Assert.Null(ScenarioBundle.TryParse(text, out var error));
        Assert.Contains("новее", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("это не json")]
    [InlineData("{}")]
    public void Garbage_is_refused_with_a_reason(string text)
    {
        Assert.Null(ScenarioBundle.TryParse(text, out var error));
        Assert.NotEqual("", error);
    }
}
