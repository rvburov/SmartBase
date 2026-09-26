using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Core.Calendar;
using SmartBase.Core.Models;
using SmartBase.Utils;
using SmartBase.Controls.Behaviors;

namespace SmartBase.Views.ConstructorDb.Calendar;

public partial class MonthView : UserControl
{
    // Размеры/веса шрифта — из токенов Typography.axaml (резолв ресурса в рантайме).
    // Свежий размер из Application.Resources — меняется живьем при изменении FontScale.
    private static double FontSizeBase =>
        (double)Application.Current!.FindResource("FontSizeBase")!;

    private static readonly FontWeight WeightBold   = (FontWeight)Application.Current!.FindResource("FontWeightBold")!;
    private static readonly FontWeight WeightNormal = (FontWeight)Application.Current!.FindResource("FontWeightNormal")!;

    public event EventHandler<DateTime>? DaySelected;
    public event EventHandler<DateTime>? DayAddClicked;
    public event EventHandler<CalendarRecord>? RecordClicked;
    // Перенос между днями: запись уже с НОВЫМИ датами + исходные даты — для точечной
    // дельты в контроллере (−1 старому дню, +1 новому, включая карту счетчиков «+N еще»).
    public event EventHandler<(CalendarRecord Rec, DateTime OldStart, DateTime OldEnd)>? RecordMoved;
    public event EventHandler<CalendarRecord>? RecordDeleteRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDeleteRequested;
    public event EventHandler<CalendarRecord>? RecordDuplicateRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDuplicateRequested;

    // Константы макета (высоты элементов ячейки дня и полос событий).
    private const double DateH   = MonthCellsSurface.DateH;
    private const double BarH    = MonthCellsSurface.BarH;
    private const double BarStep = MonthCellsSurface.BarStep;
    private const double MoreH   = MonthCellsSurface.MoreH;
    private const double BPad    = MonthCellsSurface.BPad;

    private List<CalendarRecord> _events      = [];
    private DateTime    _currentDate = DateTime.Today;
    private DateTime    _gridStart;

    // Drag state
    private CalendarRecord?    _dragEv;
    private bool      _dragActive;
    private Point     _dragPress;
    private DateTime  _dragOrigStart;
    private DateTime  _dragOrigEnd;
    private DateTime  _dragSourceDay;
    private DateTime  _dragTargetDay;
    private IPointer? _dragPointer;
    private MonthChip? _dragSrc;   // плашка или сегмент полосы, за который тащат
    private Grid?     _dragGhost;
    private DateTime? _shiftedDay;
    private bool      _srcCounterShifted; // в исходном дне «+N еще» временно уменьшен на утащенное
    private int       _pressClickCount;
    private bool      _skipNextDeselect;

    // Выделенные события → действие снятия их подсветки. Множественное выделение
    // (Shift+клик) копит несколько; одиночный клик оставляет одно.
    private readonly Dictionary<CalendarRecord, Action> _selections = new();

    // Ячейки месяца по дате (раскладка и «+N еще» — для счетчиков при перетаскивании).
    private readonly Dictionary<DateTime, MonthCell> _dayUiMap = new();
    // Две рисованные поверхности вместо контролов ячеек, плашек и полос (см. MonthSurfaces).
    private readonly MonthCellsSurface _cells = new();
    private readonly MonthBarsSurface  _bars  = new();
    private DateTime? _hoveredDay;
    private DateTime? _hoveredCircleDay;
    private readonly HashSet<int> _selectedSpanIds = [];

    private EventHandler<PointerPressedEventArgs>? _windowClickHandler;
    private EventHandler<KeyEventArgs>?            _windowKeyHandler;

