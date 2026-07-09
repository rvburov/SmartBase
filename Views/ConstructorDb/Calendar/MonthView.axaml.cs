using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DataBase.Core.Models;
using DataBase.Utils;

namespace DataBase.Views.ConstructorDb.Calendar;

public partial class MonthView : UserControl
{
    // Размеры/веса шрифта — из токенов Typography.axaml (резолв ресурса в рантайме).
    // Свежий размер из Application.Resources — меняется живьём при изменении FontScale.
    private static double FontSizeBase =>
        (double)Application.Current!.FindResource("FontSizeBase")!;

    private static readonly FontWeight WeightBold   = (FontWeight)Application.Current!.FindResource("FontWeightBold")!;
    private static readonly FontWeight WeightNormal = (FontWeight)Application.Current!.FindResource("FontWeightNormal")!;

    public event EventHandler<DateTime>? DaySelected;
    public event EventHandler<DateTime>? DayAddClicked;
    public event EventHandler<CalendarRecord>? RecordClicked;
    // Перенос между днями: запись уже с НОВЫМИ датами + исходные даты — для точечной
    // дельты в контроллере (−1 старому дню, +1 новому, включая карту счётчиков «+N ещё»).
    public event EventHandler<(CalendarRecord Rec, DateTime OldStart, DateTime OldEnd)>? RecordMoved;
    public event EventHandler<CalendarRecord>? RecordDeleteRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDeleteRequested;
    public event EventHandler<CalendarRecord>? RecordDuplicateRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDuplicateRequested;

    // Константы макета (высоты элементов ячейки дня и полос событий).
    private const int DateH   = 34;
    private const int BarH    = 20;
    private const int BarStep = 22;
    private const int MoreH   = 16;
    private const int BPad    = 4;

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
    private Control?  _dragSrcControl;
    private Grid?     _dragGhost;
    private DateTime? _shiftedDay;
    private bool      _srcCounterShifted; // в исходном дне «+N ещё» временно уменьшен на утащенное
    private int       _pressClickCount;
    private bool      _skipNextDeselect;

    // Выделенные события → действие снятия их подсветки. Множественное выделение
    // (Shift+клик) копит несколько; одиночный клик оставляет одно.
    private readonly Dictionary<CalendarRecord, Action> _selections = new();

    private readonly Dictionary<DateTime, (Grid Panel, TextBlock More, HashSet<int> Occupied, int OrigHidden)> _dayUiMap    = new();
    private readonly Dictionary<int, List<(Border SelFrame, Border? LeftStripe)>>                             _spanFrames    = new();
    private readonly Dictionary<int, List<Border>>                                                             _spanBgBorders = new();
    private readonly Dictionary<int, List<TextBlock>>                                                          _spanTextBlocks = new();
    private readonly Dictionary<DateTime, (Border Cell, IBrush BaseBg)>                                       _dayCellMap   = new();
    private readonly Dictionary<DateTime, (Action Show, Action Hide)>                                         _dayPlusMap   = new();
    private DateTime? _hoveredDay;
    private DateTime? _hoveredCircleDay;
    private readonly HashSet<int> _selectedSpanIds = [];

    private EventHandler<PointerPressedEventArgs>? _windowClickHandler;
    private EventHandler<KeyEventArgs>?            _windowKeyHandler;

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
                if (ke.Key != Key.Delete && ke.Key != Key.Back) return;
                if (!IsVisible) return;
                if (_selections.Count == 0) return;
                // Не перехватываем Delete внутри текстовых полей (диалог редактирования и т.п.).
                if (ke.Source is TextBox or AutoCompleteBox) return;
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

    // Карта счётчиков дней (день → сколько событий СТАРТУЕТ в нём): при ограниченном чтении
    // окна день-гигант отдаёт сетке только первые GridDayCap строк — недоставленные события
    // не попадают в _events вовсе, и честный «+N ещё» получается только из карты. null —
    // чтение без ограничения (или поиск), скрытые считаются от фактически полученного.
    private IReadOnlyDictionary<DateOnly, int>? _dayTotals;

    public void Refresh(IEnumerable<CalendarRecord> events, DateTime currentDate,
                        IReadOnlyDictionary<DateOnly, int>? dayTotals = null)
    {
        _events      = events.ToList();
        _currentDate = currentDate;
        _dayTotals   = dayTotals;
        _BuildAll();
    }

