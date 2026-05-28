using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MyPlanner.Core.Models;

namespace MyPlanner.Views.Calendar;

public partial class MonthView : UserControl
{
    public event EventHandler<DateTime>? DaySelected;
    public event EventHandler<DateTime>? DayAddClicked;
    public event EventHandler<Event>?    EventClicked;
    public event EventHandler<Event>?    EventMoved;

    // Константы макета (соответствуют PyQt DayCell / MonthOverlay)
    private const int DateH   = 34;
    private const int BarH    = 18;
    private const int BarStep = 20;
    private const int MoreH   = 16;
    private const int BPad    = 4;

    private List<Event> _events      = [];
    private DateTime    _currentDate = DateTime.Today;
    private DateTime    _gridStart;

    // Drag state
    private Event?    _dragEv;
    private bool      _dragActive;
    private Point     _dragPress;
    private DateTime  _dragOrigStart;
    private DateTime  _dragOrigEnd;
    private DateTime  _dragSourceDay;
    private DateTime  _dragTargetDay;
    private IPointer? _dragPointer;
    private Grid?     _dragPreview;
    private DateTime? _shiftedDay;
    private Control?  _removedChip;
    private Action?   _deselectCurrent;
    private int       _pressClickCount;
    private bool      _skipNextDeselect;

    private readonly Dictionary<DateTime, (StackPanel Panel, TextBlock More, int OrigTopOff, int OrigHidden)> _dayUiMap    = new();
    private readonly Dictionary<int, List<(Border SelFrame, Border? LeftStripe)>>                             _spanFrames   = new();
    private readonly Dictionary<int, List<Border>>                                                             _spanBgBorders = new();
    private readonly Dictionary<DateTime, (Border Cell, IBrush BaseBg)>                                       _dayCellMap   = new();
    private readonly Dictionary<DateTime, (Action Show, Action Hide)>                                         _dayPlusMap   = new();
    private DateTime? _hoveredDay;
    private DateTime? _hoveredCircleDay;

    private EventHandler<PointerPressedEventArgs>? _windowClickHandler;

