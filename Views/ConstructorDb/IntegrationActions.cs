using Avalonia.Controls;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// Точки вызова обмена в разделе данных (docs/ROADMAP-API.md, шаг 6): пункт «Действия» в
/// контекстном меню таблицы, такой же пункт в меню коллекции в сайдбаре и кнопка в нижней
/// полосе открытой записи.
///
/// Смысл шага — «интеграции приходят туда, где данные»: человек не должен идти в настройки, чтобы
/// отправить заказ. Собрано в одном месте, потому что точек вызова три, а список и запуск у них
/// обязаны быть одни: разойдись они — и в меню таблицы оказался бы обмен, которого нет в сайдбаре.
///
/// <b>Что попадает в список.</b> Исходящие запросы, чья привязка указывает на ЭТУ коллекцию, — их
/// можно звать и для выделенных записей. Ручные сценарии — только там, где охват коллекция
/// целиком: у сценария нет понятия «эти три записи», и предлагать его на строке значило бы
/// обещать то, чего не будет.
///
/// <b>Отправка спрашивает подтверждение.</b> Не из вежливости: в настройках кнопку «В коллекцию»
/// нажимает тот, кто этот обмен настроил, а здесь — любой, кто работает с таблицей. Он должен
/// увидеть, СКОЛЬКО записей и КУДА уходит, до того как это случится.
/// </summary>
public static class IntegrationActions
{
    /// <summary>
    /// Собрать список действий для коллекции; пустой — показывать нечего, и пункт меню не
    /// появляется вовсе.
    /// </summary>
    /// <param name="owner">Окно для диалогов.</param>
    /// <param name="registry">Реестр интеграций; <c>null</c> — обмен недоступен.</param>
    /// <param name="databases">Открытые базы.</param>
    /// <param name="scenarioRun">Запуск сценария — та же служба, что ходит по расписанию.</param>
    /// <param name="dbId">База открытой коллекции.</param>
    /// <param name="collectionId">Коллекция.</param>
    /// <param name="scenarios">Показывать ли сценарии (охват — вся коллекция).</param>
    public static IReadOnlyList<DataAction> Build(
        Window                                                      owner,
        IntegrationsRegistry?                                       registry,
        IReadOnlyList<DatabaseManager>                              databases,
        Func<Scenario, CancellationToken, Task<ScenarioOutcome>>?   scenarioRun,
        int                                                         dbId,
        int                                                         collectionId,
        bool                                                        scenarios)
    {
        if (registry is null || dbId <= 0 || collectionId <= 0) return [];

        var actions = new List<DataAction>();

        foreach (var request in registry.Requests())
        {
            if (!request.Outward) continue;

            if (registry.Outgoing(request.Id) is not { } binding) continue;
            if (binding.DbId != dbId || binding.CollectionId != collectionId) continue;

            actions.Add(new DataAction(
                $"Отправить: {request.Name}",
                records => SendAsync(owner, registry, databases, request, records)));
        }

        if (scenarios && scenarioRun is { } run)
            foreach (var scenario in registry.Scenarios())
            {
                if (scenario.Trigger.Kind != ScenarioTriggerKind.Manual) continue;
                if (!Touches(registry, scenario, dbId, collectionId)) continue;

                actions.Add(new DataAction(
                    $"Запустить: {scenario.Name}", _ => RunScenarioAsync(owner, scenario, run)));
            }

        return actions;
    }

    /// <summary>
    /// Имеет ли сценарий отношение к этой коллекции. Отношение — это привязка любого его шага:
    /// входящая (ответ ложится сюда) или исходящая (записи уходят отсюда). Показывать в меню
    /// коллекции все сценарии подряд значило бы предлагать человеку обмен, который к его данным
    /// не относится.
    /// </summary>
    private static bool Touches(IntegrationsRegistry registry, Scenario scenario,
                                int dbId, int collectionId) =>
        scenario.Steps.Any(step =>
            step.Kind == ScenarioStepKind.Request
         && ((registry.Binding(step.RequestId) is { } incoming
              && incoming.DbId == dbId && incoming.CollectionId == collectionId)
          || (registry.Outgoing(step.RequestId) is { } outgoing
              && outgoing.DbId == dbId && outgoing.CollectionId == collectionId)));

    // ── Отправка ──────────────────────────────────────────────────────────────

