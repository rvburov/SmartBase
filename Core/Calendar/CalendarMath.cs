namespace SmartBase.Core.Calendar;

/// <summary>Режим листания вида «Неделя» кнопками Назад/Вперед.</summary>
public enum CalendarWeekScrollMode
{
    /// <summary>По неделям, окно выровнено на понедельник (стандарт).</summary>
    ByWeek,
    /// <summary>По одному видимому дню, окно скользит без выравнивания.</summary>
    ByDay,
    /// <summary>По неделям, но шаг через сегодняшний день останавливается с «сегодня» посередине.</summary>
    ByWeekStopToday,
}

/// <summary>
/// Чистая математика календаря: рабочая неделя (5/7 видимых дней), окно вида «Неделя»
/// по режимам прокрутки и рабочие часы дня. Никакого UI — все методы детерминированы
/// и покрыты юнит-тестами (CalendarMathTests, CalendarWorkHoursTests). Виды календаря
/// обязаны переводить колонку в дату только через <see cref="DayAtCol"/>: при 5-дневке
/// и прокрутке «по дню» дни окна НЕ непрерывны, арифметика AddDays по индексу колонки лжет.
/// </summary>
public static class CalendarMath
{
    /// <summary>Минут в сутках; конец дня 1440 означает «до конца суток» (лимита нет).</summary>
    public const int MinutesPerDay = 1440;

    /// <summary>Минимальная длительность события в сетке часов (как в CalendarRecordValidator).</summary>
    public const int MinEventMinutes = 30;

    // ── Дни и рабочая неделя ──────────────────────────────────────────────────

    /// <summary>Понедельник недели, в которую попадает день.</summary>
    public static DateTime StartOfWeek(DateTime d) =>
        d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    /// <summary>Суббота или воскресенье.</summary>
    public static bool IsWeekend(DateTime d) =>
        d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>День отображается в сетке: при 5-дневке выходные невидимы.</summary>
    public static bool IsVisibleDay(DateTime d, bool fiveDay) =>
        !(fiveDay && IsWeekend(d));

    /// <summary>Следующий видимый день: при 5-дневке с пятницы (и из выходных) — понедельник.</summary>
    public static DateTime NextVisibleDay(DateTime d, bool fiveDay)
    {
        var r = d.Date.AddDays(1);
        while (!IsVisibleDay(r, fiveDay)) r = r.AddDays(1);
        return r;
    }

    /// <summary>Предыдущий видимый день: при 5-дневке с понедельника (и из выходных) — пятница.</summary>
    public static DateTime PrevVisibleDay(DateTime d, bool fiveDay)
    {
        var r = d.Date.AddDays(-1);
        while (!IsVisibleDay(r, fiveDay)) r = r.AddDays(-1);
        return r;
    }

    /// <summary>Сдвиг на n видимых дней (n может быть отрицательным; 0 — тот же день).</summary>
    public static DateTime AddVisibleDays(DateTime d, int n, bool fiveDay)
    {
        var r = d.Date;
        var step = Math.Sign(n);
        for (var i = 0; i != n; i += step)
            r = step > 0 ? NextVisibleDay(r, fiveDay) : PrevVisibleDay(r, fiveDay);
        return r;
    }

    /// <summary>
    /// Прижать день к рабочему по направлению движения (direction ±1). Не выходной,
    /// 7-дневка или direction 0 — день остается как есть.
    /// </summary>
    public static DateTime SnapToWorkday(DateTime d, int direction, bool fiveDay)
    {
        if (!fiveDay || direction == 0 || !IsWeekend(d)) return d.Date;
        return direction > 0 ? NextVisibleDay(d, true) : PrevVisibleDay(d, true);
    }

    // ── Окно вида «Неделя» ────────────────────────────────────────────────────

    /// <summary>
    /// Дни окна недели: 7 подряд (или 5 видимых рабочих) от якоря. Якорь-выходной
    /// при 5-дневке сдвигается вперед к ближайшему рабочему дню.
    /// </summary>
    public static IReadOnlyList<DateTime> WeekWindow(DateTime anchor, bool fiveDay)
    {
        var count = fiveDay ? 5 : 7;
        var d = IsVisibleDay(anchor, fiveDay) ? anchor.Date : NextVisibleDay(anchor, fiveDay);
        var days = new List<DateTime>(count);
        for (var i = 0; i < count; i++)
        {
            days.Add(d);
            d = NextVisibleDay(d, fiveDay);
        }
        return days;
    }

