using CommunityToolkit.Mvvm.ComponentModel;
using MyPlanner.Core.Filters;
using MyPlanner.Database.Repositories;

namespace MyPlanner.ViewModels.Calendar;

public partial class WeekViewModel : CalendarViewModelBase
{
    [ObservableProperty] private DateTime _currentDate = DateTime.Today;

    public WeekViewModel(EventRepository events) : base(events) => Refresh();

    public DateTime WeekStart => CurrentDate.AddDays(-(int)CurrentDate.DayOfWeek == 0 ? 6 : (int)CurrentDate.DayOfWeek - 1);

    public override string HeaderText()
    {
        var s = WeekStart;
        var e = s.AddDays(6);
        return s.Month == e.Month
            ? $"{s:d MMMM} – {e:d MMMM yyyy}"
            : $"{s:d MMM} – {e:d MMM yyyy}";
    }

    public override void GoToday() { CurrentDate = DateTime.Today; Refresh(); }
    public override void GoPrev()  { CurrentDate = CurrentDate.AddDays(-7); Refresh(); }
    public override void GoNext()  { CurrentDate = CurrentDate.AddDays(7); Refresh(); }

    public override void Refresh()
    {
        var start = WeekStart;
        var end   = start.AddDays(7);
        var raw      = Events.GetByDateRange(start, end, ActiveIds);
        var filtered = EventFilter.BySearch(raw, SearchQuery).ToList();
        DisplayedEvents = new(filtered);
    }
}
