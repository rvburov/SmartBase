using SmartBase.Core.Calendar;
using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Настройки календаря в AppSettings и живой сервис CalendarSettings: инвариант
/// «в БД хранятся только отличия от стандарта» (дефолт = отсутствие ключа), валидация
/// мусора и кросс-инвариант рабочих часов (start + 60 &lt;= end) — нарушенная пара
/// читается как дефолт 0/1440 («без ограничений»), а SetDayBounds разводит границы сам.
/// </summary>
public sealed class CalendarSettingsTests
{
    private sealed class FakeStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _d = new();
        public bool Has(string key) => _d.ContainsKey(key);
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _d[key] = value;
        public void Remove(string key) => _d.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            [.. _d.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))];
    }

    private static FakeStore Attach()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        CalendarSettings.Init();
        return store;
    }

    // ── Дефолты ───────────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_saved_reads_as_defaults()
    {
        Attach();
        Assert.Equal(7, AppSettings.LoadCalendarDaysInWeek());
        Assert.Equal(0, AppSettings.LoadCalendarDayStart());
        Assert.Equal(1440, AppSettings.LoadCalendarDayEnd());
        Assert.Equal("week", AppSettings.LoadCalendarWeekScroll());
        Assert.Equal(60, AppSettings.LoadCalendarNewEventDuration());

        Assert.False(CalendarSettings.FiveDayWeek);
        Assert.False(CalendarSettings.HasDayLimits);
        Assert.Equal(CalendarWeekScrollMode.ByWeek, CalendarSettings.WeekScrollMode);
        Assert.Equal(60, CalendarSettings.NewEventDurationMinutes);
    }

    // ── Дней в неделе ─────────────────────────────────────────────────────────

    [Fact]
    public void Days_in_week_survives_save_and_load()
    {
        Attach();
        CalendarSettings.SetDaysInWeek(5);
        Assert.Equal(5, AppSettings.LoadCalendarDaysInWeek());
        Assert.True(CalendarSettings.FiveDayWeek);
    }

    [Fact]
    public void Days_in_week_default_is_stored_as_absence_of_key()
    {
        var store = Attach();
        CalendarSettings.SetDaysInWeek(5);
        Assert.True(store.Has("calendarDaysInWeek"));
        CalendarSettings.SetDaysInWeek(7);
        Assert.False(store.Has("calendarDaysInWeek"));
    }

    [Fact]
    public void Days_in_week_garbage_falls_back_to_default()
    {
        var store = Attach();
        store.Set("calendarDaysInWeek", "6");
        Assert.Equal(7, AppSettings.LoadCalendarDaysInWeek());
        store.Set("calendarDaysInWeek", "пять");
        Assert.Equal(7, AppSettings.LoadCalendarDaysInWeek());
        // Set с мусором молча игнорируется — кэш сервиса не меняется.
        CalendarSettings.SetDaysInWeek(6);
        Assert.Equal(7, CalendarSettings.DaysInWeek);
    }

    // ── Рабочие часы ──────────────────────────────────────────────────────────

    [Fact]
    public void Day_bounds_survive_save_and_load()
    {
        Attach();
        CalendarSettings.SetDayBounds(9 * 60, 18 * 60);
        Assert.Equal(9 * 60, AppSettings.LoadCalendarDayStart());
        Assert.Equal(18 * 60, AppSettings.LoadCalendarDayEnd());

        CalendarSettings.Init();
        Assert.Equal(9 * 60, CalendarSettings.DayStartMinutes);
        Assert.Equal(18 * 60, CalendarSettings.DayEndMinutes);
        Assert.True(CalendarSettings.HasDayLimits);
    }

    [Fact]
    public void Day_bounds_defaults_are_stored_as_absence_of_keys()
    {
        var store = Attach();
        CalendarSettings.SetDayBounds(9 * 60, 18 * 60);
        Assert.True(store.Has("calendarDayStartMin"));
        Assert.True(store.Has("calendarDayEndMin"));
        CalendarSettings.SetDayBounds(0, 1440);
        Assert.False(store.Has("calendarDayStartMin"));
        Assert.False(store.Has("calendarDayEndMin"));
    }

    [Fact]
    public void Day_bounds_incompatible_pair_reads_as_defaults()
    {
        var store = Attach();
        // Руками испорченная пара: конец раньше начала — сервис читает дефолт.
        store.Set("calendarDayStartMin", "1000");
        store.Set("calendarDayEndMin", "900");
        CalendarSettings.Init();
        Assert.Equal(0, CalendarSettings.DayStartMinutes);
        Assert.Equal(1440, CalendarSettings.DayEndMinutes);
        Assert.False(CalendarSettings.HasDayLimits);
    }

    [Fact]
    public void Day_bounds_garbage_falls_back_to_default()
    {
        var store = Attach();
        store.Set("calendarDayStartMin", "-15");
        store.Set("calendarDayEndMin", "1500");
        Assert.Equal(0, AppSettings.LoadCalendarDayStart());
        Assert.Equal(1440, AppSettings.LoadCalendarDayEnd());
    }

    [Fact]
    public void Set_day_bounds_keeps_minimum_hour_window()
    {
        Attach();
        // Конец вплотную к старту — конец отодвигается на час.
        CalendarSettings.SetDayBounds(9 * 60, 9 * 60 + 15);
        Assert.Equal(9 * 60, CalendarSettings.DayStartMinutes);
        Assert.Equal(10 * 60, CalendarSettings.DayEndMinutes);
        // У края суток отодвигать некуда — поджимается старт.
        CalendarSettings.SetDayBounds(23 * 60 + 45, 1440);
        Assert.Equal(23 * 60, CalendarSettings.DayStartMinutes);
        Assert.Equal(1440, CalendarSettings.DayEndMinutes);
    }

    // ── Прокрутка недели ──────────────────────────────────────────────────────

    [Fact]
    public void Week_scroll_survives_save_and_load()
    {
        Attach();
        CalendarSettings.SetWeekScrollMode(CalendarWeekScrollMode.ByDay);
        Assert.Equal("day", AppSettings.LoadCalendarWeekScroll());
        CalendarSettings.SetWeekScrollMode(CalendarWeekScrollMode.ByWeekStopToday);
        Assert.Equal("weekStopToday", AppSettings.LoadCalendarWeekScroll());
        CalendarSettings.Init();
        Assert.Equal(CalendarWeekScrollMode.ByWeekStopToday, CalendarSettings.WeekScrollMode);
    }

    [Fact]
    public void Week_scroll_default_is_stored_as_absence_of_key()
    {
        var store = Attach();
        CalendarSettings.SetWeekScrollMode(CalendarWeekScrollMode.ByDay);
        Assert.True(store.Has("calendarWeekScroll"));
        CalendarSettings.SetWeekScrollMode(CalendarWeekScrollMode.ByWeek);
        Assert.False(store.Has("calendarWeekScroll"));
    }

    [Fact]
    public void Week_scroll_garbage_falls_back_to_default()
    {
        var store = Attach();
        store.Set("calendarWeekScroll", "diagonal");
        Assert.Equal("week", AppSettings.LoadCalendarWeekScroll());
    }

    // ── Длительность нового события ───────────────────────────────────────────

    [Fact]
    public void New_event_duration_survives_save_and_load()
    {
        Attach();
        CalendarSettings.SetNewEventDuration(90);
        Assert.Equal(90, AppSettings.LoadCalendarNewEventDuration());
    }

    [Fact]
    public void New_event_duration_default_is_stored_as_absence_of_key()
    {
        var store = Attach();
        CalendarSettings.SetNewEventDuration(30);
        Assert.True(store.Has("calendarNewEventDuration"));
        CalendarSettings.SetNewEventDuration(60);
        Assert.False(store.Has("calendarNewEventDuration"));
    }

    [Fact]
    public void New_event_duration_garbage_falls_back_to_default()
    {
        var store = Attach();
        store.Set("calendarNewEventDuration", "45");
        Assert.Equal(60, AppSettings.LoadCalendarNewEventDuration());
        CalendarSettings.SetNewEventDuration(45);
        Assert.Equal(60, CalendarSettings.NewEventDurationMinutes);
    }

    // ── Событие Changed ───────────────────────────────────────────────────────

    [Fact]
    public void Set_fires_changed_only_on_real_change()
    {
        Attach();
        var fired = 0;
        Action handler = () => fired++;
        CalendarSettings.Changed += handler;
        try
        {
            CalendarSettings.SetDaysInWeek(5);
            CalendarSettings.SetDaysInWeek(5);           // без изменения — без события
            CalendarSettings.SetDayBounds(0, 1440);      // уже дефолт — без события
            CalendarSettings.SetWeekScrollMode(CalendarWeekScrollMode.ByDay);
            Assert.Equal(2, fired);
        }
        finally
        {
            CalendarSettings.Changed -= handler;
            CalendarSettings.SetDaysInWeek(7);
            CalendarSettings.SetWeekScrollMode(CalendarWeekScrollMode.ByWeek);
        }
    }
}
