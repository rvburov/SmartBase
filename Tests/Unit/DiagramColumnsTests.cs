using SmartBase.Core.Calendar;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Колонки оси Диаграммы (DiagramColumns). При 5-дневке на масштабе месяца суббота и
/// воскресенье с оси убраны: полоса Пт→Пн идет из пятницы прямо в понедельник, а ее длина —
/// число рабочих дней. Опора — сентябрь 2026: 1-е — вторник, выходные 5–6, 12–13, 19–20, 26–27.
/// </summary>
public sealed class DiagramColumnsTests
{
    private static readonly DateTime Sep1 = new(2026, 9, 1);
    private static DateTime Sep(int day, int hour = 0) => new(2026, 9, day, hour, 0, 0);

    [Fact]
    public void Five_day_axis_has_only_workdays()
    {
        var cols = new DiagramColumns(Sep1, 30, workdaysOnly: true);

        Assert.True(cols.WorkdaysOnly);
        Assert.Equal(22, cols.Count);
        Assert.Equal(Sep(4), cols.DayOf(3));   // пятница
        Assert.Equal(Sep(7), cols.DayOf(4));   // сразу понедельник
        Assert.Equal(Sep(30), cols.DayOf(21));
        Assert.DoesNotContain(Enumerable.Range(0, cols.Count).Select(cols.DayOf), CalendarMath.IsWeekend);
    }

    [Fact]
    public void Friday_to_monday_bar_is_two_adjacent_columns()
    {
        var cols = new DiagramColumns(Sep1, 30, workdaysOnly: true);

        Assert.Equal((3, 4, false), cols.Span(Sep(4), Sep(7)));
        // «10 рабочих дней» с понедельника 14-го — ровно десять колонок подряд.
        var (first, last, _) = cols.Span(Sep(14), Sep(25));
        Assert.Equal(10, last - first + 1);
        // Край на выходном прижимается к рабочему дню: Чт–Сб — это Чт–Пт.
        Assert.Equal((2, 3, false), cols.Span(Sep(3), Sep(5)));
        // Начатое на выходных стоит с понедельника.
        Assert.Equal((4, 5, false), cols.Span(Sep(5), Sep(8)));
    }

    [Fact]
    public void Weekend_only_event_sits_on_the_week_boundary()
    {
        var cols = new DiagramColumns(Sep1, 30, workdaysOnly: true);

        Assert.Equal((4, 4, true), cols.Span(Sep(5), Sep(6)));
        Assert.Equal(4, cols.X(Sep(5, 10)));   // момент в выходной — граница перед понедельником
        Assert.Equal(4.5, cols.X(Sep(7, 12)));
    }

    [Fact]
    public void Event_started_before_the_period_is_clipped_on_the_left()
    {
        var cols = new DiagramColumns(Sep1, 30, workdaysOnly: true);

        var (first, last, inWeekend) = cols.Span(new DateTime(2026, 8, 28), Sep(2));
        Assert.True(first < 0);
        Assert.Equal(1, last);
        Assert.False(inWeekend);
        Assert.True(cols.Span(Sep(29), new DateTime(2026, 10, 2)).Last > cols.Count - 1);
    }

    [Fact]
    public void Seven_day_axis_keeps_every_day()
    {
        var cols = new DiagramColumns(Sep1, 30, workdaysOnly: false);

        Assert.False(cols.WorkdaysOnly);
        Assert.Equal(30, cols.Count);
        Assert.Equal(Sep(5), cols.DayOf(4));
        Assert.Equal((4, 5, false), cols.Span(Sep(5), Sep(6)));
        Assert.Equal(4.5, cols.X(Sep(5, 12)));
    }
}
