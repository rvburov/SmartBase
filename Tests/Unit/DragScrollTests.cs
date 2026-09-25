using Avalonia;
using SmartBase.Views.ConstructorDb.DrawnGrid;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Автопрокрутка при протягивании выделения за край области строк (<see cref="DragScroll"/>):
/// внутри области ничего не едет, снаружи — шаг в сторону указателя, растущий с расстоянием и
/// ограниченный сверху; точка фокуса прижимается к краю области.
/// </summary>
public sealed class DragScrollTests
{
    private static readonly Rect Body = new(0, 36, 1000, 800);   // под шапкой 36, окно 1000×800

    [Fact]
    public void Inside_body_nothing_scrolls_and_point_is_kept()
    {
        var (p, dx, dy) = DragScroll.Step(new Point(400, 300), Body, rowStep: 37);

        Assert.Equal(new Point(400, 300), p);
        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
    }

    [Fact]
    public void Below_body_scrolls_down_by_rows_and_clamps_to_bottom_edge()
    {
        var (p, dx, dy) = DragScroll.Step(new Point(400, 850), Body, rowStep: 37);

        Assert.Equal(400, p.X);
        Assert.True(p.Y < Body.Bottom && p.Y > Body.Bottom - 1, "точка прижата к нижнему краю");
        Assert.Equal(0, dx);
        Assert.Equal(37, dy);   // 14 px за краем — один шаг
    }

    [Fact]
    public void Speed_grows_with_distance_and_is_capped()
    {
        var (_, _, near) = DragScroll.Step(new Point(400, Body.Bottom + 10),   Body, rowStep: 37);
        var (_, _, mid)  = DragScroll.Step(new Point(400, Body.Bottom + 100),  Body, rowStep: 37);
        var (_, _, far)  = DragScroll.Step(new Point(400, Body.Bottom + 5000), Body, rowStep: 37);

        Assert.Equal(37 * 1, near);
        Assert.Equal(37 * 3, mid);                      // 1 + 100 / 40
        Assert.Equal(37 * DragScroll.MaxSteps, far);
    }

    [Fact]
    public void Above_header_and_left_of_table_scroll_up_and_left()
    {
        var (p, dx, dy) = DragScroll.Step(new Point(-50, 10), Body, rowStep: 37, colStep: 48);

        Assert.Equal(new Point(0, 36), p);
        Assert.Equal(-48 * 2, dx);   // 50 px за краем — два шага
        Assert.Equal(-37 * 1, dy);   // 26 px над областью — один шаг
    }

    [Fact]
    public void Empty_body_is_inert()
    {
        var (p, dx, dy) = DragScroll.Step(new Point(5, 5), new Rect(0, 0, 0, 0), rowStep: 37);

        Assert.Equal(new Point(5, 5), p);
        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
    }
}
