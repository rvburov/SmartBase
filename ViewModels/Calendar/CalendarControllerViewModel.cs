using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.ViewModels.Calendar;

/// <summary>
/// Аналог CalendarController — оркестратор модуля календарь.
/// Индексы вьюх: 0-Расписание, 1-Журнал, 2-День, 3-Неделя, 4-Месяц, 5-Год
/// </summary>
public partial class CalendarControllerViewModel : ViewModelBase
{
    private readonly EventRepository    _events;
    private readonly CategoryRepository _categories;

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

    public CalendarControllerViewModel(EventRepository events, CategoryRepository categories)
    {
        _events     = events;
        _categories = categories;

        ListVm    = new ListViewModel(events);
        JournalVm = new JournalViewModel(events);
        DayVm     = new DayViewModel(events);
        WeekVm    = new WeekViewModel(events);
        MonthVm   = new MonthViewModel(events);
        YearVm    = new YearViewModel(events);

        UpdateTitle();
        SeedIfEmpty();
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

    public void OnEventChanged()
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

    private void SeedIfEmpty()
    {
        if (_events.Count() > 0) return;

        var today = DateTime.Today;
        var now   = DateTime.Now.AddSeconds(-DateTime.Now.Second);

        var seeds = new[]
        {
            MakeEvent("Планёрка отдела",                         today.AddHours(9),  today.AddHours(10)),
            MakeEvent("Совещание по разработке документации",    today.AddHours(11), today.AddHours(12.5)),
            MakeEvent("Встреча с конструкторским отделом",       today.AddDays(1).AddHours(14), today.AddDays(1).AddHours(15)),
            MakeEvent("Проверка изделий на соответствие",        today.AddDays(3).AddHours(10), today.AddDays(3).AddHours(12)),
            MakeEvent("Подготовка сканов технической документации", today.AddDays(5).AddHours(9), today.AddDays(5).AddHours(11)),
            MakeEvent("День рождения Петров Петрович",           today.AddDays(7), today.AddDays(7).AddHours(23.98), allDay: true),
        };

        foreach (var e in seeds)
            _events.Add(e);

        foreach (var v in AllViews()) v.Refresh();
    }

    private static Event MakeEvent(string title, DateTime start, DateTime end, bool allDay = false) =>
        new() { Title = title, StartDt = start, EndDt = end, AllDay = allDay };
}