    /// <summary>
    /// Якорь окна «сегодня посередине»: 7 дней — за три дня до сегодня; 5 дней — за два
    /// рабочих. Сегодня-выходной при 5-дневке центрировать не на что — понедельник его недели.
    /// </summary>
    public static DateTime CenteredWeekAnchor(DateTime today, bool fiveDay)
    {
        if (!fiveDay) return today.Date.AddDays(-3);
        if (IsWeekend(today)) return StartOfWeek(today);
        return AddVisibleDays(today, -2, true);
    }

    /// <summary>
    /// Якорь окна недели для текущего состояния: по неделям — понедельник; по дню — сам день
    /// (выходной при 5-дневке уедет вперед в <see cref="WeekWindow"/>); «с остановкой» —
    /// центр на сегодня, пока окно центрировано, иначе понедельник.
    /// </summary>
    public static DateTime WeekAnchor(DateTime current, CalendarWeekScrollMode mode,
                                      bool fiveDay, bool centered, DateTime today) => mode switch
    {
        CalendarWeekScrollMode.ByDay => current.Date,
        CalendarWeekScrollMode.ByWeekStopToday when centered => CenteredWeekAnchor(today, fiveDay),
        _ => StartOfWeek(current),
    };

    /// <summary>
    /// Шаг Назад/Вперед (direction ±1) вида «Неделя». Возвращает новый якорь окна и флаг
    /// «окно центрировано на сегодня». Режим «с остановкой»: шаг, перепрыгивающий сегодняшний
    /// день (или приходящий в его неделю), сначала останавливается с «сегодня» посередине;
    /// следующий шаг продолжает по неделям от понедельника недели сегодняшнего дня.
    /// </summary>
    public static (DateTime Anchor, bool Centered) StepWeekAnchor(
        DateTime anchor, bool centered, CalendarWeekScrollMode mode, bool fiveDay,
        DateTime today, int direction)
    {
        today = today.Date;
        switch (mode)
        {
            case CalendarWeekScrollMode.ByDay:
            {
                var start = IsVisibleDay(anchor, fiveDay) ? anchor.Date : NextVisibleDay(anchor, fiveDay);
                return (AddVisibleDays(start, direction, fiveDay), false);
            }
            case CalendarWeekScrollMode.ByWeekStopToday:
            {
                if (centered)
                    return (StartOfWeek(today).AddDays(direction * 7), false);

                var first = StartOfWeek(anchor);
                var last  = first.AddDays(fiveDay ? 4 : 6);   // последний ВИДИМЫЙ день окна
                var candidate = first.AddDays(direction * 7);
                var stops = direction > 0
                    ? last < today && today <= candidate.AddDays(fiveDay ? 4 : 6)
                    : candidate <= today && today < first;
                if (stops)
                {
                    var c = CenteredWeekAnchor(today, fiveDay);
                    // Центр совпал с текущим окном (сегодня — выходной его недели): не застревать.
                    if (c != first) return (c, true);
                }
                return (candidate, false);
            }
            default: // ByWeek
                return (StartOfWeek(anchor).AddDays(direction * 7), false);
        }
    }

    /// <summary>
    /// Дата колонки по списку видимых дней окна. Колонки за краями (drag/resize уводят
    /// указатель за сетку) экстраполируются видимыми днями: при 5-дневке колонка «после
    /// пятницы» — понедельник следующей недели.
    /// </summary>
    public static DateTime DayAtCol(IReadOnlyList<DateTime> days, int col, bool fiveDay)
    {
        if (col >= 0 && col < days.Count) return days[col].Date;
        return col < 0
            ? AddVisibleDays(days[0], col, fiveDay)
            : AddVisibleDays(days[^1], col - (days.Count - 1), fiveDay);
    }

    // ── Рабочие часы (минуты дня 0..1440) ─────────────────────────────────────
    // Дефолт 0/1440 означает «лимитов нет» — все методы вырождаются в сквозной проход,
    // поведение календаря без настройки тождественно прежнему.

    /// <summary>Лимиты дня заданы (не весь диапазон 0..1440).</summary>
    public static bool HasDayLimits(int dayStartMin, int dayEndMin) =>
        dayStartMin > 0 || dayEndMin < MinutesPerDay;

