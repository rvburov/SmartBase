using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>Порядок сортировки колонки (<see cref="ColumnSort"/>): какие типы идут по индексу,
/// ключи числа и даты, прежний компаратор с пустыми в конце.</summary>
public sealed class ColumnSortTests
{
    [Theory]
    [InlineData("text", true)]
    [InlineData("date", true)]      // ISO-канон: байтовый порядок хронологический
    [InlineData("select", true)]
    [InlineData("checkbox", true)]
    [InlineData("file", true)]
    [InlineData("number", false)]   // «10» раньше «9» — нужен типизированный ключ
    [InlineData("relation", false)]
    [InlineData("created_at", false)]
    public void Index_sortable_types(string type, bool expected) =>
        Assert.Equal(expected, ColumnSort.IsIndexSortable(type));

    [Fact]
    public void NumberKey_parses_both_separators_and_sends_junk_to_the_end()
    {
        Assert.Equal(2.5, ColumnSort.NumberKey("2,5"));
        Assert.Equal(2.5, ColumnSort.NumberKey("2.5"));
        Assert.Equal(10,  ColumnSort.NumberKey("10"));
        Assert.True(double.IsPositiveInfinity(ColumnSort.NumberKey("")));
        Assert.True(double.IsPositiveInfinity(ColumnSort.NumberKey("abc")));
    }

    [Fact]
    public void Comparer_keeps_empty_last_in_both_directions()
    {
        var asc  = ColumnSort.BuildComparer("number", asc: true);
        var desc = ColumnSort.BuildComparer("number", asc: false);
        Assert.True(asc("9", "10") < 0);
        Assert.True(desc("9", "10") > 0);
        Assert.True(asc("", "10") > 0);
        Assert.True(desc("", "10") > 0);
        Assert.Equal(0, asc("", ""));
    }

    [Fact]
    public void Text_comparer_ignores_case_by_culture()
    {
        var asc = ColumnSort.BuildComparer("text", asc: true);
        Assert.Equal(0, asc("Москва", "москва"));
        Assert.True(asc("абрикос", "Яблоко") < 0);
    }
}
