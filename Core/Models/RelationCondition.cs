using System.Text.Json;

namespace SmartBase.Core.Models;

/// <summary>
/// Условие выбора записей связи: свойство цели, сравнение и значения («Статус» равно «Активен»
/// или «На продлении»). Условия одной связи соединяются по «и». Сравнивается сохраненное значение
/// записи-цели: у текста — без учета регистра и пробелов по краям («е с точками» равна «е»), у
/// списков и пользователя — точно, как фильтр столбца, у флажка — отмечен он или нет.
///
/// Условие держит новые связи (выбор, вставка, импорт, автоподбор); уже выбранные оно не
/// снимает — несоответствия показывает проверка связей. Нечитаемое условие (повреждено или задано
/// более новой версией) не пропускается молча: выбор новых связей закрыт, пока его не уберут.
/// </summary>
public sealed record RelationCondition(string Field, string Op, IReadOnlyList<string> Values)
{
    public const string Equal = "eq";
    public const string NotEqual = "ne";
    public const string Contains = "contains";
    public const string Empty = "empty";
    public const string NotEmpty = "notEmpty";
    public const int MaxConditions = 5;
    public const int MaxValues = 20;
    public const int MaxValueLength = 200;

    private static readonly string[] TextOps = [Equal, NotEqual, Contains, NotEmpty, Empty];
    private static readonly string[] ChoiceOps = [Equal, NotEqual, NotEmpty, Empty];
    private static readonly string[] CheckboxOps = [NotEmpty, Empty];

    /// <summary>Тип свойства для условия: «Название» сравнивается как текст.</summary>
    public static string TypeOf(string key, string? fieldType) => key == RelationConfig.TitleField ? "text" : fieldType ?? "";

    /// <summary>Какие свойства цели можно поставить в условие.</summary>
    public static bool SupportsType(string? fieldType) =>
        fieldType is "text" or "markdown" or "url" or "select" or "status" or "user" or "checkbox";

    /// <summary>Свойство-список: значение — один или несколько пунктов, сравнение точное.</summary>
    public static bool IsChoice(string? fieldType) => fieldType is "select" or "status" or "user";

    /// <summary>Сравнения, доступные типу свойства.</summary>
    public static IReadOnlyList<string> OpsFor(string? fieldType) => fieldType switch
    {
        "checkbox" => CheckboxOps,
        _ when IsChoice(fieldType) => ChoiceOps,
        _ when SupportsType(fieldType) => TextOps,
        _ => [],
    };

    /// <summary>Подпись сравнения в окне связи.</summary>
    public static string Label(string op, string? fieldType = null) => (op, fieldType) switch
    {
        (Empty, "checkbox") => "не отмечено",
        (NotEmpty, "checkbox") => "отмечено",
        (Equal, _) => "равно",
        (NotEqual, _) => "не равно",
        (Contains, _) => "содержит",
        (Empty, _) => "пусто",
        (NotEmpty, _) => "заполнено",
        _ => op,
    };

    public bool NeedsValue => NeedsValues(Op);

    public static bool NeedsValues(string op) => op is Equal or NotEqual or Contains;

    /// <summary>Подходит ли сохраненное значение <paramref name="raw"/> свойства типа
    /// <paramref name="fieldType"/>. null — сравнение не подходит к нынешнему типу свойства (его
    /// сменили после настройки): условие сломано, решать по нему нельзя.</summary>
    public bool? Matches(string? raw, string fieldType)
    {
        if (!OpsFor(fieldType).Contains(Op)) return null;
        if (fieldType == "checkbox")
            return CheckboxValue.IsChecked(raw ?? "") == (Op == NotEmpty);
        if (IsChoice(fieldType))
        {
            var value = raw ?? "";
            return Op switch
            {
                Equal => Values.Contains(value, StringComparer.Ordinal),
                NotEqual => !Values.Contains(value, StringComparer.Ordinal),
                Empty => value.Length == 0,
                _ => value.Length > 0,
            };
        }
        // Окно задает тексту одно значение; несколько (настройка из файла, бывший список)
        // читаются через «или», как и в тексте условия. Пустые значения не участвуют. Свертка —
        // общая с автоподбором (RelationAutoMatch.FoldText).
        var text = RelationAutoMatch.FoldText(raw);
        var expected = Values.Select(RelationAutoMatch.FoldText).Where(v => v.Length > 0).ToList();
        return Op switch
        {
            Equal => expected.Contains(text, StringComparer.Ordinal),
            NotEqual => !expected.Contains(text, StringComparer.Ordinal),
            Contains => expected.Any(v => text.Contains(v, StringComparison.Ordinal)),
            Empty => text.Length == 0,
            _ => text.Length > 0,
        };
    }

    /// <summary>Текст условия для окон и проверки: «Статус» равно «Активен» или «На продлении».</summary>
    public string Describe(string fieldName, string? fieldType)
    {
        var name = $"«{fieldName}» {Label(Op, fieldType)}";
        return NeedsValue ? $"{name} {string.Join(" или ", Values.Select(v => $"«{v.Trim()}»"))}" : name;
    }

    /// <summary>Разобрать «where» настройки связи. Unreadable — исходный JSON условия, если в нем
    /// есть незнакомое или неверное (условие более новой версии или поврежденное): такое условие
    /// хранится как есть и закрывает выбор новых связей.</summary>
    internal static (IReadOnlyList<RelationCondition>? Where, string? Unreadable) Read(JsonElement root)
    {
        if (!root.TryGetProperty("where", out var list)) return (null, null);
        var raw = list.GetRawText();
        if (list.ValueKind != JsonValueKind.Array) return (null, raw);
        var result = new List<RelationCondition>();
        foreach (var item in list.EnumerateArray())
        {
            if (result.Count == MaxConditions || TryReadOne(item) is not { } condition) return (null, raw);
            result.Add(condition);
        }
        return result.Count == 0 ? (null, null) : (result, null);
    }

    private static RelationCondition? TryReadOne(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        string? field = null, op = null;
        List<string>? values = null;
        foreach (var property in item.EnumerateObject())
            switch (property.Name)
            {
                case "field" when property.Value.ValueKind == JsonValueKind.String:
                    field = property.Value.GetString();
                    break;
                case "op" when property.Value.ValueKind == JsonValueKind.String:
                    op = property.Value.GetString();
                    break;
                case "values" when property.Value.ValueKind == JsonValueKind.Array:
                    values = [];
                    foreach (var value in property.Value.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text
                            || text.Length > MaxValueLength) return null;
                        values.Add(text);
                    }
                    break;
                default:
                    return null;
            }
        if (!RelationConfig.IsFieldKey(field) || op is not (Equal or NotEqual or Contains or Empty or NotEmpty)) return null;
        values ??= [];
        if (NeedsValues(op) ? values.Count is 0 or > MaxValues || values.All(v => v.Trim().Length == 0) : values.Count > 0)
            return null;
        return new RelationCondition(field!, op, values);
    }

    internal object ToConfigObject() => NeedsValue
        ? new Dictionary<string, object> { ["field"] = Field, ["op"] = Op, ["values"] = Values }
        : new Dictionary<string, object> { ["field"] = Field, ["op"] = Op };

    // Список значений сравнивается по составу: иначе две одинаковые настройки различались бы
    // ссылкой на список.
    public bool Equals(RelationCondition? other) => other is not null
        && Field == other.Field && Op == other.Op && Values.SequenceEqual(other.Values);

    public override int GetHashCode() => HashCode.Combine(Field, Op, Values.Count);
}
