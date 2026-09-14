using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Прямоугольник выделенных ячеек: нормализация в любую сторону протягивания, перечисление ячеек
/// в порядке буфера обмена и видимые столбцы диапазона (скрытые в «Отображении» не в счет).
/// </summary>
public sealed class CellRangeTests
{
    [Fact]
    public void Single_cell_range_covers_one_cell()
    {
        var range = CellRange.Single(3, 2);

        Assert.True(range.IsSingleCell);
        Assert.Equal(1, range.CellCount);
        Assert.Equal((3, 2), (range.Top, range.Left));
        Assert.Equal((3, 2), (range.Bottom, range.Right));
    }

    /// <summary>Тянуть можно в любую сторону — границы всегда нормализованы.</summary>
    [Theory]
    [InlineData(1, 1, 3, 4)]   // вправо вниз
    [InlineData(3, 4, 1, 1)]   // влево вверх
    [InlineData(1, 4, 3, 1)]   // влево вниз
    [InlineData(3, 1, 1, 4)]   // вправо вверх
    public void Bounds_are_normalized_in_every_direction(int ar, int ac, int fr, int fc)
    {
        var range = new CellRange(ar, ac, fr, fc);

        Assert.Equal(1, range.Top);
        Assert.Equal(3, range.Bottom);
        Assert.Equal(1, range.Left);
        Assert.Equal(4, range.Right);
        Assert.Equal(3, range.RowCount);
        Assert.Equal(4, range.ColumnCount);
        Assert.Equal(12, range.CellCount);
    }

    [Fact]
    public void Cells_go_left_to_right_then_top_to_bottom()
    {
        var cells = new CellRange(1, 1, 2, 2).Cells().ToList();

        Assert.Equal([(1, 1), (1, 2), (2, 1), (2, 2)], cells);
    }

    /// <summary>Растягивание Shift-кликом двигает фокус, якорь остается на месте.</summary>
    [Fact]
    public void Extending_moves_the_focus_and_keeps_the_anchor()
    {
        var range = CellRange.Single(2, 2).WithFocus(5, 7);

        Assert.Equal((2, 2), (range.AnchorRow, range.AnchorColumn));
        Assert.Equal((5, 7), (range.FocusRow, range.FocusColumn));
        Assert.Equal(4 * 6, range.CellCount);
    }

    [Fact]
    public void Contains_tells_cells_inside_from_cells_outside()
    {
        var range = new CellRange(1, 1, 3, 3);

        Assert.True(range.Contains(2, 2));
        Assert.True(range.Contains(1, 3));
        Assert.False(range.Contains(0, 2));
        Assert.False(range.Contains(2, 4));
    }

    // ── Видимые столбцы ───────────────────────────────────────────────────────

    /// <summary>Скрытый в «Отображении» столбец внутри диапазона не копируется, не заполняется и не
    /// считается в размере выделения.</summary>
    [Fact]
    public void Hidden_columns_inside_the_range_are_not_counted()
    {
        int[] visible = [1, 2, 4, 5];   // 3 скрыт

        Assert.Equal([2, 4, 5], new CellRange(0, 5, 3, 2).VisibleColumns(visible));
        Assert.Equal([4], new CellRange(0, 3, 0, 4).VisibleColumns(visible));
        Assert.Empty(CellRange.Single(0, 3).VisibleColumns(visible));
    }
}
