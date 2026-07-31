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

    /// <summary>Человекочитаемый показ: выбранные значения, склеенные <paramref name="separator"/>
    /// (по умолчанию «, »). Разделитель — настройка поля из ConfigJson (см. <see cref="SeparatorFromConfig"/>).</summary>
    public static string Display(string? raw, string separator = ", ") => string.Join(separator, Parse(raw));

    /// <summary>Ключ формата (хранится в ConfigJson поля) по умолчанию — запятая.</summary>
    public const string DefaultSeparatorKey = "comma";

    /// <summary>Ключ формата разделителя → строка-склейка для показа/экспорта.</summary>
    public static string SeparatorFor(string? key) => key switch
    {
        "dot"         => ". ",
        "space"       => " ",
        "semicolon"   => "; ",
        "newline"     => "\n",
        "comma_tight" => ",",
        "dot_tight"   => ".",
        _             => ", ",   // comma — по умолчанию
    };

    /// <summary>Строка-склейка из ConfigJson поля (<c>{"separator":"..."}</c>); по умолчанию запятая.</summary>
    public static string SeparatorFromConfig(string? configJson) => SeparatorFor(SeparatorKeyFromConfig(configJson));

    /// <summary>Ключ формата из ConfigJson поля; для отсутствующего/нечитаемого — запятая.</summary>
    public static string SeparatorKeyFromConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return DefaultSeparatorKey;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.TryGetProperty("separator", out var el) && el.ValueKind == JsonValueKind.String &&
                el.GetString() is { Length: > 0 } s)
                return s;
        }
        catch (JsonException) { /* нечитаемый конфиг — дефолт */ }
        return DefaultSeparatorKey;
    }
}
