using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Выражения шага «преобразовать строки» (<see cref="Formula"/>) — язык без ввода-вывода
/// (docs/ROADMAP-API.md, шаг 12).
///
/// Проверяется не «считается ли арифметика» — это нетрудно, — а обещания, на которых язык
/// держится: угаданный за человека тип не проходит молча, пустая колонка не превращается в ноль,
/// отказ говорит, ГДЕ споткнулись, а ленивые ветки ЕСЛИ не считаются зря.
/// </summary>
public sealed class FormulaTests
{
    /// <summary>Момент запуска по местным часам: его видит СЕГОДНЯ().</summary>
    private static readonly DateTimeOffset Moment =
        new(new DateTime(2026, 9, 13, 8, 30, 0, DateTimeKind.Local));

    private static readonly Dictionary<string, string> Nothing = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> Row(params (string Column, string Value)[] cells)
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, value) in cells) row[column] = value;
        return row;
    }

    /// <summary>Посчитать выражение; неудача разбора или счета — провал теста.</summary>
    private static string Value(string expression, IReadOnlyDictionary<string, string>? row = null,
                                DateTimeOffset? now = null)
    {
        var formula = Formula.TryParse(expression, out var parse);
        Assert.True(formula is not null, parse);

        var value = formula!.Run(row ?? Nothing, now ?? Moment, out var trouble);
        Assert.Equal("", trouble);

        return value;
    }

    /// <summary>Почему выражение не разобралось; разобравшееся — провал теста.</summary>
    private static string Unparsed(string expression)
    {
        var formula = Formula.TryParse(expression, out var trouble);

        Assert.Null(formula);
        Assert.NotEqual("", trouble);

        return trouble;
    }

    /// <summary>Почему выражение не посчиталось; посчитавшееся — провал теста.</summary>
    private static string Refused(string expression, IReadOnlyDictionary<string, string>? row = null)
    {
        var formula = Formula.TryParse(expression, out var parse);
        Assert.True(formula is not null, parse);

        formula!.Run(row ?? Nothing, Moment, out var trouble);
        Assert.NotEqual("", trouble);

        return trouble;
    }

    // ── Разбор ────────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_expression_is_refused()
    {
        Assert.Contains("не заполнено", Unparsed("   "));
    }

    [Fact]
    public void Too_long_expression_is_refused()
    {
        Assert.Contains("длиннее", Unparsed(new string('1', Formula.MaxLength + 1)));
    }

    [Theory]
    [InlineData("2 + (3 * 4",        "закрывающей скобки")]
    [InlineData("2 + 3)",            "закрывающих скобок больше")]
    [InlineData("\"незакрытый",      "не закрыт")]
    [InlineData("{{Цена",            "нет закрывающих")]
    [InlineData("{{}} + 1",          "не названа колонка")]
    [InlineData("2 % 3",             "не понятен")]
    [InlineData("2 +",               "обрывается")]
    public void Broken_text_is_refused_with_words(string expression, string what)
    {
        Assert.Contains(what, Unparsed(expression));
    }

    [Fact]
    public void Unknown_function_is_named()
    {
        Assert.Contains("ОКРУГЛИТЬ", Unparsed("ОКРУГЛИТЬ(2.5)"));
    }

    [Fact]
    public void Function_with_wrong_count_of_values_is_refused()
    {
        Assert.Contains("ЕСЛИ", Unparsed("ЕСЛИ(1 = 1; \"да\")"));
    }

    /// <summary>
    /// Вложенность ограничена не из вкуса: рекурсивный спуск на тысяче открывающих скобок уронил
    /// бы не выражение, а программу целиком.
    /// </summary>
    [Fact]
    public void Too_deep_nesting_is_refused()
    {
        var deep = new string('(', Formula.MaxDepth + 2) + "1"
                 + new string(')', Formula.MaxDepth + 2);

        Assert.Contains("вложенность", Unparsed(deep));
    }

    [Fact]
    public void Named_columns_are_listed_once_and_in_order()
    {
        var formula = Formula.TryParse("{{Цена}} * {{Кол}} + {{цена}}", out var trouble);

        Assert.True(formula is not null, trouble);
        Assert.Equal(["Цена", "Кол"], formula!.Columns);
    }

    // ── Арифметика ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2 + 3 * 4",       "14")]
    [InlineData("(2 + 3) * 4",     "20")]
    [InlineData("10 / 4",          "2.5")]
    [InlineData("-2 * 3",          "-6")]
    [InlineData("2 - -3",          "5")]
    [InlineData("1.5 + 1.5",       "3")]
    public void Arithmetic_counts(string expression, string expected)
    {
        Assert.Equal(expected, Value(expression));
    }

    /// <summary>
    /// Число из колонки читается и с запятой: в базе оно лежит сырым вводом, и «12,5» там
    /// обычное дело.
    /// </summary>
    [Fact]
    public void Column_with_comma_is_a_number()
    {
        Assert.Equal("50", Value("{{Цена}} * {{Количество}}",
                                 Row(("Цена", "12,5"), ("Количество", "4"))));
    }

    [Fact]
    public void Columns_are_found_regardless_of_case()
    {
        Assert.Equal("3", Value("{{цена}} + 1", Row(("Цена", "2"))));
    }

    [Fact]
    public void Empty_column_in_arithmetic_is_refused_not_zero()
    {
        var trouble = Refused("{{Цена}} + 1", Row(("Цена", "  ")));

        Assert.Contains("пусто", trouble);
        Assert.Contains("нужно число", trouble);
    }

    [Fact]
    public void Text_that_is_not_a_number_is_named_in_the_refusal()
    {
        Assert.Contains("около трех", Refused("{{Цена}} + 1", Row(("Цена", "около трех"))));
    }

    [Fact]
    public void Missing_column_stops_the_count()
    {
        Assert.Contains("колонки «Цена» в строке нет", Refused("{{Цена}} + 1", Row(("Сумма", "1"))));
    }

    [Fact]
    public void Division_by_zero_is_refused()
    {
        Assert.Contains("деление на ноль", Refused("10 / 0"));
    }

    // ── Текст ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("СЦЕПИТЬ(\"Заказ \"; 12)",        "Заказ 12")]
    [InlineData("ОБРЕЗАТЬ(\"  тут  \")",          "тут")]
    [InlineData("ЛЕВО(\"АБВГ.301121.001\"; 4)",   "АБВГ")]
    [InlineData("ПРАВО(\"АБВГ.301121.001\"; 3)",  "001")]
    [InlineData("ЛЕВО(\"АБ\"; 9)",                "АБ")]
    [InlineData("ДЛИНА(\"АБВ\")",                 "3")]
    [InlineData("ЗАМЕНИТЬ(\"а-б-в\"; \"-\"; \" \")", "а б в")]
    [InlineData("ВЕРХНИЙ(\"счет\")",              "СЧЕТ")]
    [InlineData("НИЖНИЙ(\"СЧЕТ\")",               "счет")]
    public void Text_functions_work(string expression, string expected)
    {
        Assert.Equal(expected, Value(expression));
    }

    /// <summary>Кавычка внутри текста пишется двумя — своего знака экранирования тут нет.</summary>
    [Fact]
    public void Doubled_quote_is_a_quote()
    {
        Assert.Equal("ООО \"Ромашка\"", Value("\"ООО \"\"Ромашка\"\"\""));
    }

    /// <summary>
    /// Склейки знаком «плюс» нет намеренно: «1» + «2» означало бы то 3, то «12» — в зависимости
    /// от того, что пришло в колонке.
    /// </summary>
    [Fact]
    public void Plus_does_not_glue_text()
    {
        Assert.Contains("не число", Refused("\"Заказ \" + \"12\""));
    }

    // ── Число из текста ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("ЧИСЛО(\"1 250 руб.\")",  "1250")]
    [InlineData("ЧИСЛО(\"1 234,50\")",    "1234.5")]
    [InlineData("ЧИСЛО(\"-3\")",          "-3")]
    public void Number_from_text_is_tolerant(string expression, string expected)
    {
        Assert.Equal(expected, Value(expression));
    }

    [Fact]
    public void Number_from_text_without_digits_is_refused()
    {
        Assert.Contains("числа нет", Refused("ЧИСЛО(\"около трех\")"));
    }

    [Theory]
    [InlineData("ОКРУГЛ(2.5; 0)",    "3")]
    [InlineData("ОКРУГЛ(1.2345; 2)", "1.23")]
    [InlineData("ОКРУГЛ(-2.5; 0)",   "-3")]
    public void Rounding_sends_halves_away_from_zero(string expression, string expected)
    {
        Assert.Equal(expected, Value(expression));
    }

    [Fact]
    public void Rounding_to_strange_places_is_refused()
    {
        Assert.Contains("от 0 до 10", Refused("ОКРУГЛ(2.5; 42)"));
    }

    // ── Условия ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1 = 1",       "да")]
    [InlineData("1 <> 1",      "нет")]
    [InlineData("2 > 10",      "нет")]
    [InlineData("\"10\" > 9",  "да")]
    public void Comparison_answers_yes_or_no(string expression, string expected)
    {
        Assert.Equal(expected, Value(expression));
    }

    /// <summary>Регистр в сравнении не учитывается — как и в отборе строк.</summary>
    [Fact]
    public void Equality_ignores_case()
    {
        Assert.Equal("да", Value("{{Статус}} = \"Оплачен\"", Row(("Статус", "оплачен"))));
    }

    /// <summary>
    /// «Больше» и «меньше» над текстом не работают: сравнивать слова по величине значило бы
    /// угадывать за человека, что «10» меньше «9». От этого отказались условия отбора.
    /// </summary>
    [Fact]
    public void Greater_than_over_text_is_refused()
    {
        Assert.Contains("больше", Refused("\"абв\" > \"а\""));
    }

    [Theory]
    [InlineData("ЕСЛИ(1 = 1; \"да\"; \"нет\")",       "да")]
    [InlineData("ЕСЛИ(1 = 2; \"да\"; \"нет\")",       "нет")]
    [InlineData("НЕ(1 = 2)",                          "да")]
    [InlineData("И(1 = 1; 2 = 2)",                    "да")]
    [InlineData("ИЛИ(1 = 2; 2 = 2)",                  "да")]
    public void Conditions_work(string expression, string expected)
    {
        Assert.Equal(expected, Value(expression));
    }

    /// <summary>
    /// Ветки ЕСЛИ считаются ЛЕНИВО, и это не оптимизация, а смысл: иначе выражение, написанное
    /// ради пустой колонки, на ней же и отказывало бы.
    /// </summary>
    [Fact]
    public void If_counts_only_the_branch_it_took()
    {
        Assert.Equal("0", Value("ЕСЛИ(ПУСТО({{Сумма}}); 0; ЧИСЛО({{Сумма}}))",
                                Row(("Сумма", ""))));
    }

    [Fact]
    public void And_stops_at_the_first_no()
    {
        Assert.Equal("нет", Value("И(1 = 2; ЧИСЛО({{Сумма}}) > 0)", Row(("Сумма", "мусор"))));
    }

    [Theory]
    [InlineData("да",  "да")]
    [InlineData("1",   "да")]
    [InlineData("нет", "нет")]
    [InlineData("",    "нет")]
    public void Checkbox_like_text_reads_as_yes_or_no(string value, string expected)
    {
        Assert.Equal(expected, Value("ЕСЛИ({{Весь день}}; \"да\"; \"нет\")",
                                     Row(("Весь день", value))));
    }

    [Fact]
    public void Text_that_is_neither_yes_nor_no_is_refused()
    {
        Assert.Contains("не «да» и не «нет»",
                        Refused("ЕСЛИ({{Статус}}; 1; 2)", Row(("Статус", "Оплачен"))));
    }

    [Theory]
    [InlineData("",     "да")]
    [InlineData("   ",  "да")]
    [InlineData("что",  "нет")]
    public void Blank_is_about_spaces_too(string value, string expected)
    {
        Assert.Equal(expected, Value("ПУСТО({{Проект}})", Row(("Проект", value))));
    }

    // ── Даты ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Date_plus_days_is_a_date()
    {
        Assert.Equal("02.03.2026", Value("ДАТА({{Срок}}) + 30", Row(("Срок", "31.01.2026"))));
    }

    [Fact]
    public void Date_minus_date_is_days()
    {
        Assert.Equal("30", Value("ДАТА(\"02.03.2026\") - ДАТА(\"31.01.2026\")"));
    }

    [Fact]
    public void Today_is_the_moment_the_scenario_ran()
    {
        Assert.Equal("13.09.2026", Value("СЕГОДНЯ()"));
    }

    [Fact]
    public void Today_plus_days_counts_from_that_moment()
    {
        Assert.Equal("23.09.2026", Value("СЕГОДНЯ() + 10"));
    }

    /// <summary>Время в дате сохраняется — так его приносит календарный триггер.</summary>
    [Fact]
    public void Time_survives_the_date()
    {
        Assert.Equal("14.09.2026 10:00", Value("ДАТА({{Начало}}) + 1",
                                               Row(("Начало", "13.09.2026 10:00"))));
    }

    /// <summary>Канон хранения из базы разбирается так же, как человеческая запись.</summary>
    [Fact]
    public void Stored_iso_date_is_understood()
    {
        Assert.Equal("01.02.2026", Value("ДАТА({{Дата}})", Row(("Дата", "2026-02-01"))));
    }

    [Fact]
    public void Dates_compare_as_dates()
    {
        Assert.Equal("да", Value("ДАТА({{Срок}}) < СЕГОДНЯ()", Row(("Срок", "01.01.2026"))));
    }

    /// <summary>
    /// Текст в дату сам собой не превращается: «10.05» — это десятое мая или десять и пять
    /// сотых? Двусмысленность решает функция ДАТА, а не догадка программы.
    /// </summary>
    [Fact]
    public void Text_is_not_a_date_by_itself()
    {
        Assert.Contains("ДАТА", Refused("{{Срок}} + 1", Row(("Срок", "01.02.2026"))));
    }

    [Fact]
    public void Text_that_is_not_a_date_is_refused_with_a_sample()
    {
        Assert.Contains("01.02.2026", Refused("ДАТА({{Срок}})", Row(("Срок", "когда-нибудь"))));
    }
}
