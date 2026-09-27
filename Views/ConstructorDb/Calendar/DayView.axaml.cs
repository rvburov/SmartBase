using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SmartBase.Core.Calendar;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Views.ConstructorDb.Calendar;

public partial class DayView : UserControl
{
    // Сосед дня при drag/resize за полночь: при 5-дневке (рабочая неделя) — ближайший
    // ВИДИМЫЙ день (после пятницы — понедельник, перед понедельником — пятница).
    private static DateTime NextDay(DateTime d) => CalendarMath.NextVisibleDay(d, CalendarSettings.FiveDayWeek);
    private static DateTime PrevDay(DateTime d) => CalendarMath.PrevVisibleDay(d, CalendarSettings.FiveDayWeek);

    // Размеры/веса шрифта — из токенов Typography.axaml (резолв ресурса в рантайме).
    // Свежий размер из Application.Resources — меняется живьем при изменении FontScale.
    private static double FontSizeBase =>
        (double)Application.Current!.FindResource("FontSizeBase")!;

    private static readonly FontWeight WeightBold = (FontWeight)Application.Current!.FindResource("FontWeightBold")!;

    public event EventHandler<CalendarRecord>?    RecordClicked;
    public event EventHandler<CalendarRecord>?    RecordMoved;
    public event EventHandler<CalendarRecord>?    RecordDeleteRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDeleteRequested;
    public event EventHandler<CalendarRecord>?    RecordDuplicateRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDuplicateRequested;
    public event EventHandler<DateOnly>? AllDayCreationRequested;
    public event EventHandler<DateTime>? TimeSlotCreationRequested;

    private Canvas?          _todayTimeOverlay;
    private DispatcherTimer? _timeLineTimer;

    // drag state
    private CalendarRecord?            _dragEvent;
    private Border?           _dragGhost;
    private Border?           _dragGhostOverflow;
    private TextBlock?        _dragGhostTitleBlock;
    private TextBlock?        _dragGhostTimeBlock;
    private StackPanel?       _dragGhostContentStack;
    private double            _dragGhostOriginalHeight;
    private CalendarDayCanvas.Item? _dragSourceBtn;
    private List<EventEntry>  _dragDimmedBtns = [];
    private Point             _dragStartPos;
    private double            _dragOffsetY;
    private int               _dragWorkingLen;
    private bool              _isDragging;
    private int               _pressClickCount;
    private int               _dragSourceStartMin;
    private List<DateTime>    _currentDays = [];

    // resize state
    private bool        _isResizingEvent;
    private EventEntry? _resizeEntry;
    private List<EventEntry> _resizeHiddenBtns = [];
    private bool        _resizeFromBottom;
    // Поправка «ручка на границе сегмента — не край события» (см. WeekView).
    private int         _resizeVirtualOffset;
    private Border?     _resizeGhost;
    private Border?     _resizeGhostOverflow;
    private TextBlock?  _resizeGhostTitleBlock;
    private TextBlock?  _resizeGhostTimeBlock;
    private StackPanel? _resizeGhostContentStack;

    // selection (множественное: событие -> функция снятия подсветки)
    private readonly Dictionary<CalendarRecord, Action> _selections = new();
    // Идентичность события — пара (Id записи, FieldId поля-даты): одна запись может давать
    // несколько событий (по полю на дату), поэтому одного Id для опознания недостаточно.
    private (int Id, int FieldId)? _pendingSelectId;

    // позиция последнего правого клика по сетке — для меню «Создать запись»
    private Point _gridContextPos;

    private sealed class EventEntry(CalendarRecord ev, int startMin, int endMin, CalendarDayCanvas.Item btn)
    {
        public CalendarRecord Ev  = ev;
        public int     StartMin   = startMin;
        public int     EndMin     = endMin;
        public CalendarDayCanvas.Item Btn = btn;   // блок события в рисованной колонке дня
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

        // Запоминаем позицию правого клика по сетке для меню «Создать запись».
        EventsGrid.AddHandler(PointerPressedEvent, (_, pe) =>
        {
            if (pe.GetCurrentPoint(EventsGrid).Properties.IsRightButtonPressed)
                _gridContextPos = pe.GetPosition(EventsGrid);
        }, RoutingStrategies.Tunnel);
        EventsGrid.ContextMenu = _BuildGridContextMenu();

        AllDayBarCtrl.RecordClicked           += (_, ev) => RecordClicked?.Invoke(this, ev);
        AllDayBarCtrl.RecordMoved              += (_, ev) => RecordMoved?.Invoke(this, ev);
        AllDayBarCtrl.RecordDeleteRequested    += (_, ev) => RecordDeleteRequested?.Invoke(this, ev);
        AllDayBarCtrl.RecordsDeleteRequested   += (_, rs) => RecordsDeleteRequested?.Invoke(this, rs);
        AllDayBarCtrl.RecordDuplicateRequested += (_, ev) => RecordDuplicateRequested?.Invoke(this, ev);
        AllDayBarCtrl.RecordsDuplicateRequested += (_, rs) => RecordsDuplicateRequested?.Invoke(this, rs);
        AllDayBarCtrl.RecordCreationRequested += (_, d)  => AllDayCreationRequested?.Invoke(this, d);
        // Выделили в AllDayBar: при обычном клике снимаем выделение в часовой сетке;
        // при Shift (additive) — не трогаем, чтобы копился общий набор.
        AllDayBarCtrl.SelectionRequested       += (_, additive) => { if (!additive) _Deselect(); };
        // Хуки для общих массовых операций из контекстного меню бара.
        AllDayBarCtrl.ExternalSelectedCount     = () => _selections.Count;
        AllDayBarCtrl.DeleteAllSelected         = _DeleteSelectedAll;
        AllDayBarCtrl.DuplicateAllSelected      = _DuplicateSelectedAll;

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
                if (!IsVisible) return;
                // Клавишу уже забрала боковая панель (удаление коллекции) — второй раз не наша:
                // обработчик висит на окне с handledEventsToo и видит даже обработанное.
                if (KeyClaim.Claimed(ke)) return;
                if (ke.Source is TextBox or AutoCompleteBox) return;

