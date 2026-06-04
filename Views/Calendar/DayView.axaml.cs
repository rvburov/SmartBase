using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MyPlanner.Core.Models;

namespace MyPlanner.Views.Calendar;

public partial class DayView : UserControl
{
    public event EventHandler<Event>?    EventClicked;
    public event EventHandler<Event>?    EventMoved;
    public event EventHandler<DateOnly>? AllDayCreationRequested;
    public event EventHandler<DateTime>? TimeSlotCreationRequested;

    private Canvas?          _todayTimeOverlay;
    private DispatcherTimer? _timeLineTimer;

    // drag state
    private Event?            _dragEvent;
    private Border?           _dragGhost;
    private Border?           _dragGhostOverflow;
    private TextBlock?        _dragGhostTitleBlock;
    private TextBlock?        _dragGhostTimeBlock;
    private StackPanel?       _dragGhostContentStack;
    private double            _dragGhostOriginalHeight;
    private Border?           _dragSourceBtn;
    private List<EventEntry>  _dragDimmedBtns = [];
    private Point             _dragStartPos;
    private double            _dragOffsetY;
    private bool              _isDragging;
    private int               _pressClickCount;
    private int               _dragSourceStartMin;
    private List<DateTime>    _currentDays = [];

    // resize state
    private bool        _isResizingEvent;
    private EventEntry? _resizeEntry;
    private bool        _resizeFromBottom;
    private Border?     _resizeGhost;
    private Border?     _resizeGhostOverflow;
    private TextBlock?  _resizeGhostTitleBlock;
    private TextBlock?  _resizeGhostTimeBlock;
    private StackPanel? _resizeGhostContentStack;

    // selection
    private Action? _deselectCurrent;
    private int?    _pendingSelectId;

    private sealed class EventEntry(Event ev, int startMin, int endMin, Border btn)
    {
        public Event   Ev         = ev;
        public int     StartMin   = startMin;
        public int     EndMin     = endMin;
        public Border  Btn        = btn;
        public int     SubCol     = 0;
        public int     SubColSpan = 1;
        public int     TotalCols  = 1;
        public Action? Select;
        public Action? Deselect;
        public Action? Dim;
        public Action? Undim;
    }
    private readonly List<EventEntry> _eventEntries = [];

