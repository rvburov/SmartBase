using CommunityToolkit.Mvvm.ComponentModel;
using MyPlanner.Database.Repositories;

namespace MyPlanner.ViewModels.Calendar;

public partial class JournalViewModel : CalendarViewModelBase
{
    [ObservableProperty] private DateTime _currentDate = DateTime.Today;

    public JournalViewModel(ConstructorRepository records) : base(records) => Refresh();

    public override string HeaderText() =>
        CurrentDate.ToString("MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    public override void GoToday() { CurrentDate = DateTime.Today; Refresh(); }
    public override void GoPrev()  { CurrentDate = CurrentDate.AddMonths(-1); Refresh(); }
    public override void GoNext()  { CurrentDate = CurrentDate.AddMonths(1); Refresh(); }

    public override void Refresh()
    {
        var start = CurrentDate.Date;
        var end   = start.AddMonths(3);
        var raw      = Records.GetCalendarRecordsByRange(start, end, ActiveIds);
        DisplayedRecords = new(FilterBySearch(raw));
    }
}
