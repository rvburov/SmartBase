using CommunityToolkit.Mvvm.ComponentModel;
using MyPlanner.Database.Repositories;

namespace MyPlanner.ViewModels.Calendar;

public partial class YearViewModel : CalendarViewModelBase
{
    [ObservableProperty] private DateTime _currentDate = DateTime.Today;

    public YearViewModel(ConstructorRepository records) : base(records) => Refresh();

    public override string HeaderText() => CurrentDate.Year.ToString();

    public override void GoToday() { CurrentDate = DateTime.Today; Refresh(); }
    public override void GoPrev()  { CurrentDate = CurrentDate.AddYears(-1); Refresh(); }
    public override void GoNext()  { CurrentDate = CurrentDate.AddYears(1); Refresh(); }

    public override void Refresh()
    {
        var start = new DateTime(CurrentDate.Year, 1, 1);
        var end   = start.AddYears(1);
        var raw      = Records.GetCalendarRecordsByRange(start, end, ActiveIds);
        DisplayedRecords = new(FilterBySearch(raw));
    }
}
