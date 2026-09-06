using SmartBase.Core.Calendar;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Недельная математика календаря (CalendarMath): рабочая неделя 5/7 дней, окно вида
/// «Неделя» по режимам прокрутки и перевод колонки в дату. Ключевой инвариант — при
/// 5-дневке дни НЕ непрерывны (пятница соседствует с понедельником), поэтому любые
/// вычисления «колонка → дата» обязаны идти через DayAtCol/AddVisibleDays, а не AddDays.
/// </summary>
public sealed class CalendarMathTests
{
    // Опорная неделя: понедельник 7 сентября 2026 (сб 5-е, вс 6-е).
    private static readonly DateTime Mon = new(2026, 9, 7);
    private static readonly DateTime Fri = new(2026, 9, 11);
    private static readonly DateTime Sat = new(2026, 9, 12);
    private static readonly DateTime Sun = new(2026, 9, 13);

    // ── Дни и рабочая неделя ──────────────────────────────────────────────────

    [Fact]
    public void StartOfWeek_is_monday_for_all_days_of_week()
    {
        for (var i = 0; i < 7; i++)
            Assert.Equal(Mon, CalendarMath.StartOfWeek(Mon.AddDays(i)));
    }

    [Fact]
    public void NextVisibleDay_friday_jumps_to_monday_in_five_day()
    {
        Assert.Equal(Mon.AddDays(7), CalendarMath.NextVisibleDay(Fri, fiveDay: true));
        Assert.Equal(Sat, CalendarMath.NextVisibleDay(Fri, fiveDay: false));
        // Из выходного тоже уходит в понедельник.
        Assert.Equal(Mon.AddDays(7), CalendarMath.NextVisibleDay(Sat, fiveDay: true));
    }

    [Fact]
    public void PrevVisibleDay_monday_jumps_to_friday_in_five_day()
    {
        Assert.Equal(Fri, CalendarMath.PrevVisibleDay(Mon.AddDays(7), fiveDay: true));
        Assert.Equal(Sun, CalendarMath.PrevVisibleDay(Mon.AddDays(7), fiveDay: false));
        Assert.Equal(Fri, CalendarMath.PrevVisibleDay(Sun, fiveDay: true));
    }

    [Fact]
    public void AddVisibleDays_walks_across_weekends_in_both_directions()
    {
        // Пт + 2 рабочих = Вт следующей недели; Пн − 2 рабочих = Чт предыдущей.
        Assert.Equal(Mon.AddDays(8), CalendarMath.AddVisibleDays(Fri, 2, fiveDay: true));
        Assert.Equal(Mon.AddDays(-4), CalendarMath.AddVisibleDays(Mon, -2, fiveDay: true));
        Assert.Equal(Fri.Date, CalendarMath.AddVisibleDays(Fri, 0, fiveDay: true));
        // 7-дневка — обычная арифметика дней.
        Assert.Equal(Fri.AddDays(2), CalendarMath.AddVisibleDays(Fri, 2, fiveDay: false));
    }

    [Fact]
    public void SnapToWorkday_moves_weekend_by_direction_only_in_five_day()
    {
        Assert.Equal(Mon.AddDays(7), CalendarMath.SnapToWorkday(Sat, +1, fiveDay: true));
        Assert.Equal(Fri, CalendarMath.SnapToWorkday(Sun, -1, fiveDay: true));
        // Без направления, в 7-дневке и для рабочего дня — день не трогается.
        Assert.Equal(Sat, CalendarMath.SnapToWorkday(Sat, 0, fiveDay: true));
        Assert.Equal(Sat, CalendarMath.SnapToWorkday(Sat, +1, fiveDay: false));
        Assert.Equal(Fri, CalendarMath.SnapToWorkday(Fri, +1, fiveDay: true));
    }

    // ── Окно недели ───────────────────────────────────────────────────────────

    [Fact]
    public void WeekWindow_seven_days_contiguous()
    {
        var days = CalendarMath.WeekWindow(Mon, fiveDay: false);
        Assert.Equal(7, days.Count);
        for (var i = 0; i < 7; i++) Assert.Equal(Mon.AddDays(i), days[i]);
    }

