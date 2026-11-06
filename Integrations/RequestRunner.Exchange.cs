using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartBase.Integrations;

public static partial class RequestRunner
{
    /// <summary>
    /// Один запрос передачи: буквальные JSON-свойства добавляются после подстановок,
    /// двоичное тело не перекодируется. Создание сообщения и блоки не повторяются:
    /// обрыв ответа не означает, что та сторона не приняла запрос.
    /// </summary>
    public static async Task<EgressResponse> ExchangeAsync(IntegrationConnection connection,
        IntegrationRequest request, string secret, VariableContext context, string? properties,
        byte[]? data, CancellationToken ct = default)
    {
        if (!request.Outward || request.Paging != RequestPaging.None)
            return EgressResponse.Failed("Для передачи нужен изменяющий запрос без разбивки на страницы.");

        var page = Page(request, context, null, null, 1, 0, out var unknown);
        if (unknown.Count > 0) return EgressResponse.Failed(Unknown(unknown));
        // Идентификаторы в параметрах — данные, а не готовые фрагменты адреса.
        var pathContext = context with { Record = context.Record?.ToDictionary(p => p.Key,
            p => Uri.EscapeDataString(p.Value), StringComparer.OrdinalIgnoreCase) };
        var path = RequestVariables.Apply(request.Path, pathContext, out var pathUnknown, VariablePlace.Path);
        if (pathUnknown.Count > 0) return EgressResponse.Failed(Unknown(pathUnknown));
        page = page with { Path = path };
        var headers = new List<IntegrationHeader>();
        foreach (var header in page.Headers)
        {
            var value = RequestVariables.Apply(header.Value, context, out var missing, VariablePlace.Body);
            if (missing.Count > 0) return EgressResponse.Failed(Unknown(missing));
            headers.Add(header with { Value = value });
        }
        page = page with { Headers = headers };

        if (properties is not null)
        {
            try
            {
                if (JsonNode.Parse(page.Body) is not JsonObject body
                    || JsonNode.Parse(properties) is not JsonObject additions)
                    return EgressResponse.Failed("Тело запроса сообщения должно быть объектом JSON.");
                foreach (var property in additions) body[property.Key] = property.Value?.DeepClone();
                page = page with { Body = body.ToJsonString() };
            }
            catch (JsonException)
            {
                return EgressResponse.Failed("Тело запроса сообщения не является JSON.");
            }
        }
        var plan = EgressPlan.Build(connection, page, secret, out var error);
        if (plan is null) return EgressResponse.Failed(error);
        plan = plan with { Data = data, Idempotent = false, Retries = 0 };
        if (data is not null) plan = plan with { Body = "", ContentType = "application/octet-stream" };
        ct.ThrowIfCancellationRequested();
        var answer = await EgressHttp.SendAsync(plan, ct);
        ct.ThrowIfCancellationRequested();
        return answer;
    }
}
