using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using MyPlanner.Core.Models;
using System.Windows.Input;

namespace MyPlanner.Views.Calendar;

public partial class ListView : UserControl
{
    public event EventHandler<Event>? EventClicked;

    public ListView() { InitializeComponent(); }

    public void Refresh(IEnumerable<Event> events, DateTime currentDate)
    {
        EventsList.ItemsSource = events
            .OrderBy(e => e.StartDt)
            .Select(e => new EventListItem(e, () => EventClicked?.Invoke(this, e)))
            .ToList();
    }
}

public class EventListItem
{
    public string   Title         { get; }
    public string   DateTimeText  { get; }
    public string   Description   { get; }
    public bool     HasDescription { get; }
    public IBrush   ColorBrush    { get; }
    public ICommand ClickCommand  { get; }

    private static readonly string[] Months =
        ["янв", "фев", "мар", "апр", "май", "июн",
         "июл", "авг", "сен", "окт", "ноя", "дек"];

    public EventListItem(Event ev, Action onClick)
    {
        Title         = ev.Title;
        Description   = ev.Description;
        HasDescription = !string.IsNullOrWhiteSpace(ev.Description);
        ColorBrush    = new SolidColorBrush(Color.Parse(ev.Color));
        ClickCommand  = new RelayCommand(onClick);

        DateTimeText = ev.AllDay
            ? $"{ev.StartDt.Day} {Months[ev.StartDt.Month - 1]} {ev.StartDt.Year}"
            : $"{ev.StartDt.Day} {Months[ev.StartDt.Month - 1]} {ev.StartDt.Year}  {ev.StartDt:HH:mm} – {ev.EndDt:HH:mm}";
    }
}
