using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Extensions;
using SmartBase.Integrations;
using static SmartBase.Controls.Widgets.SettingRows;

namespace SmartBase.Views.Settings;

/// <summary>
/// Вкладка «Сценарии» раздела «Интеграции» (partial SettingsView): что запускается само и когда.
///
/// Список карточек, как у соединений и запросов, — с двумя добавками, которых нет у них.
///
/// <list type="number">
///   <item><b>Тумблер прямо в строке.</b> Выключить обмен — самое частое действие в беспокойный
///   день, и ради него не должно открываться окно.</item>
///   <item><b>Строка «что мешает», посчитанная вживую.</b> Служба такие сценарии пропускает
///   МОЛЧА, чтобы не забить журнал одинаковыми строками каждые полминуты, — и без этой строки
///   человек видел бы включенный сценарий, который ничего не делает, и не знал бы почему.</item>
/// </list>
/// </summary>
public partial class SettingsView
{
    private void BuildIntegrationsScenarios()
    {
        var rows = IntegrationsScenariosTab.Children;
        rows.Clear();

        if (Integrations is null)
        {
            rows.Add(Row("Сценарии", "Список станет доступен, когда откроется база данных."));
            return;
        }

        var registry = Integrations;

        var scenarios = registry.Scenarios();

        rows.Add(ScenarioActions(registry, scenarios));

        if (scenarios.Count == 0)
        {
            rows.Add(Row("Сценариев пока нет",
                "Сценарий — это «когда и что»: расписание и шаги. Самый обычный сценарий — "
                + "запустить запрос и уложить ответ в коллекцию раз в полчаса; раскладку колонок "
                + "он берет у карточки запроса, ту, которую вы однажды настроили кнопкой "
                + "«В коллекцию»."));
            return;
        }

        foreach (var scenario in scenarios)
        {
            rows.Add(ScenarioRow(registry, scenario));
            rows.Add(Separator());
        }
    }

    /// <summary>
    /// Действия над списком: добавить и перенести настройки между машинами. Перенос стоит
    /// рядом с «Добавить», потому что это второй способ завести сценарий — и для того, кто
    /// получил файл от коллеги, единственный.
    /// </summary>
    private Border ScenarioActions(Database.IntegrationsRegistry registry,
                                   IReadOnlyList<Scenario> scenarios)
    {
        var export = RowButton("Выгрузить в файл", () => _ = ExportScenariosAsync(registry, scenarios));
        export.IsEnabled = scenarios.Count > 0;

        var buttons = ButtonLine(
            RowButton("Добавить", () => _ = EditScenarioAsync(Scenario.New())),
            export,
            RowButton("Загрузить из файла", () => _ = ImportScenariosAsync(registry)));

        return new Border { Classes = { "settingRow" }, Child = buttons };
    }

