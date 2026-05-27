using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MyPlanner.Core.Models;

namespace MyPlanner.Views.Calendar;

public partial class WeekView : UserControl
{
    public event EventHandler<Event>? EventClicked;

    private static readonly string[] DayNames = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];

    public WeekView()
    {
        InitializeComponent();
        HourLabels.ItemsSource = Enumerable.Range(1, 23).Select(h => $"{h:00}:00").ToList();
    }

    public void Refresh(IEnumerable<Event> events, DateTime weekStart)
    {
        var monday   = weekStart.AddDays(-(((int)weekStart.DayOfWeek + 6) % 7));
        var allDays  = Enumerable.Range(0, 7).Select(i => monday.AddDays(i)).ToList();
        var allEvents = events.ToList();

        // All-day bar
        AllDayBarCtrl.SetData(allDays.Select(DateOnly.FromDateTime), allEvents);

        // Header with weekday names + dates
        BuildHeader(allDays);

        // Clear and rebuild week columns
        WeekColumns.Children.Clear();
        foreach (var day in allDays)
        {
            var col = new Canvas { Background = Brushes.Transparent };
            var dayDate = DateOnly.FromDateTime(day);
            var dayEvents = allEvents
                .Where(e => !e.AllDay && DateOnly.FromDateTime(e.StartDt) <= dayDate
                         && dayDate <= DateOnly.FromDateTime(e.EndDt))
                .ToList();

            foreach (var ev in dayEvents)
            {
                var startMin = ev.StartDt.Hour * 60 + ev.StartDt.Minute;
                var evH      = Math.Max(20, ev.EndDt.Hour * 60 + ev.EndDt.Minute - startMin);
                var color    = Color.Parse(ev.Color);

                var btn = new Button
                {
                    Background   = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)),
                    BorderBrush  = new SolidColorBrush(color),
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    CornerRadius = new CornerRadius(3),
                    Padding      = new Thickness(3, 1),
                    Height       = evH,
                    Content      = new TextBlock
                    {
                        Text         = ev.Title,
                        FontSize     = 11,
                        Foreground   = new SolidColorBrush(color),
                        TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                    },
                };
                btn.Click += (_, _) => EventClicked?.Invoke(this, ev);
                Canvas.SetTop(btn, startMin);
                Canvas.SetLeft(btn, 2);
                col.Children.Add(btn);
            }
            WeekColumns.Children.Add(col);
        }

        Dispatcher.UIThread.Post(
            () => TimeScroll.Offset = new Vector(0, Math.Max(0, DateTime.Now.Hour * 60 - 120)),
            DispatcherPriority.Loaded);
    }

    private void BuildHeader(List<DateTime> days)
    {
        WeekHeader.ColumnDefinitions.Clear();
        WeekHeader.Children.Clear();

        for (var i = 0; i < 7; i++)
        {
            WeekHeader.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var d       = days[i];
            var isToday = d.Date == DateTime.Today;

            var cell = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            cell.Children.Add(new TextBlock
            {
                Text         = DayNames[i],
                FontSize     = 11,
                Foreground   = isToday
                    ? new SolidColorBrush(Color.Parse("#007AFF"))
                    : new SolidColorBrush(Color.Parse("#8E8E93")),
                TextAlignment = Avalonia.Media.TextAlignment.Center,
            });
            cell.Children.Add(new TextBlock
            {
                Text         = d.Day.ToString(),
                FontSize     = 13,
                FontWeight   = isToday ? FontWeight.Bold : FontWeight.Normal,
                Foreground   = isToday
                    ? new SolidColorBrush(Color.Parse("#007AFF"))
                    : new SolidColorBrush(Color.Parse("#1C1C1E")),
                TextAlignment = Avalonia.Media.TextAlignment.Center,
            });

            Grid.SetColumn(cell, i);
            WeekHeader.Children.Add(cell);
        }
    }
}
