using System.Globalization;
using System.Text.Json;
using SmartBase.Core.Exporters.TableWriters;

namespace SmartBase.Core.Models;

/// <summary>Способ показа значения поля «Число» (<c>number</c>). Хранение НЕ меняется — в
/// db_field_values по-прежнему лежит сырое число-строка; формат применяется только на показе
/// (ячейка таблицы, панель записи, значения фильтра). Переключение формата обратимо и данные
/// не трогает (аналогично разделителю «Мультисписка», см. <see cref="MultiSelectValue"/>).</summary>
public enum NumberFormatKind
{
    /// <summary>Как есть — сырой ввод, без обработки (поведение по умолчанию).</summary>
    Plain,
    /// <summary>Число с группировкой разрядов и фиксированной точностью.</summary>
    Grouped,
    /// <summary>Денежный формат: число + символ валюты (спереди/сзади).</summary>
    Currency,
    /// <summary>Проценты: число + «%» (без домножения — величина показа = хранимой).</summary>
    Percent,
}

/// <summary>Десятичный разделитель поля «Число». В отличие от прочих параметров формата (только
/// показ), выбор разделителя **канонизирует хранение**: значение приводится к нему на записи и при
/// смене настройки (цифры сохраняются, меняется только символ разделителя).</summary>
public enum DecimalMark
{
    /// <summary>Запятая (`42,5`) — по умолчанию (RU-локаль).</summary>
    Comma,
    /// <summary>Точка (`42.5`).</summary>
    Dot,
}

/// <summary>
/// Настройка формата поля «Число», хранится в ConfigJson поля. Вид/точность/разряды/валюта — только
/// показ (смена обратима, значения не трогает); <see cref="DecimalSeparator"/> дополнительно
/// канонизирует хранение под выбранный символ. Нечисловое значение (после смены типа из текста/даты)
/// показывается и хранится как есть.
/// </summary>
/// <param name="Kind">Тип формата.</param>
/// <param name="Decimals">Знаков после запятой; -1 = «Авто» (сколько есть, до 6).</param>
/// <param name="Thousands">Группировать разряды (в РФ — неразрывный пробел).</param>
/// <param name="CurrencySymbol">Символ валюты (только для <see cref="NumberFormatKind.Currency"/>).</param>
/// <param name="SymbolSuffix">true — символ валюты после числа («100 ₽»), false — перед («₽100»).</param>
/// <param name="DecimalSeparator">Десятичный разделитель — показ и канон хранения.</param>
public sealed record NumberFormat(
    NumberFormatKind Kind,
    int Decimals,
    bool Thousands,
    string CurrencySymbol,
    bool SymbolSuffix,
    DecimalMark DecimalSeparator = DecimalMark.Comma)
{
    /// <summary>Формат по умолчанию — «Обычное число» с десятичной запятой.</summary>
    public static readonly NumberFormat Default = new(NumberFormatKind.Plain, -1, false, "₽", true, DecimalMark.Comma);
}

/// <summary>Разбор/применение формата числа (<see cref="NumberFormat"/>).</summary>
public static class NumberFormatValue
{
    // Неразрывный пробел для группировки разрядов — не переносится и не «съедается» версткой.
    private const string GroupSep = " ";

    /// <summary>Ключ формата (<see cref="NumberFormat.Kind"/>) → строка ConfigJson и обратно.</summary>
    private static string KindKey(NumberFormatKind kind) => kind switch
    {
        NumberFormatKind.Grouped  => "grouped",
        NumberFormatKind.Currency => "currency",
        NumberFormatKind.Percent  => "percent",
        _                         => "plain",
    };

    private static NumberFormatKind KindFromKey(string? key) => key switch
    {
        "grouped"  => NumberFormatKind.Grouped,
        "currency" => NumberFormatKind.Currency,
        "percent"  => NumberFormatKind.Percent,
        _          => NumberFormatKind.Plain,
    };

    /// <summary>Символ выбранного десятичного разделителя.</summary>
    private static char MarkChar(DecimalMark mark) => mark == DecimalMark.Dot ? '.' : ',';

    /// <summary>Формат из ConfigJson поля; для отсутствующего/нечитаемого — <see cref="NumberFormat.Default"/>.</summary>
    public static NumberFormat FromConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return NumberFormat.Default;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return NumberFormat.Default;

