using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Русское согласование с числом (<see cref="Plural"/>).
///
/// Тест существует ради одной ловушки: одиннадцать–четырнадцать берут форму «много» вопреки
/// последней цифре. «11 документа» — типовая ошибка самодельных вариантов, и именно она здесь
/// закреплена.
/// </summary>
public sealed class PluralTests
{
    [Theory]
    [InlineData(1,   "документ")]
    [InlineData(2,   "документа")]
    [InlineData(4,   "документа")]
    [InlineData(5,   "документов")]
    [InlineData(0,   "документов")]
    [InlineData(21,  "документ")]
    [InlineData(22,  "документа")]
    [InlineData(25,  "документов")]
    [InlineData(101, "документ")]
    public void Ordinary_numbers_follow_the_last_digit(int n, string expected) =>
        Assert.Equal(expected, Plural.Documents(n));

    // Вот она, ловушка: последняя цифра говорит «один» и «два», а правильная форма — «много».
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(111)]
    [InlineData(112)]
    [InlineData(1013)]
    public void Eleven_to_fourteen_always_take_the_many_form(int n) =>
        Assert.Equal("документов", Plural.Documents(n));

    [Fact]
    public void Count_puts_the_number_before_the_word() =>
        Assert.Equal("5 страниц", Plural.Count(5, "страница", "страницы", "страниц"));

    // Отрицательных чисел в интерфейсе быть не должно, но падать на них тоже незачем.
    [Fact]
    public void A_negative_number_does_not_break_the_rule() =>
        Assert.Equal("документа", Plural.Documents(-2));

    // ── Родительный падеж ────────────────────────────────────────────────────
    //
    // После «около» правило ДРУГОЕ: «около часа», но «около двух часов» — там, где обычное
    // согласование дало бы «часа». Перепутать их значит получить «около 1 час».

    [Theory]
    [InlineData(1,  "часа")]
    [InlineData(2,  "часов")]
    [InlineData(3,  "часов")]
    [InlineData(5,  "часов")]
    [InlineData(11, "часов")]
    [InlineData(21, "часа")]
    [InlineData(22, "часов")]
    public void After_a_preposition_only_one_takes_the_singular(int n, string expected) =>
        Assert.Equal(expected, Plural.GenitiveHours(n));

    [Fact]
    public void The_two_rules_really_differ()
    {
        // Именительный: «2 часа». Родительный: «около 2 часов».
        Assert.Equal("часа",  Plural.Of(2, "час", "часа", "часов"));
        Assert.Equal("часов", Plural.GenitiveHours(2));
    }
}
