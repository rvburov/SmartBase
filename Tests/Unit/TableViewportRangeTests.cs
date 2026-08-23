using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Навигация нижней панели таблицы (<see cref="TableViewportRange"/> и
/// <see cref="TableViewportScroll"/>) — чистая логика без Avalonia: что написано в
/// индикаторе («10 – 50 из 125 437»), какие кнопки погашены у краев и куда встает
/// вертикальная полоса при нажатии.
///
/// Живые данные приходят из раскладки DataGrid, где в момент пересборки строк
/// встречается и «первая больше последней», и номера за пределами общего числа, —
/// нормализация <see cref="TableViewportRange.Of"/> проверяется отдельно.
/// </summary>
public sealed class TableViewportRangeTests
{
    // Неразрывный пробел — им же отбиваются разряды в индикаторе. Задан кодом, а не
    // символом: в исходнике он неотличим от обычного пробела, и тест «проходил» бы вслепую.
    private const string Nbsp = "\u00A0";

    // ── Текст индикатора ──────────────────────────────────────────────────────

    [Fact]
    public void Label_shows_visible_rows_and_total()
    {
        var range = TableViewportRange.Of(10, 50, 125437);

        Assert.Equal($"10 – 50 из 125{Nbsp}437", range.Label);
    }

    [Fact]
    public void Label_of_single_visible_row_has_no_dash()
    {
        Assert.Equal("7 из 7", TableViewportRange.Of(7, 7, 7).Label);
    }

    [Fact]
    public void Label_of_empty_collection_says_no_records()
    {
        Assert.Equal("Нет записей", TableViewportRange.Empty.Label);
        Assert.Equal("Нет записей", TableViewportRange.Of(1, 10, 0).Label);
    }

    [Fact]
    public void Label_before_layout_shows_zero_of_total()
    {
        // Строки еще не разложены (ни одна не реализована), но их число уже известно.
        Assert.Equal($"0 из 1{Nbsp}000", TableViewportRange.Of(0, 0, 1000).Label);
    }

    [Theory]
    [InlineData(0,       "0")]
    [InlineData(54,      "54")]
    [InlineData(999,     "999")]
    [InlineData(1000,    "1" + Nbsp + "000")]
    [InlineData(125437,  "125" + Nbsp + "437")]
    [InlineData(1000000, "1" + Nbsp + "000" + Nbsp + "000")]
    public void Format_groups_thousands_with_nbsp(int value, string expected)
    {
        Assert.Equal(expected, TableViewportRange.Format(value));
    }

    // ── Активность кнопок ─────────────────────────────────────────────────────

    [Fact]
    public void At_start_when_first_row_is_visible()
    {
        var range = TableViewportRange.Of(1, 40, 500);

        Assert.True(range.AtStart);
        Assert.False(range.AtEnd);
    }

    [Fact]
    public void At_end_when_last_row_is_visible()
    {
        var range = TableViewportRange.Of(461, 500, 500);

        Assert.False(range.AtStart);
        Assert.True(range.AtEnd);
    }

    [Fact]
    public void Middle_of_table_enables_both_directions()
    {
        var range = TableViewportRange.Of(100, 140, 500);

        Assert.False(range.AtStart);
        Assert.False(range.AtEnd);
    }

    [Fact]
    public void Whole_table_fits_on_screen_disables_all_buttons()
    {
        var range = TableViewportRange.Of(1, 12, 12);

        Assert.True(range.AtStart);
        Assert.True(range.AtEnd);
    }

    [Fact]
    public void Empty_range_disables_all_buttons()
    {
        Assert.True(TableViewportRange.Empty.AtStart);
        Assert.True(TableViewportRange.Empty.AtEnd);
        Assert.False(TableViewportRange.Empty.HasRows);
    }

    // ── Нормализация входа ────────────────────────────────────────────────────

    [Fact]
    public void Of_clamps_rows_beyond_total()
    {
        // Строки удалили, а раскладка еще старая: номера больше нового количества.
        var range = TableViewportRange.Of(48, 90, 50);

        Assert.Equal(48, range.First);
        Assert.Equal(50, range.Last);
        Assert.True(range.AtEnd);
    }

    [Fact]
    public void Of_repairs_last_below_first()
    {
        var range = TableViewportRange.Of(30, 10, 100);

        Assert.Equal(30, range.First);
        Assert.Equal(30, range.Last);
    }

    [Fact]
    public void Of_ignores_negative_input()
    {
        var range = TableViewportRange.Of(-5, -1, 100);

        Assert.Equal(0, range.First);
        Assert.Equal(0, range.Last);
        Assert.Equal(100, range.Total);
        Assert.False(range.HasRows);
    }

    [Fact]
    public void Of_with_nonpositive_total_is_empty()
    {
        Assert.Equal(TableViewportRange.Empty, TableViewportRange.Of(1, 10, 0));
        Assert.Equal(TableViewportRange.Empty, TableViewportRange.Of(1, 10, -3));
    }

    // ── Позиция вертикальной полосы ───────────────────────────────────────────

    [Fact]
    public void Page_forward_moves_exactly_one_screen()
    {
        Assert.Equal(400, TableViewportScroll.PageForward(offset: 0, viewport: 400, max: 5000));
        Assert.Equal(800, TableViewportScroll.PageForward(offset: 400, viewport: 400, max: 5000));
    }

    [Fact]
    public void Page_back_moves_exactly_one_screen()
    {
        Assert.Equal(600, TableViewportScroll.PageBack(offset: 1000, viewport: 400, max: 5000));
    }

    [Fact]
    public void Page_forward_stops_at_the_end()
    {
        Assert.Equal(5000, TableViewportScroll.PageForward(offset: 4900, viewport: 400, max: 5000));
    }

    [Fact]
    public void Page_back_stops_at_the_start()
    {
        Assert.Equal(0, TableViewportScroll.PageBack(offset: 200, viewport: 400, max: 5000));
    }

    [Fact]
    public void Page_step_does_nothing_until_table_is_laid_out()
    {
        // Вьюпорт еще нулевой — прокрутка не должна «уезжать» на ноль пикселей и обратно.
        Assert.Equal(300, TableViewportScroll.PageForward(offset: 300, viewport: 0, max: 5000));
        Assert.Equal(300, TableViewportScroll.PageBack(offset: 300, viewport: 0, max: 5000));
    }

    [Fact]
    public void Jumps_land_on_the_edges()
    {
        Assert.Equal(0, TableViewportScroll.ToStart());
        Assert.Equal(5000, TableViewportScroll.ToEnd(5000));
    }

    [Fact]
    public void Jump_to_end_of_table_that_fits_on_screen_stays_at_zero()
    {
        // Прокручивать нечего — Maximum у полосы нулевой (а на сжатии окна бывает и меньше).
        Assert.Equal(0, TableViewportScroll.ToEnd(0));
        Assert.Equal(0, TableViewportScroll.ToEnd(-10));
    }
}
