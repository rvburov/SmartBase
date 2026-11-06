using SmartBase.Integrations;

namespace SmartBase.Database;

public static partial class RequestLauncher
{
    /// <summary>Запрос передачи с рабочим журналом; содержимое файлов в журнал не попадает.</summary>
    public static async Task<EgressResponse> ExchangeAsync(IntegrationsRegistry registry,
        IntegrationConnection connection, IntegrationRequest request,
        IReadOnlyDictionary<string, string> fields, string? reply, string? properties, byte[]? data,
        string scenario, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var secret = registry.Secret(connection.Id);
        var context = VariableContext.Now(registry.LastRun(request.Id), connection.Address, fields, reply);
        var answer = await RequestRunner.ExchangeAsync(connection, request, secret, context, properties, data, ct);
        RunJournal.Add(RunEntry.New(RunChannel.Request, request.Name, answer.SafeAddress,
            answer.Ok && !answer.Truncated, answer.Explanation, 0,
            (data?.LongLength ?? 0) + answer.Bytes, answer.Elapsed, 0, now,
            counts: answer.Sent, direction: request.Id,
            gotBody: RequestRunner.Journalled(answer.Body, secret), scenario: scenario, step: ActionReplies.Mark));
        return answer;
    }
}
