using SmartBase.Controls.Dialogs;
using SmartBase.Utils;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Отправка инструкции адресатам из списка «Получатели инструкции».
///
/// Уходит только по кнопке <see cref="ButtonText"/> под списком и только новым — тем, кому
/// инструкция еще не уходила (решения пользователя 21.10.2026). Кнопка оставляет человеку выбор,
/// отправлять или нет, а правка списка сама ничего не рассылает: поле сохраняется при каждой
/// потере фокуса, и недописанный номер получил бы сообщение.
///
/// Кто уже получил, помнит скрытая настройка <see cref="InstructedKey"/>: в списке полей ее нет,
/// она только пополняется. Удаление из списка ничего не отправляет и ее не трогает: вернули
/// человека в список — второй раз инструкция ему не придет.
///
/// Как сообщение доходит до сторонней программы, расширение не знает: путь — тот же, каким
/// отвечает на сообщения действие <see cref="RegisterMessageAction"/> (<see cref="IActionReplies"/>).
/// </summary>
public static class SenderGreeting
{
    /// <summary>Подпись кнопки под списком.</summary>
    public const string ButtonText = "Отправить инструкцию";

    /// <summary>Первая строка сообщения с инструкцией.</summary>
    public const string FirstLine = "Инструкция по регистрации документов в журнале КД.";

    /// <summary>Скрытая настройка: идентификаторы тех, кому инструкция уже ушла.</summary>
    public const string InstructedKey = "senders.instructed";

    /// <summary>
    /// Текст сообщения: заголовок, пустая строка и инструкция.
    /// Разметка — как у остальных ответов (<see cref="ReplyMarkup"/>).
    /// </summary>
    public static string Text => ReplyMarkup.Bold(FirstLine) + "\n\n" + MessageInstructions.Text;

    /// <summary>Новые: записи списка, чьим идентификаторам инструкция еще не уходила, — в порядке
    /// списка.</summary>
    public static IReadOnlyList<(string Id, string Entry)> Fresh(string? list, string? instructed)
    {
        var sent = MessageRegistration.Senders(instructed);

        return MessageRegistration.SenderEntries(list).Where(e => !sent.Contains(e.Id)).ToList();
    }

    /// <summary>Пополнить список получивших.</summary>
    public static string Remember(string? instructed, string id) =>
        string.Join("; ", MessageRegistration.SenderEntries($"{instructed};{id}").Select(e => e.Id));

    /// <summary>Нажатие кнопки: найти путь, спросить, отправить и рассказать, что вышло.</summary>
    public static async Task RunAsync(ExtensionSettingRun run)
    {
        var owner  = run.Owner;
        var action = new RegisterMessageAction();

        if (MessageRegistration.SenderEntries(run.Value).Count == 0)
        {
            await Info(owner, "Список пуст: отправлять некому.");
            return;
        }

        if (run.Replies is not { } replies)
        {
            await Info(owner, "Инструкцию отправить не через что: интеграции недоступны.");
            return;
        }

        if (replies.Route(DocRegistryExtension.ExtensionId, action, RegisterMessageAction.SenderField,
                          out var problem) is not { } scenario)
        {
            await Info(owner, $"Инструкцию отправить не через что: {problem}.");
            return;
        }

        var instructed = AppSettings.LoadExtensionSetting(DocRegistryExtension.ExtensionId, InstructedKey);
        var fresh      = Fresh(run.Value, instructed);

        if (fresh.Count == 0)
        {
            await Info(owner, "Новых в списке нет: инструкцию уже получили все, кто в нем записан.");
            return;
        }

        if (!await new ConfirmDialog(
                "Отправить приветствие и инструкцию?\n\n"
                + string.Join("\n", fresh.Select(f => f.Entry))
                + $"\n\nСообщение уйдет через сценарий «{scenario}».",
                confirmText: "Отправить",
                cancelText:  "Отмена").ShowDialog<bool>(owner))
            return;

        var sent   = 0;
        var failed = new List<string>();

        using var cancel = new CancellationTokenSource();

        // Окно прогресса — потому что та сторона может молчать: запрос ждет ответа до своего
        // предела, и без окна кнопка выглядела бы зависшей.
        var progress = new ProgressDialog { Title = "Регистрация КД" };
        progress.SetProgress(0, "Отправка инструкции…");
        progress.EnableCancel(cancel.Cancel);
        var closed = progress.ShowDialog(owner);

        try
        {
            for (var i = 0; i < fresh.Count && !cancel.IsCancellationRequested; i++)
            {
                var (id, entry) = fresh[i];
                progress.SetProgress((double)i / fresh.Count, $"Отправка: {entry}");

                var result = await replies.SendAsync(DocRegistryExtension.ExtensionId, action,
                    RegisterMessageAction.SenderField, id, Text, cancel.Token);

                if (!result.Ok)
                {
                    failed.Add($"{entry} — {result.Explanation}");
                    continue;
                }

                // Запоминается после КАЖДОЙ удачи, а не в конце: оборвись рассылка на середине,
                // ушедшие уже не получили бы инструкцию второй раз при следующем нажатии.
                instructed = Remember(instructed, id);
                AppSettings.SaveExtensionSetting(DocRegistryExtension.ExtensionId, InstructedKey, instructed);
                sent++;
            }
        }
        catch (OperationCanceledException)
        {
            // Отмена — не ошибка: неотправленные останутся новыми до следующего нажатия.
        }
        finally
        {
            progress.Done();
            await closed;
        }

        var report = $"Отправлено: {sent} из {fresh.Count}.";
        if (failed.Count > 0)
            report += "\n\nНе отправлено:\n" + string.Join("\n", failed);
        if (sent + failed.Count < fresh.Count)
            report += "\n\nОтправка остановлена.";
        if (sent < fresh.Count)
            report += "\n\nНе получившие остаются новыми: нажмите кнопку еще раз, когда причина будет устранена.";

        await Info(owner, report);
    }

    private static Task Info(Avalonia.Controls.Window owner, string text) =>
        ConfirmDialog.Info(text).ShowDialog(owner);
}
