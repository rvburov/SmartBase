using Avalonia.Controls;
using SmartBase.Core.Calendar;
using SmartBase.Utils;

namespace SmartBase.Views.Settings;

/// <summary>
/// Раздел «Настройки → Календарь» (partial SettingsView, без ViewModel — как «Бэкап»).
/// Строки стартового вида и видимости видов обслуживает общий InitAppearance (контролы
/// просто живут в этой панели); остальные строки наполняются здесь и пишут в живой
/// сервис <see cref="CalendarSettings"/> — календарь пересобирается сразу, по событию
/// Changed. Конец дня «до конца суток» (1440 минут) в поле времени показывается как
/// 00:00 — сам виджет TimeField про 24:00 не знает, маппинг только здесь.
/// </summary>
public partial class SettingsView
{
    // Пункты закрытых списков; индексы совпадают с ComboBox.SelectedIndex.
    private static readonly int[] CalendarDayOptions = [7, 5];
    private static readonly string[] CalendarDayLabels = ["7 (Пн–Вс)", "5 (Пн–Пт)"];

    private static readonly CalendarWeekScrollMode[] CalendarScrollOptions =
        [CalendarWeekScrollMode.ByWeek, CalendarWeekScrollMode.ByDay, CalendarWeekScrollMode.ByWeekStopToday];
    private static readonly string[] CalendarScrollLabels =
        ["По неделе", "По дню", "По неделе с остановкой на «Сегодня»"];

    private static readonly int[] CalendarDurationOptions = [30, 60, 90, 120];
    private static readonly string[] CalendarDurationLabels = ["30 минут", "1 час", "1,5 часа", "2 часа"];

    // Гасит обработчики, пока контролы наполняются кодом (как _backupBuilding у «Бэкапа»).
    private bool _calendarBuilding;

    // Поля времени подписываются кодом один раз: TimeChanged — обычное CLR-событие.
    private bool _calendarHooked;

    // ── Построение раздела ────────────────────────────────────────────────────

    private void RebuildCalendarPanel()
    {
        _calendarBuilding = true;

        if (!_calendarHooked)
        {
            _calendarHooked = true;
            CalendarDayStartField.TimeChanged += (_, _) => OnCalendarDayBoundsChanged();
            CalendarDayEndField.TimeChanged   += (_, _) => OnCalendarDayBoundsChanged();
        }

        CalendarDaysCombo.ItemsSource   = CalendarDayLabels;
        CalendarDaysCombo.SelectedIndex = Math.Max(0, Array.IndexOf(CalendarDayOptions, CalendarSettings.DaysInWeek));

        CalendarWeekScrollCombo.ItemsSource   = CalendarScrollLabels;
        CalendarWeekScrollCombo.SelectedIndex = Math.Max(0, Array.IndexOf(CalendarScrollOptions, CalendarSettings.WeekScrollMode));

        CalendarDurationCombo.ItemsSource   = CalendarDurationLabels;
        CalendarDurationCombo.SelectedIndex = Math.Max(0, Array.IndexOf(CalendarDurationOptions, CalendarSettings.NewEventDurationMinutes));

        ShowCalendarDayBounds();

        _calendarBuilding = false;
    }

    // Показать фактические рабочие часы (после зажима сервисом): 1440 ↔ 00:00 в поле конца.
    private void ShowCalendarDayBounds()
    {
        CalendarDayStartField.Time = TimeSpan.FromMinutes(CalendarSettings.DayStartMinutes);
        CalendarDayEndField.Time   = CalendarSettings.DayEndMinutes >= CalendarMath.MinutesPerDay
            ? TimeSpan.Zero
            : TimeSpan.FromMinutes(CalendarSettings.DayEndMinutes);
    }

    // ── Обработчики ───────────────────────────────────────────────────────────

    private void OnCalendarDaysChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_calendarBuilding) return;
        var i = CalendarDaysCombo.SelectedIndex;
        if (i >= 0 && i < CalendarDayOptions.Length)
            CalendarSettings.SetDaysInWeek(CalendarDayOptions[i]);
    }

    private void OnCalendarWeekScrollChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_calendarBuilding) return;
        var i = CalendarWeekScrollCombo.SelectedIndex;
        if (i >= 0 && i < CalendarScrollOptions.Length)
            CalendarSettings.SetWeekScrollMode(CalendarScrollOptions[i]);
    }

    private void OnCalendarDurationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_calendarBuilding) return;
        var i = CalendarDurationCombo.SelectedIndex;
        if (i >= 0 && i < CalendarDurationOptions.Length)
            CalendarSettings.SetNewEventDuration(CalendarDurationOptions[i]);
    }

    private void OnCalendarDayBoundsChanged()
    {
        if (_calendarBuilding) return;

        var start = (int)(CalendarDayStartField.TimeOrNull ?? TimeSpan.FromMinutes(CalendarSettings.DayStartMinutes)).TotalMinutes;
        var endTs = CalendarDayEndField.TimeOrNull;
        var end   = endTs is null ? CalendarSettings.DayEndMinutes
                  : endTs.Value == TimeSpan.Zero ? CalendarMath.MinutesPerDay   // 00:00 = до конца суток
                  : (int)endTs.Value.TotalMinutes;

        // Обе границы 00:00 — «без ограничений».
        if (start == 0 && end == CalendarMath.MinutesPerDay)
            CalendarSettings.SetDayBounds(0, CalendarMath.MinutesPerDay);
        else
            CalendarSettings.SetDayBounds(start, end);

        // Сервис мог развести границы (минимум час) — показать фактическое.
        _calendarBuilding = true;
        ShowCalendarDayBounds();
        _calendarBuilding = false;
    }
}