                // Общий набор: выделенные в часовой сетке + в баре «весь день».
                if (_SelectedAll() is not { Count: > 0 } selected) return;

                if (ShortcutSettings.Matches("calendar.deleteSelection", ke))
                {
                    _DeleteSelectedAll();
                    ke.Handled = true;
                }
                else if (ShortcutSettings.Matches("calendar.duplicateSelection", ke))
                {
                    RecordsDuplicateRequested?.Invoke(this, selected);
                    ke.Handled = true;
                }
                else if (ShortcutSettings.Matches("calendar.openEvent", ke))
                {
                    RecordClicked?.Invoke(this, selected[0]);
                    ke.Handled = true;
                }
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

    public void Refresh(IEnumerable<CalendarRecord> events, DateOnly date, bool scrollToNow = false,
                        IReadOnlyCollection<CalendarRecord>? select = null)
    {
        var day       = date.ToDateTime(TimeOnly.MinValue);
        var allEvents = events.ToList();
        // Выделение переживает пересборку; select (созданная запись, копии, перенесенная) его
        // заменяет — как нажатие по ним.
        var keep      = select is null ? SelectionKeys() : KeysOf(select);

        _currentDays = [day];
        _eventEntries.Clear();
        _selections.Clear();

        AllDayBarCtrl.SetData(new[] { date }, allEvents);

        WeekColumns.Children.Clear();
        _todayTimeOverlay = null;

        // Колонка дня — все события одним рисованным контролом (CalendarDayCanvas), как в
        // «Неделе». Меню события — по первому правому нажатию; перед открытием событие
        // выделяется (если оно еще не в наборе) — как в режимах «Месяц»/«Неделя».
        var canvas  = new CalendarDayCanvas { MenuFactory = it => _BuildEventContextMenu((EventEntry)it.Tag!) };
        canvas.ItemRightPressed += it =>
        {
            var entry = (EventEntry)it.Tag!;
            if (_selections.ContainsKey(entry.Ev)) return;
            _Deselect();
            _ClearAllDaySelection();
            _SelectEvent(entry);
        };
        var dayDate = date;
        // Порог «сетка или полоса „весь день"» — FitsHourGrid (см. WeekView): с настройкой
        // «длинные события в сетке» событие любой длины остается блоками по дням.
        var dayEvents = allEvents
            .Where(e => !e.AllDay
                     && CalendarMath.FitsHourGrid(e.StartDt, e.EndDt,
                            CalendarSettings.MultiDayGrid, CalendarSettings.FiveDayWeek)
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

        foreach (var group in GroupOverlapping(sortedDayEvents))
        {
            foreach (var (ev, colIdx, nCols, rightLimit) in AssignColumns(group, keep))
            {
                // Сегменты многодневного события обрезаются рабочими часами (см. WeekView):
                // граница дня ведет себя как полночь, ночные часы не закрашиваются.
                var segS       = CalendarSettings.DayStartMinutes;
                var segE       = CalendarSettings.DayEndMinutes;
                var isStartDay = DateOnly.FromDateTime(ev.StartDt) == dayDate;
                var isEndDay   = DateOnly.FromDateTime(ev.EndDt) == dayDate;
                var rawStart   = ev.StartDt.Hour * 60 + ev.StartDt.Minute;
                var rawEnd     = ev.EndDt.Hour * 60 + ev.EndDt.Minute;
                var startMin = isStartDay
                    ? rawStart
                    : (isEndDay ? rawEnd : segE) >= segS ? segS : 0;
                var effectiveEndMin = isEndDay
                    ? rawEnd
                    : (isStartDay ? rawStart : segS) < segE ? segE : 1440;
                var evH        = Math.Max(22, effectiveEndMin - startMin);
                var evHVisible = evH - 2;
                // Блок события — запись рисованной колонки (раскладку по ширине она ведет сама).
                var block = canvas.Add(ev, startMin + 2, evHVisible, colIdx, rightLimit - colIdx, nCols,
                                       FontSizeBase, WeightBold);
                var entry = new EventEntry(ev, startMin, startMin + evH, block)
                {
                    SubCol     = colIdx,
                    SubColSpan = rightLimit - colIdx,
                    TotalCols  = nCols,
                    Select     = () => block.Selected = true,
                    Deselect   = () => block.Selected = false,
                    Dim        = () => block.Opacity = 0.45,
                    Undim      = () => block.Opacity = 1.0,
                };
                block.Tag = entry;
                _eventEntries.Add(entry);
            }
        }

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
            var toSelect = _eventEntries.FirstOrDefault(e =>
                e.Ev.Id == _pendingSelectId.Value.Id && e.Ev.FieldId == _pendingSelectId.Value.FieldId);
            if (toSelect is not null)
            {
                _SelectEvent(toSelect);
            }
            _pendingSelectId = null;
        }

        if (keep.Count > 0) ApplySelection(keep);

        if (scrollToNow)
            Dispatcher.UIThread.Post(
                () => TimeScroll.Offset = new Vector(0, Math.Max(0, DateTime.Now.Hour * 60 - 120)),
                DispatcherPriority.Loaded);
    }

    // Ключи выделенного по обеим зонам (сетка + полоса «весь день»).
    private HashSet<(int DbId, int Id, int FieldId)> SelectionKeys() =>
        _SelectedAll().Where(r => r.Id.HasValue).Select(r => (r.DbId, r.Id!.Value, r.FieldId)).ToHashSet();

    private static HashSet<(int DbId, int Id, int FieldId)> KeysOf(IEnumerable<CalendarRecord> records) =>
        records.Where(r => r.Id.HasValue).Select(r => (r.DbId, r.Id!.Value, r.FieldId)).ToHashSet();

