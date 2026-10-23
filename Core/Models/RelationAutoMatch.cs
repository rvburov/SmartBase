using System.Globalization;
using System.Text;
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

    /// <summary>Семейство свойства с учетом его настройки: итог «самая ранняя/поздняя дата» —
    /// дата, остальные итоги — число.</summary>
    public static string Family(string? fieldType, string? configJson) =>
        fieldType == "rollup" && RelationComputedConfig.IsDateAggregate(RelationComputedConfig.FromConfig(configJson)?.Aggregate)
            ? "date" : Category(fieldType);

    /// <summary>Можно ли сопоставлять свойства: одно семейство либо значения из связанных записей
    /// с одной из сторон (их тип известен только по значению). Одно правило для подбора, окна и
    /// проверки связей.</summary>
    public static bool Compatible(string? sourceType, string? sourceConfig, string? targetType, string? targetConfig)
    {
        var source = Family(sourceType, sourceConfig);
        var target = Family(targetType, targetConfig);
        return source.Length > 0 && target.Length > 0
            && (sourceType == "lookup" || targetType == "lookup" || source == target);
    }

    /// <summary>Текст для сравнения без учета регистра и пробелов по краям: края без пробельных и
    /// невидимых знаков, составные буквы в одном виде (NFC), нижний регистр, «е с точками» как «е».
    /// Пробелы внутри не трогаются. Одна функция для автоподбора и условия выбора записей.</summary>
    public static string FoldText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        int start = 0, end = value.Length;
        while (start < end && IsEdge(value[start])) start++;
        while (end > start && IsEdge(value[end - 1])) end--;
        var core = value[start..end];
        try
        {
            if (!core.IsNormalized(NormalizationForm.FormC)) core = core.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException) { /* одиночный суррогат: сравниваем как есть */ }
        return CellTyping.Fold(core);
    }

    private static bool IsEdge(char c) => char.IsWhiteSpace(c) || c is '\u200B' or '\u2060' or '\uFEFF';

    /// <summary>Пустое значение дает null. false означает поврежденное или неподдерживаемое
    /// значение: такой пересчет нельзя применять как отсутствие совпадений. <paramref name="fold"/>
    /// сворачивает текст и пункты мультисписка (<see cref="FoldText"/>); пользователь, пути файлов,
    /// числа, даты, флажки и связи сравниваются как раньше.</summary>
    public static bool TryKey(string? raw, string fieldType, out string? key, int localDbId = 0, bool fold = false)
        => TryKeyCore(raw, fieldType, out key, localDbId, 0, fold);

    /// <summary>Ключ записи по всем парам правила. Любая поврежденная часть — отказ всего подбора
    /// (false). Пустая первая часть — <paramref name="key"/> = null и <paramref name="firstEmpty"/>:
    /// запись без совпадения. Пустая дополнительная часть совпадает только с пустой. Части
    /// склеиваются с длиной, чтобы («a|b», «c») не совпало с («a», «b|c»).</summary>
    public static bool TryRuleKey(IReadOnlyList<(string? Raw, string FieldType)> parts, bool fold, int localDbId,
        StringBuilder buffer, out string? key, out bool firstEmpty)
    {
        key = null;
        firstEmpty = false;
        buffer.Clear();
        string? first = null;
        for (var i = 0; i < parts.Count; i++)
        {
            if (!TryKey(parts[i].Raw, parts[i].FieldType, out var part, localDbId, fold)) return false;
            if (i == 0) first = part;
            if (part is null)
            {
                if (i == 0) firstEmpty = true;
                buffer.Append("-;");
            }
            else buffer.Append(part.Length).Append(':').Append(part).Append(';');
        }
        // Одна пара — прежний ключ без обертки.
        if (!firstEmpty) key = parts.Count == 1 ? first : buffer.ToString();
        return true;
    }

    private static bool TryKeyCore(string? raw, string fieldType, out string? key, int localDbId, int depth, bool fold = false)
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
                var options = MultiSelectValue.Parse(value).Select(x => fold ? FoldText(x) : x).Where(x => !string.IsNullOrEmpty(x))
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
                            || !TryKeyCore(r.GetString(), type.GetString()!, out var itemKey, localDbId, depth + 1, fold)) return false;
                        if (itemKey is not null) keys.Add(itemKey);
                    }
                    if (keys.Count > 0)
                        key = "l:" + JsonSerializer.Serialize(keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
                    return true;
                }
                catch (JsonException) { return false; }
            default:
                if (category == "user") { key = "u:" + value; return true; }
                if (!fold) { key = "s:" + value; return true; }
                // Строка из одних пробелов и невидимых знаков после свертки пуста.
                var folded = FoldText(value);
                if (folded.Length > 0) key = "s:" + folded;
                return true;
        }
    }
}