    public MonthView()
    {
        InitializeComponent();
        // Ячейки — под линиями сетки, полосы — над ними (полоса закрывает линии белой подложкой),
        // призрак перетаскивания — на холсте поверх всего.
        GridContainer.Children.Insert(0, _cells);
        GridContainer.Children.Insert(GridContainer.Children.IndexOf(SpanCanvas), _bars);
        _cells.IsSelected = ev => _selections.ContainsKey(ev);
        _bars.IsSelected  = ev => ev.Id.HasValue && _selectedSpanIds.Contains(ev.Id.Value);
        _cells.PointerPressed  += OnCellsPressed;
        _cells.PointerMoved    += (_, e) => _OnDragMoved(e);
        _cells.PointerReleased += OnChipReleased;
        _bars.PointerPressed   += OnBarsPressed;
        _bars.PointerMoved     += (_, e) => _OnDragMoved(e);
        _bars.PointerReleased  += OnChipReleased;
        // Меню правой кнопкой — по месту нажатия: над плашкой — меню события, мимо — меню дня.
        _cells.AddHandler(PointerPressedEvent, OnCellsRightPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _bars.AddHandler(PointerPressedEvent, OnBarsRightPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        GridContainer.SizeChanged    += (_, _) => _BuildAll();
        GridContainer.PointerMoved   += (_, e) => { var pos = e.GetPosition(GridContainer); _UpdateCellHover(_DayAtPos(pos)); _UpdateCircleHover(pos); };
        GridContainer.PointerExited  += (_, _) => { _UpdateCellHover(null); _UpdateCircleHover(null); };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (e.Root is Avalonia.Controls.Window win)
        {
            _windowClickHandler = (_, e) =>
            {
                // Правый клик не снимает выделение: он либо открывает меню на событии
                // (там выделение только что выставлено), либо обрабатывается ячейкой.
                // Снятие — только на левый клик мимо события.
                if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
                if (_skipNextDeselect) { _skipNextDeselect = false; return; }
                _Deselect();
            };
            // handledEventsToo: чип/бар помечают PointerPressed как Handled, поэтому
            // без этого флага окно не увидит клик по событию и не сбросит
            // _skipNextDeselect — тогда следующий клик по пустому дню «съедается»
            // вместо снятия выделения.
            win.AddHandler(PointerPressedEvent, _windowClickHandler, handledEventsToo: true);

            _windowKeyHandler = (_, ke) =>
            {
                if (!IsVisible) return;
                // Клавишу уже забрала боковая панель (удаление коллекции) — второй раз не наша.
                if (KeyClaim.Claimed(ke)) return;
                if (_selections.Count == 0) return;
                // Не перехватываем клавиши внутри текстовых полей (диалог правки и т.п.).
                if (ke.Source is TextBox or AutoCompleteBox) return;

                if (ShortcutSettings.Matches("calendar.deleteSelection", ke))
                {
                    if (_selections.Count == 1)
                    {
                        var only = _selections.Keys.First();
                        _Deselect();
                        RecordDeleteRequested?.Invoke(this, only);
                    }
                    else
                    {
                        var batch = _selections.Keys.ToList();
                        _Deselect();
                        RecordsDeleteRequested?.Invoke(this, batch);
                    }
                    ke.Handled = true;
                }
                else if (ShortcutSettings.Matches("calendar.duplicateSelection", ke))
                {
                    var batch = _selections.Keys.ToList();
                    _Deselect();
                    if (batch.Count == 1) RecordDuplicateRequested?.Invoke(this, batch[0]);
                    else                  RecordsDuplicateRequested?.Invoke(this, batch);
                    ke.Handled = true;
                }
                else if (ShortcutSettings.Matches("calendar.openEvent", ke))
                {
                    RecordClicked?.Invoke(this, _selections.Keys.First());
                    ke.Handled = true;
                }
            };
            win.AddHandler(KeyDownEvent, _windowKeyHandler, handledEventsToo: true);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (e.Root is Avalonia.Controls.Window win)
        {
            if (_windowClickHandler is not null)
            {
                win.RemoveHandler(PointerPressedEvent, _windowClickHandler);
                _windowClickHandler = null;
            }
            if (_windowKeyHandler is not null)
            {
                win.RemoveHandler(KeyDownEvent, _windowKeyHandler);
                _windowKeyHandler = null;
            }
        }
    }

    // Карта счетчиков дней (день → сколько событий СТАРТУЕТ в нем): при ограниченном чтении
    // окна день-гигант отдает сетке только первые GridDayCap строк — недоставленные события
    // не попадают в _events вовсе, и честный «+N еще» получается только из карты. null —
    // чтение без ограничения (или поиск), скрытые считаются от фактически полученного.
    private IReadOnlyDictionary<DateOnly, int>? _dayTotals;

    public void Refresh(IEnumerable<CalendarRecord> events, DateTime currentDate,
                        IReadOnlyDictionary<DateOnly, int>? dayTotals = null)
    {
        _events      = events.ToList();
        _spanning    = _events.Where(IsSpanning).ToList();
        _currentDate = currentDate;
        _dayTotals   = dayTotals;
        _BuildAll();
    }

    // Многодневные и «весь день» события окна — бары. Их обычно единицы, а однодневных в
    // окне десятки тысяч (день-гигант отдает сетке до GridDayCap строк): проходы «по барам»
    // для каждой недели и каждого дня идут по этому списку, а не по всем событиям.
    private List<CalendarRecord> _spanning = [];

    private static bool IsSpanning(CalendarRecord e) => e.AllDay || e.EndDt.Date > e.StartDt.Date;

    // ── алгоритм раскладки многодневных событий по строкам ────────────────────
    // Жадный алгоритм: для каждого события берем первую строку, где ec < sc.

    private readonly record struct LayoutEntry(int RowIdx, int Sc, int Ec);

    private Dictionary<int, LayoutEntry> _ComputeWeekLayout(DateTime weekStart)
    {
        var weekEnd  = weekStart.AddDays(6).Date;
        var spanning = _spanning
            .Where(e => e.StartDt.Date <= weekEnd
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
            // 5-дневка: бары обходят выходные — сегмент клипуется пятницей, сегмент
            // целиком в Сб/Вс в этой неделе не рисуется (бар Пт–Пн распадается на
            // пятницу этой недели и понедельник следующей сам собой).
            if (CalendarSettings.FiveDayWeek)
            {
                var clip = CalendarMath.ClipSpanToWorkdays(sc, ec, fiveDay: true);
                if (clip is null) continue;
                (sc, ec) = clip.Value;
            }
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

    // Строки, занятые spanning-барами именно в день d (множество с возможными
    // дырами — бар в строке 1 может покрывать день, а строка 0 в нем свободна).
    private HashSet<int> _SpanRowsForDay(DateTime d, Dictionary<int, LayoutEntry> layout)
    {
        var rows = new HashSet<int>();
        // 5-дневка: в выходных ячейках баров нет — все строки свободны для чипов.
        if (CalendarSettings.FiveDayWeek && CalendarMath.IsWeekend(d)) return rows;
        foreach (var e in _spanning)
        {
            if (!e.Id.HasValue || !layout.TryGetValue(e.Id.Value, out var le)) continue;
            if (e.StartDt.Date <= d.Date && e.EndDt.Date >= d.Date)
                rows.Add(le.RowIdx);
        }
        return rows;
    }

    // Первая строка r >= 0, отсутствующая в занятом множестве.
    private static int _FirstFreeRow(HashSet<int> occupied)
    {
        var r = 0;
        while (occupied.Contains(r)) r++;
        return r;
    }

    // Слой линий сетки месяца (GridLines): пустые Border поверх заливок ячеек.
    // В самих ячейках текст — их алиасить нельзя (EdgeMode наследуется и переключает
    // сглаживание текста), а пустой слой Aliased защелкивает линии в целый пиксель
    // при любом зуме/DPI. Каждая «ячейка» слоя рисует левую (кроме первой колонки —
    // она граничит с разделителем сайдбара) и верхнюю линии; низ/право не рисуются —
    // линию в одном месте рисует ровно один элемент, без визуального удвоения.
    // Толщина — на момент пересборки: месяц перестраивается при каждом
    // Refresh/навигации/SizeChanged (зум меняет размер → пересборка).
    private void _BuildGridLines()
    {
        GridLines.Children.Clear();
        var lineT     = LineMetrics.ThicknessFor(this);
        var lineBrush = new SolidColorBrush(Color.Parse("#E8E8E8"));
        for (var i = 0; i < 42; i++)
        {
            var col       = i % 7;
            var cellLines = new Border
            {
                BorderBrush     = lineBrush,
                BorderThickness = new Thickness(col == 0 ? 0 : lineT, lineT, 0, 0),
            };
            cellLines.Bind(UseLayoutRoundingProperty, cellLines.GetResourceObservable("LineUseLayoutRounding"));
            RenderOptions.SetEdgeMode(cellLines, EdgeMode.Aliased);
            GridLines.Children.Add(cellLines);
        }
    }

    // ── единый построитель: ячейки + Canvas-оверлей ────────────────────────────

    private void _BuildAll()
    {
        _Deselect();
        _BuildGridLines();
        _dayUiMap.Clear();
        _selectedSpanIds.Clear();
        _cells.Cells.Clear();
        _cells.HoveredChip = null;
        _bars.Bars.Clear();
        _bars.HoveredId = null;
        _hoveredDay       = null;
        _hoveredCircleDay = null;
        _cells.HoveredDay = _cells.HoveredCircleDay = null;

        var W = GridContainer.Bounds.Width;
        var H = GridContainer.Bounds.Height;

        // Сколько событий на каждый день ДОСТАВЛЕНО (по дню старта): разница с картой
        // счетчиков — события, обрезанные ограниченным чтением, идут в «+N еще».
        Dictionary<DateTime, int>? received = null;
        if (_dayTotals is not null)
        {
            received = new Dictionary<DateTime, int>();
            foreach (var e in _events)
                received[e.StartDt.Date] = received.GetValueOrDefault(e.StartDt.Date) + 1;
        }

        // Однодневные события по дням — одним проходом, в порядке начала (сортировка устойчивая:
        // равные по времени идут в порядке чтения, как раньше при выборке на каждый день).
        var singlesByDay = _events
            .Where(e => !e.AllDay && e.EndDt.Date == e.StartDt.Date)
            .GroupBy(e => e.StartDt.Date)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.StartDt).ToList());

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

            foreach (var ev in _spanning)
            {
                if (!ev.Id.HasValue || !layout.ContainsKey(ev.Id.Value)) continue;
                if (ev.StartDt.Date > weekEnd || ev.EndDt.Date < weekStart) continue;
                if (!seen.Add(ev.Id.Value)) continue;

                var (rowIdx, sc, ec) = layout[ev.Id.Value];
                var leftCap  = ev.StartDt.Date >= weekStart;
                var rightCap = ev.EndDt.Date   <= weekEnd;
                // 5-дневка: бар обрезан пятницей, а событие продолжается дальше —
                // конец рисуем открытым (без скругления), как продолжение за неделю.
                if (CalendarSettings.FiveDayWeek && ev.EndDt.Date > weekStart.AddDays(ec))
                    rightCap = false;
                var barY     = wrow * ch + DateH + rowIdx * BarStep;

                // Не влезает — учитываем в счетчике скрытых
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
                    _bars.Add(ev, bx, barY, bw, leftCap, rightCap);
                }
            }

            // ── ячейки дней ───────────────────────────────────────────────────
            for (var col = 0; col < 7; col++)
            {
                var d   = weekStart.AddDays(col);
                var isCurrentMonth = d.Month == _currentDate.Month;
                var isToday        = d.Date == DateTime.Today;
                var isWeekend      = col >= 5;
                // 5-дневка: ячейки выходных пустые — однодневные записи Сб/Вс не
                // показываются (бары туда и так не заходят), «+N еще» не рисуется.
                var hideWeekend    = isWeekend && CalendarSettings.FiveDayWeek;

                // Строки, занятые spanning-барами в этот день (с дырами).
                var spanRows = _SpanRowsForDay(d, layout);

                // Однодневные события
                var singles = hideWeekend
                    ? []
                    : singlesByDay.GetValueOrDefault(d.Date) ?? [];

                // Последняя строка, помещающаяся по высоте: row*BarStep от DateH,
                // снизу резерв под «+N еще» (MoreH) и нижний отступ (BPad).
                var maxVisRow = ch > 0
                    ? (int)((ch - BPad - MoreH - DateH) / BarStep) - 1
                    : 4;

                // Каждому single — первая свободная строка (заполняя верхние дыры).
                // Строки, ушедшие за maxVisRow, считаются скрытыми. Строки занимаются по
                // возрастанию, поэтому первая свободная ищется бегущим курсором, а не с нуля:
                // на дне-гиганте (тысячи событий) поиск с нуля был квадратичным и держал
                // UI-поток секундами.
                var cell = new MonthCell
                {
                    Date = d, IsCurrentMonth = isCurrentMonth, IsToday = isToday, IsWeekend = isWeekend,
                    Index = wrow * 7 + col,
                };
                var occupied = new HashSet<int>(spanRows);
                var hiddenSingles = 0;
                var free = 0;
                foreach (var ev in singles)
                {
                    while (occupied.Contains(free)) free++;
                    occupied.Add(free);
                    if (free <= maxVisRow) cell.Chips.Add((new MonthChip(ev, bar: false, true, true), free));
                    else                   hiddenSingles++;
                }

                var hidden = hideWeekend ? 0 : hiddenSingles + hiddenSpans[col];

                // Обрезанные ограниченным чтением (в _events их нет) — тоже скрытые.
                if (!hideWeekend && received is not null
                    && _dayTotals!.TryGetValue(DateOnly.FromDateTime(d), out var total))
                    hidden += Math.Max(0, total - received.GetValueOrDefault(d.Date));

                cell.Occupied   = occupied;
                cell.OrigHidden = hidden;
                cell.More       = hidden > 0 ? hidden : null;
                _cells.Cells.Add(cell);
                _dayUiMap[d] = cell;
            }
        }

        _cells.Relayout(GridContainer.Bounds.Size);
        _bars.InvalidateVisual();
    }

    // ── плашки и полосы: нажатие, отпускание, меню ────────────────────────────

    // additive (Shift+клик): добавляет/убирает событие из набора, не трогая остальные. Иначе —
    // одиночное выделение (сначала снимаем все). Полоса подсвечивает все сегменты события.
    private void _SelectThis(CalendarRecord ev, bool bar, bool additive)
    {
        if (additive)
        {
            if (_selections.ContainsKey(ev)) { _DeselectOne(ev); return; }
        }
        else
        {
            _Deselect();
        }
        if (bar && ev.Id.HasValue) _selectedSpanIds.Add(ev.Id.Value);
        _selections[ev] = () =>
        {
            if (bar && ev.Id.HasValue) _selectedSpanIds.Remove(ev.Id.Value);
            _cells.InvalidateVisual();
            _bars.InvalidateVisual();
        };
        _cells.InvalidateVisual();
        _bars.InvalidateVisual();
    }

    // Левое нажатие по плашке ячейки: начало возможного перетаскивания (выделение — по отпусканию).
    private void OnCellsPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(null).Properties;
        var pos   = e.GetPosition(_cells);
        var chip  = _cells.ChipAt(pos);
        if (chip is not null)
        {
            if (props.IsLeftButtonPressed) { _skipNextDeselect = true; _BeginDragTrack(chip, _cells, e); e.Handled = true; }
            return;
        }

        // Кружок числа: «+» под указателем — создать событие в дне; двойной — перейти в день.
        if (!props.IsLeftButtonPressed || _cells.CellAt(pos) is not { } cell) return;
        if (_cells.CircleRect(cell).Contains(pos))
        {
            if (_hoveredCircleDay == cell.Date)
            {
                DayAddClicked?.Invoke(this, cell.Date);
                e.Handled = true;
                return;
            }
            if (e.ClickCount >= 2)
            {
                DaySelected?.Invoke(this, cell.Date);
                e.Handled = true;
                return;
            }
        }
        // Двойной клик по ячейке — перейти в день.
        if (e.ClickCount >= 2)
        {
            DaySelected?.Invoke(this, cell.Date);
            e.Handled = true;
        }
    }

