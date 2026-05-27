using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.ViewModels.Calendar;

public abstract partial class CalendarViewModelBase : ViewModelBase
{
    protected readonly EventRepository Events;

    [ObservableProperty] protected ObservableCollection<Event> _displayedEvents = [];
    [ObservableProperty] protected string _searchQuery = "";
    protected HashSet<int>? ActiveIds;

    protected CalendarViewModelBase(EventRepository events)
    {
        Events = events;
    }

    public abstract string HeaderText();
    public abstract void GoToday();
    public abstract void GoPrev();
    public abstract void GoNext();
    public abstract void Refresh();

    public void SetActiveIds(HashSet<int>? ids) { ActiveIds = ids; Refresh(); }
    public void SetSearch(string query)         { SearchQuery = query; Refresh(); }
}
