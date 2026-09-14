using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Галочки чеклиста в окне фильтра колонки (<see cref="FilterSelection"/>) — правила табличных редакторов,
/// общие для плоского списка и дерева дат: ввод в поиске отмечает найденное, очистка
/// возвращает прежние галочки, «Готово» при поиске берет отмеченное среди найденного,
/// отмечено все — фильтра нет, отобранные раньше значения идут первыми.
///
/// Что «показано сейчас», модель не знает — ей передают найденные значения, как это делают
/// список (Matched) и дерево (VisibleRaws).
/// </summary>
public sealed class FilterSelectionTests
{
    // Значения колонки: «(Пусто)» — тоже значение (пустая строка).
    private static readonly HashSet<string> Domain = ["a", "b", "mama", "mamba", ""];

    // Поиск по подстроке без регистра — как в списке значений.
    private static List<string> Found(string q) =>
        Domain.Where(v => v.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

    // ── Поиск ────────────────────────────────────────────────────────────────

    [Fact]
    public void Typing_checks_everything_found()
    {
        // По колонке уже есть фильтр (отмечено только «a»); ввели «mam» — найденное отмечено само,
        // щелкать по галочкам не нужно. Чужие галочки не тронуты: «a» осталась, «b» не появилась.
        var sel = new FilterSelection(["a"]);
        sel.OnSearch("mam", Found("mam"));

        Assert.True(sel.Searching);
        Assert.Contains("mama",  sel.Checked);
        Assert.Contains("mamba", sel.Checked);
        Assert.Contains("a",     sel.Checked);
        Assert.DoesNotContain("b", sel.Checked);
    }

    [Fact]
    public void Every_change_of_text_rechecks_found()
    {
        // Сняли галочку внутри поиска и продолжили печатать — найденное отмечено заново.
        var sel = new FilterSelection([]);
        sel.OnSearch("mam", Found("mam"));
        sel.Checked.Remove("mama");

        sel.OnSearch("mama", Found("mama"));

        Assert.Contains("mama", sel.Checked);
    }

    [Fact]
    public void Clearing_search_restores_checks_from_before_typing()
    {
        var sel = new FilterSelection(["a"]);
        sel.OnSearch("m",   Found("m"));
        sel.OnSearch("mam", Found("mam"));
        sel.Checked.Remove("a");            // правки внутри поиска — черновик

        sel.OnSearch("", Found(""));

        Assert.False(sel.Searching);
        Assert.Equal(new HashSet<string> { "a" }, sel.Checked);
    }

    [Fact]
    public void Whitespace_is_not_a_search()
    {
        var sel = new FilterSelection(["a"]);
        sel.OnSearch("   ", Found(""));

        Assert.False(sel.Searching);
        Assert.Equal(new HashSet<string> { "a" }, sel.Checked);
    }

    [Fact]
    public void Reset_forgets_snapshot_taken_before_data_arrived()
    {
        // Печатать начали, пока значения грузились: снимок «до поиска» снят с пустого набора.
        var sel = new FilterSelection([]);
        sel.OnSearch("mam", []);

        // Данные пришли: набор заполнен заново, снимок сброшен, поиск применен еще раз.
        sel.Checked.UnionWith(Domain);
        sel.Reset();
        sel.OnSearch("mam", Found("mam"));
        sel.OnSearch("", Found(""));

        Assert.Equal(Domain, sel.Checked);   // а не пустой набор из устаревшего снимка
    }

    // ── Итог по «Готово» ─────────────────────────────────────────────────────

    [Fact]
    public void Effective_during_search_is_checked_among_found()
    {
        // Отмечено все, ищем «mam»: в фильтр идут только найденные — остальное отсекается («Заменить»).
        var sel = new FilterSelection(new HashSet<string>(Domain));
        sel.OnSearch("mam", Found("mam"));

        Assert.Equal(new HashSet<string> { "mama", "mamba" }, sel.Effective(Found("mam"), Domain));
    }

    [Fact]
    public void Effective_during_search_ignores_unchecked_found()
    {
        var sel = new FilterSelection([]);
        sel.OnSearch("mam", Found("mam"));
        sel.Checked.Remove("mamba");

        Assert.Equal(new HashSet<string> { "mama" }, sel.Effective(Found("mam"), Domain));
    }

    [Fact]
    public void Effective_without_search_is_all_checked()
    {
        var sel = new FilterSelection(["a", "b"]);

        Assert.Equal(new HashSet<string> { "a", "b" }, sel.Effective(Domain, Domain));
    }

    [Fact]
    public void Effective_is_null_when_whole_column_checked()
    {
        var sel = new FilterSelection(new HashSet<string>(Domain));

        Assert.Null(sel.Effective(Domain, Domain));
    }

    [Fact]
    public void Effective_is_null_when_checked_covers_column_plus_stale_values()
    {
        // В прежнем фильтре осталось значение, которого в колонке уже нет; все, что есть, отмечено —
        // фильтр не нужен (иначе воронка горела бы на колонке без отбора).
        var sel = new FilterSelection(new HashSet<string>(Domain) { "zzz" });

        Assert.Null(sel.Effective(Domain, Domain));
    }

    [Fact]
    public void Effective_keeps_values_hidden_by_other_columns()
    {
        // Каскад: «zzz» есть в фильтре этой колонки, но его строки спрятал фильтр другой колонки,
        // и в списке его нет. «Готово» без правок обязано сохранить его — снимут чужой фильтр,
        // и строки «zzz» снова в отборе.
        var visibleDomain = new HashSet<string> { "a", "b" };
        var sel = new FilterSelection(["a", "zzz"]);

        Assert.Equal(new HashSet<string> { "a", "zzz" }, sel.Effective(visibleDomain, visibleDomain));
    }

    [Fact]
    public void Effective_during_search_is_a_filter_unless_search_found_whole_column()
    {
        var sel = new FilterSelection(new HashSet<string>(Domain));
        sel.OnSearch("a", Found("a"));   // «a», «mama», «mamba» — не вся колонка

        Assert.Equal(new HashSet<string> { "a", "mama", "mamba" }, sel.Effective(Found("a"), Domain));
    }

    // ── Порядок при открытии ─────────────────────────────────────────────────

    [Fact]
    public void ChosenFirst_moves_filter_values_ahead_keeping_order_inside_groups()
    {
        var items = new List<(string Raw, int Count)> { ("a", 1), ("b", 2), ("mama", 3), ("mamba", 4) };

        var ordered = FilterSelection.ChosenFirst(items, i => i.Raw, new HashSet<string> { "mamba", "b" });

        Assert.Equal(["b", "mamba", "a", "mama"], ordered.Select(i => i.Raw));
    }

    [Fact]
    public void ChosenFirst_without_filter_keeps_list_as_is()
    {
        var items = new List<(string Raw, int Count)> { ("b", 1), ("a", 2) };

        Assert.Equal(["b", "a"], FilterSelection.ChosenFirst(items, i => i.Raw, null).Select(i => i.Raw));
        Assert.Equal(["b", "a"], FilterSelection.ChosenFirst(items, i => i.Raw, new HashSet<string>()).Select(i => i.Raw));
    }
}
