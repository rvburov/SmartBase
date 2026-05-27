using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MyPlanner.Core.Models;

namespace MyPlanner.Views.Calendar;

public partial class DayView : UserControl
{
    public event EventHandler<Event>? EventClicked;

    public DayView()
    {
        InitializeComponent();
        HourLabels.ItemsSource = Enumerable.Range(1, 23).Select(h => $"{h:00}:00").ToList();
    }

    public void Refresh(IEnumerable<Event> events, DateOnly date)
    {
        var allEvents = events.ToList();
        AllDayBarCtrl.SetData(date, allEvents);
        EventCanvas.Children.Clear();

        var timed = allEvents
            .Where(e => !e.AllDay
                     && (e.EndDt - e.StartDt).TotalSeconds < 86400
                     && DateOnly.FromDateTime(e.StartDt) <= date
                     && date <= DateOnly.FromDateTime(e.EndDt))
            .ToList();

        foreach (var ev in timed)
        {
            var startMin = ev.StartDt.Hour * 60 + ev.StartDt.Minute;
            var endMin   = ev.EndDt.Hour   * 60 + ev.EndDt.Minute;
            var evH      = Math.Max(30, endMin - startMin);
            var color    = Color.Parse(ev.Color);

            var btn = new Button
            {
                Background   = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)),
                BorderBrush  = new SolidColorBrush(color),
                BorderThickness = new Thickness(2, 0, 0, 0),
                CornerRadius = new CornerRadius(4),
                Padding      = new Thickness(6, 2),
                Height       = evH,
                Content      = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text         = ev.Title,
                            FontSize     = 12,
                            FontWeight   = FontWeight.SemiBold,
                            Foreground   = new SolidColorBrush(color),
                            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                        },
                        new TextBlock
                        {
                            Text       = $"{ev.StartDt:HH:mm} – {ev.EndDt:HH:mm}",
                            FontSize   = 11,
                            Foreground = new SolidColorBrush(color),
                        },
                    },
                },
            };
            btn.Click += (_, _) => EventClicked?.Invoke(this, ev);

            Canvas.SetTop(btn, startMin);
            Canvas.SetLeft(btn, 4);
            EventCanvas.Children.Add(btn);
        }

        if (date == DateOnly.FromDateTime(DateTime.Today))
            Dispatcher.UIThread.Post(
                () => TimeScroll.Offset = new Vector(0, Math.Max(0, DateTime.Now.Hour * 60 - 120)),
                DispatcherPriority.Loaded);
    }
}
