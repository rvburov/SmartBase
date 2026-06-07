using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.ViewModels.Calendar;

public abstract partial class CalendarViewModelBase : ViewModelBase
{
    protected readonly ConstructorRepository Records;

    [ObservableProperty] protected ObservableCollection<CalendarRecord> _displayedRecords = [];
    [ObservableProperty] protected string _searchQuery = "";
    protected HashSet<int>? ActiveIds;

    protected CalendarViewModelBase(ConstructorRepository records)
    {
        Records = records;
    }

    public abstract string HeaderText();
    public abstract void GoToday();
    public abstract void GoPrev();
    public abstract void GoNext();
    public abstract void Refresh();

    public void SetActiveIds(HashSet<int>? ids) { ActiveIds = ids; Refresh(); }
    public void SetSearch(string query)         { SearchQuery = query; Refresh(); }

    protected IEnumerable<CalendarRecord> FilterBySearch(IEnumerable<CalendarRecord> records)
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return records;
        var q = SearchQuery.ToLowerInvariant();
        return records.Where(r => r.Title.ToLowerInvariant().Contains(q));
    }
}
