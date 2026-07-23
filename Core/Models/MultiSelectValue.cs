using System.Text.Json;

namespace SmartBase.Core.Models;

/// <summary>
/// Значение поля типа «Мультисписок» (`multiselect`): набор выбранных опций, хранится в
/// db_field_values строкой — JSON-массивом (`["Дизайн","Backend"]`). Пусто → пустой массив.
/// Непустая не-JSON строка трактуется как одно значение (мягкая миграция из `select`/`status`
/// при смене типа — данные не теряются, при следующем сохранении перепишутся массивом).
/// </summary>
public static class MultiSelectValue
{
    public static List<string> Parse(string? raw)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
                        list.Add(s);
                return list;
            }
        }
        catch (JsonException) { /* не JSON — легаси одиночное значение ниже */ }

        list.Add(raw);   // одиночная строка (миграция из select/status)
        return list;
    }

    /// <summary>JSON-массив для хранения; порядок и состав — как переданы (пустые отброшены).</summary>
    public static string Serialize(IEnumerable<string> values) =>
        JsonSerializer.Serialize(values.Where(v => !string.IsNullOrEmpty(v)).ToList());

    /// <summary>Человекочитаемый показ: значения через запятую (таблица read-cell fallback, экспорт).</summary>
    public static string Display(string? raw) => string.Join(", ", Parse(raw));
}
