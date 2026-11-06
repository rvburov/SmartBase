using SmartBase.Extensions;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Сообщение без события (<see cref="IActionReplies"/>) на реестре интеграций: путь ответа
/// сценария, который выполняет действие расширения, — тот же запрос, тот же перенос строки и та
/// же цепочка отправки и рабочий журнал (<see cref="RequestLauncher"/>),
/// что у ответа на событие.
///
/// Путь ищется при каждой отправке, а не запоминается: сценарий могли выключить или поменять
/// между двумя нажатиями кнопки, и сообщение ушло бы по вчерашней настройке.
/// </summary>
public sealed class ActionReplies(IntegrationsRegistry registry) : IActionReplies
{
    public IReadOnlyList<ReplyUploadRequest> UploadRequests => registry.Requests()
        .Where(r => r.Outward && r.Paging == RequestPaging.None && string.IsNullOrWhiteSpace(r.Body))
        .Select(r => new ReplyUploadRequest(r.Id, r.Name)).ToArray();

    public IReplyFileExchange? FilesRoute(string extensionId, IExtensionAction action,
        string senderField, int uploadRequestId, out string problem)
    {
        var way = Find(extensionId, action, senderField, out problem);
        if (way is null) return null;
        var upload = registry.FindRequest(uploadRequestId);
        if (upload is null)
        {
            problem = "Не выбран запрос загрузки файлов.";
            return null;
        }
        if (!way.Request.Outward || way.Request.Paging != RequestPaging.None
            || !upload.Outward || upload.Paging != RequestPaging.None
            || upload.ConnectionId != way.Connection.Id || !string.IsNullOrWhiteSpace(upload.Body))
        {
            problem = "Запросы сообщения и загрузки должны изменять данные, быть без разбивки "
                + "на страницы и использовать одно соединение. У запроса загрузки не должно быть текстового тела.";
            return null;
        }
        problem = "";
        return new FileExchange(registry, way, upload);
    }

    /// <summary>Отметка шага в журнале запусков: сообщение ушло не ответом на событие.</summary>
    public const string Mark = "сообщение без события";

    private sealed record Way(Scenario Scenario, IntegrationRequest Request,
                               IntegrationConnection Connection, string Column);

    private sealed class FileExchange(IntegrationsRegistry registry, Way way,
        IntegrationRequest upload) : IReplyFileExchange
    {
        public Task<ReplyExchangeResult> CreateAsync(string recipient, string text, string properties,
            CancellationToken ct = default) => Send(way.Request,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [way.Column] = recipient },
                way.Scenario.Trigger.WithBreaks(text), properties, null, ct);

        public Task<ReplyExchangeResult> UploadAsync(IReadOnlyDictionary<string, string> fields,
            byte[] data, CancellationToken ct = default) => Send(upload, fields, null, null, data, ct);

        private async Task<ReplyExchangeResult> Send(IntegrationRequest request,
            IReadOnlyDictionary<string, string> fields, string? text, string? properties,
            byte[]? data, CancellationToken ct)
        {
            // Снимок не перенаправляется молча по новой настройке посреди загрузки.
            if (!Same(registry.Find(way.Connection.Id), way.Connection)
                || !Same(registry.FindRequest(request.Id), request)
                || !registry.Scenarios().Any(s => s.Enabled && Same(s, way.Scenario)))
                return new(false, "Настройки отправки изменились. Проверьте результат перед повтором.", "");
            var answer = await RequestLauncher.ExchangeAsync(registry, way.Connection, request,
                fields, text, properties, data, way.Scenario.Name, ct);
            var ok = answer.Ok && !answer.Truncated;
            return new(ok, ok ? "" : answer.Explanation.Length > 0 ? answer.Explanation
                : $"Ошибка передачи: HTTP {answer.StatusCode}", answer.Body);
        }