            var kind = root.TryGetProperty("numberFormat", out var f) && f.ValueKind == JsonValueKind.String
                ? KindFromKey(f.GetString())
                : NumberFormatKind.Plain;

            var decimals = root.TryGetProperty("decimals", out var d) && d.ValueKind == JsonValueKind.Number
                ? Math.Clamp(d.GetInt32(), -1, 6)
                : -1;

            var thousands = root.TryGetProperty("thousands", out var t) && t.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? t.GetBoolean()
                : false;

            var symbol = root.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } cs
                ? cs
                : "₽";

            var suffix = root.TryGetProperty("symbolSuffix", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? s.GetBoolean()
                : true;

            var mark = root.TryGetProperty("decimalSeparator", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() == "dot"
                ? DecimalMark.Dot
                : DecimalMark.Comma;

            return new NumberFormat(kind, decimals, thousands, symbol, suffix, mark);
        }
        catch (JsonException) { return NumberFormat.Default; }
    }

    /// <summary>Сериализует формат в объект для ConfigJson поля (числа). Обратно читается
    /// <see cref="FromConfig"/>. Ключи не пересекаются с «Мультисписком» (options/separator).</summary>
    public static object ToConfigObject(NumberFormat fmt) => new
    {
        numberFormat     = KindKey(fmt.Kind),
        decimals         = fmt.Decimals,
        thousands        = fmt.Thousands,
        currency         = fmt.CurrencySymbol,
        symbolSuffix     = fmt.SymbolSuffix,
        decimalSeparator = fmt.DecimalSeparator == DecimalMark.Dot ? "dot" : "comma",
    };

    /// <summary>Показ значения по формату. Пусто → пусто; нечисловое (после смены типа) → как есть;
    /// «Обычное число» → сырой ввод с приведенным десятичным разделителем (цифры сохраняются).</summary>
    public static string Format(string? raw, NumberFormat fmt)
    {
        var s = raw ?? "";
        if (s.Length == 0) return "";
        // «Обычное число» — как есть, только десятичный разделитель под выбранный (цифры не трогаем).
        if (fmt.Kind == NumberFormatKind.Plain) return NormalizeStorage(s, fmt.DecimalSeparator);
        if (!WriterHelpers.TryNumber(s, out var value)) return s;    // нечисловое — не форматируем

        var number = FormatNumber(value, fmt.Decimals, fmt.Thousands, MarkChar(fmt.DecimalSeparator));
        return fmt.Kind switch
        {
            NumberFormatKind.Percent  => number + GroupSep + "%",
            NumberFormatKind.Currency => fmt.SymbolSuffix
                ? number + GroupSep + fmt.CurrencySymbol
                : fmt.CurrencySymbol + GroupSep + number,
            _                         => number,                     // Grouped
        };
    }

    /// <summary>Канон хранения: приводит десятичный разделитель значения к выбранному (одиночная
    /// замena символа, цифры сохраняются). Нечисловое (пусто/текст/дата — TryNumber не разобрал)
    /// не трогаем. Используется на записи значения и при смене настройки разделителя.</summary>
    public static string NormalizeStorage(string? raw, DecimalMark mark)
    {
        var s = raw ?? "";
        if (s.Length == 0 || !WriterHelpers.TryNumber(s, out _)) return s;
        return mark == DecimalMark.Dot ? s.Replace(',', '.') : s.Replace('.', ',');
    }

    // Форматирует число с выбранным десятичным разделителем и опциональной группировкой разрядов
    // (неразрывный пробел). decimals = -1 → «Авто» (до 6 знаков, лишние нули убраны).
    private static string FormatNumber(double value, int decimals, bool thousands, char decimalChar)
    {
        var nfi = new NumberFormatInfo
        {
            NumberDecimalSeparator = decimalChar.ToString(),
            NumberGroupSeparator   = thousands ? GroupSep : "",
            NumberGroupSizes       = thousands ? [3] : [0],
        };
        if (decimals >= 0) return value.ToString("N" + decimals, nfi);

        var pattern = thousands ? "#,##0.######" : "0.######";
        return value.ToString(pattern, nfi);
    }
}