    /// <summary>
    /// Прижать старт перетаскиваемого/создаваемого события так, чтобы [start, start+duration]
    /// уложился в рабочие часы. Событие длиннее рабочего окна зажать невозможно —
    /// прижимается только старт к началу дня.
    /// </summary>
    public static int ClampStartMin(int startMin, int durationMin, int dayStartMin, int dayEndMin)
    {
        if (!HasDayLimits(dayStartMin, dayEndMin)) return startMin;
        if (durationMin > dayEndMin - dayStartMin) return dayStartMin;
        return Math.Clamp(startMin, dayStartMin, dayEndMin - durationMin);
    }

    /// <summary>
    /// Лимит конца дня для растягивания вниз: применим, только если совместим с минимумом
    /// 30 минут. Событие, начавшееся впритык к концу дня или позже (вне часов), ведет себя
    /// как без лимита — иначе ручку заклинит.
    /// </summary>
    public static int EffectiveDayEnd(int eventStartMin, int dayEndMin) =>
        eventStartMin + MinEventMinutes > dayEndMin ? MinutesPerDay : dayEndMin;

    /// <summary>Симметрично <see cref="EffectiveDayEnd"/> — лимит начала дня для растягивания вверх.</summary>
    public static int EffectiveDayStart(int eventEndMin, int dayStartMin) =>
        eventEndMin - MinEventMinutes < dayStartMin ? 0 : dayStartMin;

    /// <summary>
    /// Конец события при растягивании вниз: за концом дня продолжение «перепрыгивает»
    /// на начало следующего видимого дня (18:00 → 09:00 понедельника при 5-дневке),
    /// излишек хода указателя переносится туда же; излишек больше рабочего окна
    /// перетекает дальше — через несколько дней. Ранний край не трогается —
    /// его ограничивает минимум 30 минут на стороне вызывающего.
    /// </summary>
    public static (DateTime Day, int Min) ResizeEndTarget(DateTime day, int rawMin,
                                                          int dayStartMin, int dayEndMin, bool fiveDay)
    {
        day = day.Date;
        if (!HasDayLimits(dayStartMin, dayEndMin) || rawMin <= dayEndMin) return (day, rawMin);
        var window = Math.Max(1, dayEndMin - dayStartMin);
        var m = rawMin;
        while (m > dayEndMin)
        {
            day = NextVisibleDay(day, fiveDay);
            m  -= window;
        }
        return (day, m);
    }

    /// <summary>
    /// Старт события при растягивании вверх: раньше начала дня — «перепрыгивает» на конец
    /// предыдущего видимого дня (09:00 → 18:00 пятницы при 5-дневке). Поздний край не
    /// трогается — его ограничивает минимум 30 минут на стороне вызывающего.
    /// </summary>
    public static (DateTime Day, int Min) ResizeStartTarget(DateTime day, int rawMin,
                                                            int dayStartMin, int dayEndMin, bool fiveDay)
    {
        day = day.Date;
        if (!HasDayLimits(dayStartMin, dayEndMin) || rawMin >= dayStartMin) return (day, rawMin);
        var window = Math.Max(1, dayEndMin - dayStartMin);
        var m = rawMin;
        while (m < dayStartMin)
        {
            day = PrevVisibleDay(day, fiveDay);
            m  += window;
        }
        return (day, m);
    }

    // ── Рабочая лента: drag и партиция сетки ──────────────────────────────────
    // Границы дня ведут себя как полночь: время между dayEnd и dayStart следующего
    // видимого дня не существует, хвост события перетекает через границу (18:00 →
    // 09:00 понедельника при 5-дневке). Без лимитов лента совпадает с обычными
    // сутками, а методы — с прежней арифметикой AddMinutes/AddDays.

    /// <summary>
    /// Нормализация позиции старта перетаскиваемого события: снап-минуты могут выходить
    /// за границы дня (указатель утащен за сетку или в «мертвую» зону вне рабочих часов).
    /// Возвращает сдвиг в видимых днях и минуты старта в пределах дня; выход больше чем
    /// на окно (событие в 3+ дня схвачено за поздний сегмент) дает сдвиг на несколько
    /// дней. В пределах одного окна тождественно прежним веткам
    /// «&lt;0 → предыдущий день, ≥1440 → следующий».
    /// </summary>
    public static (int DayShift, int StartMin) NormalizeDragStart(int snappedMin,
                                                                  int dayStartMin, int dayEndMin)
    {
        var window = Math.Max(1, dayEndMin - dayStartMin);
        var shift  = 0;
        var m      = snappedMin;
        while (m < dayStartMin) { m += window; shift--; }
        while (m >= dayEndMin) { m -= window; shift++; }
        return (shift, Math.Clamp(m, dayStartMin, dayEndMin - 15));
    }

