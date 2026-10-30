using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>Запуск запроса и запись его результата в рабочий журнал обмена.</summary>
public static partial class RequestLauncher
{
    /// <summary>Отправить запрос. Сбой рабочего журнала не останавливает обмен.</summary>
    public static async Task<RunOutcome> SendAsync(
        IntegrationsRegistry  registry,
        IntegrationConnection connection,
        IntegrationRequest    request,
        OutgoingPayload?      payload,
        int                   dbId,
        DateTimeOffset        startedAt,
        string                scenario = "",
        string                step     = "",
        CancellationToken     ct       = default,
        IReadOnlyDictionary<string, string>? record = null,
        string?               reply    = null)
    {
        // Значения единственной уходящей записи — для подстановки {{поле.Имя}} В ПУТИ запроса.
        // Записей не одна — подстановки нет, и запуск остановится с понятным отказом: путь
        // собирается один раз, и подставить в него запись из трех значило бы отправить две
        // оставшиеся по чужому адресу.
        var context = VariableContext.Now(registry.LastRun(request.Id), connection.Address,
                                          payload?.Single ?? record, reply);

        var outcome = await RequestRunner.RunAsync(
            connection, request, registry.Secret(connection.Id), context, payload, ct);

        RunJournal.Add(RunEntry.New(
            RunChannel.Request, request.Name, outcome.SafeAddress, outcome.Ok, outcome.Explanation,
            outcome.Rows, outcome.Bytes, outcome.Elapsed, dbId, startedAt,
            counts: outcome.Sent, direction: request.Id,
            sentBody: outcome.SentBody, gotBody: outcome.GotBody,
            scenario: scenario, step: step));

        return outcome;
    }
}
