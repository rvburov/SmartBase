using SmartBase.Integrations;

namespace SmartBase.Database;

public static partial class RequestLauncher
{
    /// <summary>Скачать файл по карточке запроса. Содержимое книги не попадает в рабочий
    /// журнал: сохраняются только адрес с маской секрета, счетчики и результат передачи.</summary>
    public static async Task<EgressResponse> DownloadAsync(
        IntegrationsRegistry registry, IntegrationConnection connection, IntegrationRequest request,
        IReadOnlyDictionary<string, string> record, string scenario, string step,
        DateTimeOffset startedAt, CancellationToken ct = default)
    {
        var context = VariableContext.Now(registry.LastRun(request.Id), connection.Address, record);
        var answer = await RequestRunner.DownloadAsync(connection, request, registry.Secret(connection.Id), context, ct);
        var ok = answer.Ok && !answer.Truncated && answer.Content is not null;
        var explanation = ok ? $"Файл получен: {answer.Bytes:N0} байт"
            : answer.Truncated ? "Файл больше технического ограничения 32 МБ"
            : answer.Explanation.Length > 0 ? answer.Explanation
            : $"Не удалось получить файл: HTTP {answer.StatusCode}";
        RunJournal.Add(RunEntry.New(RunChannel.Request, request.Name, answer.SafeAddress, ok, explanation,
            0, answer.Bytes, answer.Elapsed, 0, startedAt, counts: answer.Sent, direction: request.Id,
            scenario: scenario, step: step));
        return answer;
    }
}
