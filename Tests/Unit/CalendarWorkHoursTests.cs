using SmartBase.Core.Calendar;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Рабочие часы дня (CalendarMath): прижатие перетаскиваемого/создаваемого события
/// к периоду «день начинается — день заканчивается» и перескок растягивания через
/// границу дня (18:00 → 09:00 следующего видимого дня). Главный инвариант: дефолт
/// 0/1440 («лимитов нет») — все методы сквозные, поведение календаря прежнее.
/// </summary>
public sealed class CalendarWorkHoursTests
{
    // Рабочие часы 09:00–18:00.
    private const int DayStart = 9 * 60;
    private const int DayEnd   = 18 * 60;

    // Опорная неделя: понедельник 7 сентября 2026.
    private static readonly DateTime Mon = new(2026, 9, 7);
    private static readonly DateTime Fri = new(2026, 9, 11);

    // ── Без лимитов все сквозное ──────────────────────────────────────────────

    [Fact]
    public void No_limits_defaults_pass_through()
    {
        Assert.False(CalendarMath.HasDayLimits(0, 1440));
        Assert.Equal(23 * 60, CalendarMath.ClampStartMin(23 * 60, 120, 0, 1440));
        Assert.Equal((Fri, 1440), CalendarMath.ResizeEndTarget(Fri, 1440, 0, 1440, fiveDay: true));
        Assert.Equal((Mon, 0), CalendarMath.ResizeStartTarget(Mon, 0, 0, 1440, fiveDay: true));
    }

    // ── Прижатие старта (drag и создание) ─────────────────────────────────────

    [Fact]
    public void ClampStart_fits_untouched()
    {
        Assert.Equal(10 * 60, CalendarMath.ClampStartMin(10 * 60, 60, DayStart, DayEnd));
    }

    [Fact]
    public void ClampStart_clamps_to_day_end_minus_duration()
    {
        // Бросили час на 17:30 — влезает только с 17:00.
        Assert.Equal(17 * 60, CalendarMath.ClampStartMin(17 * 60 + 30, 60, DayStart, DayEnd));
    }

    [Fact]
    public void ClampStart_clamps_to_day_start()
    {
        Assert.Equal(DayStart, CalendarMath.ClampStartMin(6 * 60, 60, DayStart, DayEnd));
    }

    [Fact]
    public void ClampStart_duration_exceeding_window_clamps_to_day_start_only()
    {
        // Событие 10 часов в окно 9 часов не влезает — прижимается только к началу дня.
        Assert.Equal(DayStart, CalendarMath.ClampStartMin(14 * 60, 10 * 60, DayStart, DayEnd));
    }

    // ── Растягивание вниз: перескок за конец дня ──────────────────────────────

    [Fact]
    public void ResizeEnd_within_day_untouched()
    {
        Assert.Equal((Fri, 17 * 60), CalendarMath.ResizeEndTarget(Fri, 17 * 60, DayStart, DayEnd, true));
        Assert.Equal((Fri, DayEnd), CalendarMath.ResizeEndTarget(Fri, DayEnd, DayStart, DayEnd, true));
    }

    [Fact]
    public void ResizeEnd_past_day_end_jumps_to_next_visible_day_start()
    {
        // Пятница, указатель на 20:00 — конец уходит на понедельник, излишек хода (2 ч)
        // продолжается от начала дня: 09:00 + 2:00 = 11:00.
        var (day, min) = CalendarMath.ResizeEndTarget(Fri, 20 * 60, DayStart, DayEnd, fiveDay: true);
        Assert.Equal(Mon.AddDays(7), day);
        Assert.Equal(11 * 60, min);

        // В 7-дневке — суббота.
        (day, _) = CalendarMath.ResizeEndTarget(Fri, 20 * 60, DayStart, DayEnd, fiveDay: false);
        Assert.Equal(Fri.AddDays(1), day);
    }

    [Fact]
    public void ResizeEnd_overflow_is_capped_by_next_day_end()
    {
        // Излишек больше рабочего окна — конец не выпрыгивает за конец следующего дня.
        var (day, min) = CalendarMath.ResizeEndTarget(Fri, 1440, 17 * 60, DayEnd, fiveDay: true);
        Assert.Equal(Mon.AddDays(7), day);
        Assert.Equal(DayEnd, min);
    }

    [Fact]
    public void ResizeEnd_below_day_start_is_left_to_min_duration_rule()
    {
        // Конец раньше начала дня (событие вне часов) — не трогаем: нижнюю границу
        // держит минимум 30 минут на стороне вызывающего.
        Assert.Equal((Mon, 8 * 60), CalendarMath.ResizeEndTarget(Mon, 8 * 60, DayStart, DayEnd, true));
    }

    // ── Растягивание вверх: перескок за начало дня ────────────────────────────

    [Fact]
    public void ResizeStart_before_day_start_jumps_to_prev_visible_day_end()
    {
        // Понедельник, указатель на 07:00 — старт уходит на пятницу: 18:00 − 2:00 = 16:00.
        var (day, min) = CalendarMath.ResizeStartTarget(Mon.AddDays(7), 7 * 60, DayStart, DayEnd, fiveDay: true);
        Assert.Equal(Fri, day);
        Assert.Equal(16 * 60, min);

        (day, _) = CalendarMath.ResizeStartTarget(Mon.AddDays(7), 7 * 60, DayStart, DayEnd, fiveDay: false);
        Assert.Equal(Mon.AddDays(6), day);
    }

    [Fact]
    public void ResizeStart_underflow_is_capped_by_prev_day_start()
    {
        var (day, min) = CalendarMath.ResizeStartTarget(Mon, 0, DayStart, 10 * 60, fiveDay: true);
        Assert.Equal(Fri.AddDays(-7), day);
        Assert.Equal(DayStart, min);
    }

    [Fact]
    public void ResizeStart_after_day_end_is_left_to_min_duration_rule()
    {
        // Старт позже конца дня (событие вне часов, 20:00) — не трогаем: верхнюю границу
        // держит минимум 30 минут (start <= end - 30) на стороне вызывающего.
        Assert.Equal((Mon, 20 * 60), CalendarMath.ResizeStartTarget(Mon, 20 * 60, DayStart, DayEnd, true));
    }

    // ── Совместимость лимитов с минимумом 30 минут ────────────────────────────

    [Fact]
    public void ResizeEnd_min_duration_wins_over_day_end()
    {
        // Событие началось в 17:45 при конце дня 18:00: минимум 30 минут несовместим
        // с лимитом — лимит для события отключается (иначе ручку заклинит).
        Assert.Equal(CalendarMath.MinutesPerDay, CalendarMath.EffectiveDayEnd(17 * 60 + 45, DayEnd));
        // Обычное событие — лимит действует.
        Assert.Equal(DayEnd, CalendarMath.EffectiveDayEnd(10 * 60, DayEnd));
        // Событие целиком вне часов (20:00) — тоже без лимита.
        Assert.Equal(CalendarMath.MinutesPerDay, CalendarMath.EffectiveDayEnd(20 * 60, DayEnd));
    }

    [Fact]
    public void ResizeStart_min_duration_wins_over_day_start()
    {
        Assert.Equal(0, CalendarMath.EffectiveDayStart(9 * 60 + 15, DayStart));
        Assert.Equal(DayStart, CalendarMath.EffectiveDayStart(12 * 60, DayStart));
        Assert.Equal(0, CalendarMath.EffectiveDayStart(8 * 60, DayStart));
    }
}
