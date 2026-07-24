using System.Text.Json;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Формат показа поля «Число» (<see cref="NumberFormatValue"/>) — чистая логика: хранение не
/// меняется, формат применяется только на показе. Проверяем валюту/проценты/разряды/точность,
/// сохранность сырого значения (Plain и нечисловое) и round-trip настройки через ConfigJson.
/// </summary>
public sealed class NumberFormatValueTests
{
    // Неразрывный пробел — тот же, что группирует разряды и отделяет символ в NumberFormatValue.
    private const string Nbsp = " ";

    private static NumberFormat Fmt(NumberFormatKind kind, int decimals = -1, bool thousands = false,
        string symbol = "₽", bool suffix = true, DecimalMark mark = DecimalMark.Comma) =>
        new(kind, decimals, thousands, symbol, suffix, mark);

    // ── Обычное число: цифры сохраняются, десятичный разделитель — под выбранный ──

    [Theory]
    [InlineData("42,5",    "42,5")]
    [InlineData("42.5",    "42,5")]   // точка → запятая (выбран разделитель-запятая)
    [InlineData("-3",      "-3")]
    [InlineData("1000000", "1000000")]
    [InlineData("42.50",   "42,50")]  // цифры (в т.ч. хвостовой ноль) сохраняются
    public void Plain_comma_normalizes_separator(string raw, string expected)
    {
        Assert.Equal(expected, NumberFormatValue.Format(raw, NumberFormat.Default));
    }

    [Theory]
    [InlineData("42,5", "42.5")]   // запятая → точка (выбран разделитель-точка)
    [InlineData("42.5", "42.5")]
    [InlineData("1500", "1500")]
    public void Plain_dot_normalizes_separator(string raw, string expected)
    {
        Assert.Equal(expected, NumberFormatValue.Format(raw, Fmt(NumberFormatKind.Plain, mark: DecimalMark.Dot)));
    }

    [Fact]
    public void Empty_returns_empty()
    {
        Assert.Equal("", NumberFormatValue.Format("", Fmt(NumberFormatKind.Currency, 2, true)));
        Assert.Equal("", NumberFormatValue.Format(null, Fmt(NumberFormatKind.Currency, 2, true)));
    }

    [Fact]
    public void NonNumeric_returns_raw_as_is()
    {
        // Значение после смены типа из текста/даты — не число: не форматируем, показываем как есть.
        Assert.Equal("привет",     NumberFormatValue.Format("привет",     Fmt(NumberFormatKind.Currency, 2, true)));
        Assert.Equal("01.02.2026", NumberFormatValue.Format("01.02.2026", Fmt(NumberFormatKind.Percent)));
        // В т.ч. «Обычное число» не трогает разделители в дате (это не число — TryNumber не разобрал).
        Assert.Equal("01.02.2026", NumberFormatValue.Format("01.02.2026", NumberFormat.Default));
    }

    // ── Канон хранения десятичного разделителя ───────────────────────────────────

    [Theory]
    [InlineData("42.5",       DecimalMark.Comma, "42,5")]
    [InlineData("42,5",       DecimalMark.Dot,   "42.5")]
    [InlineData("1500",       DecimalMark.Dot,   "1500")]    // нет разделителя — без изменений
    [InlineData("-3.14",      DecimalMark.Comma, "-3,14")]
    [InlineData("",           DecimalMark.Dot,   "")]        // пусто
    [InlineData("привет",     DecimalMark.Dot,   "привет")]  // не число — как есть
    [InlineData("01.02.2026", DecimalMark.Comma, "01.02.2026")] // дата — не число, не трогаем
    public void NormalizeStorage_swaps_only_valid_number_separator(string raw, DecimalMark mark, string expected)
    {
        Assert.Equal(expected, NumberFormatValue.NormalizeStorage(raw, mark));
    }

    // ── Деньги ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Currency_suffix_with_grouping()
    {
        Assert.Equal($"1{Nbsp}500,00{Nbsp}₽",
            NumberFormatValue.Format("1500", Fmt(NumberFormatKind.Currency, 2, thousands: true)));
    }

    [Fact]
    public void Currency_prefix()
    {
        Assert.Equal($"${Nbsp}1500,00",
            NumberFormatValue.Format("1500", Fmt(NumberFormatKind.Currency, 2, thousands: false, symbol: "$", suffix: false)));
    }

    [Fact]
    public void Currency_parses_comma_input()
    {
        Assert.Equal($"1500,50{Nbsp}₽",
            NumberFormatValue.Format("1500,5", Fmt(NumberFormatKind.Currency, 2)));
    }

    [Fact]
    public void Currency_with_dot_separator()
    {
        Assert.Equal($"1500.00{Nbsp}₽",
            NumberFormatValue.Format("1500", Fmt(NumberFormatKind.Currency, 2, mark: DecimalMark.Dot)));
    }

    // ── Проценты ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Percent_no_scaling()
    {
        // Величина показа = хранимой: 15 → «15 %» (без домножения/деления).
        Assert.Equal($"15{Nbsp}%", NumberFormatValue.Format("15", Fmt(NumberFormatKind.Percent, 0)));
    }

    // ── Разряды и точность ───────────────────────────────────────────────────────

    [Fact]
    public void Grouped_thousands_auto_decimals()
    {
        Assert.Equal($"1{Nbsp}234{Nbsp}567",
            NumberFormatValue.Format("1234567", Fmt(NumberFormatKind.Grouped, decimals: -1, thousands: true)));
    }

    [Fact]
    public void Grouped_fixed_decimals()
    {
        Assert.Equal("1234,50",
            NumberFormatValue.Format("1234.5", Fmt(NumberFormatKind.Grouped, decimals: 2, thousands: false)));
    }

    [Fact]
    public void Auto_decimals_trims_trailing_zeros()
    {
        Assert.Equal("42,5", NumberFormatValue.Format("42.50", Fmt(NumberFormatKind.Grouped, decimals: -1)));
    }

    // ── ConfigJson round-trip ────────────────────────────────────────────────────

    [Fact]
    public void Config_roundtrip_preserves_all_fields()
    {
        var original = Fmt(NumberFormatKind.Currency, decimals: 2, thousands: true, symbol: "€", suffix: false,
            mark: DecimalMark.Dot);
        var json     = JsonSerializer.Serialize(NumberFormatValue.ToConfigObject(original));
        var parsed   = NumberFormatValue.FromConfig(json);
        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"options\":[\"a\"]}")]   // конфиг мультисписка — числовых ключей нет
    public void Config_missing_or_garbage_is_default(string? json)
    {
        Assert.Equal(NumberFormat.Default, NumberFormatValue.FromConfig(json));
    }

    [Fact]
    public void Config_default_is_plain()
    {
        Assert.Equal(NumberFormatKind.Plain, NumberFormat.Default.Kind);
    }
}
