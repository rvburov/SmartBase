using MyPlanner.Core.Filters;
using MyPlanner.Database.Repositories;

namespace MyPlanner.ViewModels.Calendar;

public class ListViewModel : CalendarViewModelBase
{
    public ListViewModel(EventRepository events) : base(events) => Refresh();

    public override string HeaderText() => "Расписание";
    public override void GoToday() => Refresh();
    public override void GoPrev()  => Refresh();
    public override void GoNext()  => Refresh();

    public override void Refresh()
    {
        var raw      = Events.GetAll(ActiveIds);
        var filtered = EventFilter.BySearch(raw, SearchQuery).ToList();
        DisplayedEvents = new(filtered);
    }
}
