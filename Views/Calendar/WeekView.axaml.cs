using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MyPlanner.Core.Models;

namespace MyPlanner.Views.Calendar;

public partial class WeekView : UserControl
{
    public event EventHandler<Event>?    EventClicked;
    public event EventHandler<Event>?    EventMoved;
    public event EventHandler<DateOnly>? AllDayCreationRequested;
    public event EventHandler<DateTime>? TimeSlotCreationRequested;

    private static readonly string[] DayNames = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];

    private Canvas?          _todayTimeOverlay;
    private DispatcherTimer? _timeLineTimer;

    // drag state
    private Event?         _dragEvent;
    private Border?        _dragGhost;
    private Border?        _dragGhostOverflow;
    private double         _dragGhostOriginalHeight;
    private Border?        _dragSourceBtn;
    private List<Border>   _dragDimmedBtns = [];
    private Point          _dragStartPos;
    private double         _dragOffsetY;
    private bool           _isDragging;
    private int            _pressClickCount;
    private int            _dragSourceCol;
    private int            _dragSourceStartMin;
    private List<DateTime> _currentDays = [];

    // resize state
    private bool        _isResizingEvent;
    private EventEntry? _resizeEntry;
    private bool        _resizeFromBottom;
    private Border?     _resizeGhost;
    private Border?     _resizeGhostOverflow;

    // selection
    private Action? _deselectCurrent;
    private int?    _pendingSelectId;

    // hit-testing
    private sealed class EventEntry(Event ev, int dayCol, int startMin, int endMin, Border btn)
    {
        public Event   Ev       = ev;
        public int     DayCol   = dayCol;
        public int     StartMin = startMin;
        public int     EndMin   = endMin;
        public Border  Btn      = btn;
        public Action? Select;
        public Action? Deselect;
    }
    private readonly List<EventEntry> _eventEntries = [];

    public WeekView()
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

        AllDayBarCtrl.EventClicked            += (_, ev) => EventClicked?.Invoke(this, ev);
        AllDayBarCtrl.EventMoved              += (_, ev) => EventMoved?.Invoke(this, ev);
        AllDayBarCtrl.EventCreationRequested  += (_, d)  => AllDayCreationRequested?.Invoke(this, d);
    }

    public void Refresh(IEnumerable<Event> events, DateTime weekStart, bool scrollToNow = false)
    {
        var monday    = weekStart.AddDays(-(((int)weekStart.DayOfWeek + 6) % 7));
        var allDays   = Enumerable.Range(0, 7).Select(i => monday.AddDays(i)).ToList();
        var allEvents = events.ToList();

        _currentDays = allDays;
        _eventEntries.Clear();
        _deselectCurrent = null;

        AllDayBarCtrl.SetData(allDays.Select(DateOnly.FromDateTime), allEvents);
        BuildHeader(allDays);

        WeekColumns.Children.Clear();
        _todayTimeOverlay = null;
        var gridLineBrush = new SolidColorBrush(Color.Parse("#E8E8E8"));

        for (var col = 0; col < allDays.Count; col++)
        {
            var day     = allDays[col];
            var canvas  = new Canvas { Background = Brushes.Transparent };
            var dayDate = DateOnly.FromDateTime(day);
            var dayEvents = allEvents
                .Where(e => !e.AllDay
                         && (e.EndDt - e.StartDt).TotalSeconds < 86400
                         && DateOnly.FromDateTime(e.StartDt) <= dayDate
                         && dayDate <= DateOnly.FromDateTime(e.EndDt))
                .ToList();

            foreach (var ev in dayEvents)
            {
                var startMin = DateOnly.FromDateTime(ev.StartDt) == dayDate
                    ? ev.StartDt.Hour * 60 + ev.StartDt.Minute
                    : 0;
                var effectiveEndMin = DateOnly.FromDateTime(ev.EndDt) == dayDate
                    ? ev.EndDt.Hour * 60 + ev.EndDt.Minute
                    : 1440;
                var evH = Math.Max(22, effectiveEndMin - startMin);
                var evHVisible = evH - 2; // 2px нижний зазор между событиями
                var color      = Color.Parse(ev.Color);
                var normalBg   = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B));
                var hoverBg    = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B));
                var colorBrush = new SolidColorBrush(color);
                var darkBrush  = new SolidColorBrush(Darken(color));

                var titleBlock = new TextBlock
                {
                    Text              = ev.Title,
                    FontSize          = 13,
                    FontWeight        = FontWeight.SemiBold,
                    Foreground        = darkBrush,
                    TextTrimming      = Avalonia.Media.TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin            = new Thickness(4, 2, 3, 2),
                };
                Grid.SetColumn(titleBlock, 1);

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
                innerGrid.Children.Add(titleBlock);
                innerGrid.Children.Add(selFrame);

                var btn = new Border
                {
                    Background   = normalBg,
                    CornerRadius = new CornerRadius(3),
                    Height       = evHVisible,
                    Cursor       = new Cursor(StandardCursorType.Hand),
                    Child        = innerGrid,
                };

                var isSelected = false;
                btn.PointerEntered += (_, _) => { if (!isSelected) btn.Background = hoverBg; };
                btn.PointerExited  += (_, _) => { if (!isSelected) btn.Background = normalBg; };

                Canvas.SetTop(btn, startMin + 2);
                Canvas.SetLeft(btn, 0);
                canvas.Children.Add(btn);

                var topHandle = new Border
                {
                    Height           = 6,
                    Background       = Brushes.Transparent,
                    Cursor           = new Cursor(StandardCursorType.SizeNorthSouth),
                    IsHitTestVisible = true,
                };
                Canvas.SetTop(topHandle, startMin + 2);
                Canvas.SetLeft(topHandle, 0);
                canvas.Children.Add(topHandle);

                var bottomHandle = new Border
                {
                    Height           = 6,
                    Background       = Brushes.Transparent,
                    Cursor           = new Cursor(StandardCursorType.SizeNorthSouth),
                    IsHitTestVisible = true,
                };
                Canvas.SetTop(bottomHandle, startMin + 2 + evHVisible - 6);
                Canvas.SetLeft(bottomHandle, 0);
                canvas.Children.Add(bottomHandle);

                var solidBrush = new SolidColorBrush(color);
                var entry = new EventEntry(ev, col, startMin, startMin + evH, btn)
                {
                    Select = () =>
                    {
                        isSelected            = true;
                        btn.Background        = solidBrush;
                        titleBlock.Foreground = Brushes.White;
                    },
                    Deselect = () =>
                    {
                        isSelected            = false;
                        btn.Background        = normalBg;
                        titleBlock.Foreground = darkBrush;
                    },
                };
                _eventEntries.Add(entry);
            }

            canvas.SizeChanged += (_, args) =>
            {
                var w = args.NewSize.Width - 2;
                foreach (var child in canvas.Children.OfType<Border>())
                    child.Width = w;
            };

            var dayGrid = new Grid();
            if (col >= 5)
                dayGrid.Background = new SolidColorBrush(Color.FromArgb(7, 0, 0, 0));
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
        var colW = EventsGrid.Bounds.Width / 7;
        if (colW <= 0) return;

        var col   = (int)(pos.X / colW);
        var entry = _eventEntries.FirstOrDefault(h =>
            h.DayCol == col && pos.Y >= h.StartMin && pos.Y <= h.EndMin);

        if (entry is null)
        {
            DoDeselect();
            if (e.ClickCount >= 2 && col >= 0 && col < _currentDays.Count)
            {
                var minutes = (int)(pos.Y / 30) * 30;
                var day     = _currentDays[col];
                var dt      = new DateTime(day.Year, day.Month, day.Day,
                                           minutes / 60, minutes % 60, 0);
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

            var borderLeft = entry.DayCol > 0 ? 1.0 : 0.0;
            var ghostW     = colW - 2.0 - borderLeft;
            _resizeGhost   = CreateGhostButton(entry.Ev, entry.EndMin - entry.StartMin - 2, ghostW);
            Canvas.SetLeft(_resizeGhost, entry.DayCol * colW + borderLeft);
            Canvas.SetTop(_resizeGhost, entry.StartMin + 2);
            DragOverlay.Children.Add(_resizeGhost);

            entry.Btn.Opacity = 0.45;
            e.Pointer.Capture(EventsGrid);
            e.Handled = true;
            return;
        }

        _dragEvent           = entry.Ev;
        _dragSourceBtn       = entry.Btn;
        _dragStartPos        = pos;
        _isDragging          = false;
        _pressClickCount     = e.ClickCount;
        _dragSourceCol       = entry.DayCol;
        _dragSourceStartMin  = entry.StartMin;

        // Если схватили overflow-сегмент (событие началось в предыдущем дне),
        // учитываем виртуальное начало события (отрицательная Y) для плавного перетаскивания
        var evStartDay  = DateOnly.FromDateTime(entry.Ev.StartDt);
        var colDay      = DateOnly.FromDateTime(_currentDays[entry.DayCol]);
        if (evStartDay < colDay)
        {
            var prevDayMinutes = 1440 - (entry.Ev.StartDt.Hour * 60 + entry.Ev.StartDt.Minute);
            _dragOffsetY = pos.Y + prevDayMinutes;
        }
        else
        {
            _dragOffsetY = pos.Y - (entry.StartMin + 2);
        }

        // сразу меняем цвет как при выделении
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
            var colW       = EventsGrid.Bounds.Width / _currentDays.Count;
            var rawPointerCol = colW > 0 ? (int)Math.Floor(pos.X / colW) : _resizeEntry.DayCol;
            var pointerCol    = Math.Clamp(rawPointerCol, 0, _currentDays.Count - 1);
            var rawY          = (int)(pos.Y / 15) * 15;
            var timeSnapped   = Math.Clamp(rawY, 0, 1425);

            if (_resizeFromBottom)
            {
                // Если событие началось в предыдущем дне (overflow-сегмент), граница = 0
                var resizeColDay      = DateOnly.FromDateTime(_currentDays[_resizeEntry.DayCol]);
                var actualStartMin    = _resizeEntry.Ev.StartDt.Hour * 60 + _resizeEntry.Ev.StartDt.Minute;
                var effectiveStartMin = DateOnly.FromDateTime(_resizeEntry.Ev.StartDt) == resizeColDay
                    ? actualStartMin : 0;

                var isOverflowDown    = rawY > 1440;
                var canExtendRight    = rawPointerCol > _resizeEntry.DayCol || isOverflowDown;

                if (!canExtendRight)
                {
                    var newEndMin = Math.Clamp(timeSnapped, effectiveStartMin + 15, 1440);
                    Canvas.SetTop(_resizeGhost, effectiveStartMin + 2);
                    _resizeGhost.Height = Math.Max(13, newEndMin - effectiveStartMin - 2);

                    if (_resizeGhostOverflow != null)
                    {
                        DragOverlay.Children.Remove(_resizeGhostOverflow);
                        _resizeGhostOverflow = null;
                    }
                }
                else
                {
                    Canvas.SetTop(_resizeGhost, effectiveStartMin + 2);
                    _resizeGhost.Height = Math.Max(13, 1440 - effectiveStartMin - 2);

                    var nextTime       = isOverflowDown && rawPointerCol <= _resizeEntry.DayCol
                        ? Math.Clamp(rawY - 1440, 0, 1425) : timeSnapped;
                    var overflowH      = Math.Max(13, nextTime - 2);
                    var overflowDispCol = Math.Clamp(
                        isOverflowDown && rawPointerCol <= _resizeEntry.DayCol
                            ? _resizeEntry.DayCol + 1 : rawPointerCol,
                        0, _currentDays.Count - 1);

                    if (_resizeGhostOverflow is null)
                    {
                        _resizeGhostOverflow = CreateGhostButton(_resizeEntry.Ev, overflowH, colW - 3.0);
                        Canvas.SetLeft(_resizeGhostOverflow, overflowDispCol * colW + 1.0);
                        Canvas.SetTop(_resizeGhostOverflow, 2);
                        DragOverlay.Children.Add(_resizeGhostOverflow);
                    }
                    else
                    {
                        _resizeGhostOverflow.Height = overflowH;
                        Canvas.SetLeft(_resizeGhostOverflow, overflowDispCol * colW + 1.0);
                    }
                }
            }
            else
            {
                // Если событие заканчивается в следующем дне (overflow), граница = 1440
                var resizeColDay    = DateOnly.FromDateTime(_currentDays[_resizeEntry.DayCol]);
                var actualEndMin    = _resizeEntry.Ev.EndDt.Hour * 60 + _resizeEntry.Ev.EndDt.Minute;
                var effectiveEndMin = DateOnly.FromDateTime(_resizeEntry.Ev.EndDt) == resizeColDay
                    ? actualEndMin : 1440;

                var isOverflowUp  = rawY < 0;
                var canExtendLeft = rawPointerCol < _resizeEntry.DayCol || isOverflowUp;

                if (!canExtendLeft)
                {
                    var newStartMin = Math.Clamp(timeSnapped, 0, effectiveEndMin - 15);
                    Canvas.SetTop(_resizeGhost, newStartMin + 2);
                    _resizeGhost.Height = Math.Max(13, effectiveEndMin - newStartMin - 2);

                    if (_resizeGhostOverflow != null)
                    {
                        DragOverlay.Children.Remove(_resizeGhostOverflow);
                        _resizeGhostOverflow = null;
                    }
                }
                else
                {
                    Canvas.SetTop(_resizeGhost, 2);
                    _resizeGhost.Height = Math.Max(13, effectiveEndMin - 2);

                    var overflowStart   = isOverflowUp && rawPointerCol >= _resizeEntry.DayCol
                        ? Math.Clamp(1440 + rawY, 0, 1425) : Math.Clamp(timeSnapped, 0, 1425);
                    var overflowDispCol = Math.Clamp(
                        isOverflowUp && rawPointerCol >= _resizeEntry.DayCol
                            ? _resizeEntry.DayCol - 1 : rawPointerCol,
                        0, _currentDays.Count - 1);
                    var prevLeft        = overflowDispCol > 0 ? overflowDispCol * colW + 1.0 : 0.0;

                    if (_resizeGhostOverflow is null)
                    {
                        _resizeGhostOverflow = CreateGhostButton(_resizeEntry.Ev,
                            Math.Max(13, 1440 - overflowStart - 2), colW - 3.0);
                        Canvas.SetLeft(_resizeGhostOverflow, prevLeft);
                        Canvas.SetTop(_resizeGhostOverflow, overflowStart + 2);
                        DragOverlay.Children.Add(_resizeGhostOverflow);
                    }
                    else
                    {
                        _resizeGhostOverflow.Height = Math.Max(13, 1440 - overflowStart - 2);
                        Canvas.SetTop(_resizeGhostOverflow, overflowStart + 2);
                        Canvas.SetLeft(_resizeGhostOverflow, prevLeft);
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

            var colW       = EventsGrid.Bounds.Width / 7;
            var borderLeft = _dragSourceCol > 0 ? 1.0 : 0.0;
            var ghostW     = colW - 2.0 - borderLeft;
            var ghostH     = _dragSourceBtn.Bounds.Height;
            _dragGhostOriginalHeight = ghostH;

            // снимаем выделение с источника и делаем все сегменты события полупрозрачными
            _deselectCurrent?.Invoke();
            _deselectCurrent = null;
            _dragDimmedBtns = _eventEntries
                .Where(e => e.Ev.Id == _dragEvent!.Id)
                .Select(e => e.Btn)
                .ToList();
            foreach (var b in _dragDimmedBtns)
                b.Opacity = 0.45;

            _dragGhost = CreateGhostButton(_dragEvent, ghostH, ghostW);
            Canvas.SetLeft(_dragGhost, _dragSourceCol * colW + borderLeft);
            Canvas.SetTop(_dragGhost, _dragSourceStartMin + 2);
            DragOverlay.Children.Add(_dragGhost);
        }

        if (_dragGhost is not null)
        {
            var colW      = EventsGrid.Bounds.Width / 7;
            var rawCol    = colW > 0 ? (int)Math.Floor(pos.X / colW) : 0;
            var col       = Math.Clamp(rawCol, 0, _currentDays.Count - 1);
            var rawY      = pos.Y - _dragOffsetY; // может быть отрицательным (overflow из предыдущего дня)
            var duration  = (int)(_dragEvent!.EndDt - _dragEvent.StartDt).TotalMinutes;
            var snapped   = (int)Math.Floor(rawY / 15) * 15;

            var borderLeft = col > 0 ? 1.0 : 0.0;
            Canvas.SetLeft(_dragGhost, col * colW + borderLeft);

            if (snapped < 0 && col > 0)
            {
                // Событие началось в предыдущей колонке
                var prevDayStart     = 1440 + snapped;
                var minutesInThisDay = duration + snapped; // может быть <= 0
                var minutesInPrevDay = Math.Min(-snapped, duration);
                var prevCol          = col - 1;
                var prevBorder       = prevCol > 0 ? 1.0 : 0.0;

                // Главный ghost в текущей колонке — скрываем если реальный размер < 13px
                if (minutesInThisDay >= 13)
                {
                    Canvas.SetTop(_dragGhost, 2);
                    _dragGhost.Height    = minutesInThisDay - 2;
                    _dragGhost.IsVisible = true;
                }
                else
                {
                    _dragGhost.IsVisible = false;
                }

                // Overflow ghost в предыдущей колонке — скрываем если < 13px
                var overflowH = minutesInPrevDay - 2;
                if (overflowH >= 13)
                {
                    if (_dragGhostOverflow is null)
                    {
                        _dragGhostOverflow = CreateGhostButton(_dragEvent, overflowH, colW - 3.0);
                        Canvas.SetLeft(_dragGhostOverflow, prevCol * colW + prevBorder);
                        Canvas.SetTop(_dragGhostOverflow, prevDayStart + 2);
                        DragOverlay.Children.Add(_dragGhostOverflow);
                    }
                    else
                    {
                        _dragGhostOverflow.Height = overflowH;
                        Canvas.SetLeft(_dragGhostOverflow, prevCol * colW + prevBorder);
                        Canvas.SetTop(_dragGhostOverflow, prevDayStart + 2);
                    }
                }
                else if (_dragGhostOverflow != null)
                {
                    DragOverlay.Children.Remove(_dragGhostOverflow);
                    _dragGhostOverflow = null;
                }
            }
            else if (snapped < 0 && col == 0)
            {
                // Понедельник → предыдущее воскресенье: col-1 вне экрана.
                // Показываем только уменьшающийся хвост в понедельнике (как Thu→Wed),
                // overflow ghost не показываем — воскресенье предыдущей недели невидимо.
                var minutesInThisDay = duration + snapped; // уменьшается по мере ухода в воскресенье
                Canvas.SetLeft(_dragGhost, 0);
                if (minutesInThisDay >= 13)
                {
                    Canvas.SetTop(_dragGhost, 2);
                    _dragGhost.Height    = minutesInThisDay - 2;
                    _dragGhost.IsVisible = true;
                }
                else
                {
                    _dragGhost.IsVisible = false;
                }
                if (_dragGhostOverflow != null)
                {
                    DragOverlay.Children.Remove(_dragGhostOverflow);
                    _dragGhostOverflow = null;
                }
            }
            else if (snapped >= 1440 && col < _currentDays.Count - 1)
            {
                // Курсор утащен ниже границы canvas — переходим в следующую колонку
                var nextCol    = col + 1;
                var nextBorder = 1.0;
                var nextDayMin = Math.Clamp(snapped - 1440, 0, 1425);
                Canvas.SetLeft(_dragGhost, nextCol * colW + nextBorder);
                Canvas.SetTop(_dragGhost, nextDayMin);
                _dragGhost.Height    = Math.Max(13, Math.Min(duration, 1440 - nextDayMin) - 2);
                _dragGhost.IsVisible = true;
                if (_dragGhostOverflow != null)
                {
                    DragOverlay.Children.Remove(_dragGhostOverflow);
                    _dragGhostOverflow = null;
                }
            }
            else if (snapped >= 1440 && col == _currentDays.Count - 1)
            {
                // Воскресенье → следующий понедельник: col+1 вне экрана,
                // показываем ghost сверху той же колонки чтобы сигнализировать переход
                var nextDayMin = Math.Clamp(snapped - 1440, 0, 1425);
                Canvas.SetLeft(_dragGhost, col * colW + borderLeft);
                Canvas.SetTop(_dragGhost, nextDayMin);
                _dragGhost.Height    = Math.Max(13, Math.Min(duration, 1440 - nextDayMin) - 2);
                _dragGhost.IsVisible = true;
                if (_dragGhostOverflow != null)
                {
                    DragOverlay.Children.Remove(_dragGhostOverflow);
                    _dragGhostOverflow = null;
                }
            }
            else if (snapped >= 0 && snapped + duration > 1440 && col < _currentDays.Count - 1)
            {
                // Событие уходит в следующую колонку
                var clamped   = Math.Clamp(snapped, 0, 1425);
                var mainH     = 1440 - clamped - 2;
                var overflowH = clamped + duration - 1440 - 2;

                // Overflow ghost начинается с полуночи следующей колонки (Y=2),
                // но обрезается снизу если ScrollViewer прокручен — показываем
                // только ту часть, что попадает в видимую область
                var scrollOffset     = TimeScroll.Offset.Y;
                var overflowTop      = 2.0;
                var overflowVisTop   = Math.Max(overflowTop, scrollOffset);
                var overflowHVisible = overflowH - (overflowVisTop - overflowTop);

                // Главный ghost — скрываем если < 13px
                if (mainH >= 13)
                {
                    _dragGhost.IsVisible = true;
                    Canvas.SetTop(_dragGhost, clamped);
                    _dragGhost.Height = mainH;
                }
                else
                {
                    _dragGhost.IsVisible = false;
                }

                if (overflowHVisible >= 13)
                {
                    if (_dragGhostOverflow is null)
                    {
                        _dragGhostOverflow = CreateGhostButton(_dragEvent, overflowHVisible, colW - 3.0);
                        Canvas.SetLeft(_dragGhostOverflow, (col + 1) * colW + 1.0);
                        Canvas.SetTop(_dragGhostOverflow, overflowVisTop);
                        DragOverlay.Children.Add(_dragGhostOverflow);
                    }
                    else
                    {
                        _dragGhostOverflow.Height = overflowHVisible;
                        Canvas.SetLeft(_dragGhostOverflow, (col + 1) * colW + 1.0);
                        Canvas.SetTop(_dragGhostOverflow, overflowVisTop);
                    }
                }
                else if (_dragGhostOverflow != null)
                {
                    DragOverlay.Children.Remove(_dragGhostOverflow);
                    _dragGhostOverflow = null;
                }
            }
            else
            {
                // Без overflow
                var clamped = Math.Clamp(snapped, 0, 1425);
                _dragGhost.IsVisible = true;
                Canvas.SetTop(_dragGhost, clamped);
                _dragGhost.Height = Math.Max(13, Math.Min(duration, 1440 - clamped) - 2);

                if (_dragGhostOverflow != null)
                {
                    DragOverlay.Children.Remove(_dragGhostOverflow);
                    _dragGhostOverflow = null;
                }
            }
        }
    }

    private void OnGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isResizingEvent && _resizeEntry != null)
        {
            var resizePos = e.GetPosition(EventsGrid);
            var snapped   = Math.Clamp((int)(resizePos.Y / 15) * 15, 0, 1440);
            var ev      = _resizeEntry.Ev;

            var colW          = EventsGrid.Bounds.Width / _currentDays.Count;
            var rawPointerCol = colW > 0 ? (int)Math.Floor(resizePos.X / colW) : _resizeEntry.DayCol;
            var rawY          = (int)(resizePos.Y / 15) * 15;
            var timeSnapped   = Math.Clamp(rawY, 0, 1425);

            if (_resizeFromBottom)
            {
                var resizeColDay      = DateOnly.FromDateTime(_currentDays[_resizeEntry.DayCol]);
                var actualStartMin    = ev.StartDt.Hour * 60 + ev.StartDt.Minute;
                var effectiveStartMin = DateOnly.FromDateTime(ev.StartDt) == resizeColDay
                    ? actualStartMin : 0;
                var resizeDay = _currentDays[_resizeEntry.DayCol];

                var isOverflowDown = rawY > 1440;
                var canExtendRight = rawPointerCol > _resizeEntry.DayCol || isOverflowDown;

                if (!canExtendRight)
                {
                    var newEndMin = Math.Clamp(timeSnapped, effectiveStartMin + 15, 1440);
                    ev.EndDt = new DateTime(resizeDay.Year, resizeDay.Month, resizeDay.Day,
                                            newEndMin / 60, newEndMin % 60, 0);
                }
                else
                {
                    int dayOffset;
                    int endTimeMin;
                    if (rawPointerCol > _resizeEntry.DayCol)
                    {
                        dayOffset  = rawPointerCol - _resizeEntry.DayCol + (isOverflowDown ? 1 : 0);
                        endTimeMin = isOverflowDown ? Math.Clamp(rawY - 1440, 0, 1425) : timeSnapped;
                    }
                    else
                    {
                        dayOffset  = 1;
                        endTimeMin = Math.Clamp(rawY - 1440, 0, 1425);
                    }
                    var endDay = _currentDays[_resizeEntry.DayCol].AddDays(dayOffset);
                    ev.EndDt = new DateTime(endDay.Year, endDay.Month, endDay.Day,
                                            endTimeMin / 60, endTimeMin % 60, 0);
                }
            }
            else
            {
                var resizeColDay    = DateOnly.FromDateTime(_currentDays[_resizeEntry.DayCol]);
                var actualEndMin    = ev.EndDt.Hour * 60 + ev.EndDt.Minute;
                var effectiveEndMin = DateOnly.FromDateTime(ev.EndDt) == resizeColDay
                    ? actualEndMin : 1440;
                var resizeDay = _currentDays[_resizeEntry.DayCol];

                var isOverflowUp  = rawY < 0;
                var canExtendLeft = rawPointerCol < _resizeEntry.DayCol || isOverflowUp;

                if (!canExtendLeft)
                {
                    var newStartMin = Math.Clamp(timeSnapped, 0, effectiveEndMin - 15);
                    ev.StartDt = new DateTime(resizeDay.Year, resizeDay.Month, resizeDay.Day,
                                              newStartMin / 60, newStartMin % 60, 0);
                }
                else
                {
                    int dayOffset;
                    int startTimeMin;
                    if (rawPointerCol < _resizeEntry.DayCol)
                    {
                        dayOffset    = _resizeEntry.DayCol - rawPointerCol + (isOverflowUp ? 1 : 0);
                        startTimeMin = isOverflowUp ? Math.Clamp(1440 + rawY, 0, 1425) : Math.Clamp(timeSnapped, 0, 1425);
                    }
                    else
                    {
                        dayOffset    = 1;
                        startTimeMin = Math.Clamp(1440 + rawY, 0, 1425);
                    }
                    var startDay = _currentDays[_resizeEntry.DayCol].AddDays(-dayOffset);
                    ev.StartDt = new DateTime(startDay.Year, startDay.Month, startDay.Day,
                                              startTimeMin / 60, startTimeMin % 60, 0);
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

            _isResizingEvent = false;
            _resizeEntry     = null;
            _resizeGhost     = null;
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
                // find the entry to set selection frame
                var colW  = EventsGrid.Bounds.Width / 7;
                var col   = (int)(pos.X / colW);
                var entry = _eventEntries.FirstOrDefault(h =>
                    h.DayCol == col && pos.Y >= h.StartMin && pos.Y <= h.EndMin);

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
            var colW     = EventsGrid.Bounds.Width / 7;
            var rawCol   = colW > 0 ? (int)Math.Floor(pos.X / colW) : 0;
            var rawY     = pos.Y - _dragOffsetY;
            var duration = (int)(_dragEvent.EndDt - _dragEvent.StartDt).TotalMinutes;
            var snapped  = (int)Math.Floor(rawY / 15) * 15;

            // AddDays корректно работает с отрицательными rawCol (предыдущая неделя)
            // и rawCol >= 7 (следующая неделя)
            DateTime newStart;
            if (snapped < 0)
            {
                var prevDay    = _currentDays[0].AddDays(rawCol - 1);
                var prevDayMin = Math.Clamp(1440 + snapped, 0, 1425);
                newStart = new DateTime(prevDay.Year, prevDay.Month, prevDay.Day,
                                        prevDayMin / 60, prevDayMin % 60, 0);
            }
            else if (snapped >= 1440)
            {
                var nextDay    = _currentDays[0].AddDays(rawCol + 1);
                var nextDayMin = Math.Clamp(snapped - 1440, 0, 1425);
                newStart = new DateTime(nextDay.Year, nextDay.Month, nextDay.Day,
                                        nextDayMin / 60, nextDayMin % 60, 0);
            }
            else
            {
                var clamped = Math.Clamp(snapped, 0, 1425);
                var day     = _currentDays[0].AddDays(rawCol);
                newStart = new DateTime(day.Year, day.Month, day.Day,
                                        clamped / 60, clamped % 60, 0);
            }

            _dragEvent.StartDt = newStart;
            _dragEvent.EndDt   = newStart.AddMinutes(duration);

            _pendingSelectId = _dragEvent.Id;
            EventMoved?.Invoke(this, _dragEvent);
        }

        e.Pointer.Capture(null);
        if (_dragGhost is not null) DragOverlay.Children.Remove(_dragGhost);
        if (_dragGhostOverflow != null)
        {
            DragOverlay.Children.Remove(_dragGhostOverflow);
            _dragGhostOverflow = null;
        }
        foreach (var b in _dragDimmedBtns)
            b.Opacity = 1.0;
        _dragDimmedBtns.Clear();

        _dragEvent     = null;
        _dragGhost     = null;
        _dragSourceBtn = null;
        _isDragging    = false;
    }

    private static Border CreateGhostButton(Event ev, double height, double width)
    {
        var color      = Color.Parse(ev.Color);
        var colorBrush = new SolidColorBrush(color);

        var titleBlock = new TextBlock
        {
            Text              = ev.Title,
            FontSize          = 13,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = Brushes.White,
            TextTrimming      = Avalonia.Media.TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top,
            Margin            = new Thickness(4, 2, 3, 2),
        };
        Grid.SetColumn(titleBlock, 1);

        var innerGrid = new Grid();
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition(4, GridUnitType.Pixel));
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        innerGrid.Children.Add(new Border { Background = colorBrush, CornerRadius = new CornerRadius(2), Width = 3, Margin = new Thickness(2, 3, 0, 3), VerticalAlignment = VerticalAlignment.Stretch });
        innerGrid.Children.Add(titleBlock);

        return new Border
        {
            Background       = colorBrush,
            CornerRadius     = new CornerRadius(3),
            Height           = height,
            Width            = width,
            IsHitTestVisible = false,
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
