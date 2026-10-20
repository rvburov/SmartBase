using System.Globalization;
using System.Text.Json;

namespace SmartBase.Core.Models;

/// <summary>Сопоставление исходных значений свойств, независимо от формата показа.</summary>
public static class RelationAutoMatch
{
    /// <summary>Совместимые семейства. Список сопоставляется с текстом, набор — только с набором.</summary>
    public static string Category(string? fieldType) => fieldType switch
    {
        "number" => "number",
        "date" or "created_at" or "updated_at" => "date",
        "checkbox" => "checkbox",
        "multiselect" => "multiselect",
        "file" => "file",
        "relation" => "relation",
        "lookup" => "lookup",
        "rollup" => "number",
        "user" => "user",
        "text" or "url" or "select" or "status" or "markdown" or "created_by" or "updated_by" => "text",
        _ => "",
    };

    /// <summary>Пустое значение дает null. false означает поврежденное или неподдерживаемое
    /// значение: такой пересчет нельзя применять как отсутствие совпадений.</summary>
    public static bool TryKey(string? raw, string fieldType, out string? key, int localDbId = 0)
        => TryKeyCore(raw, fieldType, out key, localDbId, 0);

    private static bool TryKeyCore(string? raw, string fieldType, out string? key, int localDbId, int depth)
    {
        key = null;
        if (depth >= 16) return false;
        var category = Category(fieldType);
        if (category.Length == 0) return false;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var value = raw;
        switch (category)
        {
            case "number":
                // В каноне числа нет группировки. Запятая и точка — поддерживаемые десятичные знаки.
                if (!decimal.TryParse(value.Replace(',', '.'), NumberStyles.Float,
                                      CultureInfo.InvariantCulture, out var number)) return false;
                key = "n:" + number.ToString("G29", CultureInfo.InvariantCulture);
                return true;
            case "date":
                if (DateValue.TryParseExact(value, out var date))
                {
                    key = $"d:{date.AllDay}:{date.Start.Ticks}:{date.End.Ticks}";
                    return true;
                }
                // Метаданные аудита хранят round-trip timestamp, в том числе доли секунды.
                if (fieldType is "created_at" or "updated_at"
                    && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var stamp))
                {
                    var local = stamp.LocalDateTime;
                    key = $"d:False:{local.Ticks}:{local.Ticks}";
                    return true;
                }
                return false;
            case "checkbox":
                if (value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)) key = "b:1";
                else if (value is "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)) key = "b:0";
                else return false;
                return true;
            case "multiselect":
                var options = MultiSelectValue.Parse(value).Where(x => !string.IsNullOrEmpty(x))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (options.Length > 0) key = "m:" + JsonSerializer.Serialize(options);
                return true;
            case "file":
                var paths = FileValue.ReadPaths(value).Where(x => !string.IsNullOrEmpty(x))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (paths.Length > 0) key = "f:" + JsonSerializer.Serialize(paths);
                return true;
            case "relation":
                try
                {
                    using var json = JsonDocument.Parse(value);
                    if (json.RootElement.ValueKind != JsonValueKind.Array) return false;
                    var targets = new List<(int Db, int Record)>();
                    foreach (var target in json.RootElement.EnumerateArray())
                    {
                        if (target.ValueKind != JsonValueKind.Object
                            || !target.TryGetProperty("DbId", out var db)
                            || db.ValueKind != JsonValueKind.Number || !db.TryGetInt32(out var dbId)
                            || !target.TryGetProperty("RecordId", out var record)
                            || record.ValueKind != JsonValueKind.Number || !record.TryGetInt32(out var id)
                            || dbId < 0 || id <= 0) return false;
                        if (dbId == 0 && localDbId <= 0) return false;
                        targets.Add((dbId == 0 ? localDbId : dbId, id));
                    }
                    if (targets.Count > 0)
                        key = "r:" + JsonSerializer.Serialize(targets.Distinct().OrderBy(t => t.Db).ThenBy(t => t.Record)
                            .Select(t => new { DbId = t.Db, RecordId = t.Record }));
                    return true;
                }
                catch (JsonException) { return false; }
            case "lookup":
                try
                {
                    using var json = JsonDocument.Parse(value);
                    if (json.RootElement.ValueKind != JsonValueKind.Array) return false;
                    var keys = new List<string>();
                    foreach (var item in json.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object
                            || !item.TryGetProperty("raw", out var r) || r.ValueKind != JsonValueKind.String
                            || !item.TryGetProperty("fieldType", out var type) || type.ValueKind != JsonValueKind.String
                            || !TryKeyCore(r.GetString(), type.GetString()!, out var itemKey, localDbId, depth + 1)) return false;
                        if (itemKey is not null) keys.Add(itemKey);
                    }
                    if (keys.Count > 0)
                        key = "l:" + JsonSerializer.Serialize(keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
                    return true;
                }
                catch (JsonException) { return false; }
            default:
                key = (category == "user" ? "u:" : "s:") + value;
                return true;
        }
    }
}
