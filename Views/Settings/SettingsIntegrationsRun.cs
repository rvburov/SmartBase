using Avalonia.Controls;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Integrations;

namespace SmartBase.Views.Settings;

/// <summary>
/// «В коллекцию» — весь обмен от начала до конца одним нажатием: запрос уходит, ответ
/// разбирается, человек видит раскладку колонок, записи ложатся в коллекцию, итог показывается
/// числами (partial SettingsView).
///
/// Порядок шагов не случаен. Раскладка показывается ПОСЛЕ ответа, а не до: колонки в ней
/// настоящие, а не выписанные из документации, — угадывать имена полей чужой программы
/// невозможно, а ошибку в угаданном имени видно только по пустой коллекции через неделю.
///
/// Отметка о последнем удачном запуске ставится последней и не всегда — см.
/// <see cref="MarkIfComplete"/>.
/// </summary>
public partial class SettingsView
{
    /// <summary>
    /// Запустить запрос и уложить ответ в коллекцию. Ошибки на каждом шаге показываются
    /// человеку: «кнопка не работает» — худшее, чем может кончиться обмен.
    /// </summary>
    private async Task RunToCollectionAsync(Database.IntegrationsRegistry registry,
                                            IntegrationRequest            request,
                                            IntegrationConnection?        connection)
    {
        if (VisualRoot is not Window win) return;

        if (connection is null)
        {
            await ConfirmDialog.Info(
                    $"У запроса «{request.Name}» не найдено соединение — выберите его в карточке запроса.")
                .ShowDialog(win);
            return;
        }

        var databases = _databases?.Invoke() ?? [];

        if (databases.Count == 0)
        {
            await ConfirmDialog.Info(
                    "Нет открытых баз данных: ответ некуда положить.\n\n"
                    + "Подключите базу в разделе «Управление базой данных» и повторите.")
                .ShowDialog(win);
            return;
        }

        // Граница инкрементальной выгрузки — момент НАЧАЛА запроса, а не его конца: записи,
        // изменившиеся в чужой программе, пока шел обмен, иначе оказались бы позади границы и
        // не пришли бы уже никогда.
        var startedAt = DateTimeOffset.UtcNow;

        // Тело из коллекции, если запрос уносит записи. Собирается ДО окна с полосой: отказ
        // («база закрыта», «поле помечено») должен прийти сразу, а не после прогресса.
        var (payload, cannot) = Database.OutgoingSource.Build(registry, request, databases);

        if (cannot is { Length: > 0 })
        {
            await ConfirmDialog.Info($"Запрос «{request.Name}» отправить нельзя.\n\n{cannot}")
                .ShowDialog(win);
            return;
        }

        if (payload is { Rows: 0 })
        {
            await ConfirmDialog.Info(
                    $"В коллекции «{payload.Collection}» нет записей — отправлять нечего.")
                .ShowDialog(win);
            return;
        }

        var outcome = await SendAsync(win, registry, request, connection, startedAt, payload);
        if (outcome is null) return;

        var source = new JsonTextSource(outcome.Json);
        var schema = await ReadAnswerAsync(win, source, outcome);
        if (schema is null) return;

        var options = databases
            .Select(m => new DbOption(m.DbId, m.Name, m.Constructor))
            .ToList();

        var columns = new List<string> { schema.TitleHeader ?? "" };
        columns.AddRange(schema.FieldNames);

        var binding = await new IntegrationBindingDialog(
                options, columns, schema.RowCount, registry.Binding(request.Id))
            .ShowDialog<CollectionBinding?>(win);

        if (binding is null) return;

        var target = options.FirstOrDefault(o => o.Id == binding.DbId);

        if (target is null)
        {
            await ConfirmDialog.Info("База данных закрылась, пока шла настройка переноса. "
                                   + "Откройте ее и повторите.").ShowDialog(win);
            return;
        }

        var result = await ApplyAsync(win, target, binding, schema, source);
        if (result is null) return;

        // Привязка запоминается ТОЛЬКО после удачной укладки: сохрани ее раньше — и следующий
        // запуск подставил бы раскладку, которая уже один раз не сработала.
        //
        // И только тому, кому разрешена настройка обмена. Запуск руками правом не ограничен, но
        // запомненной раскладкой потом воспользуется сценарий по расписанию: иначе запуск руками
        // молча переучил бы ночной обмен на другую коллекцию — в обход запрета на настройку.
        var remembered = CanIntegrations(Core.Models.PermAction.IntegrationsSetup);

        if (remembered) registry.SaveBinding(request.Id, binding);
        MarkIfComplete(registry, request.Id, startedAt, outcome);

        // Карточка запроса показывает время последнего запуска — после обмена оно другое.
        // Разделы конструктора обновятся сами: изолированное подключение публикует свои
        // изменения в общую шину при сохранении (см. AppDbContext).
        BuildIntegrationsRequests();

        await ConfirmDialog.Info(Report(request, outcome, result)
            + (remembered
                ? ""
                : "\n\nРаскладка не запомнена: настройка интеграций запрещена администратором "
                + "базы данных. Записи перенесены, но следующий запуск снова спросит, что куда "
                + "ложится.")).ShowDialog(win);
    }

