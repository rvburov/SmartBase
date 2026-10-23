using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Ключи и метки отбора и сортировки по цвету (<see cref="ColorQuery"/>): разбор туда и обратно,
/// колонка особого ключа и совпадение с видимым цветом ячейки.
/// </summary>
public sealed class ColorQueryTests
{
    [Fact]
    public void Filter_key_points_to_its_column()
    {
        var key = ColorQuery.FilterKey("12");

        Assert.True(ColorQuery.IsColorKey(key));
        Assert.Equal("12", ColorQuery.ColumnOf(key));
        Assert.False(ColorQuery.TryParseSortKey(key, out _, out _));
    }

    [Fact]
    public void Plain_key_is_its_own_column()
    {
        Assert.False(ColorQuery.IsColorKey("12"));
        Assert.Equal("12", ColorQuery.ColumnOf("12"));
        Assert.Equal("__title__", ColorQuery.ColumnOf("__title__"));
    }

    [Fact]
    public void Sort_key_round_trips()
    {
        var token = ColorQuery.Token(StyleAspect.Background, "#ff3b30");
        var key   = ColorQuery.SortKey("__title__", token);

        Assert.True(ColorQuery.TryParseSortKey(key, out var column, out var parsed));
        Assert.Equal("__title__", column);
        Assert.Equal("bg:#FF3B30", parsed);
        Assert.Equal("__title__", ColorQuery.ColumnOf(key));
    }

    [Theory]
    [InlineData("color:12|xx:#FF3B30")]
    [InlineData("color:12|bg:красный")]
    [InlineData("color:|bg:#FF3B30")]
    [InlineData("12")]
    [InlineData(null)]
    public void Broken_sort_key_is_not_a_color_sort(string? key) =>
        Assert.False(ColorQuery.TryParseSortKey(key, out _, out _));

    [Fact]
    public void Empty_hex_means_no_fill_and_default_text()
    {
        Assert.Equal("bg:", ColorQuery.Token(StyleAspect.Background, null));
        Assert.Equal("fg:", ColorQuery.Token(StyleAspect.Text, "мусор"));
        Assert.Equal("Без заливки", ColorQuery.Name("bg:"));
        Assert.Equal("Стандартный", ColorQuery.Name("fg:"));
    }

    [Fact]
    public void Palette_color_is_named()
    {
        Assert.Equal("Красный", ColorQuery.Name("bg:#FF3B30"));
        Assert.Equal("#123456", ColorQuery.Name("fg:#123456"));
    }

    [Fact]
    public void Matches_compares_the_visible_color_of_its_aspect()
    {
        var style = new RowStyle("#ff3b30", null);

        Assert.True(ColorQuery.Matches(style, "bg:#FF3B30"));
        Assert.False(ColorQuery.Matches(style, "bg:#34C759"));
        Assert.False(ColorQuery.Matches(style, "bg:"));
        Assert.True(ColorQuery.Matches(style, "fg:"));
        Assert.False(ColorQuery.Matches(style, "fg:#FF3B30"));
        Assert.False(ColorQuery.Matches(style, "мусор"));
    }

    /// <summary>Совпадение — по видимому цвету, а не по уровню: ячейка без своего цвета в
    /// окрашенной строке — цвета строки.</summary>
    [Fact]
    public void Row_color_counts_for_cells_without_their_own()
    {
        var row     = new RowStyle("#34C759", null, At: new StyleStamps(2, 0, 0, 0));
        var visible = RowStyle.Resolve(RowStyle.None, row, RowStyle.None);

        Assert.True(ColorQuery.Matches(visible, "bg:#34C759"));
    }
}
