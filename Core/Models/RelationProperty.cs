namespace SmartBase.Core.Models;

/// <summary>Идентичность цели; DbId=0 означает базу записи-источника.</summary>
public readonly record struct RelationTarget(int DbId, int RecordId);

/// <summary>Правило автосвязи: канонические ключи свойств двух коллекций.</summary>
public sealed record RelationAutoRule(string SourceField, string TargetField);

public sealed record RelationPropertyOption(string Key, string Name, string FieldType);

/// <summary>Сырое значение для сопоставления и отдельный текст для показа/поиска.</summary>
public sealed record RelationPropertyValue(int RecordId, string Title, string Raw, string Display,
    string FieldType, string? ConfigJson);

public sealed record RelationPickerData(IReadOnlyList<RelationPropertyValue> Candidates,
    IReadOnlyDictionary<RelationTarget, string> SelectedLabels,
    IReadOnlyDictionary<RelationTarget, string> SelectedSearch,
    IReadOnlyDictionary<RelationTarget, string> SelectedTitles, bool HasMore);

public static class RelationProperty
{
    public const string NoAccess = "(нет доступа)";
    public const string Unavailable = "(база недоступна)";
    public const string MissingField = "(свойство недоступно)";
    public const string Cycle = "(циклическая связь)";

    public static string Format(string? raw, string fieldType, string? configJson = null)
    {
        var value = raw ?? "";
        if (FieldTypeInfo.IsComputed(fieldType)) return FieldTypeInfo.FormatComputedDisplay(value, fieldType);
        if (fieldType == "checkbox") return CheckboxValue.IsChecked(value) ? "✓" : "(Не отмечено)";
        if (value.Length == 0) return "";
        return fieldType switch
        {
            "number" => NumberFormatValue.Format(value, NumberFormatValue.FromConfig(configJson)),
            "date" => DateValue.TryParse(value, out var date) ? date.ToDisplay() : value,
            "multiselect" => MultiSelectValue.Display(value, MultiSelectValue.SeparatorFromConfig(configJson)),
            "file" => FileValue.DisplayName(value),
            _ => value,
        };
    }
}

/// <summary>Общая защита вложенных связей/lookup от циклов в одном потоке чтения.</summary>
public static class RelationPropertyResolution
{
    [ThreadStatic] private static HashSet<(string Database, int Field)>? _active;

    public static IDisposable? Enter(string database, int fieldId)
    {
        var active = _active ??= [];
        var key = (database, fieldId);
        if (active.Count >= 16 || !active.Add(key)) return null;
        return new ExitScope(active, key);
    }

    private sealed class ExitScope(HashSet<(string Database, int Field)> active,
        (string Database, int Field) key) : IDisposable
    {
        public void Dispose() => active.Remove(key);
    }
}