    private static async Task SendAsync(Window                         owner,
                                        IntegrationsRegistry           registry,
                                        IReadOnlyList<DatabaseManager> databases,
                                        IntegrationRequest             request,
                                        IReadOnlyList<int>             records)
    {
        if (registry.Find(request.ConnectionId) is not { } connection)
        {
            await ConfirmDialog.Info(
                    $"У запроса «{request.Name}» не найдено соединение — отправлять некуда.")
                .ShowDialog(owner);
            return;
        }

        // Тело собирается ДО окна с полосой: отказ («поле помечено», «записей больше потолка»)
        // должен прийти сразу, а не после прогресса, который никуда не шел.
        var (payload, cannot) = OutgoingSource.Build(registry, request, databases, records);

        if (cannot is { Length: > 0 })
        {
            await ConfirmDialog.Info($"Отправить нельзя.\n\n{cannot}").ShowDialog(owner);
            return;
        }

        if (payload is null or { Rows: 0 })
        {
            await ConfirmDialog.Info("Отправлять нечего: записей не набралось.").ShowDialog(owner);
            return;
        }

        // Число — ПОСЛЕ существительного, как и везде в программе («Полей с метками: 0»). Ставь
        // его перед — и пришлось бы склонять «записи» в трех формах ради одной строки, а на
        // «1 выделенных записей» спотыкается глаз.
        var scope = records.Count > 0 ? "выделенных записей" : "записей коллекции";

        var confirmed = await new ConfirmDialog(
                $"Отправить наружу {scope}: {payload.Rows:N0}?\n\n"
                + $"Запрос: {request.Name}\n"
                + $"Адрес: {connection.Address}{request.Path}\n"
                + $"Поля: {payload.Fields}",
                confirmText: "Отправить")
            .ShowDialog<bool>(owner);

        if (!confirmed) return;

        var progress = new ProgressDialog { Title = "Отправка" };
        using var cts = new CancellationTokenSource();
        progress.EnableCancel(cts.Cancel);
        progress.SetSaving($"Отправляю «{request.Name}»…");

        var closed = progress.ShowDialog(owner);
        var open   = true;

        RunOutcome? outcome = null;
        try
        {
            outcome = await RequestLauncher.SendAsync(
                registry, connection, request, payload, DbOf(databases, request, registry),
                DateTimeOffset.UtcNow, DateTimeOffset.Now, ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Отмена — не ошибка: человек передумал.
        }
        finally
        {
            if (open) { open = false; progress.Done(); await closed; }
        }

        if (outcome is null) return;

        await ConfirmDialog.Info(outcome.Ok
                ? $"Отправлено записей: {payload.Rows:N0}."
                : $"Запрос «{request.Name}» не прошел.\n\n{outcome.Explanation}")
            .ShowDialog(owner);
    }

    /// <summary>База, которую задевает обмен, — для строки журнала запусков.</summary>
    private static int DbOf(IReadOnlyList<DatabaseManager> databases, IntegrationRequest request,
                            IntegrationsRegistry registry) =>
        registry.Outgoing(request.Id) is { } binding
     && databases.Any(d => d.DbId == binding.DbId) ? binding.DbId : 0;

    // ── Сценарий ──────────────────────────────────────────────────────────────

    private static async Task RunScenarioAsync(
        Window owner, Scenario scenario,
        Func<Scenario, CancellationToken, Task<ScenarioOutcome>> run)
    {
        var progress = new ProgressDialog { Title = "Сценарий" };
        using var cts = new CancellationTokenSource();

        // Отмена останавливает сценарий МЕЖДУ шагами: обещать большее нечестно — начатый запрос
        // к чужой программе не отменяется по нажатию.
        progress.EnableCancel(cts.Cancel);
        progress.SetSaving($"Сценарий «{scenario.Name}»…");

        var closed = progress.ShowDialog(owner);
        var open   = true;

        ScenarioOutcome? outcome = null;
        try
        {
            outcome = await run(scenario, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Отмена — не ошибка.
        }
        finally
        {
            if (open) { open = false; progress.Done(); await closed; }
        }

        if (outcome is null) return;

        await ConfirmDialog.Info($"Сценарий «{scenario.Name}»: {outcome.Summary()}")
            .ShowDialog(owner);
    }
}
