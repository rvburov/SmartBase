using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Logging;
using SmartBase.Controls.Behaviors;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;
using SmartBase.Controls.DrawnGrid;

namespace SmartBase.Views.ConstructorDb.DrawnGrid;

/// <summary>
/// Рисованная сетка с полосами прокрутки: <see cref="GridSurface"/> в левом верхнем углу,
/// вертикальная полоса справа, горизонтальная снизу, квадрат на пересечении. Полосы стандартные
/// (тема приложения) и показываются только когда содержимое не помещается — как у нынешней
/// таблицы; протяженности считаются из раскладок колонок и строк: сумма высот 10 млн строк это
/// обычное число, никакой виртуализации полос не нужно.
///
/// Панель владеет моделью таблицы: колонки (порядок, скрытые, ручные ширины), строки (ручные
/// высоты по id записи и их индексы в срезе), оформление, режимы ширины и высоты, отбор и
/// сортировка. <see cref="Load"/> открывает коллекцию по снимку так же, как <c>TableView</c>;
/// снимок может нести оценку числа записей (<see cref="CollectionOpenSnapshot.CountIsEstimate"/>) —
/// тогда точный счет идет в фоне и полоса уточняется по его приходе. <see cref="SetQuery"/> меняет
/// отбор и сортировку: источник строк считается в фоне с отменой (<see cref="RowSourcePlanner"/>).
/// Жесты ширины колонки и высоты строки приходят от поверхности стартом, сдвигом и концом; панель
/// считает модель (<see cref="ColumnResize"/>), перекладывает и сохраняет один раз на отпускание.
/// Навигация нижней панели, сохранение и восстановление смещения — здесь же, из своей раскладки:
/// диапазон видимых строк — арифметика, а не обход дерева.
///
/// Выделение (этап 3): панель держит выделенные ЗАПИСИ (id, в порядке выделения — основа удаления,
/// дубля, «Расположения», «Действий») и выделенные КОЛОНКИ (ключи) и отдает поверхности «выделена
/// ли»; прямоугольник ячеек живет у фасада (<c>CellRange</c> в позициях показанной таблицы), панель
/// лишь передает поверхности его края и прокручивает к ячейке (<see cref="ScrollToCell"/>).
///
/// Правка (этап 4): над поверхностью лежат накладной редактор ячейки (<see cref="Editor"/>) и слой
/// рамки выделения (<see cref="SelectionOverlay"/>) — рамка стоит поверх редактора, как у нынешней
/// таблицы. Указатель и колесо над редактором панель передает поверхности: подсветка ячейки видна
/// сквозь прозрачный редактор, колесо прокручивает таблицу. Правки значений строк списка поднимаются
/// наверх (<see cref="RecordEdited"/>), подмена списка — заранее (<see cref="RowsReplacing"/>), чтобы
/// фасад успел записать набранное в строку, пока она еще живая.
///
/// Операции (этап 5): отбор и сортировка из шапки приходят в <see cref="SetQuery"/> (воронка в шапке —
/// сразу, список — из фона; пока он считается, на экране прежние строки), запомненные за коллекцией —
/// в <see cref="Load"/>. Перезагрузка строк держит прокрутку, смена отбора возвращает ее к началу, как
/// у нынешней таблицы. Ширину «№» панель считает сама по числу записей коллекции: оценке снимка, а
/// потом точному счету из фона — фасад больше не считает записи на UI-потоке. Флайаут фильтра
/// крепится к невидимому якорю на месте кнопки заголовка (<see cref="AnchorAt"/>).
/// Этапы 0–5 плана [drawn-grid.md].
/// </summary>
public sealed class GridPanel : Avalonia.Controls.Grid
{
    private const int    NumColWidth  = 36;
    private const int    PlusColWidth = 36;
    private const double RowHeightMax = 600;

