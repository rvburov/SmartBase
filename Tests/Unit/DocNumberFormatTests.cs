using System.Globalization;
using SmartBase.Core.Documents;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Записи формата ячейки Excel (<see cref="DocNumberFormat"/>).
///
/// Тихая это тема ровно потому, что неправильно показанное число выглядит настоящим значением
/// ячейки: «45356» вместо даты, «0,15» вместо «15 %» и «30772,48» вместо «30 772,48 ₽» ошибками
/// не выглядят вовсе — ни подчеркивания, ни сообщения, просто другое число на экране.
///
/// Разделители тут берутся у текущего языка системы, а не пишутся руками: на английской машине
/// ожидание «30 772,48» провалилось бы, хотя программа работает верно.
/// </summary>
public sealed class DocNumberFormatTests
{
    private static string Group => CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator;
    private static string Point => CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;

    // ── Без записи формата ────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("General")]
    public void Without_a_format_the_number_loses_its_tail_of_zeros(string? code)
    {
        Assert.Equal($"12{Point}5", DocNumberFormat.Apply(12.5, code).Text);
        Assert.Equal("12", DocNumberFormat.Apply(12.0, code).Text);
    }

    // ── Разряды и знаки после запятой ─────────────────────────────────────────

    [Fact]
    public void Thousands_and_decimals_come_from_the_format()
    {
        Assert.Equal($"30{Group}772{Point}48", DocNumberFormat.Apply(30772.48, "#,##0.00").Text);
        Assert.Equal($"30772{Point}48",        DocNumberFormat.Apply(30772.48, "0.00").Text);
        Assert.Equal("30772",                  DocNumberFormat.Apply(30772.48, "0").Text);
    }

    /// <summary>
    /// «#» — знак необязательный, «0» — обязательный. Разница видна на круглом числе: с «0.00»
    /// это «5,00», а с «0.##» — просто «5».
    /// </summary>
    [Fact]
    public void Optional_places_disappear_and_required_ones_stay()
    {
        Assert.Equal($"5{Point}00", DocNumberFormat.Apply(5, "0.00").Text);
        Assert.Equal("5",           DocNumberFormat.Apply(5, "0.##").Text);
        Assert.Equal($"5{Point}25", DocNumberFormat.Apply(5.25, "0.##").Text);
    }

    // ── Проценты, валюта, свой текст ──────────────────────────────────────────

    [Fact]
    public void Percent_multiplies_the_value_and_keeps_the_sign()
    {
        Assert.Equal("16%",              DocNumberFormat.Apply(0.156, "0%").Text);
        Assert.Equal($"15{Point}60%",    DocNumberFormat.Apply(0.156, "0.00%").Text);
    }

    /// <summary>Знак валюты из «[$₽-419]»: скобка несет и знак, и код языка — язык нам не нужен.</summary>
    [Fact]
    public void Currency_sign_comes_from_the_brackets()
    {
        Assert.Equal($"30{Group}772{Point}48 ₽", DocNumberFormat.Apply(30772.48, "#,##0.00 [$₽-419]").Text);
        Assert.Equal($"5{Point}00 ₽",            DocNumberFormat.Apply(5, "0.00 [$₽-419]").Text);
    }

    [Fact]
    public void Own_words_in_the_format_stay_words()
    {
        Assert.Equal($"5{Point}00 дней", DocNumberFormat.Apply(5, "0.00 \"дней\"").Text);
    }

    // ── Отрицательные и ноль ──────────────────────────────────────────────────

    /// <summary>У записи из одной части минус ставится сам — второй части для него просто нет.</summary>
    [Fact]
    public void A_single_section_signs_the_number_itself()
    {
        Assert.Equal($"-5{Point}00", DocNumberFormat.Apply(-5, "0.00").Text);
    }

