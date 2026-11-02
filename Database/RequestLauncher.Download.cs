using System.Diagnostics;
using SmartBase.Integrations;

namespace SmartBase.Database;

public static partial class RequestLauncher
{
    /// <summary>Скачать файл по карточке запроса. Содержимое книги не попадает в рабочий
    /// журнал: сохраняются только адрес с маской секрета, счетчики и результат передачи.
    /// При включенном ожидании его общий срок начинается после первого завершенного HTTP 404;
    /// первый запрос сохраняет таймаут соединения. Другие ответы и сетевые ошибки не повторяются.</summary>
    public static async Task<EgressResponse> DownloadAsync(
        IntegrationsRegistry registry, IntegrationConnection connection, IntegrationRequest request,
        IReadOnlyDictionary<string, string> record, string scenario, string step,
        DateTimeOffset startedAt, CancellationToken ct = default, int waitSeconds = 0)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(waitSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(waitSeconds, ScenarioStep.MaxDownloadWaitSeconds);

        // Одна строка и один контекст на все попытки: изменение настроек или даты во время
        // ожидания не должно превратить повтор получения файла в запрос с другими параметрами.
        var values = new Dictionary<string, string>(record, StringComparer.OrdinalIgnoreCase);
        var context = VariableContext.Now(registry.LastRun(request.Id), connection.Address, values);
        var secret = registry.Secret(connection.Id);
        var elapsed = Stopwatch.StartNew();
        var answer = await RequestRunner.DownloadAsync(connection, request, secret, context, ct);
        var attempts = answer.Attempts;
        var bytes = answer.Bytes;
        var sent = answer.Sent;

        if (waitSeconds > 0 && DownloadPending(answer))
        {
            var lastMissing = answer;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(waitSeconds));
            try
            {
                while (DownloadPending(answer))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
                    deadline.Token.ThrowIfCancellationRequested();
                    // Считаем и начатую попытку, которую общий срок прервет во время ответа.
                    attempts++;
                    answer = await RequestRunner.DownloadAsync(connection, request, secret, context, deadline.Token);
                    attempts += Math.Max(0, answer.Attempts - 1);
                    bytes += answer.Bytes;
                    sent |= answer.Sent;
                    if (DownloadPending(answer)) lastMissing = answer;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                // Это истечение срока готовности, а не остановка человеком: последним
                // завершенным ответом остается 404, и частичных байтов файла здесь нет.
                answer = lastMissing with
                {
                    Explanation = $"Истек срок ожидания готовности файла ({waitSeconds} с): HTTP 404",
                };
            }
            answer = answer with { Attempts = attempts, Elapsed = elapsed.Elapsed };
        }

        ct.ThrowIfCancellationRequested();
        var ok = answer.Ok && !answer.Truncated && answer.Content is not null;
        var explanation = ok ? $"Файл получен: {answer.Bytes:N0} байт"
            : answer.Truncated ? "Файл больше технического ограничения 32 МБ"
            : answer.Explanation.Length > 0 ? answer.Explanation
            : $"Не удалось получить файл: HTTP {answer.StatusCode}";
        if (attempts > 1) explanation += $" · попыток: {attempts}";
        RunJournal.Add(RunEntry.New(RunChannel.Request, request.Name, answer.SafeAddress, ok, explanation,
            0, bytes, answer.Elapsed, 0, startedAt, counts: sent, direction: request.Id,
            scenario: scenario, step: step));
        return answer;
    }

    private static bool DownloadPending(EgressResponse answer) =>
        answer.Sent && answer.StatusCode == 404 && !answer.Truncated;
}
