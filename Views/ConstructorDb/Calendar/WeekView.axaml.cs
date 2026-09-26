using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SmartBase.Core.Calendar;
using SmartBase.Core.Models;
using SmartBase.Utils;
using SmartBase.Controls.Behaviors;

namespace SmartBase.Views.ConstructorDb.Calendar;

public partial class WeekView : UserControl
{
    // Размеры/веса шрифта — из токенов Typography.axaml (резолв ресурса в рантайме).
    // Свежий размер из Application.Resources — меняется живьем при изменении FontScale.
    private static double FontSizeBase =>
        (double)Application.Current!.FindResource("FontSizeBase")!;

    private static readonly FontWeight WeightBold   = (FontWeight)Application.Current!.FindResource("FontWeightBold")!;
    private static readonly FontWeight WeightNormal = (FontWeight)Application.Current!.FindResource("FontWeightNormal")!;

    public event EventHandler<CalendarRecord>?    RecordClicked;
    public event EventHandler<CalendarRecord>?    RecordMoved;
    public event EventHandler<CalendarRecord>?    RecordDeleteRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDeleteRequested;
    public event EventHandler<CalendarRecord>?    RecordDuplicateRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDuplicateRequested;
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
    private CalendarDayCanvas.Item? _dragSourceBtn;
    private List<EventEntry> _dragDimmedBtns = [];
    private Point          _dragStartPos;
    private double         _dragOffsetY;
    private int            _dragWorkingLen;
    private bool           _isDragging;
    private int            _pressClickCount;
    private int            _dragSourceCol;
    private int            _dragSourceStartMin;
    private List<DateTime> _currentDays = [];

    // 5-дневка: окно короче семи дней — соседи колонок считаются по ВИДИМЫМ дням
    // (после пятницы — понедельник). Судим по фактическому окну, а не по настройке:
    // между сменой настройки и Refresh состояния не расходятся.
    private bool FiveDay => _currentDays.Count is > 0 and < 7;

    // Ширина колонки дня; 0 — окно еще не построено (деление на счетчик дней, не на 7).
    private double ColW() => _currentDays.Count > 0 ? EventsGrid.Bounds.Width / _currentDays.Count : 0;

    // resize state
    private bool        _isResizingEvent;
    private EventEntry? _resizeEntry;
    private List<EventEntry> _resizeHiddenBtns = [];
    private bool        _resizeFromBottom;
    // Ручка на границе сегмента — не край события: у события через границу дня
    // фактический конец/старт лежит в соседнем дне. Поправка (минуты рабочей ленты)
    // прибавляется к позиции указателя, чтобы жест продолжал фактический край без
    // скачка, а хвост в соседней колонке сужался плавно. 0 — край в этом же дне.
    private int         _resizeVirtualOffset;
    private Border?     _resizeGhost;
    private Border?     _resizeGhostOverflow;
    // Ghost-сегменты нового положения по остальным видимым колонкам при рабочих
    // часах: голова, серединные дни (полное окно) и перетекший хвост — растягивание
    // через несколько дней показывает все блоки. Без лимитов не используется
    // (легаси-ветки живут на одиночном _resizeGhostOverflow).
    private readonly List<Border> _segGhosts = [];
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

    // hit-testing
    private sealed class EventEntry(CalendarRecord ev, int dayCol, int startMin, int endMin, CalendarDayCanvas.Item btn)
    {
        public CalendarRecord Ev         = ev;
        public int     DayCol     = dayCol;
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
        AllDayBarCtrl.RecordsDuplicateRequested += (_, rs) => RecordsDuplicateRequested?.Invoke(this, rs);
        AllDayBarCtrl.RecordCreationRequested  += (_, d)  => AllDayCreationRequested?.Invoke(this, d);
        // Выделили в AllDayBar: при обычном клике снимаем выделение в часовой сетке
        // (одно на экран); при Shift (additive) — не трогаем, чтобы копился общий
        // набор. Обратное направление делает _ClearAllDaySelection.
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
                // Клавишу уже забрала боковая панель (удаление коллекции) — второй раз не наша.
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