    // ── Шаги ──────────────────────────────────────────────────────────────────

    /// <summary>Запрос уходит; null — не ушел или ушел неудачно (человеку уже сказали).</summary>
    private static async Task<RunOutcome?> SendAsync(Window                        win,
                                                     Database.IntegrationsRegistry registry,
                                                     IntegrationRequest            request,
                                                     IntegrationConnection         connection,
                                                     DateTimeOffset                startedAt,
                                                     OutgoingPayload?              payload)
    {
        var context = VariableContext.Now(registry.LastRun(request.Id), connection.Address);

        // Замок на исходящем — до окна с бегущей полосой: отказ должен прийти немедленно, а не
        // после прогресса, который никуда не шел. Сценария тут нет, кнопку нажал человек, и в
        // журнале безопасности так и будет записано.
        var verdict = Database.OutwardGuard.Check(registry, connection, request, context,
                                                  scenario: "", dbId: 0, DateTimeOffset.Now,
                                                  payload);

        var progress = new ProgressDialog { Title = "Запрос" };
        using var cts = new CancellationTokenSource();
        progress.EnableCancel(cts.Cancel);

        // Порций может быть много, и сколько их всего, чужая программа не сообщает — процентов
        // тут не бывает, поэтому полоса бегущая.
        progress.SetSaving("Отправляю запрос…");
        var closed = progress.ShowDialog(win);
        var open   = true;

        RunOutcome? outcome = null;
        try
        {
            outcome = await RequestRunner.RunAsync(
                connection, request, registry.Secret(connection.Id), context,
                verdict.Trouble, payload, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Отмена — не ошибка: человек передумал, и говорить ему об этом окном незачем.
        }
        finally
        {
            if (open) { open = false; progress.Done(); await closed; }
        }

        if (outcome is null) return null;   // отменили — событием обмена это не считается

        // Строка журнала пишется и для НЕУДАЧНОГО запроса. Отказ чужой программы все равно был
        // обращением к ней: он расходует квоту и он же нужен при разборе «почему ничего не
        // пришло» — именно неудачных строк там ищут больше всего.
        //
        // В расход при этом идет только то, что дошло до сети: запуск, остановленный отказом
        // политики или выбранной квотой, обменом не был — засчитай его, и отказ по квоте
        // продлевал бы сам себя.
        Database.RunJournal.Add(Database.RunEntry.New(
            Database.RunChannel.Request, request.Name, outcome.SafeAddress, outcome.Ok,
            outcome.Explanation, outcome.Rows, outcome.Bytes, outcome.Elapsed,
            dbId: 0, at: startedAt, counts: outcome.Sent, direction: request.Id,
            sentBody: outcome.SentBody, gotBody: outcome.GotBody));

        // Направлением воспользовались — отсчет простоя начинается заново.
        if (outcome.Sent) Database.OutwardGuard.Used(registry, request, DateTimeOffset.Now);

        if (outcome.Ok) return outcome;

        await ConfirmDialog.Info($"Запрос «{request.Name}» не дал записей.\n\n{outcome.Explanation}")
            .ShowDialog(win);

        return null;
    }

    /// <summary>Схема ответа; null — ответ разобрать не удалось (человеку уже сказали).</summary>
    private static async Task<ImportSchema?> ReadAnswerAsync(Window win, JsonTextSource source, RunOutcome outcome)
    {
        try
        {
            // Разбор идет в фоне: ответ уже в памяти и обрезан потолком, но десятки тысяч
            // записей на UI-потоке дали бы заметное подвисание.
            return await Task.Run(() => source.Analyze(null, CancellationToken.None));
        }
        catch (ImportException error)
        {
            await ConfirmDialog.Info(
                    $"Ответ получен ({outcome.Rows} записей, {outcome.Bytes:N0} байт), "
                    + $"но разобрать его не удалось.\n\n{error.Message}")
                .ShowDialog(win);
            return null;
        }
    }

    /// <summary>Укладка; null — не уложили (человеку уже сказали).</summary>
    private static async Task<BindingResult?> ApplyAsync(Window            win,
                                                         DbOption          target,
                                                         CollectionBinding binding,
                                                         ImportSchema      schema,
                                                         JsonTextSource    source)
    {
        var progress = new ProgressDialog { Title = "Перенос в коллекцию" };
        using var cts = new CancellationTokenSource();
        progress.EnableCancel(cts.Cancel);

        var total = Math.Max(1, schema.RowCount);
        IProgress<int> ui = new Progress<int>(done =>
        {
            progress.SetProgress(Math.Min(1.0, done / (double)total), "Записи…");
            progress.SetInfo($"{done:N0} из {schema.RowCount:N0}");
        });

        progress.SetProgress(0, "Записи…");
        var closed = progress.ShowDialog(win);
        var open   = true;

        try
        {
            // Названия НЕ достраиваются «Без названия N», как при импорте файла: обмен
            // повторяется, номер каждый раз получался бы новый, и сопоставление по названию
            // заводило бы дубли. Пустое название так и остается пустым — привязка это считает.
            return await Task.Run(() => target.Repo.ApplyRowsIsolated(
                binding, schema, source.ReadRows(cts.Token), cts.Token, ui));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception error)
        {
            await CloseProgress();
            await ConfirmDialog.Info("Перенести не удалось: " + error.Message).ShowDialog(win);
            return null;
        }
        finally
        {
            await CloseProgress();
        }

        async Task CloseProgress()
        {
            if (!open) return;
            open = false;
            progress.Done();
            await closed;
        }
    }

    /// <summary>
    /// Отметить удачный запуск — но только если ответ пришел ЦЕЛИКОМ. Сбор, остановленный
    /// потолком строк, оставляет данные за границей: сдвинь ее — и остаток не придет уже
    /// никогда, потому что следующий запуск попросит «изменения с этого момента».
    /// </summary>
    private static void MarkIfComplete(Database.IntegrationsRegistry registry,
                                       int                           requestId,
                                       DateTimeOffset                startedAt,
                                       RunOutcome                    outcome)
    {
        if (!outcome.Capped) registry.MarkRun(requestId, startedAt);
    }

    /// <summary>Итог обмена числами: что пришло, что легло и что осталось за границей.</summary>
    private static string Report(IntegrationRequest request, RunOutcome outcome, BindingResult result)
    {
        var text = $"Запрос «{request.Name}»: получено записей {outcome.Rows}, "
                 + $"порций {outcome.Pages}, {outcome.Bytes:N0} байт.\n\n"
                 + "В коллекции: " + result.Summary() + ".";

        if (result.Ambiguous > 0)
            text += $"\n\nНе тронуто из-за неоднозначности: {result.Ambiguous}. "
                  + "В коллекции несколько записей с таким же значением ключа, и какую из них "
                  + "править — неизвестно.";

        if (result.Empty > 0)
            text += $"\n\nБез ключа: {result.Empty}. У этих строк ответа колонка сопоставления "
                  + "пустая — заводить их значило бы плодить дубли при каждом запуске.";

        if (outcome.Capped)
            text += "\n\nОтвет пришел не целиком: " + outcome.Explanation
                  + ". Отметка о последнем запуске не сдвинута — иначе остаток не пришел бы "
                  + "уже никогда.";
        else if (outcome.Explanation.Length > 0)
            text += "\n\n" + outcome.Explanation;

        return text;
    }
}
