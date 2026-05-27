using Avalonia.Controls;
using MyPlanner.Controls.Dialogs;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Views.Calendar;

public partial class CalendarControllerView : UserControl
{
    public event EventHandler? CollectionsMayHaveChanged;

    private EventRepository _events = null!;
    private CategoryRepository _categories = null!;
    private ConstructorRepository _constructor = null!;

    private DateTime _currentDate = DateTime.Today;
    private int _currentViewIndex = 4;   // 4 = Месяц
    private HashSet<int>? _activeIds;
    private string _searchQuery = "";

    private static readonly UserControl[] ViewOrder = [];  // заполняется в конструкторе

    public CalendarControllerView() { InitializeComponent(); }

    public CalendarControllerView(
        EventRepository events,
        CategoryRepository categories,
        ConstructorRepository constructor)
    {
        InitializeComponent();
        Initialize(events, categories, constructor);
    }

    public void Initialize(
        EventRepository events,
        CategoryRepository categories,
        ConstructorRepository constructor)
    {
        _events      = events;
        _categories  = categories;
        _constructor = constructor;

        NavbarControl.TodayClicked  += (_, _) => { _currentDate = DateTime.Today; RefreshCurrent(); };
        NavbarControl.PrevClicked   += (_, _) => { NavigatePrev(); RefreshCurrent(); };
        NavbarControl.NextClicked   += (_, _) => { NavigateNext(); RefreshCurrent(); };
        NavbarControl.AddClicked    += (_, _) => _ = OpenAddDialog(_currentDate);
        NavbarControl.ViewChanged   += (_, idx) => SwitchView(idx);
        NavbarControl.SearchChanged += (_, q) => { _searchQuery = q; RefreshCurrent(); };

        MonthViewCtrl.DaySelected  += (_, d)  => { _currentDate = d; SwitchView(2); };
        YearViewCtrl.MonthSelected += (_, d)  => { _currentDate = d; SwitchView(3); };

        MonthViewCtrl.EventClicked += (_, ev) => _ = OpenEditDialog(ev);
        DayViewCtrl.EventClicked   += (_, ev) => _ = OpenEditDialog(ev);
        WeekViewCtrl.EventClicked  += (_, ev) => _ = OpenEditDialog(ev);

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
        RefreshCurrent();
    }

    private void NavigatePrev()
    {
        _currentDate = _currentViewIndex switch
        {
            2 => _currentDate.AddDays(-1),
            3 => _currentDate.AddDays(-7),
            4 => _currentDate.AddMonths(-1),
            5 => _currentDate.AddYears(-1),
            _ => _currentDate.AddDays(-30),
        };
    }

    private void NavigateNext()
    {
        _currentDate = _currentViewIndex switch
        {
            2 => _currentDate.AddDays(1),
            3 => _currentDate.AddDays(7),
            4 => _currentDate.AddMonths(1),
            5 => _currentDate.AddYears(1),
            _ => _currentDate.AddDays(30),
        };
    }

    private void RefreshCurrent()
    {
        var (startDt, endDt) = GetDateRange();
        var events = _events.GetByDateRange(startDt, endDt, _activeIds);
        if (!string.IsNullOrEmpty(_searchQuery))
            events = events.Where(e => e.Title.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)).ToList();

        UpdateTitle();

        switch (_currentViewIndex)
        {
            case 0: ListViewCtrl.Refresh(events, _currentDate); break;
            case 1: JournalViewCtrl.Refresh(events, _currentDate); break;
            case 2: DayViewCtrl.Refresh(events, DateOnly.FromDateTime(_currentDate)); break;
            case 3: WeekViewCtrl.Refresh(events, _currentDate); break;
            case 4: MonthViewCtrl.Refresh(events, _currentDate); break;
            case 5: YearViewCtrl.Refresh(events, _currentDate.Year); break;
        }
    }

    private (DateTime start, DateTime end) GetDateRange() => _currentViewIndex switch
    {
        2 => (_currentDate.Date, _currentDate.Date.AddDays(1)),
        3 => (StartOfWeek(_currentDate), StartOfWeek(_currentDate).AddDays(7)),
        4 => (new DateTime(_currentDate.Year, _currentDate.Month, 1),
              new DateTime(_currentDate.Year, _currentDate.Month, 1).AddMonths(1)),
        5 => (new DateTime(_currentDate.Year, 1, 1), new DateTime(_currentDate.Year + 1, 1, 1)),
        _ => (DateTime.Today.AddDays(-365), DateTime.Today.AddDays(365)),
    };

    private static DateTime StartOfWeek(DateTime d)
    {
        var diff = (int)d.DayOfWeek - (int)DayOfWeek.Monday;
        if (diff < 0) diff += 7;
        return d.AddDays(-diff).Date;
    }

    private void UpdateTitle() => NavbarControl.SetTitle(_currentViewIndex switch
    {
        2 => _currentDate.ToString("dddd, d MMMM yyyy", new System.Globalization.CultureInfo("ru-RU")),
        3 => $"{StartOfWeek(_currentDate):d MMM} – {StartOfWeek(_currentDate).AddDays(6):d MMM yyyy}",
        4 => _currentDate.ToString("MMMM yyyy", new System.Globalization.CultureInfo("ru-RU")),
        5 => _currentDate.Year.ToString(),
        _ => "Все события",
    });

    private async Task OpenAddDialog(DateTime presetDate)
    {
        if (VisualRoot is not Avalonia.Controls.Window win) return;
        var dlg = new EventDialog(_categories, _constructor, presetDate: presetDate);
        var result = await dlg.ShowDialog<EventDialogResult?>(win);
        if (result?.Event is not null)
        {
            _events.Add(result.Event);
            CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
            RefreshCurrent();
        }
    }

    private async Task OpenEditDialog(Event ev)
    {
        if (VisualRoot is not Avalonia.Controls.Window win) return;
        var dlg = new EventDialog(_categories, _constructor, editEvent: ev);
        var result = await dlg.ShowDialog<EventDialogResult?>(win);
        if (result is null) return;
        if (result.Deleted) _events.Delete(ev.Id!.Value);
        else if (result.Event is not null)
        {
            _events.Update(result.Event);
            CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
        }
        RefreshCurrent();
    }

    private void SeedIfEmpty()
    {
        if (_events.Count() > 0) return;

        var today = DateTime.Today;
        _events.Add(new Event { Title = "Стэндап", StartDt = today.AddHours(10), EndDt = today.AddHours(10.5), Color = "#007AFF" });
        _events.Add(new Event { Title = "Обед", StartDt = today.AddHours(13), EndDt = today.AddHours(14), Color = "#34C759" });
        _events.Add(new Event { Title = "Ревью", StartDt = today.AddDays(1).AddHours(15), EndDt = today.AddDays(1).AddHours(16), Color = "#FF9500" });
    }
}