    // ── алгоритм раскладки многодневных событий по строкам ────────────────────
    // Жадный алгоритм: для каждого события берём первую строку, где ec < sc.

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

    // Строки, занятые spanning-барами именно в день d (множество с возможными
    // дырами — бар в строке 1 может покрывать день, а строка 0 в нём свободна).
    private HashSet<int> _SpanRowsForDay(DateTime d, Dictionary<int, LayoutEntry> layout)
    {
        var rows = new HashSet<int>();
        foreach (var e in _events)
        {
            if (!(e.AllDay || e.EndDt.Date > e.StartDt.Date)) continue;
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
    // сглаживание текста), а пустой слой Aliased защёлкивает линии в целый пиксель
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
        DayGrid.Children.Clear();
        _BuildGridLines();
        SpanCanvas.Children.Clear();
        _dayUiMap.Clear();
        _spanFrames.Clear();
        _spanBgBorders.Clear();
        _spanTextBlocks.Clear();
        _selectedSpanIds.Clear();
        _dayCellMap.Clear();
        _dayPlusMap.Clear();
        _hoveredDay       = null;
        _hoveredCircleDay = null;

        var W = GridContainer.Bounds.Width;
        var H = GridContainer.Bounds.Height;

        // Сколько событий на каждый день ДОСТАВЛЕНО (по дню старта): разница с картой
        // счётчиков — события, обрезанные ограниченным чтением, идут в «+N ещё».
        Dictionary<DateTime, int>? received = null;
        if (_dayTotals is not null)
        {
            received = new Dictionary<DateTime, int>();
            foreach (var e in _events)
                received[e.StartDt.Date] = received.GetValueOrDefault(e.StartDt.Date) + 1;
        }

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

                // Строки, занятые spanning-барами в этот день (с дырами).
                var spanRows = _SpanRowsForDay(d, layout);

                // Однодневные события
                var singles = _events
                    .Where(e => !e.AllDay
                             && e.EndDt.Date == e.StartDt.Date
                             && e.StartDt.Date == d.Date)
                    .OrderBy(e => e.StartDt)
                    .ToList();

                // Последняя строка, помещающаяся по высоте: row*BarStep от DateH,
                // снизу резерв под «+N ещё» (MoreH) и нижний отступ (BPad).
                var maxVisRow = ch > 0
                    ? (int)((ch - BPad - MoreH - DateH) / BarStep) - 1
                    : 4;

                // Каждому single — первая свободная строка (заполняя верхние дыры).
                // Строки, ушедшие за maxVisRow, считаются скрытыми.
                var occupied = new HashSet<int>(spanRows);
                var placed   = new List<(CalendarRecord Ev, int Row)>();
                var hiddenSingles = 0;
                foreach (var ev in singles)
                {
                    var r = _FirstFreeRow(occupied);
                    occupied.Add(r);
                    if (r <= maxVisRow) placed.Add((ev, r));
                    else                hiddenSingles++;
                }

                var hidden = hiddenSingles + hiddenSpans[col];

                // Обрезанные ограниченным чтением (в _events их нет) — тоже скрытые.
                if (received is not null && _dayTotals!.TryGetValue(DateOnly.FromDateTime(d), out var total))
                    hidden += Math.Max(0, total - received.GetValueOrDefault(d.Date));

                DayGrid.Children.Add(
                    _MakeCell(d, isCurrentMonth, isToday, isWeekend,
                              placed, occupied, hidden));
            }
        }
    }

    // ── ячейка дня ────────────────────────────────────────────────────────────

    private Control _MakeCell(
        DateTime    date,
        bool        isCurrentMonth,
        bool        isToday,
        bool        isWeekend,
        List<(CalendarRecord Ev, int Row)> placed,
        HashSet<int> occupied,
        int         hidden)
    {
        // Число дня
        var labelFg = isToday ? Brushes.White
            : isWeekend ? (IBrush)new SolidColorBrush(Color.Parse("#FF3B30"))
            : new SolidColorBrush(isCurrentMonth ? Color.Parse("#1C1C1E") : Color.Parse("#8E8E93"));

        var dayLabel = new TextBlock
        {
            Text                = date.Day.ToString(),
            FontSize            = FontSizeBase,
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Center,
            Foreground          = labelFg,
            FontWeight          = isToday ? WeightBold : WeightNormal,
        };

        // Плюс — как в кнопке «Создать» в сайдбаре (Sidebar.axaml):
        // Path на Canvas 24×24 внутри Viewbox 18×18 — так и геометрия,
        // и толщина штриха масштабируются одинаково (визуально тоньше и компактнее).
        Application.Current!.TryFindResource("IconPlus", out var plusRes);
        var plusPath = new Avalonia.Controls.Shapes.Path
        {
            Data            = plusRes as Geometry,
            Stroke          = new SolidColorBrush(Color.Parse("#636366")),
            StrokeThickness = 2,
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        };
        var plusCanvas = new Canvas { Width = 24, Height = 24 };
        plusCanvas.Children.Add(plusPath);
        var plusSz = Application.Current!.TryFindResource("IconSizeMedium", out var pszR) && pszR is double pszD ? pszD : 18d;
        var plusIcon = new Viewbox
        {
            Width               = plusSz,
            Height              = plusSz,
            Child               = plusCanvas,
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
                dayCircle.BorderThickness = new Thickness(LineMetrics.ThicknessFor(this));
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

        // Однодневные события: каждый чип в свою строку дня (DateH + row*BarStep),
        // заполняя «дыры» между spanning-барами. Grid-контейнер без строк —
        // чипы разводятся по вертикали верхним отступом, ширина тянется по ячейке.
        var eventsPanel = new Grid
        {
            VerticalAlignment   = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (var (ev, row) in placed)
        {
            var chip = _MakeChip(ev);
            chip.Margin            = new Thickness(2, DateH + row * BarStep, 2, 0);
            chip.VerticalAlignment = VerticalAlignment.Top;
            eventsPanel.Children.Add(chip);
        }

        // «+N ещё» — прижата к нижнему краю ячейки
        var moreLabel = new TextBlock
        {
            Text                = $"+{hidden} ещё",
            IsVisible           = hidden > 0,
            FontSize            = FontSizeBase,
            Foreground          = new SolidColorBrush(Color.Parse("#8E8E93")),
            Margin              = new Thickness(6, 0, 0, 4),
            VerticalAlignment   = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        _dayUiMap[date] = (eventsPanel, moreLabel, occupied, hidden);

        var inner = new Grid();
        inner.Children.Add(dayCircle);
        inner.Children.Add(eventsPanel);
        inner.Children.Add(moreLabel);

        IBrush baseBg = new SolidColorBrush(
            isCurrentMonth ? Color.Parse("#FFFFFF") : Color.Parse("#F9F9F9"));

        // Линии сетки рисует отдельный слой GridLines (см. _BuildGridLines) —
        // бордер на ячейке с текстом нельзя алиасить, линии были бы размытыми при зуме.
        var cellBorder = new Border
        {
            Background   = baseBg,
            ClipToBounds = true,
            Child        = inner,
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
        cellBorder.ContextMenu = _BuildDayContextMenu(date);
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

    private Border _MakeChip(CalendarRecord ev)
    {
        var color      = Color.Parse(EventColors.Display(ev.Color));
        var normalBg   = new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B));
        var hoverBg    = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B));
        var selectedBg = new SolidColorBrush(color);
        var darkBrush  = new SolidColorBrush(_Darken(color));

        // Полоска утоплена внутрь плашки (как в Неделе): прижатая к краю она
        // выступала «усиками» за скруглённый силуэт — её радиус меньше радиуса плашки.
        var leftStripe = new Border
        {
            Width               = 3,
            Margin              = new Thickness(2, 2, 0, 2),
            Background          = new SolidColorBrush(color),
            CornerRadius        = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var content = new Grid();
        content.Children.Add(leftStripe);

        List<TextBlock> chipTextBlocks = [];
        if (ev.AllDay)
        {
            var tb = new TextBlock
            {
                Text              = ev.DisplayTitle,
                FontSize          = FontSizeBase,
                FontWeight        = WeightBold,
                Foreground        = darkBrush,
                Margin            = new Thickness(8, 0, 2, 0),
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            chipTextBlocks.Add(tb);
            content.Children.Add(tb);
        }
        else
        {
            // Grid [время Auto][заголовок *] — заголовок в колонке * получает конечную
            // ширину и обрезается многоточием, когда событие не помещается.
            var timeAndTitle = new Grid
            {
                VerticalAlignment   = VerticalAlignment.Center,
                Margin              = new Thickness(8, 0, 2, 0),
                ColumnDefinitions   = new ColumnDefinitions("Auto,*"),
            };
            var timeTb = new TextBlock
            {
                Text       = $"{ev.StartDt:HH:mm} ",
                FontSize   = FontSizeBase,
                FontWeight = WeightNormal,
                Foreground = darkBrush,
            };
            var titleTb = new TextBlock
            {
                Text         = ev.DisplayTitle,
                FontSize     = FontSizeBase,
                FontWeight   = WeightBold,
                Foreground   = darkBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(timeTb, 0);
            Grid.SetColumn(titleTb, 1);
            chipTextBlocks.Add(timeTb);
            chipTextBlocks.Add(titleTb);
            timeAndTitle.Children.Add(timeTb);
            timeAndTitle.Children.Add(titleTb);
            content.Children.Add(timeAndTitle);
        }

        var wrapper = new Grid();
        wrapper.Children.Add(content);

        var chip = new Border
        {
            Height       = BarH,
            Background   = normalBg,
            CornerRadius = new CornerRadius(4),
            Cursor       = new Cursor(StandardCursorType.Hand),
            Child        = wrapper,
        };

        var isSelected = false;

        void Highlight()
        {
            isSelected           = true;
            chip.Background      = selectedBg;
            leftStripe.IsVisible = false;
            foreach (var tb in chipTextBlocks) tb.Foreground = Brushes.White;
        }
        void Unhighlight()
        {
            isSelected           = false;
            chip.Background      = normalBg;
            leftStripe.IsVisible = true;
            foreach (var tb in chipTextBlocks) tb.Foreground = darkBrush;
        }

        // additive (Shift+клик): добавляет/убирает событие из набора, не трогая
        // остальные. Иначе — одиночное выделение (сначала снимаем всё).
        void SelectThis(bool additive)
        {
            if (additive)
            {
                if (_selections.ContainsKey(ev)) { _DeselectOne(ev); return; }
            }
            else
            {
                _Deselect();
            }
            Highlight();
            _selections[ev] = Unhighlight;
        }

        chip.PointerEntered += (_, _) => { if (!isSelected) chip.Background = hoverBg; };
        chip.PointerExited  += (_, _) => { if (!isSelected) chip.Background = normalBg; };
        chip.ContextMenu = _BuildEventContextMenu(ev);
        // Правый клик: если событие уже в множественном выделении — оставляем набор
        // как есть (меню удалит все). Иначе выделяем только это (как одиночный клик),
        // чтобы было видно, с чем работаем. Окно снимает выделение только на левый
        // клик мимо, поэтому переключение правой кнопкой подсветку не гасит.
        chip.PointerPressed  += (_, e) =>
        {
            var props = e.GetCurrentPoint(null).Properties;
            if (props.IsRightButtonPressed)
            {
                if (!_selections.ContainsKey(ev)) SelectThis(additive: false);
                return;
            }
            if (props.IsLeftButtonPressed)  { _skipNextDeselect = true; _BeginDragTrack(ev, chip, e); e.Handled = true; }
        };
        chip.PointerMoved    += (_, e) => _OnDragMoved(e);
        chip.PointerReleased += (_, e) =>
        {
            // Правый клик уже обработан в PointerPressed (выделение + меню) —
            // не запускаем логику по левой кнопке.
            if (e.InitialPressMouseButton != MouseButton.Left) return;
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
                SelectThis(additive: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            }
        };
        return chip;
    }

    // ── бар многодневного события на Canvas ───────────────────────────────────

    private void _AddSpanBar(CalendarRecord ev, double bx, double by, double bw,
                              bool leftCap, bool rightCap)
    {
        var color      = Color.Parse(EventColors.Display(ev.Color));
        var cr         = new CornerRadius(
            leftCap  ? 4 : 0, rightCap ? 4 : 0,
            rightCap ? 4 : 0, leftCap  ? 4 : 0);
        var normalBg   = new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B));
        var hoverBg    = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B));
        var selectedBg = new SolidColorBrush(color);
        var darkBrush  = new SolidColorBrush(_Darken(color));

        var bar = new Grid { Width = bw, Height = BarH, Cursor = new Cursor(StandardCursorType.Hand) };

        // Белый непрозрачный слой блокирует линии сетки снизу
        bar.Children.Add(new Border { Background = Brushes.White, CornerRadius = cr });

        // Полупрозрачный цветной фон поверх белого
        var bgBorder = new Border { Background = normalBg, CornerRadius = cr };
        bar.Children.Add(bgBorder);

        if (ev.Id.HasValue)
        {
            if (!_spanBgBorders.ContainsKey(ev.Id.Value))
                _spanBgBorders[ev.Id.Value] = new List<Border>();
            _spanBgBorders[ev.Id.Value].Add(bgBorder);
        }

        // Цветной акцент слева — только у начала события
        Border? leftStripeBar = null;
        if (leftCap)
        {
            // Утоплена внутрь бара (как в Неделе) — прижатая к краю выступала
            // «усиками» за скруглённый силуэт.
            leftStripeBar = new Border
            {
                Width               = 3,
                Margin              = new Thickness(2, 2, 0, 2),
                Background          = new SolidColorBrush(color),
                CornerRadius        = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            bar.Children.Add(leftStripeBar);
        }

        List<TextBlock> barTextBlocks = [];
        if (ev.AllDay)
        {
            var tb = new TextBlock
            {
                Text              = ev.DisplayTitle,
                FontSize          = FontSizeBase,
                FontWeight        = WeightBold,
                Foreground        = darkBrush,
                Margin            = new Thickness(8, 0, 4, 0),
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            barTextBlocks.Add(tb);
            bar.Children.Add(tb);
        }
        else
        {
            // Grid [время Auto][заголовок *] — заголовок в колонке * получает
            // конечную ширину бара и обрезается многоточием, как у однодневного чипа.
            var timeAndTitle = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(8, 0, 4, 0),
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            };
            var timeTb = new TextBlock
            {
                Text       = $"{ev.StartDt:HH:mm} ",
                FontSize   = FontSizeBase,
                FontWeight = WeightNormal,
                Foreground = darkBrush,
            };
            var titleTb = new TextBlock
            {
                Text         = ev.DisplayTitle,
                FontSize     = FontSizeBase,
                FontWeight   = WeightBold,
                Foreground   = darkBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(timeTb, 0);
            Grid.SetColumn(titleTb, 1);
            barTextBlocks.Add(timeTb);
            barTextBlocks.Add(titleTb);
            timeAndTitle.Children.Add(timeTb);
            timeAndTitle.Children.Add(titleTb);
            bar.Children.Add(timeAndTitle);
        }

        if (ev.Id.HasValue)
        {
            if (!_spanTextBlocks.ContainsKey(ev.Id.Value))
                _spanTextBlocks[ev.Id.Value] = new List<TextBlock>();
            _spanTextBlocks[ev.Id.Value].AddRange(barTextBlocks);
        }

        // Проверяем общий словарь _selectedSpanIds — он один на все сегменты события,
        // поэтому ховер на любом сегменте не перебивает выделение другого сегмента.
        var isSelected = () => ev.Id.HasValue && _selectedSpanIds.Contains(ev.Id.Value);

        bar.PointerEntered += (_, _) =>
        {
            if (isSelected()) return;
            var all = ev.Id.HasValue && _spanBgBorders.TryGetValue(ev.Id.Value, out var bs) ? bs : [bgBorder];
            foreach (var b in all) b.Background = hoverBg;
        };
        bar.PointerExited += (_, _) =>
        {
            if (isSelected()) return;
            var all = ev.Id.HasValue && _spanBgBorders.TryGetValue(ev.Id.Value, out var bs) ? bs : [bgBorder];
            foreach (var b in all) b.Background = normalBg;
        };

        // Выделение бара подсвечивает все сегменты события сразу через _selectedSpanIds.
        void Highlight()
        {
            if (ev.Id.HasValue) _selectedSpanIds.Add(ev.Id.Value);
            var allBg  = ev.Id.HasValue && _spanBgBorders.TryGetValue(ev.Id.Value, out var bs) ? bs : [bgBorder];
            var allTbs = ev.Id.HasValue && _spanTextBlocks.TryGetValue(ev.Id.Value, out var tbs) ? tbs : barTextBlocks;
            foreach (var b in allBg)  b.Background = selectedBg;
            foreach (var tb in allTbs) tb.Foreground = Brushes.White;
            if (leftStripeBar is not null) leftStripeBar.IsVisible = false;
        }
        void Unhighlight()
        {
            if (ev.Id.HasValue) _selectedSpanIds.Remove(ev.Id.Value);
            var allBg  = ev.Id.HasValue && _spanBgBorders.TryGetValue(ev.Id.Value, out var bs) ? bs : [bgBorder];
            var allTbs = ev.Id.HasValue && _spanTextBlocks.TryGetValue(ev.Id.Value, out var tbs) ? tbs : barTextBlocks;
            foreach (var b in allBg)   b.Background = normalBg;
            foreach (var tb in allTbs) tb.Foreground = darkBrush;
            if (leftStripeBar is not null) leftStripeBar.IsVisible = true;
        }

        // additive (Shift+клик): добавляет/убирает событие из набора, не трогая
        // остальные. Иначе — одиночное выделение (сначала снимаем всё).
        void SelectThis(bool additive)
        {
            if (additive)
            {
                if (_selections.ContainsKey(ev)) { _DeselectOne(ev); return; }
            }
            else
            {
                _Deselect();
            }
            Highlight();
            _selections[ev] = Unhighlight;
        }

        bar.ContextMenu = _BuildEventContextMenu(ev);
        // Правый клик: если событие уже в множественном выделении — оставляем набор;
        // иначе выделяем только это (как одиночный клик).
        bar.PointerPressed  += (_, e) =>
        {
            var props = e.GetCurrentPoint(null).Properties;
            if (props.IsRightButtonPressed)
            {
                if (!_selections.ContainsKey(ev)) SelectThis(additive: false);
                return;
            }
            if (props.IsLeftButtonPressed)  { _skipNextDeselect = true; _BeginDragTrack(ev, bar, e); }
        };
        bar.PointerMoved    += (_, e) => _OnDragMoved(e);
        bar.PointerReleased += (_, e) =>
        {
            // Правый клик уже обработан в PointerPressed (выделение + меню) —
            // не запускаем логику по левой кнопке.
            if (e.InitialPressMouseButton != MouseButton.Left) return;
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
                SelectThis(additive: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            }
        };

        Canvas.SetLeft(bar, bx);
        Canvas.SetTop(bar, by);
        SpanCanvas.Children.Add(bar);
    }

    // ── drag-and-drop ─────────────────────────────────────────────────────────

    private void _BeginDragTrack(CalendarRecord ev, Control src, PointerPressedEventArgs e)
    {
        if (_dragEv is not null) return;
        _dragEv          = ev;
        _dragActive      = false;
        _dragSrcControl  = src;
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
            _Deselect();
            // Оригинал остаётся прозрачным (Opacity 0.45) на исходном месте.
            if (_dragSrcControl is not null) _dragSrcControl.Opacity = 0.45;
        }
        var target = _DayAtPos(pos);
        if (!target.HasValue) return;
        var changed = target.Value.Date != _dragTargetDay;
        if (changed) _dragTargetDay = target.Value.Date;

        // Счётчик исходного дня: пока событие уведено в другой день — там одним
        // меньше; вернулось на место — счётчик восстанавливаем.
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
            _CancelDrag();
            // Исходные даты — контроллеру: перенос между днями правится дельтой на месте
            // (−1 старому дню, +1 новому — в т.ч. карта счётчиков «+N ещё»), без перезагрузки.
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
        if (_dragSrcControl is not null) { _dragSrcControl.Opacity = 1.0; _dragSrcControl = null; }
        if (_dragGhost is not null) { SpanCanvas.Children.Remove(_dragGhost); _dragGhost = null; }
        if (_shiftedDay.HasValue) _RestoreShift(_shiftedDay.Value);
        _RestoreSourceCounter();
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

    // Чипы при drag больше не двигаются — восстановить нужно только «+N ещё».
    private void _RestoreShift(DateTime day)
    {
        if (_dayUiMap.TryGetValue(day, out var ui))
        {
            ui.More.IsVisible = ui.OrigHidden > 0;
            ui.More.Text      = $"+{ui.OrigHidden} ещё";
        }
        _shiftedDay = null;
    }

    // Пока событие утащено из исходного дня в другой — там одним событием меньше:
    // освободившееся место займёт скрытое, поэтому «+N ещё» уменьшаем на 1.
    private void _ShiftSourceCounter()
    {
        if (_srcCounterShifted) return;
        if (_dayUiMap.TryGetValue(_dragSourceDay, out var ui) && ui.OrigHidden > 0)
        {
            var n = ui.OrigHidden - 1;
            ui.More.IsVisible = n > 0;
            ui.More.Text      = $"+{n} ещё";
        }
        _srcCounterShifted = true;
    }

    // Вернуть исходному дню его настоящий «+N ещё» (возврат курсора / отпускание).
    private void _RestoreSourceCounter()
    {
        if (!_srcCounterShifted) return;
        if (_dayUiMap.TryGetValue(_dragSourceDay, out var ui))
        {
            ui.More.IsVisible = ui.OrigHidden > 0;
            ui.More.Text      = $"+{ui.OrigHidden} ещё";
        }
        _srcCounterShifted = false;
    }

    // Создаёт ghost-плашку (яркая красная, белый текст) при первом обращении.
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
        if (_dragTargetDay == _dragSourceDay && _dragSrcControl is not null)
        {
            if (_shiftedDay.HasValue) _RestoreShift(_shiftedDay.Value);
            var topLeft = _dragSrcControl.TranslatePoint(new Point(0, 0), SpanCanvas);
            if (topLeft.HasValue)
            {
                _EnsureDragGhost(color);
                _dragGhost!.Width = _dragSrcControl.Bounds.Width;
                Canvas.SetLeft(_dragGhost, topLeft.Value.X);
                Canvas.SetTop(_dragGhost, topLeft.Value.Y);
            }
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
        // и показываем «+N ещё», увеличив счётчик на это перетаскиваемое событие
        // (исходные скрытые + 1).
        if (ch > 0 && by + BarH > row * ch + ch - BPad)
        {
            if (_dragGhost is not null) { SpanCanvas.Children.Remove(_dragGhost); _dragGhost = null; }
            if (_shiftedDay.HasValue && _shiftedDay.Value != _dragTargetDay)
                _RestoreShift(_shiftedDay.Value);
            if (_dayUiMap.TryGetValue(_dragTargetDay, out var uiHidden))
            {
                var n = uiHidden.OrigHidden + 1;
                uiHidden.More.IsVisible = true;
                uiHidden.More.Text      = $"+{n} ещё";
            }
            _shiftedDay = _dragTargetDay;
            return;
        }

        // Если ghost заняло бы строку «+N ещё» — прячем подпись на время drag.
        if (_dayUiMap.TryGetValue(_dragTargetDay, out var ui2))
            ui2.More.IsVisible = false;
        _shiftedDay = _dragTargetDay;

        _EnsureDragGhost(color);

        _dragGhost!.Width = bw;
        Canvas.SetLeft(_dragGhost, bx);
        Canvas.SetTop(_dragGhost, by);
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
                menu.Items.Add(DataBase.Controls.Components.EventContextMenu.MakeDuplicateItem(
                    () => { _Deselect(); RecordsDuplicateRequested?.Invoke(this, batch); }));
                menu.Items.Add(DataBase.Controls.Components.EventContextMenu.MakeDeleteItem(
                    () => { _Deselect(); RecordsDeleteRequested?.Invoke(this, batch); }));
                return;
            }
            menu.Items.Add(DataBase.Controls.Components.EventContextMenu.MakeEditItem(
                () => { _Deselect(); RecordClicked?.Invoke(this, ev); }));
            menu.Items.Add(DataBase.Controls.Components.EventContextMenu.MakeDuplicateItem(
                () => { _Deselect(); RecordDuplicateRequested?.Invoke(this, ev); }));
            menu.Items.Add(DataBase.Controls.Components.EventContextMenu.MakeDeleteItem(
                () => { _Deselect(); RecordDeleteRequested?.Invoke(this, ev); }));
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
            menu.Items.Add(DataBase.Controls.Components.EventContextMenu.MakeCreateItem(
                () => DayAddClicked?.Invoke(this, date)));
        };
        return menu;
    }
}