    private readonly ScrollBar _vertical   = new() { Orientation = Orientation.Vertical,   AllowAutoHide = false };
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal, AllowAutoHide = false };
    private readonly Border    _corner     = new();

    private ConstructorRepository?       _repo;
    private int                          _collectionId;
    private CollectionOpenSnapshot?      _snapshot;
    private bool                         _hiddenRecords;
    private int?                         _exactCount;     // точное число записей, когда пришло из фона
    private VirtualRowList?              _rows;
    private List<TableColumnDef>         _defs        = [];
    private Dictionary<string, double>   _widths      = [];   // модель ручных ширин: ключ → DIP; пусто — стандарт
    private HashSet<string>              _hidden      = [];
    private Dictionary<int, RowStyle>    _rowStyles   = [];
    private Dictionary<string, RowStyle> _colStyles   = [];
    private Dictionary<int, int>         _rowHeights  = [];   // ручные высоты: id записи → высота (без линии)
    private Dictionary<int, int>         _rowIndexes  = [];   // id записи с ручной высотой → индекс в срезе
    private bool                         _manualCol;
    private bool                         _manualRow;
    private readonly RelationLinksCache  _relLinks    = new();
    private CancellationTokenSource?     _query;
    private CancellationTokenSource?     _ranks;
    private IReadOnlyDictionary<string, HashSet<string>> _filters = new Dictionary<string, HashSet<string>>();
    private string? _sortKey;
    private bool    _sortAsc = true;
    private bool    _syncingBars;
    private double? _pendingOffsetY;
    private int?    _pendingScrollRecord;   // прокрутка к записи, заказанная, пока список считался в фоне
    private int     _countGeneration;       // отбрасывает точный счет, пришедший к устаревшему открытию
    private TableViewportRange _lastRange = TableViewportRange.Empty;

    // Невидимые якоря всплывающих окон: у рисованной шапки нет контролов, и флайаут фильтра крепится
    // к прямоугольнику на месте кнопки заголовка.
    private readonly Canvas _anchors      = new() { IsHitTestVisible = false };
    private readonly Border _filterAnchor = new();

    // Жест ширины колонки.
    private string? _resizeKey;
    private double  _resizeStartWidth;
    private double  _resizeStartOffsetX;
    private double  _resizeFill;
    private Dictionary<string, double> _resizeBase = [];
    // Жест высоты строки.
    private int     _resizeRow = -1;
    private int     _resizeRecordId;
    private double  _resizeStartHeight;
    private double? _resizeLiveHeight;

    // Выделенные записи (порядок выделения — как SelectedItems нынешней сетки) и колонки.
    private readonly List<int>       _selectedRecords   = [];
    private readonly HashSet<int>    _selectedRecordSet = [];
    private readonly HashSet<string> _selectedColumns   = [];

    /// <summary>Отладочный выключатель для стендов: не трогать полосы прокрутки при сдвиге.</summary>
    public static bool DebugSkipBars { get; set; }

    /// <summary>Отладочный делитель диапазона вертикальной полосы для стендов (1 — пиксели).</summary>
    public static double DebugBarDivisor { get; set; } = 1;

    public GridSurface Surface { get; } = new();

    /// <summary>Накладной редактор ячейки (один на сетку).</summary>
    public EditorHost Editor { get; }

    private readonly SelectionOverlay _overlay;

    /// <summary>Видимый кусок таблицы изменился (прокрутка, ресайз, перезагрузка строк, уточнение счетчика).</summary>
    public event EventHandler<TableViewportRange>? VisibleRangeChanged;

    /// <summary>Нажатие на ячейке, заголовке или кнопке фильтра — для фасада (выделение, меню, флайаут).</summary>
    public event EventHandler<GridPointerEventArgs>? Pressed;

    /// <summary>Клик по строке «+» — создать запись в конце.</summary>
    public event EventHandler? RecordCreateRequested;

    /// <summary>Клик по заголовку «+» — создать свойство.</summary>
    public event EventHandler? PropertyCreateRequested;

    /// <summary>Строки сменились: подключен новый список (открытие, перезагрузка, отбор) или список
    /// перестроился (страница заполнилась, структурное обновление). Фасад по нему догоняет выделение
    /// за своими записью и столбцами (<c>FollowSelectionIdentity</c>).</summary>
    public event EventHandler? RowsChanged;

    /// <summary>Список строк сейчас заменят новым (перезагрузка, отбор): последний момент, когда строки
    /// прежнего списка еще подписаны, — фасад записывает в них открытую правку.</summary>
    public event EventHandler? RowsReplacing;

    /// <summary>Значение записи изменено правкой строки (ячейка, галочка, путь файла) — id записи.</summary>
    public event EventHandler<int>? RecordEdited;

    public GridPanel()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        RowDefinitions    = new RowDefinitions("*,Auto");

        Editor   = new EditorHost(Surface);
        _overlay = new SelectionOverlay(Surface);

        SetColumn(Surface, 0);      SetRow(Surface, 0);
        SetColumn(Editor, 0);       SetRow(Editor, 0);
        SetColumn(_overlay, 0);     SetRow(_overlay, 0);
        SetColumn(_anchors, 0);     SetRow(_anchors, 0);
        SetColumn(_vertical, 1);    SetRow(_vertical, 0);
        SetColumn(_horizontal, 0);  SetRow(_horizontal, 1);
        SetColumn(_corner, 1);      SetRow(_corner, 1);

        _anchors.Children.Add(_filterAnchor);

        Children.Add(Surface);
        Children.Add(Editor);
        Children.Add(_overlay);
        Children.Add(_anchors);
        Children.Add(_vertical);
        Children.Add(_horizontal);
        Children.Add(_corner);

        Surface.ExtentChanged  += (_, _) => { SyncBars(); RefreshVisibleRange(); };
        Surface.OffsetChanged  += (_, _) => { SyncBars(); RefreshVisibleRange(); };
        Surface.SizeChanged    += (_, _) => OnSurfaceSized();
        Surface.MetricsChanged += (_, _) => OnMetricsChanged();
        Surface.Pressed        += OnSurfacePressed;

        Surface.ColumnResizeStarted += OnColumnResizeStarted;
        Surface.ColumnResizeMoved   += OnColumnResizeMoved;
        Surface.ColumnResizeEnded   += OnColumnResizeEnded;
        Surface.RowResizeStarted    += OnRowResizeStarted;
        Surface.RowResizeMoved      += OnRowResizeMoved;
        Surface.RowResizeEnded      += OnRowResizeEnded;

        _vertical.Scroll   += (_, e) => { if (!_syncingBars) Surface.OffsetY = e.NewValue * DebugBarDivisor; };
        _horizontal.Scroll += (_, e) => { if (!_syncingBars) Surface.OffsetX = e.NewValue; };

        Surface.IsRecordSelected = rid => _selectedRecordSet.Contains(rid);
        Surface.IsColumnSelected = key => _selectedColumns.Contains(key);
        Surface.IsColumnFiltered = key => _filters.ContainsKey(key) || _sortKey == key;

        // Редактор лежит над поверхностью и забирает указатель: наведение и колесо — ей. Колесо из
        // раскрытого списка редактора (свое окно-попап) таблицу не крутит.
        Editor.AddHandler(PointerMovedEvent, (_, e) => Surface.HoverAt(e.GetPosition(Surface)),
                          Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        // Редактор открылся под неподвижным указателем: поверхность получила «ушел», движения нет —
        // наведение ставится по входу на редактор.
        Editor.PointerEntered += (_, e) => Surface.HoverAt(e.GetPosition(Surface));
        Editor.PointerExited  += (_, _) => Surface.HoverAt(null);
        Editor.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.Handled || e.Source is not Visual v || v.GetVisualRoot() is Avalonia.Controls.Primitives.PopupRoot) return;
            Surface.ScrollByWheel(e);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);

        AttachedToVisualTree += (_, _) =>
        {
            if (this.TryFindResource("BrushScrollbarTrack", out var b) && b is IBrush brush)
                _corner.Background = brush;
        };
    }

    public IReadOnlyList<TableColumnDef> Columns => _defs;
    public int RowCount => _rows?.Count ?? 0;

    /// <summary>Текущий список строк (для стендов и проверок).</summary>
    internal VirtualRowList? Rows => _rows;

    /// <summary>Кеш связей таблицы — его прогревает список строк и читает поверхность; редактор связи
    /// берет из него стартовый набор.</summary>
    internal RelationLinksCache RelationLinks => _relLinks;

    /// <summary>Последний запуск <see cref="SetQuery"/>; завершен — источник строк подключен или отменен.</summary>
    public Task QueryTask { get; private set; } = Task.CompletedTask;

    /// <summary>Последний расчет индексов строк с ручной высотой (стенды ждут его перед кадром).</summary>
    public Task RanksTask { get; private set; } = Task.CompletedTask;

    public bool IsManualColResize => _manualCol;
    public bool IsManualRowResize => _manualRow;

    /// <summary>Есть ли записи, скрытые в «Отображении записей» (их нет в строках и в списках фильтра).</summary>
    public bool HasHiddenRecords => _hiddenRecords;

    // ── Открытие ──────────────────────────────────────────────────────────────

    /// <summary>Открыть коллекцию по снимку открытия (прочитан заранее, UI-поток в базу не ходит).
    /// <paramref name="defs"/> — готовые определения колонок фасада (с меню и хуками); без них
    /// панель строит их сама, как <c>TableView.BuildColDefs</c>. <paramref name="keepOffsets"/> —
    /// обновление той же коллекции: прокрутку не сбрасывать. <paramref name="filters"/> и
    /// <paramref name="sortKey"/> — отбор и сортировка, запомненные за коллекцией: список считается в
    /// фоне, и пока он идет, на экране прежние строки той же коллекции или пустая таблица — показать
    /// неотобранные строки значило бы на миг показать то, что человек отфильтровал.</summary>
    public void Load(ConstructorRepository repo, int collectionId, CollectionOpenSnapshot snapshot,
                     IReadOnlyList<TableColumnDef>? defs = null, bool keepOffsets = false,
                     IReadOnlyDictionary<string, HashSet<string>>? filters = null, string? sortKey = null, bool sortAsc = true)
    {
        var (colResize, rowResize, _) = snapshot.UiSettings;
        var sameList = keepOffsets && _rows is not null && _repo == repo && _collectionId == collectionId;

        _repo          = repo;
        _collectionId  = collectionId;
        _snapshot      = snapshot;
        _exactCount    = snapshot.CountIsEstimate ? null : snapshot.VisibleRecordCount;
        _manualCol     = colResize;
        _manualRow     = rowResize;
        _defs          = defs?.ToList() ?? BuildDefs(snapshot);
        _hidden        = snapshot.ColumnDisplay.Where(kv => !kv.Value.Visible).Select(kv => kv.Key).ToHashSet();
        _widths        = colResize ? new Dictionary<string, double>(snapshot.ColumnWidths) : [];
        _rowStyles     = snapshot.RowStyles;
        _colStyles     = snapshot.ColumnStyles;
        _rowHeights    = rowResize ? new Dictionary<int, int>(snapshot.RowHeights) : [];
        _hiddenRecords = snapshot.RecordDisplay.Any(kv => !kv.Value.Visible);
        _filters       = filters ?? new Dictionary<string, HashSet<string>>();
        _sortKey       = sortKey;
        _sortAsc       = sortAsc;
        _pendingScrollRecord = null;
        UpdateNumWidth(relayout: false);

        Surface.ColumnGripsEnabled = _manualCol;
        Surface.RowGripsEnabled    = _manualRow;

        _query?.Cancel();
        _query = null;
        RefineCount();
        if (_filters.Count == 0 && _sortKey is null)
        {
            AttachSource(RowSource.Plain, keepOffsets, keepOffsets);
            return;
        }
        if (sameList) Surface.SetColumns(BuildLayout());
        else AttachSource(new IdListRowSource([]), keepX: false, keepY: false);
        StartQuery(keepY: keepOffsets);
    }

    /// <summary>Забыть коллекцию: пустая поверхность, фоновые запросы отменены.</summary>
    public void Clear()
    {
        _query?.Cancel();  _query = null;
        _ranks?.Cancel();  _ranks = null;
        _countGeneration++;
        if (_rows is not null)
        {
            _rows.CollectionChanged -= OnRowsCollectionChanged;
            _rows.RecordEdited      -= OnRecordEdited;
        }
        Editor.Close();
        _repo         = null;
        _collectionId = 0;
        _snapshot     = null;
        _exactCount   = null;
        _rows         = null;
        _defs         = [];
        _widths       = [];
        _rowHeights   = [];
        _rowIndexes   = [];
        _filters      = new Dictionary<string, HashSet<string>>();
        _sortKey      = null;
        _pendingOffsetY      = null;
        _pendingScrollRecord = null;
        _relLinks.Clear();
        _selectedRecords.Clear();
        _selectedRecordSet.Clear();
        _selectedColumns.Clear();
        Surface.ClearCellSelection();
        Surface.Detach();
        SyncBars();
        RefreshVisibleRange();
    }

    // ── Выделение ─────────────────────────────────────────────────────────────

    /// <summary>Выделенные записи в порядке выделения (без строки «+»).</summary>
    public IReadOnlyList<int> SelectedRecordIds => _selectedRecords;

    public bool IsRecordSelected(int recordId) => _selectedRecordSet.Contains(recordId);

    /// <summary>Выделить ровно эти записи, сняв прежнее выделение.</summary>
    public void SetRecordSelection(IEnumerable<int> recordIds)
    {
        _selectedRecords.Clear();
        _selectedRecordSet.Clear();
        foreach (var id in recordIds)
            if (id > 0 && _selectedRecordSet.Add(id)) _selectedRecords.Add(id);
        Surface.RefreshSelectionVisuals();
    }

    /// <summary>Точечно добавить или убрать запись из выделения (Shift и Ctrl по «№»).</summary>
    public void ToggleRecordSelection(int recordId)
    {
        if (recordId <= 0) return;
        if (_selectedRecordSet.Remove(recordId)) _selectedRecords.Remove(recordId);
        else { _selectedRecordSet.Add(recordId); _selectedRecords.Add(recordId); }
        Surface.RefreshSelectionVisuals();
    }

    /// <summary>Снять выделение записей.</summary>
    public void ClearRecordSelection()
    {
        if (_selectedRecords.Count == 0) return;
        _selectedRecords.Clear();
        _selectedRecordSet.Clear();
        Surface.RefreshSelectionVisuals();
    }

    /// <summary>Выделенные колонки — ключи (порядок и правила набора держит фасад).</summary>
    public void SetColumnSelection(IEnumerable<string> keys)
    {
        _selectedColumns.Clear();
        _selectedColumns.UnionWith(keys);
        Surface.RefreshSelectionVisuals();
    }

    /// <summary>Рамка выделения ячеек: строки среза и ключи крайних видимых колонок диапазона.</summary>
    public void SetCellSelection(int top, int bottom, string firstKey, string lastKey, bool tinted) =>
        Surface.SetCellSelection(top, bottom, firstKey, lastKey, tinted);

    public void ClearCellSelection() => Surface.ClearCellSelection();

    /// <summary>На сколько строк листают PageUp/PageDown: сколько строк сейчас в окне.</summary>
    public int PageRows()
    {
        var (first, last) = Surface.VisibleRows();
        return Math.Max(1, last - first);
    }

    /// <summary>Строк с данными в срезе (без строки «+»).</summary>
    public int DataRowCount => Math.Max(0, RowCount - 1);

    /// <summary>Прокрутить так, чтобы ячейка (строка среза, ключ колонки) была в окне целиком —
    /// минимальным сдвигом по обеим осям. Арифметика раскладок: страницу к строке заведет сама
    /// поверхность по новому окну.</summary>
    public void ScrollToCell(int row, string columnKey)
    {
        if (_rows is null || row < 0 || row >= _rows.Count) return;
        var layout = Surface.RowLayout;
        var top    = layout.Top(row);
        var bottom = layout.Bottom(row);
        var view   = Surface.ViewportHeight;
        if (top < Surface.OffsetY) Surface.OffsetY = top;
        else if (bottom > Surface.OffsetY + view) Surface.OffsetY = bottom - view;

        var c = Surface.Columns.IndexOf(columnKey);
        if (c < 0) return;
        var left  = Surface.Columns.Left(c);
        var right = Surface.Columns.Right(c);
        var viewW = Surface.ViewportWidth;
        if (left < Surface.OffsetX) Surface.OffsetX = left;
        else if (right > Surface.OffsetX + viewW) Surface.OffsetX = right - viewW;
    }

    /// <summary>Сдвинуть прокрутку на (dx, dy) пикселей — автопрокрутка при протягивании у края.</summary>
    public void ScrollBy(double dx, double dy)
    {
        if (dx != 0) Surface.OffsetX += dx;
        if (dy != 0) Surface.OffsetY += dy;
    }

    /// <summary>Область строк в координатах поверхности (под шапкой, без полос прокрутки).</summary>
    public Rect BodyRect() => new(0, Surface.HeaderHeight, Surface.ViewportWidth, Surface.ViewportHeight);

    // ── Оформление строк и колонок (меню «Изменить цвет», «Форматирование текста») ──

    public RowStyle RowStyleOf(int recordId)    => _rowStyles.GetValueOrDefault(recordId);
    public RowStyle ColumnStyleOf(string key)   => _colStyles.GetValueOrDefault(key);

    /// <summary>Оформление строк уже записано в базу фасадом — обновить словарь и перерисовать:
    /// оно читается на кадре, строки перечитывать не нужно.</summary>
    public void ApplyRowStyle(IEnumerable<int> recordIds, IReadOnlyList<(StyleAspect Aspect, string? Value)> changes)
    {
        foreach (var id in recordIds)
        {
            var next = WithChanges(RowStyleOf(id), changes);
            if (next.IsEmpty) _rowStyles.Remove(id); else _rowStyles[id] = next;
        }
        Surface.RefreshSelectionVisuals();
    }

    /// <summary>То же для колонок.</summary>
    public void ApplyColumnStyle(IEnumerable<string> keys, IReadOnlyList<(StyleAspect Aspect, string? Value)> changes)
    {
        foreach (var key in keys)
        {
            var next = WithChanges(ColumnStyleOf(key), changes);
            if (next.IsEmpty) _colStyles.Remove(key); else _colStyles[key] = next;
        }
        Surface.RefreshSelectionVisuals();
    }

    private static RowStyle WithChanges(RowStyle style, IReadOnlyList<(StyleAspect Aspect, string? Value)> changes)
    {
        foreach (var (aspect, value) in changes) style = style.With(aspect, value);
        return style;
    }

    private void OnRowsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _rows)) RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRecordEdited(object? sender, int recordId) => RecordEdited?.Invoke(this, recordId);

    /// <summary>Сменить отбор и сортировку: источник строк считается в фоне (<see cref="RowSourcePlanner"/>),
    /// прежний незавершенный запрос отменяется, устаревший результат не подключается. Без фильтров и
    /// сортировки по индексируемому типу подключение мгновенное. Воронка в шапке меняется сразу, строки
    /// до прихода нового списка остаются прежними; новый список показывается с начала, горизонтальная
    /// прокрутка остается — колонки те же.</summary>
    public Task SetQuery(IReadOnlyDictionary<string, HashSet<string>> filters, string? sortKey, bool sortAsc)
    {
        if (_repo is null || _snapshot is null) return Task.CompletedTask;
        _filters = filters;
        _sortKey = sortKey;
        _sortAsc = sortAsc;
        return StartQuery(keepY: false);
    }

    // Список строк еще считается в фоне: _query жив до подключения результата (или отмены новым запросом).
    private bool QueryPending => _query is not null;

    private Task StartQuery(bool keepY)
    {
        Surface.RefreshSelectionVisuals();   // воронка в шапке
        _query?.Cancel();
        var cts = _query = new CancellationTokenSource();
        return QueryTask = ApplyQueryAsync(_filters, _sortKey, _sortAsc, keepY, cts);
    }

    private async Task ApplyQueryAsync(IReadOnlyDictionary<string, HashSet<string>> filters, string? sortKey, bool sortAsc,
                                       bool keepY, CancellationTokenSource cts)
    {
        var ct   = cts.Token;
        var repo = _repo!;
        RowSource source;
        try
        {
            source = await RowSourcePlanner.BuildAsync(repo, _collectionId, _snapshot!.Fields, filters, sortKey, sortAsc,
                                                       _hiddenRecords, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            // База пропала или занята: строки остаются прежними, причина — в журнале и окне ошибки чтения.
            DbErrors.HandleRead(ex, repo.DbPath);
            Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_query, cts)) _query = null; });
            return;
        }

        void Attach()
        {
            if (ct.IsCancellationRequested) return;
            if (ReferenceEquals(_query, cts)) _query = null;
            AttachSource(source, keepX: true, keepY);
        }
        // Продолжение после фона — на UI-поток явно (стенды без контекста синхронизации).
        if (Dispatcher.UIThread.CheckAccess()) Attach();
        else await Dispatcher.UIThread.InvokeAsync(Attach);
    }

    /// <summary>Пересобрать строки: перечитать видимость записей, ручные высоты и оформление строк,
    /// новый список по текущему отбору и сортировке. Колонки и прокрутка не трогаются.</summary>
    public void ReloadRows()
    {
        if (_repo is null || _snapshot is null) return;
        var display    = _repo.GetRecordDisplay(_collectionId);
        _hiddenRecords = display.Any(kv => !kv.Value.Visible);
        _rowHeights    = _manualRow ? _repo.GetRowHeights(_collectionId) : [];
        _rowStyles     = _repo.GetRowStyles(_collectionId);
        // Число записей могло измениться (видимость, создание, удаление): до точного счета из фона
        // список живет последним известным — оно ближе к правде, чем оценка по позициям.
        var lastKnown  = _exactCount ?? _snapshot.VisibleRecordCount;
        _exactCount    = null;
        _snapshot      = _snapshot with { RecordDisplay = display, RowHeights = _rowHeights, RowStyles = _rowStyles,
                                          CountIsEstimate = true, RecordCount = lastKnown, VisibleRecordCount = lastKnown };
        RefineCount();
        StartQuery(keepY: true);
    }

    /// <summary>Структурное обновление строк (создание, удаление, дубль) без пересоздания списка —
    /// теплый кеш, без заглушек; при отборе или сортировке — полная пересборка.
    /// <paramref name="countDelta"/> — на сколько изменилось число записей (известно вызывающему);
    /// null — неизвестно (чужая правка): число остается прежним и уточняется фоновым счетом.</summary>
    public void ReloadRowsStructural(int? countDelta = null)
    {
        if (_repo is null) return;
        // Отбор или сортировка (список пересчитывается целиком) либо новый список еще считается в фоне.
        if (_rows is null || _rows.Source is not PlainRowSource || QueryPending)
        {
            ReloadRows();
            return;
        }
        _countGeneration++;             // счет в полете считал прежний состав
        var estimate = countDelta is null || _rows.CountIsEstimate;
        _rows.RefreshStructural(_rows.DataCount + (countDelta ?? 0), estimate);
        _exactCount = estimate ? null : _rows.DataCount;
        if (estimate) RefineCount();
        UpdateNumWidth();
        ResolveRowIndexes();
    }

    /// <summary>Пересобрать колонки, сохранив строки: поля, порядок, скрытые, оформление и ширины
    /// перечитываются из базы (или берутся готовые определения фасада).</summary>
    public void ReloadColumns(IReadOnlyList<TableColumnDef>? defs = null)
    {
        if (_repo is null || _snapshot is null) return;
        var fresh = _repo.ReadOpenSnapshot(_collectionId, deferCount: true);
        ReloadColumns(defs ?? BuildDefs(fresh), fresh.Fields,
                      fresh.ColumnDisplay.Where(kv => !kv.Value.Visible).Select(kv => kv.Key).ToHashSet(),
                      fresh.ColumnStyles, fresh.ColumnWidths);
    }

    /// <summary>То же по состоянию колонок, уже прочитанному фасадом (он читает его для своих
    /// определений) — база второй раз не спрашивается. Строки и прокрутка остаются.</summary>
    public void ReloadColumns(IReadOnlyList<TableColumnDef> defs, List<DbField> fields, IReadOnlySet<string> hidden,
                              Dictionary<string, RowStyle> columnStyles, IReadOnlyDictionary<string, double> widths)
    {
        if (_repo is null || _snapshot is null) return;
        _snapshot  = _snapshot with { Fields = fields };
        _defs      = defs.ToList();
        _hidden    = hidden.ToHashSet();
        _colStyles = columnStyles;
        _widths    = _manualCol ? new Dictionary<string, double>(widths) : [];
        UpdateNumWidth(relayout: false);
        _rows?.SetFields(fields);
        Surface.SetColumns(BuildLayout());
    }

    /// <summary>Пересобрать раскладку колонок по текущим настройкам (стандартная ширина столбцов из
    /// «Оформления», шрифт «№») — строки и модель ширин не трогаются.</summary>
    public void RefreshLayout()
    {
        if (_rows is null) return;
        UpdateNumWidth(relayout: false);
        Surface.SetColumns(BuildLayout());
    }

    /// <summary>Невидимый якорь на месте прямоугольника поверхности — к нему крепится всплывающее окно
    /// (флайаут фильтра у кнопки заголовка). Раскладывается сразу: окно встает по его границам.</summary>
    public Control AnchorAt(Rect surfaceRect)
    {
        Canvas.SetLeft(_filterAnchor, surfaceRect.X);
        Canvas.SetTop(_filterAnchor, surfaceRect.Y);
        _filterAnchor.Width  = Math.Max(1, surfaceRect.Width);
        _filterAnchor.Height = Math.Max(1, surfaceRect.Height);
        _anchors.UpdateLayout();
        return _filterAnchor;
    }

    // Новый список строк по источнику; число записей — точное, если уже известно, иначе оценка
    // из снимка с уточнением в фоне (список id знает свою длину сам).
    private void AttachSource(RowSource source, bool keepX, bool keepY)
    {
        var snapshot = _snapshot!;
        if (_rows is not null) RowsReplacing?.Invoke(this, EventArgs.Empty);
        _relLinks.Clear();
        _ranks?.Cancel();
        _rowIndexes = [];

        var idList   = source as IdListRowSource;
        var known    = idList?.Ids.Count ?? _exactCount ?? snapshot.VisibleRecordCount;
        var estimate = idList is null && _exactCount is null && snapshot.CountIsEstimate;
        var rows = new VirtualRowList(_repo!, _collectionId, snapshot.Fields,
                                      excludeHidden: _hiddenRecords, relLinks: _relLinks,
                                      knownCount: known, source: source, countIsEstimate: estimate);
        if (_rows is not null)
        {
            _rows.CollectionChanged -= OnRowsCollectionChanged;
            _rows.RecordEdited      -= OnRecordEdited;
        }
        _rows = rows;
        rows.CollectionChanged += OnRowsCollectionChanged;
        rows.RecordEdited      += OnRecordEdited;

        var (x, y) = (Surface.OffsetX, Surface.OffsetY);
        Surface.SetRowLayout(BuildRowLayout());
        Surface.Attach(rows, BuildLayout(),
                       rid => _rowStyles.GetValueOrDefault(rid),
                       key => _colStyles.GetValueOrDefault(key),
                       _relLinks);
        Surface.OffsetX = keepX ? x : 0;
        Surface.OffsetY = keepY ? y : 0;
        SyncBars();
        ApplyPendingOffset();
        RefreshVisibleRange();
        ResolveRowIndexes();
        RowsChanged?.Invoke(this, EventArgs.Empty);
        if (_pendingScrollRecord is int rid && !QueryPending)
        {
            _pendingScrollRecord = null;
            ScrollToRecord(rid);
        }
    }

    // Точный счет записей коллекции в фоне, когда известна только оценка: полоса прокрутки и ширина
    // «№» уточняются по приходе, первый кадр его не ждал. Список id знает свою длину сам — ему счет
    // нужен только для «№» (как у нынешней таблицы, она меряется всеми записями, а не отобранными).
    private void RefineCount()
    {
        if (_exactCount is not null || _repo is null) return;
        var gen    = ++_countGeneration;
        var repo   = _repo;
        var cid    = _collectionId;
        var hidden = _hiddenRecords;
        _ = repo.CountRecordsAsync(cid, hidden).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully)
            {
                if (t.Exception?.GetBaseException() is { } ex) DbErrors.HandleRead(ex, repo.DbPath);
                return;
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (gen != _countGeneration || !ReferenceEquals(_repo, repo) || _collectionId != cid) return;
                _exactCount = t.Result;
                if (_rows is { CountIsEstimate: true } rows) rows.SetCount(t.Result);
                UpdateNumWidth();
                ApplyPendingOffset();
                RefreshVisibleRange();
            });
        }, TaskScheduler.Default);
    }

    // Ширина «№» — под самый длинный номер: число записей коллекции, точное или оценка снимка.
    // Определение «№» у панели свое (у фасада без ширины): меняется только при смене числа разрядов
    // или шрифта, и тогда раскладка пересобирается.
    private void UpdateNumWidth(bool relayout = true)
    {
        var i = _defs.FindIndex(d => d.Key == TableColumnDef.NumKey);
        if (i < 0) return;
        var width = NumColumnWidth(_exactCount ?? _snapshot?.VisibleRecordCount ?? 0);
        var cur   = _defs[i];
        if (cur.FixedWidth == width) return;
        _defs[i] = new TableColumnDef
        {
            Key = cur.Key, Header = cur.Header, IsStarWidth = false, FixedWidth = width,
            CanResize = false, IsReadOnly = true,
            OnHeaderClick = cur.OnHeaderClick, BuildHeaderMenu = cur.BuildHeaderMenu,
            OnCellClick = cur.OnCellClick, BuildCellMenu = cur.BuildCellMenu,
        };
        if (relayout && _rows is not null) Surface.SetColumns(BuildLayout());
    }

    // ── Раскладки ─────────────────────────────────────────────────────────────

    private double Quant() => 1.0 / LineMetrics.EffectiveScale(this);

    private ColumnLayout BuildLayout() =>
        ColumnLayout.Build(_defs, _manualCol ? _widths : new Dictionary<string, double>(), _hidden, Surface.Bounds.Width,
                           ConstructorSettings.ColWidth, Quant());

    // Раскладка строк: стандартный шаг, а в ручном режиме — ручные высоты по индексам, которые уже
    // известны (RowRanks в фоне), плюс живая высота тянущейся строки.
    private RowLayout BuildRowLayout()
    {
        var pitch = Surface.RowPitch;
        if (!_manualRow || _rows is null) return RowLayout.Uniform(pitch);

        var manual = new List<KeyValuePair<int, double>>();
        foreach (var (rid, height) in _rowHeights)
            if (_rowIndexes.TryGetValue(rid, out var index) && !(_resizeLiveHeight is not null && index == _resizeRow))
                manual.Add(new(index, height));
        if (_resizeLiveHeight is double live && _resizeRow >= 0) manual.Add(new(_resizeRow, live));
        return manual.Count == 0 ? RowLayout.Uniform(pitch) : RowLayout.Build(pitch, Surface.LineWidth, manual);
    }

    // Индексы строк с ручной высотой в текущем срезе — в фоне; до ответа строки стандартной высоты.
    private void ResolveRowIndexes()
    {
        _ranks?.Cancel();
        _ranks = null;
        if (!_manualRow || _rowHeights.Count == 0 || _rows is null || _repo is null) return;

        var rows = _rows;
        if (rows.Source is IdListRowSource list)
        {
            var wanted = new HashSet<int>(_rowHeights.Keys);
            var found  = new Dictionary<int, int>();
            for (var i = 0; i < list.Ids.Count && found.Count < wanted.Count; i++)
                if (wanted.Contains(list.Ids[i])) found[list.Ids[i]] = i;
            _rowIndexes = found;
            Surface.SetRowLayout(BuildRowLayout());
            return;
        }

        var cts    = _ranks = new CancellationTokenSource();
        var sorted = rows.Source as SortedRowSource;
        RanksTask = ResolveAsync();

        async Task ResolveAsync()
        {
            Dictionary<int, int> ranks;
            try
            {
                ranks = await _repo.RankRecordsAsync(_collectionId, _rowHeights.Keys.ToList(), _hiddenRecords,
                                                     sorted?.FieldId, sorted?.Asc ?? true, cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { DbErrors.HandleRead(ex, _repo?.DbPath); return; }
            if (cts.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested || !ReferenceEquals(_rows, rows)) return;
                _rowIndexes = ranks;
                Surface.SetRowLayout(BuildRowLayout());
            });
        }
    }

    private void OnSurfaceSized()
    {
        if (_rows is null) return;
        Surface.SetColumns(BuildLayout());
        ApplyPendingOffset();
    }

    // Сменились токены (зум, DPI, шрифт, высота строк): квант и стандартная ширина другие —
    // пересобрать обе раскладки.
    private void OnMetricsChanged()
    {
        if (_rows is null) return;
        UpdateNumWidth(relayout: false);   // «№» меряется шрифтом таблицы
        Surface.SetRowLayout(BuildRowLayout());
        Surface.SetColumns(BuildLayout());
    }

    // ── Режимы ширины и высоты ────────────────────────────────────────────────

    /// <summary>Ручная или стандартная ширина столбцов. Выключение стирает сохраненные ширины;
    /// включение вид не меняет — ширины появятся в модели с первым перетаскиванием.</summary>
    public void SetColResizeMode(bool manual)
    {
        _manualCol = manual;
        if (!manual)
        {
            _widths.Clear();
            _repo?.ClearColumnWidths(_collectionId);
        }
        if (_repo is not null)
        {
            var (_, rowResize, viewMode) = _repo.GetUiSettings(_collectionId);
            _repo.SaveUiSettings(_collectionId, manual, rowResize, viewMode);
        }
        Surface.ColumnGripsEnabled = manual;
        if (_rows is not null) Surface.SetColumns(BuildLayout());
    }

    /// <summary>Ручная или стандартная высота строк. Выключение стирает сохраненные высоты.</summary>
    public void SetRowResizeMode(bool manual)
    {
        _manualRow = manual;
        if (!manual)
        {
            _rowHeights.Clear();
            _rowIndexes = [];
            _repo?.SaveRowHeights(_collectionId, []);
        }
        else if (_repo is not null && _rowHeights.Count == 0)
        {
            _rowHeights = _repo.GetRowHeights(_collectionId);
        }
        if (_repo is not null)
        {
            var (colResize, _, viewMode) = _repo.GetUiSettings(_collectionId);
            _repo.SaveUiSettings(_collectionId, colResize, manual, viewMode);
        }
        Surface.RowGripsEnabled = manual;
        Surface.SetRowLayout(BuildRowLayout());
        ResolveRowIndexes();
    }

    // ── Жест ширины колонки ───────────────────────────────────────────────────

    // Движимые показанные колонки слева направо — те, у кого есть полоска-захват.
    private List<string> MovableKeys()
    {
        var keys = new List<string>();
        var layout = Surface.Columns;
        for (var i = 0; i < layout.Count; i++)
            if (layout[i].IsStarWidth) keys.Add(layout[i].Key);
        return keys;
    }

    private void OnColumnResizeStarted(string key)
    {
        var layout = Surface.Columns;
        if (layout.Count == 0 || Surface.Bounds.Width <= 0) { _resizeKey = null; return; }

        // Зафиксировать в модели то, что человек видит: до первого перетаскивания модель пуста
        // (колонки поровну), и жест должен отталкиваться от текущей раскладки.
        var service = 0.0;
        for (var i = 0; i < layout.Count; i++)
        {
            if (layout[i].IsStarWidth) _widths[layout[i].Key] = layout.Width(i);
            else service += layout.Width(i);
        }
        if (!_widths.TryGetValue(key, out var start)) { _resizeKey = null; return; }

        _resizeKey          = key;
        _resizeStartWidth   = start;
        _resizeStartOffsetX = Surface.OffsetX;
        _resizeFill         = Math.Max(0, Surface.Bounds.Width - service);
        _resizeBase         = new Dictionary<string, double>(_widths);
    }

    private void OnColumnResizeMoved(string key, double dx)
    {
        if (_resizeKey != key) return;
        // Слагаемое с прокруткой компенсирует ее клампинг: когда таблица шире окна и прокрутка сама
        // доезжает до края, без поправки граница отлипает от курсора.
        var scrolled = Surface.OffsetX - _resizeStartOffsetX;
        var raw      = _resizeStartWidth + dx + scrolled;
        var q        = Quant();
        var next     = ColumnResize.Apply(MovableKeys(), _resizeBase, key, raw, _resizeFill);
        foreach (var (k, w) in next) _widths[k] = Math.Max(q, Math.Round(w / q) * q);
        Surface.SetColumns(BuildLayout());
    }

    private void OnColumnResizeEnded(string key, bool commit)
    {
        if (_resizeKey != key) return;
        _resizeKey = null;
        if (commit)
        {
            // Один раз на отпускание мыши — точечным upsert'ом, а не на каждый кадр.
            _repo?.SaveColumnWidths(_collectionId, _widths);
            return;
        }
        // Захват потерян (Alt+Tab, модальное окно поверх): вернуть, что было на старте, без записи.
        _widths = _resizeBase;
        Surface.SetColumns(BuildLayout());
    }

    // ── Жест высоты строки ────────────────────────────────────────────────────

    private void OnRowResizeStarted(int index)
    {
        _resizeRow = -1;
        if (_rows is null || index < 0 || index >= _rows.Count) return;
        var row = _rows[index];
        if (row.IsAddRow || row.IsLoading || row.RecordId <= 0) return;   // заглушка: записи еще нет
        _resizeRow         = index;
        _resizeRecordId    = row.RecordId;
        _resizeStartHeight = Surface.RowLayout.Height(index) - Surface.LineWidth;
        _resizeLiveHeight  = _resizeStartHeight;
    }

    private void OnRowResizeMoved(int index, double dy)
    {
        if (_resizeRow != index) return;
        _resizeLiveHeight = Math.Clamp(_resizeStartHeight + dy, Surface.RowHeight, RowHeightMax);
        Surface.SetRowLayout(BuildRowLayout());
    }

    private void OnRowResizeEnded(int index, bool commit)
    {
        if (_resizeRow != index) return;
        if (commit && _resizeLiveHeight is double h)
        {
            var height = (int)Math.Round(Math.Clamp(h, Surface.RowHeight, RowHeightMax));
            _rowHeights[_resizeRecordId] = height;
            _rowIndexes[_resizeRecordId] = index;
            _repo?.SaveRowHeights(_collectionId, _rowHeights);
        }
        _resizeRow        = -1;
        _resizeLiveHeight = null;
        Surface.SetRowLayout(BuildRowLayout());
    }

    // ── Нажатия ───────────────────────────────────────────────────────────────

    private void OnSurfacePressed(object? sender, GridPointerEventArgs e)
    {
        var props = e.Pointer.GetCurrentPoint(Surface).Properties;
        if (props.IsLeftButtonPressed && e.Hit.Kind == GridHitKind.AddRow)    RecordCreateRequested?.Invoke(this, EventArgs.Empty);
        if (props.IsLeftButtonPressed && e.Hit.Kind == GridHitKind.HeaderAdd) PropertyCreateRequested?.Invoke(this, EventArgs.Empty);
        Pressed?.Invoke(this, e);
    }

    // ── Навигация и смещение ──────────────────────────────────────────────────

    /// <summary>Текущая вертикальная прокрутка в пикселях — для сохранения позиции при пересборке.</summary>
    public double CurrentVerticalOffset() => Surface.OffsetY;

    /// <summary>Вернуть вертикальную прокрутку после пересборки. Протяженность известна сразу
    /// (арифметика раскладки), но до первой раскладки окна и до уточнения оценки числа записей
    /// смещение может не поместиться — тогда оно доезжает при их приходе.</summary>
    public void RestoreVerticalOffset(double offset)
    {
        if (offset <= 0) { _pendingOffsetY = null; return; }
        _pendingOffsetY = offset;
        ApplyPendingOffset();
    }

    private void ApplyPendingOffset()
    {
        if (_pendingOffsetY is not double target) return;
        if (Surface.ViewportHeight <= 0) return;
        Surface.OffsetY = target;
        // Дошли до цели или уперлись в точный край — забываем; оценочный край еще подрастет.
        if (Math.Abs(Surface.OffsetY - target) < 0.5 || _rows is null || !_rows.CountIsEstimate) _pendingOffsetY = null;
    }

    /// <summary>Номера строк в окне и общее число строк среза — из раскладки, арифметикой.</summary>
    public TableViewportRange VisibleRowRange()
    {
        var total = Math.Max(0, (_rows?.Count ?? 0) - 1);   // Count у списка = записи + строка «+»
        if (total == 0) return TableViewportRange.Empty;
        var (first, last) = Surface.VisibleRows();
        return TableViewportRange.FromSlots(first, last, total);
    }

    // Наверх — только на изменение диапазона.
    private void RefreshVisibleRange()
    {
        var range = VisibleRowRange();
        if (range == _lastRange) return;
        _lastRange = range;
        VisibleRangeChanged?.Invoke(this, range);
    }

    private double MaxOffsetY => Math.Max(0, Surface.ExtentHeight - Surface.ViewportHeight);

    /// <summary>Экран назад (кнопка «‹»).</summary>
    public void ScrollRowsPageBack()    => Surface.OffsetY = TableViewportScroll.PageBack(Surface.OffsetY, Surface.ViewportHeight, MaxOffsetY);
    /// <summary>Экран вперед (кнопка «›»).</summary>
    public void ScrollRowsPageForward() => Surface.OffsetY = TableViewportScroll.PageForward(Surface.OffsetY, Surface.ViewportHeight, MaxOffsetY);
    /// <summary>В начало таблицы (кнопка «‹‹»).</summary>
    public void ScrollRowsToStart()     => Surface.OffsetY = TableViewportScroll.ToStart();
    /// <summary>В конец таблицы (кнопка «››»).</summary>
    public void ScrollRowsToEnd()       => Surface.OffsetY = TableViewportScroll.ToEnd(MaxOffsetY);

    /// <summary>Прокрутить к колонке «+» (после добавления свойства).</summary>
    public void ScrollToAddPropertyColumn() => Surface.OffsetX = double.MaxValue;

    /// <summary>Показать запись и строку «+» под ней (после создания записи в конце). Строка в кеше —
    /// сразу; иначе ее индекс в срезе считается в фоне.</summary>
    public void ScrollToRecord(int recordId)
    {
        if (_rows is null || _repo is null) return;
        // Новый список еще считается (запись создана при отборе или сортировке) — прокрутка к ней
        // выполнится, когда он подключится: в прежнем списке ее нет.
        if (QueryPending)
        {
            _pendingScrollRecord = recordId;
            return;
        }
        var rows = _rows;
        if (rows.FindCachedByRecordId(recordId) is { } cached)
        {
            ScrollToIndex(cached.RowNumber - 1);
            return;
        }
        var sorted = rows.Source as SortedRowSource;
        if (rows.Source is IdListRowSource list)
        {
            var i = 0;
            for (; i < list.Ids.Count; i++) if (list.Ids[i] == recordId) break;
            if (i < list.Ids.Count) ScrollToIndex(i);
            return;
        }
        _ = _repo.RankRecordsAsync(_collectionId, [recordId], _hiddenRecords, sorted?.FieldId, sorted?.Asc ?? true)
                 .ContinueWith(t =>
                 {
                     if (t.IsCompletedSuccessfully && t.Result.TryGetValue(recordId, out var index))
                         Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_rows, rows)) ScrollToIndex(index); });
                 });
    }

    // Строка в окне минимальным сдвигом; затем видна и строка «+» (как ScrollIntoView(AddRowItem) у
    // нынешней таблицы — новая запись в конце остается с кнопкой добавления в кадре).
    private void ScrollToIndex(int index)
    {
        if (_rows is null || index < 0 || index >= _rows.Count) return;
        var layout = Surface.RowLayout;
        var top    = layout.Top(index);
        var bottom = layout.Bottom(index);
        var view   = Surface.ViewportHeight;
        if (top < Surface.OffsetY) Surface.OffsetY = top;
        else if (bottom > Surface.OffsetY + view) Surface.OffsetY = bottom - view;
        var addTop = layout.Top(_rows.Count - 1);
        if (addTop + layout.Height(_rows.Count - 1) > Surface.OffsetY + view && top >= addTop - view + layout.Height(index))
            Surface.OffsetY = MaxOffsetY;
    }

    // ── Полосы прокрутки ──────────────────────────────────────────────────────

    private double BarThickness() =>
        this.TryFindResource("ScrollBarThickness", out var v) && v is double d ? d : 14;

    private void SyncBars()
    {
        if (DebugSkipBars) return;
        var t0 = GridTimings.Start();
        _syncingBars = true;
        try
        {
            // Полосы только когда содержимое не помещается — как у нынешней таблицы (Auto). Решение
            // от собственных размеров панели, а не от размера поверхности: иначе показ одной полосы
            // сужал бы окно, требовал вторую, та — первую, и раскладка мигала.
            var bar    = BarThickness();
            var w      = Bounds.Width;
            var h      = Bounds.Height;
            var headH  = Surface.HeaderHeight;
            var extW   = Surface.ExtentWidth;
            var extH   = Surface.ExtentHeight;
            var needV  = extH > h - headH + 0.5;
            var needH  = extW > w - (needV ? bar : 0) + 0.5;
            needV      = extH > h - headH - (needH ? bar : 0) + 0.5;
            _vertical.IsVisible   = needV;
            _horizontal.IsVisible = needH;
            _corner.IsVisible     = needV && needH;

            var maxY = Math.Max(0, extH - Surface.ViewportHeight);
            var maxX = Math.Max(0, extW - Surface.ViewportWidth);

            var d = DebugBarDivisor;
            _vertical.Minimum      = 0;
            _vertical.Maximum      = maxY / d;
            _vertical.ViewportSize = Surface.ViewportHeight / d;
            _vertical.SmallChange  = Surface.RowPitch / d;
            _vertical.LargeChange  = Math.Max(Surface.RowPitch, Surface.ViewportHeight) / d;
            _vertical.Value        = Surface.OffsetY / d;

            _horizontal.Minimum      = 0;
            _horizontal.Maximum      = maxX;
            _horizontal.ViewportSize = Surface.ViewportWidth;
            _horizontal.SmallChange  = 48;
            _horizontal.LargeChange  = Math.Max(48, Surface.ViewportWidth);
            _horizontal.Value        = Surface.OffsetX;
        }
        finally
        {
            _syncingBars = false;
            GridTimings.Stop(t0, "полосы прокрутки (UI)");
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SyncBars();
    }

    // ── Определения колонок ───────────────────────────────────────────────────

    // Колонки — как TableView.BuildColDefs, без меню и обработчиков: «№», движимые в сохраненном
    // порядке (Название = 1000, поля = 3000 + Position), «+». Поля markdown в таблице не показываются.
    private List<TableColumnDef> BuildDefs(CollectionOpenSnapshot s)
    {
        var titleName = s.SystemNames.TryGetValue(TableRowVm.TitleKey, out var n) && !string.IsNullOrWhiteSpace(n)
            ? n
            : "Название";

        var movable = new List<(TableColumnDef Def, int Pos)>
        {
            (new TableColumnDef { Key = TableRowVm.TitleKey, Header = titleName, FieldType = "text",
                                  IsStarWidth = true, MinWidth = ConstructorSettings.ColWidth },
             s.ColumnOrder.GetValueOrDefault(TableRowVm.TitleKey, 1000)),
        };

        foreach (var f in s.Fields)
        {
            if (f.FieldType == "markdown") continue;
            var fid = f.Id!.Value;
            var key = fid.ToString();
            movable.Add((new TableColumnDef
            {
                Key            = key,
                Header         = f.Name,
                FieldType      = f.FieldType,
                IsStarWidth    = true,
                MinWidth       = ConstructorSettings.ColWidth,
                IsReadOnly     = f.FieldType is "checkbox" or "file" || FieldTypeInfo.IsComputed(f.FieldType),
                Separator      = MultiSelectValue.SeparatorFromConfig(f.ConfigJson),
                NumberFormat   = NumberFormatValue.FromConfig(f.ConfigJson),
                FileAutoFolder = FileAutoRule.FromConfig(f.ConfigJson)?.Folder ?? "",
                Relation       = f.FieldType == "relation" ? RelationConfig.FromConfig(f.ConfigJson) : null,
                Tag            = fid,
            }, s.ColumnOrder.GetValueOrDefault(key, 3000 + f.Position)));
        }

        var defs = new List<TableColumnDef>(movable.Count + 2)
        {
            new() { Key = TableColumnDef.NumKey, Header = "№", IsStarWidth = false,
                    FixedWidth = NumColumnWidth(s.VisibleRecordCount), CanResize = false, IsReadOnly = true },
        };
        defs.AddRange(movable.OrderBy(m => m.Pos).Select(m => m.Def));
        defs.Add(new() { Key = TableColumnDef.AddPropKey, Header = "+", IsStarWidth = false,
                         FixedWidth = PlusColWidth, CanResize = false, IsReadOnly = true, MaxWidth = PlusColWidth });
        return defs;
    }

    // Ширина «№»: под самый длинный номер (число записей) с полями по 8 — та же мера, что у
    // NumColumnWidth нынешней таблицы: FormattedText унаследованным шрифтом контрола (не шрифтом
    // надписей — у них он свой), иначе ширина расходится на пиксель.
    private double NumColumnWidth(int recordCount)
    {
        var text     = Math.Max(1, recordCount).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var fontSize = this.TryFindResource("FontSizeBase", out var fs) && fs is double d ? d : 12;
        var family   = Avalonia.Controls.Documents.TextElement.GetFontFamily(this);
        var measured = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                         new Typeface(family), fontSize, null).Width;
        return Math.Max(NumColWidth, Math.Ceiling(measured) + 16);
    }
}
