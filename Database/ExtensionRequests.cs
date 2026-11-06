using SmartBase.Extensions;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>Чтение внешних данных для подготовки действия и редакторов его настроек.</summary>
public sealed class ExtensionRequests(IntegrationsRegistry registry, int sourceConnectionId = 0) : IExtensionRequests
{
    public IReadOnlyList<ExtensionReadRequest> Requests => registry.Requests()
        .Where(r => r.Method == RequestMethod.Get)
        .Select(r => new ExtensionReadRequest(r.Id, r.Name, r.ConnectionId)).ToArray();

    public async Task<ExtensionReadResult> ReadAsync(int requestId,
        IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
    {
        var request = registry.FindRequest(requestId);
        if (request is null || request.Method != RequestMethod.Get)
            return new(false, "Не выбран сохраненный запрос GET для чтения данных пользователя.");
        if (sourceConnectionId > 0 && request.ConnectionId != sourceConnectionId)
            return new(false, "Запрос данных пользователя должен использовать соединение источника сообщения.");
        if (registry.Find(request.ConnectionId) is not { } connection)
            return new(false, "Соединение запроса чтения не найдено.");
        var now = DateTimeOffset.Now;
        var result = await RequestRunner.RunAsync(connection, request, registry.Secret(connection.Id),
            VariableContext.Now(registry.LastRun(request.Id), connection.Address, fields), null, ct);
        // Карточки содержат персональные данные. В рабочем журнале нужны результат и
        // счетчики, а не полная копия профиля или списка контактов.
        RunJournal.Add(RunEntry.New(RunChannel.Request, request.Name, result.SafeAddress,
            result.Ok && !result.Capped, result.Explanation, result.Rows, result.Bytes,
            result.Elapsed, 0, now, counts: result.Sent, direction: request.Id,
            step: "данные для действия расширения"));
        return result.Ok && !result.Capped
            ? new(true, "", result.Json)
            : new(false, result.Explanation.Length > 0 ? result.Explanation : "Ответ API получен не целиком.");
    }
}
