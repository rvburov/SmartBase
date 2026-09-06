using SmartBase.Core.Calendar;

namespace SmartBase.Utils;

/// <summary>
/// Живые настройки календаря (по образцу <see cref="FontScale"/>): кэш значений из
/// AppSettings + событие <see cref="Changed"/> — календарь слушает и пересобирается
/// без перезапуска. Set* сохраняют дельту и публикуют изменение. Кросс-инвариант
/// рабочих часов (start + 60 &lt;= end) проверяется здесь: нарушен — пара считается
/// дефолтной (0/1440, «без ограничений»).
/// </summary>
public static class CalendarSettings
{
    /// <summary>Минимальное рабочее окно дня в минутах (день не короче часа).</summary>
    public const int MinDayWindowMinutes = 60;

    /// <summary>Дней в неделе: 7 (Пн-Вс) или 5 (Пн-Пт).</summary>
    public static int DaysInWeek { get; private set; } = AppSettings.CalendarDaysInWeekDefault;

    /// <summary>Рабочая неделя Пн-Пт: виды «День»/«Неделя» без выходных.</summary>
    public static bool FiveDayWeek => DaysInWeek == 5;

    /// <summary>Начало дня в минутах от полуночи (0 — без ограничения).</summary>
    public static int DayStartMinutes { get; private set; } = AppSettings.CalendarDayStartDefault;

    /// <summary>Конец дня в минутах от полуночи (1440 — до конца суток).</summary>
    public static int DayEndMinutes { get; private set; } = AppSettings.CalendarDayEndDefault;

    /// <summary>Рабочие часы заданы (не весь диапазон суток).</summary>
    public static bool HasDayLimits => CalendarMath.HasDayLimits(DayStartMinutes, DayEndMinutes);

    /// <summary>Режим листания вида «Неделя».</summary>
    public static CalendarWeekScrollMode WeekScrollMode { get; private set; } = CalendarWeekScrollMode.ByWeek;

    /// <summary>Длительность нового события из сетки, минуты.</summary>
    public static int NewEventDurationMinutes { get; private set; } = AppSettings.CalendarNewEventDurationDefault;

    /// <summary>Настройки изменились — календарю пора пересобраться.</summary>
    public static event Action? Changed;

    /// <summary>Счетчик изменений — «догоняющая» перерисовка при возврате к календарю
    /// (изменение могло произойти, пока был открыт другой раздел), как FontScale.Delta.</summary>
    public static long Stamp { get; private set; }

    /// <summary>Старт: считать сохраненные значения. Вызывать после AppSettings.AttachStore.</summary>
    public static void Init()
    {
        DaysInWeek = AppSettings.LoadCalendarDaysInWeek();
        (DayStartMinutes, DayEndMinutes) = LoadDayBounds();
        WeekScrollMode = ParseScroll(AppSettings.LoadCalendarWeekScroll());
        NewEventDurationMinutes = AppSettings.LoadCalendarNewEventDuration();
    }

    public static void SetDaysInWeek(int days)
    {
        if (days is not (5 or 7) || days == DaysInWeek) return;
        DaysInWeek = days;
        AppSettings.SaveCalendarDaysInWeek(days);
        Stamp++;
        Changed?.Invoke();
    }

    /// <summary>
    /// Задать рабочие часы. Значения зажимаются в сутки и разводятся минимум на час:
    /// конец подтягивается за стартом (а у края суток — старт под конец).
    /// </summary>
    public static void SetDayBounds(int startMin, int endMin)
    {
        startMin = Math.Clamp(startMin, 0, 1425);
        endMin   = Math.Clamp(endMin, 15, CalendarMath.MinutesPerDay);
        if (endMin - startMin < MinDayWindowMinutes)
        {
            endMin = startMin + MinDayWindowMinutes;
            if (endMin > CalendarMath.MinutesPerDay)
            {
                endMin   = CalendarMath.MinutesPerDay;
                startMin = endMin - MinDayWindowMinutes;
            }
        }
        if (startMin == DayStartMinutes && endMin == DayEndMinutes) return;
        DayStartMinutes = startMin;
        DayEndMinutes   = endMin;
        AppSettings.SaveCalendarDayStart(startMin);
        AppSettings.SaveCalendarDayEnd(endMin);
        Stamp++;
        Changed?.Invoke();
    }

    public static void SetWeekScrollMode(CalendarWeekScrollMode mode)
    {
        if (mode == WeekScrollMode) return;
        WeekScrollMode = mode;
        AppSettings.SaveCalendarWeekScroll(ScrollKey(mode));
        Stamp++;
        Changed?.Invoke();
    }

    public static void SetNewEventDuration(int minutes)
    {
        if (minutes is not (30 or 60 or 90 or 120) || minutes == NewEventDurationMinutes) return;
        NewEventDurationMinutes = minutes;
        AppSettings.SaveCalendarNewEventDuration(minutes);
        Stamp++;
        Changed?.Invoke();
    }

    private static (int Start, int End) LoadDayBounds()
    {
        var start = AppSettings.LoadCalendarDayStart();
        var end   = AppSettings.LoadCalendarDayEnd();
        return end - start >= MinDayWindowMinutes
            ? (start, end)
            : (AppSettings.CalendarDayStartDefault, AppSettings.CalendarDayEndDefault);
    }

    private static CalendarWeekScrollMode ParseScroll(string key) => key switch
    {
        "day"           => CalendarWeekScrollMode.ByDay,
        "weekStopToday" => CalendarWeekScrollMode.ByWeekStopToday,
        _               => CalendarWeekScrollMode.ByWeek,
    };

    private static string ScrollKey(CalendarWeekScrollMode mode) => mode switch
    {
        CalendarWeekScrollMode.ByDay           => "day",
        CalendarWeekScrollMode.ByWeekStopToday => "weekStopToday",
        _                                      => "week",
    };
}
