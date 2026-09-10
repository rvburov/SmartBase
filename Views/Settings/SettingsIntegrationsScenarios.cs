using Avalonia.Controls;
using Avalonia.Layout;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Integrations;

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

        rows.Add(Actions("Добавить", () => _ = EditScenarioAsync(Scenario.New())));

        var scenarios = registry.Scenarios();

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
            rows.Add(new Border { Classes = { "separator", "h" } });
        }
    }

    private Border ScenarioRow(IntegrationsRegistry registry, Scenario scenario)
    {
        var steps = scenario.Steps.Count == 1
            ? "1 шаг"
            : $"{scenario.Steps.Count} " + (scenario.Steps.Count < 5 ? "шага" : "шагов");

        var description = scenario.Trigger.Describe() + " · " + steps;

        var extra = "";

        if (registry.ScenarioLastRun(scenario.Id) is { } last)
            extra = "Последний запуск: " + last.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

        // Что помешает прямо сейчас — спрашивается вживую у того же кода, которым пользуется
        // служба: два ответа на один вопрос разошлись бы на первой же правке.
        if (ScenarioRunner.Blocker(scenario, registry, _databases?.Invoke() ?? []) is { } blocker)
            extra += (extra.Length > 0 ? " · " : "") + "Не сработает: " + blocker;

        var toggle = new ToggleSwitch
        {
            IsChecked         = scenario.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            // Включать сценарий без расписания незачем: он не сработает никогда, а человек
            // будет считать, что обмен идет.
            IsEnabled         = scenario.Trigger.Kind != ScenarioTriggerKind.Manual,
        };

        toggle.IsCheckedChanged += (_, _) =>
        {
            // Включение ставит ЯКОРЬ — момент, от которого считается первый запуск. Без него
            // «ежедневно в 07:00», включенное вечером, сработало бы немедленно.
            registry.EnableScenario(scenario.Id, toggle.IsChecked == true, DateTimeOffset.Now);
            BuildIntegrationsScenarios();
        };

        return CardRow(scenario.Name, description, extra,
            edit:   () => _ = EditScenarioAsync(scenario),
            delete: () => _ = DeleteScenarioAsync(scenario),
            first:  ("Запустить", () => _ = RunScenarioAsync(scenario)),
            lead:   toggle);
    }

    // ── Правка ────────────────────────────────────────────────────────────────

    private async Task EditScenarioAsync(Scenario card)
    {
        if (VisualRoot is not Window win || Integrations is null) return;

        var registry = Integrations;

        // Пометка «без привязки» считается здесь, а не в окне: окно не знает про реестр, а
        // человеку эта пометка нужна в момент выбора шага, а не после сохранения.
        var requests = registry.Requests()
            .Select(r => (r.Id, r.Name, Bound: registry.Binding(r.Id) is not null))
            .ToList();

        var result = await new ScenarioDialog(card, requests).ShowDialog<Scenario?>(win);

        if (result is null) return;

        try
        {
            registry.SaveScenario(result);
        }
        catch (InvalidOperationException error)
        {
            await ConfirmDialog.Info(error.Message).ShowDialog(win);
            return;
        }

        BuildIntegrationsScenarios();
    }

    private async Task DeleteScenarioAsync(Scenario scenario)
    {
        if (VisualRoot is not Window win || Integrations is null) return;

        var confirmed = await new ConfirmDialog($"Удалить сценарий «{scenario.Name}»?")
            .ShowDialog<bool>(win);

        if (confirmed != true) return;

        Integrations.DeleteScenario(scenario.Id);
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
        // к чужой программе не отменяется по нажатию.
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
