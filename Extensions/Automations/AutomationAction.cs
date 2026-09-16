using System.Text.Encodings.Web;
using System.Text.Json;

namespace SmartBase.Extensions.Automations;

/// <summary>
/// Мост «автоматизация → действие расширения»: то, что превращает один класс с одним методом в
/// шаг сценария (docs/ROADMAP-API.md, шаг 13).
///
/// Нужен он ровно затем, чтобы автор автоматизации не писал ничего лишнего. Контракт действий
/// (<see cref="IExtensionAction"/>) знает про виды, подключение к базе, JSON строк и результат с
/// объяснением; автоматизации из всего этого нужны только строки. Разница и живет здесь — в
/// одном файле на все автоматизации, а не в каждой по разу.
/// </summary>
/// <param name="automation">Чью работу оборачиваем.</param>
internal sealed class AutomationAction(IAutomation automation) : IExtensionAction
{
    /// <summary>
    /// Как строки уходят дальше: массивом объектов JSON. Оболочка читает его тем же источником,
    /// что и ответ сторонней программы, поэтому сопоставление колонок работает без единой правки.
    /// Кириллица остается кириллицей — эти строки читают глазами в отчете о запуске.
    /// </summary>
    private static readonly JsonSerializerOptions Format = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Key         => automation.Key;
    public string Name        => automation.Name;
    public string Description => automation.What;

    public ExtensionActionKind Kind => ExtensionActionKind.Rows;

    public IReadOnlyList<ExtensionActionField> Fields => automation.Settings;

    public ActionResult Run(ActionContext context, ActionInput input)
    {
        var rows = new List<AutomationRow>(input.Table.Count);
        foreach (var row in input.Table) rows.Add(new AutomationRow(row));

        IReadOnlyList<AutomationRow> made;
        try
        {
            made = automation.Run(new AutomationInput(rows, input.Values)) ?? [];
        }
        catch (AutomationRefusal refusal)
        {
            // Отказ словами — обычный исход, а не поломка: его ждут в отчете о запуске и в
            // квитанции отправителю.
            return ActionResult.Failed(refusal.Message);
        }

        // Пустой ответ — «дальше идти незачем». Оболочка прочитает это как остановку сценария
        // без жалобы, ровно как пустой отбор.
        if (made.Count == 0) return ActionResult.Rowset("[]", 0, "строк не осталось");

        var json = JsonSerializer.Serialize(made.Select(m => m.Values), Format);

        return ActionResult.Rowset(json, made.Count, $"строк {made.Count}");
    }
}
