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

    [Theory]
    [InlineData("bg:#ff3b30", "bg:#FF3B30")]
    [InlineData("fg:#34c759", "fg:#34C759")]
    [InlineData("bg:", "bg:")]
    [InlineData("fg:", "fg:")]
    public void Remembered_color_sort_becomes_a_color_filter(string token, string expected)
    {
        Dictionary<string, HashSet<string>> filters = [];
        string? sortKey = ColorQuery.SortKey("12", token);
        var sortAsc = false;

        Assert.True(ColorQuery.MigrateSortToFilter(filters, ref sortKey, ref sortAsc));

        Assert.Null(sortKey);
        Assert.True(sortAsc);
        Assert.Single(filters);
        Assert.Equal([expected], filters[ColorQuery.FilterKey("12")]);
    }

    [Fact]
    public void Migrating_color_sort_preserves_existing_color_and_value_filters()
    {
        HashSet<string> colors = ["bg:#34C759", "fg:"];
        HashSet<string> values = ["A", "B"];
        Dictionary<string, HashSet<string>> filters = new()
        {
            [ColorQuery.FilterKey("12")] = colors,
            ["12"] = values,
            [ColorQuery.FilterKey("13")] = ["bg:"],
        };
        string? sortKey = ColorQuery.SortKey("12", "bg:#FF3B30");
        var sortAsc = false;

        Assert.True(ColorQuery.MigrateSortToFilter(filters, ref sortKey, ref sortAsc));

        Assert.Null(sortKey);
        Assert.True(sortAsc);
        Assert.Equal(3, filters.Count);
        Assert.Same(colors, filters[ColorQuery.FilterKey("12")]);
        Assert.True(colors.SetEquals(["bg:#34C759", "fg:"]));
        Assert.Same(values, filters["12"]);
        Assert.True(values.SetEquals(["A", "B"]));
        Assert.Equal(["bg:"], filters[ColorQuery.FilterKey("13")]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("12")]
    [InlineData("__title__")]
    [InlineData("color:12")]
    [InlineData("color:12|мусор")]
    [InlineData("color:|bg:")]
    public void Migrating_ordinary_or_invalid_sort_changes_nothing(string? oldSortKey)
    {
        HashSet<string> values = ["A"];
        Dictionary<string, HashSet<string>> filters = new() { ["12"] = values };
        var sortKey = oldSortKey;
        var sortAsc = false;

        Assert.False(ColorQuery.MigrateSortToFilter(filters, ref sortKey, ref sortAsc));

        Assert.Equal(oldSortKey, sortKey);
        Assert.False(sortAsc);
        Assert.Single(filters);
        Assert.Same(values, filters["12"]);
        Assert.Equal(["A"], values);
    }

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

    [Theory]
    [InlineData("#FF3B30", "#FF3B30", true)]
    [InlineData("#34C759", "#ff3b30", true)]
    [InlineData("#FF3B30", null, false)]
    [InlineData(null, "#FF3B30", false)]
    [InlineData("#123456", "#FF3B30", false)]
    public void Multiple_backgrounds_are_alternatives_but_text_is_another_condition(string? bg, string? fg, bool expected) =>
        Assert.Equal(expected, ColorQuery.MatchesFilter(new RowStyle(bg, fg),
            ["bg:#FF3B30", "bg:#34C759", "fg:#FF3B30"]));

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("#FF3B30", null, true)]
    [InlineData(null, "#FF3B30", true)]
    [InlineData("#FF3B30", "#FF3B30", true)]
    [InlineData("#34C759", null, false)]
    [InlineData(null, "#34C759", false)]
    public void No_fill_and_default_text_can_be_selected_alongside_colors(string? bg, string? fg, bool expected) =>
        Assert.Equal(expected, ColorQuery.MatchesFilter(new RowStyle(bg, fg),
            ["bg:", "bg:#FF3B30", "fg:", "fg:#FF3B30"]));

    [Fact]
    public void Empty_color_selection_does_not_filter()
    {
        Assert.True(ColorQuery.MatchesFilter(new RowStyle("#FF3B30", "#34C759"), []));
        Assert.True(ColorQuery.MatchesFilter(RowStyle.None, []));
    }

    [Fact]
    public void Invalid_tokens_do_not_turn_a_color_filter_into_all_records()
    {
        Assert.False(ColorQuery.MatchesFilter(RowStyle.None, ["мусор"]));
        Assert.False(ColorQuery.MatchesFilter(RowStyle.None, ["мусор", "bg:#FF3B30"]));
        Assert.True(ColorQuery.MatchesFilter(new RowStyle("#ff3b30", null), ["мусор", "bg:#FF3B30"]));
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