    [Fact]
    public void WeekWindow_five_day_unaligned_has_gap_inside()
    {
        // Якорь-среда: Ср, Чт, Пт, Пн, Вт — «дыра» на выходных внутри окна.
        var days = CalendarMath.WeekWindow(Mon.AddDays(2), fiveDay: true);
        Assert.Equal(new[] { Mon.AddDays(2), Mon.AddDays(3), Fri, Mon.AddDays(7), Mon.AddDays(8) }, days);
    }

    [Fact]
    public void WeekWindow_weekend_anchor_slides_to_monday_in_five_day()
    {
        var days = CalendarMath.WeekWindow(Sat, fiveDay: true);
        Assert.Equal(Mon.AddDays(7), days[0]);
        Assert.Equal(5, days.Count);
    }

    // ── Колонка → дата ────────────────────────────────────────────────────────

    [Fact]
    public void DayAtCol_inside_window_returns_listed_day()
    {
        var days = CalendarMath.WeekWindow(Mon.AddDays(2), fiveDay: true); // Ср..Вт
        Assert.Equal(Fri, CalendarMath.DayAtCol(days, 2, fiveDay: true));
        Assert.Equal(Mon.AddDays(7), CalendarMath.DayAtCol(days, 3, fiveDay: true));
    }

    [Fact]
    public void DayAtCol_beyond_edges_extrapolates_by_visible_days()
    {
        var days = CalendarMath.WeekWindow(Mon, fiveDay: true); // Пн..Пт
        // Колонка «после пятницы» — понедельник следующей недели, не суббота.
        Assert.Equal(Mon.AddDays(7), CalendarMath.DayAtCol(days, 5, fiveDay: true));
        Assert.Equal(Mon.AddDays(8), CalendarMath.DayAtCol(days, 6, fiveDay: true));
        // Колонка «до понедельника» — пятница предыдущей недели.
        Assert.Equal(Fri.AddDays(-7), CalendarMath.DayAtCol(days, -1, fiveDay: true));

        var week7 = CalendarMath.WeekWindow(Mon, fiveDay: false);
        Assert.Equal(Mon.AddDays(7), CalendarMath.DayAtCol(week7, 7, fiveDay: false));
        Assert.Equal(Sun.AddDays(-7), CalendarMath.DayAtCol(week7, -1, fiveDay: false));
    }

    // ── Центрирование «Сегодня» ───────────────────────────────────────────────

    [Fact]
    public void CenteredWeekAnchor_seven_day_puts_today_minus_three()
    {
        var wed = Mon.AddDays(2);
        Assert.Equal(wed.AddDays(-3), CalendarMath.CenteredWeekAnchor(wed, fiveDay: false));
    }

    [Fact]
    public void CenteredWeekAnchor_five_day_puts_two_workdays_back()
    {
        // Сегодня-вторник: за два рабочих дня — пятница предыдущей недели.
        Assert.Equal(Fri.AddDays(-7), CalendarMath.CenteredWeekAnchor(Mon.AddDays(1), fiveDay: true));
    }

    [Fact]
    public void CenteredWeekAnchor_weekend_today_falls_back_to_start_of_week()
    {
        Assert.Equal(Mon, CalendarMath.CenteredWeekAnchor(Sat, fiveDay: true));
        Assert.Equal(Mon, CalendarMath.CenteredWeekAnchor(Sun, fiveDay: true));
    }

    // ── Шаг прокрутки ─────────────────────────────────────────────────────────

    [Fact]
    public void StepWeekAnchor_by_week_steps_seven_days_aligned()
    {
        var (next, centered) = CalendarMath.StepWeekAnchor(
            Mon.AddDays(2), false, CalendarWeekScrollMode.ByWeek, false, Mon, +1);
        Assert.Equal(Mon.AddDays(7), next);
        Assert.False(centered);

        (next, _) = CalendarMath.StepWeekAnchor(
            Mon.AddDays(2), false, CalendarWeekScrollMode.ByWeek, false, Mon, -1);
        Assert.Equal(Mon.AddDays(-7), next);
    }

