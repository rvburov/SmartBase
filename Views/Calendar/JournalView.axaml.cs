using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using MyPlanner.Core.Models;
using System.Windows.Input;

namespace MyPlanner.Views.Calendar;

public partial class JournalView : UserControl
{
    public event EventHandler<Event>? EventClicked;

    private static readonly string[] Months =
        ["января", "февраля", "марта", "апреля", "мая", "июня",
         "июля",   "августа", "сентября", "октября", "ноября", "декабря"];

    private static readonly string[] WeekDays =
        ["воскресенье", "понедельник", "вторник", "среда", "четверг", "пятница", "суббота"];

    public JournalView() { InitializeComponent(); }

    public void Refresh(IEnumerable<Event> events, DateTime currentDate)
    {
        var groups = events
            .OrderBy(e => e.StartDt)
            .GroupBy(e => e.StartDt.Date)
            .Select(g => new JournalGroup(
                date: g.Key,
                events: g.ToList(),
                months: Months,
                weekDays: WeekDays,
                onEventClick: ev => EventClicked?.Invoke(this, ev)))
            .ToList();

        GroupsList.ItemsSource = groups;
    }
}

public class JournalGroup
{
    public string            DateHeader { get; }
    public List<JournalItem> Events     { get; }

    public JournalGroup(DateTime date, IEnumerable<Event> events,
                        string[] months, string[] weekDays, Action<Event> onEventClick)
    {
        var isToday    = date.Date == DateTime.Today;
        var isTomorrow = date.Date == DateTime.Today.AddDays(1);

        DateHeader = isToday
            ? "Сегодня"
            : isTomorrow
                ? "Завтра"
                : $"{weekDays[(int)date.DayOfWeek]}, {date.Day} {months[date.Month - 1]}";

        Events = events.Select(e => new JournalItem(e, () => onEventClick(e))).ToList();
    }
}

public class JournalItem
{
    public string   Title    { get; }
    public string   TimeText { get; }
    public IBrush   ChipBg   { get; }
    public IBrush   TitleFg  { get; }
    public ICommand ClickCommand { get; }

    public JournalItem(Event ev, Action onClick)
    {
        Title        = ev.Title;
        TimeText     = ev.AllDay ? "весь день" : $"{ev.StartDt:HH:mm} – {ev.EndDt:HH:mm}";
        ClickCommand = new RelayCommand(onClick);

        var color = Color.Parse(ev.Color);
        ChipBg  = new SolidColorBrush(Color.FromArgb(20, color.R, color.G, color.B));
        TitleFg = new SolidColorBrush(color);
    }
}
