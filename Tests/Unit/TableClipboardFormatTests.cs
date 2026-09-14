using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Формат прямоугольника ячеек в буфере обмена: табуляция между столбцами, перевод строки между
/// строками, кавычки вокруг значений с разделителями внутри. Главное требование — круг
/// «собрали и разобрали» не теряет и не подменяет значения, а текст, скопированный в табличном
/// редакторе, разбирается здесь так же, как там.
/// </summary>
public sealed class TableClipboardFormatTests
{
    private static string Build(params string[][] rows) =>
        TableClipboardFormat.Build(rows.Select(r => (IReadOnlyList<string>)r).ToList());

    [Fact]
    public void Columns_are_tab_separated_and_rows_newline_separated()
    {
        var text = Build(["Иванов", "Москва"], ["Петров", "Тверь"]);

        Assert.Equal("Иванов\tМосква\nПетров\tТверь", text);
    }

    [Fact]
    public void Single_cell_has_no_separators()
    {
        Assert.Equal("Иванов", Build(["Иванов"]));
    }

    /// <summary>Значение с табуляцией, переводом строки или кавычкой берется в кавычки, иначе
    /// одна ячейка при разборе развалилась бы на две.</summary>
    [Theory]
    [InlineData("до\tпосле", "\"до\tпосле\"")]
    [InlineData("две\nстроки", "\"две\nстроки\"")]
    [InlineData("он сказал \"да\"", "\"он сказал \"\"да\"\"\"")]
    [InlineData("обычный текст", "обычный текст")]
    [InlineData("", "")]
    public void Values_with_separators_are_quoted(string value, string expected)
    {
        Assert.Equal(expected, Build([value]));
    }

    [Fact]
    public void Round_trip_keeps_everything()
    {
        string[][] rows =
        [
            ["Иванов", "до\tпосле", "42,5"],
            ["", "две\nстроки", "он сказал \"да\""],
        ];

        var parsed = TableClipboardFormat.Parse(Build(rows));

        Assert.Equal(2, parsed.Count);
        Assert.Equal(rows[0], parsed[0]);
        Assert.Equal(rows[1], parsed[1]);
    }

    [Fact]
    public void Empty_text_parses_to_nothing()
    {
        Assert.Empty(TableClipboardFormat.Parse(""));
        Assert.Empty(TableClipboardFormat.Parse(null));
    }

    /// <summary>Случайный пробел в буфере — не значение: разобрав его как ячейку, вставка по
    /// выделенному столбцу стерла бы в нем все.</summary>
    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    public void Spaces_only_parse_to_nothing(string text)
    {
        Assert.Empty(TableClipboardFormat.Parse(text));
    }

    /// <summary>А вот пустая ячейка, скопированная как ячейка (с переводом строки), остается
    /// значением: ей очищают целевую.</summary>
    [Fact]
    public void Copied_empty_cell_is_still_a_value()
    {
        Assert.Equal([""], Assert.Single(TableClipboardFormat.Parse("\r\n")));
    }

    /// <summary>Метка порядка байтов — след кодировки, а не часть первого значения.</summary>
    [Fact]
    public void Byte_order_mark_is_dropped()
    {
        var parsed = TableClipboardFormat.Parse((char)0xFEFF + "Иванов\tМосква");

        Assert.Equal(["Иванов", "Москва"], parsed[0]);
    }

    /// <summary>Незакрытая кавычка — просто символ: иначе она проглотила бы весь хвост буфера,
    /// с табуляциями и переводами строк, в одно значение.</summary>
    [Fact]
    public void Unclosed_quote_is_an_ordinary_character()
    {
        var parsed = TableClipboardFormat.Parse("\"дюйм\tвторая\nтретья\tчетвертая");

        Assert.Equal(2, parsed.Count);
        Assert.Equal(["\"дюйм", "вторая"], parsed[0]);
        Assert.Equal(["третья", "четвертая"], parsed[1]);
    }

    /// <summary>Буфер приходит и из Windows-программ, где конец строки — «возврат каретки плюс
    /// перевод строки», и из старых, где только возврат каретки.</summary>
    [Theory]
    [InlineData("a\tb\r\nc\td")]
    [InlineData("a\tb\rc\td")]
    [InlineData("a\tb\nc\td")]
    public void All_line_endings_are_understood(string text)
    {
        var parsed = TableClipboardFormat.Parse(text);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(["a", "b"], parsed[0]);
        Assert.Equal(["c", "d"], parsed[1]);
    }

    /// <summary>Текст, закончившийся переводом строки, не дает лишней пустой строки — иначе
    /// вставка затирала бы строку под блоком.</summary>
    [Fact]
    public void Trailing_newline_does_not_add_an_empty_row()
    {
        Assert.Single(TableClipboardFormat.Parse("a\tb\n"));
        Assert.Single(TableClipboardFormat.Parse("a\tb\r\n"));
    }

    /// <summary>Табличные редакторы обрезают хвостовые пустые значения, поэтому строки бывают разной длины —
    /// выравнивать их нельзя, это затерло бы лишние ячейки.</summary>
    [Fact]
    public void Rows_may_have_different_length()
    {
        var parsed = TableClipboardFormat.Parse("a\tb\tc\nd");

        Assert.Equal(3, parsed[0].Count);
        Assert.Single(parsed[1]);
    }

    [Fact]
    public void Empty_cells_inside_a_row_survive()
    {
        var parsed = TableClipboardFormat.Parse("a\t\tc");

        Assert.Equal(["a", "", "c"], parsed[0]);
    }

    /// <summary>Кавычка в середине значения — обычный символ, а не начало экранирования: так же
    /// ведут себя табличные редакторы, и без этого «размер 5" труба» разобрался бы неверно.</summary>
    [Fact]
    public void Quote_inside_a_value_is_literal()
    {
        var parsed = TableClipboardFormat.Parse("размер 5\" труба\tвторая");

        Assert.Equal(["размер 5\" труба", "вторая"], parsed[0]);
    }
}
