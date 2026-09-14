using System.Globalization;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Разбор значений, пришедших из буфера обмена: число в показанном виде
/// (<see cref="NumberFormatValue.TryParseDisplay"/>) и дата целой строкой
/// (<see cref="DateValue.TryParseExact"/>). Главное требование — скопированное из своей же таблицы
/// вставляется обратно тем же значением при любом формате столбца.
/// </summary>
public sealed class PasteValueParsingTests
{
    // ── Числа ─────────────────────────────────────────────────────────────────

    private static IEnumerable<NumberFormat> Formats()
    {
        foreach (var kind in Enum.GetValues<NumberFormatKind>())
            foreach (var decimals in new[] { -1, 0, 2 })
                foreach (var thousands in new[] { false, true })
                    foreach (var suffix in new[] { false, true })
                        foreach (var mark in Enum.GetValues<DecimalMark>())
                            yield return new NumberFormat(kind, decimals, thousands, "₽", suffix, mark);
    }

    /// <summary>Круг «показали → скопировали → вставили» дает то же число при любом формате.
    /// Значения без половинок на границе округления: как округляет показ, проверяет не этот тест.</summary>
    [Fact]
    public void Shown_number_parses_back_to_the_same_value()
    {
        foreach (var format in Formats())
        foreach (var stored in new[] { "0", "7", "42,25", "-1234,75", "1234567,25", "999" })
        {
            var raw   = NumberFormatValue.NormalizeStorage(stored, format.DecimalSeparator);
            var shown = NumberFormatValue.Format(raw, format);

            Assert.True(NumberFormatValue.TryParseDisplay(shown, format, format.DecimalSeparator, out var parsed),
                        $"«{shown}» не разобралось ({format})");

            // Сравниваем числа, а не строки: формат с двумя знаками показывает «42,50».
            var decimalsOnScreen = format.Kind == NumberFormatKind.Plain || format.Decimals < 0 ? 6 : format.Decimals;
            Assert.Equal(Math.Round(Value(raw), decimalsOnScreen), Value(parsed), 6);
        }
    }

    [Theory]
    [InlineData("1 234,50 ₽",   DecimalMark.Comma, "1234,50")]
    [InlineData("$1,234.50",    DecimalMark.Comma, "1234,50")]
    [InlineData("1.234.567,8",  DecimalMark.Dot,   "1234567.8")]
    [InlineData("1'234'567",    DecimalMark.Comma, "1234567")]
    [InlineData("(1 200)",      DecimalMark.Comma, "-1200")]
    [InlineData("−15,5 %",      DecimalMark.Comma, "-15,5")]
    [InlineData("100 руб.",     DecimalMark.Comma, "100")]
    [InlineData("42.5",         DecimalMark.Comma, "42,5")]
    public void Foreign_number_notation_is_understood(string text, DecimalMark mark, string expected)
    {
        Assert.True(NumberFormatValue.TryParseDisplay(text, null, mark, out var storage));
        Assert.Equal(expected, storage);
    }

    /// <summary>Одна запятая с тремя цифрами: в столбце с разрядами и точкой — это тысячи, без
    /// разрядов — дробь. Угадывать иначе значило бы молча портить числа.</summary>
    [Fact]
    public void Single_separator_with_three_digits_depends_on_the_column()
    {
        var grouped = new NumberFormat(NumberFormatKind.Grouped, -1, true, "₽", true, DecimalMark.Dot);
        var plain   = grouped with { Thousands = false };

        Assert.True(NumberFormatValue.TryParseDisplay("1,234", grouped, DecimalMark.Dot, out var thousands));
        Assert.True(NumberFormatValue.TryParseDisplay("1,234", plain,   DecimalMark.Dot, out var fraction));

        Assert.Equal("1234",  thousands);
        Assert.Equal("1.234", fraction);
    }

    [Theory]
    [InlineData("абв")]
    [InlineData("1,2.3,4")]
    [InlineData("12 штук")]
    [InlineData("--5")]
    public void Garbage_is_not_a_number(string text)
    {
        Assert.False(NumberFormatValue.TryParseDisplay(text, null, DecimalMark.Comma, out _));
    }

    private static double Value(string storage) =>
        double.Parse(storage.Replace(',', '.'), CultureInfo.InvariantCulture);

    // ── Даты ──────────────────────────────────────────────────────────────────

    /// <summary>Все, что показывает сама таблица, разбирается обратно в то же значение.</summary>
    [Theory]
    [InlineData("2026-02-01")]
    [InlineData("2026-02-01/2026-02-03")]
    [InlineData("2026-02-01T14:00")]
    [InlineData("2026-02-01T14:00/2026-02-01T15:30")]
    [InlineData("2026-02-01T09:00/2026-02-03T18:00")]
    public void Shown_date_parses_back_to_the_same_value(string storage)
    {
        var value = DateValue.Parse(storage)!.Value;

        Assert.True(DateValue.TryParseExact(value.ToDisplay(), out var parsed));
        Assert.Equal(storage, parsed.ToStorage());
    }

    [Theory]
    [InlineData("01.02.2026",       "2026-02-01")]
    [InlineData("1.2.2026",         "2026-02-01")]
    [InlineData("01.02.26",         "2026-02-01")]
    [InlineData("01/02/2026",       "2026-02-01")]
    [InlineData("2026-02-01",       "2026-02-01")]
    [InlineData("2026-02-01T14:30:15", "2026-02-01T14:30")]
    [InlineData("01.02.2026 14:30", "2026-02-01T14:30")]
    public void Common_date_notations_are_understood(string text, string storage)
    {
        Assert.True(DateValue.TryParseExact(text, out var value));
        Assert.Equal(storage, value.ToStorage());
    }

    /// <summary>Лишние слова делают строку не датой: «Отчет от 01.02.2026» — текст, и молча
    /// выбросить слова вставка не вправе.</summary>
    [Theory]
    [InlineData("Отчет от 01.02.2026")]
    [InlineData("01.02.2026 утром")]
    [InlineData("31.02.2026")]
    [InlineData("вчера")]
    public void Text_around_a_date_is_not_a_date(string text)
    {
        Assert.False(DateValue.TryParseExact(text, out _));
    }
}
