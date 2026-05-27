using CommunityToolkit.Mvvm.ComponentModel;
using MyPlanner.Core.Filters;
using MyPlanner.Database.Repositories;

namespace MyPlanner.ViewModels.Calendar;

public partial class MonthViewModel : CalendarViewModelBase
{
    [ObservableProperty] private DateTime _currentDate = DateTime.Today;

    public MonthViewModel(EventRepository events) : base(events) => Refresh();

    public override string HeaderText() =>
        CurrentDate.ToString("MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    public override void GoToday() { CurrentDate = DateTime.Today; Refresh(); }
    public override void GoPrev()  { CurrentDate = CurrentDate.AddMonths(-1); Refresh(); }
    public override void GoNext()  { CurrentDate = CurrentDate.AddMonths(1); Refresh(); }

    public override void Refresh()
    {
        var start = new DateTime(CurrentDate.Year, CurrentDate.Month, 1);
        var end   = start.AddMonths(1);
        var raw      = Events.GetByDateRange(start, end, ActiveIds);
        var filtered = EventFilter.BySearch(raw, SearchQuery).ToList();
        DisplayedEvents = new(filtered);
    }
}
