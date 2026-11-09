namespace SmartBase.Core.Models;

/// <summary>Идентичность цели; DbId=0 означает базу записи-источника.</summary>
public readonly record struct RelationTarget(int DbId, int RecordId);

/// <param name="ConfigJson">Настройка свойства цели (пункты списка — для условия выбора записей).</param>
public sealed record RelationPropertyOption(string Key, string Name, string FieldType, string? ConfigJson = null);

/// <summary>Сырое значение для сопоставления и отдельный текст для показа/поиска.</summary>
public sealed record RelationPropertyValue(int RecordId, string Title, string Raw, string Display,
    string FieldType, string? ConfigJson);

/// <param name="Condition">Текст условия выбора записей («Статус» равно «Активен»); пусто — условия нет.</param>
/// <param name="ConditionProblem">Почему новые связи не выбираются (условие сломано); null — выбирать можно.</param>
/// <param name="SelectedOutside">Выбранные раньше записи, которые под условие не подходят: связь
/// остается, но снятую выбрать снова нельзя.</param>
public sealed record RelationPickerData(IReadOnlyList<RelationPropertyValue> Candidates,
    IReadOnlyDictionary<RelationTarget, string> SelectedLabels,
    IReadOnlyDictionary<RelationTarget, string> SelectedSearch,
    IReadOnlyDictionary<RelationTarget, string> SelectedTitles, bool HasMore,
    string Condition = "", string? ConditionProblem = null, IReadOnlySet<RelationTarget>? SelectedOutside = null);

public static class RelationProperty
{
    public const string NoAccess = "(нет доступа)";
    public const string Unavailable = "(база недоступна)";
    /// <summary>Старая межбазовая связь с номером реестра другого компьютера: база не определена,
    /// пока ее не выберут в окне свойства (ребра сохраняются).</summary>
    public const string UnknownBase = "(база не определена)";
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
