using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using MyPlanner.Core.Models;
using System.Windows.Input;

namespace MyPlanner.Views.Calendar;

public partial class MonthView : UserControl
{
    public event EventHandler<DateTime>? DaySelected;
    public event EventHandler<Event>?    EventClicked;

    public MonthView() { InitializeComponent(); }

    public void Refresh(IEnumerable<Event> events, DateTime currentDate)
    {
        var eventList  = events.ToList();
        var firstDay   = new DateTime(currentDate.Year, currentDate.Month, 1);
        var totalCells = 42;  // 6 строк × 7 дней

        // Смещение: неделя начинается с понедельника
        var startOffset = ((int)firstDay.DayOfWeek + 6) % 7;
        var cells = new List<DayCellModel>();

        for (var i = 0; i < totalCells; i++)
        {
            var d = firstDay.AddDays(i - startOffset);
            var dayEvents = eventList
                .Where(e => DateOnly.FromDateTime(e.StartDt) <= DateOnly.FromDateTime(d)
                         && DateOnly.FromDateTime(d) <= DateOnly.FromDateTime(e.EndDt))
                .ToList();

            cells.Add(new DayCellModel(d, currentDate, dayEvents,
                ev => EventClicked?.Invoke(this, ev),
                date => DaySelected?.Invoke(this, date)));
        }

        DayGrid.ItemsSource = cells;
    }
}

public class DayCellModel
{
    private readonly DateTime _date;

    public string   DayNumber       { get; }
    public IBrush   Background      { get; }
    public IBrush   DayNumberBg     { get; }
    public IBrush   DayNumberFg     { get; }
    public string   DayNumberWeight { get; }
    public List<DayEventChip> Events { get; }
    public ICommand SelectCommand   { get; }

    public DayCellModel(DateTime date, DateTime currentMonth, IEnumerable<Event> events, Action<Event> onEventClick, Action<DateTime> onDaySelect)
    {
        _date = date;

        var isCurrentMonth = date.Month == currentMonth.Month;
        var isToday        = date.Date == DateTime.Today;
        var isWeekend      = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        DayNumber = date.Day.ToString();

        Background    = isCurrentMonth
            ? new SolidColorBrush(Color.Parse("#FFFFFF"))
            : new SolidColorBrush(Color.Parse("#F9F9F9"));

        if (isToday)
        {
            DayNumberBg     = new SolidColorBrush(Color.Parse("#007AFF"));
            DayNumberFg     = new SolidColorBrush(Colors.White);
            DayNumberWeight = "Bold";
        }
        else if (isWeekend)
        {
            DayNumberBg     = Brushes.Transparent;
            DayNumberFg     = new SolidColorBrush(Color.Parse("#FF3B30"));
            DayNumberWeight = "Normal";
        }
        else
        {
            DayNumberBg     = Brushes.Transparent;
            DayNumberFg     = new SolidColorBrush(isCurrentMonth ? Color.Parse("#1C1C1E") : Color.Parse("#8E8E93"));
            DayNumberWeight = "Normal";
        }

        Events        = events.Take(3).Select(e => new DayEventChip(e, () => onEventClick(e))).ToList();
        SelectCommand = new RelayCommand(() => onDaySelect(date));
    }
}

public class DayEventChip
{
    public string   Title      { get; }
    public IBrush   ChipBg     { get; }
    public IBrush   ChipFg     { get; }
    public ICommand ClickCommand { get; }

    public DayEventChip(Event ev, Action onClick)
    {
        Title        = ev.AllDay ? ev.Title : $"{ev.StartDt:HH:mm} {ev.Title}";
        ClickCommand = new RelayCommand(onClick);

        var color = Color.Parse(ev.Color);
        ChipBg = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B));
        ChipFg = new SolidColorBrush(color);
    }
}
