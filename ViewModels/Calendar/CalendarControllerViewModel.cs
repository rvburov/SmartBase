using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataBase.Core.Models;
using DataBase.Database.Repositories;

namespace DataBase.ViewModels.Calendar;

/// <summary>
/// Аналог CalendarController — оркестратор модуля календарь.
/// Индексы вьюх: 0-Расписание, 1-Журнал, 2-День, 3-Неделя, 4-Месяц, 5-Год
/// </summary>
public partial class CalendarControllerViewModel : ViewModelBase
{
    private readonly ConstructorRepository _records;
    private readonly CategoryRepository    _categories;

    [ObservableProperty] private int      _currentViewIndex = 4; // Месяц по умолчанию
    [ObservableProperty] private string   _title            = "";
    [ObservableProperty] private string   _searchQuery      = "";
    [ObservableProperty] private HashSet<int>? _activeIds;

    public ListViewModel    ListVm    { get; }
    public JournalViewModel JournalVm { get; }
    public DayViewModel     DayVm     { get; }
    public WeekViewModel    WeekVm    { get; }
    public MonthViewModel   MonthVm   { get; }
    public YearViewModel    YearVm    { get; }

    public CalendarControllerViewModel(ConstructorRepository records, CategoryRepository categories)
    {
        _records    = records;
        _categories = categories;

        ListVm    = new ListViewModel(records);
        JournalVm = new JournalViewModel(records);
        DayVm     = new DayViewModel(records);
        WeekVm    = new WeekViewModel(records);
        MonthVm   = new MonthViewModel(records);
        YearVm    = new YearViewModel(records);

        UpdateTitle();
    }

    [RelayCommand] public void GoToday()   { CurrentView.GoToday(); UpdateTitle(); }
    [RelayCommand] public void GoPrev()    { CurrentView.GoPrev();  UpdateTitle(); }
    [RelayCommand] public void GoNext()    { CurrentView.GoNext();  UpdateTitle(); }
    [RelayCommand] public void SwitchView(int idx) { CurrentViewIndex = idx; UpdateTitle(); }

    public void SetActiveIds(HashSet<int>? ids)
    {
        ActiveIds = ids;
        foreach (var v in AllViews()) v.SetActiveIds(ids);
    }

    public void SetSearch(string query)
    {
        SearchQuery = query;
        foreach (var v in AllViews()) v.SetSearch(query);
    }

    public void RefreshCollections()
    {
        foreach (var v in AllViews()) v.Refresh();
    }

    public void OnRecordChanged()
    {
        foreach (var v in AllViews()) v.SetActiveIds(ActiveIds);
        UpdateTitle();
    }

    public CalendarViewModelBase CurrentView => CurrentViewIndex switch
    {
        0 => ListVm,
        1 => JournalVm,
        2 => DayVm,
        3 => WeekVm,
        4 => MonthVm,
        5 => YearVm,
        _ => MonthVm,
    };

    private IEnumerable<CalendarViewModelBase> AllViews() =>
        [ListVm, JournalVm, DayVm, WeekVm, MonthVm, YearVm];

    private void UpdateTitle() => Title = CurrentView.HeaderText();
}
