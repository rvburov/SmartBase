namespace SmartBase.Extensions.DocRegistry;

/// <summary>Участник рассылки: идентификатор для отправки и полная строка для человека.</summary>
public sealed record BroadcastRecipient(string Id, string Label);

/// <summary>Отказ одного запроса отправки; остальные участники продолжают получать сообщение.</summary>
public sealed record BroadcastFailure(BroadcastRecipient Recipient, string Problem);

/// <summary>Число завершенных запросов и участник, с которым работает рассылка.</summary>
public sealed record BroadcastProgress(int Completed, int Total, BroadcastRecipient Recipient);

/// <summary>
/// Итог рассылки. Успех означает ответ пути отправки, а не прочтение человеком.
/// Прерванный запрос отмечен отдельно: его доставка не подтверждена, поэтому он не относится
/// ни к успешным, ни к не начатым. Автоматически повторять его нельзя.
/// </summary>
public sealed record BroadcastReport(int Sent, int Total, IReadOnlyList<BroadcastFailure> Failed,
                                     bool Cancelled, string Problem = "", BroadcastRecipient? Interrupted = null)
{
    public int Attempted => Sent + Failed.Count + (Interrupted is null ? 0 : 1);
    public int NotAttempted => Total - Attempted;
}

/// <summary>
/// Произвольное сообщение участникам из настроек расширения. Отправка идет тем же путем,
/// что инструкция и ответы действия регистрации; список получивших инструкцию не меняется.
/// UI отвечает за выбор и подтверждение, здесь — снимок адресатов и последовательная отправка.
/// </summary>
public static class ParticipantBroadcast
{
    /// <summary>Участники в порядке настройки, без повторов идентификатора.</summary>
    public static IReadOnlyList<BroadcastRecipient> Participants(string? list) =>
        MessageRegistration.SenderEntries(list).Select(e => new BroadcastRecipient(e.Id, e.Entry)).ToArray();

    /// <summary>
    /// Только выбранные из актуального списка: старый выбор не должен отправить сообщение
    /// человеку, которого уже убрали из настройки. Сравнение идентификаторов — без регистра.
    /// </summary>
    public static IReadOnlyList<BroadcastRecipient> Select(string? list, IReadOnlyCollection<string> selectedIds)
    {
        var selected = selectedIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Participants(list).Where(p => selected.Contains(p.Id)).ToArray();
    }

    /// <summary>
    /// Проверить ввод и путь, затем отправить каждому по одному запросу. Ошибка одного не
    /// останавливает остальных. Отмена оставляет различимыми отказ, прерванный запрос и
    /// участников, к которым еще не обращались. Текст вводится обычный, разметкой не считается.
    /// </summary>
    public static async Task<BroadcastReport> SendAsync(IActionReplies? replies,
        IReadOnlyList<BroadcastRecipient> recipients, string? text,
        IProgress<BroadcastProgress>? progress = null, CancellationToken ct = default)
    {
        // Защита и для вызова вне окна: одному идентификатору нельзя отправить дважды
        // из-за дубля в списке. Написание и подпись первого вхождения сохраняются.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = recipients.Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .Select(r => r with { Id = r.Id.Trim() }).Where(r => seen.Add(r.Id)).ToArray();
        var total = targets.Length;
        BroadcastReport Refused(string problem) => new(0, total, [], false, problem);

        if (string.IsNullOrWhiteSpace(text)) return Refused("Не указан текст сообщения.");
        if (total == 0) return Refused("Не выбраны получатели сообщения.");
        if (replies is null) return Refused("Сообщение отправить не через что: интеграции недоступны.");
        if (ct.IsCancellationRequested) return new(0, total, [], true);

        var action = new RegisterMessageAction();
        try
        {
            if (replies.Route(DocRegistryExtension.ExtensionId, action, RegisterMessageAction.SenderField,
                out var problem) is null)
                return Refused($"Сообщение отправить не через что: {problem}.");
        }
        catch (Exception error)
        {
            return Refused($"Не удалось проверить путь отправки сообщения: {error.Message}");
        }

        // Ответы сценария — HTML. Пользовательский текст остается буквальным: теги и
        // амперсанды экранируются, а переносы заменяет стандартный путь ответов сценария.
        var outgoing = ReplyMarkup.Escape(text.ReplaceLineEndings("\n"));
        var failed = new List<BroadcastFailure>();
        var sent = 0;
        var cancelled = false;
        BroadcastRecipient? interrupted = null;
        foreach (var recipient in targets)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            progress?.Report(new(sent + failed.Count, total, recipient));
            try
            {
                var result = await replies.SendAsync(DocRegistryExtension.ExtensionId, action,
                    RegisterMessageAction.SenderField, recipient.Id, outgoing, ct);
                if (result.Ok) sent++;
                else failed.Add(new(recipient, result.Explanation));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                interrupted = recipient;
                cancelled = true;
                break;
            }
            catch (Exception error)
            {
                failed.Add(new(recipient, error.Message));
            }
            progress?.Report(new(sent + failed.Count, total, recipient));
        }
        return new(sent, total, failed.ToArray(), cancelled, Interrupted: interrupted);
    }
}