    public DayView()
    {
        InitializeComponent();
        HourLabels.ItemsSource = Enumerable.Range(1, 23).Select(h => $"{h:00}:00").ToList();
        HourRows.ItemsSource   = Enumerable.Range(0, 24).ToList();

        _timeLineTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timeLineTimer.Tick += (_, _) => UpdateTimeLine();
        _timeLineTimer.Start();

        EventsGrid.AddHandler(PointerPressedEvent,  OnGridPointerPressed,  RoutingStrategies.Bubble, handledEventsToo: true);
        EventsGrid.AddHandler(PointerMovedEvent,    OnGridPointerMoved,    RoutingStrategies.Bubble, handledEventsToo: true);
        EventsGrid.AddHandler(PointerReleasedEvent, OnGridPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        AllDayBarCtrl.EventClicked           += (_, ev) => EventClicked?.Invoke(this, ev);
        AllDayBarCtrl.EventMoved             += (_, ev) => EventMoved?.Invoke(this, ev);
        AllDayBarCtrl.EventCreationRequested += (_, d)  => AllDayCreationRequested?.Invoke(this, d);

        SizeChanged += (_, _) => AllDayBarCtrl.InvalidateMeasure();
    }

    public void Refresh(IEnumerable<Event> events, DateOnly date, bool scrollToNow = false)
    {
        var day       = date.ToDateTime(TimeOnly.MinValue);
        var allEvents = events.ToList();

        _currentDays = [day];
        _eventEntries.Clear();
        _deselectCurrent = null;

        AllDayBarCtrl.SetData(new[] { date }, allEvents);

        WeekColumns.Children.Clear();
        _todayTimeOverlay = null;

        var canvas  = new Canvas { Background = Brushes.Transparent };
        var dayDate = date;
        var dayEvents = allEvents
            .Where(e => !e.AllDay
                     && (e.EndDt - e.StartDt).TotalSeconds < 86400
                     && DateOnly.FromDateTime(e.StartDt) <= dayDate
                     && dayDate <= DateOnly.FromDateTime(e.EndDt))
            .ToList();

        var sortedDayEvents = dayEvents
            .OrderBy(e => e.StartDt)
            .ThenBy(e => e.Title)
            .ToList();

        var dayEntryLayouts = new List<(EventEntry Entry, Border TopHandle, Border BottomHandle)>();

        foreach (var group in GroupOverlapping(sortedDayEvents))
        {
            foreach (var (ev, colIdx, nCols, rightLimit) in AssignColumns(group))
            {
                var startMin = DateOnly.FromDateTime(ev.StartDt) == dayDate
                    ? ev.StartDt.Hour * 60 + ev.StartDt.Minute
                    : 0;
                var effectiveEndMin = DateOnly.FromDateTime(ev.EndDt) == dayDate
                    ? ev.EndDt.Hour * 60 + ev.EndDt.Minute
                    : 1440;
                var evH        = Math.Max(22, effectiveEndMin - startMin);
                var evHVisible = evH - 2;
                var color      = Color.Parse(EventColors.Display(ev.Color));
                var normalBg   = new SolidColorBrush(BlendWithWhite(color, 40));
                var hoverBg    = new SolidColorBrush(BlendWithWhite(color, 90));
                var colorBrush = new SolidColorBrush(color);
                var darkBrush  = new SolidColorBrush(Darken(color));

                const double lineH   = 16.0;
                const double margins = 4.0;
                const double timeH   = 15.0;

                var maxTitleLines = (int)((evHVisible - margins - timeH) / lineH);
                var showTime      = maxTitleLines >= 1;
                var titleMaxLines = showTime
                    ? maxTitleLines
                    : Math.Max(1, (int)((evHVisible - margins) / lineH));

                var titleBlock = new TextBlock
                {
                    Text         = ev.Title,
                    FontSize     = 13,
                    FontWeight   = FontWeight.SemiBold,
                    Foreground   = darkBrush,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines     = titleMaxLines,
                };

                var contentStack = new StackPanel { Margin = new Thickness(4, 2, 3, 2) };
                contentStack.Children.Add(titleBlock);

                if (showTime)
                {
                    contentStack.Children.Add(new TextBlock
                    {
                        Text       = $"{ev.StartDt:HH:mm}–{ev.EndDt:HH:mm}",
                        FontSize   = 13,
                        Foreground = darkBrush,
                        Margin     = new Thickness(0, 1, 0, 0),
                    });
                }

                Grid.SetColumn(contentStack, 1);

                var strip = new Border
                {
                    Background        = colorBrush,
                    CornerRadius      = new CornerRadius(2),
                    Width             = 3,
                    Margin            = new Thickness(2, 3, 0, 3),
                    VerticalAlignment = VerticalAlignment.Stretch,
                };

                var selFrame = new Border
                {
                    CornerRadius     = new CornerRadius(3),
                    BorderThickness  = new Thickness(1.5),
                    BorderBrush      = Brushes.Transparent,
                    IsHitTestVisible = false,
                };
                Grid.SetColumnSpan(selFrame, 2);

                var innerGrid = new Grid();
                innerGrid.ColumnDefinitions.Add(new ColumnDefinition(4, GridUnitType.Pixel));
                innerGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                innerGrid.Children.Add(strip);
                innerGrid.Children.Add(contentStack);
                innerGrid.Children.Add(selFrame);

                var btn = new Border
                {
                    Background   = normalBg,
                    CornerRadius = new CornerRadius(3),
                    Height       = evHVisible,
                    Cursor       = new Cursor(StandardCursorType.Hand),
                    ClipToBounds = true,
                    Child        = innerGrid,
                };

                var isSelected = false;
                btn.PointerEntered += (_, _) => { if (!isSelected) btn.Background = hoverBg; };
                btn.PointerExited  += (_, _) => { if (!isSelected) btn.Background = normalBg; };

                Canvas.SetTop(btn, startMin + 2);
                canvas.Children.Add(btn);

                var topHandle = new Border
                {
                    Height           = 6,
                    Background       = Brushes.Transparent,
                    Cursor           = new Cursor(StandardCursorType.SizeNorthSouth),
                    IsHitTestVisible = true,
                };
                Canvas.SetTop(topHandle, startMin + 2);
                canvas.Children.Add(topHandle);

                var bottomHandle = new Border
                {
                    Height           = 6,
                    Background       = Brushes.Transparent,
                    Cursor           = new Cursor(StandardCursorType.SizeNorthSouth),
                    IsHitTestVisible = true,
                };
                Canvas.SetTop(bottomHandle, startMin + 2 + evHVisible - 6);
                canvas.Children.Add(bottomHandle);

                var solidBrush = new SolidColorBrush(color);
                var entry = new EventEntry(ev, startMin, startMin + evH, btn)
                {
                    SubCol     = colIdx,
                    SubColSpan = rightLimit - colIdx,
                    TotalCols  = nCols,
                    Select = () =>
                    {
                        isSelected = true;
                        btn.Background = solidBrush;
                        foreach (var tb in contentStack.Children.OfType<TextBlock>())
                            tb.Foreground = Brushes.White;
                    },
                    Deselect = () =>
                    {
                        isSelected = false;
                        btn.Background = normalBg;
                        foreach (var tb in contentStack.Children.OfType<TextBlock>())
                            tb.Foreground = darkBrush;
                    },
                    Dim   = () => btn.Opacity = 0.45,
                    Undim = () => btn.Opacity = 1.0,
                };
                _eventEntries.Add(entry);
                dayEntryLayouts.Add((entry, topHandle, bottomHandle));
            }
        }

        void LayoutEntries(double canvasW)
        {
            foreach (var (entry, topHandle, bottomHandle) in dayEntryLayouts)
            {
                var seg  = canvasW / entry.TotalCols;
                var left = entry.SubCol * seg + 1.0;
                var w    = Math.Max(1, entry.SubColSpan * seg - 3.0);
                Canvas.SetLeft(entry.Btn, left);
                entry.Btn.Width = w;
                Canvas.SetLeft(topHandle, left);
                topHandle.Width = w;
                Canvas.SetLeft(bottomHandle, left);
                bottomHandle.Width = w;
            }
        }

        canvas.SizeChanged += (_, args) => LayoutEntries(args.NewSize.Width);
        canvas.AttachedToVisualTree += (_, _) => LayoutEntries(canvas.Bounds.Width > 0 ? canvas.Bounds.Width : canvas.Width);

        var dayGrid = new Grid();
        dayGrid.Children.Add(canvas);

        if (day.Date == DateTime.Today)
        {
            var overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
            dayGrid.Children.Add(overlay);
            _todayTimeOverlay = overlay;
        }

        WeekColumns.Children.Add(dayGrid);
        UpdateTimeLine();

        if (_pendingSelectId.HasValue)
        {
            var toSelect = _eventEntries.FirstOrDefault(e => e.Ev.Id == _pendingSelectId.Value);
            if (toSelect is not null)
            {
                toSelect.Select?.Invoke();
                _deselectCurrent = toSelect.Deselect;
            }
            _pendingSelectId = null;
        }

        if (scrollToNow)
            Dispatcher.UIThread.Post(
                () => TimeScroll.Offset = new Vector(0, Math.Max(0, DateTime.Now.Hour * 60 - 120)),
                DispatcherPriority.Loaded);
    }

    private void DoDeselect()
    {
        _deselectCurrent?.Invoke();
        _deselectCurrent = null;
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(EventsGrid).Properties.IsLeftButtonPressed) return;

        var pos  = e.GetPosition(EventsGrid);
        var colW = EventsGrid.Bounds.Width;
        if (colW <= 0) return;

        var entry = _eventEntries
            .Where(h => pos.Y >= h.StartMin && pos.Y <= h.EndMin)
            .OrderByDescending(h =>
            {
                var seg   = colW / h.TotalCols;
                var left  = h.SubCol * seg + 1.0;
                var right = left + h.SubColSpan * seg - 3.0;
                return pos.X >= left && pos.X <= right ? 1 : 0;
            })
            .FirstOrDefault();

        if (entry is null)
        {
            DoDeselect();
            if (e.ClickCount >= 2 && _currentDays.Count > 0)
            {
                var minutes = (int)(pos.Y / 30) * 30;
                var day     = _currentDays[0];
                var dt      = new DateTime(day.Year, day.Month, day.Day, minutes / 60, minutes % 60, 0);
                TimeSlotCreationRequested?.Invoke(this, dt);
                e.Handled = true;
            }
            return;
        }

        var onTopEdge    = pos.Y >= entry.StartMin + 2 && pos.Y <= entry.StartMin + 8;
        var onBottomEdge = pos.Y >= entry.EndMin - 6   && pos.Y <= entry.EndMin;

        if (onTopEdge || onBottomEdge)
        {
            _resizeEntry      = entry;
            _resizeFromBottom = onBottomEdge;
            _isResizingEvent  = true;

            var ghostW   = colW - 2.0;
            _resizeGhost = CreateGhostButton(entry.Ev, entry.EndMin - entry.StartMin - 2, ghostW,
                               out _resizeGhostTitleBlock, out _resizeGhostTimeBlock, out _resizeGhostContentStack);
            Canvas.SetLeft(_resizeGhost, 0);
            Canvas.SetTop(_resizeGhost, entry.StartMin + 2);
            DragOverlay.Children.Add(_resizeGhost);

            entry.Btn.Opacity = 0;
            e.Pointer.Capture(EventsGrid);
            e.Handled = true;
            return;
        }

        _dragEvent          = entry.Ev;
        _dragSourceBtn      = entry.Btn;
        _dragStartPos       = pos;
        _isDragging         = false;
        _pressClickCount    = e.ClickCount;
        _dragSourceStartMin = entry.StartMin;

        var evStartDay = DateOnly.FromDateTime(entry.Ev.StartDt);
        var colDay     = DateOnly.FromDateTime(_currentDays[0]);
        _dragOffsetY   = evStartDay < colDay
            ? pos.Y + (1440 - (entry.Ev.StartDt.Hour * 60 + entry.Ev.StartDt.Minute))
            : pos.Y - (entry.StartMin + 2);

        DoDeselect();
        entry.Select?.Invoke();
        _deselectCurrent = entry.Deselect;

        e.Pointer.Capture(EventsGrid);
        e.Handled = true;
    }

