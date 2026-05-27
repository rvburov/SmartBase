using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using MyPlanner.Core.Models;
using System.Windows.Input;

namespace MyPlanner.Views.Calendar;

public partial class YearView : UserControl
{
    public event EventHandler<DateTime>? MonthSelected;

    private static readonly string[] MonthNames =
        ["Январь", "Февраль", "Март", "Апрель", "Май", "Июнь",
         "Июль",   "Август",  "Сентябрь", "Октябрь", "Ноябрь", "Декабрь"];

    private static readonly string[] DayNames = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];

    public YearView() { InitializeComponent(); }

    public void Refresh(IEnumerable<Event> events, int year)
    {
        var eventCounts = new Dictionary<DateTime, int>();
        foreach (var e in events)
            foreach (var d in EachDay(e.StartDt.Date, e.EndDt.Date))
                eventCounts[d] = eventCounts.GetValueOrDefault(d) + 1;

        MonthGrid.Children.Clear();

        for (var m = 1; m <= 12; m++)
        {
            var month = m;
            MonthGrid.Children.Add(BuildMonthCard(year, month, eventCounts));
        }
    }

    private static readonly (Color Bg, Color Text)[] EventSteps =
    [
        (Color.FromArgb(40, 255, 204,   0), Color.FromArgb(255, 140, 100,   0)),
        (Color.FromArgb(40, 255, 149,   0), Color.FromArgb(255, 160,  70,   0)),
        (Color.FromArgb(40, 255,  90, 140), Color.FromArgb(255, 180,  30,  80)),
        (Color.FromArgb(40, 255,  59,  48), Color.FromArgb(255, 160,  20,  10)),
    ];


    private const double Dch       = 24;
    private const double TitleH    = 32;
    private const double CardHeight = TitleH + 7 * Dch; // 32 + 189 = 221

    private Control BuildMonthCard(int year, int month, Dictionary<DateTime, int> eventCounts)
    {
        var isTitleMonth = year == DateTime.Today.Year && month == DateTime.Today.Month;

        // Заголовок месяца: высота 30px (top=6, text=22, gap=2)
        var titleBtn = new TextBlock
        {
            Text                = MonthNames[month - 1],
            FontSize            = 13,
            FontWeight          = FontWeight.Bold,
            Height              = TitleH,
            Padding             = new Avalonia.Thickness(6, 6, 0, 2),
            Foreground          = isTitleMonth
                ? new SolidColorBrush(Color.Parse("#FF3B30"))
                : new SolidColorBrush(Color.Parse("#1C1C1E")),
        };

        // Один UniformGrid: 7 заголовков + 42 ячейки = 49 элементов, 7 строк × 7 колонок
        var dayGrid    = new UniformGrid { Columns = 7, Height = 7 * Dch };
        var firstDay   = new DateTime(year, month, 1);
        var startOffset = ((int)firstDay.DayOfWeek + 6) % 7;

        // Строка заголовков дней недели
        for (var di = 0; di < DayNames.Length; di++)
            dayGrid.Children.Add(new TextBlock
            {
                Text              = DayNames[di],
                FontSize          = 13,
                FontWeight        = FontWeight.Light,
                TextAlignment     = Avalonia.Media.TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground        = di >= 5
                    ? new SolidColorBrush(Color.Parse("#FF3B30"))
                    : new SolidColorBrush(Color.Parse("#8E8E93")),
            });

        // 42 ячейки с числами
        for (var i = 0; i < 42; i++)
        {
            var dc             = i % 7;
            var d              = firstDay.AddDays(i - startOffset);
            var isCurrentMonth = d.Month == month;
            var isToday        = d.Date == DateTime.Today;
            var isWeekend      = dc >= 5;
            var count          = isCurrentMonth ? eventCounts.GetValueOrDefault(d.Date) : 0;

            Control cell;

            if (isToday)
            {
                // Круг 22×22 по центру ячейки, как в PyQt (CS=22)
                cell = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Width               = 22,
                    Height              = 22,
                    CornerRadius        = new Avalonia.CornerRadius(11),
                    Background          = new SolidColorBrush(Color.Parse("#FF3B30")),
                    Child = new TextBlock
                    {
                        Text              = d.Day.ToString(),
                        FontSize          = 13,
                        FontWeight        = FontWeight.Light,
                        TextAlignment     = Avalonia.Media.TextAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground        = Avalonia.Media.Brushes.White,
                    },
                };
            }
            else if (count >= 1 && isCurrentMonth)
            {
                var step = count == 1 ? 0 : count <= 5 ? 1 : count <= 8 ? 2 : 3;
                cell = new Border
                {
                    Margin       = new Avalonia.Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(3),
                    Background   = new SolidColorBrush(EventSteps[step].Bg),
                    Child = new TextBlock
                    {
                        Text              = d.Day.ToString(),
                        FontSize          = 13,
                        FontWeight        = FontWeight.Light,
                        TextAlignment     = Avalonia.Media.TextAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground        = new SolidColorBrush(EventSteps[step].Text),
                    },
                };
            }
            else
            {
                var fg = !isCurrentMonth
                    ? Color.Parse("#E5E5EA")
                    : isWeekend
                        ? Color.Parse("#8E8E93")
                        : Color.Parse("#1C1C1E");

                cell = new TextBlock
                {
                    Text              = d.Day.ToString(),
                    FontSize          = 13,
                    FontWeight        = FontWeight.Light,
                    TextAlignment     = Avalonia.Media.TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground        = new SolidColorBrush(fg),
                };
            }

            dayGrid.Children.Add(cell);
        }

        var panel = new StackPanel
        {
            Width               = 220,
            Height              = CardHeight,
            Margin              = new Avalonia.Thickness(13),
            Spacing             = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top,
            Background          = Brushes.Transparent,
        };
        panel.Children.Add(titleBtn);
        panel.Children.Add(dayGrid);

        panel.DoubleTapped += (_, _) => MonthSelected?.Invoke(this, new DateTime(year, month, 1));

        return panel;
    }

    private static IEnumerable<DateTime> EachDay(DateTime from, DateTime to)
    {
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            yield return d;
    }
}
