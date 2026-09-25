using SmartBase.ViewModels.ConstructorDb;
using SmartBase.Views.ConstructorDb.DrawnGrid;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Раскладка колонок рисованной сетки (<see cref="ColumnLayout"/>): стандартный режим делит окно
/// поровну с остатком первым колонкам, ручной держит модель и раздает остаток пропорционально;
/// в обоих случаях сумма ширин в целых квантах равна ширине окна, когда таблица помещается.
/// Скрытые колонки в раскладке отсутствуют; поиск по координате двоичный.
/// </summary>
public sealed class ColumnLayoutTests
{
    private static List<TableColumnDef> Defs(int movable) =>
    [
        new() { Key = TableColumnDef.NumKey, Header = "№", IsStarWidth = false, FixedWidth = 49 },
        .. Enumerable.Range(1, movable).Select(i => new TableColumnDef { Key = $"f{i}", Header = $"Поле {i}" }),
        new() { Key = TableColumnDef.AddPropKey, Header = "+", IsStarWidth = false, FixedWidth = 36 },
    ];

    private static readonly Dictionary<string, double> NoWidths = [];
    private static readonly HashSet<string> NoHidden = [];

    [Fact]
    public void Standard_mode_fills_window_exactly_with_remainder_to_first_columns()
    {
        var l = ColumnLayout.Build(Defs(6), NoWidths, NoHidden, viewportWidth: 1586, standardWidth: 160, quant: 1);

        Assert.Equal(8, l.Count);
        Assert.Equal(1586, l.TotalWidth);
        Assert.Equal(251, l.Width(1));   // 1501 / 6 = 250, остаток 1 — первой
        Assert.Equal(250, l.Width(2));
        Assert.Equal(250, l.Width(6));
        Assert.Equal(1586 - 36, l.Left(7));   // «+» у правого края
    }

    [Fact]
    public void Standard_mode_never_goes_below_standard_width()
    {
        var l = ColumnLayout.Build(Defs(21), NoWidths, NoHidden, 1586, 160, 1);
        for (var i = 1; i <= 21; i++) Assert.Equal(160, l.Width(i));
        Assert.True(l.TotalWidth > 1586);
    }

    [Fact]
    public void Manual_mode_keeps_model_when_wider_than_window()
    {
        var widths = new Dictionary<string, double> { ["f1"] = 400, ["f2"] = 300, ["f3"] = 900 };
        var l = ColumnLayout.Build(Defs(3), widths, NoHidden, 1000, 160, 1);
        Assert.Equal(400, l.Width(1));
        Assert.Equal(300, l.Width(2));
        Assert.Equal(900, l.Width(3));
    }

    [Fact]
    public void Manual_mode_distributes_extra_proportionally_and_sums_exactly()
    {
        var widths = new Dictionary<string, double> { ["f1"] = 100, ["f2"] = 300 };
        var l = ColumnLayout.Build(Defs(2), widths, NoHidden, 85 + 800, 160, 1);
        Assert.Equal(200, l.Width(1));   // 100 + 400 × 1/4
        Assert.Equal(600, l.Width(2));
        Assert.Equal(885, l.TotalWidth);
    }

    [Fact]
    public void Manual_mode_rounding_never_leaves_gap_or_overflow()
    {
        // Три колонки по 333,33 в окне на 1000: сумма квантов ровно 1000.
        var widths = new Dictionary<string, double> { ["f1"] = 333.33, ["f2"] = 333.33, ["f3"] = 333.34 };
        var l = ColumnLayout.Build(Defs(3), widths, NoHidden, 85 + 1000, 160, 1);
        Assert.Equal(1085, l.TotalWidth);
        for (var i = 1; i <= 3; i++) Assert.Equal(l.Width(i), Math.Round(l.Width(i)));
    }

    [Fact]
    public void Manual_mode_floors_at_minimum_and_uses_standard_for_unknown_keys()
    {
        var widths = new Dictionary<string, double> { ["f1"] = 10 };
        var l = ColumnLayout.Build(Defs(2), widths, NoHidden, 100, 160, 1);   // окно уже таблицы
        Assert.Equal(ColumnLayout.MinManualWidth, l.Width(1));
        Assert.Equal(160, l.Width(2));
    }

    [Fact]
    public void Quant_puts_borders_on_physical_pixels()
    {
        var l = ColumnLayout.Build(Defs(3), NoWidths, NoHidden, 1000, 160, quant: 0.5);
        for (var i = 0; i < l.Count; i++)
        {
            Assert.Equal(0, Math.Round(l.Left(i) * 2) - l.Left(i) * 2, 9);
            Assert.Equal(0, Math.Round(l.Width(i) * 2) - l.Width(i) * 2, 9);
        }
        Assert.Equal(1000, l.TotalWidth);
    }

    [Fact]
    public void Hidden_columns_do_not_exist_in_layout()
    {
        var l = ColumnLayout.Build(Defs(4), NoWidths, new HashSet<string> { "f2", "f3" }, 1000, 160, 1);
        Assert.Equal(4, l.Count);
        Assert.Equal(-1, l.IndexOf("f2"));
        Assert.Equal(2, l.IndexOf("f4"));
        Assert.Equal(1000, l.TotalWidth);
    }

    [Fact]
    public void IndexAt_and_VisibleRange_are_consistent()
    {
        var l = ColumnLayout.Build(Defs(10), NoWidths, NoHidden, 1000, 160, 1);
        Assert.Equal(0, l.IndexAt(0));
        Assert.Equal(0, l.IndexAt(48.9));
        Assert.Equal(1, l.IndexAt(49));
        Assert.Equal(l.Count - 1, l.IndexAt(l.TotalWidth - 0.1));
        Assert.Equal(-1, l.IndexAt(l.TotalWidth));
        Assert.Equal(-1, l.IndexAt(-1));

        var (first, last) = l.VisibleRange(offsetX: 300, viewportWidth: 500);
        Assert.Equal(l.IndexAt(300), first);
        Assert.Equal(l.IndexAt(799.9), last);
        Assert.Equal((0, -1), l.VisibleRange(0, 0));
        Assert.Equal(l.Count - 1, l.VisibleRange(0, 10_000).Last);
    }
}
