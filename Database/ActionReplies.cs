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
    /// <summary>Отметка шага в журнале запусков: сообщение ушло не ответом на событие.</summary>
    public const string Mark = "сообщение без события";

    private sealed record Way(Scenario Scenario, IntegrationRequest Request,
                               IntegrationConnection Connection, string Column);

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