    /// <summary>
    /// Конец события: старт + длительность по рабочей ленте. Хвост за концом дня
    /// перетекает на начало следующего видимого дня. Конец ровно в dayEnd остается
    /// в том же дне (минуты 1440 вызывающий превращает в полночь следующих суток).
    /// Без лимитов и при 7-дневке тождественно StartDt.AddMinutes(minutes).
    /// </summary>
    public static (DateTime Day, int Min) AddWorkingMinutes(DateTime day, int startMin, int minutes,
                                                            int dayStartMin, int dayEndMin, bool fiveDay)
    {
        day = day.Date;
        var window = Math.Max(1, dayEndMin - dayStartMin);
        var m      = startMin + minutes;
        while (m > dayEndMin)
        {
            day = NextVisibleDay(day, fiveDay);
            m  -= window;
        }
        return (day, m);
    }

    /// <summary>
    /// Длительность события в минутах рабочей ленты — то, что сохраняет перетаскивание.
    /// Однодневное — сырая длительность (событие вне часов не усыхает). Многодневное —
    /// сумма пересечений с рабочими окнами видимых дней (Пт 23:00 – Пн 01:00 при
    /// 5-дневке — 2 часа); совсем не пересекается — сырая длительность, не больше суток.
    /// Без лимитов и при 7-дневке тождественно (EndDt − StartDt).TotalMinutes.
    /// </summary>
    public static int WorkingLengthMinutes(DateTime startDt, DateTime endDt,
                                           int dayStartMin, int dayEndMin, bool fiveDay)
    {
        var raw = (int)(endDt - startDt).TotalMinutes;
        if (raw <= 0) return 0;
        if (endDt <= startDt.Date.AddDays(1)) return raw;

        var total = 0;
        for (var d = startDt.Date; d < endDt; d = d.AddDays(1))
        {
            if (!IsVisibleDay(d, fiveDay)) continue;
            var s = d == startDt.Date ? (int)startDt.TimeOfDay.TotalMinutes : 0;
            var e = d == endDt.Date ? (int)endDt.TimeOfDay.TotalMinutes : MinutesPerDay;
            total += Math.Max(0, Math.Min(e, dayEndMin) - Math.Max(s, dayStartMin));
        }
        return total > 0 ? total : Math.Min(raw, MinutesPerDay);
    }

    /// <summary>
    /// Видимая длительность события в минутах — критерий «часовая сетка или полоса
    /// „весь день"» (порог 1440). При 5-дневке дни выходных не считаются: Пт 23:00 –
    /// Пн 01:00 — 2 минуты × 60, событие остается в сетке. При 7-дневке — сырая
    /// длительность (прежний порог 24 часов).
    /// </summary>
    public static int VisibleDurationMinutes(DateTime startDt, DateTime endDt, bool fiveDay) =>
        WorkingLengthMinutes(startDt, endDt, 0, MinutesPerDay, fiveDay);

    /// <summary>
    /// Событие (не «весь день») отображается в часовой сетке, а не в полосе «весь день».
    /// Стандарт (multiDayGrid=false) — прежний порог: видимая длительность меньше суток,
    /// длиннее — полоса «весь день». С настройкой «длинные события в сетке» событие
    /// любой длины остается в сетке блоками по дням (серединные дни — полное рабочее
    /// окно или сутки), в полосу уходят только AllDay. Правило одно для любого источника
    /// длительности: drag, resize или ручной ввод времени в окне записи.
    /// </summary>
    public static bool FitsHourGrid(DateTime startDt, DateTime endDt,
                                    bool multiDayGrid, bool fiveDay) =>
        multiDayGrid || VisibleDurationMinutes(startDt, endDt, fiveDay) < MinutesPerDay;

    // ── Месяц: бары многодневных ──────────────────────────────────────────────

    /// <summary>
    /// Клип сегмента бара к рабочим колонкам недели Месяца (неделя выровнена на понедельник,
    /// выходные — колонки 5 и 6). null — сегмент целиком в выходных, бар в этой неделе
    /// не рисуется. При 7-дневке сегмент возвращается как есть.
    /// </summary>
    public static (int Sc, int Ec)? ClipSpanToWorkdays(int sc, int ec, bool fiveDay)
    {
        if (!fiveDay) return (sc, ec);
        if (sc > 4) return null;
        return (sc, Math.Min(ec, 4));
    }
}
