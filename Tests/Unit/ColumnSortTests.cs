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

    [Theory]
    [InlineData("created_at")]
    [InlineData("updated_at")]
    public void Audit_dates_sort_by_instant_with_precision_and_keep_legacy_empty_last(string type)
    {
        var earlier = "2026-10-28T08:00:00.0000001+02:00";
        var same = "2026-10-28T06:00:00.0000001Z";
        var nextTick = "2026-10-28T06:00:00.0000002Z";
        var later = "2026-10-28T06:30:00.0000000Z";
        foreach (var ascending in new[] { true, false })
        {
            var compare = ColumnSort.BuildComparer(type, ascending);
            Assert.Equal(0, compare(earlier, same));
            Assert.Equal(ascending ? -1 : 1, Math.Sign(compare(earlier, nextTick)));
            Assert.Equal(ascending ? -1 : 1, Math.Sign(compare(earlier, later)));
            Assert.True(compare("", earlier) > 0);
            Assert.True(compare("не дата", earlier) > 0);
            Assert.Equal(0, compare("", "не дата"));
        }
        Assert.Equal(ColumnSort.AuditDateKey(same), ColumnSort.AuditDateKey("2026-10-28T06:00:00.0000001"));
        Assert.Equal(1, ColumnSort.AuditDateKey(nextTick) - ColumnSort.AuditDateKey(same));
    }
}
