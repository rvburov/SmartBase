using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Движение выделения ячеек с клавиатуры. Таблица только переводит клавишу в
/// <see cref="CellNavKey"/>, поэтому все правила — активная ячейка, растягивание, перешагивание
/// скрытых столбцов, края — проверяются здесь без сетки.
/// </summary>
public sealed class CellRangeNavigationTests
{
    // Позиции видимых столбцов: 0 — «№», 3 — скрытый, 6 — «+».
    private static readonly int[] Columns = [1, 2, 4, 5];

    private static CellRange Nav(CellRange range, CellNavKey key, bool extend = false, int rows = 10, int page = 3) =>
        CellRangeNavigation.Navigate(range, key, extend, rows, Columns, page)!.Value;

    [Fact]
    public void Arrow_moves_the_active_cell_and_collapses_the_selection()
    {
        var moved = Nav(new CellRange(2, 1, 5, 4), CellNavKey.Down);

        Assert.Equal(CellRange.Single(3, 1), moved);
    }

    /// <summary>С Shift двигается дальний угол, а активная ячейка стоит: Shift+↓ после Shift+→
    /// растягивает тот же прямоугольник.</summary>
    [Fact]
    public void Shift_arrow_extends_from_the_same_anchor()
    {
        var once  = Nav(CellRange.Single(2, 1), CellNavKey.Right, extend: true);
        var twice = Nav(once, CellNavKey.Down, extend: true);

        Assert.Equal(new CellRange(2, 1, 3, 2), twice);
    }

    /// <summary>Скрытый столбец перешагивается, а не «съедает» нажатие.</summary>
    [Fact]
    public void Hidden_column_is_stepped_over()
    {
        Assert.Equal(CellRange.Single(0, 4), Nav(CellRange.Single(0, 2), CellNavKey.Right));
        Assert.Equal(CellRange.Single(0, 2), Nav(CellRange.Single(0, 4), CellNavKey.Left));
    }

    [Fact]
    public void Movement_stops_at_the_edges()
    {
        Assert.Equal(CellRange.Single(0, 1), Nav(CellRange.Single(0, 1), CellNavKey.Up));
        Assert.Equal(CellRange.Single(0, 1), Nav(CellRange.Single(0, 1), CellNavKey.Left));
        Assert.Equal(CellRange.Single(9, 5), Nav(CellRange.Single(9, 5), CellNavKey.Down));
        Assert.Equal(CellRange.Single(9, 5), Nav(CellRange.Single(9, 5), CellNavKey.Right));
    }

    [Fact]
    public void Ctrl_arrows_and_home_end_jump_to_the_table_edges()
    {
        var start = CellRange.Single(4, 2);

        Assert.Equal(CellRange.Single(0, 2), Nav(start, CellNavKey.CtrlUp));
        Assert.Equal(CellRange.Single(9, 2), Nav(start, CellNavKey.CtrlDown));
        Assert.Equal(CellRange.Single(4, 1), Nav(start, CellNavKey.Home));
        Assert.Equal(CellRange.Single(4, 5), Nav(start, CellNavKey.End));
        Assert.Equal(CellRange.Single(0, 1), Nav(start, CellNavKey.CtrlHome));
        Assert.Equal(CellRange.Single(9, 5), Nav(start, CellNavKey.CtrlEnd));
    }

    [Fact]
    public void Page_keys_move_by_a_screen_and_stop_at_the_edge()
    {
        Assert.Equal(CellRange.Single(7, 1), Nav(CellRange.Single(4, 1), CellNavKey.PageDown));
        Assert.Equal(CellRange.Single(9, 1), Nav(CellRange.Single(8, 1), CellNavKey.PageDown));
        Assert.Equal(CellRange.Single(0, 1), Nav(CellRange.Single(2, 1), CellNavKey.PageUp));
    }

    /// <summary>Tab идет по строке и переносится на следующую; растягивать выделение он не умеет.</summary>
    [Fact]
    public void Tab_walks_the_row_and_wraps()
    {
        Assert.Equal(CellRange.Single(0, 4), Nav(CellRange.Single(0, 2), CellNavKey.Tab));
        Assert.Equal(CellRange.Single(1, 1), Nav(CellRange.Single(0, 5), CellNavKey.Tab));
        Assert.Equal(CellRange.Single(0, 5), Nav(CellRange.Single(1, 1), CellNavKey.ShiftTab));
        Assert.Equal(CellRange.Single(3, 2), Nav(new CellRange(3, 1, 6, 4), CellNavKey.Tab, extend: true));
    }

    /// <summary>Столбец выделения скрыли, пока выделение жило, — движение идет от ближайшего видимого.</summary>
    [Fact]
    public void Selection_on_a_hidden_column_snaps_to_the_nearest_visible_one()
    {
        Assert.Equal(CellRange.Single(1, 2), Nav(CellRange.Single(0, 3), CellNavKey.Down));
    }

    [Fact]
    public void Empty_table_gives_no_selection()
    {
        Assert.Null(CellRangeNavigation.Navigate(CellRange.Single(0, 1), CellNavKey.Down, false, 0, Columns, 3));
        Assert.Null(CellRangeNavigation.Navigate(CellRange.Single(0, 1), CellNavKey.Down, false, 5, [], 3));
    }

    [Fact]
    public void Select_all_spans_every_row_and_visible_column()
    {
        Assert.Equal(new CellRange(0, 1, 9, 5), CellRangeNavigation.SelectAll(10, Columns));
        Assert.Null(CellRangeNavigation.SelectAll(0, Columns));
    }
}
