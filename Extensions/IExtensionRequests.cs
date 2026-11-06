namespace SmartBase.Extensions;

/// <summary>Сохраненный запрос чтения; подключение нужно для сверки источника данных.</summary>
public sealed record ExtensionReadRequest(int Id, string Name, int ConnectionId);
public sealed record ExtensionReadResult(bool Ok, string Problem, string Json = "");

/// <summary>Чтение API через сохраненные запросы оболочки, без сетевых типов в расширениях.</summary>
public interface IExtensionRequests
{
    IReadOnlyList<ExtensionReadRequest> Requests { get; }
    Task<ExtensionReadResult> ReadAsync(int requestId, IReadOnlyDictionary<string, string> fields,
        CancellationToken ct = default);
}

/// <summary>Данные, полученные подготовкой действия, а не из редактируемых полей события.</summary>
public sealed record ActionIdentity(string Id, string FullName);

public sealed record ActionPreparation(ActionInput Input, ActionResult? Failure = null);
