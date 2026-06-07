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

    private void SeedIfEmpty()
    {
        if (_records.CountCalendarRecords() > 0) return;

        var today = DateTime.Today;

        var seeds = new[]
        {
            Make("Планёрка отдела",                         today.AddHours(9),  today.AddHours(10)),
            Make("Совещание по разработке документации",    today.AddHours(11), today.AddHours(12.5)),
            Make("Встреча с конструкторским отделом",       today.AddDays(1).AddHours(14), today.AddDays(1).AddHours(15)),
            Make("Проверка изделий на соответствие",        today.AddDays(3).AddHours(10), today.AddDays(3).AddHours(12)),
            Make("Подготовка сканов технической документации", today.AddDays(5).AddHours(9), today.AddDays(5).AddHours(11)),
            Make("День рождения Петров Петрович",           today.AddDays(7), today.AddDays(7).AddHours(23.98), allDay: true),
        };

        foreach (var r in seeds)
            _records.AddCalendarRecord(r);

        foreach (var v in AllViews()) v.Refresh();
    }

    private static CalendarRecord Make(string title, DateTime start, DateTime end, bool allDay = false) =>
        new() { Title = title, StartDt = start, EndDt = end, AllDay = allDay };
}
