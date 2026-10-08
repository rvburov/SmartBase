using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Правка ячейки набором: какой правкой набор открывает ячейку каждого типа и какой пункт списка
/// выбирает набранное. Таблица только переводит ввод в <see cref="CellTyping"/>, поэтому правила
/// проверяются здесь без сетки; сам набор в таблице — стенд GridProto --edit.
/// </summary>
public sealed class CellTypingTests
{
    private static readonly string[] Statuses = ["Новая", "В работе", "Выполнено", "Готово", "Где-то", "Ёлка"];

    [Theory]
    [InlineData("text", "п")]
    [InlineData("text", "Z")]
    [InlineData("text", "5")]
    [InlineData("text", " ")]
    [InlineData("text", "@")]
    [InlineData("url", "h")]
    [InlineData("number", "7")]
    [InlineData("number", "-")]
    [InlineData("number", ",")]
    [InlineData("number", ".")]
    public void Text_like_cells_open_as_a_text_field(string type, string typed) =>
        Assert.Equal(CellTypingStart.Text, CellTyping.Start(type, typed));

    [Fact]
    public void An_unknown_type_is_edited_as_text_like_its_editor()
    {
        // Редактор неизвестного типа — поле ввода (ветка по умолчанию BuildCellEditor).
        Assert.Equal(CellTypingStart.Text, CellTyping.Start("something-new", "а"));
        Assert.Equal(CellTypingStart.Text, CellTyping.Start(null, "а"));
    }

    [Theory]
    [InlineData("number", "a")]
    [InlineData("number", "+")]
    [InlineData("number", " ")]
    [InlineData("date", "a")]
    [InlineData("date", ".")]
    [InlineData("date", " ")]
    [InlineData("select", " ")]
    [InlineData("status", "  ")]
    public void Typing_that_does_not_fit_the_type_does_not_open_the_cell(string type, string typed) =>
        Assert.Equal(CellTypingStart.None, CellTyping.Start(type, typed));

    [Theory]
    [InlineData("select")]
    [InlineData("status")]
    [InlineData("user")]
    public void Lists_open_with_a_search_by_the_typed_letter(string type)
    {
        Assert.Equal(CellTypingStart.List, CellTyping.Start(type, "в"));
        Assert.Equal(CellTypingStart.List, CellTyping.Start(type, "1"));
    }

    [Fact]
    public void A_digit_opens_the_date_as_manual_input() =>
        Assert.Equal(CellTypingStart.Date, CellTyping.Start("date", "0"));

    [Theory]
    [InlineData("multiselect")]
    [InlineData("relation")]
    public void Multiselect_and_relation_open_their_picker_with_the_typed_search(string type)
    {
        Assert.Equal(CellTypingStart.Picker, CellTyping.Start(type, "ф"));
        Assert.Equal(CellTypingStart.Picker, CellTyping.Start(type, "7"));
        Assert.Equal(CellTypingStart.None,   CellTyping.Start(type, " "));
    }

    [Theory]
    [InlineData("checkbox")]
    [InlineData("file")]
    [InlineData("created_at")]
    [InlineData("updated_at")]
    [InlineData("created_by")]
    [InlineData("updated_by")]
    public void Cells_edited_not_by_text_ignore_typing(string type)
    {
        Assert.Equal(CellTypingStart.None, CellTyping.Start(type, "а"));
        Assert.Equal(CellTypingStart.None, CellTyping.Start(type, "1"));
    }

    [Fact]
    public void Control_characters_and_empty_input_are_keys_not_text()
    {
        // Ctrl+Backspace приходит символом DEL, Tab и Enter — своими кодами.
        foreach (var code in new[] { 8, 9, 10, 13, 27, 127 })
            Assert.Equal(CellTypingStart.None, CellTyping.Start("text", ((char)code).ToString()));
        Assert.Equal(CellTypingStart.None, CellTyping.Start("text", ""));
        Assert.Equal(CellTypingStart.None, CellTyping.Start("text", null));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("-", true)]
    [InlineData("12", true)]
    [InlineData("12,", true)]
    [InlineData("-3.5", true)]
    [InlineData("1,2,3", false)]
    [InlineData("--1", false)]
    [InlineData("1-", false)]
    [InlineData("1 000", false)]
    [InlineData("x", false)]
    public void Partial_number_allows_unfinished_input_only(string text, bool ok) =>
        Assert.Equal(ok, CellTyping.IsPartialNumber(text));

    [Fact]
    public void The_first_letter_picks_the_first_option_starting_with_it()
    {
        Assert.Equal(("г", 3), CellTyping.FindOption(Statuses, "", "г", current: 0));
        // Регистр не важен.
        Assert.Equal(("Г", 3), CellTyping.FindOption(Statuses, "", "Г", current: -1));
    }

    [Fact]
    public void Next_letters_refine_the_search()
    {
        var (term, index) = CellTyping.FindOption(Statuses, "", "в", current: 0);
        Assert.Equal(("в", 1), (term, index));
        (term, index) = CellTyping.FindOption(Statuses, term, "ы", index);
        Assert.Equal(("вы", 2), (term, index));
    }

    [Fact]
    public void A_space_continues_a_phrase()
    {
        var (term, index) = CellTyping.FindOption(Statuses, "в", " ", current: 1);
        Assert.Equal(("в ", 1), (term, index));
        Assert.Equal(("в р", 1), CellTyping.FindOption(Statuses, term, "р", index));
    }

    [Fact]
    public void Yo_and_ye_are_the_same_letter_both_ways()
    {
        Assert.Equal(("е", 5), CellTyping.FindOption(Statuses, "", "е", current: 0));
        Assert.Equal(1, CellTyping.IndexOf(["Ёж", "В работе"], "в"));
        Assert.Equal(0, CellTyping.IndexOf(["Елка"], "ёл"));
    }

    [Fact]
    public void A_typed_text_that_matches_nothing_starts_over_from_the_new_letter()
    {
        // «го» нашло «Готово»; «гон» не подходит ни к чему — набор начинается заново с «н».
        Assert.Equal(("н", 0), CellTyping.FindOption(Statuses, "го", "н", current: 3));
    }

    [Fact]
    public void Repeating_one_letter_walks_through_the_options_starting_with_it()
    {
        var (term, index) = CellTyping.FindOption(Statuses, "", "г", current: 0);
        Assert.Equal(3, index);
        (term, index) = CellTyping.FindOption(Statuses, term, "г", index);
        Assert.Equal(("г", 4), (term, index));
        (term, index) = CellTyping.FindOption(Statuses, term, "г", index);
        Assert.Equal(("г", 3), (term, index));   // по кругу
    }

    [Fact]
    public void Nothing_found_keeps_the_selection()
    {
        Assert.Equal(("я", -1), CellTyping.FindOption(Statuses, "", "я", current: 2));
        Assert.Equal(("я", -1), CellTyping.FindOption(Statuses, "в", "я", current: 1));
        Assert.Equal(-1, CellTyping.IndexOf(Statuses, ""));
        Assert.Equal(-1, CellTyping.IndexOf([], "а"));
        Assert.Equal(("а", -1), CellTyping.FindOption([], "", "а", current: -1));
    }
}