    /// <summary>
    /// Выгрузить все сценарии одним файлом. Вместе с ними уезжают запросы и соединения, на
    /// которые они ссылаются: сценарий без них на той стороне не соберется.
    ///
    /// <b>Секретов в файле нет и быть не может.</b> Сказано это и в самом файле, и человеку —
    /// иначе на той стороне будут неделю искать, почему обмен отвечает «401».
    /// </summary>
    private async Task ExportScenariosAsync(Database.IntegrationsRegistry registry,
                                            IReadOnlyList<Scenario> scenarios)
    {
        if (VisualRoot is not Window win || scenarios.Count == 0) return;

        var now    = DateTimeOffset.Now;
        var bundle = Database.ScenarioTransfer.Export(registry, scenarios, _databases?.Invoke() ?? [],
                                                      Environment.MachineName, now);

        var file = await win.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title               = "Куда выгрузить настройки обмена",
            SuggestedFileName   = ScenarioBundle.FileName(now),
            DefaultExtension    = "json",
            ShowOverwritePrompt = true,
            FileTypeChoices     = [new FilePickerFileType("Файл JSON") { Patterns = ["*.json"] }],
        });

        if (file?.TryGetLocalPath() is not { Length: > 0 } path) return;

        try
        {
            await File.WriteAllTextAsync(path, bundle.ToJson(), new System.Text.UTF8Encoding(false));

            await ConfirmDialog.Info(
                $"Настройки выгружены: {bundle.Summary()}.\n\nФайл: {path}\n\n"
                + "Секретов в нем нет: токены и пароли лежат в защищенном хранилище системы и в "
                + "файл не попадают. На той стороне их впишут заново.").ShowDialog(win);
        }
        catch (Exception error)
        {
            await ConfirmDialog.Info("Выгрузить не удалось.\n\n" + error.Message).ShowDialog(win);
        }
    }

    /// <summary>
    /// Загрузить настройки из файла. Существующее не переписывается, тезка приезжает рядом под
    /// свободным именем, а приехавшие сценарии выключены: файл открывают, чтобы посмотреть, а не
    /// чтобы немедленно начать обмен со сторонней системой.
    /// </summary>
    private async Task ImportScenariosAsync(Database.IntegrationsRegistry registry)
    {
        if (VisualRoot is not Window win) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

        var chosen = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title           = "Файл с настройками обмена",
            AllowMultiple   = false,
            FileTypeFilter  = [new FilePickerFileType("Файл JSON") { Patterns = ["*.json"] }],
        });

        if (chosen.FirstOrDefault()?.TryGetLocalPath() is not { Length: > 0 } path) return;

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path);
        }
        catch (Exception error)
        {
            await ConfirmDialog.Info("Файл не прочитался.\n\n" + error.Message).ShowDialog(win);
            return;
        }

        if (ScenarioBundle.TryParse(text, out var trouble) is not { } bundle)
        {
            await ConfirmDialog.Info("Файл не принят: " + trouble + ".").ShowDialog(win);
            return;
        }

        var ok = await new ConfirmDialog(
            $"Загрузить настройки обмена?\n\nВ файле: {bundle.Summary()}.\n"
            + $"Собран на компьютере «{bundle.Machine}».\n\n"
            + "Уже заведенное не переписывается, а сценарии приедут выключенными — "
            + "включите их, когда проверите.", "Загрузить").ShowDialog<bool>(win);

        if (!ok) return;

        ImportReport report;
        try
        {
            report = Database.ScenarioTransfer.Import(bundle, registry, _databases?.Invoke() ?? [],
                                                      DateTimeOffset.Now);
        }
        catch (Exception error)
        {
            await ConfirmDialog.Info("Загрузить не удалось.\n\n" + error.Message).ShowDialog(win);
            return;
        }

        BuildIntegrationsScenarios();

        var message = report.Summary() + ".";

        if (report.Notes.Count > 0)
            message += "\n\n" + string.Join("\n", report.Notes);

        await ConfirmDialog.Info(message).ShowDialog(win);
    }

    /// <summary>
    /// Имя коллекции, за правками или событиями которой следит сценарий; пусто — база закрыта
    /// или коллекции больше нет. Тогда строка покажет ключ, и это честнее выдуманного имени:
    /// «коллекция №5» сразу говорит, что искать ее сейчас негде.
    /// </summary>
    private string WatchedName(Scenario scenario)
    {
        // У триггера потока на месте коллекции стоит имя потока.
        if (scenario.Trigger.WatchesStream)
            return Integrations?.FindStream(scenario.Trigger.StreamId)?.Name ?? "";

        if (!scenario.Trigger.WatchesCollection) return "";

        var databases = _databases?.Invoke() ?? [];

        return databases.FirstOrDefault(d => d.DbId == scenario.Trigger.DbId) is { } database
            ? database.Constructor.CollectionName(scenario.Trigger.CollectionId)
            : "";
    }

    private Border ScenarioRow(IntegrationsRegistry registry, Scenario scenario)
    {
        var steps = scenario.Steps.Count == 1
            ? "1 шаг"
            : $"{scenario.Steps.Count} " + (scenario.Steps.Count < 5 ? "шага" : "шагов");

        var description = scenario.Trigger.Describe(WatchedName(scenario)) + " · " + steps;

        var facts = new List<string>();

        // «Работает на этом компьютере» — раздел 3. Сценарии лежат в app.db, а он у каждой машины
        // свой: включенный здесь сценарий у коллеги не идет, и наоборот. Пока строки не было,
        // человек считал настройку общей и ждал ночного обмена от выключенного ноутбука.
        facts.Add(scenario.Automatic
            ? "Работает на этом компьютере, пока программа открыта"
            : scenario.Trigger.WatchesStream
                ? "Выключен: события потока его не заводят"
                : "Запускается только вручную");

        // Чем и что сценарий отвечает — в строке: это половина его настройки, и искать ее в
        // окне правки ради ответа «а отвечает ли он вообще» незачем.
        if (scenario.Trigger.Replies)
            facts.Add("отвечает запросом «"
                    + (registry.FindRequest(scenario.Trigger.RequestId)?.Name ?? "не найден") + "»"
                    + (scenario.Trigger.FailText.Length > 0
                        ? $", при сбое — «{scenario.Trigger.FailText}»"
                        : ""));

        if (registry.ScenarioLastRun(scenario.Id) is { } last)
            facts.Add("последний запуск " + last.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));

        // Сколько раз сходил сегодня. Отвечает на «идет ли обмен вообще» лучше, чем момент
        // последнего запуска: «в 07:00» одинаково выглядит и когда сценарий отработал раз, и
        // когда он с тех пор молчит.
        if (RunsToday(scenario) is { } today)
            facts.Add($"сегодня запусков: {today}");

        // Что помешает прямо сейчас — спрашивается вживую у того же кода, которым пользуется
        // служба: два ответа на один вопрос разошлись бы на первой же правке.
        if (ScenarioRunner.Blocker(scenario, registry, _databases?.Invoke() ?? [],
                                   ExtensionRegistry.All) is { } blocker)
            facts.Add("не сработает: " + blocker);

        var extra = string.Join(" · ", facts);

        // Включать сценарий без расписания незачем: он не сработает никогда, а человек будет
        // считать, что обмен идет.
        var toggle = Toggle(scenario.Enabled,
                            enabled: scenario.Trigger.Kind != ScenarioTriggerKind.Manual);

        toggle.IsCheckedChanged += (_, _) =>
        {
            // Тумблер — настройка обмена, а не запуск: он решает, пойдет ли сценарий сам по
            // расписанию, когда за экраном никого нет. Пересборка списка возвращает галку на
            // место, если права не хватило.
            if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup))
            {
                BuildIntegrationsScenarios();
                return;
            }

            // Включение ставит ЯКОРЬ — момент, от которого считается первый запуск. Без него
            // «ежедневно в 07:00», включенное вечером, сработало бы немедленно.
            registry.EnableScenario(scenario.Id, toggle.IsChecked == true, DateTimeOffset.Now);

            // Сценарий потока включили или выключили — поток пора открыть или закрыть сразу.
            _streams?.Sync();

            BuildIntegrationsScenarios();
        };

        var more = new List<(string, Action)>
        {
            // Журнал сценария — не «журнал вообще»: у человека на экране один сценарий, и
            // искать его строки среди чужих он будет воронкой, которую эта кнопка и ставит.
            ("Журнал сценария", () => ShowScenarioRuns(scenario)),
        };

        // Сухой прогон жил только на карточке запроса, а спрашивают его о сценарии: «что уйдет,
        // когда это сработает ночью». Показывается первый исходящий шаг — именно он уносит
        // данные, и именно про него вопрос.
        if (Outgoing(registry, scenario) is { } step)
            more.Add(("Показать, что уйдет", () => _ = PreviewScenarioAsync(registry, step)));

        // Сценарий потока кнопкой не запускается: строку ему приносит событие, а события нет.
        // Кнопка, которая на любое нажатие отвечает отказом, читается как поломка.
        (string, Action)? run = scenario.Trigger.WatchesStream
            ? null
            : ("Запустить", () => _ = RunScenarioAsync(scenario));

        return CardRow(scenario.Name, description, extra,
            edit:   () => _ = EditScenarioAsync(scenario),
            delete: () => _ = DeleteScenarioAsync(scenario),
            first:  run,
            more:   more,
            lead:   toggle);
    }

    // ── Чем карточка отвечает на «идет ли обмен» ──────────────────────────────

    /// <summary>
    /// Сколько раз сценарий сходил сегодня; <c>null</c> — журнала нет и считать нечем.
    ///
    /// Считается по ИМЕНИ сценария — тем же, каким он подписан в строке журнала. Ключ был бы
    /// надежнее, но журнал его не хранит намеренно: сценарий могли удалить, а строки о нем
    /// остаются, и ссылаться им не на что.
    /// </summary>
    private static int? RunsToday(Scenario scenario)
    {
        if (RunJournal.Current is not { } journal) return null;

        var since = DateTimeOffset.Now.Date;
        var only  = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["Scenario"] = new[] { scenario.Name },
        };

        return (int)Math.Min(int.MaxValue,
            journal.Count(new RunFilter(Columns: only, Since: new DateTimeOffset(since))));
    }

    /// <summary>
    /// Открыть журнал, отобранный по ЭТОМУ сценарию. Воронка ставится за человека: искать свои
    /// строки среди чужих он пошел бы туда же и сделал бы то же самое, только руками.
    /// </summary>
    private void ShowScenarioRuns(Scenario scenario)
    {
        _journalPicked.Clear();
        _journalPicked["Scenario"] = [scenario.Name];

        _journalChannel    = null;
        _journalFailedOnly = false;
        _journalOpen       = 0;
        _journalShown      = 0;

        ShowIntegrationTab("journal");
    }

    /// <summary>
    /// Первый шаг-запрос сценария, который уносит данные наружу; <c>null</c> — такого нет.
    /// Сухой прогон показывает именно его: вопрос «что уйдет» — про то, что покидает машину, а
    /// читающие шаги не уносят ничего.
    /// </summary>
    private static IntegrationRequest? Outgoing(IntegrationsRegistry registry, Scenario scenario)
    {
        foreach (var step in scenario.Steps)
        {
            if (step.Kind != ScenarioStepKind.Request) continue;

            if (registry.FindRequest(step.RequestId) is { Outward: true } request) return request;
        }

        return null;
    }

    /// <summary>
    /// Сухой прогон шага сценария — тем же кодом, что и у карточки запроса. Второй сухой прогон
    /// однажды показал бы не то тело, что уйдет, а сухой прогон, показывающий не то, хуже, чем
    /// его отсутствие.
    /// </summary>
    private async Task PreviewScenarioAsync(IntegrationsRegistry registry, IntegrationRequest request) =>
        await PreviewRequestAsync(registry, request, registry.Find(request.ConnectionId));

    // ── Правка ────────────────────────────────────────────────────────────────

    private async Task EditScenarioAsync(Scenario card)
    {
        if (VisualRoot is not Window win || Integrations is null) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

        var registry = Integrations;

        // Пометка «без привязки» считается здесь, а не в окне: окно не знает про реестр, а
        // человеку эта пометка нужна в момент выбора шага, а не после сохранения.
        var requests = registry.Requests()
            .Select(r => (r.Id, r.Name, Bound: registry.Binding(r.Id) is not null))
            .ToList();

        // Отвечать можно только исходящим запросом: ответ — это отправка, и идти ей через замок
        // направлений.
        var replies = registry.Requests()
            .Where(r => r.Outward)
            .Select(r => (r.Id, r.Name))
            .ToList();

        var streams = registry.Streams().Select(s => (s.Id, s.Name)).ToList();

        var result = await new ScenarioDialog(card, requests, Actions(), Places(), streams, replies)
            .ShowDialog<Scenario?>(win);

        if (result is null) return;

        try
        {
            // Момент передается явно — от него реестр ставит якорь сценарию, сохраненному
            // включенным: окно правки заводит новый сценарий, а тумблер в списке трогают уже
            // после.
            registry.SaveScenario(result, DateTimeOffset.Now);
        }
        catch (InvalidOperationException error)
        {
            await ConfirmDialog.Info(error.Message).ShowDialog(win);
            return;
        }

        _streams?.Sync();

        BuildIntegrationsScenarios();
    }

    /// <summary>
    /// Действия, которые можно поставить шагом, — оба вида. Преобразователю нужен текст, и
    /// приносит его шаг «файл из папки»; поставленный без него, он отказывает словами, а живая
    /// строка «не сработает» говорит об этом еще в списке.
    /// </summary>
    private static List<ActionChoice> Actions() =>
    [
        .. ExtensionRegistry.All.SelectMany(
            extension => extension.Actions.Select(a => new ActionChoice(extension.Id, extension.Name, a))),
    ];

    /// <summary>
    /// Коллекции открытых баз — куда действие может писать. Список читается в момент открытия
    /// окна: базы монтируются и отваливаются, и держать его заранее значило бы показывать
    /// вчерашние.
    /// </summary>
    private List<CollectionChoice> Places()
    {
        var places = new List<CollectionChoice>();

        foreach (var database in _databases?.Invoke() ?? [])
            foreach (var collection in database.Constructor.GetAllCollections())
                if (collection.Id is { } id)
                    places.Add(new CollectionChoice(database.DbId, id, database.Name, collection.Name));

        return places;
    }

    private async Task DeleteScenarioAsync(Scenario scenario)
    {
        if (VisualRoot is not Window win || Integrations is null) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

        var confirmed = await new ConfirmDialog($"Удалить сценарий «{scenario.Name}»?")
            .ShowDialog<bool>(win);

        if (confirmed != true) return;

        Integrations.DeleteScenario(scenario.Id);
        _streams?.Sync();

        BuildIntegrationsScenarios();
    }

    // ── Запуск руками ─────────────────────────────────────────────────────────

    /// <summary>
    /// «Запустить» ведет ту же службу, что ходит по расписанию: две реализации одного запуска
    /// разошлись бы на первой же правке, и проверка перестала бы отвечать на вопрос «а что
    /// будет само». Расписание при этом не двигается — оно принадлежит сценарию.
    /// </summary>
    private async Task RunScenarioAsync(Scenario scenario)
    {
        if (VisualRoot is not Window win) return;

        if (_scenarioRun is not { } run)
        {
            await ConfirmDialog.Info("Сценарии сейчас недоступны: база данных еще не открыта.")
                .ShowDialog(win);
            return;
        }

        var progress = new ProgressDialog { Title = "Сценарий" };
        using var cts = new CancellationTokenSource();

        // Отмена останавливает сценарий МЕЖДУ шагами: обещать большее нечестно — начатый запрос
        // к сторонней программе не отменяется по нажатию.
        progress.EnableCancel(cts.Cancel);
        progress.SetSaving($"Сценарий «{scenario.Name}»…");

        var closed = progress.ShowDialog(win);
        var open   = true;

        ScenarioOutcome? outcome = null;
        try
        {
            outcome = await run(scenario, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Отмена — не ошибка: человек передумал, и говорить ему об этом окном незачем.
        }
        catch (Exception failure)
        {
            outcome = ScenarioOutcome.Blocked(failure.Message, scenario.Steps.Count);
        }
        finally
        {
            if (open) { open = false; progress.Done(); await closed; }
        }

        // Отметка о последнем запуске и строка «что мешает» после запуска другие.
        BuildIntegrationsScenarios();

        if (outcome is null) return;

        await ConfirmDialog.Info(Report(scenario, outcome)).ShowDialog(win);
    }

    /// <summary>Итог запуска: что сделал каждый шаг и на чем остановились.</summary>
    private static string Report(Scenario scenario, ScenarioOutcome outcome)
    {
        var text = $"Сценарий «{scenario.Name}»: {outcome.Summary()}.";

        if (outcome.Lines.Count > 0)
            text += "\n\n" + string.Join("\n", outcome.Lines);

        if (!outcome.Ok && !outcome.Started)
            text += "\n\nЭто же мешает и запуску по расписанию: пока причина не уйдет, "
                  + "сценарий пропускает свой срок и догонит его потом.";

        return text;
    }
}
