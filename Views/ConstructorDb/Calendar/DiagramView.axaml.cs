using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using SmartBase.Controls.Components;
using SmartBase.Core.Calendar;
using SmartBase.Core.Models;
using SmartBase.Utils;
using SmartBase.Controls.Behaviors;

namespace SmartBase.Views.ConstructorDb.Calendar;

// Диаграмма (гант): строки — коллекции и их записи, ось X — дни показанного периода.
// Масштабы — лесенка месяц → год → 5 лет → 10 лет (кнопки «−»/«+» в шапке);
// геометрия одна и та же — по ячейкам дней, на крупных шкалах ячейки просто узкие,
// а шапка и линии сетки идут по месяцам (год) или по годам (5, 10 лет).
// Многолетние окна выровнены по календарю (5 лет — с кратного пяти,
// 10 — десятилетие), как месяц и год. Раскладка императивная (Canvas +
// Border/TextBlock, пересборка на Refresh/SizeChanged), как в остальных видах
// календаря. Drag/resize — целодневный шаг: минутная точность при таких ячейках
// недостижима, а время суток события сохраняется (семантика горизонтального
// drag в AllDayBar).
public partial class DiagramView : UserControl
{
    // Размеры/веса шрифта — из токенов Typography.axaml (резолв ресурса в рантайме).
    // Свежий размер из Application.Resources — меняется живьем при изменении FontScale.
    private static double FontSizeBase =>
        (double)Application.Current!.FindResource("FontSizeBase")!;
    private static double FontSizeSmall =>
        (double)Application.Current!.FindResource("FontSizeSmall")!;

    private static readonly FontWeight WeightBold = (FontWeight)Application.Current!.FindResource("FontWeightBold")!;

    private static IBrush BrushOf(string key) => (IBrush)Application.Current!.FindResource(key)!;

    // Размерный токен (иконки, толщина обводки) — резолв в рантайме, как у шрифтов.
    private static double Res(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var v) && v is double d ? d : fallback;

    public event EventHandler<CalendarRecord>? RecordClicked;
    public event EventHandler<CalendarRecord>? RecordMoved;
    public event EventHandler<CalendarRecord>? RecordDeleteRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDeleteRequested;
    public event EventHandler<CalendarRecord>? RecordDuplicateRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDuplicateRequested;
    // Смена масштаба (месяц/год): окно данных меняется — контроллер перезагружает вид.
    public event EventHandler? ScaleChanged;