    // Событие сетки выделяется всеми сегментами, плашка «весь день» — в полосе.
    private void ApplySelection(HashSet<(int DbId, int Id, int FieldId)> keys)
    {
        foreach (var entry in _eventEntries)
            if (entry.Ev.Id is int id && keys.Contains((entry.Ev.DbId, id, entry.Ev.FieldId))
                && !_selections.ContainsKey(entry.Ev))
                _SelectEvent(entry);
        AllDayBarCtrl.SelectKeys(keys);
    }

    private void _Deselect()
    {
        foreach (var unhighlight in _selections.Values.ToList()) unhighlight();
        _selections.Clear();
    }

    // Выделить событие целиком: событие через границу дня может давать несколько
    // сегментов — подсвечиваются и снимаются ВСЕ разом (симметрия с WeekView).
    private void _SelectEvent(EventEntry entry)
    {
        var twins = _eventEntries
            .Where(x => x.Ev.Id == entry.Ev.Id && x.Ev.FieldId == entry.Ev.FieldId)
            .ToList();
        foreach (var t in twins) t.Select?.Invoke();
        _selections[entry.Ev] = () =>
        {
            foreach (var t in twins) t.Deselect?.Invoke();
        };
    }

    // Снять выделение в баре «весь день» при выделении в часовой сетке.
    private void _ClearAllDaySelection() => AllDayBarCtrl.ClearSelection();

    // Общий набор выделенного по обеим зонам (сетка + бар «весь день»).
    private List<CalendarRecord> _SelectedAll() => _selections.Keys
        .Concat(AllDayBarCtrl.SelectedRecords)
        .DistinctBy(r => r.Id)
        .ToList();

    // Снять выделение в обеих зонах и удалить общий набор разом одним пакетом.
    private void _DeleteSelectedAll()
    {
        var batch = _SelectedAll();
        if (batch.Count == 0) return;
        _Deselect();
        AllDayBarCtrl.ClearSelection();
        if (batch.Count == 1) RecordDeleteRequested?.Invoke(this, batch[0]);
        else                   RecordsDeleteRequested?.Invoke(this, batch);
    }

