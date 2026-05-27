using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using MyPlanner.Core.Models;
using System.Windows.Input;

namespace MyPlanner.Controls.Components;

public partial class EventsSidebar : UserControl
{
    public event EventHandler<Event>? EventClicked;

    private static readonly string[] Months =
        ["января", "февраля", "марта", "апреля", "мая", "июня",
         "июля", "августа", "сентября", "октября", "ноября", "декабря"];

    public EventsSidebar()
    {
        InitializeComponent();
    }

    public void SetDate(DateOnly date, IEnumerable<Event> events)
    {
        DateLabel.Text = $"{date.Day} {Months[date.Month - 1]}";

        EventsList.ItemsSource = events
            .OrderBy(e => e.AllDay ? 0 : 1)
            .ThenBy(e => e.StartDt)
            .Select(e => new EventSidebarItem(e, () => EventClicked?.Invoke(this, e)))
            .ToList();
    }

    public void ClearDate()
    {
        DateLabel.Text         = "";
        EventsList.ItemsSource = null;
    }
}

public class EventSidebarItem
{
    public string   Title      { get; }
    public IBrush   ColorBrush { get; }
    public string   TimeText   { get; }
    public ICommand ClickCommand { get; }

    public EventSidebarItem(Event ev, Action onClick)
    {
        Title        = ev.Title;
        ColorBrush   = new SolidColorBrush(Color.Parse(ev.Color));
        TimeText     = ev.AllDay ? "весь день" : $"{ev.StartDt:HH:mm} – {ev.EndDt:HH:mm}";
        ClickCommand = new RelayCommand(onClick);
    }
}
