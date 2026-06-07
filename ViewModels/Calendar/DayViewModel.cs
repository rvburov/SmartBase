using CommunityToolkit.Mvvm.ComponentModel;
using MyPlanner.Database.Repositories;

namespace MyPlanner.ViewModels.Calendar;

public partial class DayViewModel : CalendarViewModelBase
{
    [ObservableProperty] private DateTime _currentDate = DateTime.Today;

    public DayViewModel(ConstructorRepository records) : base(records) => Refresh();

    public override string HeaderText() =>
        CurrentDate.ToString("dddd, d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    public override void GoToday() { CurrentDate = DateTime.Today; Refresh(); }
    public override void GoPrev()  { CurrentDate = CurrentDate.AddDays(-1); Refresh(); }
    public override void GoNext()  { CurrentDate = CurrentDate.AddDays(1); Refresh(); }

    public override void Refresh()
    {
        var start = CurrentDate.Date;
        var end   = start.AddDays(1);
        var raw      = Records.GetCalendarRecordsByRange(start, end, ActiveIds);
        DisplayedRecords = new(FilterBySearch(raw));
    }
}