    public MonthView()
    {
        InitializeComponent();
        GridContainer.SizeChanged    += (_, _) => _BuildAll();
        GridContainer.PointerMoved   += (_, e) => { var pos = e.GetPosition(GridContainer); _UpdateCellHover(_DayAtPos(pos)); _UpdateCircleHover(pos); };
        GridContainer.PointerExited  += (_, _) => { _UpdateCellHover(null); _UpdateCircleHover(null); };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (e.Root is Avalonia.Controls.Window win)
        {
            _windowClickHandler = (_, _) =>
            {
                if (_skipNextDeselect) { _skipNextDeselect = false; return; }
                _Deselect();
            };
            win.AddHandler(PointerPressedEvent, _windowClickHandler);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (e.Root is Avalonia.Controls.Window win && _windowClickHandler is not null)
        {
            win.RemoveHandler(PointerPressedEvent, _windowClickHandler);
            _windowClickHandler = null;
        }
    }

    public void Refresh(IEnumerable<Event> events, DateTime currentDate)
    {
        _events      = events.ToList();
        _currentDate = currentDate;
        _BuildAll();
    }

    // ── алгоритм раскладки многодневных событий по строкам ────────────────────
    // Жадный алгоритм: для каждого события берём первую строку, где ec < sc.
    // Порт _compute_week_layout из PyQt.

    private readonly record struct LayoutEntry(int RowIdx, int Sc, int Ec);

    private Dictionary<int, LayoutEntry> _ComputeWeekLayout(DateTime weekStart)
    {
        var weekEnd  = weekStart.AddDays(6).Date;
        var spanning = _events
            .Where(e => (e.AllDay || e.EndDt.Date > e.StartDt.Date)
                     && e.StartDt.Date <= weekEnd
                     && e.EndDt.Date   >= weekStart.Date
                     && e.Id.HasValue)
            .OrderBy(e => e.StartDt.Date)
            .ThenByDescending(e => (e.EndDt - e.StartDt).TotalDays)
            .ToList();

        var rowEnds = new List<int>();
        var layout  = new Dictionary<int, LayoutEntry>();

        foreach (var ev in spanning)
        {
            int sc  = Math.Max(0, (int)(ev.StartDt.Date - weekStart.Date).TotalDays);
            int ec  = Math.Min(6, (int)(ev.EndDt.Date   - weekStart.Date).TotalDays);
            int row = rowEnds.Count;
            for (int r = 0; r < rowEnds.Count; r++)
            {
                if (sc > rowEnds[r]) { row = r; break; }
            }
            if (row == rowEnds.Count) rowEnds.Add(-1);
            rowEnds[row] = ec;
            layout[ev.Id!.Value] = new LayoutEntry(row, sc, ec);
        }

        return layout;
    }

    // ── единый построитель: ячейки + Canvas-оверлей ────────────────────────────

    private void _BuildAll()
    {
        _Deselect();
        DayGrid.Children.Clear();
        SpanCanvas.Children.Clear();
        _dayUiMap.Clear();
        _spanFrames.Clear();
        _spanBgBorders.Clear();
        _dayCellMap.Clear();
        _dayPlusMap.Clear();
        _hoveredDay       = null;
        _hoveredCircleDay = null;

        var W = GridContainer.Bounds.Width;
        var H = GridContainer.Bounds.Height;

        var firstDay    = new DateTime(_currentDate.Year, _currentDate.Month, 1);
        var startOffset = ((int)firstDay.DayOfWeek + 6) % 7;
        var gridStart   = firstDay.AddDays(-startOffset).Date;
        _gridStart      = gridStart;

        var cw = W > 0 ? W / 7.0 : 0;
        var ch = H > 0 ? H / 6.0 : 0;

        for (var wrow = 0; wrow < 6; wrow++)
        {
            var weekStart = gridStart.AddDays(wrow * 7);
            var weekEnd   = weekStart.AddDays(6);
            var layout    = _ComputeWeekLayout(weekStart);

            // ── многодневные бары для этой строки недели ──────────────────────
            var hiddenSpans = new int[7];
            var seen        = new HashSet<int>();

            foreach (var ev in _events)
            {
                if (!ev.Id.HasValue || !layout.ContainsKey(ev.Id.Value)) continue;
                if (!(ev.AllDay || ev.EndDt.Date > ev.StartDt.Date))     continue;
                if (ev.StartDt.Date > weekEnd || ev.EndDt.Date < weekStart) continue;
                if (!seen.Add(ev.Id.Value)) continue;

                var (rowIdx, sc, ec) = layout[ev.Id.Value];
                var leftCap  = ev.StartDt.Date >= weekStart;
                var rightCap = ev.EndDt.Date   <= weekEnd;
                var barY     = wrow * ch + DateH + rowIdx * BarStep;

                // Не влезает — учитываем в счётчике скрытых
                if (ch > 0 && barY + BarH > wrow * ch + ch - BPad - MoreH)
                {
                    for (var c2 = sc; c2 <= ec; c2++) hiddenSpans[c2]++;
                    continue;
                }

                if (W > 0)
                {
                    var insetL = leftCap  ? 3.0 : 0.0;
                    var insetR = rightCap ? 2.0 : 0.0;
                    var bx = sc * cw + insetL;
                    var bw = (ec + 1) * cw - insetR - bx;
                    _AddSpanBar(ev, bx, barY, bw, leftCap, rightCap);
                }
            }

            // ── ячейки дней ───────────────────────────────────────────────────
            for (var col = 0; col < 7; col++)
            {
                var d   = weekStart.AddDays(col);
                var isCurrentMonth = d.Month == _currentDate.Month;
                var isToday        = d.Date == DateTime.Today;
                var isWeekend      = col >= 5;

                // Максимальная занятая строка spanning-событий в этой ячейке
                var maxSpanRow = _events
                    .Where(e => (e.AllDay || e.EndDt.Date > e.StartDt.Date)
                             && e.StartDt.Date <= d.Date
                             && e.EndDt.Date   >= d.Date
                             && e.Id.HasValue && layout.ContainsKey(e.Id.Value))
                    .Select(e => layout[e.Id!.Value].RowIdx)
                    .DefaultIfEmpty(-1)
                    .Max();

                // Однодневные события
                var singles = _events
                    .Where(e => !e.AllDay
                             && e.EndDt.Date == e.StartDt.Date
                             && e.StartDt.Date == d.Date)
                    .OrderBy(e => e.StartDt)
                    .ToList();

                // Сколько однодневных влезает ниже spanning-строк
                // -1 компенсирует 1px границу ячейки, чтобы зазор chip↔spanbar = chip↔chip = 2px
                var topOff = DateH + (maxSpanRow + 1) * BarStep - 1;
                var maxVis = ch > 0
                    ? Math.Max(0, (int)((ch - BPad - MoreH - topOff) / BarStep))
                    : 5;

                var visCount = Math.Min(singles.Count, maxVis);
                var hidden   = singles.Count - visCount + hiddenSpans[col];

                DayGrid.Children.Add(
                    _MakeCell(d, isCurrentMonth, isToday, isWeekend,
                              singles.Take(visCount).ToList(), topOff, hidden));
            }
        }
    }

    // ── ячейка дня ────────────────────────────────────────────────────────────

    private Control _MakeCell(
        DateTime    date,
        bool        isCurrentMonth,
        bool        isToday,
        bool        isWeekend,
        List<Event> singles,
        int         topOff,
        int         hidden)
    {
        // Число дня
        var labelFg = isToday ? Brushes.White
            : isWeekend ? (IBrush)new SolidColorBrush(Color.Parse("#FF3B30"))
            : new SolidColorBrush(isCurrentMonth ? Color.Parse("#1C1C1E") : Color.Parse("#8E8E93"));

        var dayLabel = new TextBlock
        {
            Text                = date.Day.ToString(),
            FontSize            = 13,
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Center,
            Foreground          = labelFg,
            FontWeight          = isToday ? FontWeight.Bold : FontWeight.Normal,
        };

        // «+» текстом с LineHeight = FontSize — убирает скрытые отступы шрифта
        var plusIcon = new TextBlock
        {
            Text                = "+",
            FontSize            = 20,
            LineHeight          = 20,
            FontWeight          = FontWeight.SemiBold,
            Foreground          = new SolidColorBrush(Color.Parse("#636366")),
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            IsVisible           = false,
        };

        var dayContent = new Grid();
        dayContent.Children.Add(dayLabel);
        dayContent.Children.Add(plusIcon);

        IBrush normalCircleBg = isToday
            ? new SolidColorBrush(Color.Parse("#FF3B30"))
            : Brushes.Transparent;
        IBrush hoverCircleBg  = new SolidColorBrush(Color.Parse("#F2F2F7"));
        IBrush hoverBorderBrush = new SolidColorBrush(Color.Parse("#E8E8E8"));

        var dayCircle = new Border
        {
            Width               = 24,
            Height              = 24,
            CornerRadius        = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Thickness(0, 4, 4, 0),
            Background          = normalCircleBg,
            Cursor              = new Cursor(StandardCursorType.Hand),
            Child               = dayContent,
        };

        _dayPlusMap[date] = (
            () =>
            {
                dayLabel.IsVisible      = false;
                plusIcon.IsVisible      = true;
                dayCircle.Background    = hoverCircleBg;
                dayCircle.BorderBrush   = hoverBorderBrush;
                dayCircle.BorderThickness = new Thickness(1);
            },
            () =>
            {
                dayLabel.IsVisible      = true;
                plusIcon.IsVisible      = false;
                dayCircle.Background    = normalCircleBg;
                dayCircle.BorderBrush   = Brushes.Transparent;
                dayCircle.BorderThickness = new Thickness(0);
            }
        );

        // Однодневные события (StackPanel смещён вниз, чтобы не перекрывать spanning-бары)
        var eventsPanel = new StackPanel
        {
            Spacing             = 2,
            Margin              = new Thickness(2, topOff, 2, 0),
            VerticalAlignment   = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (var ev in singles)
            eventsPanel.Children.Add(_MakeChip(ev));

        // «+N ещё» — прижата к нижнему краю ячейки
        var moreLabel = new TextBlock
        {
            Text                = $"+{hidden} ещё",
            IsVisible           = hidden > 0,
            FontSize            = 13,
            Foreground          = new SolidColorBrush(Color.Parse("#8E8E93")),
            Margin              = new Thickness(6, 0, 0, 4),
            VerticalAlignment   = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        _dayUiMap[date] = (eventsPanel, moreLabel, topOff, hidden);

        var inner = new Grid();
        inner.Children.Add(dayCircle);
        inner.Children.Add(eventsPanel);
        inner.Children.Add(moreLabel);

        IBrush baseBg = new SolidColorBrush(
            isCurrentMonth ? Color.Parse("#FFFFFF") : Color.Parse("#F9F9F9"));

        var cellBorder = new Border
        {
            BorderBrush     = new SolidColorBrush(Color.Parse("#E8E8E8")),
            BorderThickness = new Thickness(1, 1, 0, 0),
            Background      = baseBg,
            ClipToBounds    = true,
            Child           = inner,
        };

        if (isCurrentMonth)
            _dayCellMap[date] = (cellBorder, baseBg);

        // Клик по «+» → создать событие; двойной клик по числу → перейти в день
        dayCircle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
            if (_hoveredCircleDay == date)
            {
                DayAddClicked?.Invoke(this, date);
                e.Handled = true;
            }
            else if (e.ClickCount >= 2)
            {
                DaySelected?.Invoke(this, date);
                e.Handled = true;
            }
        };
        cellBorder.PointerPressed += (_, e) =>
        {
            if (e.ClickCount >= 2 && e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            {
                DaySelected?.Invoke(this, date);
                e.Handled = true;
            }
        };

        return cellBorder;
    }

    // ── плашка однодневного события ───────────────────────────────────────────

    private Border _MakeChip(Event ev)
    {
        var color    = Color.Parse(ev.Color);
        var normalBg = new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B));
        var hoverBg  = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B));

