using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Шаги «создать запись» и «экспорт выборки» и триггер «календарное событие»
/// (docs/ROADMAP-API.md, шаг 11) — на настоящей базе.
///
/// Проверяются обещания, а не механика: подстановки считаются ДО первой записи, выгрузка не
/// живет без следа в журнале безопасности и без разрешения политики на папку, а календарный
/// триггер заводит сценарий по событию ровно один раз и приносит его строками.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class ScenarioDeedsTests : IDisposable
{
    private readonly TempDatabase     _db = new();
    private readonly string           _dir;
    private readonly string           _appDb;
    private readonly string           _otherDb;
    private readonly string           _folder;
    private readonly DatabaseRegistry _registry;

    private readonly List<string> _notices = [];

    public ScenarioDeedsTests()
    {
        _dir      = Directory.CreateTempSubdirectory("sbdeeds_").FullName;
        _appDb    = Path.Combine(_dir, "app.db");
        _otherDb  = Path.Combine(_dir, "database.db");
        _folder   = Path.Combine(_dir, "Выгрузки");
        _registry = DatabaseRegistry.Open(_otherDb, _appDb);

        Directory.CreateDirectory(_folder);
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

        try { SqliteGate.ClearAllPools(); } catch { /* best-effort */ }
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    // ── Стенд ─────────────────────────────────────────────────────────────────

    private IntegrationsRegistry Cards => _registry.Integrations;

    private void Policy(params string[] folders) =>
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal, [], [], folders, 1000, 1024 * 1024, 100,
            null, false, "тест")));

    /// <summary>«Журнал смен» со свойством «Смена» — сюда пишет шаг «создать запись».</summary>
    private (int Id, int Shift) Journal()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Журнал смен");
        var shift      = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Смена");

        return (collection.Id!.Value, shift.Id!.Value);
    }

    /// <summary>«Заказы» со «Статусом» и тремя записями — их выгружает шаг экспорта.</summary>
    private int Orders()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Заказы");
        var status     = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Статус");

        foreach (var (title, state, at) in new[]
                 {
                     ("Заказ 1", "Оплачен",    1000),
                     ("Заказ 2", "Не оплачен", 2000),
                     ("Заказ 3", "Оплачен",    3000),
                 })
        {
            var record = SampleData.AddRecord(_db.Constructor, collection.Id!.Value, title, at);
            _db.Constructor.SetFieldValue(record.Id!.Value, status.Id!.Value, state);
        }

        return collection.Id!.Value;
    }

    private Scenario Save(ScenarioTrigger? trigger = null, params ScenarioStep[] steps) =>
        Cards.SaveScenario(Scenario.New() with
        {
            Name    = "Дела по расписанию",
            Trigger = trigger ?? ScenarioTrigger.Manual,
            Steps   = steps,
        });

    private Task<ScenarioOutcome> Run(Scenario scenario, StepTable? initial = null) =>
        ScenarioRunner.RunAsync(scenario, Cards, [_db.Manager], [], DateTimeOffset.Now,
                                _notices.Add, default, initial);

    private static StepTable Shifts() => new(
        ["Название", "Начало"],
        [
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["Название"] = "Смена А", ["Начало"] = "13.09.2026 08:00" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["Название"] = "Смена Б", ["Начало"] = "13.09.2026 20:00" },
        ]);

    // ── Шаг «создать запись» ──────────────────────────────────────────────────

    [Fact]
    public async Task A_record_step_creates_one_record_per_incoming_row()
    {
        var (journal, shift) = Journal();

        var scenario = Save(null, ScenarioStep.OfRecord(_db.Manager.DbId, journal,
            new Dictionary<string, string>
            {
                ["Название"] = "{{Название}} — напоминание",
                ["Смена"]    = "{{Начало}}",
            }));

        var outcome = await Run(scenario, Shifts());

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Equal(2, outcome.Rows);

        var records = _db.Constructor.GetRecords(journal);

        Assert.Equal(["Смена А — напоминание", "Смена Б — напоминание"],
                     records.Select(r => r.Title).ToArray());
        Assert.Equal("13.09.2026 08:00",
                     _db.Constructor.GetFieldValues(records[0].Id!.Value)[shift]);
    }

    // ── Шаг «обновить запись» ─────────────────────────────────────────────────

    /// <summary>
    /// Ключ назван — найденная запись ОБНОВЛЯЕТСЯ, а не заводится второй раз. Ради этого шаг и
    /// доделан: раздел 2 обещает «создать/обновить запись», а сценарий умел одно создание, и
    /// «разобрать сообщение → обновить статус» оставлял по дублю на каждый запуск.
    /// </summary>
    [Fact]
    public async Task A_key_updates_the_record_instead_of_making_a_second_one()
    {
        var (journal, shift) = Journal();

        var scenario = Save(null, ScenarioStep.OfRecord(_db.Manager.DbId, journal,
            new Dictionary<string, string>
            {
                ["Название"] = "{{Название}}",
                ["Смена"]    = "{{Начало}}",
            },
            matchField: "Название"));

        Assert.True((await Run(scenario, Shifts())).Ok);
        Assert.Equal(2, _db.Constructor.GetRecords(journal).Count);

        // Второй запуск с теми же названиями и другим временем: записей столько же, время новое.
        var later = new StepTable(["Название", "Начало"],
        [
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["Название"] = "Смена А", ["Начало"] = "14.09.2026 08:00" },
        ]);

        var outcome = await Run(scenario, later);

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Contains("обновлено 1", string.Join(" | ", outcome.Lines), StringComparison.Ordinal);

        var records = _db.Constructor.GetRecords(journal);

        Assert.Equal(2, records.Count);
        Assert.Equal("14.09.2026 08:00",
                     _db.Constructor.GetFieldValues(
                         records.Single(r => r.Title == "Смена А").Id!.Value)[shift]);
    }

    /// <summary>
    /// Записи по ключу не нашлось — заводится новая, если это разрешено. Обычный случай: первый
    /// запуск обмена, когда в коллекции еще пусто.
    /// </summary>
    [Fact]
    public async Task A_key_that_finds_nothing_creates_the_record()
    {
        var (journal, _) = Journal();

        var scenario = Save(null, ScenarioStep.OfRecord(_db.Manager.DbId, journal,
            new Dictionary<string, string> { ["Название"] = "{{Название}}" },
            matchField: "Название"));

        var outcome = await Run(scenario, Shifts());

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Contains("заведено 2", string.Join(" | ", outcome.Lines), StringComparison.Ordinal);
        Assert.Equal(2, _db.Constructor.GetRecords(journal).Count);
    }

    /// <summary>
    /// «Только найденные»: записи нет — строка пропускается, а не заводится. Нужно обмену, где
    /// запись обязана уже существовать: новая там означает, что ключ разошелся, и завести ее
    /// молча — худшее из возможного.
    /// </summary>
    [Fact]
    public async Task Update_only_skips_what_it_did_not_find()
    {
        var (journal, _) = Journal();

        var scenario = Save(null, ScenarioStep.OfRecord(_db.Manager.DbId, journal,
            new Dictionary<string, string> { ["Название"] = "{{Название}}" },
            matchField: "Название", createMissing: false));

        var outcome = await Run(scenario, Shifts());

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Contains("пропущено 2", string.Join(" | ", outcome.Lines), StringComparison.Ordinal);
        Assert.Empty(_db.Constructor.GetRecords(journal));
    }

    /// <summary>
    /// Под ключ подошло НЕСКОЛЬКО записей — строка пропускается и считается отдельной цифрой.
    /// Какую из них править, программа не угадывает: угаданная неверно правка тихо портит чужую
    /// запись, и заметить это нечем.
    /// </summary>
    [Fact]
    public async Task An_ambiguous_key_touches_nothing()
    {
        var (journal, shift) = Journal();

        // Два тезки заведены заранее — так выглядит коллекция, которую вели руками.
        foreach (var _ in Enumerable.Range(0, 2))
            SampleData.AddRecord(_db.Constructor, journal, "Смена А");

        var scenario = Save(null, ScenarioStep.OfRecord(_db.Manager.DbId, journal,
            new Dictionary<string, string>
            {
                ["Название"] = "{{Название}}",
                ["Смена"]    = "{{Начало}}",
            },
            matchField: "Название", createMissing: false));

        var outcome = await Run(scenario, Shifts());

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Contains("неоднозначных 1", string.Join(" | ", outcome.Lines), StringComparison.Ordinal);

        // Ни одна из двух не тронута.
        foreach (var record in _db.Constructor.GetRecords(journal))
            Assert.False(_db.Constructor.GetFieldValues(record.Id!.Value).ContainsKey(shift));
    }

    /// <summary>
    /// Поля-ключа в коллекции нет — шаг встает СЛОВАМИ и до первой записи, как и при опечатке в
    /// обычном поле. Молча отобрать ноль записей здесь нельзя: обмен выглядел бы работающим.
    /// </summary>
    [Fact]
    public async Task An_unknown_key_field_stops_the_step()
    {
        var (journal, _) = Journal();

        var scenario = Save(null, ScenarioStep.OfRecord(_db.Manager.DbId, journal,
            new Dictionary<string, string> { ["Название"] = "{{Название}}" },
            matchField: "Такого поля нет"));

        var outcome = await Run(scenario, Shifts());

        Assert.False(outcome.Ok);
        Assert.Contains("поля-ключа", outcome.Problem!, StringComparison.Ordinal);
        Assert.Empty(_db.Constructor.GetRecords(journal));
    }

    /// <summary>
    /// Две строки с одним ключом в ОДНОЙ пачке — это одна запись, обновленная дважды, а не два
    /// дубля: заведенное в этом же запуске сразу попадает в индекс.
    /// </summary>
    [Fact]
    public async Task Two_rows_with_the_same_key_make_one_record()
    {
        var (journal, shift) = Journal();

        var twice = new StepTable(["Название", "Начало"],
        [
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["Название"] = "Смена А", ["Начало"] = "13.09.2026 08:00" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["Название"] = "Смена А", ["Начало"] = "13.09.2026 20:00" },
        ]);

        var scenario = Save(null, ScenarioStep.OfRecord(_db.Manager.DbId, journal,
            new Dictionary<string, string>
            {
                ["Название"] = "{{Название}}",
                ["Смена"]    = "{{Начало}}",
            },
            matchField: "Название"));

        Assert.True((await Run(scenario, twice)).Ok);

        var records = _db.Constructor.GetRecords(journal);

        Assert.Single(records);
        Assert.Equal("13.09.2026 20:00",
                     _db.Constructor.GetFieldValues(records[0].Id!.Value)[shift]);
    }

    /// <summary>Шагу без строк — одна запись из постоянных значений: «каждый день в 07:00
    /// завести запись планерки» работает без единого запроса.</summary>
    [Fact]
    public async Task A_record_step_without_rows_creates_a_single_record()
    {
        var (journal, _) = Journal();

        var scenario = Save(null, ScenarioStep.OfRecord(_db.Manager.DbId, journal,
            new Dictionary<string, string> { ["Название"] = "Планерка" }));

        var outcome = await Run(scenario);

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Equal("Планерка", Assert.Single(_db.Constructor.GetRecords(journal)).Title);
    }

    /// <summary>Поля нет в коллекции — ни одной записи и отказ, называющий, что есть.</summary>
    [Fact]
    public async Task An_unknown_field_name_stops_the_step_before_the_first_record()
    {
        var (journal, _) = Journal();

        var scenario = Save(null, ScenarioStep.OfRecord(_db.Manager.DbId, journal,
            new Dictionary<string, string> { ["Цех"] = "Первый" }));

        var outcome = await Run(scenario, Shifts());

        Assert.False(outcome.Ok);
        Assert.Contains("«Цех»", outcome.Problem, StringComparison.Ordinal);
        Assert.Contains("Смена", outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(_db.Constructor.GetRecords(journal));
    }

    /// <summary>Опечатка в подстановке — отказ до первой записи, а не пустое поле через неделю.</summary>
    [Fact]
    public async Task An_unknown_column_in_an_expression_stops_the_step()
    {
        var (journal, _) = Journal();

        var scenario = Save(null, ScenarioStep.OfRecord(_db.Manager.DbId, journal,
            new Dictionary<string, string> { ["Название"] = "{{Наименование}}" }));

        var outcome = await Run(scenario, Shifts());

        Assert.False(outcome.Ok);
        Assert.Contains("Наименование", outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(_db.Constructor.GetRecords(journal));
    }

    // ── Шаг «экспорт выборки» ─────────────────────────────────────────────────

    [Fact]
    public async Task An_export_step_writes_the_file_and_leaves_a_trace()
    {
        Policy(_folder);
        var journal = new SecurityJournal(Path.Combine(_dir, "security.db"));
        SecurityJournal.Attach(journal);

        var orders = Orders();

        var scenario = Save(null, ScenarioStep.OfExport(
            _db.Manager.DbId, orders, _folder, "csv",
            [new ScenarioCondition("Статус", ScenarioTest.Equals, "Оплачен")]));

        var outcome = await Run(scenario);

        Assert.True(outcome.Ok, outcome.Problem);

        var file = Assert.Single(Directory.GetFiles(_folder, "*.csv"));
        var text = File.ReadAllText(file);

        Assert.Contains("Заказ 1", text, StringComparison.Ordinal);
        Assert.Contains("Заказ 3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Заказ 2", text, StringComparison.Ordinal);

        // След — тот же, что у ручного экспорта, только с именем сценария.
        var row = journal.Recent().Single();

        Assert.Equal(SecurityEvent.Export,     row.Event);
        Assert.Equal("Дела по расписанию",     row.Scenario);
        Assert.Equal(file,                     row.Address);
        Assert.Equal(2,                        row.Rows);
        Assert.Contains("выгружено сценарием", row.Explanation, StringComparison.Ordinal);

        // Строка отчета называет отсев: молча потерянная половина неотличима от поломки.
        Assert.Contains(outcome.Lines, l => l.Contains("отсеяно условиями 1",
                                                       StringComparison.Ordinal));
    }

    /// <summary>Папка вне политики — сценарий не запускается вовсе, и причина названа еще в
    /// списке: автоматическая выгрузка работает только внутри разрешенных корней (10.11).</summary>
    [Fact]
    public async Task A_folder_outside_the_policy_blocks_the_scenario()
    {
        Policy(Path.Combine(_dir, "Разрешенная"));
        SecurityJournal.Attach(new SecurityJournal(Path.Combine(_dir, "security.db")));

        var scenario = Save(null, ScenarioStep.OfExport(
            _db.Manager.DbId, Orders(), _folder, "csv"));

        var outcome = await Run(scenario);

        Assert.False(outcome.Started);
        Assert.Contains("папка выгрузки", outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    /// <summary>Без журнала безопасности файл не создается: не записалось — не выгружаем.</summary>
    [Fact]
    public async Task Without_the_security_journal_no_file_is_written()
    {
        Policy(_folder);
        SecurityJournal.Attach(null);

        var scenario = Save(null, ScenarioStep.OfExport(
            _db.Manager.DbId, Orders(), _folder, "csv"));

        var outcome = await Run(scenario);

        Assert.False(outcome.Ok);
        Assert.Contains("Журнал безопасности", outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    /// <summary>Помеченное свойство останавливает выгрузку словами — вторым рубежом, как у
    /// отправки: метку могли поставить после того, как сценарий собрали.</summary>
    [Fact]
    public async Task A_labeled_field_stops_the_export()
    {
        Policy(_folder);
        SecurityJournal.Attach(new SecurityJournal(Path.Combine(_dir, "security.db")));

        var orders = Orders();
        var status = _db.Constructor.GetFields(orders).Single(f => f.Name == "Статус");

        _db.Constructor.SetFieldLabel(status.Id!.Value, ConfidentialityLabel.Closed);

        var scenario = Save(null, ScenarioStep.OfExport(
            _db.Manager.DbId, orders, _folder, "csv"));

        var outcome = await Run(scenario);

        Assert.False(outcome.Ok);
        Assert.Contains("Статус", outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    // ── Триггер «календарное событие» ─────────────────────────────────────────

    /// <summary>
    /// Событие заводит сценарий один раз и приходит строками: за 30 минут до смены в «Журнале
    /// смен» появляется напоминание с ее названием и временем. Второй тик не повторяет.
    /// </summary>
    [Fact]
    public async Task A_calendar_event_starts_the_scenario_once_and_brings_its_row()
    {
        Policy();
        RunJournal.Attach(new RunJournal(Path.Combine(_dir, "integrations.db")));

        var events = SampleData.AddCollection(_db.Constructor, "Смены");
        var date   = SampleData.AddCalendarDateField(_db.Constructor, events.Id!.Value);
        var shift  = SampleData.AddRecord(_db.Constructor, events.Id!.Value, "Смена А");

        var (journal, _) = Journal();

        var now   = DateTimeOffset.Now;
        var start = now.ToLocalTime().DateTime.AddMinutes(40);

        _db.Constructor.SetFieldValue(shift.Id!.Value, date.Id!.Value,
            new DateValue(start, start.AddHours(8), false).ToStorage());

        var scenario = Cards.SaveScenario(Scenario.New() with
        {
            Name    = "Напоминание о смене",
            Enabled = true,
            Trigger = ScenarioTrigger.BeforeEvent(_db.Manager.DbId, events.Id!.Value, lead: 30),
            Steps   = [ScenarioStep.OfRecord(_db.Manager.DbId, journal,
                new Dictionary<string, string>
                {
                    ["Название"] = "{{Название}} в {{Начало}}",
                })],
        });

        Cards.EnableScenario(scenario.Id, true, now);

        var service = new ScenarioService(Cards, () => [_db.Manager], [], _notices.Add);

        // Событие в +40 мин, упреждение 30: на тике в +5 мин еще рано, в +15 — пора.
        Assert.Equal(0, await service.TickAsync(now.AddMinutes(5)));
        Assert.Equal(1, await service.TickAsync(now.AddMinutes(15)));

        var made = Assert.Single(_db.Constructor.GetRecords(journal));
        Assert.Equal($"Смена А в {start:dd.MM.yyyy HH:mm}", made.Title);

        // Тот же тик позже — событие уже отработано и не повторяется.
        Assert.Equal(0, await service.TickAsync(now.AddMinutes(16)));
        Assert.Single(_db.Constructor.GetRecords(journal));
    }
}