        private static bool Same<T>(T current, T saved) =>
            System.Text.Json.JsonSerializer.Serialize(current) == System.Text.Json.JsonSerializer.Serialize(saved);
    }

    public string? Route(string extensionId, IExtensionAction action, string senderField,
                         out string problem) =>
        Find(extensionId, action, senderField, out problem)?.Scenario.Name;

    public async Task<ReplyResult> SendAsync(string extensionId, IExtensionAction action,
                                             string senderField, string recipient, string text,
                                             CancellationToken ct = default)
    {
        if (Find(extensionId, action, senderField, out var problem) is not { } way)
            return new ReplyResult(false, problem);

        var now = DateTimeOffset.Now;
        var db  = way.Scenario.Steps.FirstOrDefault(s => s.DbId > 0)?.DbId ?? 0;

        // Строка «события» из одной колонки — той, откуда шаг берет отправителя: запрос ответа
        // ищет получателя в ней же ({{поле.…}}), как при ответе на настоящее событие.
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [way.Column] = recipient,
        };

        var sent = await RequestLauncher.SendAsync(
            registry, way.Connection, way.Request, payload: null, db, now,
            way.Scenario.Name, Mark, ct, row, way.Scenario.Trigger.WithBreaks(text));

        return new ReplyResult(sent.Ok, sent.Explanation);
    }

    private Way? Find(string extensionId, IExtensionAction action, string senderField,
                       out string problem)
    {
        // Выключенный сценарий не годится: им не отвечают и на события, и сообщение по нему
        // ушло бы путем, который человек сам закрыл.
        var scenarios = registry.Scenarios()
            .Where(s => s.Enabled && s.Trigger.WatchesStream && s.Trigger.RequestId > 0
                        && s.Steps.Any(step => Runs(step, extensionId, action)))
            .ToList();

        if (scenarios.Count == 0)
        {
            problem = $"нет включенного сценария, который по событию потока выполняет действие "
                    + $"«{action.Name}» и отвечает запросом";
            return null;
        }

        // Два сценария — два пути, и выбрать за человека нельзя: отправка по обоим дала бы два
        // одинаковых сообщения, а по первому попавшемуся — по тому, о котором он не думал.
        if (scenarios.Count > 1)
        {
            problem = $"действие «{action.Name}» выполняют несколько сценариев ("
                    + string.Join(", ", scenarios.Select(s => $"«{s.Name}»"))
                    + ") — непонятно, через какой отправлять";
            return null;
        }

        var scenario = scenarios[0];
        var step     = scenario.Steps.First(s => Runs(s, extensionId, action));
        var label    = action.Fields.FirstOrDefault(f => f.Key == senderField)?.Label ?? senderField;

        if (Column(step.Fields.GetValueOrDefault(senderField)) is not { } column)
        {
            problem = $"в сценарии «{scenario.Name}» поле шага «{label}» — не одна колонка события "
                    + "в двойных фигурных скобках: непонятно, куда поставить получателя";
            return null;
        }

        if (registry.FindRequest(scenario.Trigger.RequestId) is not { } request
            || registry.Find(request.ConnectionId) is not { } connection)
        {
            problem = $"в сценарии «{scenario.Name}» не найден запрос ответа";
            return null;
        }

        problem = "";
        return new Way(scenario, request, connection, column);
    }

    private static bool Runs(ScenarioStep step, string extensionId, IExtensionAction action) =>
        step.Kind == ScenarioStepKind.Action
        && string.Equals(step.Extension, extensionId, StringComparison.Ordinal)
        && string.Equals(step.Action, action.Key, StringComparison.Ordinal);

    /// <summary>
    /// Колонка события из выражения поля шага: ровно одна подстановка <c>{{колонка}}</c> и ничего
    /// вокруг. Выражение сложнее (<c>{{автор}}-{{отдел}}</c>) — получателя поставить некуда.
    /// </summary>
    public static string? Column(string? expression)
    {
        var text = (expression ?? "").Trim();
        if (text.Length < 5 || !text.StartsWith("{{", StringComparison.Ordinal)
                            || !text.EndsWith("}}", StringComparison.Ordinal))
            return null;

        var name = text[2..^2].Trim();
        return name.Length == 0 || name.Contains('{') || name.Contains('}') ? null : name;
    }
}
