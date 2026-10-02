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

    /// <summary>5 марта 2024 года, вторник, 14:07:09.</summary>
    private static readonly double March5 = new DateTime(2024, 3, 5, 14, 7, 9).ToOADate();

    /// <summary>
    /// Язык системы на время проверки. Названия месяцев проверяются на русском — на нем книги и
    /// читают, — и итог не должен зависеть от языка машины, где идут тесты.
    /// </summary>
    private sealed class Culture : IDisposable
    {
        private readonly CultureInfo _was = CultureInfo.CurrentCulture;
        public Culture(string name) => CultureInfo.CurrentCulture = new CultureInfo(name);
        public void Dispose() => CultureInfo.CurrentCulture = _was;
    }

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
        Assert.Equal(DocNumberFormat.General(3e6),  DocNumberFormat.Apply(3e6,  "dd.mm.yyyy").Text);
    }

    /// <summary>
    /// Дата печатается ПО СВОЕЙ ЗАПИСИ. Раньше любая выходила жестким «дд.ММ.гггг»: год двумя
    /// цифрами и секунды пропадали, а день появлялся там, где запись его прятала. Поиск хранит
    /// книгу так, как ее показывает панель, и такая дата не находилась бы по тому, что видно.
    /// </summary>
    [Theory]
    [InlineData("dd.mm.yyyy",               "05.03.2024")]
    [InlineData("d.m.yy",                   "5.3.24")]
    [InlineData("yyyy-mm-dd",               "2024-03-05")]
    [InlineData("dd/mm/yyyy",               "05/03/2024")]
    [InlineData("dd.mm.yyyy hh:mm:ss",      "05.03.2024 14:07:09")]
    [InlineData("h:mm",                     "14:07")]
    [InlineData("mm:ss",                    "07:09")]               // «m» перед секундами — минуты
    [InlineData("mm.yyyy",                  "03.2024")]             // а без часов и секунд — месяц
    [InlineData("h:mm AM/PM",               "2:07 PM")]
    [InlineData("dd.mm.yyyy;@",             "05.03.2024")]          // вторая часть — для отрицательных
    [InlineData("[$-419]dd.mm.yyyy",        "05.03.2024")]          // язык записи не печатается
    [InlineData("dd\\.mm\\.yyyy\" г.\"",  "05.03.2024 г.")]
    public void A_date_follows_its_own_format(string code, string expected)
    {
        Assert.Equal(expected, DocNumberFormat.Apply(March5, code).Text);
    }

    /// <summary>
    /// Месяц словом — на языке системы и в нужном падеже: рядом с числом дня «5 марта», без
    /// него — «март 2024».
    /// </summary>
    [Fact]
    public void Month_and_day_names_speak_the_language_of_the_system()
    {
        using var _ = new Culture("ru-RU");

        Assert.Equal("5 марта 2024",     DocNumberFormat.Apply(March5, "d mmmm yyyy").Text);
        Assert.Equal("05 марта 2024 г.", DocNumberFormat.Apply(March5, "[$-FC19]dd mmmm yyyy \\г\\.").Text);
        Assert.Equal("март 2024",        DocNumberFormat.Apply(March5, "mmmm yyyy").Text);
        Assert.Equal("вторник",          DocNumberFormat.Apply(March5, "dddd").Text);
    }

    /// <summary>
    /// Сокращенный месяц — без точки на конце, как у табличного редактора: «5-мар-24», а не
    /// «5-мар.-24». И встроенная запись «месяц-год» дня не показывает: прежде он появлялся.
    /// </summary>
    [Fact]
    public void Short_month_names_lose_their_dot_and_hide_what_the_format_hides()
    {
        using var _ = new Culture("ru-RU");
        var names = CultureInfo.CurrentCulture.DateTimeFormat;

        var full = DocNumberFormat.Apply(March5, DocNumberFormat.Builtin(15)).Text;
        Assert.Equal($"5-{names.AbbreviatedMonthGenitiveNames[2].TrimEnd('.')}-24", full);

        var month = DocNumberFormat.Apply(March5, DocNumberFormat.Builtin(17)).Text;
        Assert.Equal($"{names.AbbreviatedMonthNames[2].TrimEnd('.')}-24", month);
        Assert.DoesNotContain("05", month, StringComparison.Ordinal);
    }

    /// <summary>Формат системы («[$-F800]» — длинная дата) берется у системы, а не из записи.</summary>
    [Fact]
    public void System_long_date_comes_from_the_system()
    {
        Assert.Equal(new DateTime(2024, 3, 5).ToString("D", CultureInfo.CurrentCulture),
                     DocNumberFormat.Apply(March5, "[$-F800]dddd, mmmm dd, yyyy").Text);
    }

    /// <summary>
    /// Встроенные записи времени — со своими секундами и «до полудня / после»: раньше все они
    /// были одной «h:mm».
    /// </summary>
    [Theory]
    [InlineData(19u, "2:07:09 PM")]
    [InlineData(21u, "14:07:09")]
    [InlineData(45u, "07:09")]
    public void Builtin_times_keep_what_they_show(uint id, string expected)
    {
        Assert.Equal(expected, DocNumberFormat.Apply(March5, DocNumberFormat.Builtin(id)).Text);
    }

    /// <summary>«[h]» — сколько прошло всего: полтора дня — это 36 часов, а не «12:00».</summary>
    [Fact]
    public void Elapsed_time_counts_past_a_day()
    {
        Assert.Equal("36:00",    DocNumberFormat.Apply(1.5,  "[h]:mm").Text);
        Assert.Equal("36:00:00", DocNumberFormat.Apply(1.5,  DocNumberFormat.Builtin(46)).Text);
        Assert.Equal("72:00",    DocNumberFormat.Apply(0.05, "[mm]:ss").Text);
    }

    /// <summary>
    /// Время округляется до показанной секунды и только потом делится на часы и минуты: без
    /// этого 10:29:59,6 по «h:mm» вышло бы «10:29».
    /// </summary>
    [Fact]
    public void Time_is_rounded_to_the_shown_second_first()
    {
        var value = (10 * 3600 + 29 * 60 + 59.6) / 86400;

        Assert.Equal("10:30",             DocNumberFormat.Apply(value, "h:mm").Text);
        Assert.Equal("10:30:00",          DocNumberFormat.Apply(value, "h:mm:ss").Text);
        Assert.Equal($"10:29:59{Point}6", DocNumberFormat.Apply(value, "h:mm:ss.0").Text);
    }

    /// <summary>
    /// Книга со счетом дней от 1904 года: то же число в ней — дата на четыре года и день
    /// позже. Без признака книги все ее даты уехали бы назад.
    /// </summary>
    [Fact]
    public void A_1904_book_counts_its_days_from_1904()
    {
        var serial = new DateTime(2024, 3, 5).ToOADate() - 1462;

        Assert.Equal("05.03.2024", DocNumberFormat.Apply(serial, "dd.mm.yyyy", date1904: true).Text);
        Assert.Equal("04.03.2020", DocNumberFormat.Apply(serial, "dd.mm.yyyy").Text);
    }

    /// <summary>
    /// Ошибка 1900 года, которую табличные редакторы держат ради совместимости: 1900 год у них
    /// високосный. Номер 60 — 29 февраля 1900, ноль — «00.01.1900», а обычный счет показал бы
    /// весь январь и февраль 1900 года на сутки раньше: номер 1 у него — 31 декабря 1899-го.
    /// </summary>
    [Theory]
    [InlineData(0,  "00.01.1900")]
    [InlineData(1,  "01.01.1900")]
    [InlineData(59, "28.02.1900")]
    [InlineData(60, "29.02.1900")]
    [InlineData(61, "01.03.1900")]
    public void The_1900_leap_day_is_kept(double serial, string expected)
    {
        Assert.Equal(expected, DocNumberFormat.Apply(serial, "dd.mm.yyyy").Text);
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

    /// <summary>
    /// Дробная запись — то же: 3,5 по «# ?/?» печаталось «4 /», и это хуже числа как есть.
    /// </summary>
    [Theory]
    [InlineData("# ?/?")]
    [InlineData("# ??/??")]
    [InlineData("?/8")]
    public void Fractions_fall_back_to_the_plain_number(string code)
    {
        Assert.Equal(DocNumberFormat.General(3.5), DocNumberFormat.Apply(3.5, code).Text);
        Assert.Equal(DocNumberFormat.General(3.5), DocNumberFormat.Apply(3.5, DocNumberFormat.Builtin(12)).Text);
    }

    /// <summary>Черта в подписи — не дробь: «руб/шт» остается подписью при числе.</summary>
    [Fact]
    public void A_slash_in_quotes_is_not_a_fraction()
    {
        Assert.Equal($"3{Point}50 руб/шт", DocNumberFormat.Apply(3.5, "0.00 \"руб/шт\"").Text);
    }
}
