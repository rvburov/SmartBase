using CommunityToolkit.Mvvm.ComponentModel;
using MyPlanner.Core.Filters;
using MyPlanner.Database.Repositories;

namespace MyPlanner.ViewModels.Calendar;

public partial class DayViewModel : CalendarViewModelBase
{
    [ObservableProperty] private DateTime _currentDate = DateTime.Today;

    public DayViewModel(EventRepository events) : base(events) => Refresh();

    public override string HeaderText() =>
        CurrentDate.ToString("dddd, d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    public override void GoToday() { CurrentDate = DateTime.Today; Refresh(); }
    public override void GoPrev()  { CurrentDate = CurrentDate.AddDays(-1); Refresh(); }
    public override void GoNext()  { CurrentDate = CurrentDate.AddDays(1); Refresh(); }

    public override void Refresh()
    {
        var start = CurrentDate.Date;
        var end   = start.AddDays(1);
        var raw      = Events.GetByDateRange(start, end, ActiveIds);
        var filtered = EventFilter.BySearch(raw, SearchQuery).ToList();
        DisplayedEvents = new(filtered);
    }
}