        var leftStripe = new Border
        {
            Width               = 4,
            Background          = new SolidColorBrush(color),
            CornerRadius        = new CornerRadius(2, 0, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var content = new Grid();
        content.Children.Add(leftStripe);
        content.Children.Add(new TextBlock
        {
            Text              = ev.AllDay ? ev.Title : $"{ev.StartDt:HH:mm} {ev.Title}",
            FontSize          = 13,
            Foreground        = new SolidColorBrush(_Darken(color)),
            Margin            = new Thickness(8, 0, 2, 0),
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var selFrame = new Border
        {
            CornerRadius     = new CornerRadius(4),
            BorderThickness  = new Thickness(1.5),
            BorderBrush      = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        var wrapper = new Grid();
        wrapper.Children.Add(content);
        wrapper.Children.Add(selFrame);

        var chip = new Border
        {
            Height       = BarH,
            Background   = normalBg,
            CornerRadius = new CornerRadius(4),
            Cursor       = new Cursor(StandardCursorType.Hand),
            Child        = wrapper,
        };
        chip.PointerEntered += (_, _) => chip.Background = hoverBg;
        chip.PointerExited  += (_, _) => chip.Background = normalBg;
        chip.PointerPressed  += (_, e) => { if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) { _skipNextDeselect = true; _BeginDragTrack(ev, chip, e); e.Handled = true; } };
        chip.PointerMoved    += (_, e) => _OnDragMoved(e);
        chip.PointerReleased += (_, e) =>
        {
            var wasDrag = _dragActive;
            _OnDragReleased(e);
            if (wasDrag) return;
            if (_pressClickCount >= 2)
            {
                _Deselect();
                EventClicked?.Invoke(this, ev);
            }
            else
            {
                _Deselect();
                leftStripe.IsVisible = false;
                selFrame.BorderBrush = new SolidColorBrush(color);
                _deselectCurrent     = () => { selFrame.BorderBrush = Brushes.Transparent; leftStripe.IsVisible = true; };
            }
        };
        return chip;
    }

    // ── бар многодневного события на Canvas ───────────────────────────────────

    private void _AddSpanBar(Event ev, double bx, double by, double bw,
                              bool leftCap, bool rightCap)
    {
        var color = Color.Parse(ev.Color);
        var cr    = new CornerRadius(
            leftCap  ? 5 : 0, rightCap ? 5 : 0,
            rightCap ? 5 : 0, leftCap  ? 5 : 0);

        var bar = new Grid { Width = bw, Height = BarH, Cursor = new Cursor(StandardCursorType.Hand) };

        // Белый непрозрачный слой блокирует линии сетки снизу
        bar.Children.Add(new Border { Background = Brushes.White, CornerRadius = cr });

        // Полупрозрачный цветной фон поверх белого
        var normalBg = new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B));
        var hoverBg  = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B));
        var bgBorder = new Border { Background = normalBg, CornerRadius = cr };
        bar.Children.Add(bgBorder);

