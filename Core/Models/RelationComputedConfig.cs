using System.Globalization;
using System.Text.Json;

namespace SmartBase.Core.Models;

/// <summary>Вычисляемое свойство поверх локального поля-связи. TargetField — «Название» либо
/// ключ свойства в целевой коллекции; идентичность самой связи остается в db_record_links.</summary>
public sealed record RelationComputedConfig(int RelationFieldId, string TargetField = "__title__",
                                            string Aggregate = "lookup")
{
    public static readonly string[] Aggregates = ["count", "sum", "avg", "min", "max"];

    public static RelationComputedConfig? FromConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("relationFieldId", out var id)
                || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var fieldId) || fieldId <= 0)
                return null;
            var target = root.TryGetProperty("targetField", out var t)
                ? t.ValueKind == JsonValueKind.String ? t.GetString() : null : "__title__";
            if (!IsPropertyKey(target)) return null;
            var aggregate = root.TryGetProperty("aggregate", out var a)
                ? a.ValueKind == JsonValueKind.String ? a.GetString() : null : "lookup";
            if (aggregate != "lookup" && !Aggregates.Contains(aggregate, StringComparer.Ordinal)) return null;
            return new(fieldId, target!, aggregate!);
        }
        catch (JsonException ex)
        {
            ConfigJsonLog.Broken("вычисление по связи", json, ex);
            return null;
        }
    }

    public static bool IsPropertyKey(string? key) => key == "__title__"
        || int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;

    public string ToConfigJson() => JsonSerializer.Serialize(new
    {
        relationFieldId = RelationFieldId, targetField = TargetField, aggregate = Aggregate,
    });

    public static string AggregateLabel(string aggregate) => aggregate switch
    {
        "count" => "Количество", "sum" => "Сумма", "avg" => "Среднее",
        "min" => "Минимум", "max" => "Максимум", _ => "Значения",
    };
}

/// <summary>Вычисленное значение с сохраненным типом для автоматического сопоставления.
/// У многозначного lookup Raw — массив типизированных элементов, а не склейка подписей.</summary>
public sealed record RelationComputedValue(string Raw, string Display, string FieldType, string? ConfigJson);
