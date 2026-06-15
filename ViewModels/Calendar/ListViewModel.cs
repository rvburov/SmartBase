using DataBase.Database.Repositories;

namespace DataBase.ViewModels.Calendar;

public class ListViewModel : CalendarViewModelBase
{
    public ListViewModel(ConstructorRepository records) : base(records) => Refresh();

    public override string HeaderText() => "Расписание";
    public override void GoToday() => Refresh();
    public override void GoPrev()  => Refresh();
    public override void GoNext()  => Refresh();

    public override void Refresh()
    {
        var raw = Records.GetAllCalendarRecords(ActiveIds);
        DisplayedRecords = new(FilterBySearch(raw));
    }
}
