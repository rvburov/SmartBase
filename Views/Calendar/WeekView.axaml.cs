using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MyPlanner.Core.Models;

namespace MyPlanner.Views.Calendar;

public partial class WeekView : UserControl
{
    public event EventHandler<Event>? EventClicked;

    private static readonly string[] DayNames = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];

    private Canvas?          _todayTimeOverlay;
    private DispatcherTimer? _timeLineTimer;

    public WeekView()
    {
        InitializeComponent();
        HourLabels.ItemsSource = Enumerable.Range(1, 23).Select(h => $"{h:00}:00").ToList();
        HourRows.ItemsSource   = Enumerable.Range(0, 24).ToList();

        _timeLineTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timeLineTimer.Tick += (_, _) => UpdateTimeLine();
        _timeLineTimer.Start();
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
        _todayTimeOverlay = null;
        var gridLineBrush = new SolidColorBrush(Color.Parse("#E8E8E8"));

        for (var col = 0; col < allDays.Count; col++)
        {
            var day     = allDays[col];
            var canvas  = new Canvas { Background = Brushes.Transparent };
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
                canvas.Children.Add(btn);
            }

            // Wrap canvas + time overlay in a Grid for today's column
            var dayGrid = new Grid();
            dayGrid.Children.Add(canvas);

            if (day.Date == DateTime.Today)
            {
                var overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
                dayGrid.Children.Add(overlay);
                _todayTimeOverlay = overlay;
            }

            WeekColumns.Children.Add(new Border
            {
                BorderBrush     = gridLineBrush,
                BorderThickness = new Thickness(col == 0 ? 0 : 1, 0, 0, 0),
                Child           = dayGrid,
            });
        }

        UpdateTimeLine();

        Dispatcher.UIThread.Post(
            () => TimeScroll.Offset = new Vector(0, Math.Max(0, DateTime.Now.Hour * 60 - 120)),
            DispatcherPriority.Loaded);
    }

    private void UpdateTimeLine()
    {
        if (_todayTimeOverlay == null) return;
        _todayTimeOverlay.Children.Clear();

        var y   = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
        var red = new SolidColorBrush(Color.Parse("#FF3B30"));

        // Горизонтальная линия (обрезается ClipToBounds)
        var line = new Border { Height = 2, Width = 10000, Background = red };
        Canvas.SetLeft(line, 0);
        Canvas.SetTop(line, y - 1);
        _todayTimeOverlay.Children.Add(line);
    }

    private void BuildHeader(List<DateTime> days)
    {
        WeekHeaderDays.Children.Clear();

        for (var i = 0; i < 7; i++)
        {
            var d         = days[i];
            var isToday   = d.Date == DateTime.Today;
            var isWeekend = i >= 5;

            var fg = isToday   ? new SolidColorBrush(Color.Parse("#007AFF"))
                   : isWeekend ? new SolidColorBrush(Color.Parse("#FF3B30"))
                               : new SolidColorBrush(Color.Parse("#8E8E93"));

            WeekHeaderDays.Children.Add(new TextBlock
            {
                Text                = $"{DayNames[i]} {d.Day}",
                FontSize            = 13,
                FontWeight          = isToday ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground          = fg,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment       = Avalonia.Media.TextAlignment.Center,
                Padding             = new Thickness(0, 6),
            });
        }
    }
}

// Скрывает сплошную линию для часа 0 (00:00), но оставляет пунктир 00:30.
public class HourLineBorderConverter : IValueConverter
{
    public static readonly HourLineBorderConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int h && h == 0 ? new Thickness(0) : new Thickness(0, 1, 0, 0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