    private static readonly string[] DayNames = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];

    // Константы макета. LeftW продублирована в ColumnDefinitions="230,*" разметки.
    private const double LeftW      = 230;  // ширина левой панели

    // Высота строки (и группы, и записи) — базовые 36 из общего токена RowHeightStandard
    // (Controls.axaml). Настройке «Плотность строк таблицы» диаграмма НЕ подчиняется: та
    // двигает пару «таблица + дерево коллекций», выровненную построчно (токен RowHeightTable),
    // а здесь строки ни с чем не выравниваются, и менять их заодно смысла нет.
    private static double RowH =>
        Application.Current!.TryFindResource("RowHeightStandard", out var r) && r is double d ? d : 36;
    private const double BarH       = 20;   // высота блока и диаметр круга однодневного события
    private const double LabelGap   = 6;    // зазор бар ↔ подпись
    private const double EdgeHit    = 6;    // зона захвата ресайза по краям бара
    private const double LeftIndent = 24;   // отступ строк записей в левой панели

    // Кэпы строк: сверх кэпа — серые строки «+N еще»; смотреть все — Расписание/Журнал
    // (у них виртуализация). MaxRecordsPerCollection публичен: контроллер передает его
    // загрузчику окна как cap чтения (репозиторий отдает не больше, чем вид нарисует).
    public  const int MaxRecordsPerCollection = 200;
    private const int MaxTotalRows            = 1000;

    private List<CalendarRecord> _events = [];
    private DateTime _currentDate = DateTime.Today;

    // Лесенка масштабов; Years = 0 — календарный месяц. Ключи хранятся в app.db.
    private static readonly (string Key, string Label, int Years)[] Scales =
    [
        ("month",   "месяц",  0),
        ("year",    "год",    1),
        ("5years",  "5 лет",  5),
        ("10years", "10 лет", 10),
    ];
    private int _scaleIdx;

    // Показанный период. Вся геометрия считает от его начала и числа дней —
    // построители масштаба не знают, различаются только шапка, линии сетки
    // и отступы блоков.
    private DateTime _rangeStart;
    private int      _rangeDays;

    // ── масштаб: API для контроллера (окно данных, навигация, заголовок) ────────

    // Годы показанного периода; 0 — месяц.
    public int ScaleYears => Scales[_scaleIdx].Years;

    // Окно данных для даты d: календарный месяц или N лет, выровненных по
    // календарю (5 лет — с кратного пяти, 10 — десятилетие) — страницы
    // таймлайна стабильны, как у месяца и года.
    public (DateTime Start, DateTime End) RangeFor(DateTime d)
    {
        var n = ScaleYears;
        if (n == 0)
        {
            var s = new DateTime(d.Year, d.Month, 1);
            return (s, s.AddMonths(1));
        }
        var a = AlignYear(d.Year, n);
        return (new DateTime(a, 1, 1), new DateTime(a + n, 1, 1));
    }

    // Заголовок навбара: «июль 2026» / «2026» / «2020–2029».
    public string TitleFor(DateTime d) => ScaleYears switch
    {
        0 => d.ToString("MMMM yyyy", new System.Globalization.CultureInfo("ru-RU")),
        1 => d.Year.ToString(),
        var n => $"{AlignYear(d.Year, n)}–{AlignYear(d.Year, n) + n - 1}",
    };

    private static int AlignYear(int year, int span) => year - ((year % span) + span) % span;

    // Отмеченные коллекции всех допущенных БД (порядок сайдбара) — каркас групп:
    // коллекция видна строкой даже без событий в показанном периоде.
    private IReadOnlyList<(int DbId, DbCollection Col)>? _collections;
    private bool _hideEmptyGroups;   // при активном поиске пустые группы — шум

    // Точные агрегаты окна по коллекциям (фаза 2 контроллера): чтение окна обрезано
    // cap'ом на коллекцию, поэтому счетчик, «+N еще» и охват суммарного бара берутся
    // отсюда; до прихода агрегатов (и при поиске) — от фактически доставленного.
    private IReadOnlyDictionary<(int DbId, int ColId), DiagramGroupStat>? _stats;

    private List<GroupRow> _groups = [];
    // Свернутость групп переживает Refresh (пересборку), но не перезапуск приложения.
    // По умолчанию коллекция свернута, поэтому в словаре лежит ЯВНО выбранное
    // пользователем состояние: нет ключа — свернута.
    private readonly Dictionary<(int DbId, int ColId), bool> _collapsed = new();

    // Текущая ширина холста таймлайна — то, что реально осталось справа от левой панели
    // ПОСЛЕ полосы прокрутки. Обновляется из SizeChanged холста (см. конструктор).
    private double _timelineW;

    // Геометрия последней сборки — для линии «сейчас» и пересчета drag.
    private double _lastTimelineW;
    private double _lastBgH;

    private readonly DispatcherTimer _timeLineTimer;

    // ── модель строк ────────────────────────────────────────────────────────────

    private sealed class GroupRow
    {
        public int    DbId;
        public int    ColId;
        public string Name  = "";
        public string Color = "";
        public List<CalendarRecord> Records = [];   // все доставленные, по StartDt
        public DiagramGroupStat? Stat;              // точные агрегаты окна (фаза 2)
        public int    Total;                        // событий в окне: Stat.Count или доставленные
        public bool   Collapsed;
    }

    // Плоский план строк: Ev == null && Note == null — строка группы;
    // Ev != null — строка записи; Note != null — серая инфо-строка («+N еще»).
    private sealed class RowItem
    {
        public GroupRow        Group = null!;
        public CalendarRecord? Ev;
        public string?         Note;
    }

    // ── hit-testing ─────────────────────────────────────────────────────────────

    // Слои отрисованного события — для ховера/выделения (эффекты как в Месяце).
    private sealed class EventVisual
    {
        public double X1, X2;
        public bool   IsDot;
        public bool   ClipL, ClipR;
        public Border? BlockBg;                              // полупрозрачный слой блока
        public Avalonia.Controls.Shapes.Ellipse? Dot;        // сплошной круг однодневного события
        public TextBlock Label = null!;
        public bool LabelInside;                             // подпись внутри блока
    }

    private sealed class BarEntry
    {
        public CalendarRecord Ev = null!;
        public int    RowIdx;     // индекс строки — для выделения диапазона
        public double Y;          // верх строки
        public double X1, X2;     // клампленная геометрия бара/круга
        public bool   IsDot;
        public bool   ClipL, ClipR;   // край обрезан границей периода — ресайз с него запрещен
        public EventVisual? Vis;
        public List<Control> Visuals = [];   // прячутся на время drag (виден только ghost)
    }
    private readonly List<BarEntry> _barEntries = [];

    // Выделение — как в Месяце/Расписании: клик выделяет одно (заливка цветом),
    // Shift+клик — аддитивно, Ctrl+Shift+клик — диапазон строк от якоря, двойной
    // клик открывает, drag и клик мимо снимают. Якорь — последняя выделенная
    // кликом запись. Набор и якорь не переживают пересборку (визуалы пересоздаются).
    private readonly List<BarEntry> _selection = [];
    private BarEntry? _rangeAnchor;
    private BarEntry? _hover;

    // Голубая подсветка строки записи под курсором (как ховер строки таблицы
    // конструктора, BrushAccentLight) — две полосы: левая панель и таймлайн.
    private List<RowItem> _rowPlan = [];
    private Border? _hoverBandLeft;
    private Border? _hoverBandRight;
    private int     _hoverRowIdx = -1;

    // Строки строятся контролами только в видимой части ленты (с запасом в экран сверху и снизу)
    // и докладываются при прокрутке: раскрытые группы — сотни строк по восемь контролов, и полная
    // пересборка после каждой правки (дубль, удаление, растяжение) стоила 0,2–0,4 с на UI-потоке.
    // Геометрия баров (попадание указателя, выделение, перенос) считается для всех строк сразу —
    // она дешевая. Заливки строк групп и разделители строк — в своих слоях, в прежнем порядке
    // наложения: под полосой наведения и линиями дней, как были.
    private readonly HashSet<int> _realizedRows = [];
    private readonly Dictionary<int, BarEntry> _entryByRow = [];
    private Canvas _bandLayer = new(), _leftBandLayer = new(), _lineLayer = new(), _leftLineLayer = new();
    private double _buildDayW;
    private double _lineT;
    private IBrush _lineBrush = Brushes.Transparent;

    // drag/resize state
    private BarEntry?    _pressEntry;
    private Point        _pressPos;
    private int          _pressClickCount;
    private KeyModifiers _pressModifiers;
    private bool         _isDragging;
    private bool      _resizeLeft;
    private bool      _resizeRight;
    private DateTime  _origStart, _origEnd;
    private DateTime  _newStart,  _newEnd;

    private static readonly Cursor CursorResize = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor CursorHand   = new(StandardCursorType.Hand);

    public DiagramView()
    {
        InitializeComponent();

        // Масштаб переживает перезапуск (app.db). Кнопки «−»/«+» шагают по
        // лесенке масштабов, между ними подпись текущего; крайняя кнопка гаснет.
        _scaleIdx = Math.Max(0, Array.FindIndex(Scales, s => s.Key == AppSettings.LoadDiagramScale()));
        ScaleMinusBtn.Click += (_, _) => StepScale(-1);
        ScalePlusBtn.Click  += (_, _) => StepScale(+1);
        UpdateScaleButtons();

        // Пересборка — по ширине САМОГО холста таймлайна, а не окна: полоса прокрутки
        // стоит отдельной колонкой и то появляется, то исчезает (ScrollBarLink прячет ее,
        // когда прокручивать нечего), поэтому доступная ширина меняется и без изменения
        // размера окна. На высоту не реагируем: ее задает число строк, и каждая пересборка
        // вызывала бы следующую.
        // Ширину запоминаем из аргументов события: Bounds холста в момент SizeChanged еще
        // старый (в DayView/WeekView по той же причине берут args.NewSize.Width).
        TimelinePane.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - _timelineW) < 0.1) return;
            _timelineW = e.NewSize.Width;
            BuildAll();
        };
        RowsScroll.ScrollChanged += (_, _) => RealizeVisibleRows();

        _timeLineTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timeLineTimer.Tick += (_, _) => UpdateTodayLine();
        _timeLineTimer.Start();

        TimelinePane.AddHandler(PointerPressedEvent,  OnPanePressed,  RoutingStrategies.Bubble, handledEventsToo: true);
        TimelinePane.AddHandler(PointerMovedEvent,    OnPaneMoved,    RoutingStrategies.Bubble, handledEventsToo: true);
        TimelinePane.AddHandler(PointerReleasedEvent, OnPaneReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        TimelinePane.PointerExited += (_, _) =>
        {
            if (_pressEntry is not null) return;
            if (_hover is not null) { ApplyHover(_hover, on: false); _hover = null; }
            SetRowHover(-1);
            TimelinePane.Cursor = Cursor.Default;
        };
    }

    private void StepScale(int step)
    {
        var idx = Math.Clamp(_scaleIdx + step, 0, Scales.Length - 1);
        if (idx == _scaleIdx) return;
        _scaleIdx = idx;
        AppSettings.SaveDiagramScale(Scales[idx].Key);
        UpdateScaleButtons();
        ScaleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateScaleButtons()
    {
        ScaleMinusBtn.IsEnabled = _scaleIdx > 0;
        ScalePlusBtn.IsEnabled  = _scaleIdx < Scales.Length - 1;
        // Тултип называет масштаб, КУДА ведет нажатие («Масштаб: 10 лет»).
        ToolTip.SetTip(ScaleMinusBtn, _scaleIdx > 0
            ? $"Масштаб: {Scales[_scaleIdx - 1].Label}" : null);
        ToolTip.SetTip(ScalePlusBtn, _scaleIdx < Scales.Length - 1
            ? $"Масштаб: {Scales[_scaleIdx + 1].Label}" : null);
    }

    // Delete/Backspace удаляют выделенное, пока вид видим и фокус не в текстовом
    // поле — оконный обработчик, как в Месяце.
    private EventHandler<KeyEventArgs>? _windowKeyHandler;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (e.Root is not Window win) return;
        _windowKeyHandler = (_, ke) =>
        {
            if (!IsVisible) return;
            // Клавишу уже забрала боковая панель (удаление коллекции) — второй раз не наша.
            if (KeyClaim.Claimed(ke)) return;
            if (_selection.Count == 0) return;
            if (ke.Source is TextBox or AutoCompleteBox) return;

            if (ShortcutSettings.Matches("calendar.deleteSelection", ke))
            {
                if (_selection.Count == 1)
                {
                    var only = _selection[0].Ev;
                    DeselectAll();
                    RecordDeleteRequested?.Invoke(this, only);
                }
                else
                {
                    var batch = _selection.Select(b => b.Ev).ToList();
                    DeselectAll();
                    RecordsDeleteRequested?.Invoke(this, batch);
                }
                ke.Handled = true;
            }
            else if (ShortcutSettings.Matches("calendar.duplicateSelection", ke))
            {
                var batch = _selection.Select(b => b.Ev).ToList();
                DeselectAll();
                if (batch.Count == 1) RecordDuplicateRequested?.Invoke(this, batch[0]);
                else                  RecordsDuplicateRequested?.Invoke(this, batch);
                ke.Handled = true;
            }
            else if (ShortcutSettings.Matches("calendar.openEvent", ke))
            {
                RecordClicked?.Invoke(this, _selection[0].Ev);
                ke.Handled = true;
            }
        };
        win.AddHandler(KeyDownEvent, _windowKeyHandler, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (e.Root is Window win && _windowKeyHandler is not null)
            win.RemoveHandler(KeyDownEvent, _windowKeyHandler);
        _windowKeyHandler = null;
    }

    // ── публичный API ───────────────────────────────────────────────────────────

    public void Refresh(IEnumerable<CalendarRecord> events, DateTime currentDate,
                        bool scrollToNow = false,
                        IReadOnlyList<(int DbId, DbCollection Col)>? collections = null,
                        bool hideEmptyGroups = false,
                        IReadOnlyDictionary<(int DbId, int ColId), DiagramGroupStat>? stats = null,
                        IReadOnlyCollection<CalendarRecord>? select = null)
    {
        // select (созданная запись, копии, перенесенная) заменяет выделение — как нажатие по ним;
        // записи свернутых групп не выделяются.
        if (select is not null)
        {
            DeselectAll();
            _restoreSelection = KeysOf(select);
        }
        _events          = events.ToList();
        _currentDate     = currentDate;
        var (rs, re)     = RangeFor(currentDate);
        _rangeStart      = rs;
        _rangeDays       = (int)(re - rs).TotalDays;
        _collections     = collections;
        _hideEmptyGroups = hideEmptyGroups;
        _stats           = stats;

        BuildGroups();
        BuildAll();

        if (scrollToNow)
            Dispatcher.UIThread.Post(() => RowsScroll.Offset = default, DispatcherPriority.Loaded);
    }

    // ── группировка ─────────────────────────────────────────────────────────────

    private void BuildGroups()
    {
        // Каркас групп — отмеченные коллекции всех БД (порядок сайдбара: БД по порядку
        // монтирования, коллекции по Position), даже без событий в показанном месяце.
        // События раскладываются по своим группам; события коллекций вне каркаса
        // (нет CollectionId и т.п.) образуют группы в конце.
        var groups = new List<GroupRow>();
        var byKey  = new Dictionary<(int DbId, int ColId), GroupRow>();

        if (_collections is not null)
            foreach (var (dbId, col) in _collections)
            {
                var g = new GroupRow
                {
                    DbId = dbId, ColId = col.Id ?? 0,
                    Name = TitleText.FirstLine(col.Name),   // подпись строки — одна строка
                    Color = col.Color,
                };
                if (byKey.TryAdd((g.DbId, g.ColId), g)) groups.Add(g);
            }

        foreach (var e in _events)
        {
            var key = (DbId: e.DbId, ColId: e.CollectionId ?? 0);
            if (!byKey.TryGetValue(key, out var g))
            {
                g = new GroupRow
                {
                    DbId  = key.DbId,
                    ColId = key.ColId,
                    // Подпись строки Диаграммы — одна строка (TitleText).
                    Name  = string.IsNullOrWhiteSpace(e.CollectionName)
                                ? "Без коллекции" : TitleText.FirstLine(e.CollectionName),
                    Color = e.Color,
                };
                byKey[key] = g;
                groups.Add(g);
            }
            g.Records.Add(e);
        }

        if (_hideEmptyGroups)
            groups = groups.Where(g => g.Records.Count > 0).ToList();

        foreach (var g in groups)
        {
            g.Records = g.Records
                .OrderBy(e => e.StartDt)
                .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Точные агрегаты (чтение окна обрезано cap'ом на коллекцию); до их прихода
            // и при поиске — от доставленного. Агрегаты не могут знать меньше доставленного.
            g.Stat  = _stats?.GetValueOrDefault((g.DbId, g.ColId));
            g.Total = Math.Max(g.Stat?.Count ?? 0, g.Records.Count);
            // Свернуто по умолчанию: диаграмма открывается списком коллекций с суммарными
            // барами, записи разворачивает сам пользователь (в словаре — только то, что он
            // менял руками). При активном поиске группы раскрыты: иначе найденного не видно.
            g.Collapsed = !_hideEmptyGroups
                          && (!_collapsed.TryGetValue((g.DbId, g.ColId), out var c) || c);
        }

        _groups = groups;
    }

    private List<RowItem> BuildRowPlan()
    {
        var rows = new List<RowItem>();
        var groupsShown = 0;
        foreach (var g in _groups)
        {
            var shown = Math.Min(g.Records.Count, MaxRecordsPerCollection);
            var need  = 1 + (g.Collapsed ? 0 : shown + (g.Total > shown ? 1 : 0));
            if (groupsShown > 0 && rows.Count + need > MaxTotalRows)
            {
                var left = _groups.Count - groupsShown;
                rows.Add(new RowItem { Group = g, Note = $"+{left} {Plural(left, "коллекция", "коллекции", "коллекций")} еще" });
                break;
            }

            rows.Add(new RowItem { Group = g });
            groupsShown++;
            if (g.Collapsed) continue;

            for (var i = 0; i < shown; i++)
                rows.Add(new RowItem { Group = g, Ev = g.Records[i] });
            if (g.Total > shown)
                rows.Add(new RowItem { Group = g, Note = $"+{g.Total - shown} еще" });
        }
        return rows;
    }

    // ── единый построитель ──────────────────────────────────────────────────────

    private void BuildAll()
    {
        // Выделение переживает пересборку (приход агрегатов, правка, сворачивание группы): ключи
        // записей — до очистки, восстанавливается на новых строках. Якорь и ховер не переживают.
        var keep = _selection.Count > 0 ? KeysOf(_selection.Select(b => b.Ev)) : _restoreSelection;
        CancelDrag();
        DayHeaderCanvas.Children.Clear();
        LeftRowsCanvas.Children.Clear();
        BgCanvas.Children.Clear();
        BarsCanvas.Children.Clear();
        DragOverlay.Children.Clear();
        _barEntries.Clear();
        _selection.Clear();
        _rangeAnchor = null;
        _hover       = null;

        EmptyHint.IsVisible = _groups.Count == 0;

        // Ширина — у холста таймлайна, а не «ширина окна − LeftW»: справа отдельной
        // колонкой стоит полоса прокрутки, и вычитание только левой панели рисовало
        // последний день ПОД полосой (шкала обрывалась на полуцифре). Как в таблице
        // конструктора: ячейки заканчиваются там, где начинается полоса.
        var timelineW = _timelineW;
        if (timelineW <= 0 || _rangeDays <= 0) { _restoreSelection = keep; return; }
        var dayW = timelineW / _rangeDays;
        _lastTimelineW = timelineW;

        BuildDayHeader(dayW);

        var rows     = _groups.Count == 0 ? new List<RowItem>() : BuildRowPlan();
        _rowPlan     = rows;
        _hoverRowIdx = -1;
        var contentH = rows.Count * RowH;
        _lastBgH     = contentH;

        // Явные высоты слоев задают extent прокрутки; фон, линии дней и линия
        // «сейчас» заканчиваются на последней строке, а не тянутся до низа окна.
        LeftRowsCanvas.Height = contentH;
        BgCanvas.Height       = contentH;
        BarsCanvas.Height     = contentH;
        TodayOverlay.Height   = contentH;
        DragOverlay.Height    = contentH;

        BuildBackground(dayW, timelineW, contentH);

        _realizedRows.Clear();
        _entryByRow.Clear();
        _buildDayW = dayW;
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Ev is { } ev)
            {
                var entry = EntryFor(ev, i, dayW);
                _barEntries.Add(entry);
                _entryByRow[i] = entry;
            }
        RealizeVisibleRows();

        UpdateTodayLine();
        _restoreSelection = null;
        if (keep is not null) ApplySelection(keep);
    }

    // Выделение, которое восстановит следующая пересборка (эта не состоялась: нет ширины).
    private HashSet<(int DbId, int Id, int FieldId)>? _restoreSelection;

    private static HashSet<(int DbId, int Id, int FieldId)> KeysOf(IEnumerable<CalendarRecord> records) =>
        records.Where(r => r.Id.HasValue).Select(r => (r.DbId, r.Id!.Value, r.FieldId)).ToHashSet();

    private void ApplySelection(HashSet<(int DbId, int Id, int FieldId)> keys)
    {
        foreach (var b in _barEntries)
            if (b.Ev.Id is int id && keys.Contains((b.Ev.DbId, id, b.Ev.FieldId))) AddToSelection(b);
    }

    private void BuildDayHeader(double dayW)
    {
        // Ячейки шапки по масштабу: месяц — дни, год — месяцы, 5/10 лет — годы
        // (ячейки месяцев там уже нечитаемы).
        if (ScaleYears >= 5) { BuildYearHeader(dayW);  return; }
        if (ScaleYears >= 1) { BuildMonthHeader(dayW); return; }

        var secondary = BrushOf("BrushSecondaryText");
        var primary   = BrushOf("BrushPrimaryText");
        var red       = BrushOf("BrushRed");
        var accent    = BrushOf("BrushAccent");

        for (var i = 0; i < _rangeDays; i++)
        {
            var d         = _rangeStart.AddDays(i);
            var isToday   = d.Date == DateTime.Today;   // синим и жирным — как шапка Недели
            var isWeekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

            var name = new TextBlock
            {
                Text       = DayNames[((int)d.DayOfWeek + 6) % 7],
                FontSize   = FontSizeSmall,
                FontWeight = isToday ? WeightBold : FontWeight.Normal,
                Foreground = isToday ? accent : isWeekend ? red : secondary,
            };
            name.Measure(Size.Infinity);
            Canvas.SetLeft(name, i * dayW + (dayW - name.DesiredSize.Width) / 2);
            Canvas.SetTop(name, 6);
            DayHeaderCanvas.Children.Add(name);

            var num = new TextBlock
            {
                Text       = d.Day.ToString(),
                FontSize   = FontSizeBase,
                FontWeight = isToday ? WeightBold : FontWeight.Normal,
                Foreground = isToday ? accent : isWeekend ? red : primary,
            };
            num.Measure(Size.Infinity);
            Canvas.SetLeft(num, i * dayW + (dayW - num.DesiredSize.Width) / 2);
            Canvas.SetTop(num, 21);
            DayHeaderCanvas.Children.Add(num);
        }
    }

    // Шапка масштаба «год»: ячейки месяцев (границы — по реальному числу дней).
    // Полное имя месяца, не влезло в ячейку — трехбуквенное («Янв»). Текущий
    // месяц — синим и жирным, как «сегодня» в шапке Недели.
    private void BuildMonthHeader(double dayW)
    {
        var primary = BrushOf("BrushPrimaryText");
        var accent  = BrushOf("BrushAccent");
        var ru      = new System.Globalization.CultureInfo("ru-RU");
        var today   = DateTime.Today;

        for (var m = _rangeStart; m < RangeEnd; m = m.AddMonths(1))
        {
            var x1 = (m - _rangeStart).Days * dayW;
            var x2 = (m.AddMonths(1) - _rangeStart).Days * dayW;

            var isCurrent = m.Year == today.Year && m.Month == today.Month;
            var tb = new TextBlock
            {
                Text       = Capitalize(ru.DateTimeFormat.MonthNames[m.Month - 1]),
                FontSize   = FontSizeBase,
                FontWeight = isCurrent ? WeightBold : FontWeight.Normal,
                Foreground = isCurrent ? accent : primary,
            };
            tb.Measure(Size.Infinity);
            if (tb.DesiredSize.Width > x2 - x1 - 6)
            {
                tb.Text = Capitalize(ru.DateTimeFormat.AbbreviatedMonthNames[m.Month - 1]);
                tb.Measure(Size.Infinity);
            }
            Canvas.SetLeft(tb, x1 + (x2 - x1 - tb.DesiredSize.Width) / 2);
            Canvas.SetTop(tb, (44 - tb.DesiredSize.Height) / 2);
            DayHeaderCanvas.Children.Add(tb);
        }
    }

    // Шапка масштабов «5 лет»/«10 лет»: ячейки годов, текущий — синим и жирным.
    private void BuildYearHeader(double dayW)
    {
        var primary = BrushOf("BrushPrimaryText");
        var accent  = BrushOf("BrushAccent");
        var today   = DateTime.Today;

        for (var y = _rangeStart.Year; y < RangeEnd.Year; y++)
        {
            var x1 = (new DateTime(y, 1, 1) - _rangeStart).Days * dayW;
            var x2 = (new DateTime(y + 1, 1, 1) - _rangeStart).Days * dayW;
            var tb = new TextBlock
            {
                Text       = y.ToString(),
                FontSize   = FontSizeBase,
                FontWeight = y == today.Year ? WeightBold : FontWeight.Normal,
                Foreground = y == today.Year ? accent : primary,
            };
            tb.Measure(Size.Infinity);
            Canvas.SetLeft(tb, x1 + (x2 - x1 - tb.DesiredSize.Width) / 2);
            Canvas.SetTop(tb, (44 - tb.DesiredSize.Height) / 2);
            DayHeaderCanvas.Children.Add(tb);
        }
    }

    // Индексы дней, где начинается новый месяц/год внутри периода (без нулевого) —
    // границы ячеек шапки и вертикальные линии сетки крупных масштабов.
    private IEnumerable<int> BoundaryIndices(bool byYear)
    {
        var d = byYear ? _rangeStart.AddYears(1) : _rangeStart.AddMonths(1);
        for (; d < RangeEnd; d = byYear ? d.AddYears(1) : d.AddMonths(1))
            yield return (d - _rangeStart).Days;
    }

    private static string Capitalize(string s)
        => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    // Фон таймлайна: заливка выходных → тинты строк групп → линии дней → линии строк.
    // Все обрывается на последней строке (contentH), ниже — чистый фон.
    // Линии — пустые Border с Aliased + LayoutRounding (защелкиваются в целый пиксель,
    // текста в поддереве нет — сглаживанию текста не мешают), см. docs/ui/theming.md.
    private void BuildBackground(double dayW, double timelineW, double contentH)
    {
        // Заливка выходных — только на месячном масштабе: уже на годовом ячейка
        // дня ~3 px, и 52 пары серых полос сливаются в рябь.
        if (ScaleYears == 0)
        {
            var weekendBg = BrushOf("BrushSecondaryBg");
            for (var i = 0; i < _rangeDays; i++)
            {
                var d = _rangeStart.AddDays(i);
                if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) continue;
                var fill = new Border { Width = dayW, Height = contentH, Background = weekendBg };
                Canvas.SetLeft(fill, i * dayW);
                BgCanvas.Children.Add(fill);
            }
        }

        // Заливки строк групп — слой под полосой наведения (строки добавляются по мере показа).
        _bandLayer     = new Canvas { IsHitTestVisible = false };
        _leftBandLayer = new Canvas { IsHitTestVisible = false };
        BgCanvas.Children.Add(_bandLayer);
        LeftRowsCanvas.Children.Add(_leftBandLayer);

        // Полоса наведения строки записи — голубая, как ховер строки таблицы
        // конструктора; под линиями сетки, поверх заливок. Позиционирует SetRowHover.
        var hoverBrush  = BrushOf("BrushAccentLight");
        _hoverBandRight = new Border { Width = timelineW, Height = RowH, Background = hoverBrush, IsVisible = false };
        BgCanvas.Children.Add(_hoverBandRight);
        _hoverBandLeft  = new Border { Width = LeftW, Height = RowH, Background = hoverBrush, IsVisible = false };
        LeftRowsCanvas.Children.Add(_hoverBandLeft);

        var lineT     = LineMetrics.ThicknessFor(this);
        var lineBrush = new SolidColorBrush(Color.Parse("#E8E8E8"));
        _lineT     = lineT;
        _lineBrush = lineBrush;
        // Разделитель левой панели в теле (продолжение separator'а шапки) — до
        // последней строки, как и остальные линии.
        var paneLine = new Border { Width = lineT, Height = contentH, Background = BrushOf("BrushSeparator") };
        paneLine.Bind(UseLayoutRoundingProperty, paneLine.GetResourceObservable("LineUseLayoutRounding"));
        RenderOptions.SetEdgeMode(paneLine, EdgeMode.Aliased);
        BgCanvas.Children.Add(paneLine);
        // Вертикальные линии: на месяце — по дням, на годе — по границам месяцев
        // (дневные линии при ячейке ~3 px — сплошная серость), на 5/10 годах —
        // по границам годов (месячные ячейки уже нечитаемы). x=0 не рисуется —
        // там разделитель левой панели.
        var vlines = ScaleYears switch
        {
            0 => Enumerable.Range(1, _rangeDays - 1),
            1 => BoundaryIndices(byYear: false),
            _ => BoundaryIndices(byYear: true),
        };
        foreach (var i in vlines)
        {
            var line = new Border { Width = lineT, Height = contentH, Background = lineBrush };
            line.Bind(UseLayoutRoundingProperty, line.GetResourceObservable("LineUseLayoutRounding"));
            RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
            Canvas.SetLeft(line, i * dayW);
            BgCanvas.Children.Add(line);
        }
        // Горизонтальные разделители строк — слой поверх линий дней (строки — по мере показа).
        _lineLayer     = new Canvas { IsHitTestVisible = false };
        _leftLineLayer = new Canvas { IsHitTestVisible = false };
        BgCanvas.Children.Add(_lineLayer);
        LeftRowsCanvas.Children.Add(_leftLineLayer);
    }

    // Строки видимой части ленты (с запасом в экран сверху и снизу), еще не построенные.
    private void RealizeVisibleRows()
    {
        if (_rowPlan.Count == 0 || _lastTimelineW <= 0) return;
        var rowH  = RowH;
        var view  = RowsScroll.Viewport.Height > 0 ? RowsScroll.Viewport.Height : Math.Max(Bounds.Height, 900);
        var top   = RowsScroll.Offset.Y;
        var first = Math.Max(0, (int)((top - view) / rowH));
        var last  = Math.Min(_rowPlan.Count - 1, (int)((top + 2 * view) / rowH));
        for (var i = first; i <= last; i++)
            if (_realizedRows.Add(i)) RealizeRow(i);
    }

    private void RealizeRow(int i)
    {
        var r = _rowPlan[i];
        var y = i * RowH;
        var timelineW = _lastTimelineW;

        if (r.Ev is null && r.Note is null)
        {
            var tint = new SolidColorBrush(ColorTools.BlendWithWhite(DisplayColor(r.Group.Color), 14));
            var band = new Border { Width = timelineW, Height = RowH, Background = tint };
            Canvas.SetTop(band, y);
            _bandLayer.Children.Add(band);
            var bandL = new Border { Width = LeftW, Height = RowH, Background = tint, IsHitTestVisible = false };
            Canvas.SetTop(bandL, y);
            _leftBandLayer.Children.Add(bandL);
        }

        // Разделитель — по низу строки, слева и справа встык.
        foreach (var (layer, w) in new[] { (_lineLayer, timelineW), (_leftLineLayer, LeftW) })
        {
            var line = new Border { Width = w, Height = _lineT, Background = _lineBrush };
            line.Bind(UseLayoutRoundingProperty, line.GetResourceObservable("LineUseLayoutRounding"));
            RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
            Canvas.SetTop(line, (i + 1) * RowH - _lineT);
            layer.Children.Add(line);
        }

        if      (r.Ev is not null)   BuildRecordRow(_entryByRow[i], y, _buildDayW, timelineW);
        else if (r.Note is not null) BuildNoteRow(r.Note, y);
        else                         BuildGroupRow(r.Group, y, _buildDayW, timelineW);
    }

    // Геометрия бара записи без контролов — для попадания указателя, выделения и переноса.
    private BarEntry EntryFor(CalendarRecord ev, int rowIdx, double dayW)
    {
        var g = EventGeometry(ev.StartDt, ev.EndDt, ev.AllDay, dayW);
        return new BarEntry
        {
            Ev = ev, Y = rowIdx * RowH, RowIdx = rowIdx,
            X1 = g.IsDot ? g.Cx - BarH / 2 : g.X1, X2 = g.IsDot ? g.Cx + BarH / 2 : g.X2,
            IsDot = g.IsDot, ClipL = g.ClipL, ClipR = g.ClipR,
        };
    }

    // ── строки ──────────────────────────────────────────────────────────────────

    private void BuildGroupRow(GroupRow g, double y, double dayW, double timelineW)
    {
        var color = DisplayColor(g.Color);

        // Левая панель: шеврон + имя + счетчик. Клик — свернуть/развернуть.
        // Цвет коллекции показывает сам бар справа, цветной точки в строке нет.
        var row = new Grid
        {
            Width      = LeftW,
            Height     = RowH,
            Background = Brushes.Transparent,
            Cursor     = CursorHand,
            ColumnDefinitions = new ColumnDefinitions($"{LeftIndent},*,Auto"),
        };
        Canvas.SetTop(row, y);

        // Стрелка — как в дереве сайдбара: IconSizeSmall в слоте, обводка IconStrokeSmall.
        var arrow = Res("IconSizeSmall", 16);
        var chevron = new Viewbox
        {
            Width  = arrow, Height = arrow,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
            IsHitTestVisible    = false,
            Child = new Canvas
            {
                Width = 24, Height = 24,
                Children =
                {
                    new Avalonia.Controls.Shapes.Path
                    {
                        Data            = (Geometry)Application.Current!.FindResource(g.Collapsed ? "IconChevronRight" : "IconChevronDown")!,
                        Stroke          = BrushOf("BrushSecondaryText"),
                        StrokeThickness = Res("IconStrokeSmall", 1.8),
                        StrokeLineCap   = PenLineCap.Round,
                        StrokeJoin      = PenLineJoin.Round,
                    },
                },
            },
        };
        row.Children.Add(chevron);

        var name = new TextBlock
        {
            Text         = g.Name,
            FontSize     = FontSizeBase,
            FontWeight   = WeightBold,
            Foreground   = BrushOf("BrushPrimaryText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin       = new Thickness(4, 0, 4, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsHitTestVisible  = false,
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var count = new TextBlock
        {
            Text       = g.Total.ToString(),
            FontSize   = FontSizeSmall,
            Foreground = BrushOf("BrushSecondaryText"),
            Margin     = new Thickness(0, 0, 10, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsHitTestVisible  = false,
        };
        Grid.SetColumn(count, 2);
        row.Children.Add(count);

        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            _collapsed[(g.DbId, g.ColId)] = !g.Collapsed;
            g.Collapsed = !g.Collapsed;
            BuildAll();
            e.Handled = true;
        };
        LeftRowsCanvas.Children.Add(row);

        // Пустая в этом периоде коллекция — только строка слева, без суммарного бара.
        if (g.Total == 0) return;

        // Суммарный бар: min(начало)…max(конец) по ВСЕМ событиям группы в окне.
        // Источник — агрегаты фазы 2 (чтение окна обрезано cap'ом на коллекцию, охват
        // должен знать и про необрезанное); до их прихода и при поиске — доставленные
        // записи. Диапазон группы рисуется КАК СОБЫТИЕ (тот же рендер, что у записей):
        // блок по ячейкам дней, однодневный охват — круг по центру ячейки; подпись —
        // суммарный охват дней И времени («30 минут», «1 день 3 часа 30 минут»).
        // Полностью all-day группа считается как all-day («N дней»/«24 часа»);
        // в смешанной all-day-конец берется концом суток, чтобы длительность не
        // засорялась хвостом «23 часа 59 минут».
        bool allDayGroup;
        DateTime spanStart, spanEnd;
        if (g.Stat is { } st)
        {
            allDayGroup = st.AllDayCount == st.Count;
            spanStart   = st.MinStart;
            spanEnd     = allDayGroup
                ? st.MaxEndAllDay ?? spanStart
                : new[] { st.MaxEndTimed ?? DateTime.MinValue,
                          st.MaxEndAllDay?.Date.AddDays(1) ?? DateTime.MinValue }.Max();
        }
        else if (g.Records.Count > 0)
        {
            allDayGroup = g.Records.All(e => e.AllDay);
            spanStart   = g.Records.Min(e => e.StartDt);
            spanEnd     = allDayGroup
                ? g.Records.Max(e => e.EndDt)
                : g.Records.Max(e => e.AllDay ? e.EndDt.Date.AddDays(1) : e.EndDt);
        }
        else return;   // счетчик есть, а строк и агрегатов нет — бар нарисует фаза 2

        RenderEventVisual(BarsCanvas, [], y, spanStart, spanEnd, allDayGroup,
                          color, dayW, timelineW);
    }

    private void BuildRecordRow(BarEntry entry, double y, double dayW, double timelineW)
    {
        var ev = entry.Ev;
        // Левая панель: только название записи.
        var row = new Grid
        {
            Width      = LeftW,
            Height     = RowH,
            Background = Brushes.Transparent,
            Cursor     = CursorHand,
            ColumnDefinitions = new ColumnDefinitions($"{LeftIndent},*"),
        };
        Canvas.SetTop(row, y);

        var title = new TextBlock
        {
            Text         = ev.DisplayTitle,
            FontSize     = FontSizeBase,
            Foreground   = BrushOf("BrushPrimaryText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin       = new Thickness(0, 0, 10, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            IsHitTestVisible  = false,
        };
        Grid.SetColumn(title, 1);
        row.Children.Add(title);

        var rowIdx = entry.RowIdx;
        row.PointerEntered += (_, _) => SetRowHover(rowIdx);
        row.PointerExited  += (_, _) => SetRowHover(-1);
        LeftRowsCanvas.Children.Add(row);

        // Таймлайн: бар или круг; геометрия записи уже посчитана (EntryFor), здесь — визуалы.
        entry.Vis = RenderEventVisual(
            BarsCanvas, entry.Visuals, y, ev.StartDt, ev.EndDt, ev.AllDay,
            DisplayColor(ev.Color), dayW, timelineW);
        if (_selection.Contains(entry)) ApplySelectedVisual(entry, on: true);

        // Клик по строке слева — как по блоку: одиночный выделяет (Shift/Ctrl+Shift —
        // аддитивно/диапазон), двойной открывает; правый — меню по выделению.
        row.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(row).Properties;
            if (props.IsRightButtonPressed)
            {
                if (!_selection.Contains(entry)) SelectOnly(entry);
                row.ContextMenu = BuildSelectionMenu();
                return;
            }
            if (!props.IsLeftButtonPressed) return;
            HandleEntryClick(entry, e.ClickCount, e.KeyModifiers);
            e.Handled = true;
        };
    }

    private void BuildNoteRow(string note, double y)
    {
        var tb = new TextBlock
        {
            Text       = note,
            FontSize   = FontSizeSmall,
            Foreground = BrushOf("BrushSecondaryText"),
        };
        tb.Measure(Size.Infinity);
        Canvas.SetLeft(tb, LeftIndent);
        Canvas.SetTop(tb, y + (RowH - tb.DesiredSize.Height) / 2);
        LeftRowsCanvas.Children.Add(tb);
    }

    // Геометрия события на шкале (без контролов): края блока, ячейки, обрезка краем периода,
    // круг ли и его центр. Общая для визуалов (RenderEventVisual) и записей строк (EntryFor).
    private (double X1, double X2, int I1, int I2, bool ClipL, bool ClipR, bool IsDot, double Cx) EventGeometry(
        DateTime start, DateTime end, bool allDay, double dayW)
    {
        // Геометрия — ПО ЯЧЕЙКАМ ДНЕЙ, как бары Месяца: блок занимает колонки от
        // первого до последнего дня события целиком, время суток на позицию не
        // влияет. Все, что уже круга (однодневное событие и любой короткий диапазон
        // на крупном масштабе), — сплошной круг диаметром в высоту блока.
        var startIdx = (int)(start.Date - _rangeStart).TotalDays;
        var endIdx   = (int)(LastIncludedDay(start, end, allDay) - _rangeStart).TotalDays;
        if (endIdx < startIdx) endIdx = startIdx;
        var clipL = startIdx < 0;
        var clipR = endIdx > _rangeDays - 1;
        var i1    = Math.Clamp(startIdx, 0, _rangeDays - 1);
        var i2    = Math.Clamp(endIdx,   0, _rangeDays - 1);
        // Небольшие отступы блока от границ ячеек — только на реальных краях события
        // (insetL/insetR как у баров Месяца) и только при широкой ячейке: на годовом
        // масштабе (ячейка ~3 px) отступы съедали бы целые дни. Обрезанный краем
        // периода край — вплотную.
        var insetL = dayW >= 8 ? 3.0 : 0.0;
        var insetR = dayW >= 8 ? 2.0 : 0.0;
        var x1     = i1 * dayW + (clipL ? 0 : insetL);
        var x2     = (i2 + 1) * dayW - (clipR ? 0 : insetR);
        // Круг — однодневное событие или блок не шире круга (см. RenderEventVisual).
        var isDot = startIdx == endIdx || x2 - x1 < BarH;
        return (x1, x2, i1, i2, clipL, clipR, isDot, (i1 + i2 + 1) / 2.0 * dayW);
    }

    // Рисует событие (блок или круг) в строке y и возвращает геометрию + слои
    // для эффектов. Используется для записей, диапазона группы и ghost при drag.
    private EventVisual RenderEventVisual(
        Canvas canvas, List<Control> visuals, double y,
        DateTime start, DateTime end, bool allDay, Color color,
        double dayW, double timelineW)
    {
        var (x1, x2, i1, i2, clipL, clipR, isDot, cx) = EventGeometry(start, end, allDay, dayW);

        var dark  = new SolidColorBrush(ColorTools.Darken(color));
        var label = new TextBlock
        {
            Text       = RuDuration(start, end, allDay),
            FontSize   = FontSizeBase,
            FontWeight = WeightBold,   // как заголовок события в Месяце
            Foreground = dark,
        };
        label.Measure(Size.Infinity);
        var tw = label.DesiredSize.Width;

        // Подпись ВНЕ блока — в хосте высотой в строку: вертикальный центр считает
        // layout по реальным метрикам шрифта (ручной расчет от предварительного
        // Measure давал сдвиг — до подключения к дереву метрики немного другие).
        void AddOutside(double lx)
        {
            label.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            var host = new Border { Height = RowH, Child = label };
            Canvas.SetLeft(host, Math.Max(0, lx));
            Canvas.SetTop(host, y);
            canvas.Children.Add(host);
            visuals.Add(host);
        }

        // Круг — вид события, которое не растянуто: однодневное (на любом масштабе, даже
        // когда ячейка дня шире круга) ИЛИ такое, чей блок не шире круга. Второе — про
        // крупные масштабы: на годе, 5 и 10 годах ячейка дня — единицы пикселей, и «4 дня»
        // превратились бы в невидимую полоску. Круг стоит по центру закрытых событием
        // ячеек: на пороге (блок ровно в круг) это та же точка, где у блока стоит круг
        // начала, — перехода не видно.
        if (isDot)
        {
            var dot = new Avalonia.Controls.Shapes.Ellipse
            {
                Width = BarH, Height = BarH,
                Fill  = new SolidColorBrush(color),
            };
            Canvas.SetLeft(dot, cx - BarH / 2);
            Canvas.SetTop(dot, y + (RowH - BarH) / 2);
            canvas.Children.Add(dot);
            visuals.Add(dot);

            var lx = cx + BarH / 2 + LabelGap;
            if (lx + tw > timelineW) lx = cx - BarH / 2 - LabelGap - tw;
            AddOutside(lx);
            return new EventVisual
            {
                X1 = cx - BarH / 2, X2 = cx + BarH / 2, IsDot = true,
                ClipL = clipL, ClipR = clipR,
                Dot = dot, Label = label,
            };
        }

        var w    = x2 - x1;
        var barY = y + (RowH - BarH) / 2;
        var bar  = MakeBlock(w, color, clipL, clipR, out var blockBg, out var circle);
        Canvas.SetLeft(bar, x1);
        Canvas.SetTop(bar, barY);
        canvas.Children.Add(bar);
        visuals.Add(bar);

        // Подпись предпочитает место ВНЕ блока: справа, не влезла — слева; внутрь
        // (в начало, сразу после круга) уходит только когда снаружи места нет —
        // блок почти во всю ширину таймлайна.
        var fitsR  = x2 + LabelGap + tw <= timelineW;
        var fitsL  = x1 - LabelGap - tw >= 0;
        var inside = !fitsR && !fitsL;
        if (inside)
        {
            label.VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center;
            label.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            label.Margin              = new Thickness(circle is null ? 8 : BarH + LabelGap, 0, 8, 0);
            bar.Children.Add(label);   // скрытие на drag наследуется от блока
        }
        else
        {
            AddOutside(fitsR ? x2 + LabelGap : x1 - LabelGap - tw);
        }
        return new EventVisual
        {
            X1 = x1, X2 = x2, ClipL = clipL, ClipR = clipR,
            BlockBg = blockBg, Label = label, LabelInside = inside,
        };
    }

    // Блок-«пилюля»: белая подложка глушит линии сетки и заливку выходных под
    // баром, поверх — полупрозрачный цвет; скругление в половину высоты — силуэт
    // совпадает с кругом у начала. Скругление и круг — только на реальных краях
    // события. Вызывается лишь для блока ШИРЕ круга (что уже — рисуется кругом),
    // поэтому круг не помещается только у обрезанного левым краем периода события.
    private static Grid MakeBlock(double w, Color color, bool clipL, bool clipR,
                                  out Border bg, out Border? circle)
    {
        var r   = BarH / 2;
        var cr  = new CornerRadius(clipL ? 0 : r, clipR ? 0 : r, clipR ? 0 : r, clipL ? 0 : r);
        var bar = new Grid { Width = w, Height = BarH };
        bar.Children.Add(new Border { Background = Brushes.White, CornerRadius = cr });
        bg = new Border
        {
            Background   = new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B)),
            CornerRadius = cr,
        };
        bar.Children.Add(bg);
        circle = null;
        if (!clipL)
        {
            circle = new Border
            {
                Width               = BarH,
                Background          = new SolidColorBrush(color),
                CornerRadius        = new CornerRadius(r),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            bar.Children.Add(circle);
        }
        return bar;
    }

    // ── линия «сейчас» ──────────────────────────────────────────────────────────

    private void UpdateTodayLine()
    {
        TodayOverlay.Children.Clear();
        var now = DateTime.Now;
        if (_rangeDays <= 0 || now < _rangeStart || now >= RangeEnd) return;
        if (_lastTimelineW <= 0) return;

        var x    = (now - _rangeStart).TotalDays * (_lastTimelineW / _rangeDays);
        var line = new Border { Width = 2, Height = _lastBgH, Background = BrushOf("BrushRed") };
        Canvas.SetLeft(line, x - 1);
        TodayOverlay.Children.Add(line);
    }

    // ── drag / resize ───────────────────────────────────────────────────────────

    private void OnPanePressed(object? sender, PointerPressedEventArgs e)
    {
        var p     = e.GetPosition(TimelinePane);
        var props = e.GetCurrentPoint(TimelinePane).Properties;

        // Правый клик: по невыделенной записи — выделить ее одну (как в Месяце),
        // меню собирается по актуальному выделению и откроется на отпускании.
        if (props.IsRightButtonPressed)
        {
            var rHit = HitTest(p);
            if (rHit is null) { TimelinePane.ContextMenu = null; return; }
            if (!_selection.Contains(rHit)) SelectOnly(rHit);
            TimelinePane.ContextMenu = BuildSelectionMenu();
            return;
        }

        if (!props.IsLeftButtonPressed) return;
        var hit = HitTest(p);
        // Клик мимо — снять выделение (Shift-клики набор не сбрасывают, как в Расписании).
        if (hit is null)
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift)) DeselectAll();
            return;
        }

        _pressEntry      = hit;
        _pressPos        = p;
        _pressClickCount = e.ClickCount;
        _pressModifiers  = e.KeyModifiers;
        _isDragging      = false;
        // Узкий бар (короткое событие на годовом масштабе) не ресайзится: зоны краев
        // накрыли бы его целиком, не оставив места для переноса.
        var canResize = !hit.IsDot && hit.X2 - hit.X1 >= EdgeHit * 3;
        _resizeLeft  = canResize && !hit.ClipL && Math.Abs(p.X - hit.X1) <= EdgeHit;
        _resizeRight = canResize && !_resizeLeft && !hit.ClipR && Math.Abs(p.X - hit.X2) <= EdgeHit;
        _origStart   = hit.Ev.StartDt;
        _origEnd     = hit.Ev.EndDt;
        _newStart    = _origStart;
        _newEnd      = _origEnd;

        e.Pointer.Capture(TimelinePane);
        e.Handled = true;
    }

    private void OnPaneMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(TimelinePane);
        if (_pressEntry is null) { UpdateCursor(p); return; }

        var dayW = _rangeDays > 0 ? _lastTimelineW / _rangeDays : 0;
        if (dayW <= 0) return;

        if (!_isDragging)
        {
            if (Math.Abs(p.X - _pressPos.X) + Math.Abs(p.Y - _pressPos.Y) < 5) return;
            _isDragging = true;
            DeselectAll();      // drag отменяет выделение — как в Месяце
            SetRowHover(-1);    // и прячет подсветку строки
            // Исходник прячется целиком (в Месяце он приглушается, здесь полупрозрачный
            // остаток на старом месте не нужен) — виден только ghost на новой позиции.
            foreach (var v in _pressEntry.Visuals) v.IsVisible = false;
        }

        // 5-дневка: край растягивания и старт переноса на Сб/Вс не встают —
        // перепрыгивают по направлению жеста (за пятницу — понедельник, назад —
        // пятница). Направление считается от ИСХОДНОГО положения (стабильно в
        // течение жеста). Полосы через выходные при этом не обрезаются, событие
        // с исходным краем на выходном без движения не трогается (direction 0).
        var fiveDay = CalendarSettings.FiveDayWeek;
        if (_resizeLeft || _resizeRight)
        {
            var dayIdx = Math.Clamp((int)(p.X / dayW), 0, _rangeDays - 1);
            var day    = _rangeStart.AddDays(dayIdx);
            if (_resizeRight)
            {
                if (fiveDay)
                    day = CalendarMath.SnapToWorkday(day, Math.Sign((day - _origEnd.Date).Ticks), true);
                // Новый день конца + исходное время суток; конец не раньше начала.
                var candidate = day + _origEnd.TimeOfDay;
                if (candidate > _newStart) _newEnd = candidate;
            }
            else
            {
                if (fiveDay)
                    day = CalendarMath.SnapToWorkday(day, Math.Sign((day - _origStart.Date).Ticks), true);
                var candidate = day + _origStart.TimeOfDay;
                if (candidate < _newEnd) _newStart = candidate;
            }
        }
        else
        {
            // Перенос целыми днями: время суток и длительность сохраняются.
            var delta   = (int)Math.Round((p.X - _pressPos.X) / dayW);
            var center0 = _origStart + (_origEnd - _origStart) / 2;
            var minD    = Math.Min(0, (int)Math.Ceiling((_rangeStart - center0).TotalDays));
            var maxD    = Math.Max(0, (int)Math.Floor((RangeEnd - center0).TotalDays));
            delta       = Math.Clamp(delta, minD, maxD);
            _newStart   = _origStart.AddDays(delta);
            _newEnd     = _origEnd.AddDays(delta);
            if (fiveDay && delta != 0)
            {
                // Старт не встает на Сб/Вс, а конец считается по числу ВИДИМЫХ дней
                // исходной записи: пересечение нового положения с выходными растягивает
                // календарные даты через них (Чт–Пт, перенесенная на день вперед, —
                // это Пт–Пн, а не Пт–Сб).
                var snapped = CalendarMath.SnapToWorkday(_newStart, Math.Sign(delta), true);
                var span    = CalendarMath.VisibleDaysBetween(_origStart, _origEnd, true);
                _newStart   = snapped + _origStart.TimeOfDay;
                _newEnd     = CalendarMath.AddVisibleDays(snapped, span, true) + _origEnd.TimeOfDay;
            }
        }

        DragOverlay.Children.Clear();
        var ghostVisuals = new List<Control>();
        RenderEventVisual(DragOverlay, ghostVisuals, _pressEntry.Y,
                          _newStart, _newEnd, _pressEntry.Ev.AllDay,
                          DisplayColor(_pressEntry.Ev.Color), dayW, _lastTimelineW);
    }

    private void OnPaneReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressEntry is null) return;
        var entry       = _pressEntry;
        var wasDragging = _isDragging;

        e.Pointer.Capture(null);
        DragOverlay.Children.Clear();
        foreach (var v in entry.Visuals) v.IsVisible = true;
        _pressEntry = null;
        _isDragging = false;

        if (!wasDragging)
        {
            // Как в Месяце: одиночный клик выделяет (Shift — аддитивно,
            // Ctrl+Shift — диапазон), открывает только двойной клик.
            HandleEntryClick(entry, _pressClickCount, _pressModifiers);
            return;
        }

        if (_newStart != _origStart || _newEnd != _origEnd)
        {
            entry.Ev.StartDt = _newStart;
            entry.Ev.EndDt   = _newEnd;
            RecordMoved?.Invoke(this, entry.Ev);
        }
    }

    private void CancelDrag()
    {
        // Пересборка на живом drag: визуалы все равно пересоздаются — только сбросить состояние.
        _pressEntry = null;
        _isDragging = false;
    }

    private BarEntry? HitTest(Point p)
    {
        foreach (var b in _barEntries)
        {
            if (p.Y < b.Y || p.Y > b.Y + RowH) continue;
            var pad = b.IsDot ? 4 : EdgeHit;
            if (p.X >= b.X1 - pad && p.X <= b.X2 + pad) return b;
        }
        return null;
    }

    private void UpdateCursor(Point p)
    {
        var hit = HitTest(p);
        SetRowHover((int)(p.Y / RowH));

        // Ховер — как в Месяце: подсветка блока/круга под курсором.
        if (!ReferenceEquals(hit, _hover))
        {
            if (_hover is not null) ApplyHover(_hover, on: false);
            _hover = hit;
            if (_hover is not null) ApplyHover(_hover, on: true);
        }

        if (hit is null) { TimelinePane.Cursor = Cursor.Default; return; }
        var onEdge = !hit.IsDot && hit.X2 - hit.X1 >= EdgeHit * 3 &&
                     ((!hit.ClipL && Math.Abs(p.X - hit.X1) <= EdgeHit) ||
                      (!hit.ClipR && Math.Abs(p.X - hit.X2) <= EdgeHit));
        TimelinePane.Cursor = onEdge ? CursorResize : CursorHand;
    }

    // ── эффекты (ховер/выделение — как в Месяце) ────────────────────────────────

    // Подсветить строку записи idx (обе полосы); -1 или не-запись — спрятать.
    private void SetRowHover(int idx)
    {
        if (idx >= 0 && (idx >= _rowPlan.Count || _rowPlan[idx].Ev is null)) idx = -1;
        if (idx == _hoverRowIdx) return;
        _hoverRowIdx = idx;
        var on = idx >= 0;
        if (_hoverBandLeft is not null)
        {
            _hoverBandLeft.IsVisible = on;
            if (on) Canvas.SetTop(_hoverBandLeft, idx * RowH);
        }
        if (_hoverBandRight is not null)
        {
            _hoverBandRight.IsVisible = on;
            if (on) Canvas.SetTop(_hoverBandRight, idx * RowH);
        }
    }

    private void ApplyHover(BarEntry e, bool on)
    {
        if (_selection.Contains(e) || e.Vis is null) return;
        var color = DisplayColor(e.Ev.Color);
        if (e.Vis.Dot is not null)
            e.Vis.Dot.Fill = new SolidColorBrush(on ? ColorTools.Darken(color, 0.85) : color);
        else if (e.Vis.BlockBg is not null)
            e.Vis.BlockBg.Background =
                new SolidColorBrush(Color.FromArgb(on ? (byte)90 : (byte)50, color.R, color.G, color.B));
    }

    // Визуал выделения одной записи: блок заливается цветом (круг у начала сливается
    // с заливкой — прятать не нужно), круг однодневного события темнеет.
    private void ApplySelectedVisual(BarEntry e, bool on)
    {
        if (e.Vis is null) return;
        var color = DisplayColor(e.Ev.Color);
        if (e.Vis.Dot is not null)
        {
            e.Vis.Dot.Fill = new SolidColorBrush(on ? ColorTools.Darken(color) : color);
        }
        else if (e.Vis.BlockBg is not null)
        {
            e.Vis.BlockBg.Background = on
                ? new SolidColorBrush(color)
                : new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B));
            // Белый текст — только внутри залитого блока, подпись снаружи остается темной.
            if (e.Vis.LabelInside) e.Vis.Label.Foreground = on ? Brushes.White : new SolidColorBrush(ColorTools.Darken(color));
        }
    }

    private void AddToSelection(BarEntry e)
    {
        if (_selection.Contains(e)) return;
        _selection.Add(e);
        ApplySelectedVisual(e, on: true);
        _rangeAnchor = e;
    }

    private void SelectOnly(BarEntry e)
    {
        DeselectAll();
        AddToSelection(e);
    }

    // Shift+клик: добавить/снять запись из набора, не трогая остальные.
    private void ToggleAdditive(BarEntry e)
    {
        if (_selection.Remove(e)) { ApplySelectedVisual(e, on: false); return; }
        AddToSelection(e);
    }

    // Ctrl+Shift+клик: выделить все записи между якорем и кликнутой включительно
    // (строки групп и «+N еще» пропускаются — их записей нет в хит-листе).
    private void SelectRange(BarEntry e)
    {
        if (_rangeAnchor is null || !_barEntries.Contains(_rangeAnchor)) { SelectOnly(e); return; }
        var lo = Math.Min(_rangeAnchor.RowIdx, e.RowIdx);
        var hi = Math.Max(_rangeAnchor.RowIdx, e.RowIdx);
        foreach (var b in _barEntries)
            if (b.RowIdx >= lo && b.RowIdx <= hi && !_selection.Contains(b))
            {
                _selection.Add(b);
                ApplySelectedVisual(b, on: true);
            }
    }

    private void DeselectAll()
    {
        foreach (var e in _selection) ApplySelectedVisual(e, on: false);
        _selection.Clear();
    }

    // Общая обработка клика по записи (блок/круг на таймлайне и строка слева).
    private void HandleEntryClick(BarEntry entry, int clickCount, KeyModifiers mods)
    {
        if (clickCount >= 2)
        {
            DeselectAll();
            RecordClicked?.Invoke(this, entry.Ev);
        }
        else if (mods.HasFlag(KeyModifiers.Shift) && mods.HasFlag(KeyModifiers.Control))
            SelectRange(entry);
        else if (mods.HasFlag(KeyModifiers.Shift))
            ToggleAdditive(entry);
        else
            SelectOnly(entry);
    }

    // Контекстное меню по текущему выделению: одна запись — Изменить/Дублировать/
    // Удалить, несколько — Дублировать/Удалить на весь набор (фабрика EventContextMenu).
    private ContextMenu BuildSelectionMenu()
    {
        if (_selection.Count > 1)
        {
            var batch = _selection.Select(b => b.Ev).ToList();
            return EventContextMenu.Multi(
                onDeleteAll:    () => { DeselectAll(); RecordsDeleteRequested?.Invoke(this, batch); },
                onDuplicateAll: () => { DeselectAll(); RecordsDuplicateRequested?.Invoke(this, batch); }, keys: Controls.Components.EventContextMenu.Keys.Calendar, owner: this);
        }
        var ev = _selection[0].Ev;
        return EventContextMenu.Single(
            onEdit:      () => { DeselectAll(); RecordClicked?.Invoke(this, ev); },
            onDelete:    () => { DeselectAll(); RecordDeleteRequested?.Invoke(this, ev); },
            onDuplicate: () => { DeselectAll(); RecordDuplicateRequested?.Invoke(this, ev); }, keys: Controls.Components.EventContextMenu.Keys.Calendar, owner: this);
    }

    // ── геометрия/дата-хелперы ──────────────────────────────────────────────────

    private DateTime RangeEnd => _rangeStart.AddDays(_rangeDays);

    // Последний день, который событие реально занимает (timed-конец ровно в 00:00
    // следующего дня этот день не занимает).
    private static DateTime LastIncludedDay(DateTime start, DateTime end, bool allDay)
        => allDay ? end.Date
         : end.TimeOfDay == TimeSpan.Zero && end > start ? end.Date.AddDays(-1)
         : end.Date;

    // ── длительность по-русски ──────────────────────────────────────────────────

    private static string Plural(int n, string one, string few, string many)
    {
        var n100 = Math.Abs(n) % 100;
        if (n100 is >= 11 and <= 14) return many;
        return (n100 % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }

    private static string RuDuration(DateTime start, DateTime end, bool allDay)
    {
        // 5-дневка: суббота и воскресенье в расчет дней и часов не идут (Пт–Пн
        // «весь день» — 2 дня, Пт 23:00 – Пн 01:00 — 2 часа). Сами полосы и ось
        // дней не меняются — выходные на диаграмме видимы. Охват целиком из
        // выходных считается как есть, иначе подпись была бы пустой.
        var fiveDay = CalendarSettings.FiveDayWeek;
        if (allDay)
        {
            var days = 0;
            if (fiveDay)
                for (var wd = start.Date; wd <= end.Date; wd = wd.AddDays(1))
                    if (!CalendarMath.IsWeekend(wd)) days++;
            if (!fiveDay || days == 0) days = (end.Date - start.Date).Days + 1;
            return days <= 1 ? "24 часа" : $"{days} {Plural(days, "день", "дня", "дней")}";
        }

        var totalMin = fiveDay
            ? CalendarMath.VisibleDurationMinutes(start, end, true)
            : (int)(end - start).TotalMinutes;
        if (totalMin <= 0) return "";
        var d = totalMin / 1440;
        var h = totalMin % 1440 / 60;
        var m = totalMin % 60;
        var parts = new List<string>(3);
        if (d > 0) parts.Add($"{d} {Plural(d, "день", "дня", "дней")}");
        if (h > 0) parts.Add($"{h} {Plural(h, "час", "часа", "часов")}");
        if (m > 0) parts.Add($"{m} {Plural(m, "минута", "минуты", "минут")}");
        return parts.Count == 0 ? "меньше минуты" : string.Join(" ", parts);
    }

    // ── цвета ───────────────────────────────────────────────────────────────────

    private static Color DisplayColor(string? hex) => Color.Parse(EventColors.Display(hex));
}