    private void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(EventsGrid);

        if (_isResizingEvent && _resizeEntry != null && _resizeGhost != null)
        {
            var colW      = EventsGrid.Bounds.Width;
            var rawY      = (int)(pos.Y / 15) * 15;
            var timeSnapped = Math.Clamp(rawY, 0, 1440);
            _resizeGhost.IsVisible = true;

            if (_resizeFromBottom)
            {
                var resizeColDay      = DateOnly.FromDateTime(_currentDays[0]);
                var actualStartMin    = _resizeEntry.Ev.StartDt.Hour * 60 + _resizeEntry.Ev.StartDt.Minute;
                var effectiveStartMin = DateOnly.FromDateTime(_resizeEntry.Ev.StartDt) == resizeColDay
                    ? actualStartMin : 0;
                var startIsPrevDay    = DateOnly.FromDateTime(_resizeEntry.Ev.StartDt) < resizeColDay;

                var isOverflowDown = rawY > 1440;
                var canShrinkLeft  = rawY < 0 && startIsPrevDay;

                if (canShrinkLeft)
                {
                    // Конец уходит в предыдущий день — вне экрана
                    _resizeGhost.IsVisible = false;
                    if (_resizeGhostOverflow != null)
                    {
                        DragOverlay.Children.Remove(_resizeGhostOverflow);
                        _resizeGhostOverflow = null;
                    }
                }
                else if (!isOverflowDown)
                {
                    var minEnd = DateOnly.FromDateTime(_resizeEntry.Ev.StartDt) == resizeColDay
                        ? effectiveStartMin + 30
                        : Math.Max(0, 30 - (1440 - actualStartMin));
                    var newEndMin = Math.Clamp(timeSnapped, minEnd, 1440);
                    var ghostH    = Math.Max(13, newEndMin - effectiveStartMin - 2);
                    Canvas.SetTop(_resizeGhost, effectiveStartMin + 2);
                    _resizeGhost.Height = ghostH;
                    UpdateResizeGhostText(ghostH, effectiveStartMin, newEndMin);

                    var svH = TimeScroll.Bounds.Height;
                    if (svH > 0)
                    {
                        var ghostBottom = effectiveStartMin + 2 + ghostH;
                        if (ghostBottom > TimeScroll.Offset.Y + svH - 30)
                            TimeScroll.Offset = TimeScroll.Offset.WithY(
                                Math.Clamp(ghostBottom - svH + 30, 0, Math.Max(0, 1440 - svH)));
                    }
                    if (_resizeGhostOverflow != null)
                    {
                        DragOverlay.Children.Remove(_resizeGhostOverflow);
                        _resizeGhostOverflow = null;
                    }
                }
                else
                {
                    // canExtendRight — следующий день вне экрана, показываем полный ghost текущего дня
                    var ghostH = Math.Max(13, 1440 - effectiveStartMin - 2);
                    Canvas.SetTop(_resizeGhost, effectiveStartMin + 2);
                    _resizeGhost.Height = ghostH;
                    UpdateResizeGhostText(ghostH, effectiveStartMin, 1440);
                    if (_resizeGhostOverflow != null)
                    {
                        DragOverlay.Children.Remove(_resizeGhostOverflow);
                        _resizeGhostOverflow = null;
                    }
                }
            }
            else
            {
                var resizeColDay    = DateOnly.FromDateTime(_currentDays[0]);
                var actualEndMin    = _resizeEntry.Ev.EndDt.Hour * 60 + _resizeEntry.Ev.EndDt.Minute;
                var effectiveEndMin = DateOnly.FromDateTime(_resizeEntry.Ev.EndDt) == resizeColDay
                    ? actualEndMin : 1440;

                var isOverflowUp   = rawY < 0;
                var isOverflowDown = rawY > 1440;

                if (isOverflowDown)
                {
                    // Начало уходит в следующий день — вне экрана
                    _resizeGhost.IsVisible = false;
                    if (_resizeGhostOverflow != null)
                    {
                        DragOverlay.Children.Remove(_resizeGhostOverflow);
                        _resizeGhostOverflow = null;
                    }
                }
                else if (!isOverflowUp)
                {
                    var maxStart = DateOnly.FromDateTime(_resizeEntry.Ev.EndDt) == resizeColDay
                        ? effectiveEndMin - 30
                        : Math.Min(1440, 1440 + actualEndMin - 30);
                    var newStartMin = Math.Clamp(timeSnapped, 0, maxStart);
                    var ghostH      = Math.Max(13, effectiveEndMin - newStartMin - 2);
                    Canvas.SetTop(_resizeGhost, newStartMin + 2);
                    _resizeGhost.Height = ghostH;
                    UpdateResizeGhostText(ghostH, newStartMin, effectiveEndMin);

                    var svH = TimeScroll.Bounds.Height;
                    if (svH > 0 && newStartMin + 2 < TimeScroll.Offset.Y + 30)
                        TimeScroll.Offset = TimeScroll.Offset.WithY(Math.Max(0, newStartMin - 28));

                    if (_resizeGhostOverflow != null)
                    {
                        DragOverlay.Children.Remove(_resizeGhostOverflow);
                        _resizeGhostOverflow = null;
                    }
                }
                else
                {
                    // canExtendLeft — начало уходит в предыдущий день, ghost прикалывается к 00:00
                    var ghostH = Math.Max(13, effectiveEndMin - 2);
                    Canvas.SetTop(_resizeGhost, 2);
                    _resizeGhost.Height = ghostH;
                    UpdateResizeGhostText(ghostH, 0, effectiveEndMin);
                    if (_resizeGhostOverflow != null)
                    {
                        DragOverlay.Children.Remove(_resizeGhostOverflow);
                        _resizeGhostOverflow = null;
                    }
                }
            }
            return;
        }