    [Fact]
    public void StepWeekAnchor_by_day_five_day_skips_weekend()
    {
        var (next, _) = CalendarMath.StepWeekAnchor(
            Fri, false, CalendarWeekScrollMode.ByDay, true, Mon, +1);
        Assert.Equal(Mon.AddDays(7), next);

        (var prev, _) = CalendarMath.StepWeekAnchor(
            Mon.AddDays(7), false, CalendarWeekScrollMode.ByDay, true, Mon, -1);
        Assert.Equal(Fri, prev);
    }

    [Fact]
    public void StepWeekAnchor_stop_today_stops_centered_when_reaching_todays_week()
    {
        // Сегодня-среда; листаем с предыдущей недели вперед: остановка «сегодня посередине».
        var today = Mon.AddDays(2);
        var (anchor, centered) = CalendarMath.StepWeekAnchor(
            Mon.AddDays(-7), false, CalendarWeekScrollMode.ByWeekStopToday, false, today, +1);
        Assert.Equal(today.AddDays(-3), anchor);
        Assert.True(centered);
    }

    [Fact]
    public void StepWeekAnchor_stop_today_prev_is_symmetric()
    {
        var today = Mon.AddDays(2);
        var (anchor, centered) = CalendarMath.StepWeekAnchor(
            Mon.AddDays(7), false, CalendarWeekScrollMode.ByWeekStopToday, false, today, -1);
        Assert.Equal(today.AddDays(-3), anchor);
        Assert.True(centered);
    }

    [Fact]
    public void StepWeekAnchor_stop_today_continues_aligned_after_centered()
    {
        var today = Mon.AddDays(2);
        var (next, centered) = CalendarMath.StepWeekAnchor(
            today.AddDays(-3), true, CalendarWeekScrollMode.ByWeekStopToday, false, today, +1);
        Assert.Equal(Mon.AddDays(7), next);
        Assert.False(centered);

        (next, centered) = CalendarMath.StepWeekAnchor(
            today.AddDays(-3), true, CalendarWeekScrollMode.ByWeekStopToday, false, today, -1);
        Assert.Equal(Mon.AddDays(-7), next);
        Assert.False(centered);
    }

    [Fact]
    public void StepWeekAnchor_stop_today_far_from_today_steps_normally()
    {
        var today = Mon.AddDays(2);
        var (next, centered) = CalendarMath.StepWeekAnchor(
            Mon.AddDays(28), false, CalendarWeekScrollMode.ByWeekStopToday, false, today, +1);
        Assert.Equal(Mon.AddDays(35), next);
        Assert.False(centered);
    }

    [Fact]
    public void StepWeekAnchor_stop_today_weekend_today_does_not_get_stuck()
    {
        // Сегодня — суббота текущего окна при 5-дневке: центр совпал бы с самим окном,
        // шаг обязан пройти дальше, иначе «Вперед» зациклится.
        var (next, centered) = CalendarMath.StepWeekAnchor(
            Mon, false, CalendarWeekScrollMode.ByWeekStopToday, true, Sat, +1);
        Assert.Equal(Mon.AddDays(7), next);
        Assert.False(centered);
    }

    // ── Клип баров Месяца ─────────────────────────────────────────────────────

    [Fact]
    public void ClipSpanToWorkdays_trims_weekend_tail()
    {
        // Бар Пт(4)–Вс(6) → остается только пятница.
        Assert.Equal((4, 4), CalendarMath.ClipSpanToWorkdays(4, 6, fiveDay: true));
        Assert.Equal((0, 4), CalendarMath.ClipSpanToWorkdays(0, 6, fiveDay: true));
    }

    [Fact]
    public void ClipSpanToWorkdays_weekend_only_returns_null()
    {
        Assert.Null(CalendarMath.ClipSpanToWorkdays(5, 6, fiveDay: true));
        Assert.Null(CalendarMath.ClipSpanToWorkdays(6, 6, fiveDay: true));
    }

    [Fact]
    public void ClipSpanToWorkdays_seven_day_passes_through()
    {
        Assert.Equal((4, 6), CalendarMath.ClipSpanToWorkdays(4, 6, fiveDay: false));
        Assert.Equal((5, 6), CalendarMath.ClipSpanToWorkdays(5, 6, fiveDay: false));
    }
}
