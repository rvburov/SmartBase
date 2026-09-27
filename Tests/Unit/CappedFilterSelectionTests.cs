using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Галочки окна фильтра колонки, у которой значений больше, чем показывает список
/// (<see cref="CappedFilterSelection"/>): все значения в памяти не держатся, поэтому галочки —
/// правило «все, кроме снятых» или «только отмеченные», а снятие значения из «всех» дает отбор
/// «кроме» (<see cref="FilterExclusion"/>). Поиск — как в <see cref="FilterSelection"/>.
/// </summary>
public sealed class CappedFilterSelectionTests
{
    [Fact]
    public void Without_filter_everything_is_checked_and_no_filter_results()
    {
        var sel = new CappedFilterSelection(null);

        Assert.True(sel.AllChecked);
        Assert.True(sel.IsChecked("любое, даже не показанное"));
        Assert.Null(sel.Effective([]));
    }

    [Fact]
    public void Unchecking_from_all_gives_exclude_filter()
    {
        // Снятую галочку нельзя выразить списком допустимых — всех значений окно не знает.
        var sel = new CappedFilterSelection(null);
        sel.Set("b", false);

        var f = sel.Effective([])!;
        Assert.True(FilterExclusion.IsExclude(f));
        Assert.Equal(["b"], FilterExclusion.Values(f));
        Assert.False(sel.IsChecked("b"));
        Assert.True(sel.IsChecked("c"));

        sel.Set("b", true);   // вернули — фильтра снова нет
        Assert.Null(sel.Effective([]));
    }

    [Fact]
    public void Select_all_off_then_checking_gives_include_filter()
    {
        var sel = new CappedFilterSelection(null);
        sel.SetAll(false, []);
        sel.Set("a", true);

        var f = sel.Effective([])!;
        Assert.False(FilterExclusion.IsExclude(f));
        Assert.Equal(["a"], f);
    }

    [Fact]
    public void Reopens_with_current_include_and_exclude_filters()
    {
        var include = new CappedFilterSelection(new HashSet<string> { "a" });
        Assert.True(include.IsChecked("a"));
        Assert.False(include.IsChecked("b"));
        Assert.Equal(["a"], include.Effective([]));

        var exclude = new CappedFilterSelection(FilterExclusion.Exclude(["b"]));
        Assert.True(exclude.AllByDefault);
        Assert.False(exclude.IsChecked("b"));
        Assert.True(exclude.IsChecked("a"));
        Assert.Equal(["b"], FilterExclusion.Values(exclude.Effective([])!));
    }

    [Fact]
    public void Search_checks_all_found_and_done_replaces_with_checked_among_found()
    {
        // Был отбор «только a»; поиск нашел mama и mamba — оба отмечены сами, одну сняли.
        var sel = new CappedFilterSelection(new HashSet<string> { "a" });
        sel.OnSearch("mam");
        Assert.True(sel.Searching);
        Assert.True(sel.IsChecked("mama"));
        sel.Set("mamba", false);

        Assert.Equal(["mama"], sel.Effective(["mama", "mamba"]));
    }

    [Fact]
    public void Each_search_change_rechecks_found_and_clearing_restores()
    {
        var sel = new CappedFilterSelection(null);
        sel.Set("x", false);            // до поиска сняли x
        sel.OnSearch("ma");
        sel.Set("mama", false);
        sel.OnSearch("mam");            // новый текст — найденное снова отмечено целиком
        Assert.True(sel.IsChecked("mama"));

        sel.OnSearch("");               // очистка — прежние галочки
        Assert.False(sel.Searching);
        Assert.False(sel.IsChecked("x"));
        Assert.True(sel.IsChecked("mama"));
    }

    [Fact]
    public void Select_all_during_search_acts_on_found_only()
    {
        var sel = new CappedFilterSelection(null);
        sel.OnSearch("ma");
        sel.SetAll(false, ["mama", "mamba"]);
        Assert.False(sel.IsChecked("mama"));
        Assert.Empty(sel.Effective(["mama", "mamba"])!);

        sel.OnSearch("");               // вне поиска все осталось отмеченным
        Assert.True(sel.AllChecked);
    }
}
