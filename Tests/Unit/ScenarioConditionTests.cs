using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Условия отбора (<see cref="ScenarioCondition"/>) — то самое «ЕСЛИ» из плана.
///
/// Логика чистая: строка приходит словарем, ответ «подходит или нет». Проверяется ровно то, на
/// чем отбор врет молча, — регистр, крайние пробелы, отсутствующая колонка и маска, которая не
/// должна превратиться в регулярное выражение.
/// </summary>
public sealed class ScenarioConditionTests
{
    private static Dictionary<string, string> Row(params (string Column, string Value)[] values) =>
        values.ToDictionary(v => v.Column, v => v.Value, StringComparer.OrdinalIgnoreCase);

    private static bool Holds(ScenarioTest test, string value, string sample = "") =>
        new ScenarioCondition("Проект", test, sample).Holds(Row(("Проект", value)));

    // ── Заполнено и пусто ─────────────────────────────────────────────────────

    [Fact]
    public void Filled_is_about_having_something_to_read()
    {
        Assert.True (Holds(ScenarioTest.Filled, "Молоток"));
        Assert.False(Holds(ScenarioTest.Filled, ""));
        Assert.False(Holds(ScenarioTest.Filled, "   "));
    }

    [Fact]
    public void Empty_is_the_other_half()
    {
        Assert.True (Holds(ScenarioTest.Empty, "   "));
        Assert.False(Holds(ScenarioTest.Empty, "Молоток"));
    }

    /// <summary>
    /// Колонки в строке нет — условие не выполняется. Само по себе это не ответ на вопрос
    /// «почему»: о пропавшей колонке говорит ШАГ, и говорит словами. Здесь важно другое —
    /// отсутствие колонки не роняет отбор.
    /// </summary>
    [Fact]
    public void A_missing_column_does_not_throw()
    {
        var condition = new ScenarioCondition("Заказчик", ScenarioTest.Filled);

        Assert.False(condition.Holds(Row(("Проект", "Молоток"))));
    }

    // ── Сравнение ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Регистр не учитывается нигде. Данные приходят из чужой программы, где «Оплачен» и
    /// «оплачен» — одно слово, и требовать от человека угадать написание значило бы отбирать не
    /// те строки по причине, которой в отчете не видно.
    /// </summary>
    [Fact]
    public void Case_never_matters()
    {
        Assert.True(Holds(ScenarioTest.Equals,   "ОПЛАЧЕН", "оплачен"));
        Assert.True(Holds(ScenarioTest.Contains, "Счет ОПЛАЧЕН полностью", "оплачен"));
        Assert.True(Holds(ScenarioTest.Matches,  "АБВГ.301121.001", "абвг.*"));
    }

    /// <summary>Крайние пробелы снимаются с обеих сторон: их приносит чужая выгрузка.</summary>
    [Fact]
    public void Edge_spaces_are_trimmed_on_both_sides()
    {
        Assert.True(Holds(ScenarioTest.Equals, "  Молоток  ", " Молоток "));
    }

    [Fact]
    public void Not_equals_is_the_mirror_of_equals()
    {
        Assert.True (Holds(ScenarioTest.NotEquals, "Рубанок", "Молоток"));
        Assert.False(Holds(ScenarioTest.NotEquals, "Молоток", "Молоток"));
    }

    [Fact]
    public void Contains_looks_inside()
    {
        Assert.True (Holds(ScenarioTest.Contains, "Кронштейн сварной", "сварн"));
        Assert.False(Holds(ScenarioTest.Contains, "Кронштейн сварной", "литой"));
    }

    // ── Маска ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Маска из двух знаков: <c>*</c> — любое число знаков, <c>?</c> — ровно один. Это НЕ
    /// регулярное выражение, и точка в маске значит точку, а не «любой знак»: обозначения по
    /// ГОСТ состоят из точек, и первое же выражение отобрало бы не то.
    /// </summary>
    [Theory]
    [InlineData("АБВГ.301121.001", "АБВГ.*",           true)]
    [InlineData("АБВГ.301121.001", "*.001",            true)]
    [InlineData("АБВГ.301121.001", "АБВГ.??????.???",  true)]
    [InlineData("АБВГ.301121.001", "АБВГ.?????.???",   false)]
    [InlineData("ЖЗИК.301121.001", "АБВГ.*",           false)]
    [InlineData("АБВГ.301121.001", "*",                true)]
    [InlineData("АБВГ.301121.001", "АБВГ.301121.001",  true)]
    [InlineData("АБВГхЗ01121х001", "АБВГ.301121.001",  false)]
    public void A_mask_is_two_signs_and_nothing_more(string text, string mask, bool expected)
    {
        Assert.Equal(expected, ScenarioCondition.MaskMatches(text, mask));
    }

    [Fact]
    public void A_star_matches_nothing_too()
    {
        Assert.True(ScenarioCondition.MaskMatches("АБВГ.", "АБВГ.*"));
        Assert.True(ScenarioCondition.MaskMatches("", "*"));
    }

    // ── Форма ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_condition_needs_a_column()
    {
        Assert.Single(new ScenarioCondition("  ", ScenarioTest.Filled).Problems());
    }

    /// <summary>Образец нужен не всем: «заполнено» и «пусто» спрашивают о самом значении.</summary>
    [Fact]
    public void Only_some_tests_need_a_sample()
    {
        Assert.Empty(new ScenarioCondition("Проект", ScenarioTest.Filled).Problems());
        Assert.Single(new ScenarioCondition("Проект", ScenarioTest.Equals).Problems());
        Assert.Empty(new ScenarioCondition("Проект", ScenarioTest.Equals, "Молоток").Problems());
    }

    [Fact]
    public void A_sample_longer_than_the_limit_is_a_problem()
    {
        var long_ = new string('я', ScenarioCondition.MaxValueLength + 1);

        Assert.Single(new ScenarioCondition("Проект", ScenarioTest.Contains, long_).Problems());
    }

    /// <summary>Условие читается словами: его видно и в списке шагов, и в отчете о запуске.</summary>
    [Fact]
    public void A_condition_reads_as_a_phrase()
    {
        Assert.Equal("Проект заполнено",
                     new ScenarioCondition("Проект", ScenarioTest.Filled).ToString());

        Assert.Equal("Статус равно Оплачен",
                     new ScenarioCondition("Статус", ScenarioTest.Equals, "Оплачен").ToString());
    }
}
