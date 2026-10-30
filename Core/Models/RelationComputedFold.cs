using System.Text.Json;

namespace SmartBase.Core.Models;

/// <summary>Значение данных из связанных записей у одной записи: строка хранения (показ —
/// <see cref="FieldTypeInfo.FormatComputedDisplay"/>) и типизированное значение для автосвязи и
/// вложенных вычислений.</summary>
public readonly record struct RelationComputedResult(string Value, RelationComputedValue Typed);

/// <summary>
/// Свертка значений целей одной записи в значение столбца lookup или rollup — одна для страницы,
/// карточки записи, отбора, сортировки и окна фильтра: значения столбца целиком и одной записи
/// совпадают байт в байт. Цели — в порядке ребер, без повторов.
///
/// Старшинство: «(циклическая связь)» у любой цели, затем первая неверная цель (подпись ошибки
/// вместо значения — «(свойство недоступно)»), затем значение: lookup — непустые
/// подписи через разделитель связи, rollup — итог (<see cref="RelationRollupValue"/>). Ошибку
/// базы цели («(база недоступна)») выбирает вызывающий: она не зависит от целей.
/// </summary>
public static class RelationComputedFold
{
    public static RelationComputedResult Fold(string fieldType, string aggregate, string separator, string? fieldConfigJson,
        IReadOnlyList<RelationPropertyValue> selected)
    {
        if (selected.Any(v => v.Display == RelationProperty.Cycle || v.Raw == RelationProperty.Cycle))
            return Error(RelationProperty.Cycle);
        if (selected.FirstOrDefault(v => v.FieldType.Length == 0 && v.Display.Length > 0) is { } invalid)
            return Error(invalid.Display);
        if (fieldType == "rollup")
        {
            var total = RelationRollupValue.Calculate(aggregate, selected.Select(v => v.Raw));
            return new(total, new RelationComputedValue(total, FieldTypeInfo.FormatComputedDisplay(total, "rollup"),
                RelationComputedConfig.IsDateAggregate(aggregate) ? "date" : "number", "{}"));
        }
        var value = string.Join(separator, selected.Select(v => v.Display).Where(v => v.Length > 0));
        return new(value, selected.Count == 1
            ? new RelationComputedValue(selected[0].Raw, value, selected[0].FieldType, selected[0].ConfigJson)
            : new RelationComputedValue(JsonSerializer.Serialize(selected.Select(v => new
                { raw = v.Raw, fieldType = v.FieldType, configJson = v.ConfigJson })), value, "lookup", fieldConfigJson));
    }

    /// <summary>Подпись ошибки вместо значения: типизированного значения у нее нет.</summary>
    public static RelationComputedResult Error(string text) => new(text, new RelationComputedValue("", text, "", null));
}
