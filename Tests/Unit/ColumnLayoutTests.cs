using SmartBase.ViewModels.ConstructorDb;
using SmartBase.Controls.DrawnGrid;

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

    // ── Служебные таблицы: дробный дележ и округление ячеек, как у прежних таблиц на DataGrid ──
    //
    // Числа — из кадров прежних таблиц (стенд Tests/ServiceGrids): окно 1586 без полосы, начало
    // ячейки округляется к четному, ширина — вверх вместе с дробной частью начала.

    private static List<TableColumnDef> Stars(int n, double min = 110) =>
        Enumerable.Range(1, n).Select(i => new TableColumnDef { Key = $"c{i}", Header = $"К {i}", MinWidth = min }).ToList();

    private static string Cells(ColumnLayout l) =>
        string.Join(" ", Enumerable.Range(0, l.Count).Select(i =>
            "[" + l.Left(i).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                + "+" + l.Width(i).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "]"));

    [Fact]
    public void Rounded_four_stars_match_former_table_at_scale_1_and_2()
    {
        Assert.Equal("[0+397] [396+397] [793+397] [1190+397]", Cells(ColumnLayout.BuildRounded(Stars(4), 1586, 1)));
        Assert.Equal("[0+396.5] [396.5+396.5] [793+396.5] [1189.5+396.5]", Cells(ColumnLayout.BuildRounded(Stars(4), 1586, 2)));
    }

    [Fact]
    public void Rounded_six_and_seven_stars_match_former_table()
    {
        Assert.Equal("[0+265] [264+265] [529+265] [793+265] [1057+265] [1322+265]", Cells(ColumnLayout.BuildRounded(Stars(6), 1586, 1)));
        Assert.Equal("[0+227] [227+228] [453+227] [680+228] [906+227] [1133+228] [1359+227]", Cells(ColumnLayout.BuildRounded(Stars(7), 1586, 1)));
        Assert.Equal("[0+227] [226.5+227] [453+227] [679.5+227] [906.5+227] [1133+227] [1359.5+227]", Cells(ColumnLayout.BuildRounded(Stars(7), 1586, 2)));
    }

    [Fact]
    public void Rounded_extent_is_fractional_sum_not_rounded_cells()
    {
        // Последняя ячейка после округления вылезает на пиксель (1190 + 397 = 1587), а протяженность
        // прокрутки — сумма дробных ширин: полосы не будет.
        var l = ColumnLayout.BuildRounded(Stars(4), 1586, 1);
        Assert.Equal(1586, l.TotalWidth);
        Assert.Equal(1587, l.Right(3));
        Assert.Equal(3, l.IndexAt(1586.5));   // правее протяженности, но внутри последней ячейки
        Assert.Equal(-1, l.IndexAt(1587));
    }

    [Fact]
    public void Rounded_pins_columns_at_their_minimum_and_shares_the_rest()
    {
        // Семь колонок в узком окне: адрес не уже 180, остальные делят остаток поровну.
        var defs = Stars(7, min: 130);
        defs[5] = new TableColumnDef { Key = "c6", Header = "Адрес", MinWidth = 180 };
        var l = ColumnLayout.BuildRounded(defs, 1145, 1);
        Assert.InRange(l.Width(5), 180, 181);   // 180 плюс дробная часть начала ячейки
        var others = Enumerable.Range(0, 7).Where(i => i != 5).Select(l.Width).ToList();
        Assert.All(others, w => Assert.InRange(w, 160, 162));   // (1145 − 180) / 6 = 160,83 → ячейки 161–162
        Assert.Equal(1145, l.TotalWidth, 6);
    }

    [Fact]
    public void Rounded_fixed_columns_stay_contiguous()
    {
        var defs = new List<TableColumnDef>
        {
            new() { Key = "a", Header = "А", IsStarWidth = false, FixedWidth = 250 },
            new() { Key = "b", Header = "Б", IsStarWidth = false, FixedWidth = 200 },
        };
        var l = ColumnLayout.BuildRounded(defs, 1600, 1);
        Assert.Equal("[0+250] [250+200]", Cells(l));
        Assert.Equal(450, l.TotalWidth);
        Assert.Equal(1, l.IndexAt(250));
    }
}
