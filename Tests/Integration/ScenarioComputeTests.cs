using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Шаг «преобразовать строки» в работающем сценарии (docs/ROADMAP-API.md, шаг 12).
///
/// Проверяется не арифметика — ее проверяют юнит-тесты выражений, — а то, ради чего шаг заведен:
/// посчитанная колонка доезжает до следующего шага и ложится в запись, колонки видят друг друга
/// по порядку, а отказ на одной строке останавливает сценарий, не оставив половину работы
/// сделанной.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class ScenarioComputeTests : IDisposable
{
    private readonly TempDatabase     _db = new();
    private readonly string           _dir;
    private readonly string           _appDb;
    private readonly string           _otherDb;
    private readonly DatabaseRegistry _registry;

    private readonly List<string> _notices = [];

    public ScenarioComputeTests()
    {
        _dir      = Directory.CreateTempSubdirectory("sbcompute_").FullName;
        _appDb    = Path.Combine(_dir, "app.db");
        _otherDb  = Path.Combine(_dir, "database.db");
        _registry = DatabaseRegistry.Open(_otherDb, _appDb);
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

    private IntegrationsRegistry Cards => _registry.Integrations;

    /// <summary>«Счета» со свойством «Сумма» — сюда ложится посчитанное.</summary>
    private (int Id, int Total) Invoices()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Счета");
        var total      = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Сумма");

        return (collection.Id!.Value, total.Id!.Value);
    }

    private Scenario Save(params ScenarioStep[] steps) =>
        Cards.SaveScenario(Scenario.New() with
        {
            Name    = "Пересчет и запись",
            Trigger = ScenarioTrigger.Manual,
            Steps   = steps,
        });

    private Task<ScenarioOutcome> Run(Scenario scenario, StepTable? initial = null) =>
        ScenarioRunner.RunAsync(scenario, Cards, [_db.Manager], [], DateTimeOffset.Now,
                                _notices.Add, default, initial);

    /// <summary>Две строки «как из чужой выгрузки»: цена текстом, с запятой и пробелами.</summary>
    private static StepTable Goods() => new(
        ["Товар", "Цена", "Количество"],
        [
            Row(("Товар", "Болт"),  ("Цена", "12,5"), ("Количество", "4")),
            Row(("Товар", "Гайка"), ("Цена", "3"),    ("Количество", "10")),
        ]);

    private static Dictionary<string, string> Row(params (string Column, string Value)[] cells)
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, value) in cells) row[column] = value;
        return row;
    }

    // ── Посчитанное едет дальше ───────────────────────────────────────────────

    /// <summary>
    /// Ради этого шаг и заведен: данные пришли, их пересчитали, они ушли дальше. Колонки считаются
    /// по порядку, и вторая берет посчитанное первой.
    /// </summary>
    [Fact]
    public async Task A_counted_column_reaches_the_next_step()
    {
        var (invoices, total) = Invoices();

        var scenario = Save(
            ScenarioStep.OfCompute(
                new ScenarioFormula("Сумма",  "{{Цена}} * {{Количество}}"),
                new ScenarioFormula("С НДС",  "ОКРУГЛ({{Сумма}} * 1.2; 2)")),
            ScenarioStep.OfRecord(_db.Manager.DbId, invoices,
                new Dictionary<string, string>
                {
                    ["Название"] = "{{Товар}}",
                    ["Сумма"]    = "{{С НДС}}",
                }));

        var outcome = await Run(scenario, Goods());

        Assert.True(outcome.Ok, outcome.Problem);

        var records = _db.Constructor.GetRecords(invoices);

        Assert.Equal(["Болт", "Гайка"], records.Select(r => r.Title).ToArray());
        Assert.Equal("60", _db.Constructor.GetFieldValues(records[0].Id!.Value)[total]);
        Assert.Equal("36", _db.Constructor.GetFieldValues(records[1].Id!.Value)[total]);
    }

    /// <summary>
    /// Шаг заводит не записи, а колонки: считать его строки в итог сценария значило бы удвоить
    /// счет — те же строки посчитаются еще раз на шаге, который заводит записи.
    /// </summary>
    [Fact]
    public async Task Counting_rows_does_not_double_the_total()
    {
        var (invoices, _) = Invoices();

        var scenario = Save(
            ScenarioStep.OfCompute(new ScenarioFormula("Сумма", "{{Цена}} * {{Количество}}")),
            ScenarioStep.OfRecord(_db.Manager.DbId, invoices,
                new Dictionary<string, string> { ["Название"] = "{{Товар}}" }));

        var outcome = await Run(scenario, Goods());

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Equal(2, outcome.Rows);
    }

    /// <summary>Отбор после преобразования работает по ПОСЧИТАННОЙ колонке.</summary>
    [Fact]
    public async Task A_filter_after_the_step_sees_the_new_column()
    {
        var (invoices, _) = Invoices();

        var scenario = Save(
            ScenarioStep.OfCompute(
                new ScenarioFormula("Дорого", "ЕСЛИ({{Цена}} * {{Количество}} > 40; \"да\"; \"нет\")")),
            ScenarioStep.OfFilter(new ScenarioCondition("Дорого", ScenarioTest.Equals, "да")),
            ScenarioStep.OfRecord(_db.Manager.DbId, invoices,
                new Dictionary<string, string> { ["Название"] = "{{Товар}}" }));

        var outcome = await Run(scenario, Goods());

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Equal("Болт", Assert.Single(_db.Constructor.GetRecords(invoices)).Title);
    }

    /// <summary>
    /// Имя, совпавшее с существующей колонкой, заменяет ее значение — так приводят к одному
    /// написанию и обрезают пробелы. Замена названа числом в отчете: молча переписанная колонка
    /// неотличима от посчитанной новой.
    /// </summary>
    [Fact]
    public async Task An_existing_column_is_replaced_and_said_so()
    {
        var scenario = Save(ScenarioStep.OfCompute(
            new ScenarioFormula("Товар", "ВЕРХНИЙ({{Товар}})")));

        var outcome = await Run(scenario, Goods());

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Contains(outcome.Lines, l => l.Contains("колонок заменено 1", StringComparison.Ordinal));
    }

    // ── Отказы ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Опечатка в имени колонки — отказ ДО первой строки, и в нем сказано, какие колонки
    /// пришли на самом деле.
    /// </summary>
    [Fact]
    public async Task An_unknown_column_stops_the_step_before_the_first_row()
    {
        var (invoices, _) = Invoices();

        var scenario = Save(
            ScenarioStep.OfCompute(new ScenarioFormula("Сумма", "{{Стоимость}} * {{Количество}}")),
            ScenarioStep.OfRecord(_db.Manager.DbId, invoices,
                new Dictionary<string, string> { ["Название"] = "{{Товар}}" }));

        var outcome = await Run(scenario, Goods());

        Assert.False(outcome.Ok);
        Assert.Contains("Стоимость", outcome.Problem, StringComparison.Ordinal);
        Assert.Contains("Цена",      outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(_db.Constructor.GetRecords(invoices));
    }

    /// <summary>
    /// Первая отказавшая строка останавливает шаг и называет ее номер: считать дальше значило бы
    /// решить за человека, что пустая цена в одной строке — мелочь.
    /// </summary>
    [Fact]
    public async Task A_bad_row_stops_the_scenario_and_names_its_place()
    {
        var (invoices, _) = Invoices();

        var table = new StepTable(["Товар", "Цена"],
        [
            Row(("Товар", "Болт"),  ("Цена", "10")),
            Row(("Товар", "Гайка"), ("Цена", "почем-то")),
        ]);

        var scenario = Save(
            ScenarioStep.OfCompute(new ScenarioFormula("Итого", "{{Цена}} * 2")),
            ScenarioStep.OfRecord(_db.Manager.DbId, invoices,
                new Dictionary<string, string> { ["Название"] = "{{Товар}}" }));

        var outcome = await Run(scenario, table);

        Assert.False(outcome.Ok);
        Assert.Contains("строка 2 из 2", outcome.Problem, StringComparison.Ordinal);
        Assert.Contains("«Итого»",       outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(_db.Constructor.GetRecords(invoices));
    }

    /// <summary>
    /// Преобразовывать нечего — сценарий не запускается вовсе, и это видно ДО запуска: то же
    /// правило, что у отбора, поставленного первым шагом.
    /// </summary>
    [Fact]
    public async Task Without_incoming_rows_the_scenario_does_not_start()
    {
        var scenario = Save(ScenarioStep.OfCompute(new ScenarioFormula("Итого", "{{Цена}} * 2")));

        var outcome = await Run(scenario);

        Assert.False(outcome.Ok);
        Assert.False(outcome.Started);
        Assert.Contains("преобразовывать нечего", outcome.Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Календарный триггер приносит строки сам — преобразование после него законно: из «Начала»
    /// события считается напоминание.
    /// </summary>
    [Fact]
    public async Task Calendar_rows_are_enough_for_the_step()
    {
        var (invoices, _) = Invoices();

        var events = new StepTable(["Название", "Начало"],
        [
            Row(("Название", "Планерка"), ("Начало", "13.09.2026 08:00")),
        ]);

        var scenario = Save(
            ScenarioStep.OfCompute(new ScenarioFormula("Накануне", "ДАТА({{Начало}}) - 1")),
            ScenarioStep.OfRecord(_db.Manager.DbId, invoices,
                new Dictionary<string, string>
                {
                    ["Название"] = "{{Название}} — подготовка к {{Накануне}}",
                }));

        var outcome = await Run(scenario, events);

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Equal("Планерка — подготовка к 12.09.2026 08:00",
                     Assert.Single(_db.Constructor.GetRecords(invoices)).Title);
    }
}
