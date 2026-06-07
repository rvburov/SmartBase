using Avalonia.Controls;
using MyPlanner.Controls.Dialogs;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Views.ConstructorDb.Calendar;

public partial class CalendarControllerView : UserControl
{
    public event EventHandler? CollectionsMayHaveChanged;

    private ConstructorRepository _records = null!;
    private CategoryRepository _categories = null!;

    private DateTime _currentDate = DateTime.Today;
    private int _currentViewIndex = 4;   // 4 = Месяц
    private HashSet<int>? _activeIds;
    private string _searchQuery = "";

    private static readonly UserControl[] ViewOrder = [];  // заполняется в конструкторе

    public CalendarControllerView() { InitializeComponent(); }

    public CalendarControllerView(
        ConstructorRepository records,
        CategoryRepository categories)
    {
        InitializeComponent();
        Initialize(records, categories);
    }

    public void Initialize(
        ConstructorRepository records,
        CategoryRepository categories)
    {
        _records    = records;
        _categories = categories;

        NavbarControl.TodayClicked  += (_, _) =>
        {
            if (_currentViewIndex == 1) { JournalViewCtrl.GoToday(); return; }
            _currentDate = DateTime.Today; RefreshCurrent(scrollToNow: true);
        };
        NavbarControl.PrevClicked   += (_, _) =>
        {
            if (_currentViewIndex == 1) { JournalViewCtrl.GoPrev(); return; }
            NavigatePrev(); RefreshCurrent();
        };
        NavbarControl.NextClicked   += (_, _) =>
        {
            if (_currentViewIndex == 1) { JournalViewCtrl.GoNext(); return; }
            NavigateNext(); RefreshCurrent();
        };
        NavbarControl.AddClicked    += (_, _) => _ = OpenAddDialog(_currentDate);
        NavbarControl.ViewChanged   += (_, idx) => SwitchView(idx);
        NavbarControl.SearchChanged += (_, q) => { _searchQuery = q; RefreshCurrent(); };

        MonthViewCtrl.DaySelected   += (_, d)  => { _currentDate = d; SwitchView(2); };
        MonthViewCtrl.DayAddClicked += (_, d)  => _ = OpenAddDialog(d);
        YearViewCtrl.MonthSelected += (_, d)  => { _currentDate = d; SwitchView(4); };

        ListViewCtrl.RecordClicked      += (_, r) => _ = OpenEditDialog(r);
        ListViewCtrl.CreateRequested    += (_, d) => _ = OpenAddDialog(d);
        JournalViewCtrl.RecordClicked   += (_, r) => _ = OpenEditDialog(r);
        JournalViewCtrl.CreateRequested += (_, d) => _ = OpenAddDialog(d);
        MonthViewCtrl.RecordClicked += (_, r) => _ = OpenEditDialog(r);
        MonthViewCtrl.RecordMoved   += (_, r) => { _records.UpdateCalendarRecord(r); CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty); RefreshCurrent(); };
        DayViewCtrl.RecordClicked              += (_, r) => _ = OpenEditDialog(r);
        DayViewCtrl.RecordMoved                += (_, r) => { _records.UpdateCalendarRecord(r); CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty); RefreshCurrent(); };
        DayViewCtrl.AllDayCreationRequested    += (_, d)  => _ = OpenAddAllDayDialog(d);
        DayViewCtrl.TimeSlotCreationRequested  += (_, dt) => _ = OpenAddDialog(dt.Date, dt.TimeOfDay);
        WeekViewCtrl.RecordClicked             += (_, r) => _ = OpenEditDialog(r);
        WeekViewCtrl.RecordMoved               += (_, r) => { _records.UpdateCalendarRecord(r); CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty); RefreshCurrent(); };
        WeekViewCtrl.AllDayCreationRequested   += (_, d)  => _ = OpenAddAllDayDialog(d);
        WeekViewCtrl.TimeSlotCreationRequested += (_, dt) => _ = OpenAddDialog(dt.Date, dt.TimeOfDay);

        SwitchView(4);
        SeedIfEmpty();
    }

    public void SetActiveIds(HashSet<int> ids)
    {
        _activeIds = ids;
        RefreshCurrent();
    }

    private void SwitchView(int idx)
    {
        _currentViewIndex = idx;
        ListViewCtrl.IsVisible    = idx == 0;
        JournalViewCtrl.IsVisible = idx == 1;
        DayViewCtrl.IsVisible     = idx == 2;
        WeekViewCtrl.IsVisible    = idx == 3;
        MonthViewCtrl.IsVisible   = idx == 4;
        YearViewCtrl.IsVisible    = idx == 5;

        NavbarControl.SetView(idx);
        RefreshCurrent(scrollToNow: true);
    }

    private void NavigatePrev()
    {
        _currentDate = _currentViewIndex switch
        {
            0 => _currentDate.AddDays(-1),
            2 => _currentDate.AddDays(-1),
            3 => _currentDate.AddDays(-7),
            4 => _currentDate.AddMonths(-1),
            5 => _currentDate.AddYears(-1),
            _ => _currentDate.AddDays(-1),
        };
    }

    private void NavigateNext()
    {
        _currentDate = _currentViewIndex switch
        {
            0 => _currentDate.AddDays(1),
            2 => _currentDate.AddDays(1),
            3 => _currentDate.AddDays(7),
            4 => _currentDate.AddMonths(1),
            5 => _currentDate.AddYears(1),
            _ => _currentDate.AddDays(1),
        };
    }

    private void RefreshCurrent(bool scrollToNow = false)
    {
        var (startDt, endDt) = GetDateRange();
        var records = _records.GetCalendarRecordsByRange(startDt, endDt, _activeIds);
        if (!string.IsNullOrEmpty(_searchQuery))
            records = records.Where(r => r.Title.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)).ToList();

        UpdateTitle();

        switch (_currentViewIndex)
        {
            case 0: ListViewCtrl.Refresh(records, _currentDate); break;
            case 1: JournalViewCtrl.Refresh(records, _currentDate); break;
            case 2: DayViewCtrl.Refresh(records, DateOnly.FromDateTime(_currentDate)); break;
            case 3: WeekViewCtrl.Refresh(records, _currentDate, scrollToNow); break;
            case 4: MonthViewCtrl.Refresh(records, _currentDate); break;
            case 5: YearViewCtrl.Refresh(records, _currentDate.Year); break;
        }
    }

    private (DateTime start, DateTime end) GetDateRange() => _currentViewIndex switch
    {
        2 => (_currentDate.Date, _currentDate.Date.AddDays(1)),
        3 => (StartOfWeek(_currentDate), StartOfWeek(_currentDate).AddDays(7)),
        4 => (StartOfWeek(new DateTime(_currentDate.Year, _currentDate.Month, 1)),
              StartOfWeek(new DateTime(_currentDate.Year, _currentDate.Month, 1)).AddDays(42)),
        5 => (new DateTime(_currentDate.Year, 1, 1), new DateTime(_currentDate.Year + 1, 1, 1)),
        0 => (_currentDate.Date, _currentDate.Date.AddDays(1)),
        _ => (DateTime.Today.AddDays(-365), DateTime.Today.AddDays(365)),
    };

    private static DateTime StartOfWeek(DateTime d)
    {
        var diff = (int)d.DayOfWeek - (int)DayOfWeek.Monday;
        if (diff < 0) diff += 7;
        return d.AddDays(-diff).Date;
    }

    private static readonly Avalonia.Media.IBrush BrushRed =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF3B30"));
    private static readonly Avalonia.Media.IBrush BrushSecondary =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8E8E93"));
    private static readonly Avalonia.Media.IBrush BrushPrimary =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1C1C1E"));

    private void UpdateTitle()
    {
        var ru    = new System.Globalization.CultureInfo("ru-RU");
        var today = DateTime.Today;

        if (_currentViewIndex == 0)
        {
            var (text, brush) = _currentDate.Date switch
            {
                var d when d == today => (_currentDate.ToString("dddd, d MMMM yyyy", ru), BrushRed),
                var d when d < today  => (_currentDate.ToString("dddd, d MMMM yyyy", ru), BrushSecondary),
                _                     => (_currentDate.ToString("dddd, d MMMM yyyy", ru), BrushPrimary),
            };
            NavbarControl.SetTitle(text, brush);
            return;
        }

        NavbarControl.SetTitle(_currentViewIndex switch
        {
            2 => _currentDate.ToString("dddd, d MMMM yyyy", ru),
            3 => $"{StartOfWeek(_currentDate):d MMM} – {StartOfWeek(_currentDate).AddDays(6):d MMM yyyy}",
            4 => _currentDate.ToString("MMMM yyyy", ru),
            5 => _currentDate.Year.ToString(),
            _ => "Все записи",
        });
    }

    private async Task OpenAddAllDayDialog(DateOnly date)
    {
        if (VisualRoot is not Avalonia.Controls.Window win) return;
        var dlg = new RecordDialog(_records,
                                   presetDate: date.ToDateTime(TimeOnly.MinValue),
                                   presetAllDay: true);
        var result = await dlg.ShowDialog<RecordDialogResult?>(win);
        if (result?.Record is not null)
        {
            _records.AddCalendarRecord(result.Record);
            CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
            RefreshCurrent();
        }
    }

    private async Task OpenAddDialog(DateTime presetDate, TimeSpan? presetTime = null)
    {
        if (VisualRoot is not Avalonia.Controls.Window win) return;
        var dlg = new RecordDialog(_records, presetDate: presetDate, presetTime: presetTime);
        var result = await dlg.ShowDialog<RecordDialogResult?>(win);
        if (result?.Record is not null)
        {
            _records.AddCalendarRecord(result.Record);
            CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
            RefreshCurrent();
        }
    }

    private async Task OpenEditDialog(CalendarRecord r)
    {
        if (VisualRoot is not Avalonia.Controls.Window win) return;
        var dlg = new RecordDialog(_records, editRecord: r);
        var result = await dlg.ShowDialog<RecordDialogResult?>(win);
        if (result is null) return;
        if (result.Deleted) _records.DeleteCalendarRecord(r.Id!.Value);
        else if (result.Record is not null)
        {
            _records.UpdateCalendarRecord(result.Record);
            CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
        }
        RefreshCurrent();
    }

    private void SeedIfEmpty()
    {
        if (_records.CountCalendarRecords() > 0) return;

        var today = DateTime.Today;
        _records.AddCalendarRecord(new CalendarRecord { Title = "Стэндап", StartDt = today.AddHours(10), EndDt = today.AddHours(10.5), Color = "#007AFF" });
        _records.AddCalendarRecord(new CalendarRecord { Title = "Обед", StartDt = today.AddHours(13), EndDt = today.AddHours(14), Color = "#34C759" });
        _records.AddCalendarRecord(new CalendarRecord { Title = "Ревью", StartDt = today.AddDays(1).AddHours(15), EndDt = today.AddDays(1).AddHours(16), Color = "#FF9500" });
    }
}
