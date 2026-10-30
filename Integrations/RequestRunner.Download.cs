namespace SmartBase.Integrations;

public static partial class RequestRunner
{
    /// <summary>Получить один файл через обычный запрос с подстановками. Ответ не разбирается
    /// как текст или таблица, байты передаются действию без перекодировки.</summary>
    public static async Task<EgressResponse> DownloadAsync(
        IntegrationConnection connection, IntegrationRequest request, string secret,
        VariableContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Method != RequestMethod.Get || request.Paging != RequestPaging.None
            || !string.IsNullOrWhiteSpace(request.Body))
            return EgressResponse.Failed("Для получения файла нужен запрос GET без тела и разбивки на страницы");

        var page = Page(request, context, payload: null, cursor: null, number: 1, offset: 0, out var unknown);
        if (unknown.Count > 0) return EgressResponse.Failed(Unknown(unknown));
        var plan = EgressPlan.Build(connection, page, secret, out var problem);
        if (plan is null) return EgressResponse.Failed(problem);
        var answer = await EgressHttp.DownloadAsync(plan, ct);
        ct.ThrowIfCancellationRequested();
        return answer;
    }
}
