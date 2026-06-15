using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DataBase.Core.Models;

namespace DataBase.Views.ConstructorDb.Calendar;

public partial class WeekView : UserControl
{
    private static readonly double FontSizeBase =
        Application.Current!.Resources["FontSizeBase"] is double v ? v : 12;

    public event EventHandler<CalendarRecord>?    RecordClicked;
    public event EventHandler<CalendarRecord>?    RecordMoved;
    public event EventHandler<CalendarRecord>?    RecordDeleteRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDeleteRequested;
    public event EventHandler<CalendarRecord>?    RecordDuplicateRequested;
    public event EventHandler<DateOnly>? AllDayCreationRequested;
    public event EventHandler<DateTime>? TimeSlotCreationRequested;

    private static readonly string[] DayNames = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];

    private Canvas?          _todayTimeOverlay;
    private DispatcherTimer? _timeLineTimer;

    // drag state
    private CalendarRecord?         _dragEvent;
    private Border?        _dragGhost;
    private Border?        _dragGhostOverflow;
    private TextBlock?     _dragGhostTitleBlock;
    private TextBlock?     _dragGhostTimeBlock;
    private StackPanel?    _dragGhostContentStack;
    private double         _dragGhostOriginalHeight;
    private Border?        _dragSourceBtn;
    private List<EventEntry> _dragDimmedBtns = [];
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
    private TextBlock?  _resizeGhostTitleBlock;
    private TextBlock?  _resizeGhostTimeBlock;
    private StackPanel? _resizeGhostContentStack;

    // selection (множественное: событие -> функция снятия подсветки)
    private readonly Dictionary<CalendarRecord, Action> _selections = new();
    private int? _pendingSelectId;

    // позиция последнего правого клика по сетке — для меню «Создать запись»
    private Point _gridContextPos;

    // hit-testing
    private sealed class EventEntry(CalendarRecord ev, int dayCol, int startMin, int endMin, Border btn)
    {
        public CalendarRecord Ev         = ev;
        public int     DayCol     = dayCol;
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

        // Запоминаем позицию правого клика по сетке для меню «Создать запись».
        EventsGrid.AddHandler(PointerPressedEvent, (_, pe) =>
        {
            if (pe.GetCurrentPoint(EventsGrid).Properties.IsRightButtonPressed)
                _gridContextPos = pe.GetPosition(EventsGrid);
        }, RoutingStrategies.Tunnel);
        EventsGrid.ContextMenu = _BuildGridContextMenu();

        AllDayBarCtrl.RecordClicked            += (_, ev) => RecordClicked?.Invoke(this, ev);
        AllDayBarCtrl.RecordMoved              += (_, ev) => RecordMoved?.Invoke(this, ev);
        AllDayBarCtrl.RecordDeleteRequested    += (_, ev) => RecordDeleteRequested?.Invoke(this, ev);
        AllDayBarCtrl.RecordsDeleteRequested   += (_, rs) => RecordsDeleteRequested?.Invoke(this, rs);
        AllDayBarCtrl.RecordDuplicateRequested += (_, ev) => RecordDuplicateRequested?.Invoke(this, ev);
        AllDayBarCtrl.RecordCreationRequested  += (_, d)  => AllDayCreationRequested?.Invoke(this, d);
        // Выделили в AllDayBar: при обычном клике снимаем выделение в часовой сетке
        // (одно на экран); при Shift (additive) — не трогаем, чтобы копился общий
        // набор. Обратное направление делает _ClearAllDaySelection.
        AllDayBarCtrl.SelectionRequested       += (_, additive) => { if (!additive) _Deselect(); };
        // Хуки для общего удаления из контекстного меню бара.
        AllDayBarCtrl.ExternalSelectedCount     = () => _selections.Count;
        AllDayBarCtrl.DeleteAllSelected         = _DeleteSelectedAll;

        AllDayResizeGrip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            AllDayBarCtrl.ResizeBegin(e.GetPosition(this).Y);
            e.Pointer.Capture(AllDayResizeGrip);
            e.Handled = true;
        };
        AllDayResizeGrip.PointerMoved    += (_, e) => AllDayBarCtrl.ResizeUpdate(e.GetPosition(this).Y);
        AllDayResizeGrip.PointerReleased += (_, e) =>
        {
            AllDayBarCtrl.ResizeEnd();
            e.Pointer.Capture(null);
            e.Handled = true;
        };

        SizeChanged += (_, _) => AllDayBarCtrl.InvalidateMeasure();
    }

    private EventHandler<KeyEventArgs>? _windowKeyHandler;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (e.Root is Avalonia.Controls.Window win)
        {
            _windowKeyHandler = (_, ke) =>
            {
                if (ke.Key != Key.Delete && ke.Key != Key.Back) return;
                if (!IsVisible) return;
                if (ke.Source is TextBox or AutoCompleteBox) return;

                // Общий набор: выделенные в часовой сетке + в баре «весь день».
                if (_SelectedAll().Count == 0) return;
                _DeleteSelectedAll();
                ke.Handled = true;
            };
            win.AddHandler(KeyDownEvent, _windowKeyHandler, handledEventsToo: true);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (e.Root is Avalonia.Controls.Window win && _windowKeyHandler is not null)
        {
            win.RemoveHandler(KeyDownEvent, _windowKeyHandler);
            _windowKeyHandler = null;
        }
    }

    public void Refresh(IEnumerable<CalendarRecord> events, DateTime weekStart, bool scrollToNow = false)
    {
        var monday    = weekStart.AddDays(-(((int)weekStart.DayOfWeek + 6) % 7));
        var allDays   = Enumerable.Range(0, 7).Select(i => monday.AddDays(i)).ToList();
        var allEvents = events.ToList();

        _currentDays = allDays;
        _eventEntries.Clear();
        _selections.Clear();

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
                         && dayDate <= DateOnly.FromDateTime(e.EndDt)
                         && !(DateOnly.FromDateTime(e.EndDt) == dayDate
                              && DateOnly.FromDateTime(e.StartDt) != dayDate
                              && e.EndDt.TimeOfDay == TimeSpan.Zero))
                .ToList();

            var sortedDayEvents = dayEvents
                .OrderBy(e => e.StartDt)
                .ThenBy(e => e.Title)
                .ToList();

            // список пар (entry, topHandle, bottomHandle) для пересчёта при resize
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
                        Text         = ev.DisplayTitle,
                        FontSize     = FontSizeBase,
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
                            FontSize   = FontSizeBase,
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
                    var dimBg      = new SolidColorBrush(BlendWithWhite(color, 18));
                    var entry = new EventEntry(ev, col, startMin, startMin + evH, btn)
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

                    // Контекстное меню по правому клику. Перед открытием выделяем
                    // событие (если оно ещё не в наборе), чтобы пункт действовал
                    // на видимо выделенное — как в режиме «Месяц».
                    btn.ContextMenu = _BuildEventContextMenu(entry);
                    btn.AddHandler(PointerPressedEvent, (_, pe) =>
                    {
                        if (pe.GetCurrentPoint(btn).Properties.IsRightButtonPressed
                            && !_selections.ContainsKey(entry.Ev))
                        {
                            _Deselect();
                            _ClearAllDaySelection();
                            entry.Select?.Invoke();
                            _selections[entry.Ev] = entry.Deselect ?? (() => { });
                        }
                    }, RoutingStrategies.Tunnel);
                }
            }

            void LayoutDayEntries(double canvasW)
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

            canvas.SizeChanged += (_, args) => LayoutDayEntries(args.NewSize.Width);
            // начальная раскладка — будет вызвана при первом SizeChanged, но на случай
            // если размер уже известен — применяем после layout pass
            canvas.AttachedToVisualTree += (_, _) => LayoutDayEntries(canvas.Bounds.Width > 0 ? canvas.Bounds.Width : canvas.Width);

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
                _selections[toSelect.Ev] = toSelect.Deselect ?? (() => { });
            }
            _pendingSelectId = null;
        }

        if (scrollToNow)
            Dispatcher.UIThread.Post(
                () => TimeScroll.Offset = new Vector(0, Math.Max(0, DateTime.Now.Hour * 60 - 120)),
                DispatcherPriority.Loaded);
    }

    private void _Deselect()
    {
        foreach (var unhighlight in _selections.Values.ToList()) unhighlight();
        _selections.Clear();
    }

    // Снять выделение в баре «весь день» — зовём при выделении в часовой сетке,
    // чтобы на экране было ровно одно выделенное событие.
    private void _ClearAllDaySelection() => AllDayBarCtrl.ClearSelection();

    // Общий набор выделенного по обеим зонам (сетка + бар «весь день»).
    private List<CalendarRecord> _SelectedAll() => _selections.Keys
        .Concat(AllDayBarCtrl.SelectedRecords)
        .DistinctBy(r => r.Id)
        .ToList();

    // Снять выделение в обеих зонах и удалить общий набор разом одним пакетом
    // (один — без подтверждения, несколько — с окном подтверждения).
    private void _DeleteSelectedAll()
    {
        var batch = _SelectedAll();
        if (batch.Count == 0) return;
        _Deselect();
        AllDayBarCtrl.ClearSelection();
        if (batch.Count == 1) RecordDeleteRequested?.Invoke(this, batch[0]);
        else                   RecordsDeleteRequested?.Invoke(this, batch);
    }

    private void _DeselectOne(CalendarRecord ev)
    {
        if (_selections.TryGetValue(ev, out var unhighlight))
        {
            unhighlight();
            _selections.Remove(ev);
        }
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(EventsGrid).Properties.IsLeftButtonPressed) return;

        var pos  = e.GetPosition(EventsGrid);
        var colW = EventsGrid.Bounds.Width / 7;
        if (colW <= 0) return;

        var col   = (int)(pos.X / colW);
        var entry = _eventEntries
            .Where(h => h.DayCol == col && pos.Y >= h.StartMin && pos.Y <= h.EndMin)
            .OrderByDescending(h =>
            {
                var seg  = colW / h.TotalCols;
                var left = h.SubCol * seg + 1.0;
                var right = left + h.SubColSpan * seg - 3.0;
                var localX = pos.X - col * colW;
                return localX >= left && localX <= right ? 1 : 0;
            })
            .FirstOrDefault();

        if (entry is null)
        {
            _Deselect();
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
            _resizeGhost   = CreateGhostButton(entry.Ev, entry.EndMin - entry.StartMin - 2, ghostW,
                                out _resizeGhostTitleBlock, out _resizeGhostTimeBlock, out _resizeGhostContentStack);
            Canvas.SetLeft(_resizeGhost, entry.DayCol * colW + borderLeft);
            Canvas.SetTop(_resizeGhost, entry.StartMin + 2);
            DragOverlay.Children.Add(_resizeGhost);

            entry.Btn.Opacity = 0;
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

        // Визуальный отклик на press даём только для обычного клика по
        // невыделенному событию (чтобы при последующем drag цвет уже сменился).
        // С Shift ничего не трогаем здесь — всё выделение/toggle делается на
        // release, иначе press+release дважды переключают набор и он «мигает».
        var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (!additive && !_selections.ContainsKey(entry.Ev))
        {
            _Deselect();
            _ClearAllDaySelection();
            entry.Select?.Invoke();
            _selections[entry.Ev] = entry.Deselect ?? (() => { });
        }

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
            var timeSnapped   = Math.Clamp(rawY, 0, 1440);
            _resizeGhost.IsVisible = true;

            if (_resizeFromBottom)
            {
                var resizeColDay      = DateOnly.FromDateTime(_currentDays[_resizeEntry.DayCol]);
                var actualStartMin    = _resizeEntry.Ev.StartDt.Hour * 60 + _resizeEntry.Ev.StartDt.Minute;
                var effectiveStartMin = DateOnly.FromDateTime(_resizeEntry.Ev.StartDt) == resizeColDay
                    ? actualStartMin : 0;
                var startIsPrevDay    = DateOnly.FromDateTime(_resizeEntry.Ev.StartDt) < resizeColDay;

                var isOverflowDown = rawY > 1440;
                var canExtendRight = rawPointerCol > _resizeEntry.DayCol || isOverflowDown;
                // Конец уходит в предыдущий день: мышь перешла влево ИЛИ ушла выше Y=0
                var canShrinkLeft  = (rawPointerCol < _resizeEntry.DayCol || rawY < 0)
                                     && startIsPrevDay;

                if (canShrinkLeft)
                {
                    // Конец уходит обратно в предыдущий день — основной ghost переезжает в предыдущую колонку
                    var logicalCol = rawPointerCol < _resizeEntry.DayCol
                        ? rawPointerCol : _resizeEntry.DayCol - 1;
                    var targetTime = rawPointerCol < _resizeEntry.DayCol
                        ? timeSnapped
                        : Math.Clamp(1440 + rawY, 0, 1440);
                    var targetVisible  = logicalCol >= 0 && logicalCol < _currentDays.Count;
                    var targetCol      = Math.Clamp(logicalCol, 0, _currentDays.Count - 1);
                    var targetDate     = _currentDays[_resizeEntry.DayCol].AddDays(logicalCol - _resizeEntry.DayCol);
                    var targetBorder   = logicalCol > 0 ? 1.0 : 0.0;
                    var evStartDay     = DateOnly.FromDateTime(_resizeEntry.Ev.StartDt);
                    var targetDateOnly = DateOnly.FromDateTime(targetDate);
                    var newStartMin    = evStartDay == targetDateOnly ? actualStartMin : 0;
                    var minEnd         = evStartDay == targetDateOnly ? actualStartMin + 30 : 30;
                    var newEndMin      = Math.Clamp(targetTime, minEnd, 1440);
                    var ghostH         = Math.Max(13, newEndMin - newStartMin - 2);
                    Canvas.SetLeft(_resizeGhost, logicalCol * colW + targetBorder);
                    _resizeGhost.Width  = colW - 2.0 - targetBorder;
                    Canvas.SetTop(_resizeGhost, newStartMin + 2);
                    _resizeGhost.Height = ghostH;
                    _resizeGhost.IsVisible = targetVisible;
                    UpdateResizeGhostText(ghostH, newStartMin, newEndMin);

                    if (_resizeGhostOverflow != null)
                    {
                        DragOverlay.Children.Remove(_resizeGhostOverflow);
                        _resizeGhostOverflow = null;
                    }
                }
                else if (!canExtendRight)
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
                    var ghostH = Math.Max(13, 1440 - effectiveStartMin - 2);
                    Canvas.SetTop(_resizeGhost, effectiveStartMin + 2);
                    _resizeGhost.Height = ghostH;
                    UpdateResizeGhostText(ghostH, effectiveStartMin, 1440);

                    var nextTime       = isOverflowDown && rawPointerCol <= _resizeEntry.DayCol
                        ? Math.Clamp(rawY - 1440, 0, 1425) : timeSnapped;
                    var overflowH      = Math.Max(13, nextTime - 2);
                    var logicalOverflowCol = isOverflowDown && rawPointerCol <= _resizeEntry.DayCol
                        ? _resizeEntry.DayCol + 1 : rawPointerCol;
                    var overflowVisible    = logicalOverflowCol >= 0 && logicalOverflowCol < _currentDays.Count;

                    if (!overflowVisible)
                    {
                        if (_resizeGhostOverflow != null)
                        {
                            DragOverlay.Children.Remove(_resizeGhostOverflow);
                            _resizeGhostOverflow = null;
                        }
                    }
                    else if (_resizeGhostOverflow is null)
                    {
                        _resizeGhostOverflow = CreateGhostButton(_resizeEntry.Ev, overflowH, colW - 3.0);
                        Canvas.SetLeft(_resizeGhostOverflow, logicalOverflowCol * colW + 1.0);
                        Canvas.SetTop(_resizeGhostOverflow, 2);
                        DragOverlay.Children.Add(_resizeGhostOverflow);
                    }
                    else
                    {
                        _resizeGhostOverflow.Height = overflowH;
                        Canvas.SetLeft(_resizeGhostOverflow, logicalOverflowCol * colW + 1.0);
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

                var isOverflowUp   = rawY < 0;
                var isOverflowDown = rawY > 1440;
                var canExtendLeft  = rawPointerCol < _resizeEntry.DayCol || isOverflowUp;
                var canExtendRight = (rawPointerCol > _resizeEntry.DayCol || isOverflowDown) && !canExtendLeft;

                if (canExtendRight)
                {
                    // Начало события уходит в следующую колонку
                    var logicalCol = isOverflowDown && rawPointerCol <= _resizeEntry.DayCol
                        ? _resizeEntry.DayCol + 1 : rawPointerCol;
                    var targetTime = isOverflowDown && rawPointerCol <= _resizeEntry.DayCol
                        ? Math.Clamp(rawY - 1440, 0, 1440)
                        : timeSnapped;
                    var targetVisible  = logicalCol >= 0 && logicalCol < _currentDays.Count;
                    var targetCol      = Math.Clamp(logicalCol, 0, _currentDays.Count - 1);
                    var targetDate     = _currentDays[_resizeEntry.DayCol].AddDays(logicalCol - _resizeEntry.DayCol);
                    var targetBorder   = logicalCol > 0 ? 1.0 : 0.0;
                    var evEndDay       = DateOnly.FromDateTime(_resizeEntry.Ev.EndDt);
                    var targetDateOnly = DateOnly.FromDateTime(targetDate);
                    var newEndMin      = evEndDay == targetDateOnly ? actualEndMin : 1440;
                    var maxStart       = evEndDay == targetDateOnly ? Math.Max(0, actualEndMin - 30) : 1425;
                    var newStartMin    = Math.Clamp(targetTime, 0, maxStart);
                    var ghostH         = Math.Max(13, newEndMin - newStartMin - 2);
                    Canvas.SetLeft(_resizeGhost, logicalCol * colW + targetBorder);
                    _resizeGhost.Width  = colW - 2.0 - targetBorder;
                    Canvas.SetTop(_resizeGhost, newStartMin + 2);
                    _resizeGhost.Height = ghostH;
                    _resizeGhost.IsVisible = targetVisible;
                    UpdateResizeGhostText(ghostH, newStartMin, newEndMin);

                    if (_resizeGhostOverflow != null)
                    {
                        DragOverlay.Children.Remove(_resizeGhostOverflow);
                        _resizeGhostOverflow = null;
                    }
                }
                else if (!canExtendLeft)
                {
                    // For overflow segments (event ends on a next day), max start accounts for
                    // the portion remaining on the next day so the total >= 30 min.
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
                    var ghostH = Math.Max(13, effectiveEndMin - 2);
                    Canvas.SetTop(_resizeGhost, 2);
                    _resizeGhost.Height = ghostH;
                    UpdateResizeGhostText(ghostH, 0, effectiveEndMin);

                    var overflowStart      = isOverflowUp && rawPointerCol >= _resizeEntry.DayCol
                        ? Math.Clamp(1440 + rawY, 0, 1425) : Math.Clamp(timeSnapped, 0, 1425);
                    var logicalOverflowCol = isOverflowUp && rawPointerCol >= _resizeEntry.DayCol
                        ? _resizeEntry.DayCol - 1 : rawPointerCol;
                    var overflowVisible    = logicalOverflowCol >= 0 && logicalOverflowCol < _currentDays.Count;
                    var prevLeft           = logicalOverflowCol > 0 ? logicalOverflowCol * colW + 1.0 : 0.0;

                    if (!overflowVisible)
                    {
                        if (_resizeGhostOverflow != null)
                        {
                            DragOverlay.Children.Remove(_resizeGhostOverflow);
                            _resizeGhostOverflow = null;
                        }
                    }
                    else if (_resizeGhostOverflow is null)
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

            // снимаем выделение со всех событий (drag отменяет multi-select) и
            // делаем все сегменты перетаскиваемого события полупрозрачными
            _Deselect();
            _dragDimmedBtns = _eventEntries
                .Where(e => e.Ev.Id == _dragEvent!.Id)
                .ToList();
            foreach (var b in _dragDimmedBtns)
                b.Dim?.Invoke();

            _dragGhost = CreateGhostButton(_dragEvent, ghostH, ghostW,
                out _dragGhostTitleBlock, out _dragGhostTimeBlock, out _dragGhostContentStack);
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
                    UpdateDragGhostText(_dragGhost.Height, prevDayStart, prevDayStart + duration);
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
                    UpdateDragGhostText(_dragGhost.Height, 1440 + snapped, 1440 + snapped + duration);
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
                UpdateDragGhostText(_dragGhost.Height, nextDayMin, nextDayMin + duration);
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
                UpdateDragGhostText(_dragGhost.Height, nextDayMin, nextDayMin + duration);
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
                    UpdateDragGhostText(mainH, clamped, clamped + duration);
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
                var clamped   = Math.Clamp(snapped, 0, 1425);
                var ghostH    = Math.Max(13, Math.Min(duration, 1440 - clamped) - 2);
                _dragGhost.IsVisible = true;
                Canvas.SetTop(_dragGhost, clamped);
                _dragGhost.Height = ghostH;
                UpdateDragGhostText(ghostH, clamped, clamped + duration);

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
            var timeSnapped   = Math.Clamp(rawY, 0, 1440);

            if (_resizeFromBottom)
            {
                var resizeColDay      = DateOnly.FromDateTime(_currentDays[_resizeEntry.DayCol]);
                var actualStartMin    = ev.StartDt.Hour * 60 + ev.StartDt.Minute;
                var effectiveStartMin = DateOnly.FromDateTime(ev.StartDt) == resizeColDay
                    ? actualStartMin : 0;
                var resizeDay = _currentDays[_resizeEntry.DayCol];

                var startIsPrevDay = DateOnly.FromDateTime(ev.StartDt) < resizeColDay;
                var isOverflowDown = rawY > 1440;
                var canExtendRight = rawPointerCol > _resizeEntry.DayCol || isOverflowDown;
                var canShrinkLeft  = (rawPointerCol < _resizeEntry.DayCol || rawY < 0)
                                     && startIsPrevDay;

                if (canShrinkLeft)
                {
                    var logicalCol = rawPointerCol < _resizeEntry.DayCol
                        ? rawPointerCol : _resizeEntry.DayCol - 1;
                    var targetDay      = _currentDays[_resizeEntry.DayCol].AddDays(logicalCol - _resizeEntry.DayCol);
                    var targetTime     = rawPointerCol < _resizeEntry.DayCol
                        ? timeSnapped
                        : Math.Clamp(1440 + rawY, 0, 1440);
                    var evStartDay     = DateOnly.FromDateTime(ev.StartDt);
                    var targetDateOnly = DateOnly.FromDateTime(targetDay);
                    var minEnd         = evStartDay == targetDateOnly ? actualStartMin + 30 : 30;
                    var newEndMin      = Math.Clamp(targetTime, minEnd, 1440);
                    ev.EndDt = newEndMin >= 1440
                        ? targetDay.Date.AddDays(1)
                        : new DateTime(targetDay.Year, targetDay.Month, targetDay.Day,
                                       newEndMin / 60, newEndMin % 60, 0);
                }
                else if (!canExtendRight)
                {
                    var minEnd = DateOnly.FromDateTime(ev.StartDt) == resizeColDay
                        ? effectiveStartMin + 30
                        : Math.Max(0, 30 - (1440 - actualStartMin));
                    var newEndMin = Math.Clamp(timeSnapped, minEnd, 1440);
                    ev.EndDt = newEndMin >= 1440
                        ? resizeDay.Date.AddDays(1)
                        : new DateTime(resizeDay.Year, resizeDay.Month, resizeDay.Day, newEndMin / 60, newEndMin % 60, 0);
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

                var isOverflowUp   = rawY < 0;
                var isOverflowDown = rawY > 1440;
                var canExtendLeft  = rawPointerCol < _resizeEntry.DayCol || isOverflowUp;
                var canExtendRight = (rawPointerCol > _resizeEntry.DayCol || isOverflowDown) && !canExtendLeft;

                if (canExtendRight)
                {
                    var logicalCol = isOverflowDown && rawPointerCol <= _resizeEntry.DayCol
                        ? _resizeEntry.DayCol + 1 : rawPointerCol;
                    var targetTime     = isOverflowDown && rawPointerCol <= _resizeEntry.DayCol
                        ? Math.Clamp(rawY - 1440, 0, 1440)
                        : timeSnapped;
                    var targetDay      = _currentDays[_resizeEntry.DayCol].AddDays(logicalCol - _resizeEntry.DayCol);
                    var evEndDay       = DateOnly.FromDateTime(ev.EndDt);
                    var targetDateOnly = DateOnly.FromDateTime(targetDay);
                    var maxStart       = evEndDay == targetDateOnly
                        ? Math.Max(0, actualEndMin - 30) : 1425;
                    var newStartMin    = Math.Clamp(targetTime, 0, maxStart);
                    ev.StartDt = new DateTime(targetDay.Year, targetDay.Month, targetDay.Day,
                                             newStartMin / 60, newStartMin % 60, 0);
                }
                else if (!canExtendLeft)
                {
                    var maxStart = DateOnly.FromDateTime(ev.EndDt) == resizeColDay
                        ? effectiveEndMin - 30
                        : Math.Min(1440, 1440 + actualEndMin - 30);
                    var newStartMin = Math.Clamp(timeSnapped, 0, maxStart);
                    ev.StartDt = newStartMin >= 1440
                        ? resizeDay.Date.AddDays(1)
                        : new DateTime(resizeDay.Year, resizeDay.Month, resizeDay.Day, newStartMin / 60, newStartMin % 60, 0);
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
            RecordMoved?.Invoke(this, ev);

            e.Pointer.Capture(null);
            DragOverlay.Children.Remove(_resizeGhost!);
            if (_resizeGhostOverflow != null)
            {
                DragOverlay.Children.Remove(_resizeGhostOverflow);
                _resizeGhostOverflow = null;
            }
            _resizeEntry.Btn.Opacity = 1.0;

            _isResizingEvent          = false;
            _resizeEntry              = null;
            _resizeGhost              = null;
            _resizeGhostTitleBlock    = null;
            _resizeGhostTimeBlock     = null;
            _resizeGhostContentStack  = null;
            return;
        }

        if (_dragEvent is null) return;

        var pos = e.GetPosition(EventsGrid);

        if (!_isDragging)
        {
            if (_pressClickCount >= 2)
            {
                _Deselect();
                RecordClicked?.Invoke(this, _dragEvent);
            }
            else
            {
                // find the entry to set selection frame
                var colW  = EventsGrid.Bounds.Width / 7;
                var col   = (int)(pos.X / colW);
                var entry = _eventEntries
                    .Where(h => h.DayCol == col && pos.Y >= h.StartMin && pos.Y <= h.EndMin)
                    .OrderByDescending(h =>
                    {
                        var seg    = colW / h.TotalCols;
                        var left   = h.SubCol * seg + 1.0;
                        var right  = left + h.SubColSpan * seg - 3.0;
                        var localX = pos.X - col * colW;
                        return localX >= left && localX <= right ? 1 : 0;
                    })
                    .FirstOrDefault();

                if (entry is not null)
                {
                    var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    if (additive)
                    {
                        // toggle: повторный Shift+клик по выделенному — снять.
                        // AllDayBar НЕ трогаем — Shift копит общий набор через зоны.
                        if (_selections.ContainsKey(entry.Ev)) _DeselectOne(entry.Ev);
                        else
                        {
                            entry.Select?.Invoke();
                            _selections[entry.Ev] = entry.Deselect ?? (() => { });
                        }
                    }
                    else
                    {
                        // обычный клик — оставить выделенным только это событие
                        _Deselect();
                        _ClearAllDaySelection();
                        entry.Select?.Invoke();
                        _selections[entry.Ev] = entry.Deselect ?? (() => { });
                    }
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
            RecordMoved?.Invoke(this, _dragEvent);
        }

        e.Pointer.Capture(null);
        if (_dragGhost is not null) DragOverlay.Children.Remove(_dragGhost);
        if (_dragGhostOverflow != null)
        {
            DragOverlay.Children.Remove(_dragGhostOverflow);
            _dragGhostOverflow = null;
        }
        foreach (var b in _dragDimmedBtns)
            b.Undim?.Invoke();
        _dragDimmedBtns.Clear();

        _dragEvent             = null;
        _dragGhost             = null;
        _dragSourceBtn         = null;
        _dragGhostTitleBlock   = null;
        _dragGhostTimeBlock    = null;
        _dragGhostContentStack = null;
        _isDragging    = false;
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
                _dragGhostTimeBlock = new TextBlock
                {
                    Text       = timeText,
                    FontSize   = FontSizeBase,
                    Foreground = Brushes.White,
                    Margin     = new Thickness(0, 1, 0, 0),
                };
                _dragGhostContentStack.Children.Add(_dragGhostTimeBlock);
            }
            else if (_dragGhostTimeBlock is not null)
            {
                _dragGhostTimeBlock.IsVisible = true;
                _dragGhostTimeBlock.Text      = timeText;
            }
        }
        else if (_dragGhostTimeBlock != null)
        {
            _dragGhostTimeBlock.IsVisible = false;
        }
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
                // ghost стал достаточно большим — создаём TextBlock времени динамически
                _resizeGhostTimeBlock = new TextBlock
                {
                    Text       = timeText,
                    FontSize   = FontSizeBase,
                    Foreground = Brushes.White,
                    Margin     = new Thickness(0, 1, 0, 0),
                };
                _resizeGhostContentStack.Children.Add(_resizeGhostTimeBlock);
            }
            else if (_resizeGhostTimeBlock is not null)
            {
                _resizeGhostTimeBlock.IsVisible = true;
                _resizeGhostTimeBlock.Text      = timeText;
            }
        }
        else if (_resizeGhostTimeBlock != null)
        {
            _resizeGhostTimeBlock.IsVisible = false;
        }
    }

    private static Border CreateGhostButton(CalendarRecord ev, double height, double width)
        => CreateGhostButton(ev, height, width, out _, out _, out _);

    private static Border CreateGhostButton(CalendarRecord ev, double height, double width,
        out TextBlock outTitleBlock, out TextBlock? outTimeBlock, out StackPanel outContentStack)
    {
        var color      = Color.Parse(EventColors.Display(ev.Color));
        var colorBrush = new SolidColorBrush(color);

        const double lineH   = 16.0;
        const double margins = 4.0;
        const double timeH   = 15.0;

        var maxTitleLines = (int)((height - margins - timeH) / lineH);
        var showTime      = maxTitleLines >= 1;
        var titleMaxLines = showTime
            ? maxTitleLines
            : Math.Max(1, (int)((height - margins) / lineH));

        var titleBlock = new TextBlock
        {
            Text         = ev.DisplayTitle,
            FontSize     = FontSizeBase,
            FontWeight   = FontWeight.SemiBold,
            Foreground   = Brushes.White,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            MaxLines     = titleMaxLines,
        };
        outTitleBlock = titleBlock;

        var contentStack = new StackPanel
        {
            Margin = new Thickness(4, 2, 3, 2),
        };
        outContentStack = contentStack;
        contentStack.Children.Add(titleBlock);

        TextBlock? timeBlock = null;
        if (showTime)
        {
            timeBlock = new TextBlock
            {
                Text       = $"{ev.StartDt:HH:mm}–{ev.EndDt:HH:mm}",
                FontSize   = FontSizeBase,
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
        innerGrid.Children.Add(new Border { Background = colorBrush, CornerRadius = new CornerRadius(2), Width = 3, Margin = new Thickness(2, 3, 0, 3), VerticalAlignment = VerticalAlignment.Stretch });
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

    // смешивает цвет с белым фоном чтобы убрать прозрачность (alpha blending)
    private static Color BlendWithWhite(Color c, byte alpha)
    {
        var a = alpha / 255.0;
        return Color.FromRgb(
            (byte)(c.R * a + 255 * (1 - a)),
            (byte)(c.G * a + 255 * (1 - a)),
            (byte)(c.B * a + 255 * (1 - a)));
    }

    private static List<List<CalendarRecord>> GroupOverlapping(List<CalendarRecord> events)
    {
        if (events.Count == 0) return [];
        var evs    = events.OrderBy(e => e.StartDt).ThenBy(e => e.Title).ToList();
        var groups = new List<List<CalendarRecord>>();
        var cur    = new List<CalendarRecord> { evs[0] };
        for (var i = 1; i < evs.Count; i++)
        {
            var ev = evs[i];
            if (cur.Any(g => ev.StartDt < g.EndDt && ev.EndDt > g.StartDt))
                cur.Add(ev);
            else
            {
                groups.Add(cur);
                cur = [ev];
            }
        }
        groups.Add(cur);
        return groups;
    }

    // Returns list of (ev, colIdx, nCols, rightLimit)
    private static List<(CalendarRecord Ev, int ColIdx, int NCols, int RightLimit)> AssignColumns(List<CalendarRecord> group)
    {
        var evs     = group.OrderBy(e => e.StartDt).ThenBy(e => -(e.EndDt - e.StartDt).Ticks).ToList();
        var colEnds = new List<DateTime>();
        var evCol   = new Dictionary<CalendarRecord, int>(ReferenceEqualityComparer.Instance);

        foreach (var ev in evs)
        {
            var col = -1;
            for (var i = 0; i < colEnds.Count; i++)
            {
                if (ev.StartDt >= colEnds[i]) { col = i; break; }
            }
            if (col == -1) { col = colEnds.Count; colEnds.Add(ev.EndDt); }
            else colEnds[col] = ev.EndDt;
            evCol[ev] = col;
        }

        var nCols  = colEnds.Count;
        var result = new List<(CalendarRecord, int, int, int)>();
        foreach (var ev in group)
        {
            var colIdx     = evCol[ev];
            var rightLimit = nCols;
            foreach (var other in group)
            {
                if (!ReferenceEquals(other, ev) && evCol[other] > colIdx)
                {
                    if (ev.StartDt < other.EndDt && ev.EndDt > other.StartDt)
                        rightLimit = Math.Min(rightLimit, evCol[other]);
                }
            }
            result.Add((ev, colIdx, nCols, rightLimit));
        }
        return result;
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
                FontSize            = FontSizeBase,
                FontWeight          = isToday ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground          = fg,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment       = Avalonia.Media.TextAlignment.Center,
                Padding             = new Thickness(0, 6),
            });
        }
    }

    // Меню назначается свойством ContextMenu на кнопке события — фреймворк сам
    // открывает его по правому клику и закрывает предыдущее. Состав пунктов
    // зависит от выделения на момент открытия, поэтому наполняем в Opening.
    private ContextMenu _BuildEventContextMenu(EventEntry entry)
    {
        var ev   = entry.Ev;
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();

            // Множественное выделение (это событие в общем наборе обеих зон и в нём
            // 2+): только массовое удаление всех выделенных разом — как по Delete.
            var all = _SelectedAll();
            if (all.Count > 1 && all.Any(r => r.Id == ev.Id))
            {
                menu.Items.Add(DataBase.Controls.Components.EventContextMenu.MakeDeleteItem(
                    _DeleteSelectedAll));
                return;
            }
            menu.Items.Add(DataBase.Controls.Components.EventContextMenu.MakeEditItem(
                () => { _Deselect(); RecordClicked?.Invoke(this, ev); }));
            menu.Items.Add(DataBase.Controls.Components.EventContextMenu.MakeDuplicateItem(
                () => { _Deselect(); _ClearAllDaySelection(); RecordDuplicateRequested?.Invoke(this, ev); }));
            menu.Items.Add(DataBase.Controls.Components.EventContextMenu.MakeDeleteItem(
                () => { _Deselect(); _ClearAllDaySelection(); RecordDeleteRequested?.Invoke(this, ev); }));
        };
        return menu;
    }

    // Меню пустого места сетки: «Создать запись» в выбранный день/время.
    // Если правый клик попал на событие — отменяем открытие, чтобы показалось
    // меню события (оно висит на своей кнопке отдельным ContextMenu).
    private ContextMenu _BuildGridContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opening += (_, args) =>
        {
            var colW = EventsGrid.Bounds.Width / 7;
            if (colW <= 0 || _currentDays.Count == 0)
            {
                if (args is System.ComponentModel.CancelEventArgs cancel) cancel.Cancel = true;
                return;
            }

            var col = (int)(_gridContextPos.X / colW);
            var onEvent = _eventEntries.Any(h =>
            {
                if (h.DayCol != col) return false;
                if (_gridContextPos.Y < h.StartMin || _gridContextPos.Y > h.EndMin) return false;
                var seg    = colW / h.TotalCols;
                var left   = h.SubCol * seg + 1.0;
                var right  = left + h.SubColSpan * seg - 3.0;
                var localX = _gridContextPos.X - col * colW;
                return localX >= left && localX <= right;
            });

            if (onEvent || col < 0 || col >= _currentDays.Count)
            {
                if (args is System.ComponentModel.CancelEventArgs cancel) cancel.Cancel = true;
                return;
            }

            // Правый клик по пустому месту снимает выделение группы — как левый.
            _Deselect();

            var minutes = (int)(_gridContextPos.Y / 30) * 30;
            var day     = _currentDays[col];
            var dt      = new DateTime(day.Year, day.Month, day.Day, minutes / 60, minutes % 60, 0);

            menu.Items.Clear();
            menu.Items.Add(DataBase.Controls.Components.EventContextMenu.MakeCreateItem(
                () => TimeSlotCreationRequested?.Invoke(this, dt)));
        };
        return menu;
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