    // Снять выделение в обеих зонах и дублировать общий набор разом одним пакетом
    // (подтверждение «Вы дублируете N записей» — в контроллере, как в «Расписании»).
    private void _DuplicateSelectedAll()
    {
        var batch = _SelectedAll();
        if (batch.Count == 0) return;
        _Deselect();
        AllDayBarCtrl.ClearSelection();
        if (batch.Count == 1) RecordDuplicateRequested?.Invoke(this, batch[0]);
        else                   RecordsDuplicateRequested?.Invoke(this, batch);
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
            _Deselect();
            if (e.ClickCount >= 2 && _currentDays.Count > 0)
            {
                var minutes = (int)(pos.Y / 30) * 30;
                // Рабочие часы: новое событие укладывается в период дня.
                minutes = CalendarMath.ClampStartMin(minutes, CalendarSettings.NewEventDurationMinutes,
                    CalendarSettings.DayStartMinutes, CalendarSettings.DayEndMinutes);
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

            // Поправка «ручка на границе сегмента — не край события» (см. WeekView).
            // Край может лежать не в соседнем, а в более дальнем дне — каждый
            // промежуточный видимый день добавляет рабочее окно.
            _resizeVirtualOffset = 0;
            var limS = CalendarSettings.DayStartMinutes;
            var limE = CalendarSettings.DayEndMinutes;
            if (CalendarMath.HasDayLimits(limS, limE))
            {
                var window  = Math.Max(15, limE - limS);
                var fiveDay = CalendarSettings.FiveDayWeek;
                var segDate = DateOnly.FromDateTime(_currentDays[0]);
                if (onBottomEdge && DateOnly.FromDateTime(entry.Ev.EndDt) > segDate)
                {
                    var endMin = entry.Ev.EndDt.Hour * 60 + entry.Ev.EndDt.Minute;
                    _resizeVirtualOffset = Math.Clamp(endMin, limS, limE) - limS;
                    for (var d = CalendarMath.NextVisibleDay(_currentDays[0], fiveDay);
                         DateOnly.FromDateTime(d) < DateOnly.FromDateTime(entry.Ev.EndDt);
                         d = CalendarMath.NextVisibleDay(d, fiveDay))
                        _resizeVirtualOffset += window;
                }
                else if (onTopEdge && !onBottomEdge
                         && DateOnly.FromDateTime(entry.Ev.StartDt) < segDate)
                {
                    var startMin = entry.Ev.StartDt.Hour * 60 + entry.Ev.StartDt.Minute;
                    _resizeVirtualOffset = -(limE - Math.Clamp(startMin, limS, limE));
                    for (var d = CalendarMath.PrevVisibleDay(_currentDays[0], fiveDay);
                         DateOnly.FromDateTime(d) > DateOnly.FromDateTime(entry.Ev.StartDt);
                         d = CalendarMath.PrevVisibleDay(d, fiveDay))
                        _resizeVirtualOffset -= window;
                }
            }

            var ghostW   = colW - 2.0;
            _resizeGhost = CreateGhostButton(entry.Ev, entry.EndMin - entry.StartMin - 2, ghostW,
                               out _resizeGhostTitleBlock, out _resizeGhostTimeBlock, out _resizeGhostContentStack);
            Canvas.SetLeft(_resizeGhost, 0);
            Canvas.SetTop(_resizeGhost, entry.StartMin + 2);
            DragOverlay.Children.Add(_resizeGhost);

            entry.Btn.Opacity = 0;
            // Остальные сегменты события скрываются целиком (симметрия с WeekView):
            // при resize новое положение показывают только яркие ghost, без прозрачных.
            _resizeHiddenBtns = _eventEntries
                .Where(x => x != entry && x.Ev.Id == entry.Ev.Id && x.Ev.FieldId == entry.Ev.FieldId)
                .ToList();
            foreach (var b in _resizeHiddenBtns) b.Btn.Opacity = 0;
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
        var dayS       = CalendarSettings.DayStartMinutes;
        var dayE       = CalendarSettings.DayEndMinutes;
        // Виртуальный верх события, начавшегося в предыдущем дне, — на рабочей ленте
        // (без лимитов формула вырождается в прежнюю pos.Y + (1440 − startMin)).
        // Старт может лежать не в соседнем, а в более дальнем дне (событие в 3+
        // дня) — каждый промежуточный видимый день добавляет окно ленты.
        var evStartMin = entry.Ev.StartDt.Hour * 60 + entry.Ev.StartDt.Minute;
        _dragOffsetY   = evStartDay < colDay
            ? pos.Y - dayS + dayE - Math.Clamp(evStartMin, dayS, dayE)
            : pos.Y - (entry.StartMin + 2);
        if (evStartDay < colDay)
        {
            var pressWindow = Math.Max(15, dayE - dayS);
            for (var d = CalendarMath.PrevVisibleDay(_currentDays[0], CalendarSettings.FiveDayWeek);
                 DateOnly.FromDateTime(d) > evStartDay;
                 d = CalendarMath.PrevVisibleDay(d, CalendarSettings.FiveDayWeek))
                _dragOffsetY += pressWindow;
        }
        // Перетаскивание сохраняет длительность по рабочей ленте (хвост за границей
        // дня перетекает на следующий видимый день, а не в невидимую субботу).
        _dragWorkingLen = CalendarMath.WorkingLengthMinutes(entry.Ev.StartDt, entry.Ev.EndDt,
            dayS, dayE, CalendarSettings.FiveDayWeek);

        // Визуальный отклик на press — только для обычного клика по невыделенному
        // (для плавного drag). С Shift все выделение/toggle делается на release,
        // иначе press+release дважды переключают набор и он «мигает».
        var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (!additive && !_selections.ContainsKey(entry.Ev))
        {
            _Deselect();
            _ClearAllDaySelection();
            _SelectEvent(entry);
        }

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
            var rawYeff   = rawY + _resizeVirtualOffset; // фактический край события на ленте
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
                var dayS      = CalendarSettings.DayStartMinutes;
                var dayE      = CalendarSettings.DayEndMinutes;
                var hasLimits = CalendarMath.HasDayLimits(dayS, dayE);
                // При лимитах конец выше начала рабочего дня уходит в предыдущий день
                // (вне экрана «Дня») — ghost скрывается, как при уходе за верх сетки.
                var canShrinkLeft  = ((!hasLimits && rawY < 0) || (hasLimits && rawYeff <= dayS)) && startIsPrevDay;

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
                else if (hasLimits)
                {
                    // Рабочие часы: ghost прижат к концу дня, излишек перетекает на
                    // следующий видимый день — текст показывает фактический конец.
                    var minEnd = DateOnly.FromDateTime(_resizeEntry.Ev.StartDt) == resizeColDay
                        ? effectiveStartMin + 30
                        : Math.Max(0, 30 - (1440 - actualStartMin));
                    var effE   = CalendarMath.EffectiveDayEnd(effectiveStartMin, dayE);
                    var rawEnd = Math.Max(rawYeff, minEnd);
                    var endT   = CalendarMath.ResizeEndTarget(_currentDays[0], rawEnd,
                        dayS, effE, CalendarSettings.FiveDayWeek).Min;
                    var ghostEnd = Math.Clamp(rawEnd, minEnd, Math.Max(minEnd, effE));
                    // Верх ghost — как у отрисованного сегмента: продолжение с прошлого
                    // дня при лимитах начинается с начала рабочего дня, не с полуночи.
                    var ghostTop = effectiveStartMin;
                    if (DateOnly.FromDateTime(_resizeEntry.Ev.StartDt) != resizeColDay)
                    {
                        var endHere = DateOnly.FromDateTime(_resizeEntry.Ev.EndDt) == resizeColDay
                            ? _resizeEntry.Ev.EndDt.Hour * 60 + _resizeEntry.Ev.EndDt.Minute : dayE;
                        if (endHere > dayS) ghostTop = dayS;
                    }
                    var ghostH = Math.Max(13, ghostEnd - ghostTop - 2);
                    Canvas.SetTop(_resizeGhost, ghostTop + 2);
                    _resizeGhost.Height = ghostH;
                    // Текст — фактические края события: старт может лежать в прошлом дне.
                    var textStart = DateOnly.FromDateTime(_resizeEntry.Ev.StartDt) != resizeColDay
                        ? actualStartMin : ghostTop;
                    UpdateResizeGhostText(ghostH, textStart, endT);

                    var svH = TimeScroll.Bounds.Height;
                    if (svH > 0)
                    {
                        var ghostBottom = ghostTop + 2 + ghostH;
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
                var dayS      = CalendarSettings.DayStartMinutes;
                var dayE      = CalendarSettings.DayEndMinutes;
                var hasLimits = CalendarMath.HasDayLimits(dayS, dayE);

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
                else if (hasLimits)
                {
                    // Рабочие часы: ghost прижат к началу дня, излишек перетекает на конец
                    // предыдущего видимого дня — текст показывает фактический старт.
                    var sameDayEndHere = DateOnly.FromDateTime(_resizeEntry.Ev.EndDt) == resizeColDay;
                    var maxStart = sameDayEndHere
                        ? effectiveEndMin - 30
                        : Math.Min(1440, 1440 + actualEndMin - 30);
                    var effS     = CalendarMath.EffectiveDayStart(effectiveEndMin, dayS);
                    var rawStart = Math.Min(rawYeff, maxStart);

                    // Зона за концом дня: старт перетекает в следующий видимый день —
                    // он вне экрана «Дня», ghost скрывается (как при уходе за низ сетки).
                    if (!sameDayEndHere && rawStart >= dayE)
                    {
                        _resizeGhost.IsVisible = false;
                        if (_resizeGhostOverflow != null)
                        {
                            DragOverlay.Children.Remove(_resizeGhostOverflow);
                            _resizeGhostOverflow = null;
                        }
                        return;
                    }
                    var startT   = CalendarMath.ResizeStartTarget(_currentDays[0], rawStart,
                        effS, dayE, CalendarSettings.FiveDayWeek).Min;
                    var ghostStart = Math.Clamp(rawStart, Math.Min(effS, maxStart), maxStart);
                    // Низ ghost — как у отрисованного сегмента: продолжение в следующий
                    // день при лимитах заканчивается в конце рабочего дня, не в полночь.
                    var ghostBottom = effectiveEndMin;
                    if (DateOnly.FromDateTime(_resizeEntry.Ev.EndDt) != resizeColDay)
                    {
                        var startHere = DateOnly.FromDateTime(_resizeEntry.Ev.StartDt) == resizeColDay
                            ? _resizeEntry.Ev.StartDt.Hour * 60 + _resizeEntry.Ev.StartDt.Minute : dayS;
                        if (startHere < dayE) ghostBottom = dayE;
                    }
                    var ghostH = Math.Max(13, ghostBottom - ghostStart - 2);
                    Canvas.SetTop(_resizeGhost, ghostStart + 2);
                    _resizeGhost.Height = ghostH;
                    // Текст — фактические края события: конец может лежать в другом дне.
                    var textEnd = sameDayEndHere ? ghostBottom : actualEndMin;
                    UpdateResizeGhostText(ghostH, startT, textEnd);

                    var svH = TimeScroll.Bounds.Height;
                    if (svH > 0 && ghostStart + 2 < TimeScroll.Offset.Y + 30)
                        TimeScroll.Offset = TimeScroll.Offset.WithY(Math.Max(0, ghostStart - 28));

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

            // Снимаем выделение со всех событий (drag отменяет multi-select).
            _Deselect();
            _dragDimmedBtns = _eventEntries
                .Where(e => e.Ev.Id == _dragEvent!.Id && e.Ev.FieldId == _dragEvent!.FieldId).ToList();
            foreach (var b in _dragDimmedBtns) b.Dim?.Invoke();

            _dragGhost = CreateGhostButton(_dragEvent, ghostH, ghostW,
                out _dragGhostTitleBlock, out _dragGhostTimeBlock, out _dragGhostContentStack);
            Canvas.SetLeft(_dragGhost, 0);
            Canvas.SetTop(_dragGhost, _dragSourceStartMin + 2);
            DragOverlay.Children.Add(_dragGhost);
        }

        if (_dragGhost is not null)
        {
            // Позиция и длительность считаются по рабочей ленте: границы дня из настроек
            // ведут себя как полночь (без лимитов — прежние сутки 0..1440).
            var rawY     = pos.Y - _dragOffsetY;
            var dayS     = CalendarSettings.DayStartMinutes;
            var dayE     = CalendarSettings.DayEndMinutes;
            var duration = _dragWorkingLen;
            var snapped  = (int)Math.Floor(rawY / 15) * 15;
            var (shift, m) = CalendarMath.NormalizeDragStart(snapped, dayS, dayE);
            var endM = CalendarMath.AddWorkingMinutes(_currentDays[0], m, duration,
                dayS, dayE, CalendarSettings.FiveDayWeek).Min;

            Canvas.SetLeft(_dragGhost, 0);

            if (shift < 0)
            {
                // Старт ушел в предыдущий день (вне экрана) — виден только хвост
                // от начала дня; текст показывает виртуальный старт предыдущего дня.
                var tail = m + duration - dayE;
                if (tail >= 13)
                {
                    Canvas.SetTop(_dragGhost, dayS + 2);
                    _dragGhost.Height    = Math.Min(tail, dayE - dayS) - 2;
                    _dragGhost.IsVisible = true;
                    UpdateDragGhostText(_dragGhost.Height, m, endM);
                }
                else _dragGhost.IsVisible = false;
            }
            else if (shift > 0)
            {
                // Событие целиком перетекло в следующий день (вне экрана) — ghost
                // скрываем, сверху того же дня ничего не рисуем (перенос виден
                // после отпускания).
                _dragGhost.IsVisible = false;
            }
            else
            {
                // shift 0 — обычное положение (хвост за концом дня уходит за экран).
                var visH = Math.Min(duration, dayE - m) - 2;
                if (m + duration > dayE && visH < 13)
                {
                    _dragGhost.IsVisible = false;
                }
                else
                {
                    _dragGhost.IsVisible = true;
                    Canvas.SetTop(_dragGhost, m);
                    _dragGhost.Height = Math.Max(13, visH);
                    UpdateDragGhostText(_dragGhost.Height, m, endM);
                }
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
            var rawYeff   = rawY + _resizeVirtualOffset; // фактический край события на ленте
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
                var dayS      = CalendarSettings.DayStartMinutes;
                var dayE      = CalendarSettings.DayEndMinutes;
                var hasLimits = CalendarMath.HasDayLimits(dayS, dayE);

                if (((!hasLimits && rawY < 0) || (hasLimits && rawYeff <= dayS)) && startIsPrevDay)
                {
                    // Конец ушел выше начала рабочего дня (или верха сетки) — перетекает
                    // на конец предыдущего видимого дня, не выходя за его рабочие часы;
                    // ход по ленте может перетечь назад через несколько дней (не раньше
                    // дня старта — дальше конец держит минимум 30 минут).
                    var prevDay = PrevDay(_currentDays[0]);
                    var baseT   = hasLimits ? dayE - (dayS - rawYeff) : 1440 + rawY;
                    if (hasLimits)
                    {
                        var window  = Math.Max(15, dayE - dayS);
                        var startDo = DateOnly.FromDateTime(ev.StartDt);
                        while (baseT <= dayS && DateOnly.FromDateTime(prevDay) > startDo)
                        {
                            prevDay = PrevDay(prevDay);
                            baseT  += window;
                        }
                    }
                    var endIsStartDay = DateOnly.FromDateTime(prevDay) == DateOnly.FromDateTime(ev.StartDt);
                    var targetT = Math.Clamp(baseT,
                        endIsStartDay ? actualStartMin + 30 : 30,
                        CalendarMath.EffectiveDayEnd(endIsStartDay ? actualStartMin : 0, dayE));
                    ev.EndDt = targetT >= 1440
                        ? prevDay.Date.AddDays(1)
                        : new DateTime(prevDay.Year, prevDay.Month, prevDay.Day, targetT / 60, targetT % 60, 0);
                }
                else if (isOverflowDown && !hasLimits)
                {
                    var nextDay    = NextDay(_currentDays[0]);
                    var endTimeMin = Math.Clamp(rawY - 1440, 0, 1425);
                    ev.EndDt = new DateTime(nextDay.Year, nextDay.Month, nextDay.Day,
                                            endTimeMin / 60, endTimeMin % 60, 0);
                }
                else
                {
                    var sameDayStart = DateOnly.FromDateTime(ev.StartDt) == resizeColDay;
                    var minEnd = sameDayStart
                        ? effectiveStartMin + 30
                        : Math.Max(0, 30 - (1440 - actualStartMin));
                    // Рабочие часы: верхнего зажима нет — излишек за концом дня (в том
                    // числе за низом сетки) перетекает на следующий видимый день через
                    // ResizeEndTarget; событие вне часов лимита не имеет (EffectiveDayEnd).
                    var rawEnd = hasLimits
                        ? Math.Max(rawYeff, minEnd)
                        : Math.Clamp(timeSnapped, minEnd, 1440);
                    var (endDay, endMin) = CalendarMath.ResizeEndTarget(resizeDay, rawEnd,
                        dayS, CalendarMath.EffectiveDayEnd(effectiveStartMin, dayE),
                        CalendarSettings.FiveDayWeek);
                    ev.EndDt = endMin >= 1440
                        ? endDay.Date.AddDays(1)
                        : new DateTime(endDay.Year, endDay.Month, endDay.Day,
                                       endMin / 60, endMin % 60, 0);
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
                var dayS       = CalendarSettings.DayStartMinutes;
                var dayE       = CalendarSettings.DayEndMinutes;
                var hasLimits  = CalendarMath.HasDayLimits(dayS, dayE);
                var sameDayEnd = DateOnly.FromDateTime(ev.EndDt) == resizeColDay;

                if (isOverflowDown && !hasLimits)
                {
                    var nextDay     = NextDay(_currentDays[0]);
                    var evEndDay    = DateOnly.FromDateTime(ev.EndDt);
                    var maxStart    = evEndDay == DateOnly.FromDateTime(nextDay)
                        ? Math.Max(0, actualEndMin - 30) : 1425;
                    var newStartMin = Math.Clamp(rawY - 1440, 0, maxStart);
                    ev.StartDt = new DateTime(nextDay.Year, nextDay.Month, nextDay.Day,
                                             newStartMin / 60, newStartMin % 60, 0);
                }
                else if (isOverflowUp && !hasLimits)
                {
                    var prevDay      = PrevDay(_currentDays[0]);
                    var startTimeMin = Math.Clamp(1440 + rawY, 0, 1425);
                    ev.StartDt = new DateTime(prevDay.Year, prevDay.Month, prevDay.Day,
                                              startTimeMin / 60, startTimeMin % 60, 0);
                }
                else
                {
                    var maxStart = sameDayEnd
                        ? effectiveEndMin - 30
                        : Math.Min(1440, 1440 + actualEndMin - 30);
                    // Рабочие часы: нижнего зажима нет — старт выше начала дня (в том
                    // числе выше верха сетки) перетекает на конец предыдущего видимого
                    // дня; зона за концом дня — на начало следующего.
                    var rawStart = hasLimits
                        ? Math.Min(rawYeff, maxStart)
                        : Math.Clamp(timeSnapped, 0, maxStart);
                    if (hasLimits && rawStart >= dayE && !sameDayEnd)
                    {
                        var nextDay = NextDay(resizeDay);
                        var sm = Math.Min(dayS + (rawStart - dayE), dayE);
                        if (DateOnly.FromDateTime(nextDay) == DateOnly.FromDateTime(ev.EndDt))
                            sm = Math.Min(sm, actualEndMin - 30);
                        sm = Math.Max(0, sm);
                        ev.StartDt = new DateTime(nextDay.Year, nextDay.Month, nextDay.Day,
                                                  sm / 60, sm % 60, 0);
                    }
                    else
                    {
                        var (startDay, startMin) = CalendarMath.ResizeStartTarget(resizeDay, rawStart,
                            CalendarMath.EffectiveDayStart(effectiveEndMin, dayS),
                            dayE, CalendarSettings.FiveDayWeek);
                        ev.StartDt = startMin >= 1440
                            ? startDay.Date.AddDays(1)
                            : new DateTime(startDay.Year, startDay.Month, startDay.Day,
                                           startMin / 60, startMin % 60, 0);
                    }
                }
            }

            _pendingSelectId = (ev.Id ?? 0, ev.FieldId);
            RecordMoved?.Invoke(this, ev);

            e.Pointer.Capture(null);
            DragOverlay.Children.Remove(_resizeGhost!);
            if (_resizeGhostOverflow != null)
            {
                DragOverlay.Children.Remove(_resizeGhostOverflow);
                _resizeGhostOverflow = null;
            }
            _resizeEntry.Btn.Opacity = 1.0;
            foreach (var b in _resizeHiddenBtns) b.Btn.Opacity = 1.0;
            _resizeHiddenBtns.Clear();

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
                _Deselect();
                RecordClicked?.Invoke(this, _dragEvent);
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
                    var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    if (additive)
                    {
                        // toggle: повторный Shift+клик по выделенному — снять.
                        // AllDayBar НЕ трогаем — Shift копит общий набор через зоны.
                        if (_selections.ContainsKey(entry.Ev)) _DeselectOne(entry.Ev);
                        else
                        {
                            _SelectEvent(entry);
                        }
                    }
                    else
                    {
                        // обычный клик — оставить выделенным только это событие
                        _Deselect();
                        _ClearAllDaySelection();
                        _SelectEvent(entry);
                    }
                }
            }
        }
        else if (_currentDays.Count > 0)
        {
            // Старт и конец — по рабочей ленте: позиция вне границ дня перетекает на
            // соседний ВИДИМЫЙ день, хвост события — тоже (Пт 23:00 + 2 ч при 5-дневке
            // = Пн 01:00, а не невидимая суббота; 17:00 + 2 ч при 09–18 = 10:00 завтра).
            var rawY     = pos.Y - _dragOffsetY;
            var dayS     = CalendarSettings.DayStartMinutes;
            var dayE     = CalendarSettings.DayEndMinutes;
            var fiveDay  = CalendarSettings.FiveDayWeek;
            var duration = _dragWorkingLen;
            var snapped  = (int)Math.Floor(rawY / 15) * 15;
            var (shift, m) = CalendarMath.NormalizeDragStart(snapped, dayS, dayE);
            // Сдвиг может быть больше одного дня (событие в 3+ дня схвачено за
            // поздний сегмент) — шагаем видимыми днями.
            var day = CalendarMath.AddVisibleDays(_currentDays[0], shift, fiveDay);
            var (endDay, endMin) = CalendarMath.AddWorkingMinutes(day, m, duration, dayS, dayE, fiveDay);

            _dragEvent.StartDt = new DateTime(day.Year, day.Month, day.Day, m / 60, m % 60, 0);
            _dragEvent.EndDt   = endMin >= 1440
                ? endDay.Date.AddDays(1)
                : new DateTime(endDay.Year, endDay.Month, endDay.Day, endMin / 60, endMin % 60, 0);
            _pendingSelectId   = (_dragEvent.Id ?? 0, _dragEvent.FieldId);
            RecordMoved?.Invoke(this, _dragEvent);
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
                _dragGhostTimeBlock = new TextBlock { Text = timeText, FontSize = FontSizeBase, Foreground = Brushes.White, Margin = new Thickness(0, 1, 0, 0) };
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
                _resizeGhostTimeBlock = new TextBlock { Text = timeText, FontSize = FontSizeBase, Foreground = Brushes.White, Margin = new Thickness(0, 1, 0, 0) };
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
        var titleMaxLines = showTime ? maxTitleLines : Math.Max(1, (int)((height - margins) / lineH));

        var titleBlock = new TextBlock
        {
            Text         = ev.DisplayTitle,
            FontSize     = FontSizeBase,
            FontWeight   = WeightBold,
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



    // Лимит ПАРАЛЛЕЛЬНЫХ под-колонок пересекающихся событий: сверх лимита событие не рисуется —
    // полоски уже нечитаемо узкие, а день-гигант (сетке приходят первые 2000 записей, и все
    // пересекаются) давал 2000 под-колонок = тысячи субпиксельных контролов и подвисание UI на
    // КАЖДУЮ перерисовку (открытие дня, создание/удаление/перенос/растяжение). Колонки заполняются
    // по времени равномерно — видна выборка событий по всему дню; смотреть все — в «Расписании».
    private const int MaxEventColumns = 12;

    private static List<List<CalendarRecord>> GroupOverlapping(List<CalendarRecord> events)
    {
        if (events.Count == 0) return [];
        var evs    = events.OrderBy(e => e.StartDt).ThenBy(e => e.Title).ToList();
        var groups = new List<List<CalendarRecord>>();
        var cur    = new List<CalendarRecord> { evs[0] };
        // Старты отсортированы → «пересекается с кем-то из cur» ⇔ StartDt < max(EndDt по cur):
        // держим бегущий максимум конца вместо O(n²)-перебора cur.Any на длинных цепочках.
        var curMaxEnd = evs[0].EndDt;
        for (var i = 1; i < evs.Count; i++)
        {
            var ev = evs[i];
            if (ev.StartDt < curMaxEnd) cur.Add(ev);
            else { groups.Add(cur); cur = [ev]; curMaxEnd = ev.EndDt; }
            if (ev.EndDt > curMaxEnd) curMaxEnd = ev.EndDt;
        }
        groups.Add(cur);
        return groups;
    }

    // pinned — выделенные. Раскладка всегда естественная: выделенное стоит там же, где стояло бы
    // без выделения, и сетка колонок не сбивается. Только если на переполненном дне (пересечений
    // больше лимита колонок) перенесенное, созданное или скопированное событие ушло за лимит, оно
    // встает в крайнюю правую рисуемую колонку вместо пересекающихся с ним событий — иначе оно
    // пропало бы с экрана вместе с выделением.
    private static List<(CalendarRecord Ev, int ColIdx, int NCols, int RightLimit)> AssignColumns(
        List<CalendarRecord> group, IReadOnlySet<(int DbId, int Id, int FieldId)>? pinned = null)
    {
        var evs     = group.OrderBy(e => e.StartDt).ThenBy(e => -(e.EndDt - e.StartDt).Ticks).ToList();
        var colEnds = new List<DateTime>();
        var evCol   = new Dictionary<CalendarRecord, int>(ReferenceEqualityComparer.Instance);

        bool IsPinned(CalendarRecord e) => pinned is { Count: > 0 } && e.Id is int id && pinned.Contains((e.DbId, id, e.FieldId));
        foreach (var ev in evs)
        {
            var col = -1;
            for (var i = 0; i < colEnds.Count; i++)
                if (ev.StartDt >= colEnds[i]) { col = i; break; }
            if (col == -1) { col = colEnds.Count; colEnds.Add(ev.EndDt); }
            else colEnds[col] = ev.EndDt;
            evCol[ev] = col;
        }
        if (pinned is { Count: > 0 } && colEnds.Count > MaxEventColumns)
            PinHidden(evs, evCol, MaxEventColumns, IsPinned);

        // Сверх лимита параллельных колонок события не рисуются. Правая граница рисуемого —
        // ближайшая правее колонка пересекающегося с ним рисуемого; сравниваются только
        // рисуемые: на дне-гиганте группа пересечений — весь день (1270 событий), и перебор
        // всех со всеми стоил 1,6 млн шагов с поиском в словаре на каждый день.
        var nCols = Math.Min(colEnds.Count, MaxEventColumns);
        var drawn = new List<(CalendarRecord Ev, int Col)>();
        foreach (var ev in group)
            if (evCol[ev] < nCols) drawn.Add((ev, evCol[ev]));
        var result = new List<(CalendarRecord, int, int, int)>(drawn.Count);
        foreach (var (ev, colIdx) in drawn)
        {
            var rightLimit = nCols;
            foreach (var (other, oc) in drawn)
                if (oc > colIdx && oc < rightLimit && ev.StartDt < other.EndDt && ev.EndDt > other.StartDt)
                    rightLimit = oc;
            result.Add((ev, colIdx, nCols, rightLimit));
        }
        return result;
    }

    // Выделенное, ушедшее за лимит колонок, — в рисуемую колонку справа налево, где нет другого
    // выделенного с пересечением; пересекающиеся с ним невыделенные этой колонки уходят за лимит.
    private static void PinHidden(List<CalendarRecord> evs, Dictionary<CalendarRecord, int> evCol, int limit,
                                  Func<CalendarRecord, bool> isPinned)
    {
        static bool Overlap(CalendarRecord a, CalendarRecord b) => a.StartDt < b.EndDt && a.EndDt > b.StartDt;
        foreach (var ev in evs)
        {
            if (evCol[ev] < limit || !isPinned(ev)) continue;
            for (var col = limit - 1; col >= 0; col--)
            {
                if (evs.Any(o => evCol[o] == col && isPinned(o) && Overlap(o, ev))) continue;
                foreach (var o in evs)
                    if (evCol[o] == col && Overlap(o, ev)) evCol[o] = int.MaxValue;
                evCol[ev] = col;
                break;
            }
        }
    }

    // Меню события: «Изменить»/«Удалить», либо при множественном
    // выделении — только массовое «Удалить». Наполняем в Opening.
    private ContextMenu _BuildEventContextMenu(EventEntry entry)
    {
        var ev   = entry.Ev;
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();

            // Множественное выделение по обеим зонам → массовые «Дублировать»/«Удалить»
            // всего набора разом (как по Delete).
            var all = _SelectedAll();
            if (all.Count > 1 && all.Any(r => r.Id == ev.Id))
            {
                menu.Items.Add(SmartBase.Controls.Components.EventContextMenu.MakeDuplicateItem(
                    _DuplicateSelectedAll, Controls.Components.EventContextMenu.Keys.Calendar.Duplicate, this));
                menu.Items.Add(SmartBase.Controls.Components.EventContextMenu.MakeDeleteItem(
                    _DeleteSelectedAll, Controls.Components.EventContextMenu.Keys.Calendar.Delete, this));
                return;
            }
            menu.Items.Add(SmartBase.Controls.Components.EventContextMenu.MakeEditItem(
                () => { _Deselect(); RecordClicked?.Invoke(this, ev); }, Controls.Components.EventContextMenu.Keys.Calendar.Open, this));
            menu.Items.Add(SmartBase.Controls.Components.EventContextMenu.MakeDuplicateItem(
                () => { _Deselect(); _ClearAllDaySelection(); RecordDuplicateRequested?.Invoke(this, ev); }, Controls.Components.EventContextMenu.Keys.Calendar.Duplicate, this));
            menu.Items.Add(SmartBase.Controls.Components.EventContextMenu.MakeDeleteItem(
                () => { _Deselect(); _ClearAllDaySelection(); RecordDeleteRequested?.Invoke(this, ev); }, Controls.Components.EventContextMenu.Keys.Calendar.Delete, this));
        };
        return menu;
    }

    // Меню пустого места сетки: «Создать запись» в выбранное время.
    // Если правый клик попал на событие — отменяем (покажется меню события).
    private ContextMenu _BuildGridContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opening += (_, args) =>
        {
            var colW = EventsGrid.Bounds.Width;
            if (colW <= 0 || _currentDays.Count == 0)
            {
                if (args is System.ComponentModel.CancelEventArgs cancel) cancel.Cancel = true;
                return;
            }

            var onEvent = _eventEntries.Any(h =>
            {
                if (_gridContextPos.Y < h.StartMin || _gridContextPos.Y > h.EndMin) return false;
                var seg   = colW / h.TotalCols;
                var left  = h.SubCol * seg + 1.0;
                var right = left + h.SubColSpan * seg - 3.0;
                return _gridContextPos.X >= left && _gridContextPos.X <= right;
            });

            if (onEvent)
            {
                if (args is System.ComponentModel.CancelEventArgs cancel) cancel.Cancel = true;
                return;
            }

            // Правый клик по пустому месту снимает выделение группы — как левый.
            _Deselect();

            var minutes = (int)(_gridContextPos.Y / 30) * 30;
            // Рабочие часы: новое событие укладывается в период дня.
            minutes = CalendarMath.ClampStartMin(minutes, CalendarSettings.NewEventDurationMinutes,
                CalendarSettings.DayStartMinutes, CalendarSettings.DayEndMinutes);
            var day     = _currentDays[0];
            var dt      = new DateTime(day.Year, day.Month, day.Day, minutes / 60, minutes % 60, 0);

            menu.Items.Clear();
            menu.Items.Add(SmartBase.Controls.Components.EventContextMenu.MakeCreateItem(
                () => TimeSlotCreationRequested?.Invoke(this, dt), Controls.Components.EventContextMenu.Keys.Calendar.Create, this));
        };
        return menu;
    }
}