    private void OnBarsPressed(object? sender, PointerPressedEventArgs e)
    {
        var bar = _bars.BarAt(e.GetPosition(_bars));
        if (bar is null) return;
        if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            _skipNextDeselect = true;
            _BeginDragTrack(bar, _bars, e);
        }
    }

    // Правый клик: если событие уже в множественном выделении — оставляем набор как есть (меню
    // удалит все). Иначе выделяем только это (как одиночный клик), чтобы было видно, с чем
    // работаем. Окно снимает выделение только на левый клик мимо, поэтому переключение правой
    // кнопкой подсветку не гасит. Меню — по месту: событие или день.
    private readonly Dictionary<CalendarRecord, ContextMenu> _eventMenus = new();
    private readonly Dictionary<DateTime, ContextMenu> _dayMenus = new();

    private void OnCellsRightPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed) return;
        var pos  = e.GetPosition(_cells);
        var chip = _cells.ChipAt(pos);
        if (chip is not null)
        {
            if (!_selections.ContainsKey(chip.Ev)) _SelectThis(chip.Ev, bar: false, additive: false);
            _cells.ContextMenu = MenuFor(chip.Ev);
            return;
        }
        if (_cells.CellAt(pos) is { } cell)
        {
            if (!_dayMenus.TryGetValue(cell.Date, out var menu))
                _dayMenus[cell.Date] = menu = _BuildDayContextMenu(cell.Date);
            _cells.ContextMenu = menu;
        }
        else _cells.ContextMenu = null;
    }

    private void OnBarsRightPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed) return;
        var bar = _bars.BarAt(e.GetPosition(_bars));
        if (bar is null) { _bars.ContextMenu = null; return; }
        if (!_selections.ContainsKey(bar.Ev)) _SelectThis(bar.Ev, bar: true, additive: false);
        _bars.ContextMenu = MenuFor(bar.Ev);
    }

    private ContextMenu MenuFor(CalendarRecord ev)
    {
        if (!_eventMenus.TryGetValue(ev, out var menu)) _eventMenus[ev] = menu = _BuildEventContextMenu(ev);
        return menu;
    }

    private void OnChipReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Правый клик уже обработан при нажатии (выделение + меню) — не запускаем логику по левой.
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (_dragEv is not { } ev || _dragSrc is not { } src) return;
        var wasDrag = _dragActive;
        _OnDragReleased(e);
        if (wasDrag) return;
        if (_pressClickCount >= 2)
        {
            _Deselect();
            RecordClicked?.Invoke(this, ev);
        }
        else
        {
            _SelectThis(ev, src.IsBar, additive: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        }
    }

    // ── drag-and-drop ─────────────────────────────────────────────────────────

    private void _BeginDragTrack(MonthChip chip, Control surface, PointerPressedEventArgs e)
    {
        if (_dragEv is not null) return;
        var ev = chip.Ev;
        _dragEv          = ev;
        _dragActive      = false;
        _dragSrc         = chip;
        _pressClickCount = e.ClickCount;
        _dragPress       = e.GetPosition(GridContainer);
        _dragOrigStart = ev.StartDt;
        _dragOrigEnd   = ev.EndDt;
        _dragSourceDay = ev.StartDt.Date;
        _dragTargetDay = ev.StartDt.Date;
        _dragPointer   = e.Pointer;
        e.Pointer.Capture(surface);
    }

    private void _OnDragMoved(PointerEventArgs e)
    {
        if (_dragEv is null) return;
        var pos = e.GetPosition(GridContainer);
        if (!_dragActive)
        {
            if (Math.Abs(pos.X - _dragPress.X) + Math.Abs(pos.Y - _dragPress.Y) < 6) return;
            _dragActive = true;
            _Deselect();
            // Оригинал остается прозрачным (Opacity 0.45) на исходном месте.
            if (_dragSrc is not null) { _dragSrc.Opacity = 0.45; _cells.InvalidateVisual(); _bars.InvalidateVisual(); }
        }
        var target = _DayAtPos(pos);
        if (!target.HasValue) return;
        // 5-дневка: сброс на выходной прижимается к ближайшему рабочему дню по
        // направлению перетаскивания (ghost сразу встает на прижатый день).
        var targetDay = CalendarMath.SnapToWorkday(target.Value.Date,
            Math.Sign((target.Value.Date - _dragSourceDay).Days), CalendarSettings.FiveDayWeek);
        var changed = targetDay != _dragTargetDay;
        if (changed) _dragTargetDay = targetDay;

        // Счетчик исходного дня: пока событие уведено в другой день — там одним
        // меньше; вернулось на место — счетчик восстанавливаем.
        if (_dragTargetDay != _dragSourceDay) _ShiftSourceCounter();
        else                                  _RestoreSourceCounter();

        // Ghost рисуем для любого дня под курсором — в т.ч. при возврате в исходный,
        // чтобы событие там тоже было ярким, а не пропадало.
        if (changed || _dragGhost is null) _UpdateDragGhost();
    }

    private void _OnDragReleased(PointerReleasedEventArgs e)
    {
        if (_dragEv is null) return;
        if (_dragActive && _dragTargetDay != _dragSourceDay)
        {
            var delta  = _dragTargetDay - _dragSourceDay;
            var ev     = _dragEv;
            var (origS, origE) = (_dragOrigStart, _dragOrigEnd);
            ev.StartDt = _dragOrigStart + delta;
            ev.EndDt   = _dragOrigEnd   + delta;
            // 5-дневка: старт не встает на Сб/Вс (бар мог быть схвачен за серединный
            // сегмент — прижимаем по направлению переноса), а конец многодневного —
            // по числу ВИДИМЫХ дней исходной записи: пересечение нового положения с
            // выходными растягивает календарные даты через них (Чт–Пт, перенесенная
            // на день вперед, — это Пт–Пн, а не Пт–Сб).
            if (CalendarSettings.FiveDayWeek && _dragOrigEnd.Date > _dragOrigStart.Date)
            {
                var snapped = CalendarMath.SnapToWorkday(ev.StartDt, Math.Sign(delta.Days), true);
                var span    = CalendarMath.VisibleDaysBetween(_dragOrigStart, _dragOrigEnd, true);
                ev.StartDt  = snapped + _dragOrigStart.TimeOfDay;
                ev.EndDt    = CalendarMath.AddVisibleDays(ev.StartDt, span, true) + _dragOrigEnd.TimeOfDay;
            }
            _CancelDrag();
            // Исходные даты — контроллеру: перенос между днями правится дельтой на месте
            // (−1 старому дню, +1 новому — в т.ч. карта счетчиков «+N еще»), без перезагрузки.
            RecordMoved?.Invoke(this, (ev, origS, origE));
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
        if (_dragSrc is not null) { _dragSrc.Opacity = 1.0; _dragSrc = null; _cells.InvalidateVisual(); _bars.InvalidateVisual(); }
        if (_dragGhost is not null) { SpanCanvas.Children.Remove(_dragGhost); _dragGhost = null; }
        if (_shiftedDay.HasValue) _RestoreShift(_shiftedDay.Value);
        _RestoreSourceCounter();
        _dragEv     = null;
        _dragActive = false;
    }

    private void _UpdateCellHover(DateTime? day)
    {
        if (day == _hoveredDay) return;
        _hoveredDay = day;
        _cells.HoveredDay = day;
        _cells.InvalidateVisual();
    }

    private void _UpdateCircleHover(Point? pos)
    {
        var day = pos.HasValue ? _CircleDayAtPos(pos.Value) : null;
        if (day == _hoveredCircleDay) return;
        _hoveredCircleDay = day;
        _cells.HoveredCircleDay = day;
        _cells.InvalidateVisual();
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

    // Снять выделение со всех событий.
    private void _Deselect()
    {
        foreach (var unhighlight in _selections.Values.ToList()) unhighlight();
        _selections.Clear();
    }

    // Снять выделение с одного события (для toggle по Shift+клику).
    private void _DeselectOne(CalendarRecord ev)
    {
        if (_selections.TryGetValue(ev, out var unhighlight))
        {
            unhighlight();
            _selections.Remove(ev);
        }
    }

    // Чипы при drag больше не двигаются — восстановить нужно только «+N еще».
    private void _RestoreShift(DateTime day)
    {
        if (_dayUiMap.TryGetValue(day, out var ui))
        {
            ui.More = ui.OrigHidden > 0 ? ui.OrigHidden : null;
            _cells.InvalidateVisual();
        }
        _shiftedDay = null;
    }

    // Пока событие утащено из исходного дня в другой — там одним событием меньше:
    // освободившееся место займет скрытое, поэтому «+N еще» уменьшаем на 1.
    private void _ShiftSourceCounter()
    {
        if (_srcCounterShifted) return;
        if (_dayUiMap.TryGetValue(_dragSourceDay, out var ui) && ui.OrigHidden > 0)
        {
            var n = ui.OrigHidden - 1;
            ui.More = n > 0 ? n : null;
            _cells.InvalidateVisual();
        }
        _srcCounterShifted = true;
    }

    // Вернуть исходному дню его настоящий «+N еще» (возврат курсора / отпускание).
    private void _RestoreSourceCounter()
    {
        if (!_srcCounterShifted) return;
        if (_dayUiMap.TryGetValue(_dragSourceDay, out var ui))
        {
            ui.More = ui.OrigHidden > 0 ? ui.OrigHidden : null;
            _cells.InvalidateVisual();
        }
        _srcCounterShifted = false;
    }

    // Создает ghost-плашку (яркая красная, белый текст) при первом обращении.
    private void _EnsureDragGhost(Color color)
    {
        if (_dragGhost is not null || _dragEv is null) return;
        _dragGhost = new Grid { Height = BarH, IsHitTestVisible = false };
        _dragGhost.Children.Add(new Border
        {
            Background   = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(4),
        });
        _dragGhost.Children.Add(new TextBlock
        {
            Text              = _dragEv.AllDay ? _dragEv.DisplayTitle : $"{_dragEv.StartDt:HH:mm} {_dragEv.DisplayTitle}",
            FontSize          = FontSizeBase,
            FontWeight        = WeightBold,
            Foreground        = Brushes.White,
            Margin            = new Thickness(8, 0, 4, 0),
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        SpanCanvas.Children.Add(_dragGhost);
    }

    private void _UpdateDragGhost()
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
        var color = Color.Parse(EventColors.Display(_dragEv.Color));

        // Возврат в исходный день: ставим ghost ровно на место оригинала (по его
        // фактическим координатам), не пересчитывая строку — так яркая плашка
        // садится точно туда, откуда тащим.
        if (_dragTargetDay == _dragSourceDay && _dragSrc is not null)
        {
            if (_shiftedDay.HasValue) _RestoreShift(_shiftedDay.Value);
            // Поверхности лежат в начале сетки, как и холст призрака: координаты плашки — его.
            _EnsureDragGhost(color);
            _dragGhost!.Width = _dragSrc.Bounds.Width;
            Canvas.SetLeft(_dragGhost, _dragSrc.Bounds.X);
            Canvas.SetTop(_dragGhost, _dragSrc.Bounds.Y);
            return;
        }

        var weekStart  = _gridStart.AddDays(row * 7);
        var layout     = _ComputeWeekLayout(weekStart);

        // Restore shift from a previous target day before applying to new one
        if (_shiftedDay.HasValue && _shiftedDay.Value != _dragTargetDay)
            _RestoreShift(_shiftedDay.Value);

        // Первая свободная строка дня: занятые spanning-бары + уже показанные
        // чипы этого дня (их строки запомнены в _dayUiMap.Occupied). Ghost
        // заполняет верхние дыры так же, как и обычная раскладка.
        var occupied = _dayUiMap.TryGetValue(_dragTargetDay, out var ui)
            ? new HashSet<int>(ui.Occupied)
            : _SpanRowsForDay(_dragTargetDay, layout);
        var freeRow = _FirstFreeRow(occupied);

        // +1 — верхняя граница ячейки (BorderThickness top=1): контент ячейки
        // (и реальные чипы) сдвинут на 1px вниз относительно row*ch, а ghost
        // лежит на Canvas без этой границы.
        var by = row * ch + 1 + DateH + freeRow * BarStep;

        // Свободной строки в пределах ячейки нет — событие не влезает: прячем ghost
        // и показываем «+N еще», увеличив счетчик на это перетаскиваемое событие
        // (исходные скрытые + 1).
        if (ch > 0 && by + BarH > row * ch + ch - BPad)
        {
            if (_dragGhost is not null) { SpanCanvas.Children.Remove(_dragGhost); _dragGhost = null; }
            if (_shiftedDay.HasValue && _shiftedDay.Value != _dragTargetDay)
                _RestoreShift(_shiftedDay.Value);
            if (_dayUiMap.TryGetValue(_dragTargetDay, out var uiHidden))
            {
                uiHidden.More = uiHidden.OrigHidden + 1;
                _cells.InvalidateVisual();
            }
            _shiftedDay = _dragTargetDay;
            return;
        }

        // Если ghost заняло бы строку «+N еще» — прячем подпись на время drag.
        if (_dayUiMap.TryGetValue(_dragTargetDay, out var ui2))
        {
            ui2.More = null;
            _cells.InvalidateVisual();
        }
        _shiftedDay = _dragTargetDay;

        _EnsureDragGhost(color);

        _dragGhost!.Width = bw;
        Canvas.SetLeft(_dragGhost, bx);
        Canvas.SetTop(_dragGhost, by);
    }

    /// <summary>Середина плашки или полосы события по названию в координатах вида (стенд кадров
    /// Tests/Sections наводит и нажимает по ней); null — такой нет.</summary>
    internal Point? EventCenter(string title)
    {
        foreach (var cell in _cells.Cells)
            foreach (var (chip, _) in cell.Chips)
                if (chip.Ev.Title == title) return _cells.TranslatePoint(chip.Bounds.Center, this);
        foreach (var bar in _bars.Bars)
            if (bar.Ev.Title == title) return _bars.TranslatePoint(bar.Bounds.Center, this);
        return null;
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

    // Меню назначаются свойством ContextMenu контрола — фреймворк сам открывает
    // их по правому клику и закрывает (в т.ч. предыдущее), поэтому ручной Open()
    // и перехват правой кнопки в PointerPressed не нужны (иначе попапы зависают
    // и накапливаются). Состав пунктов зависит от выделения на момент открытия,
    // поэтому наполняем меню в Opening, а не один раз при создании.
    private ContextMenu _BuildEventContextMenu(CalendarRecord ev)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            // Множественное выделение → массовые «Дублировать»/«Удалить» всего набора разом
            // (подтверждение «Вы дублируете/удаляете N записей» — в контроллере).
            if (_selections.Count > 1 && _selections.ContainsKey(ev))
            {
                var batch = _selections.Keys.ToList();
                menu.Items.Add(SmartBase.Controls.Components.EventContextMenu.MakeDuplicateItem(
                    () => { _Deselect(); RecordsDuplicateRequested?.Invoke(this, batch); }, Controls.Components.EventContextMenu.Keys.Calendar.Duplicate, this));
                menu.Items.Add(SmartBase.Controls.Components.EventContextMenu.MakeDeleteItem(
                    () => { _Deselect(); RecordsDeleteRequested?.Invoke(this, batch); }, Controls.Components.EventContextMenu.Keys.Calendar.Delete, this));
                return;
            }
            menu.Items.Add(SmartBase.Controls.Components.EventContextMenu.MakeEditItem(
                () => { _Deselect(); RecordClicked?.Invoke(this, ev); }, Controls.Components.EventContextMenu.Keys.Calendar.Open, this));
            menu.Items.Add(SmartBase.Controls.Components.EventContextMenu.MakeDuplicateItem(
                () => { _Deselect(); RecordDuplicateRequested?.Invoke(this, ev); }, Controls.Components.EventContextMenu.Keys.Calendar.Duplicate, this));
            menu.Items.Add(SmartBase.Controls.Components.EventContextMenu.MakeDeleteItem(
                () => { _Deselect(); RecordDeleteRequested?.Invoke(this, ev); }, Controls.Components.EventContextMenu.Keys.Calendar.Delete, this));
        };
        return menu;
    }

    private ContextMenu _BuildDayContextMenu(DateTime date)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            _Deselect();
            menu.Items.Clear();
            menu.Items.Add(SmartBase.Controls.Components.EventContextMenu.MakeCreateItem(
                () => DayAddClicked?.Invoke(this, date), Controls.Components.EventContextMenu.Keys.Calendar.Create, this));
        };
        return menu;
    }
}