        if (ev.Id.HasValue)
        {
            if (!_spanBgBorders.ContainsKey(ev.Id.Value))
                _spanBgBorders[ev.Id.Value] = new List<Border>();
            _spanBgBorders[ev.Id.Value].Add(bgBorder);
        }

        bar.PointerEntered += (_, _) =>
        {
            var all = ev.Id.HasValue && _spanBgBorders.TryGetValue(ev.Id.Value, out var bs) ? bs : [bgBorder];
            foreach (var b in all) b.Background = hoverBg;
        };
        bar.PointerExited += (_, _) =>
        {
            var all = ev.Id.HasValue && _spanBgBorders.TryGetValue(ev.Id.Value, out var bs) ? bs : [bgBorder];
            foreach (var b in all) b.Background = normalBg;
        };

        // Цветной акцент слева — только у начала события
        Border? leftStripeBar = null;
        if (leftCap)
        {
            leftStripeBar = new Border
            {
                Width               = 4,
                Background          = new SolidColorBrush(color),
                CornerRadius        = new CornerRadius(2, 0, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            bar.Children.Add(leftStripeBar);
        }

        bar.Children.Add(new TextBlock
        {
            Text              = ev.AllDay ? ev.Title : $"{ev.StartDt:HH:mm} {ev.Title}",
            FontSize          = 13,
            Foreground        = new SolidColorBrush(_Darken(color)),
            Margin            = new Thickness(8, 0, 4, 0),
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var selFrame = new Border
        {
            CornerRadius     = cr,
            BorderThickness  = new Thickness(1.5),
            BorderBrush      = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        bar.Children.Add(selFrame);

        if (ev.Id.HasValue)
        {
            if (!_spanFrames.ContainsKey(ev.Id.Value))
                _spanFrames[ev.Id.Value] = new List<(Border, Border?)>();
            _spanFrames[ev.Id.Value].Add((selFrame, leftStripeBar));
        }

        bar.PointerPressed  += (_, e) => { if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) { _skipNextDeselect = true; _BeginDragTrack(ev, bar, e); } };
        bar.PointerMoved    += (_, e) => _OnDragMoved(e);
        bar.PointerReleased += (_, e) =>
        {
            var wasDrag = _dragActive;
            _OnDragReleased(e);
            if (wasDrag) return;
            if (_pressClickCount >= 2)
            {
                _Deselect();
                EventClicked?.Invoke(this, ev);
            }
            else
            {
                _Deselect();
                var colorBrush = new SolidColorBrush(color);
                if (ev.Id.HasValue && _spanFrames.TryGetValue(ev.Id.Value, out var frames))
                {
                    foreach (var (sf, ls) in frames) { sf.BorderBrush = colorBrush; if (ls is not null) ls.IsVisible = false; }
                    _deselectCurrent = () => { foreach (var (sf, ls) in frames) { sf.BorderBrush = Brushes.Transparent; if (ls is not null) ls.IsVisible = true; } };
                }
            }
        };

        Canvas.SetLeft(bar, bx);
        Canvas.SetTop(bar, by);
        SpanCanvas.Children.Add(bar);
    }

    // ── drag-and-drop ─────────────────────────────────────────────────────────

    private void _BeginDragTrack(Event ev, Control src, PointerPressedEventArgs e)
    {
        if (_dragEv is not null) return;
        _dragEv          = ev;
        _dragActive      = false;
        _pressClickCount = e.ClickCount;
        _dragPress       = e.GetPosition(GridContainer);
        _dragOrigStart = ev.StartDt;
        _dragOrigEnd   = ev.EndDt;
        _dragSourceDay = ev.StartDt.Date;
        _dragTargetDay = ev.StartDt.Date;
        _dragPointer   = e.Pointer;
        e.Pointer.Capture(src);
    }

    private void _OnDragMoved(PointerEventArgs e)
    {
        if (_dragEv is null) return;
        var pos = e.GetPosition(GridContainer);
        if (!_dragActive)
        {
            if (Math.Abs(pos.X - _dragPress.X) + Math.Abs(pos.Y - _dragPress.Y) < 6) return;
            _dragActive = true;
        }
        var target = _DayAtPos(pos);
        if (!target.HasValue) return;
        var changed = target.Value.Date != _dragTargetDay;
        if (changed) _dragTargetDay = target.Value.Date;

        if (_dragTargetDay != _dragSourceDay)
        {
            if (changed || _dragPreview is null) _UpdateDragPreview();
        }
        else
        {
            if (_dragPreview is not null) { SpanCanvas.Children.Remove(_dragPreview); _dragPreview = null; }
            if (_shiftedDay.HasValue) _RestoreShift(_shiftedDay.Value);
        }
    }

    private void _OnDragReleased(PointerReleasedEventArgs e)
    {
        if (_dragEv is null) return;
        if (_dragActive && _dragTargetDay != _dragSourceDay)
        {
            var delta  = _dragTargetDay - _dragSourceDay;
            var ev     = _dragEv;
            ev.StartDt = _dragOrigStart + delta;
            ev.EndDt   = _dragOrigEnd   + delta;
            _CancelDrag();
            EventMoved?.Invoke(this, ev);
        }
        else
        {
            _CancelDrag();
        }
    }

    private void _CancelDrag()
    {
        _dragPointer?.Capture(null);
        _dragPointer = null;
        if (_dragPreview is not null) { SpanCanvas.Children.Remove(_dragPreview); _dragPreview = null; }
        if (_shiftedDay.HasValue) _RestoreShift(_shiftedDay.Value);
        _dragEv     = null;
        _dragActive = false;
    }

    private void _UpdateCellHover(DateTime? day)
    {
        if (day == _hoveredDay) return;
        if (_hoveredDay.HasValue && _dayCellMap.TryGetValue(_hoveredDay.Value, out var prev))
            prev.Cell.Background = prev.BaseBg;
        _hoveredDay = day;
        if (day.HasValue && _dayCellMap.TryGetValue(day.Value, out var curr))
            curr.Cell.Background = new SolidColorBrush(Color.Parse("#E5F0FF"));
    }

    private void _UpdateCircleHover(Point? pos)
    {
        var day = pos.HasValue ? _CircleDayAtPos(pos.Value) : null;
        if (day == _hoveredCircleDay) return;
        if (_hoveredCircleDay.HasValue && _dayPlusMap.TryGetValue(_hoveredCircleDay.Value, out var prev))
            prev.Hide();
        _hoveredCircleDay = day;
        if (day.HasValue && _dayPlusMap.TryGetValue(day.Value, out var curr))
            curr.Show();
    }

    private DateTime? _CircleDayAtPos(Point pos)
    {
        var cw = GridContainer.Bounds.Width  / 7.0;
        var ch = GridContainer.Bounds.Height / 6.0;
        if (cw <= 0 || ch <= 0) return null;
        var col = Math.Max(0, Math.Min(6, (int)(pos.X / cw)));
        var row = Math.Max(0, Math.Min(5, (int)(pos.Y / ch)));
        // Кружок: Width=24, Height=24, HorizontalAlignment=Right, Margin=(0,4,4,0)
        var cx = col * cw + cw - 4 - 12;
        var cy = row * ch + 4 + 12;
        var dx = pos.X - cx;
        var dy = pos.Y - cy;
        return dx * dx + dy * dy <= 12.0 * 12.0 ? _gridStart.AddDays(row * 7 + col) : null;
    }

    private void _Deselect()
    {
        _deselectCurrent?.Invoke();
        _deselectCurrent = null;
    }

    private void _RestoreShift(DateTime day)
    {
        if (_dayUiMap.TryGetValue(day, out var ui))
        {
            if (_removedChip is not null)
            {
                ui.Panel.Children.Add(_removedChip);
                _removedChip = null;
            }
            ui.Panel.Margin   = new Thickness(2, ui.OrigTopOff, 2, 0);
            ui.More.IsVisible = ui.OrigHidden > 0;
            ui.More.Text      = $"+{ui.OrigHidden} ещё";
        }
        _shiftedDay = null;
    }

    private void _UpdateDragPreview()
    {
        if (_dragEv is null || GridContainer.Bounds.Width == 0) return;
        var cw  = GridContainer.Bounds.Width  / 7.0;
        var ch  = GridContainer.Bounds.Height / 6.0;
        var off = (int)(_dragTargetDay - _gridStart).TotalDays;
        if (off < 0 || off >= 42) return;
        var col = off % 7;
        var row = off / 7;
        var bx  = col * cw + 2;
        var bw  = cw - 4;
        var color = Color.Parse(_dragEv.Color);

        var weekStart  = _gridStart.AddDays(row * 7);
        var layout     = _ComputeWeekLayout(weekStart);
        var maxSpanRow = _events
            .Where(e => (e.AllDay || e.EndDt.Date > e.StartDt.Date)
                     && e.StartDt.Date <= _dragTargetDay
                     && e.EndDt.Date   >= _dragTargetDay
                     && e.Id.HasValue && layout.ContainsKey(e.Id!.Value))
            .Select(e => layout[e.Id!.Value].RowIdx)
            .DefaultIfEmpty(-1)
            .Max();
        var singles = _events
            .Where(e => !e.AllDay
                     && e.EndDt.Date == e.StartDt.Date
                     && e.StartDt.Date == _dragTargetDay)
            .ToList();
        var topOff = DateH + (maxSpanRow + 1) * BarStep - 1;
        var maxVis = ch > 0 ? Math.Max(0, (int)((ch - BPad - MoreH - topOff) / BarStep)) : 5;

        // Restore shift from a previous target day before applying to new one
        if (_shiftedDay.HasValue && _shiftedDay.Value != _dragTargetDay)
            _RestoreShift(_shiftedDay.Value);

        // Preview goes at the top of the singles area (topOff)
        var by = row * ch + topOff;

        // Not enough room even for the preview bar — hide and restore
        if (ch > 0 && by + BarH > row * ch + ch - BPad)
        {
            if (_dragPreview is not null) { SpanCanvas.Children.Remove(_dragPreview); _dragPreview = null; }
            if (_shiftedDay.HasValue) _RestoreShift(_shiftedDay.Value);
            return;
        }

        // Shift existing single-day events down by BarStep and update "+N ещё"
        if (_dayUiMap.TryGetValue(_dragTargetDay, out var ui))
        {
            ui.Panel.Margin = new Thickness(2, topOff + BarStep, 2, 0);
            // If the panel was full (all maxVis slots used), the last chip now overflows — remove it
            if (maxVis > 0 && ui.Panel.Children.Count > 0 && ui.Panel.Children.Count >= maxVis)
            {
                _removedChip = (Control)ui.Panel.Children[ui.Panel.Children.Count - 1];
                ui.Panel.Children.RemoveAt(ui.Panel.Children.Count - 1);
            }
            ui.More.IsVisible = false;
        }
        _shiftedDay = _dragTargetDay;

        if (_dragPreview is null)
        {
            _dragPreview = new Grid { Height = BarH, IsHitTestVisible = false };
            _dragPreview.Children.Add(new Border
            {
                Background      = new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B)),
                CornerRadius    = new CornerRadius(4),
                BorderBrush     = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
            });
            _dragPreview.Children.Add(new Border
            {
                Width               = 4,
                Background          = new SolidColorBrush(color),
                CornerRadius        = new CornerRadius(2, 0, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
            });
            _dragPreview.Children.Add(new TextBlock
            {
                Text              = _dragEv.AllDay ? _dragEv.Title : $"{_dragEv.StartDt:HH:mm} {_dragEv.Title}",
                FontSize          = 13,
                Foreground        = new SolidColorBrush(_Darken(color)),
                Margin            = new Thickness(8, 0, 4, 0),
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            SpanCanvas.Children.Add(_dragPreview);
        }

        _dragPreview.Width = bw;
        Canvas.SetLeft(_dragPreview, bx);
        Canvas.SetTop(_dragPreview, by);
    }

    private DateTime? _DayAtPos(Point pos)
    {
        var cw = GridContainer.Bounds.Width  / 7.0;
        var ch = GridContainer.Bounds.Height / 6.0;
        if (cw <= 0 || ch <= 0) return null;
        var col = Math.Max(0, Math.Min(6, (int)(pos.X / cw)));
        var row = Math.Max(0, Math.Min(5, (int)(pos.Y / ch)));
        return _gridStart.AddDays(row * 7 + col);
    }

    private static Color _Darken(Color c, double f = 0.6)
        => Color.FromArgb(c.A, (byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
}
