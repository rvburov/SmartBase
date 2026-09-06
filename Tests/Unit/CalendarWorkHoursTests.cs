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
    public void ResizeEnd_overflow_bigger_than_window_flows_across_days()
    {
        // Излишек больше рабочего окна перетекает дальше — растягивание работает
        // через несколько дней. Окно 17:00–18:00 (1 час), указатель на низу сетки:
        // 6 часов излишка = 6 рабочих дней вперед, конец в конце окна.
        var (day, min) = CalendarMath.ResizeEndTarget(Fri, 1440, 17 * 60, DayEnd, fiveDay: true);
        Assert.Equal(Mon.AddDays(14), day);
        Assert.Equal(DayEnd, min);
    }

    [Fact]
    public void ResizeEnd_far_overflow_flows_two_days()
    {
        // Окно 09:00–18:00, ход с виртуальной поправкой ниже низа сетки (29:00):
        // Пн 18:00 → Вт (окно целиком) → Ср 11:00.
        var (day, min) = CalendarMath.ResizeEndTarget(Mon, 29 * 60, DayStart, DayEnd, fiveDay: true);
        Assert.Equal(Mon.AddDays(2), day);
        Assert.Equal(11 * 60, min);
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
    public void ResizeStart_underflow_bigger_than_window_flows_across_days()
    {
        // Окно 09:00–10:00 (1 час), указатель на верху сетки (00:00): 9 часов
        // недолета = 9 рабочих дней назад, старт в начале окна.
        var (day, min) = CalendarMath.ResizeStartTarget(Mon, 0, DayStart, 10 * 60, fiveDay: true);
        Assert.Equal(Mon.AddDays(-13), day);
        Assert.Equal(DayStart, min);
    }

    [Fact]
    public void ResizeStart_after_day_end_is_left_to_min_duration_rule()
    {
        // Старт позже конца дня (событие вне часов, 20:00) — не трогаем: верхнюю границу
        // держит минимум 30 минут (start <= end - 30) на стороне вызывающего.
        Assert.Equal((Mon, 20 * 60), CalendarMath.ResizeStartTarget(Mon, 20 * 60, DayStart, DayEnd, true));
    }

    // ── Партиция «сетка / полоса „весь день"» ─────────────────────────────────

    [Fact]
    public void FitsHourGrid_no_limits_keeps_legacy_24h_threshold()
    {
        Assert.True(CalendarMath.FitsHourGrid(Mon.AddHours(10), Mon.AddHours(33), 0, 1440, false));
        Assert.False(CalendarMath.FitsHourGrid(Mon.AddHours(10), Mon.AddHours(35), 0, 1440, false));
        // 5-дневка: выходные не считаются — Пт 23:00 – Пн 01:00 остается в сетке.
        Assert.True(CalendarMath.FitsHourGrid(
            Fri.AddHours(23), Mon.AddDays(7).AddHours(1), 0, 1440, true));
    }

    [Fact]
    public void FitsHourGrid_with_limits_any_length_stays_in_grid()
    {
        // Рабочие часы: растягивание на несколько дней рисует блоки по дням —
        // событие не уходит в полосу «весь день» независимо от длины.
        Assert.True(CalendarMath.FitsHourGrid(
            Mon.AddHours(11), Mon.AddDays(4).AddHours(10), DayStart, DayEnd, false));
        Assert.True(CalendarMath.FitsHourGrid(
            Mon.AddHours(11), Mon.AddDays(14), DayStart, DayEnd, true));
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

    // ── Рабочая лента: нормализация старта drag ───────────────────────────────

    [Fact]
    public void NormalizeDragStart_no_limits_matches_legacy_branches()
    {
        // Прежние ветки: <0 → предыдущий день, ≥1440 → следующий, иначе clamp 0..1425.
        Assert.Equal((0, 10 * 60), CalendarMath.NormalizeDragStart(10 * 60, 0, 1440));
        Assert.Equal((0, 1425), CalendarMath.NormalizeDragStart(1435, 0, 1440));
        Assert.Equal((-1, 23 * 60), CalendarMath.NormalizeDragStart(-60, 0, 1440));
        Assert.Equal((1, 60), CalendarMath.NormalizeDragStart(1500, 0, 1440));
        Assert.Equal((1, 0), CalendarMath.NormalizeDragStart(1440, 0, 1440));
    }

    [Fact]
    public void NormalizeDragStart_dead_zones_map_to_neighbor_days()
    {
        // Зона за концом дня (18:00+) — это уже следующий день: 20:00 → 09:00 + 2:00.
        Assert.Equal((1, 11 * 60), CalendarMath.NormalizeDragStart(20 * 60, DayStart, DayEnd));
        // Зона до начала дня (08:00) — предыдущий день: 18:00 − 1:00 = 17:00.
        Assert.Equal((-1, 17 * 60), CalendarMath.NormalizeDragStart(8 * 60, DayStart, DayEnd));
        // Внутри окна — как есть; впритык к концу — не позже dayEnd − 15.
        Assert.Equal((0, 12 * 60), CalendarMath.NormalizeDragStart(12 * 60, DayStart, DayEnd));
        Assert.Equal((1, DayStart), CalendarMath.NormalizeDragStart(DayEnd, DayStart, DayEnd));
    }

    // ── Рабочая лента: конец события от старта и длительности ─────────────────

    [Fact]
    public void AddWorkingMinutes_no_limits_equals_add_minutes()
    {
        // Через полночь: 23:00 + 2:00 = 01:00 следующего видимого дня.
        Assert.Equal((Fri.AddDays(1), 60),
            CalendarMath.AddWorkingMinutes(Fri, 23 * 60, 120, 0, 1440, fiveDay: false));
        // При 5-дневке хвост пятницы уходит на понедельник — «суббота» не существует.
        Assert.Equal((Mon.AddDays(7), 60),
            CalendarMath.AddWorkingMinutes(Fri, 23 * 60, 120, 0, 1440, fiveDay: true));
        // Конец ровно в полночь остается минутой 1440 того же дня.
        Assert.Equal((Fri, 1440),
            CalendarMath.AddWorkingMinutes(Fri, 23 * 60, 60, 0, 1440, fiveDay: true));
    }

    [Fact]
    public void AddWorkingMinutes_wraps_at_day_end()
    {
        // Вторник 17:00 + 2 часа при 09:00–18:00 = среда 10:00 (час во вторник, час в среду).
        var tue = Mon.AddDays(1);
        Assert.Equal((tue.AddDays(1), 10 * 60),
            CalendarMath.AddWorkingMinutes(tue, 17 * 60, 120, DayStart, DayEnd, fiveDay: false));
        // Пятница 17:00 + 2 часа при 5-дневке = понедельник 10:00.
        Assert.Equal((Mon.AddDays(7), 10 * 60),
            CalendarMath.AddWorkingMinutes(Fri, 17 * 60, 120, DayStart, DayEnd, fiveDay: true));
        // Ровно до конца дня — без перескока.
        Assert.Equal((Fri, DayEnd),
            CalendarMath.AddWorkingMinutes(Fri, 17 * 60, 60, DayStart, DayEnd, fiveDay: true));
        // Длиннее рабочего окна — перетекает через несколько дней: 10 часов с 17:00
        // = 1 ч (пт) + 9 ч (пн) → конец в 18:00 понедельника.
        Assert.Equal((Mon.AddDays(7), DayEnd),
            CalendarMath.AddWorkingMinutes(Fri, 17 * 60, 10 * 60, DayStart, DayEnd, fiveDay: true));
    }

    // ── Рабочая лента: длительность события ───────────────────────────────────

    [Fact]
    public void WorkingLength_single_day_keeps_raw_duration()
    {
        // Однодневное вне часов не усыхает: 08:00–10:00 остается двумя часами.
        Assert.Equal(120, CalendarMath.WorkingLengthMinutes(
            Mon.AddHours(8), Mon.AddHours(10), DayStart, DayEnd, fiveDay: false));
        // До полуночи включительно — однодневное.
        Assert.Equal(60, CalendarMath.WorkingLengthMinutes(
            Mon.AddHours(23), Mon.AddDays(1), DayStart, DayEnd, fiveDay: false));
    }

    [Fact]
    public void WorkingLength_multiday_sums_visible_window_intersections()
    {
        var tue = Mon.AddDays(1);
        // Вторник 17:00 – среда 10:00 = час + час.
        Assert.Equal(120, CalendarMath.WorkingLengthMinutes(
            tue.AddHours(17), tue.AddDays(1).AddHours(10), DayStart, DayEnd, fiveDay: false));
        // Пятница 23:00 – понедельник 01:00 при 5-дневке без лимитов = 2 часа.
        Assert.Equal(120, CalendarMath.WorkingLengthMinutes(
            Fri.AddHours(23), Mon.AddDays(7).AddHours(1), 0, 1440, fiveDay: true));
        // Совсем мимо рабочих окон (чт 23:00 – пт 01:00 при 09–18) — сырая длительность.
        Assert.Equal(120, CalendarMath.WorkingLengthMinutes(
            Fri.AddDays(-1).AddHours(23), Fri.AddHours(1), DayStart, DayEnd, fiveDay: false));
        // Без лимитов при 7-дневке = сырая длительность (легаси).
        Assert.Equal(26 * 60, CalendarMath.WorkingLengthMinutes(
            tue.AddHours(10), tue.AddDays(1).AddHours(12), 0, 1440, fiveDay: false));
    }

    // ── Видимая длительность: порог «сетка или полоса „весь день"» ────────────

    [Fact]
    public void VisibleDuration_skips_weekend_days_in_five_day_mode()
    {
        // Пт 23:00 – Пн 01:00: в 7-дневке 50 часов (полоса), в 5-дневке 2 часа (сетка).
        var end = Mon.AddDays(7).AddHours(1);
        Assert.Equal(50 * 60, CalendarMath.VisibleDurationMinutes(Fri.AddHours(23), end, fiveDay: false));
        Assert.Equal(120, CalendarMath.VisibleDurationMinutes(Fri.AddHours(23), end, fiveDay: true));
        // Обычное событие — сырая длительность в обоих режимах.
        Assert.Equal(60, CalendarMath.VisibleDurationMinutes(Mon.AddHours(10), Mon.AddHours(11), true));
    }
}