    // Дни окна диктует контроллер (CalendarControllerView.WeekDays): 7 подряд или 5 рабочих,
    // выровненные на понедельник или скользящие («по дню») — вид их только рисует.
    public void Refresh(IEnumerable<CalendarRecord> events, IReadOnlyList<DateTime> days, bool scrollToNow = false,
                        IReadOnlyCollection<CalendarRecord>? select = null)
    {
        var allDays   = days.Select(d => d.Date).ToList();
        var allEvents = events.ToList();
        // Выделение переживает пересборку; select (созданная запись, копии, перенесенная) его
        // заменяет — как нажатие по ним.
        var keep      = select is null ? SelectionKeys() : KeysOf(select);

        _currentDays = allDays;
        _eventEntries.Clear();
        _selections.Clear();

        WeekHeaderDays.Columns = allDays.Count;
        ColumnLines.Columns    = allDays.Count;
        WeekColumns.Columns    = allDays.Count;

        AllDayBarCtrl.SetData(allDays.Select(DateOnly.FromDateTime), allEvents);
        BuildHeader(allDays);

        WeekColumns.Children.Clear();
        _todayTimeOverlay = null;
        var gridLineBrush = new SolidColorBrush(Color.Parse("#E8E8E8"));

        // Слой вертикальных линий колонок (ColumnLines): пустые Border без контента,
        // поэтому Aliased безопасен — линия защелкивается в целый пиксель при любом
        // зуме/DPI, а не размазывается сглаживанием. Первая колонка без линии.
        ColumnLines.Children.Clear();
        for (var i = 0; i < allDays.Count; i++)
        {
            var line = new Border { BorderBrush = gridLineBrush };
            if (i > 0)
            {
                line.Bind(Border.BorderThicknessProperty, line.GetResourceObservable("BorderLineLeft"));
                line.Bind(UseLayoutRoundingProperty, line.GetResourceObservable("LineUseLayoutRounding"));
                RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
            }
            ColumnLines.Children.Add(line);
        }

        for (var col = 0; col < allDays.Count; col++)
        {
            var day     = allDays[col];
            // Колонка дня — все события одним рисованным контролом (CalendarDayCanvas). Меню
            // события — по первому правому нажатию; перед открытием событие выделяется (если оно
            // еще не в наборе), чтобы пункт действовал на видимо выделенное — как в «Месяце».
            var canvas  = new CalendarDayCanvas { MenuFactory = it => _BuildEventContextMenu((EventEntry)it.Tag!) };
            canvas.ItemRightPressed += it =>
            {
                var entry = (EventEntry)it.Tag!;
                if (_selections.ContainsKey(entry.Ev)) return;
                _Deselect();
                _ClearAllDaySelection();
                _SelectEvent(entry);
            };
            var dayDate = DateOnly.FromDateTime(day);
            // Порог «сетка или полоса „весь день"» — FitsHourGrid: стандартно видимая
            // длительность меньше суток (при 5-дневке выходные не считаются); с настройкой
            // «длинные события в сетке» событие любой длины остается блоками по дням.
            var dayEvents = allEvents
                .Where(e => !e.AllDay
                         && CalendarMath.FitsHourGrid(e.StartDt, e.EndDt,
                                CalendarSettings.MultiDayGrid, FiveDay)
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
                    // Сегменты многодневного события обрезаются рабочими часами: граница
                    // дня ведет себя как полночь — хвост в пятнице заканчивается в 18:00,
                    // продолжение в понедельнике начинается с 09:00, ночные часы не
                    // закрашиваются. Событие вне часов сохраняет прежние границы суток
                    // (без лимитов формулы вырождаются в прежние 0/1440).
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
                    // Блок события — запись рисованной колонки: ширину по подколонкам она
                    // раскладывает сама при раскладке своего размера.
                    var block = canvas.Add(ev, startMin + 2, evHVisible, colIdx, rightLimit - colIdx, nCols,
                                           FontSizeBase, WeightBold);
                    var entry = new EventEntry(ev, col, startMin, startMin + evH, block)
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
            // Затенение выходных — по дню недели, не по индексу колонки: в режиме
            // «по дню» окно не выровнено на понедельник и Сб/Вс бывают в середине.
            if (CalendarMath.IsWeekend(day))
                dayGrid.Background = new SolidColorBrush(Color.FromArgb(7, 0, 0, 0));
            dayGrid.Children.Add(canvas);

            if (day.Date == DateTime.Today)
            {
                var overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
                dayGrid.Children.Add(overlay);
                _todayTimeOverlay = overlay;
            }

            // Левую линию колонки рисует отдельный слой ColumnLines (см. выше) —
            // бордер на контейнере с событиями нельзя алиасить (текст внутри).
            var colHost = new Border { Child = dayGrid };
            WeekColumns.Children.Add(colHost);
        }

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

    // Выделить событие целиком: событие через границу дня рисуется несколькими
    // сегментами (в колонках соседних дней) — подсвечиваются и снимаются ВСЕ разом.
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

    // Снять выделение в баре «весь день» — зовем при выделении в часовой сетке,
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
        var colW = ColW();
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
                // Рабочие часы: новое событие укладывается в период дня.
                minutes = CalendarMath.ClampStartMin(minutes, CalendarSettings.NewEventDurationMinutes,
                    CalendarSettings.DayStartMinutes, CalendarSettings.DayEndMinutes);
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

            // Поправка «ручка на границе сегмента — не край события»: при рабочих
            // часах фактический конец/старт в другом дне продолжается жестом плавно.
            // Край может лежать не в соседнем, а в более дальнем дне (событие в 3+
            // дня) — каждый промежуточный видимый день добавляет рабочее окно.
            _resizeVirtualOffset = 0;
            var limS = CalendarSettings.DayStartMinutes;
            var limE = CalendarSettings.DayEndMinutes;
            if (CalendarMath.HasDayLimits(limS, limE))
            {
                var window  = Math.Max(15, limE - limS);
                var segDate = DateOnly.FromDateTime(_currentDays[entry.DayCol]);
                if (onBottomEdge && DateOnly.FromDateTime(entry.Ev.EndDt) > segDate)
                {
                    var endMin = entry.Ev.EndDt.Hour * 60 + entry.Ev.EndDt.Minute;
                    _resizeVirtualOffset = Math.Clamp(endMin, limS, limE) - limS;
                    for (var d = CalendarMath.NextVisibleDay(_currentDays[entry.DayCol], FiveDay);
                         DateOnly.FromDateTime(d) < DateOnly.FromDateTime(entry.Ev.EndDt);
                         d = CalendarMath.NextVisibleDay(d, FiveDay))
                        _resizeVirtualOffset += window;
                }
                else if (onTopEdge && !onBottomEdge
                         && DateOnly.FromDateTime(entry.Ev.StartDt) < segDate)
                {
                    var startMin = entry.Ev.StartDt.Hour * 60 + entry.Ev.StartDt.Minute;
                    _resizeVirtualOffset = -(limE - Math.Clamp(startMin, limS, limE));
                    for (var d = CalendarMath.PrevVisibleDay(_currentDays[entry.DayCol], FiveDay);
                         DateOnly.FromDateTime(d) > DateOnly.FromDateTime(entry.Ev.StartDt);
                         d = CalendarMath.PrevVisibleDay(d, FiveDay))
                        _resizeVirtualOffset -= window;
                }
            }

            var borderLeft = entry.DayCol > 0 ? 1.0 : 0.0;
            var ghostW     = colW - 2.0 - borderLeft;
            _resizeGhost   = CreateGhostButton(entry.Ev, entry.EndMin - entry.StartMin - 2, ghostW,
                                out _resizeGhostTitleBlock, out _resizeGhostTimeBlock, out _resizeGhostContentStack);
            Canvas.SetLeft(_resizeGhost, entry.DayCol * colW + borderLeft);
            Canvas.SetTop(_resizeGhost, entry.StartMin + 2);
            DragOverlay.Children.Add(_resizeGhost);

            entry.Btn.Opacity = 0;
            // Остальные сегменты события скрываются целиком, как и грабнутый: новое
            // положение показывают только ЯРКИЕ ghost — при resize полупрозрачных
            // «призраков» быть не должно (в отличие от drag, где оригинал приглушен).
            _resizeHiddenBtns = _eventEntries
                .Where(x => x != entry && x.Ev.Id == entry.Ev.Id && x.Ev.FieldId == entry.Ev.FieldId)
                .ToList();
            foreach (var b in _resizeHiddenBtns) b.Btn.Opacity = 0;

            // Ghost-сегменты остальных блоков события (голова, серединные дни, хвост) —
            // СРАЗУ при нажатии, Moved их лишь обновляет: иначе скрытые сегменты
            // «исчезают» до первого движения мыши. При «длинных событиях в сетке»
            // раскладка нужна и без рабочих часов (сегменты по суткам).
            if (CalendarMath.HasDayLimits(limS, limE) || CalendarSettings.MultiDayGrid)
                UpdateSegGhosts(entry.Ev, entry.Ev.StartDt, entry.Ev.EndDt, entry.DayCol, colW);
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
        // учитываем виртуальное начало события (отрицательная Y) для плавного
        // перетаскивания. Виртуальный верх считается по рабочей ленте — без лимитов
        // формула вырождается в прежнюю pos.Y + (1440 − startMin).
        var evStartDay  = DateOnly.FromDateTime(entry.Ev.StartDt);
        var colDay      = DateOnly.FromDateTime(_currentDays[entry.DayCol]);
        var pressDayS   = CalendarSettings.DayStartMinutes;
        var pressDayE   = CalendarSettings.DayEndMinutes;
        if (evStartDay < colDay)
        {
            var evStartMin = entry.Ev.StartDt.Hour * 60 + entry.Ev.StartDt.Minute;
            _dragOffsetY = pos.Y - pressDayS + pressDayE - Math.Clamp(evStartMin, pressDayS, pressDayE);
            // Старт может лежать не в соседнем, а в более дальнем дне (событие в 3+
            // дня) — каждый промежуточный видимый день добавляет окно ленты.
            var pressWindow = Math.Max(15, pressDayE - pressDayS);
            for (var d = CalendarMath.PrevVisibleDay(_currentDays[entry.DayCol], FiveDay);
                 DateOnly.FromDateTime(d) > evStartDay;
                 d = CalendarMath.PrevVisibleDay(d, FiveDay))
                _dragOffsetY += pressWindow;
        }
        else
        {
            _dragOffsetY = pos.Y - (entry.StartMin + 2);
        }
        // Перетаскивание сохраняет длительность по рабочей ленте (хвост за границей
        // дня перетекает на следующий видимый день, а не в невидимую субботу).
        _dragWorkingLen = CalendarMath.WorkingLengthMinutes(entry.Ev.StartDt, entry.Ev.EndDt,
            pressDayS, pressDayE, CalendarSettings.FiveDayWeek);

        // Визуальный отклик на press даем только для обычного клика по
        // невыделенному событию (чтобы при последующем drag цвет уже сменился).
        // С Shift ничего не трогаем здесь — все выделение/toggle делается на
        // release, иначе press+release дважды переключают набор и он «мигает».
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

    // Дата + минуты дня → момент времени; 1440 — полночь следующих суток.
    private static DateTime AtMinute(DateTime day, int min) =>
        min >= 1440 ? day.Date.AddDays(1) : day.Date.AddMinutes(min);

    /// <summary>
    /// Ghost-сегменты кандидата нового положения события по всем видимым колонкам,
    /// кроме mainCol (там основной ghost с текстом): голова, серединные дни (полное
    /// рабочее окно) и перетекший хвост — drag/resize через несколько дней показывает
    /// все блоки. Границы сегментов — как в Refresh (обрезаны рабочими часами).
    /// Список переиспользуется между вызовами, лишние ghost удаляются.
    /// </summary>
    private void UpdateSegGhosts(CalendarRecord ev, DateTime newStart, DateTime newEnd,
                                 int mainCol, double colW)
    {
        var dayS = CalendarSettings.DayStartMinutes;
        var dayE = CalendarSettings.DayEndMinutes;
        var used = 0;
        if (newEnd > newStart)
        {
            var sd   = DateOnly.FromDateTime(newStart);
            var ed   = DateOnly.FromDateTime(newEnd);
            var rawS = newStart.Hour * 60 + newStart.Minute;
            var rawE = newEnd.Hour * 60 + newEnd.Minute;
            for (var c = 0; c < _currentDays.Count; c++)
            {
                if (c == mainCol) continue;
                var d = DateOnly.FromDateTime(_currentDays[c]);
                if (d < sd || d > ed) continue;
                var isS = d == sd;
                var isE = d == ed;
                if (isE && !isS && rawE == 0) continue; // конец в полночь — сегмента нет
                var top    = isS ? rawS : (isE ? rawE : dayE) >= dayS ? dayS : 0;
                var bottom = isE ? rawE : (isS ? rawS : dayS) < dayE ? dayE : 1440;
                if (bottom <= top) continue;
                var h = Math.Max(13, bottom - top - 2);
                Border g;
                if (used < _segGhosts.Count) g = _segGhosts[used];
                else
                {
                    g = CreateGhostButton(ev, h, colW - 3.0);
                    DragOverlay.Children.Add(g);
                    _segGhosts.Add(g);
                }
                used++;
                var border = c > 0 ? 1.0 : 0.0;
                g.Width  = colW - 2.0 - border;
                g.Height = h;
                Canvas.SetLeft(g, c * colW + border);
                Canvas.SetTop(g, top + 2);
            }
        }
        for (var i = _segGhosts.Count - 1; i >= used; i--)
        {
            DragOverlay.Children.Remove(_segGhosts[i]);
            _segGhosts.RemoveAt(i);
        }
    }

    private void ClearSegGhosts()
    {
        foreach (var g in _segGhosts) DragOverlay.Children.Remove(g);
        _segGhosts.Clear();
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
            var rawYeff       = rawY + _resizeVirtualOffset; // фактический край события на ленте
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
                var dayS      = CalendarSettings.DayStartMinutes;
                var dayE      = CalendarSettings.DayEndMinutes;
                var hasLimits = CalendarMath.HasDayLimits(dayS, dayE);
                var effE      = CalendarMath.EffectiveDayEnd(effectiveStartMin, dayE);
                // Зеркало коммита: при лимитах вертикальный overflow в той же колонке
                // идет через рабочую ленту, а не через «+1 колонка от полуночи».
                var canExtendRight = (rawPointerCol > _resizeEntry.DayCol || isOverflowDown)
                                     && !(hasLimits && rawPointerCol <= _resizeEntry.DayCol);
                // Конец уходит в предыдущий день: мышь перешла влево ИЛИ ушла выше Y=0;
                // при лимитах — уже выше начала рабочего дня (зона до 09:00 не существует,
                // конец перетекает на конец предыдущего видимого дня).
                var canShrinkLeft  = (rawPointerCol < _resizeEntry.DayCol || (!hasLimits && rawY < 0)
                                      || (hasLimits && rawYeff <= dayS))
                                     && startIsPrevDay;

                if (canShrinkLeft)
                {
                    // Конец уходит обратно — основной ghost переезжает в раннюю колонку.
                    // Вертикальный ход по ленте может перетечь назад через несколько дней.
                    var logicalCol = rawPointerCol < _resizeEntry.DayCol
                        ? rawPointerCol : _resizeEntry.DayCol - 1;
                    int targetTime;
                    if (rawPointerCol < _resizeEntry.DayCol) targetTime = timeSnapped;
                    else if (hasLimits)
                    {
                        // Не раньше дня старта: дальше конец держит минимум 30 минут.
                        var window  = Math.Max(15, dayE - dayS);
                        var startDo = DateOnly.FromDateTime(_resizeEntry.Ev.StartDt);
                        targetTime = rawYeff + window;
                        while (targetTime <= dayS && DateOnly.FromDateTime(
                                   CalendarMath.DayAtCol(_currentDays, logicalCol, FiveDay)) > startDo)
                        {
                            logicalCol--;
                            targetTime += window;
                        }
                        targetTime = Math.Clamp(targetTime, 0, dayE);
                    }
                    else targetTime = Math.Clamp(1440 + rawY, 0, 1440);
                    var targetVisible  = logicalCol >= 0 && logicalCol < _currentDays.Count;
                    var targetDate     = CalendarMath.DayAtCol(_currentDays, logicalCol, FiveDay);
                    var targetBorder   = logicalCol > 0 ? 1.0 : 0.0;
                    var evStartDay     = DateOnly.FromDateTime(_resizeEntry.Ev.StartDt);
                    var targetDateOnly = DateOnly.FromDateTime(targetDate);
                    // Верх ghost — как у отрисованного сегмента: продолжение при лимитах
                    // начинается с начала рабочего дня.
                    var newStartMin    = evStartDay == targetDateOnly
                        ? actualStartMin : hasLimits ? dayS : 0;
                    var minEnd         = evStartDay == targetDateOnly ? actualStartMin + 30 : 30;
                    var maxEnd         = hasLimits
                        ? Math.Max(minEnd, CalendarMath.EffectiveDayEnd(
                              evStartDay == targetDateOnly ? actualStartMin : 0, dayE))
                        : 1440;
                    var newEndMin      = Math.Clamp(targetTime, minEnd, maxEnd);
                    var ghostH         = Math.Max(13, newEndMin - newStartMin - 2);
                    Canvas.SetLeft(_resizeGhost, logicalCol * colW + targetBorder);
                    _resizeGhost.Width  = colW - 2.0 - targetBorder;
                    Canvas.SetTop(_resizeGhost, newStartMin + 2);
                    _resizeGhost.Height = ghostH;
                    _resizeGhost.IsVisible = targetVisible;
                    // Текст — фактические края: старт может лежать в более раннем дне.
                    var textStart = evStartDay == targetDateOnly || !hasLimits
                        ? newStartMin : actualStartMin;
                    UpdateResizeGhostText(ghostH, textStart, newEndMin);

                    // Голова и серединные дни между стартом и новым концом.
                    if (hasLimits || CalendarSettings.MultiDayGrid)
                        UpdateSegGhosts(_resizeEntry.Ev, _resizeEntry.Ev.StartDt,
                            AtMinute(targetDate, newEndMin), logicalCol, colW);
                    if (_resizeGhostOverflow != null)
                    {
                        DragOverlay.Children.Remove(_resizeGhostOverflow);
                        _resizeGhostOverflow = null;
                    }
                }
                else if (!canExtendRight)
                {
                    // Вернуть ghost в свою колонку (мог переезжать в соседнюю в canShrinkLeft).
                    var homeBorder = _resizeEntry.DayCol > 0 ? 1.0 : 0.0;
                    Canvas.SetLeft(_resizeGhost, _resizeEntry.DayCol * colW + homeBorder);
                    _resizeGhost.Width = colW - 2.0 - homeBorder;

                    var minEnd = DateOnly.FromDateTime(_resizeEntry.Ev.StartDt) == resizeColDay
                        ? effectiveStartMin + 30
                        : Math.Max(0, 30 - (1440 - actualStartMin));
                    // Рабочие часы: ghost прижат к концу дня, текст показывает фактический
                    // конец (хвост перетекает на следующий видимый день).
                    var rawEnd = hasLimits
                        ? Math.Max(rawYeff, minEnd)
                        : Math.Clamp(timeSnapped, minEnd, 1440);
                    var endTarget = hasLimits
                        ? CalendarMath.ResizeEndTarget(_currentDays[_resizeEntry.DayCol], rawEnd,
                              dayS, effE, FiveDay)
                        : (Day: _currentDays[_resizeEntry.DayCol], Min: rawEnd);
                    var endT = endTarget.Min;
                    var newEndMin = Math.Clamp(rawEnd, minEnd, hasLimits ? Math.Max(minEnd, effE) : 1440);
                    // Верх ghost — как у отрисованного сегмента: продолжение с прошлого
                    // дня при лимитах начинается с начала рабочего дня, не с полуночи.
                    var ghostTop = effectiveStartMin;
                    if (hasLimits && DateOnly.FromDateTime(_resizeEntry.Ev.StartDt) != resizeColDay)
                    {
                        var endHere = DateOnly.FromDateTime(_resizeEntry.Ev.EndDt) == resizeColDay
                            ? _resizeEntry.Ev.EndDt.Hour * 60 + _resizeEntry.Ev.EndDt.Minute : dayE;
                        if (endHere > dayS) ghostTop = dayS;
                    }
                    var ghostH = Math.Max(13, newEndMin - ghostTop - 2);
                    Canvas.SetTop(_resizeGhost, ghostTop + 2);
                    _resizeGhost.Height = ghostH;
                    // Текст — фактические края события: старт может лежать в прошлом дне.
                    var textStart = hasLimits
                                    && DateOnly.FromDateTime(_resizeEntry.Ev.StartDt) != resizeColDay
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

                    // Ghost-сегменты в остальных колонках: неизменная голова события,
                    // серединные дни и перетекший за конец дня хвост — растягивание
                    // работает через несколько дней, все блоки видимы.
                    if (hasLimits || CalendarSettings.MultiDayGrid)
                        UpdateSegGhosts(_resizeEntry.Ev, _resizeEntry.Ev.StartDt,
                            AtMinute(endTarget.Day, endTarget.Min), _resizeEntry.DayCol, colW);
                    if (_resizeGhostOverflow != null)
                    {
                        DragOverlay.Children.Remove(_resizeGhostOverflow);
                        _resizeGhostOverflow = null;
                    }
                }
                else
                {
                    var ghostEnd = hasLimits ? effE : 1440;
                    var ghostTop = effectiveStartMin;
                    if (hasLimits && DateOnly.FromDateTime(_resizeEntry.Ev.StartDt) != resizeColDay)
                        ghostTop = dayS;
                    var ghostH   = Math.Max(13, ghostEnd - ghostTop - 2);
                    Canvas.SetTop(_resizeGhost, ghostTop + 2);
                    _resizeGhost.Height = ghostH;
                    UpdateResizeGhostText(ghostH, ghostTop, ghostEnd);

                    if (hasLimits)
                    {
                        // Указатель в поздней колонке: кандидат конца — зеркало коммита
                        // (ниже низа сетки перетекает дальше через ResizeEndTarget);
                        // сегменты во всех прочих колонках рисует общая раскладка.
                        var baseDay = CalendarMath.DayAtCol(_currentDays, rawPointerCol, FiveDay);
                        var rawT    = isOverflowDown ? rawY : Math.Max(timeSnapped, dayS + 15);
                        var (cd, cm) = CalendarMath.ResizeEndTarget(baseDay, rawT, dayS, dayE, FiveDay);
                        UpdateSegGhosts(_resizeEntry.Ev, _resizeEntry.Ev.StartDt, AtMinute(cd, cm),
                            _resizeEntry.DayCol, colW);
                        // Текст — фактические края события.
                        var textStart = DateOnly.FromDateTime(_resizeEntry.Ev.StartDt) != resizeColDay
                            ? actualStartMin : ghostTop;
                        UpdateResizeGhostText(ghostH, textStart, cm);
                        if (_resizeGhostOverflow != null)
                        {
                            DragOverlay.Children.Remove(_resizeGhostOverflow);
                            _resizeGhostOverflow = null;
                        }
                    }
                    else
                    {
                        var nextTime       = isOverflowDown && rawPointerCol <= _resizeEntry.DayCol
                            ? Math.Clamp(rawY - 1440, 0, 1425) : timeSnapped;
                        var logicalOverflowCol = isOverflowDown && rawPointerCol <= _resizeEntry.DayCol
                            ? _resizeEntry.DayCol + 1 : rawPointerCol;

                        if (CalendarSettings.MultiDayGrid)
                        {
                            // «Длинные события в сетке»: сегменты прочих колонок (голова,
                            // серединные дни, хвост) — общая раскладка по суткам.
                            var cEndDay = CalendarMath.DayAtCol(_currentDays, logicalOverflowCol, FiveDay);
                            UpdateSegGhosts(_resizeEntry.Ev, _resizeEntry.Ev.StartDt,
                                AtMinute(cEndDay, nextTime), _resizeEntry.DayCol, colW);
                            if (_resizeGhostOverflow != null)
                            {
                                DragOverlay.Children.Remove(_resizeGhostOverflow);
                                _resizeGhostOverflow = null;
                            }
                        }
                        else
                        {
                            var overflowTop     = 2.0;
                            var overflowH       = Math.Max(13, nextTime - (int)overflowTop);
                            var overflowVisible = logicalOverflowCol >= 0 && logicalOverflowCol < _currentDays.Count;

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
                                Canvas.SetTop(_resizeGhostOverflow, overflowTop);
                                DragOverlay.Children.Add(_resizeGhostOverflow);
                            }
                            else
                            {
                                _resizeGhostOverflow.Height = overflowH;
                                Canvas.SetLeft(_resizeGhostOverflow, logicalOverflowCol * colW + 1.0);
                                Canvas.SetTop(_resizeGhostOverflow, overflowTop);
                            }
                        }
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
                var dayS      = CalendarSettings.DayStartMinutes;
                var dayE      = CalendarSettings.DayEndMinutes;
                var hasLimits = CalendarMath.HasDayLimits(dayS, dayE);
                var effS      = CalendarMath.EffectiveDayStart(effectiveEndMin, dayS);
                // Зеркало коммита: при лимитах вертикальный overflow в той же колонке
                // идет через рабочую ленту, а не через «±1 колонка от полуночи».
                var canExtendLeft  = (rawPointerCol < _resizeEntry.DayCol || isOverflowUp)
                                     && !(hasLimits && rawPointerCol >= _resizeEntry.DayCol);
                var canExtendRight = (rawPointerCol > _resizeEntry.DayCol || isOverflowDown)
                                     && !canExtendLeft
                                     && !(hasLimits && rawPointerCol <= _resizeEntry.DayCol);

                if (canExtendRight)
                {
                    // Начало события уходит в следующую колонку
                    var logicalCol = isOverflowDown && rawPointerCol <= _resizeEntry.DayCol
                        ? _resizeEntry.DayCol + 1 : rawPointerCol;
                    var targetTime = isOverflowDown && rawPointerCol <= _resizeEntry.DayCol
                        ? Math.Clamp(rawY - 1440, 0, 1440)
                        : timeSnapped;
                    if (hasLimits) targetTime = Math.Clamp(targetTime, Math.Min(dayS, dayE - 15), dayE - 15);
                    var targetVisible  = logicalCol >= 0 && logicalCol < _currentDays.Count;
                    var targetCol      = Math.Clamp(logicalCol, 0, _currentDays.Count - 1);
                    var targetDate     = CalendarMath.DayAtCol(_currentDays, logicalCol, FiveDay);
                    var targetBorder   = logicalCol > 0 ? 1.0 : 0.0;
                    var evEndDay       = DateOnly.FromDateTime(_resizeEntry.Ev.EndDt);
                    var targetDateOnly = DateOnly.FromDateTime(targetDate);
                    // Низ ghost — как у отрисованного сегмента: продолжение при лимитах
                    // заканчивается в конце рабочего дня.
                    var newEndMin      = evEndDay == targetDateOnly
                        ? actualEndMin : hasLimits ? dayE : 1440;
                    var maxStart       = evEndDay == targetDateOnly ? Math.Max(0, actualEndMin - 30) : 1425;
                    var newStartMin    = Math.Clamp(targetTime, 0, maxStart);
                    var ghostH         = Math.Max(13, newEndMin - newStartMin - 2);
                    Canvas.SetLeft(_resizeGhost, logicalCol * colW + targetBorder);
                    _resizeGhost.Width  = colW - 2.0 - targetBorder;
                    Canvas.SetTop(_resizeGhost, newStartMin + 2);
                    _resizeGhost.Height = ghostH;
                    _resizeGhost.IsVisible = targetVisible;
                    // Текст — фактические края: конец может лежать в более позднем дне.
                    var extTextEnd = hasLimits && evEndDay != targetDateOnly ? actualEndMin : newEndMin;
                    UpdateResizeGhostText(ghostH, newStartMin, extTextEnd);

                    // Серединные дни и хвост события за колонкой нового старта.
                    if (hasLimits || CalendarSettings.MultiDayGrid)
                        UpdateSegGhosts(_resizeEntry.Ev, AtMinute(targetDate, newStartMin),
                            _resizeEntry.Ev.EndDt, logicalCol, colW);
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
                    var sameDayEndHere = DateOnly.FromDateTime(_resizeEntry.Ev.EndDt) == resizeColDay;
                    var maxStart = sameDayEndHere
                        ? effectiveEndMin - 30
                        : Math.Min(1440, 1440 + actualEndMin - 30);
                    // Рабочие часы: ghost прижат к началу дня, текст показывает фактический
                    // старт (излишек перетекает на конец предыдущего видимого дня).
                    var rawStart = hasLimits
                        ? Math.Min(rawYeff, maxStart)
                        : Math.Clamp(timeSnapped, 0, maxStart);

                    // Зона за концом дня: старт перетекает в следующий видимый день —
                    // ghost переезжает в следующую колонку, правый сегмент уменьшается
                    // на глазах (зеркало коммита, вместо 13px-ползунка у низа колонки).
                    if (hasLimits && !sameDayEndHere && rawStart >= dayE)
                    {
                        var sCol = _resizeEntry.DayCol + 1;
                        var sDay = CalendarMath.DayAtCol(_currentDays, sCol, FiveDay);
                        var endsThere = DateOnly.FromDateTime(_resizeEntry.Ev.EndDt)
                            == DateOnly.FromDateTime(sDay);
                        var bottom = endsThere ? actualEndMin : dayE;
                        var sm = Math.Clamp(dayS + (rawStart - dayE),
                            dayS, Math.Max(dayS, bottom - 30));
                        if (sCol < _currentDays.Count)
                        {
                            var h = Math.Max(13, bottom - sm - 2);
                            Canvas.SetLeft(_resizeGhost, sCol * colW + 1.0);
                            _resizeGhost.Width = colW - 3.0;
                            Canvas.SetTop(_resizeGhost, sm + 2);
                            _resizeGhost.Height = h;
                            // Текст — фактические края: конец может лежать дальше.
                            UpdateResizeGhostText(h, sm, endsThere ? bottom : actualEndMin);
                        }
                        else _resizeGhost.IsVisible = false;

                        // Серединные дни и хвост события за колонкой нового старта.
                        UpdateSegGhosts(_resizeEntry.Ev, AtMinute(sDay, sm), _resizeEntry.Ev.EndDt, sCol, colW);
                        if (_resizeGhostOverflow != null)
                        {
                            DragOverlay.Children.Remove(_resizeGhostOverflow);
                            _resizeGhostOverflow = null;
                        }
                        return;
                    }
                    // Вернуть ghost в свою колонку (мог переезжать в соседнюю выше).
                    var homeBorder = _resizeEntry.DayCol > 0 ? 1.0 : 0.0;
                    Canvas.SetLeft(_resizeGhost, _resizeEntry.DayCol * colW + homeBorder);
                    _resizeGhost.Width = colW - 2.0 - homeBorder;

                    var startTarget = hasLimits
                        ? CalendarMath.ResizeStartTarget(_currentDays[_resizeEntry.DayCol], rawStart,
                              effS, dayE, FiveDay)
                        : (Day: _currentDays[_resizeEntry.DayCol], Min: rawStart);
                    var startT = startTarget.Min;
                    var newStartMin = hasLimits
                        ? Math.Clamp(rawStart, Math.Min(effS, maxStart), maxStart)
                        : rawStart;
                    // Низ ghost — как у отрисованного сегмента: продолжение в следующий
                    // день при лимитах заканчивается в конце рабочего дня, не в полночь.
                    var ghostBottom = effectiveEndMin;
                    if (hasLimits && DateOnly.FromDateTime(_resizeEntry.Ev.EndDt) != resizeColDay)
                    {
                        var startHere = DateOnly.FromDateTime(_resizeEntry.Ev.StartDt) == resizeColDay
                            ? _resizeEntry.Ev.StartDt.Hour * 60 + _resizeEntry.Ev.StartDt.Minute : dayS;
                        if (startHere < dayE) ghostBottom = dayE;
                    }
                    var ghostH = Math.Max(13, ghostBottom - newStartMin - 2);
                    Canvas.SetTop(_resizeGhost, newStartMin + 2);
                    _resizeGhost.Height = ghostH;
                    // Текст — фактические края события: конец может лежать в другом дне.
                    var textEnd = hasLimits && !sameDayEndHere ? actualEndMin : ghostBottom;
                    UpdateResizeGhostText(ghostH, startT, textEnd);

                    var svH = TimeScroll.Bounds.Height;
                    if (svH > 0 && newStartMin + 2 < TimeScroll.Offset.Y + 30)
                        TimeScroll.Offset = TimeScroll.Offset.WithY(Math.Max(0, newStartMin - 28));

                    // Ghost-сегменты в остальных колонках: перетекший выше начала дня
                    // старт, серединные дни и неизменный хвост события — растягивание
                    // работает через несколько дней, все блоки видимы.
                    if (hasLimits || CalendarSettings.MultiDayGrid)
                        UpdateSegGhosts(_resizeEntry.Ev, AtMinute(startTarget.Day, startTarget.Min),
                            _resizeEntry.Ev.EndDt, _resizeEntry.DayCol, colW);
                    if (_resizeGhostOverflow != null)
                    {
                        DragOverlay.Children.Remove(_resizeGhostOverflow);
                        _resizeGhostOverflow = null;
                    }
                }
                else
                {
                    var ghostTop    = hasLimits ? effS : 0;
                    var ghostBottom = effectiveEndMin;
                    if (hasLimits && DateOnly.FromDateTime(_resizeEntry.Ev.EndDt) != resizeColDay)
                        ghostBottom = dayE;
                    var ghostH = Math.Max(13, ghostBottom - ghostTop - 2);
                    Canvas.SetTop(_resizeGhost, ghostTop + 2);
                    _resizeGhost.Height = ghostH;
                    UpdateResizeGhostText(ghostH, ghostTop, ghostBottom);

                    var overflowStart      = isOverflowUp && rawPointerCol >= _resizeEntry.DayCol
                        ? Math.Clamp(1440 + rawY, 0, 1425) : Math.Clamp(timeSnapped, 0, 1425);
                    var logicalOverflowCol = isOverflowUp && rawPointerCol >= _resizeEntry.DayCol
                        ? _resizeEntry.DayCol - 1 : rawPointerCol;

                    if (hasLimits)
                    {
                        // Старт в ранней колонке остается в ее рабочих часах; сегменты
                        // прочих колонок (вплоть до хвоста события) — общая раскладка.
                        overflowStart = Math.Clamp(overflowStart, Math.Min(dayS, dayE - 15), dayE - 15);
                        var cStartDay = CalendarMath.DayAtCol(_currentDays, logicalOverflowCol, FiveDay);
                        UpdateSegGhosts(_resizeEntry.Ev, AtMinute(cStartDay, overflowStart),
                            _resizeEntry.Ev.EndDt, _resizeEntry.DayCol, colW);
                        // Текст — фактические края события.
                        var textEnd = DateOnly.FromDateTime(_resizeEntry.Ev.EndDt) == resizeColDay
                            ? effectiveEndMin : actualEndMin;
                        UpdateResizeGhostText(ghostH, overflowStart, textEnd);
                        if (_resizeGhostOverflow != null)
                        {
                            DragOverlay.Children.Remove(_resizeGhostOverflow);
                            _resizeGhostOverflow = null;
                        }
                    }
                    else if (CalendarSettings.MultiDayGrid)
                    {
                        // «Длинные события в сетке»: сегменты прочих колонок (старт в
                        // ранней, серединные дни, хвост) — общая раскладка по суткам.
                        var cStartDay = CalendarMath.DayAtCol(_currentDays, logicalOverflowCol, FiveDay);
                        UpdateSegGhosts(_resizeEntry.Ev, AtMinute(cStartDay, overflowStart),
                            _resizeEntry.Ev.EndDt, _resizeEntry.DayCol, colW);
                        if (_resizeGhostOverflow != null)
                        {
                            DragOverlay.Children.Remove(_resizeGhostOverflow);
                            _resizeGhostOverflow = null;
                        }
                    }
                    else
                    {
                        var overflowEnd     = 1440;
                        var overflowVisible = logicalOverflowCol >= 0 && logicalOverflowCol < _currentDays.Count;
                        var prevLeft        = logicalOverflowCol > 0 ? logicalOverflowCol * colW + 1.0 : 0.0;

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
                                Math.Max(13, overflowEnd - overflowStart - 2), colW - 3.0);
                            Canvas.SetLeft(_resizeGhostOverflow, prevLeft);
                            Canvas.SetTop(_resizeGhostOverflow, overflowStart + 2);
                            DragOverlay.Children.Add(_resizeGhostOverflow);
                        }
                        else
                        {
                            _resizeGhostOverflow.Height = Math.Max(13, overflowEnd - overflowStart - 2);
                            Canvas.SetTop(_resizeGhostOverflow, overflowStart + 2);
                            Canvas.SetLeft(_resizeGhostOverflow, prevLeft);
                        }
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

            var colW       = ColW();
            var borderLeft = _dragSourceCol > 0 ? 1.0 : 0.0;
            var ghostW     = colW - 2.0 - borderLeft;
            var ghostH     = _dragSourceBtn.Bounds.Height;
            _dragGhostOriginalHeight = ghostH;

            // снимаем выделение со всех событий (drag отменяет multi-select) и
            // делаем все сегменты перетаскиваемого события полупрозрачными
            _Deselect();
            _dragDimmedBtns = _eventEntries
                .Where(e => e.Ev.Id == _dragEvent!.Id && e.Ev.FieldId == _dragEvent!.FieldId)
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
            var colW      = ColW();
            var rawCol    = colW > 0 ? (int)Math.Floor(pos.X / colW) : 0;
            var col       = Math.Clamp(rawCol, 0, _currentDays.Count - 1);
            var rawY      = pos.Y - _dragOffsetY; // может быть отрицательным (overflow из предыдущего дня)
            var duration  = _dragWorkingLen;      // длительность по рабочей ленте
            var snapped   = (int)Math.Floor(rawY / 15) * 15;
            var dragDayS  = CalendarSettings.DayStartMinutes;
            var dragDayE  = CalendarSettings.DayEndMinutes;

            var borderLeft = col > 0 ? 1.0 : 0.0;
            Canvas.SetLeft(_dragGhost, col * colW + borderLeft);

            if (CalendarMath.HasDayLimits(dragDayS, dragDayE) || CalendarSettings.MultiDayGrid)
            {
                // Рабочие часы: позиция и хвост считаются по рабочей ленте — старт в
                // «мертвой» зоне перетекает на соседний видимый день (соседнюю колонку),
                // хвост за концом дня продолжается с начала следующего дня. При «длинных
                // событиях в сетке» лента (окно 0/1440 = сутки) рисует все сегменты.
                var (shift, m) = CalendarMath.NormalizeDragStart(snapped, dragDayS, dragDayE);
                var lastCol  = _currentDays.Count - 1;
                var startCol = col + shift;
                var startDay = CalendarMath.DayAtCol(_currentDays, startCol, FiveDay);
                var (endDay, endM) = CalendarMath.AddWorkingMinutes(startDay, m, duration,
                    dragDayS, dragDayE, FiveDay);
                var seg1H    = Math.Min(duration, dragDayE - m) - 2;

                // Основной ghost — только в колонке старта; если старт перетек за
                // последнюю видимую колонку (следующий день вне экрана) — сверху той же
                // колонки ничего не рисуем, перенос виден после отпускания.
                var mainCol = startCol;
                if (mainCol >= 0 && mainCol <= lastCol && seg1H >= 11)
                {
                    var mainBorder = mainCol > 0 ? 1.0 : 0.0;
                    Canvas.SetLeft(_dragGhost, mainCol * colW + mainBorder);
                    Canvas.SetTop(_dragGhost, m);
                    _dragGhost.Height    = Math.Max(13, seg1H);
                    _dragGhost.IsVisible = true;
                    UpdateDragGhostText(_dragGhost.Height, m, endM);
                }
                else _dragGhost.IsVisible = false;

                // Хвост за концом дня, включая серединные дни, — общая раскладка
                // сегментов (перетаскивание многодневного события показывает все блоки).
                UpdateSegGhosts(_dragEvent!, AtMinute(startDay, m), AtMinute(endDay, endM),
                    mainCol, colW);
                if (_dragGhostOverflow != null)
                {
                    DragOverlay.Children.Remove(_dragGhostOverflow);
                    _dragGhostOverflow = null;
                }
            }
            else if (snapped < 0 && col > 0)
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
                // Воскресенье → следующий понедельник: col+1 вне экрана — ghost скрываем,
                // сверху той же колонки ничего не рисуем (перенос виден после отпускания).
                _dragGhost.IsVisible = false;
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
            var rawYeff       = rawY + _resizeVirtualOffset; // фактический край события на ленте
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
                var dayS       = CalendarSettings.DayStartMinutes;
                var dayE       = CalendarSettings.DayEndMinutes;
                var hasLimits  = CalendarMath.HasDayLimits(dayS, dayE);
                // Рабочие часы: вертикальный overflow в той же колонке идет через рабочую
                // ленту (перескок за конец дня), а не через «+1 колонка от полуночи».
                var canExtendRight = (rawPointerCol > _resizeEntry.DayCol || isOverflowDown)
                                     && !(hasLimits && rawPointerCol <= _resizeEntry.DayCol);
                // При лимитах конец, ушедший выше начала рабочего дня, перетекает на
                // конец предыдущего видимого дня (зеркало превью).
                var canShrinkLeft  = (rawPointerCol < _resizeEntry.DayCol || (!hasLimits && rawY < 0)
                                      || (hasLimits && rawYeff <= dayS))
                                     && startIsPrevDay;

                if (canShrinkLeft)
                {
                    // Зеркало превью: вертикальный ход по ленте может перетечь назад
                    // через несколько дней.
                    var logicalCol = rawPointerCol < _resizeEntry.DayCol
                        ? rawPointerCol : _resizeEntry.DayCol - 1;
                    int targetTime;
                    if (rawPointerCol < _resizeEntry.DayCol) targetTime = timeSnapped;
                    else if (hasLimits)
                    {
                        // Не раньше дня старта: дальше конец держит минимум 30 минут.
                        var window  = Math.Max(15, dayE - dayS);
                        var startDo = DateOnly.FromDateTime(ev.StartDt);
                        targetTime = rawYeff + window;
                        while (targetTime <= dayS && DateOnly.FromDateTime(
                                   CalendarMath.DayAtCol(_currentDays, logicalCol, FiveDay)) > startDo)
                        {
                            logicalCol--;
                            targetTime += window;
                        }
                        targetTime = Math.Clamp(targetTime, 0, dayE);
                    }
                    else targetTime = Math.Clamp(1440 + rawY, 0, 1440);
                    var targetDay      = CalendarMath.DayAtCol(_currentDays, logicalCol, FiveDay);
                    var evStartDay     = DateOnly.FromDateTime(ev.StartDt);
                    var targetDateOnly = DateOnly.FromDateTime(targetDay);
                    var minEnd         = evStartDay == targetDateOnly ? actualStartMin + 30 : 30;
                    // При лимитах конец не выходит за рабочие часы целевого дня.
                    var maxEnd         = hasLimits
                        ? Math.Max(minEnd, CalendarMath.EffectiveDayEnd(
                              evStartDay == targetDateOnly ? actualStartMin : 0, dayE))
                        : 1440;
                    var newEndMin      = Math.Clamp(targetTime, minEnd, maxEnd);
                    ev.EndDt = newEndMin >= 1440
                        ? targetDay.Date.AddDays(1)
                        : new DateTime(targetDay.Year, targetDay.Month, targetDay.Day,
                                       newEndMin / 60, newEndMin % 60, 0);
                }
                else if (!canExtendRight)
                {
                    var sameDayStart = DateOnly.FromDateTime(ev.StartDt) == resizeColDay;
                    var minEnd = sameDayStart
                        ? effectiveStartMin + 30
                        : Math.Max(0, 30 - (1440 - actualStartMin));
                    // Рабочие часы: верхнего зажима нет — излишек за концом дня (в том
                    // числе за низом сетки) перетекает на следующий видимый день. Событие,
                    // начатое впритык к концу дня или позже (вне часов), лимита не имеет
                    // (EffectiveDayEnd) — иначе минимум 30 минут заклинил бы ручку.
                    var rawEnd = hasLimits
                        ? Math.Max(rawYeff, minEnd)
                        : Math.Clamp(timeSnapped, minEnd, 1440);
                    var (endDay, endMin) = CalendarMath.ResizeEndTarget(resizeDay, rawEnd,
                        dayS, CalendarMath.EffectiveDayEnd(effectiveStartMin, dayE), FiveDay);
                    ev.EndDt = endMin >= 1440
                        ? endDay.Date.AddDays(1)
                        : new DateTime(endDay.Year, endDay.Month, endDay.Day, endMin / 60, endMin % 60, 0);
                }
                else if (hasLimits)
                {
                    // Указатель в поздней колонке: конец — в ее дне по рабочей ленте
                    // (ниже низа сетки — перетекает дальше через ResizeEndTarget).
                    var baseDay = CalendarMath.DayAtCol(_currentDays, rawPointerCol, FiveDay);
                    var rawT    = isOverflowDown ? rawY : Math.Max(timeSnapped, dayS + 15);
                    var (endDay, endMin) = CalendarMath.ResizeEndTarget(baseDay, rawT, dayS, dayE, FiveDay);
                    ev.EndDt = endMin >= 1440
                        ? endDay.Date.AddDays(1)
                        : new DateTime(endDay.Year, endDay.Month, endDay.Day, endMin / 60, endMin % 60, 0);
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
                    var endDay = CalendarMath.DayAtCol(_currentDays, _resizeEntry.DayCol + dayOffset, FiveDay);
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
                var dayS       = CalendarSettings.DayStartMinutes;
                var dayE       = CalendarSettings.DayEndMinutes;
                var hasLimits  = CalendarMath.HasDayLimits(dayS, dayE);
                var sameDayEnd = DateOnly.FromDateTime(ev.EndDt) == resizeColDay;
                // Рабочие часы: вертикальный overflow в той же колонке идет через рабочую
                // ленту (перескок за границу дня), а не через «±1 колонка от полуночи».
                var canExtendLeft  = (rawPointerCol < _resizeEntry.DayCol || isOverflowUp)
                                     && !(hasLimits && rawPointerCol >= _resizeEntry.DayCol);
                var canExtendRight = (rawPointerCol > _resizeEntry.DayCol || isOverflowDown)
                                     && !canExtendLeft
                                     && !(hasLimits && rawPointerCol <= _resizeEntry.DayCol);

                if (canExtendRight && hasLimits)
                {
                    // Указатель в поздней колонке: старт — в ее дне по рабочей ленте
                    // (зона за концом дня перетекает на следующий видимый день).
                    var baseDay = CalendarMath.DayAtCol(_currentDays, rawPointerCol, FiveDay);
                    var t       = isOverflowDown ? rawY : timeSnapped;
                    if (t > dayE)
                    {
                        baseDay = CalendarMath.NextVisibleDay(baseDay, FiveDay);
                        t       = Math.Min(dayS + (t - dayE), dayE);
                    }
                    else t = Math.Clamp(t, Math.Min(dayS, dayE - 15), dayE - 15);
                    if (DateOnly.FromDateTime(baseDay) == DateOnly.FromDateTime(ev.EndDt))
                        t = Math.Max(0, Math.Min(t, actualEndMin - 30));
                    ev.StartDt = new DateTime(baseDay.Year, baseDay.Month, baseDay.Day,
                                              t / 60, t % 60, 0);
                }
                else if (canExtendRight)
                {
                    var logicalCol = isOverflowDown && rawPointerCol <= _resizeEntry.DayCol
                        ? _resizeEntry.DayCol + 1 : rawPointerCol;
                    var targetTime     = isOverflowDown && rawPointerCol <= _resizeEntry.DayCol
                        ? Math.Clamp(rawY - 1440, 0, 1440)
                        : timeSnapped;
                    var targetDay      = CalendarMath.DayAtCol(_currentDays, logicalCol, FiveDay);
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
                        var nextDay = CalendarMath.NextVisibleDay(resizeDay, FiveDay);
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
                            CalendarMath.EffectiveDayStart(effectiveEndMin, dayS), dayE, FiveDay);
                        ev.StartDt = startMin >= 1440
                            ? startDay.Date.AddDays(1)
                            : new DateTime(startDay.Year, startDay.Month, startDay.Day,
                                           startMin / 60, startMin % 60, 0);
                    }
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
                    var startDay = CalendarMath.DayAtCol(_currentDays, _resizeEntry.DayCol - dayOffset, FiveDay);
                    // При лимитах старт в ранней колонке остается в ее рабочих часах.
                    if (hasLimits)
                        startTimeMin = Math.Clamp(startTimeMin, Math.Min(dayS, dayE - 15), dayE - 15);
                    ev.StartDt = new DateTime(startDay.Year, startDay.Month, startDay.Day,
                                              startTimeMin / 60, startTimeMin % 60, 0);
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
            ClearSegGhosts();
            _resizeEntry.Btn.Opacity = 1.0;
            foreach (var b in _resizeHiddenBtns) b.Btn.Opacity = 1.0;
            _resizeHiddenBtns.Clear();

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
                var colW  = ColW();
                var col   = colW > 0 ? (int)(pos.X / colW) : 0;
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
            var colW     = ColW();
            var rawCol   = colW > 0 ? (int)Math.Floor(pos.X / colW) : 0;
            var rawY     = pos.Y - _dragOffsetY;
            var duration = _dragWorkingLen;
            var snapped  = (int)Math.Floor(rawY / 15) * 15;
            var dayS     = CalendarSettings.DayStartMinutes;
            var dayE     = CalendarSettings.DayEndMinutes;

            // Колонка → дата ТОЛЬКО через DayAtCol: за краями окна экстраполяция идет
            // видимыми днями (при 5-дневке за пятницей — понедельник), а окно «по дню»
            // и вовсе не непрерывно — арифметика AddDays от days[0] здесь лжет.
            // Старт и хвост — по рабочей ленте: позиция вне границ дня перетекает на
            // соседний видимый день, хвост события — тоже (Пт 23:00 + 2 ч при 5-дневке
            // = Пн 01:00, а не невидимая суббота; 17:00 + 2 ч при 09–18 = 10:00 завтра).
            var (shift, m) = CalendarMath.NormalizeDragStart(snapped, dayS, dayE);
            var day = CalendarMath.DayAtCol(_currentDays, rawCol + shift, FiveDay);
            var (endDay, endMin) = CalendarMath.AddWorkingMinutes(day, m, duration, dayS, dayE, FiveDay);

            _dragEvent.StartDt = new DateTime(day.Year, day.Month, day.Day, m / 60, m % 60, 0);
            _dragEvent.EndDt   = endMin >= 1440
                ? endDay.Date.AddDays(1)
                : new DateTime(endDay.Year, endDay.Month, endDay.Day, endMin / 60, endMin % 60, 0);

            _pendingSelectId = (_dragEvent.Id ?? 0, _dragEvent.FieldId);
            RecordMoved?.Invoke(this, _dragEvent);
        }

        e.Pointer.Capture(null);
        if (_dragGhost is not null) DragOverlay.Children.Remove(_dragGhost);
        if (_dragGhostOverflow != null)
        {
            DragOverlay.Children.Remove(_dragGhostOverflow);
            _dragGhostOverflow = null;
        }
        ClearSegGhosts();
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
                // ghost стал достаточно большим — создаем TextBlock времени динамически
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
            FontWeight   = WeightBold,
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

    // смешивает цвет с белым фоном чтобы убрать прозрачность (alpha blending)
    // Лимит ПАРАЛЛЕЛЬНЫХ под-колонок пересекающихся событий (см. DayView.MaxEventColumns —
    // здесь жестче: колонка дня в 7 раз уже). День-гигант давал 2000 под-колонок = тысячи
    // субпиксельных контролов и подвисание UI на каждую перерисовку.
    private const int MaxEventColumns = 6;

    private static List<List<CalendarRecord>> GroupOverlapping(List<CalendarRecord> events)
    {
        if (events.Count == 0) return [];
        var evs    = events.OrderBy(e => e.StartDt).ThenBy(e => e.Title).ToList();
        var groups = new List<List<CalendarRecord>>();
        var cur    = new List<CalendarRecord> { evs[0] };
        // Старты отсортированы → «пересекается с кем-то из cur» ⇔ StartDt < max(EndDt по cur):
        // бегущий максимум конца вместо O(n²)-перебора cur.Any на длинных цепочках.
        var curMaxEnd = evs[0].EndDt;
        for (var i = 1; i < evs.Count; i++)
        {
            var ev = evs[i];
            if (ev.StartDt < curMaxEnd)
                cur.Add(ev);
            else
            {
                groups.Add(cur);
                cur = [ev];
                curMaxEnd = ev.EndDt;
            }
            if (ev.EndDt > curMaxEnd) curMaxEnd = ev.EndDt;
        }
        groups.Add(cur);
        return groups;
    }

    // Returns list of (ev, colIdx, nCols, rightLimit)
    // pinned — выделенные: они раскладываются первыми, в левые колонки. На переполненном дне
    // (пересечений больше лимита колонок) перенесенное, созданное или скопированное событие иначе
    // могло уйти за лимит и пропасть с экрана вместе с выделением.
    private static List<(CalendarRecord Ev, int ColIdx, int NCols, int RightLimit)> AssignColumns(
        List<CalendarRecord> group, IReadOnlySet<(int DbId, int Id, int FieldId)>? pinned = null)
    {
        var evs     = group.OrderBy(e => e.StartDt).ThenBy(e => -(e.EndDt - e.StartDt).Ticks).ToList();
        var colEnds = new List<DateTime>();
        var evCol   = new Dictionary<CalendarRecord, int>(ReferenceEqualityComparer.Instance);

        bool IsPinned(CalendarRecord e) => pinned is { Count: > 0 } && e.Id is int id && pinned.Contains((e.DbId, id, e.FieldId));
        void Place(CalendarRecord ev, int from)
        {
            var col = -1;
            for (var i = from; i < colEnds.Count; i++)
                if (ev.StartDt >= colEnds[i]) { col = i; break; }
            if (col == -1) { col = colEnds.Count; colEnds.Add(ev.EndDt); }
            else colEnds[col] = ev.EndDt;
            evCol[ev] = col;
        }
        var firstFree = 0;
        if (pinned is { Count: > 0 })
        {
            foreach (var ev in evs)
                if (IsPinned(ev)) Place(ev, 0);
            firstFree = colEnds.Count;
        }
        foreach (var ev in evs)
            if (!evCol.ContainsKey(ev)) Place(ev, firstFree);

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

    private void BuildHeader(List<DateTime> days)
    {
        WeekHeaderDays.Children.Clear();

        for (var i = 0; i < days.Count; i++)
        {
            var d         = days[i];
            var isToday   = d.Date == DateTime.Today;
            // Подпись и «красность» — по дню недели, не по индексу: окно «по дню»
            // не выровнено на понедельник, а при 5-дневке выходных колонок нет.
            var isWeekend = CalendarMath.IsWeekend(d);

            var fg = isToday   ? new SolidColorBrush(Color.Parse("#007AFF"))
                   : isWeekend ? new SolidColorBrush(Color.Parse("#FF3B30"))
                               : new SolidColorBrush(Color.Parse("#8E8E93"));

            WeekHeaderDays.Children.Add(new TextBlock
            {
                Text                = $"{DayNames[((int)d.DayOfWeek + 6) % 7]} {d.Day}",
                FontSize            = FontSizeBase,
                FontWeight          = isToday ? WeightBold : WeightNormal,
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

            // Множественное выделение (это событие в общем наборе обеих зон и в нем 2+):
            // массовые «Дублировать»/«Удалить» всех выделенных разом — как по Delete.
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

    // Меню пустого места сетки: «Создать запись» в выбранный день/время.
    // Если правый клик попал на событие — отменяем открытие, чтобы показалось
    // меню события (оно висит на своей кнопке отдельным ContextMenu).
    private ContextMenu _BuildGridContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opening += (_, args) =>
        {
            var colW = ColW();
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
            // Рабочие часы: новое событие укладывается в период дня.
            minutes = CalendarMath.ClampStartMin(minutes, CalendarSettings.NewEventDurationMinutes,
                CalendarSettings.DayStartMinutes, CalendarSettings.DayEndMinutes);
            var day     = _currentDays[col];
            var dt      = new DateTime(day.Year, day.Month, day.Day, minutes / 60, minutes % 60, 0);

            menu.Items.Clear();
            menu.Items.Add(SmartBase.Controls.Components.EventContextMenu.MakeCreateItem(
                () => TimeSlotCreationRequested?.Invoke(this, dt), Controls.Components.EventContextMenu.Keys.Calendar.Create, this));
        };
        return menu;
    }
}

// Скрывает сплошную линию для часа 0 (00:00), но оставляет пунктир 00:30.
public class HourLineBorderConverter : IValueConverter
{
    public static readonly HourLineBorderConverter Instance = new();

    // Толщина — компенсированный токен главного окна (у конвертера нет визуала);
    // значение на момент пересборки строк, часы пересоздаются при Refresh.
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int h && h == 0 ? new Thickness(0) : new Thickness(0, LineMetrics.MainThickness, 0, 0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