    /// <summary>
    /// А у записи с отдельной частью для отрицательных минус ставит ОНА — число при этом берется
    /// по модулю. Иначе вышло бы «(-5,00)»: минус и от записи, и от нас.
    /// </summary>
    [Fact]
    public void A_negative_section_puts_the_sign_itself()
    {
        var value = DocNumberFormat.Apply(-5, "#,##0.00_);[Red](#,##0.00)");

        Assert.Equal($"(5{Point}00)", value.Text);
        Assert.True(value.Red);
    }

    [Fact]
    public void Red_belongs_to_the_negative_section_only()
    {
        Assert.False(DocNumberFormat.Apply(5, "#,##0.00_);[Red](#,##0.00)").Red);
    }

    [Fact]
    public void A_zero_section_is_used_for_zero()
    {
        Assert.Equal("—", DocNumberFormat.Apply(0, "0.00;-0.00;\"—\"").Text);
    }

    /// <summary>Запись из одних разделителей прячет значение — и мы обязаны его спрятать.</summary>
    [Fact]
    public void A_format_of_nothing_hides_the_value()
    {
        Assert.Equal("", DocNumberFormat.Apply(42, ";;;").Text);
    }

    /// <summary>А «@» — наоборот, «показать как есть»: пустотой это не кончается.</summary>
    [Fact]
    public void Text_format_shows_the_value_as_it_is()
    {
        Assert.Equal($"12{Point}5", DocNumberFormat.Apply(12.5, "@").Text);
    }

    // ── Даты ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Date_is_shown_as_a_date()
    {
        Assert.Equal("05.03.2024", DocNumberFormat.Apply(new DateTime(2024, 3, 5).ToOADate(), "dd.mm.yyyy").Text);
    }

    [Fact]
    public void A_number_out_of_the_date_range_stays_a_number()
    {
        Assert.Equal(DocNumberFormat.General(-1e9), DocNumberFormat.Apply(-1e9, "dd.mm.yyyy").Text);
    }

    // ── Что считать датой ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("dd.mm.yyyy")]
    [InlineData("h:mm")]
    [InlineData("[h]:mm:ss")]               // «прошло времени» — тоже время
    [InlineData("mmm-yy")]
    public void These_formats_are_dates(string code) => Assert.True(DocNumberFormat.IsDate(code));

    /// <summary>
    /// А эти — нет, и каждый по своей причине. «[Red]» — слово из скобки, и буква «d» в нем
    /// однажды превратила формат денег в дату: число ушло в 1900 год, и выглядело это настоящей
    /// датой.
    /// </summary>
    [Theory]
    [InlineData("[Red]-#,##0")]
    [InlineData("#,##0.00 [$₽-419]")]
    [InlineData("0.00 \"дней\"")]
    [InlineData("0%")]
    [InlineData("General")]
    [InlineData("")]
    public void These_formats_are_not_dates(string code) => Assert.False(DocNumberFormat.IsDate(code));

    // ── Встроенные записи ─────────────────────────────────────────────────────

    /// <summary>
    /// Встроенных записей в файле нет — у них одни номера. Пропусти их — и денежная колонка
    /// покажется голым числом без разрядов, молча.
    /// </summary>
    [Fact]
    public void Builtin_formats_are_known_by_their_numbers()
    {
        Assert.Equal("#,##0.00", DocNumberFormat.Builtin(4));
        Assert.Equal("0%",       DocNumberFormat.Builtin(9));
        Assert.Equal("@",        DocNumberFormat.Builtin(49));
        Assert.True(DocNumberFormat.IsDate(DocNumberFormat.Builtin(14)));
        Assert.True(DocNumberFormat.IsDate(DocNumberFormat.Builtin(22)));
        Assert.Equal("", DocNumberFormat.Builtin(999));
    }

    /// <summary>
    /// Показательная запись не разбирается вполсилы: половинчатый разбор дал бы число, похожее
    /// на правду, и это хуже честного «как есть».
    /// </summary>
    [Fact]
    public void Scientific_format_falls_back_to_the_plain_number()
    {
        Assert.Equal(DocNumberFormat.General(1234.5), DocNumberFormat.Apply(1234.5, "0.00E+00").Text);
    }
}