        if (_dragEvent is null || _dragSourceBtn is null) return;

        if (!_isDragging)
        {
            var dist = Math.Abs(pos.Y - _dragStartPos.Y) + Math.Abs(pos.X - _dragStartPos.X);
            if (dist < 5) return;

            _isDragging = true;

            var colW  = EventsGrid.Bounds.Width;
            var ghostW = colW - 2.0;
            var ghostH = _dragSourceBtn.Bounds.Height;
            _dragGhostOriginalHeight = ghostH;

            _deselectCurrent?.Invoke();
            _deselectCurrent = null;
            _dragDimmedBtns = _eventEntries.Where(e => e.Ev.Id == _dragEvent!.Id).ToList();
            foreach (var b in _dragDimmedBtns) b.Dim?.Invoke();

            _dragGhost = CreateGhostButton(_dragEvent, ghostH, ghostW,
                out _dragGhostTitleBlock, out _dragGhostTimeBlock, out _dragGhostContentStack);
            Canvas.SetLeft(_dragGhost, 0);
            Canvas.SetTop(_dragGhost, _dragSourceStartMin + 2);
            DragOverlay.Children.Add(_dragGhost);
        }

        if (_dragGhost is not null)
        {
            var rawY     = pos.Y - _dragOffsetY;
            var duration = (int)(_dragEvent!.EndDt - _dragEvent.StartDt).TotalMinutes;
            var snapped  = (int)Math.Floor(rawY / 15) * 15;

            Canvas.SetLeft(_dragGhost, 0);

            if (snapped < 0)
            {
                var minutesInThisDay = duration + snapped;
                if (minutesInThisDay >= 13)
                {
                    Canvas.SetTop(_dragGhost, 2);
                    _dragGhost.Height    = minutesInThisDay - 2;
                    _dragGhost.IsVisible = true;
                    UpdateDragGhostText(_dragGhost.Height, 1440 + snapped, 1440 + snapped + duration);
                }
                else _dragGhost.IsVisible = false;
            }
            else if (snapped >= 1440)
            {
                var nextDayMin = Math.Clamp(snapped - 1440, 0, 1425);
                Canvas.SetTop(_dragGhost, nextDayMin);
                _dragGhost.Height    = Math.Max(13, Math.Min(duration, 1440 - nextDayMin) - 2);
                _dragGhost.IsVisible = true;
                UpdateDragGhostText(_dragGhost.Height, nextDayMin, nextDayMin + duration);
            }
            else if (snapped + duration > 1440)
            {
                var clamped = Math.Clamp(snapped, 0, 1425);
                var mainH   = 1440 - clamped - 2;
                _dragGhost.IsVisible = mainH >= 13;
                if (mainH >= 13)
                {
                    Canvas.SetTop(_dragGhost, clamped);
                    _dragGhost.Height = mainH;
                    UpdateDragGhostText(mainH, clamped, clamped + duration);
                }
            }
            else
            {
                var clamped = Math.Clamp(snapped, 0, 1425);
                var ghostH  = Math.Max(13, Math.Min(duration, 1440 - clamped) - 2);
                _dragGhost.IsVisible = true;
                Canvas.SetTop(_dragGhost, clamped);
                _dragGhost.Height = ghostH;
                UpdateDragGhostText(ghostH, clamped, clamped + duration);
            }

            if (_dragGhostOverflow != null)
            {
                DragOverlay.Children.Remove(_dragGhostOverflow);
                _dragGhostOverflow = null;
            }
        }
    }

    private void OnGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isResizingEvent && _resizeEntry != null)
        {
            var resizePos = e.GetPosition(EventsGrid);
            var ev        = _resizeEntry.Ev;
            var rawY      = (int)(resizePos.Y / 15) * 15;
            var timeSnapped = Math.Clamp(rawY, 0, 1440);

            if (_resizeFromBottom)
            {
                var resizeColDay      = DateOnly.FromDateTime(_currentDays[0]);
                var actualStartMin    = ev.StartDt.Hour * 60 + ev.StartDt.Minute;
                var effectiveStartMin = DateOnly.FromDateTime(ev.StartDt) == resizeColDay
                    ? actualStartMin : 0;
                var resizeDay      = _currentDays[0];
                var startIsPrevDay = DateOnly.FromDateTime(ev.StartDt) < resizeColDay;
                var isOverflowDown = rawY > 1440;

                if (rawY < 0 && startIsPrevDay)
                {
                    var prevDay   = _currentDays[0].AddDays(-1);
                    var targetT   = Math.Clamp(1440 + rawY, actualStartMin + 30, 1440);
                    ev.EndDt = targetT >= 1440
                        ? prevDay.Date.AddDays(1)
                        : new DateTime(prevDay.Year, prevDay.Month, prevDay.Day, targetT / 60, targetT % 60, 0);
                }
                else if (isOverflowDown)
                {
                    var nextDay    = _currentDays[0].AddDays(1);
                    var endTimeMin = Math.Clamp(rawY - 1440, 0, 1425);
                    ev.EndDt = new DateTime(nextDay.Year, nextDay.Month, nextDay.Day,
                                            endTimeMin / 60, endTimeMin % 60, 0);
                }
                else
                {
                    var minEnd = DateOnly.FromDateTime(ev.StartDt) == resizeColDay
                        ? effectiveStartMin + 30
                        : Math.Max(0, 30 - (1440 - actualStartMin));
                    var newEndMin = Math.Clamp(timeSnapped, minEnd, 1440);
                    ev.EndDt = newEndMin >= 1440
                        ? resizeDay.Date.AddDays(1)
                        : new DateTime(resizeDay.Year, resizeDay.Month, resizeDay.Day,
                                       newEndMin / 60, newEndMin % 60, 0);
                }
            }
            else
            {
                var resizeColDay   = DateOnly.FromDateTime(_currentDays[0]);
                var actualEndMin   = ev.EndDt.Hour * 60 + ev.EndDt.Minute;
                var effectiveEndMin = DateOnly.FromDateTime(ev.EndDt) == resizeColDay
                    ? actualEndMin : 1440;
                var resizeDay    = _currentDays[0];
                var isOverflowUp = rawY < 0;
                var isOverflowDown = rawY > 1440;

                if (isOverflowDown)
                {
                    var nextDay     = _currentDays[0].AddDays(1);
                    var evEndDay    = DateOnly.FromDateTime(ev.EndDt);
                    var maxStart    = evEndDay == DateOnly.FromDateTime(nextDay)
                        ? Math.Max(0, actualEndMin - 30) : 1425;
                    var newStartMin = Math.Clamp(rawY - 1440, 0, maxStart);
                    ev.StartDt = new DateTime(nextDay.Year, nextDay.Month, nextDay.Day,
                                             newStartMin / 60, newStartMin % 60, 0);
                }
                else if (isOverflowUp)
                {
                    var prevDay      = _currentDays[0].AddDays(-1);
                    var startTimeMin = Math.Clamp(1440 + rawY, 0, 1425);
                    ev.StartDt = new DateTime(prevDay.Year, prevDay.Month, prevDay.Day,
                                              startTimeMin / 60, startTimeMin % 60, 0);
                }
                else
                {
                    var maxStart = DateOnly.FromDateTime(ev.EndDt) == resizeColDay
                        ? effectiveEndMin - 30
                        : Math.Min(1440, 1440 + actualEndMin - 30);
                    var newStartMin = Math.Clamp(timeSnapped, 0, maxStart);
                    ev.StartDt = newStartMin >= 1440
                        ? resizeDay.Date.AddDays(1)
                        : new DateTime(resizeDay.Year, resizeDay.Month, resizeDay.Day,
                                       newStartMin / 60, newStartMin % 60, 0);
                }
            }

            _pendingSelectId = ev.Id;
            EventMoved?.Invoke(this, ev);

            e.Pointer.Capture(null);
            DragOverlay.Children.Remove(_resizeGhost!);
            if (_resizeGhostOverflow != null)
            {
                DragOverlay.Children.Remove(_resizeGhostOverflow);
                _resizeGhostOverflow = null;
            }
            _resizeEntry.Btn.Opacity = 1.0;

            _isResizingEvent         = false;
            _resizeEntry             = null;
            _resizeGhost             = null;
            _resizeGhostTitleBlock   = null;
            _resizeGhostTimeBlock    = null;
            _resizeGhostContentStack = null;
            return;
        }

        if (_dragEvent is null) return;

        var pos = e.GetPosition(EventsGrid);

        if (!_isDragging)
        {
            if (_pressClickCount >= 2)
            {
                DoDeselect();
                EventClicked?.Invoke(this, _dragEvent);
            }
            else
            {
                var colW  = EventsGrid.Bounds.Width;
                var entry = _eventEntries
                    .Where(h => pos.Y >= h.StartMin && pos.Y <= h.EndMin)
                    .OrderByDescending(h =>
                    {
                        var seg   = colW / h.TotalCols;
                        var left  = h.SubCol * seg + 1.0;
                        var right = left + h.SubColSpan * seg - 3.0;
                        return pos.X >= left && pos.X <= right ? 1 : 0;
                    })
                    .FirstOrDefault();

                if (entry is not null)
                {
                    DoDeselect();
                    entry.Select?.Invoke();
                    _deselectCurrent = entry.Deselect;
                }
            }
        }
        else if (_currentDays.Count > 0)
        {
            var rawY     = pos.Y - _dragOffsetY;
            var duration = (int)(_dragEvent.EndDt - _dragEvent.StartDt).TotalMinutes;
            var snapped  = (int)Math.Floor(rawY / 15) * 15;

            DateTime newStart;
            if (snapped < 0)
            {
                var prevDay    = _currentDays[0].AddDays(-1);
                var prevDayMin = Math.Clamp(1440 + snapped, 0, 1425);
                newStart = new DateTime(prevDay.Year, prevDay.Month, prevDay.Day,
                                        prevDayMin / 60, prevDayMin % 60, 0);
            }
            else if (snapped >= 1440)
            {
                var nextDay    = _currentDays[0].AddDays(1);
                var nextDayMin = Math.Clamp(snapped - 1440, 0, 1425);
                newStart = new DateTime(nextDay.Year, nextDay.Month, nextDay.Day,
                                        nextDayMin / 60, nextDayMin % 60, 0);
            }
            else
            {
                var clamped = Math.Clamp(snapped, 0, 1425);
                var day     = _currentDays[0];
                newStart = new DateTime(day.Year, day.Month, day.Day, clamped / 60, clamped % 60, 0);
            }

            _dragEvent.StartDt = newStart;
            _dragEvent.EndDt   = newStart.AddMinutes(duration);
            _pendingSelectId   = _dragEvent.Id;
            EventMoved?.Invoke(this, _dragEvent);
        }

        e.Pointer.Capture(null);
        if (_dragGhost is not null) DragOverlay.Children.Remove(_dragGhost);
        if (_dragGhostOverflow != null)
        {
            DragOverlay.Children.Remove(_dragGhostOverflow);
            _dragGhostOverflow = null;
        }
        foreach (var b in _dragDimmedBtns) b.Undim?.Invoke();
        _dragDimmedBtns.Clear();

        _dragEvent             = null;
        _dragGhost             = null;
        _dragSourceBtn         = null;
        _dragGhostTitleBlock   = null;
        _dragGhostTimeBlock    = null;
        _dragGhostContentStack = null;
        _isDragging = false;
    }

    private void UpdateDragGhostText(double ghostHeight, int startMin, int endMin)
    {
        if (_dragGhostTitleBlock is null) return;

        const double lineH   = 16.0;
        const double margins = 4.0;
        const double timeH   = 15.0;

        var maxTitleLines = (int)((ghostHeight - margins - timeH) / lineH);
        var showTime      = maxTitleLines >= 1;

        _dragGhostTitleBlock.MaxLines = showTime
            ? maxTitleLines
            : Math.Max(1, (int)((ghostHeight - margins) / lineH));

        var timeText = $"{startMin % 1440 / 60:D2}:{startMin % 60:D2}–{endMin % 1440 / 60:D2}:{endMin % 60:D2}";
        if (showTime)
        {
            if (_dragGhostTimeBlock is null && _dragGhostContentStack is not null)
            {
                _dragGhostTimeBlock = new TextBlock { Text = timeText, FontSize = 13, Foreground = Brushes.White, Margin = new Thickness(0, 1, 0, 0) };
                _dragGhostContentStack.Children.Add(_dragGhostTimeBlock);
            }
            else if (_dragGhostTimeBlock is not null)
            {
                _dragGhostTimeBlock.IsVisible = true;
                _dragGhostTimeBlock.Text      = timeText;
            }
        }
        else if (_dragGhostTimeBlock != null)
            _dragGhostTimeBlock.IsVisible = false;
    }

    private void UpdateResizeGhostText(double ghostHeight, int startMin, int endMin)
    {
        if (_resizeGhostTitleBlock is null) return;

        const double lineH   = 16.0;
        const double margins = 4.0;
        const double timeH   = 15.0;

        var maxTitleLines = (int)((ghostHeight - margins - timeH) / lineH);
        var showTime      = maxTitleLines >= 1;

        _resizeGhostTitleBlock.MaxLines = showTime
            ? maxTitleLines
            : Math.Max(1, (int)((ghostHeight - margins) / lineH));

        var timeText = $"{startMin % 1440 / 60:D2}:{startMin % 60:D2}–{endMin % 1440 / 60:D2}:{endMin % 60:D2}";
        if (showTime)
        {
            if (_resizeGhostTimeBlock is null && _resizeGhostContentStack is not null)
            {
                _resizeGhostTimeBlock = new TextBlock { Text = timeText, FontSize = 13, Foreground = Brushes.White, Margin = new Thickness(0, 1, 0, 0) };
                _resizeGhostContentStack.Children.Add(_resizeGhostTimeBlock);
            }
            else if (_resizeGhostTimeBlock is not null)
            {
                _resizeGhostTimeBlock.IsVisible = true;
                _resizeGhostTimeBlock.Text      = timeText;
            }
        }
        else if (_resizeGhostTimeBlock != null)
            _resizeGhostTimeBlock.IsVisible = false;
    }

    private static Border CreateGhostButton(Event ev, double height, double width)
        => CreateGhostButton(ev, height, width, out _, out _, out _);

    private static Border CreateGhostButton(Event ev, double height, double width,
        out TextBlock outTitleBlock, out TextBlock? outTimeBlock, out StackPanel outContentStack)
    {
        var color      = Color.Parse(ev.Color);
        var colorBrush = new SolidColorBrush(color);

        const double lineH   = 16.0;
        const double margins = 4.0;
        const double timeH   = 15.0;

        var maxTitleLines = (int)((height - margins - timeH) / lineH);
        var showTime      = maxTitleLines >= 1;
        var titleMaxLines = showTime ? maxTitleLines : Math.Max(1, (int)((height - margins) / lineH));

        var titleBlock = new TextBlock
        {
            Text         = ev.Title,
            FontSize     = 13,
            FontWeight   = FontWeight.SemiBold,
            Foreground   = Brushes.White,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            MaxLines     = titleMaxLines,
        };
        outTitleBlock = titleBlock;

        var contentStack = new StackPanel { Margin = new Thickness(4, 2, 3, 2) };
        outContentStack  = contentStack;
        contentStack.Children.Add(titleBlock);

        TextBlock? timeBlock = null;
        if (showTime)
        {
            timeBlock = new TextBlock
            {
                Text       = $"{ev.StartDt:HH:mm}–{ev.EndDt:HH:mm}",
                FontSize   = 13,
                Foreground = Brushes.White,
                Margin     = new Thickness(0, 1, 0, 0),
            };
            contentStack.Children.Add(timeBlock);
        }
        outTimeBlock = timeBlock;

        Grid.SetColumn(contentStack, 1);

        var innerGrid = new Grid();
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition(4, GridUnitType.Pixel));
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        innerGrid.Children.Add(new Border
        {
            Background        = colorBrush,
            CornerRadius      = new CornerRadius(2),
            Width             = 3,
            Margin            = new Thickness(2, 3, 0, 3),
            VerticalAlignment = VerticalAlignment.Stretch,
        });
        innerGrid.Children.Add(contentStack);

        return new Border
        {
            Background       = colorBrush,
            CornerRadius     = new CornerRadius(3),
            Height           = height,
            Width            = width,
            IsHitTestVisible = false,
            ClipToBounds     = true,
            Child            = innerGrid,
        };
    }

    private void UpdateTimeLine()
    {
        if (_todayTimeOverlay is null) return;
        _todayTimeOverlay.Children.Clear();

        var y   = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
        var red = new SolidColorBrush(Color.Parse("#FF3B30"));

        var line = new Border { Height = 2, Width = 10000, Background = red };
        Canvas.SetLeft(line, 0);
        Canvas.SetTop(line, y - 1);
        _todayTimeOverlay.Children.Add(line);
    }

    private static Color Darken(Color c, double f = 0.6)
        => Color.FromArgb(c.A, (byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    private static Color BlendWithWhite(Color c, byte alpha)
    {
        var a = alpha / 255.0;
        return Color.FromRgb(
            (byte)(c.R * a + 255 * (1 - a)),
            (byte)(c.G * a + 255 * (1 - a)),
            (byte)(c.B * a + 255 * (1 - a)));
    }

    private static List<List<Event>> GroupOverlapping(List<Event> events)
    {
        if (events.Count == 0) return [];
        var evs    = events.OrderBy(e => e.StartDt).ThenBy(e => e.Title).ToList();
        var groups = new List<List<Event>>();
        var cur    = new List<Event> { evs[0] };
        for (var i = 1; i < evs.Count; i++)
        {
            var ev = evs[i];
            if (cur.Any(g => ev.StartDt < g.EndDt && ev.EndDt > g.StartDt)) cur.Add(ev);
            else { groups.Add(cur); cur = [ev]; }
        }
        groups.Add(cur);
        return groups;
    }

    private static List<(Event Ev, int ColIdx, int NCols, int RightLimit)> AssignColumns(List<Event> group)
    {
        var evs     = group.OrderBy(e => e.StartDt).ThenBy(e => -(e.EndDt - e.StartDt).Ticks).ToList();
        var colEnds = new List<DateTime>();
        var evCol   = new Dictionary<Event, int>(ReferenceEqualityComparer.Instance);

        foreach (var ev in evs)
        {
            var col = -1;
            for (var i = 0; i < colEnds.Count; i++)
                if (ev.StartDt >= colEnds[i]) { col = i; break; }
            if (col == -1) { col = colEnds.Count; colEnds.Add(ev.EndDt); }
            else colEnds[col] = ev.EndDt;
            evCol[ev] = col;
        }

        var nCols  = colEnds.Count;
        var result = new List<(Event, int, int, int)>();
        foreach (var ev in group)
        {
            var colIdx     = evCol[ev];
            var rightLimit = nCols;
            foreach (var other in group)
            {
                if (!ReferenceEquals(other, ev) && evCol[other] > colIdx)
                    if (ev.StartDt < other.EndDt && ev.EndDt > other.StartDt)
                        rightLimit = Math.Min(rightLimit, evCol[other]);
            }
            result.Add((ev, colIdx, nCols, rightLimit));
        }
        return result;
    }
}
