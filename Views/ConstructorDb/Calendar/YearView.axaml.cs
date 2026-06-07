using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using MyPlanner.Core.Models;
using System.Windows.Input;

namespace MyPlanner.Views.ConstructorDb.Calendar;

public partial class YearView : UserControl
{
    public event EventHandler<DateTime>? MonthSelected;

    private static readonly string[] MonthNames =
        ["Январь", "Февраль", "Март", "Апрель", "Май", "Июнь",
         "Июль",   "Август",  "Сентябрь", "Октябрь", "Ноябрь", "Декабрь"];

    private static readonly string[] DayNames = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];

    public YearView() { InitializeComponent(); }

    // События по дате — для попапа со списком при клике на число
    private readonly Dictionary<DateTime, List<Event>> _eventsByDay = new();

    public void Refresh(IEnumerable<Event> events, int year)
    {
        _eventsByDay.Clear();
        foreach (var e in events)
            foreach (var d in EachDay(e.StartDt.Date, e.EndDt.Date))
            {
                if (!_eventsByDay.TryGetValue(d, out var list))
                    _eventsByDay[d] = list = new List<Event>();
                list.Add(e);
            }

        var eventCounts = _eventsByDay.ToDictionary(kv => kv.Key, kv => kv.Value.Count);

        MonthGrid.Children.Clear();

        for (var m = 1; m <= 12; m++)
        {
            var month = m;
            MonthGrid.Children.Add(BuildMonthCard(year, month, eventCounts));
        }
    }

    // Открывает попап со списком событий дня, привязанный к ячейке числа
    private void ShowDayEvents(Control anchor, DateTime day)
    {
        // Переоткрываем, чтобы попап переместился к новому числу
        // (PlacementTarget применяется при открытии).
        if (DayPopup.IsOpen) DayPopup.IsOpen = false;

        var events = _eventsByDay.GetValueOrDefault(day) ?? new List<Event>();

        DayPopupTitle.Text = day.ToString("d MMMM yyyy");
        DayPopupList.Children.Clear();

        if (events.Count == 0)
        {
            DayPopupList.Children.Add(new TextBlock
            {
                Text       = "Нет записей",
                FontSize   = 13,
                Foreground = new SolidColorBrush(Color.Parse("#8E8E93")),
            });
        }
        else
        {
            foreach (var e in events.OrderBy(x => x.AllDay ? TimeSpan.Zero : x.StartDt.TimeOfDay))
            {
                var time = e.AllDay ? "весь день" : e.StartDt.ToString("HH:mm");
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing     = 8,
                };
                row.Children.Add(new Border
                {
                    Width             = 4,
                    CornerRadius      = new Avalonia.CornerRadius(2),
                    Background         = new SolidColorBrush(Color.Parse(EventColors.Display(e.Color))),
                    VerticalAlignment = VerticalAlignment.Stretch,
                });
                row.Children.Add(new StackPanel
                {
                    Spacing  = 1,
                    Children =
                    {
                        new TextBlock
                        {
                            Text       = e.Title,
                            FontSize   = 13,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = new SolidColorBrush(Color.Parse("#1C1C1E")),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text       = time,
                            FontSize   = 12,
                            Foreground = new SolidColorBrush(Color.Parse("#8E8E93")),
                        },
                    },
                });
                DayPopupList.Children.Add(row);
            }
        }

        DayPopup.PlacementTarget = anchor;
        DayPopup.IsOpen          = true;
    }

    private static readonly (Color Bg, Color Text)[] EventSteps =
    [
        (Color.FromArgb(40, 255, 204,   0), Color.FromArgb(255, 140, 100,   0)),
        (Color.FromArgb(40, 255, 149,   0), Color.FromArgb(255, 160,  70,   0)),
        (Color.FromArgb(40, 255,  90, 140), Color.FromArgb(255, 180,  30,  80)),
        (Color.FromArgb(40, 255,  59,  48), Color.FromArgb(255, 160,  20,  10)),
    ];


    private const double Dch        = 24;
    private const double TitleH     = 32;
    private const double CardHeight = TitleH + 7 * Dch; // 32 + 168 = 200
    private const int    CellW      = 31;               // 220/7=31.43 → floor, чтобы колонки были целыми
    private const int    DayGridW   = 7 * CellW;        // 217

    private Control BuildMonthCard(int year, int month, Dictionary<DateTime, int> eventCounts)
    {
        var isTitleMonth = year == DateTime.Today.Year && month == DateTime.Today.Month;

        // Заголовок месяца: высота 30px (top=6, text=22, gap=2)
        var titleBtn = new TextBlock
        {
            Text                = MonthNames[month - 1],
            FontSize            = 13,
            FontWeight          = FontWeight.SemiBold,
            Height              = TitleH,
            Padding             = new Avalonia.Thickness(6, 6, 0, 2),
            Foreground          = isTitleMonth
                ? new SolidColorBrush(Color.Parse("#FF3B30"))
                : new SolidColorBrush(Color.Parse("#1C1C1E")),
        };

        // Один UniformGrid: 7 заголовков + 42 ячейки = 49 элементов, 7 строк × 7 колонок
        var dayGrid    = new UniformGrid { Columns = 7, Width = DayGridW, Height = 7 * Dch };
        var firstDay   = new DateTime(year, month, 1);
        var startOffset = ((int)firstDay.DayOfWeek + 6) % 7;

        // Строка заголовков дней недели
        for (var di = 0; di < DayNames.Length; di++)
            dayGrid.Children.Add(new TextBlock
            {
                Text              = DayNames[di],
                FontSize          = 13,
                FontWeight        = FontWeight.Normal,
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

            // Число всегда в одной позиции (центр ячейки 24×24) — выделение его не двигает.
            // Дни с событиями = цветной треугольник одинакового размера с числом по центру.
            var label = new TextBlock
            {
                Text                = d.Day.ToString(),
                FontSize            = 13,
                FontWeight          = FontWeight.Normal,
                TextAlignment       = Avalonia.Media.TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };

            Control cell;

            if (isToday && isCurrentMonth)
            {
                label.Foreground = Avalonia.Media.Brushes.White;
                cell = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Width               = 22,
                    Height              = 22,
                    CornerRadius        = new Avalonia.CornerRadius(11),
                    Background          = new SolidColorBrush(Color.Parse("#FF3B30")),
                    Child               = label,
                };
            }
            else if (count >= 1 && isCurrentMonth)
            {
                var step = count == 1 ? 0 : count <= 5 ? 1 : count <= 8 ? 2 : 3;
                label.Foreground = new SolidColorBrush(EventSteps[step].Text);

                // Прямоугольник почти на всю ширину ячейки (зазор 1px по бокам),
                // высота 22 по центру. Ширина одинакова у всех ячеек → число по центру,
                // блоки стоят плотно друг к другу.
                cell = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Height              = 22,
                    Margin              = new Avalonia.Thickness(1, 0),
                    CornerRadius        = new Avalonia.CornerRadius(3),
                    Background           = new SolidColorBrush(EventSteps[step].Bg),
                    Child                = label,
                };
            }
            else
            {
                label.Foreground = new SolidColorBrush(!isCurrentMonth
                    ? Color.Parse("#E5E5EA")
                    : isWeekend
                        ? Color.Parse("#8E8E93")
                        : Color.Parse("#1C1C1E"));
                cell = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Width               = 22,
                    Height              = 22,
                    Background          = Brushes.Transparent,
                    Child               = label,
                };
            }

            // Клик по числу текущего месяца → попап со списком событий дня,
            // привязанный к этой ячейке. Оборачиваем в прозрачный Border на всю
            // ячейку, чтобы кликабельной была вся площадь, а не только блок 22×22.
            if (isCurrentMonth)
            {
                var dayDate = d.Date;
                var hit = new Border
                {
                    Background = Brushes.Transparent,
                    Cursor     = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Child      = cell,
                };
                // Один клик — попап со списком событий дня
                hit.Tapped += (_, _) => ShowDayEvents(hit, dayDate);
                dayGrid.Children.Add(hit);
            }
            else
            {
                dayGrid.Children.Add(cell);
            }
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
