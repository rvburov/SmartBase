using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Collections;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Styling;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DataBase.Controls.Widgets;
using DataBase.Core.Models;
using DataBase.Database.Repositories;
using DataBase.ViewModels.ConstructorDb;
using DataBase.Utils;

namespace DataBase.Views.ConstructorDb;

public partial class TableView : UserControl
{
    // ── Публичные события ─────────────────────────────────────────────────────
    public event EventHandler<int>? RecordClicked;
    // Удаление записей из таблицы (одной или мультивыделения: Shift/Ctrl-клик → меню
    // «Удалить» или клавиша Delete) — всегда пакетно, с подтверждением; для одной записи
    // список из одного id (как удаление свойств).
    public event EventHandler<IReadOnlyList<int>>? RecordsDeleteRequested;
    public event EventHandler<int>? RecordDuplicateRequested;
    // Open = открывать ли запись справа после создания. «+»-строка создаёт без
    // открытия (false), контекстное «Создать запись» — с открытием (true).
    // AfterId = id записи-инициатора (меню строки) — создать сразу после неё; null —
    // создать в конце («+»-строка и меню пустой области таблицы).
    public event EventHandler<(bool Open, int? AfterId)>? RecordCreateRequested;
    public event EventHandler?      PropertyCreateRequested;
    // Создать свойство сразу справа от колонки (string = ключ колонки-инициатора).
    public event EventHandler<string>? PropertyCreateAfterRequested;
    public event EventHandler<int>? PropertyEditRequested;
    public event EventHandler<int>? PropertyDuplicateRequested;
    // Открыть окно «Отображение» (менеджер видимости/уровней колонок). Окно глобальное
    // (список всех колонок), поэтому без аргумента — инициатор-колонка не важна.
    public event EventHandler? ColumnDisplayRequested;
    // Открыть окно «Отображение записей» (менеджер видимости/уровней строк). Окно
    // глобальное (список всех записей), поэтому без аргумента — инициатор-строка не важна.
    public event EventHandler? RecordDisplayRequested;
    // Удаление пользовательских полей (Delete или меню «Удалить») — всегда пакетно,
    // с подтверждением; для одного поля список из одного id.
    public event EventHandler<IReadOnlyList<int>>? PropertiesDeleteRequested;
    // Переименование системной колонки «Название»/«Описание» (string = ключ колонки).
    public event EventHandler<string>? SystemFieldRenameRequested;

    /// <summary>Срабатывает после построения колонок — родитель может навесить хуки на дефы.</summary>
    public event EventHandler<IReadOnlyList<TableColumnDef>>? ColumnsBuilt;

    // ── Константы геометрии ───────────────────────────────────────────────────
    private const int MinColWidth  = 160;
    private const int NumColWidth  = 36;
    private const int PlusColWidth = 36;

    // ── Состояние ─────────────────────────────────────────────────────────────
    private ConstructorRepository? _repository;
    private int  _collectionId;
    private bool _manualColResize;
    private bool _manualRowResize;
    private bool _snapPending;
    private bool _highlightPending;   // отложенная перестройка рамок выделения
    private List<DbField>        _fields  = [];
    private List<TableColumnDef> _colDefs = [];
    private VirtualRowList?      _virtualRows;
    // Виртуализирующая обёртка над _virtualRows: когда включён флаг AppSettings.LoadVirtualizedGrid,
    // именно она кладётся в ItemsSource — DataGrid берёт её как IDataGridCollectionView без
    // материализации и тянет только видимые строки. null — старый путь (без виртуализации).
    private VirtualCollectionView? _virtualView;
    // Источник по умолчанию для грида (без активного фильтра/сортировки): виртуализирующее
    // представление, если включено, иначе сам постраничный список.
    private System.Collections.IEnumerable? DefaultItemsSource =>
        (System.Collections.IEnumerable?)_virtualView ?? _virtualRows;
    // Пользовательские подписи системных колонок: ключ → отображаемое имя.
    private Dictionary<string, string> _systemNames = [];
    // Сохранённый порядок колонок: ключ → позиция (пусто, если ещё не двигали).
    private Dictionary<string, int> _columnOrder = [];
    // Скрытые колонки (окно «Отображение»): ключ есть в множестве ⇒ колонка скрыта.
    // Колонки из _colDefs не удаляются (порядок/перемещение целы) — гасим через IsVisible.
    private HashSet<string> _hiddenColumns = [];
    // Скрытые записи (окно «Отображение записей»): id есть в множестве ⇒ строка скрыта.
    // Применяется фильтром на уровне SQL (VirtualRowList + LoadAllRows), виртуализация цела.
    private HashSet<int> _hiddenRecords = [];
    // Выделенные по клику в заголовок колонки (голубые рамки). Пусто — нет выделения.
    // Обычный клик — одна колонка; Shift+клик — точечный toggle колонки.
    private readonly List<string> _selectedColumnKeys = [];

    // ── Фильтр/сортировка колонок (Excel-стиль) ───────────────────────────────
    // key колонки → набор разрешённых значений; null-наличие ключа = фильтра нет.
    private readonly Dictionary<string, HashSet<string>> _filters = [];
    // Шевроны шапки: ключ колонки → Path, чтобы подсвечивать активный фильтр.
    private readonly Dictionary<string, Avalonia.Controls.Shapes.Path> _chevronPaths = [];
    private string? _sortKey;
    private bool    _sortAsc = true;

    // ── Ручная высота строк ───────────────────────────────────────────────────
    // recordId → высота строки (px). Применяется в OnLoadingRow при ручном режиме;
    // в стандартном режиме пусто (высоты сбрасываются при выключении режима).
    private Dictionary<int, int> _rowHeights = [];

    private static readonly Cursor RowResizeCursor = new(StandardCursorType.SizeNorthSouth);
    private const double RowGripThickness = 8;     // высота прозрачной полоски-захвата
    private const double RowHeightMax     = 600;
    private bool   _rowHandlesPending;             // отложенная перестройка полосок
    // Состояние перетаскивания нижней границы строки (одно за раз).
    private bool   _isResizingRow;
    private DataGridRow? _resizeRow;
    private int    _resizeRecordId;
    private double _resizeStartPointerY;           // в координатах RowResizeLayer
    private double _resizeStartBottomY;            // нижняя граница строки на старте
    private double _resizeStartHeight;

    public TableView()
    {
        InitializeComponent();
        // Фокусируемый контрол: при Shift/Ctrl-выделении строк уводим фокус сюда из
        // текстовых полей (например, правой панели открытой записи), иначе Delete будет
        // трактоваться как «удалить символ», а не как удаление выделенных записей.
        Focusable = true;
        RecordsGrid.CellPointerPressed += OnCellPointerPressed;
        RecordsGrid.LoadingRow         += OnLoadingRow;
        // Snap дробных Star-ширин до целочисленных весов — без этого DataGrid
        // не рисует вертикальные грид-линии в сабпиксельных позициях. Снап сходится
        // за один шаг (Σ весов = доступная ширина → каждая колонка == вес px).
        RecordsGrid.LayoutUpdated      += OnGridLayoutUpdated;
        // Delete по выделенным колонкам. Tunnel — чтобы перехватить раньше, чем
        // DataGrid обработает клавишу для редактирования/навигации.
        AddHandler(KeyDownEvent, OnTableKeyDown, RoutingStrategies.Tunnel);
    }

    // Окно, на которое навешен обработчик Delete для записей (см. OnTopLevelKeyDown).
    private TopLevel? _keyHookTopLevel;

    // Записи удаляем по Delete на уровне окна, а не только когда фокус внутри таблицы:
    // после выделения строк фокус часто оказывается вне грида (правая панель открытой
    // записи и т.п.), и туннельный OnTableKeyDown на TableView клавишу не получает —
    // именно поэтому Delete «не работал». На уровне окна клавиша приходит всегда.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _keyHookTopLevel = TopLevel.GetTopLevel(this);
        _keyHookTopLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keyHookTopLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _keyHookTopLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        if (!IsVisible) return;                       // таблица не на экране (режим «Список»)
        if (_selectedColumnKeys.Count > 0) return;    // удаление колонок — в OnTableKeyDown
        if (IsTextInputFocused(e)) return;            // печатают в поле — не удаляем записи

        var ids = SelectedRecordIds();
        if (ids.Count == 0) return;

        RecordsDeleteRequested?.Invoke(this, ids);
        e.Handled = true;
    }

    private void OnTableKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete && e.Key != Key.Back) return;

        // Колонки имеют приоритет: их выделение — явный жест по заголовку.
        if (_selectedColumnKeys.Count > 0)
        {
            // Удаляемые = только пользовательские поля (ключ = id поля). Системные
            // (__title__/__desc__) и №/+ не парсятся в int → отсекаются.
            var fieldIds = _selectedColumnKeys
                .Where(k => int.TryParse(k, out _))
                .Select(int.Parse)
                .ToList();
            if (fieldIds.Count == 0) return;

            // Перед показом диалога оставляем рамки только на удаляемых колонках:
            // с системных («Название»/«Описание») обводка снимается — их удаление не коснётся.
            KeepSelectionOnDeletableOnly();
            PropertiesDeleteRequested?.Invoke(this, fieldIds);
            e.Handled = true;
            return;
        }

        // Записи: только Delete (Backspace для строк не используем — слишком легко задеть)
        // и не пока редактируется ячейка (тогда клавиша правит текст, а не удаляет строки).
        if (e.Key != Key.Delete || IsTextInputFocused(e)) return;

        var ids = SelectedRecordIds();
        if (ids.Count == 0) return;

        RecordsDeleteRequested?.Invoke(this, ids);
        e.Handled = true;
    }

    // Выделенные записи (без строки «+») в виде списка их id.
    private List<int> SelectedRecordIds() =>
        RecordsGrid.SelectedItems
            .OfType<TableRowVm>()
            .Where(r => !r.IsAddRow)
            .Select(r => r.RecordId)
            .ToList();

    // Фокус внутри текстового поля ячейки — чтобы Delete правил текст, а не удалял строки.
    private static bool IsTextInputFocused(KeyEventArgs e) =>
        e.Source is TextBox || (e.Source is Visual v && v.FindAncestorOfType<TextBox>() is not null);

    // ── Публичный API ─────────────────────────────────────────────────────────

    // Коллекция, отрисованная в прошлый Load — чтобы понять, сменилась ли она
    // (тогда горизонтальный скролл сбрасываем) или это refresh той же (скролл храним).
    private int? _lastLoadedCollectionId;

    public void Load(ConstructorRepository repository, int collectionId)
    {
        var sameCollection = _lastLoadedCollectionId == collectionId;

        _repository   = repository;
        _collectionId = collectionId;

        // Фильтры/сортировка не переносятся между коллекциями.
        _filters.Clear();
        _sortKey = null;

        var (colResize, rowResize, _) = repository.GetUiSettings(collectionId);
        _manualColResize = colResize;
        _manualRowResize = rowResize;

        // При refresh той же коллекции (создание/изменение свойства) сохраняем
        // горизонтальную прокрутку — иначе при 50 колонках откидывает в начало.
        // Смена коллекции — скролл сбрасываем (новая таблица начинается слева).
        if (sameCollection)
            ReloadPreservingScroll();
        else
            Reload();

        _lastLoadedCollectionId = collectionId;
    }

    // Avalonia DataGrid НЕ использует ScrollViewer для горизонтальной прокрутки —
    // offset живёт во внутреннем поле и доступен через internal-свойство
    // HorizontalOffset и метод UpdateHorizontalOffset(double). При Columns.Clear()
    // DataGrid сам сбрасывает offset в 0 (OnClearingColumns), поэтому после пересборки
    // его надо вернуть. Доступ — через рефлексию (версия Avalonia зафиксирована 11.2.x).
    private static readonly PropertyInfo? HOffsetProp =
        typeof(DataGrid).GetProperty("HorizontalOffset",
            BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? UpdateHOffsetMethod =
        typeof(DataGrid).GetMethod("UpdateHorizontalOffset",
            BindingFlags.Instance | BindingFlags.NonPublic);

    // Вертикальная прокрутка (строки) виртуализирована: чистого UpdateVerticalOffset
    // нет. Абсолютная прокрутка идёт через _vScrollBar.Value + ProcessVerticalScroll
    // (ветка ThumbTrack инвалидирует measure). Доступ — рефлексией (Avalonia 11.2.x).
    private static readonly PropertyInfo? VScrollBarProp =
        typeof(DataGrid).GetProperty("VerticalScrollBar",
            BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? ProcessVScrollMethod =
        typeof(DataGrid).GetMethod("ProcessVerticalScroll",
            BindingFlags.Instance | BindingFlags.NonPublic);

    // Текущая вертикальная прокрутка (0, если API недоступен). Для сохранения позиции
    // при создании записи в середине таблицы.
    public double CurrentVerticalOffset() =>
        (VScrollBarProp?.GetValue(RecordsGrid) as ScrollBar)?.Value ?? 0;

    // Восстанавливает вертикальную прокрутку после пересборки: высота контента готова
    // не сразу, поэтому ждём через LayoutUpdated, пока Maximum дорастёт до offset,
    // затем выставляем Value и применяем через ProcessVerticalScroll (ThumbTrack).
    public void RestoreVerticalOffset(double offset)
    {
        if (offset <= 0 || VScrollBarProp is null || ProcessVScrollMethod is null) return;

        var attempts = 0;

        void Handler(object? _, EventArgs __)
        {
            attempts++;
            if (VScrollBarProp.GetValue(RecordsGrid) is not ScrollBar sb)
            {
                if (attempts > 20) RecordsGrid.LayoutUpdated -= Handler;
                return;
            }

            // Контент ещё не дорос до нужного offset — ждём следующий layout-проход.
            if (sb.Maximum < offset && attempts <= 20) return;

            sb.Value = Math.Min(offset, sb.Maximum);
            ProcessVScrollMethod.Invoke(RecordsGrid, [ScrollEventType.ThumbTrack]);
            RecordsGrid.LayoutUpdated -= Handler;
        }

        RecordsGrid.LayoutUpdated += Handler;
    }

    // Текущий горизонтальный offset DataGrid (0, если API недоступен).
    private double CurrentScrollOffset() =>
        HOffsetProp?.GetValue(RecordsGrid) is double x ? x : 0;

    // Ожидающий выставитель скролла — чтобы при быстрых повторных пересборках
    // не копились хендлеры со старыми offset'ами (новый отменяет предыдущий).
    private EventHandler? _scrollRestoreHandler;

    // Восстанавливает горизонтальную прокрутку на точную позицию после пересборки.
    private void RestoreScrollOffset(double offsetX) => ApplyHorizontalOffsetDeferred(offsetX);

    // Прокручивает таблицу к правому краю — к колонке «+» (добавление свойства).
    // Используется после добавления нового свойства через правый «+»: новая колонка
    // и кнопка «+» остаются видны (аналог ScrollToRecord, удерживающего add-row «+»).
    public void ScrollToAddPropertyColumn() => ApplyHorizontalOffsetDeferred(double.MaxValue);

    // Откладывает выставление горизонтального offset до тех пор, пока star-ширины
    // колонок не осядут (несколько layout-проходов). offsetX — желаемая позиция;
    // для прокрутки к правому краю передаём double.MaxValue (setter клампит до реального
    // максимума, останавливаемся, когда offset перестаёт расти). Отписываемся, когда
    // цель достигнута, offset вышел на плато (упёрлись в максимум) или исчерпали попытки.
    private void ApplyHorizontalOffsetDeferred(double offsetX)
    {
        if (HOffsetProp is null || UpdateHOffsetMethod is null) return;

        // Отменяем предыдущее незавершённое восстановление (его offset устарел).
        if (_scrollRestoreHandler is not null)
            RecordsGrid.LayoutUpdated -= _scrollRestoreHandler;

        if (offsetX <= 0) { _scrollRestoreHandler = null; return; }

        var attempts   = 0;
        var lastActual = -1.0;
        var plateau    = 0;
        var grew       = false;

        void Handler(object? _, EventArgs __)
        {
            attempts++;

            // Setter клампит до (VisibleEdgedColumnsWidth - CellsWidth); пока ширины
            // не дорастут, offset «осядет» ниже цели — пробуем на следующем проходе.
            UpdateHOffsetMethod.Invoke(RecordsGrid, [offsetX]);
            var actual = HOffsetProp.GetValue(RecordsGrid) is double a ? a : 0;

            if (actual > 0.5) grew = true;

            // Плато: offset перестал расти между проходами. Учитываем только после того,
            // как он реально вырос (grew) — иначе нулевой offset на ранних проходах (ширины
            // ещё не готовы) приняли бы за «упёрлись в максимум» и остановились бы на нуле.
            if (Math.Abs(actual - lastActual) < 0.5) plateau++;
            else plateau = 0;
            lastActual = actual;

            if (actual >= offsetX - 0.5 || (grew && plateau >= 2) || attempts > 30)
            {
                RecordsGrid.LayoutUpdated -= _scrollRestoreHandler!;
                _scrollRestoreHandler = null;
            }
        }

        _scrollRestoreHandler = Handler;
        RecordsGrid.LayoutUpdated += Handler;
    }

    public void Clear()
    {
        _repository   = null;
        _collectionId = 0;
        _lastLoadedCollectionId = null;
        _fields       = [];
        _colDefs      = [];
        _systemNames  = [];
        _columnOrder  = [];
        _virtualRows  = null;
        _virtualView  = null;
        _filters.Clear();
        _chevronPaths.Clear();
        _sortKey = null;
        _rowHeights = [];
        _isResizingRow = false;
        _resizeRow = null;
        _lastRowGrips.Clear();
        RowResizeLayer.Children.Clear();
        RecordsGrid.ItemsSource = null;
        RecordsGrid.Columns.Clear();
    }

    public void SetColResizeMode(bool manual)
    {
        _manualColResize = manual;
        RecordsGrid.CanUserResizeColumns = manual;
        if (!manual)
            _repository?.SaveColWidths(_collectionId, []);

        // Запоминаем режим, чтобы он сохранялся между сессиями (раньше управлялся
        // из футера без персиста; теперь — из меню заголовка).
        if (_repository is not null)
        {
            var (_, rowResize, viewMode) = _repository.GetUiSettings(_collectionId);
            _repository.SaveUiSettings(_collectionId, manual, rowResize, viewMode);
        }

        // Пересобирать колонки нельзя — DataGrid теряет ресайз-грипперы. Вместо
        // этого переключаем IsVisible у filler-колонки и пересчитываем ширины.
        ApplyColumnWidths();
    }

    // Текущий режим ширины столбцов (для отметки активного пункта в меню).
    public bool IsManualColResize => _manualColResize;

    // Переключение режима высоты строк (вызывается из меню записи). Запоминаем режим
    // между сессиями — как и режим ширины столбцов (раньше управлялся из футера).
    public void SetRowResizeMode(bool manual)
    {
        _manualRowResize = manual;

        // Выключение ручного режима сбрасывает все сохранённые высоты на стандарт
        // (как сброс ширин при переходе на стандартную ширину столбцов).
        if (!manual)
        {
            _rowHeights.Clear();
            _repository?.SaveRowHeights(_collectionId, []);
        }

        if (_repository is not null)
        {
            var (colResize, _, viewMode) = _repository.GetUiSettings(_collectionId);
            _repository.SaveUiSettings(_collectionId, colResize, manual, viewMode);
        }

        // Перестраиваем строки, чтобы применить/сбросить высоты; держим прокрутку.
        var keepV = CurrentVerticalOffset();
        ReloadPreservingScroll();
        RestoreVerticalOffset(keepV);
    }

    // Текущий режим высоты строк (для отметки активного пункта в меню).
    public bool IsManualRowResize => _manualRowResize;

    public void ScrollToRecord(int recordId)
    {
        if (_virtualRows is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            var row = _virtualRows.FindCachedByRecordId(recordId);
            if (row is not null)
                RecordsGrid.ScrollIntoView(row, RecordsGrid.Columns.FirstOrDefault());
            RecordsGrid.ScrollIntoView(_virtualRows.AddRowItem, RecordsGrid.Columns.FirstOrDefault());
        }, DispatcherPriority.Background);
    }

    // ── Построение таблицы ────────────────────────────────────────────────────

    private void Reload()
    {
        if (_repository is null) return;

        _fields = _repository.GetFields(_collectionId)
            .Where(f => f.FieldType != "markdown")
            .ToList();

        _systemNames = _repository.GetSystemFieldNames(_collectionId);
        _columnOrder = _repository.GetColumnOrder(_collectionId);
        _hiddenColumns = _repository.GetColumnDisplay(_collectionId)
            .Where(kv => !kv.Value.Visible)
            .Select(kv => kv.Key)
            .ToHashSet();
        _hiddenRecords = _repository.GetRecordDisplay(_collectionId)
            .Where(kv => !kv.Value.Visible)
            .Select(kv => kv.Key)
            .ToHashSet();
        _rowHeights = _repository.GetRowHeights(_collectionId);

        ClearColumnSelection();   // колонки пересобираются — снимаем выделение

        _colDefs = BuildColDefs();
        ColumnsBuilt?.Invoke(this, _colDefs);

        _virtualRows = new VirtualRowList(_repository, _collectionId, _fields,
                                          excludeHidden: _hiddenRecords.Count > 0);
        // Виртуализирующая обёртка (если флаг включён): DataGrid не материализует источник,
        // тянет только видимые строки → большие коллекции открываются мгновенно.
        _virtualView = AppSettings.LoadVirtualizedGrid() ? new VirtualCollectionView(_virtualRows) : null;

        BuildColumns();

        RecordsGrid.ItemsSource = DefaultItemsSource;
        RecordsGrid.ContextMenu = BuildGridContextMenu();

        ApplyColumnWidths();
        ApplyFilters();   // переустановит ItemsSource, если фильтр/сортировка активны
        ScheduleRowHandlesRefresh();   // полоски ресайза строк (если ручной режим)
    }

    private List<TableColumnDef> BuildColDefs()
    {
        // Движимые колонки: Title, Desc и пользовательские поля. Порядок — по
        // эффективной позиции (из column_order, иначе дефолт). № и «+» закреплены.
        var movable = new List<TableColumnDef>
        {
            new() { Key = TableRowVm.TitleKey, Header = SystemHeader(TableRowVm.TitleKey),
                    FieldType = "text",  IsStarWidth = true, MinWidth = MinColWidth,
                    BuildHeaderMenu = _ => BuildSystemHeaderContextMenu(TableRowVm.TitleKey) },

            new() { Key = TableRowVm.DescKey,  Header = SystemHeader(TableRowVm.DescKey),
                    FieldType = "text",  IsStarWidth = true, MinWidth = MinColWidth,
                    BuildHeaderMenu = _ => BuildSystemHeaderContextMenu(TableRowVm.DescKey) },
        };

        foreach (var f in _fields)
        {
            var fid = f.Id!.Value;
            movable.Add(new TableColumnDef
            {
                Key         = fid.ToString(),
                Header      = f.Name,
                FieldType   = f.FieldType,
                IsStarWidth = true,
                MinWidth    = MinColWidth,
                IsReadOnly  = f.FieldType == "checkbox",
                Options     = ParseSelectOptions(f),
                Tag         = fid,
                BuildHeaderMenu = _ => BuildHeaderContextMenu(fid, f.IsRequired, f.Name, f.FieldType),
            });
        }

        // Стабильная сортировка по эффективной позиции (OrderBy сохраняет порядок
        // равных — важно, если у пары колонок совпали ключи дефолта).
        var ordered = movable.OrderBy(EffectiveColumnPosition).ToList();

        var defs = new List<TableColumnDef>
        {
            new() { Key = TableColumnDef.NumKey, Header = "№",
                    IsStarWidth = false, FixedWidth = NumColWidth,
                    CanResize = false,   IsReadOnly = true },
        };
        defs.AddRange(ordered);
        defs.Add(new() { Key = TableColumnDef.AddPropKey, Header = "+",
                         IsStarWidth = false, FixedWidth = PlusColWidth,
                         CanResize = false,   IsReadOnly = true,
                         MaxWidth  = PlusColWidth });

        return defs;
    }

    // Эффективная позиция движимой колонки: сохранённая (column_order) или дефолт.
    // Дефолт: Title=1000, Desc=2000, пользовательские = 3000 + DbFields.Position.
    private int EffectiveColumnPosition(TableColumnDef def)
    {
        if (_columnOrder.TryGetValue(def.Key, out var pos)) return pos;
        if (def.Key == TableRowVm.TitleKey) return 1000;
        if (def.Key == TableRowVm.DescKey)  return 2000;
        if (int.TryParse(def.Key, out var fid))
        {
            var f = _fields.FirstOrDefault(x => x.Id == fid);
            if (f is not null) return 3000 + f.Position;
        }
        return int.MaxValue;
    }

    // Текущий порядок ключей движимых колонок (без № и «+») — для MoveColumn и
    // вставки нового свойства справа от колонки (контроллер).
    public List<string> MovableKeysInOrder() =>
        _colDefs
            .Where(d => d.Key != TableColumnDef.NumKey && d.Key != TableColumnDef.AddPropKey)
            .Select(d => d.Key)
            .ToList();

    // Движимые колонки (ключ + отображаемое имя) в порядке слева-направо — для окна
    // «Отображение». Включает и скрытые колонки (их там можно вернуть), № и «+» — нет.
    public IReadOnlyList<(string Key, string Name)> MovableColumnsInOrder() =>
        _colDefs
            .Where(d => d.Key != TableColumnDef.NumKey && d.Key != TableColumnDef.AddPropKey)
            .Select(d => (d.Key, d.Header))
            .ToList();

    // Перемещение колонки влево/вправо: пишем порядок в БД, перестраиваем таблицу.
    // Reload сохраняет активные фильтры/сортировку; горизонтальную прокрутку держим
    // на месте — иначе таблицу откидывает в начало при каждом сдвиге.
    private void MoveColumn(string key, bool left)
    {
        if (_repository is null) return;
        _repository.MoveColumn(_collectionId, key, left, MovableKeysInOrder());
        ReloadPreservingScroll();
    }

    // Reload + восстановление горизонтального скролла. Используется операциями,
    // перестраивающими колонки на месте (сдвиг колонки), чтобы таблица не прыгала
    // в начало. Сам Reload пересобирает DataGrid, поэтому offset снимаем до него.
    private void ReloadPreservingScroll()
    {
        var keepOffset = CurrentScrollOffset();
        Reload();
        if (keepOffset > 0)
            RestoreScrollOffset(keepOffset);
    }

    // Можно ли двигать колонку влево/вправо (не крайняя ли она среди движимых).
    private bool CanMoveColumn(string key, bool left)
    {
        var keys = MovableKeysInOrder();
        var idx  = keys.IndexOf(key);
        if (idx < 0) return false;
        return left ? idx > 0 : idx < keys.Count - 1;
    }

    // Перемещение записи вверх/вниз: пишем порядок в БД, перестраиваем таблицу.
    // Вертикальную и горизонтальную прокрутку держим на месте — иначе таблицу
    // откидывает при каждом сдвиге, и сдвигаемая строка уезжает из зоны видимости.
    private void MoveRecord(int recordId, bool up)
    {
        if (_repository is null) return;

        var keepV = CurrentVerticalOffset();
        var keepH = CurrentScrollOffset();

        _repository.MoveRecord(_collectionId, recordId, up);
        Reload();

        if (keepH > 0) RestoreScrollOffset(keepH);
        RestoreVerticalOffset(keepV);
    }

    // Можно ли двигать запись вверх/вниз (не крайняя ли она в порядке коллекции).
    private bool CanMoveRecord(int recordId, bool up)
    {
        if (_repository is null) return false;
        var idx = _repository.GetRecordPosition(_collectionId, recordId);   // 0-based
        if (idx < 0) return false;
        return up ? idx > 0 : idx < _repository.CountRecords(_collectionId) - 1;
    }

    private void BuildColumns()
    {
        _chevronPaths.Clear();
        RecordsGrid.Columns.Clear();
        RecordsGrid.CanUserResizeColumns = _manualColResize;

        foreach (var def in _colDefs)
            RecordsGrid.Columns.Add(CreateDataGridColumn(def));
    }

    private DataGridColumn CreateDataGridColumn(TableColumnDef def)
    {
        var width = def.IsStarWidth
            ? new DataGridLength(1, DataGridLengthUnitType.Star)
            : new DataGridLength(def.FixedWidth);

        var col = new DataGridTemplateColumn
        {
            Header         = BuildHeaderCell(def),
            Tag            = def.Tag ?? def.Key,
            Width          = width,
            MinWidth       = def.IsStarWidth ? def.MinWidth : def.FixedWidth,
            IsReadOnly     = def.IsReadOnly,
            CanUserSort    = false,
            CanUserResize  = def.CanResize,
            CanUserReorder = false,
            CellTemplate   = BuildCellTemplate(def),
        };

        if (def.MaxWidth.HasValue)
            col.MaxWidth = def.MaxWidth.Value;

        if (!def.IsReadOnly)
            col.CellEditingTemplate = BuildEditTemplate(def);

        // Скрытая в окне «Отображение» колонка — гасим, не удаляя из _colDefs
        // (порядок/перемещение/фильтры продолжают работать с полным набором).
        col.IsVisible = !_hiddenColumns.Contains(def.Key);

        return col;
    }

    // ── Ячейки шапки (передаются как Header в DataGridTemplateColumn) ──────────

    private Control BuildHeaderCell(TableColumnDef def)
    {
        if (def.Key == TableColumnDef.AddPropKey)
        {
            var plus = MakeCenteredHeader("+", fontSize: 16, addRightBorder: false);
            plus.Cursor = new Cursor(StandardCursorType.Hand);
            plus.PointerPressed += (_, _) =>
                PropertyCreateRequested?.Invoke(this, EventArgs.Empty);
            return plus;
        }

        if (def.Key == TableColumnDef.NumKey)
            return MakeCenteredHeader("№");

        var cell = MakeTextHeader(def);

        if (def.OnHeaderClick is { } clickHandler)
        {
            cell.Cursor = new Cursor(StandardCursorType.Hand);
            cell.PointerPressed += (_, _) => clickHandler(def);
        }

        // Левый клик по заголовку — выделить колонку голубой рамкой. С Shift —
        // диапазон от последней обычной выделенной колонки.
        cell.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed)
                SelectColumn(def.Key, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        };

        if (def.BuildHeaderMenu is { } menuFactory)
            cell.ContextMenu = menuFactory(def);

        return cell;
    }

    private Border MakeCenteredHeader(string text, double fontSize = 0, bool addRightBorder = true) => new()
    {
        Background          = Brushes.Transparent,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment   = VerticalAlignment.Stretch,
        BorderBrush         = (IBrush)this.FindResource("BrushSeparator")!,
        BorderThickness     = new Thickness(0, 0, addRightBorder ? 1 : 0, 0),
        Child = new TextBlock
        {
            Text                = text,
            FontSize            = fontSize > 0 ? fontSize : (double)this.FindResource("FontSizeBase")!,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Foreground          = (IBrush)this.FindResource("BrushSecondaryText")!,
        },
    };

    private Border MakeTextHeader(TableColumnDef def)
    {
        var text = new TextBlock
        {
            Text              = def.Header,
            FontSize          = (double)this.FindResource("FontSizeBase")!,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Foreground        = (IBrush)this.FindResource("BrushSecondaryText")!,
        };

        // Слева — иконка типа свойства (если тип известен). Сетка: [иконка][текст][шеврон].
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var typeIcon = BuildTypeIcon(def.FieldType);
        if (typeIcon is not null)
        {
            Grid.SetColumn(typeIcon, 0);
            grid.Children.Add(typeIcon);
        }

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var chevron = BuildFilterChevron(def);
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);

        var border = new Border
        {
            Background          = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
            Padding             = new Thickness(6, 0),
            BorderBrush         = (IBrush)this.FindResource("BrushSeparator")!,
            BorderThickness     = new Thickness(0, 0, 1, 0),
            Child               = grid,
        };
        border.Classes.Add("colHeader");   // hover-фон через стиль (Assets/Styles/Controls.axaml)
        return border;
    }

    // Иконка типа свойства слева от названия колонки (серый цвет шапки). null —
    // если тип неизвестен (тогда колонка без иконки).
    private Control? BuildTypeIcon(string? fieldType)
    {
        var geo = Controls.Components.FieldTypeIcons.ForType(fieldType);
        if (geo is null) return null;

        return new Viewbox
        {
            Width             = 16,
            Height            = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 6, 0),
            Child = new Canvas
            {
                Width  = 24,
                Height = 24,
                Children =
                {
                    new Avalonia.Controls.Shapes.Path
                    {
                        Data            = geo,
                        Stroke          = (IBrush)this.FindResource("BrushSecondaryText")!,
                        StrokeThickness = 1.8,
                        StrokeLineCap   = PenLineCap.Round,
                        StrokeJoin      = PenLineJoin.Round,
                    },
                },
            },
        };
    }

    /// <summary>Стрелка в шапке колонки — открывает окно фильтра/сортировки.</summary>
    private Control BuildFilterChevron(TableColumnDef def)
    {
        // Обводочная иконка (как в полях даты/навбаре), не PathIcon-заливка.
        // Спокойное состояние — серый шеврон; активный фильтр/сортировка —
        // синяя воронка (lucide funnel), см. ApplyChevronState.
        var path = new Avalonia.Controls.Shapes.Path
        {
            StrokeThickness = 2,
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        };
        _chevronPaths[def.Key] = path;
        ApplyChevronState(def.Key, path);

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);

        var btn = new Button
        {
            Width             = 20,
            Height            = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Content           = new Viewbox { Width = 12, Height = 12, Child = canvas },
        };
        btn.Classes.Add("icon");
        btn.Classes.Add("filterChevron");
        ToolTip.SetTip(btn, "Фильтр и сортировка");
        btn.Click += (_, _) => ShowFilterFlyout(btn, def);
        return btn;
    }

    // lucide chevron-down, funnel, arrow-down-a-z и arrow-up-z-a в сетке 24×24.
    private static readonly Geometry ChevronIcon = Geometry.Parse("M6 9 L12 15 L18 9");
    private static readonly Geometry FunnelIcon  = Geometry.Parse(
        "M10 20a1 1 0 0 0 .553.895l2 1A1 1 0 0 0 14 21v-7a2 2 0 0 1 .517-1.341L21.74 4.67A1 1 0 0 0 21 3H3a1 1 0 0 0-.742 1.67l7.225 7.989A2 2 0 0 1 10 14z");
    private static readonly Geometry SortAzIcon  = Geometry.Parse(
        "M3 16 l4 4 4 -4 M7 20 V4 M20 8 h-5 M15 10 V6.5 a2.5 2.5 0 0 1 5 0 V10 M15 14 h5 l-5 6 h5");
    private static readonly Geometry SortZaIcon  = Geometry.Parse(
        "M3 8 l4 -4 4 4 M7 4 v16 M15 4 h5 l-5 6 h5 M15 20 v-3.5 a2.5 2.5 0 0 1 5 0 V20 M20 18 h-5");
    private static readonly Geometry CheckIcon   = Geometry.Parse("M20 6 L9 17 l-5 -5");
    private static readonly Geometry XIcon       = Geometry.Parse("M18 6 L6 18 M6 6 L18 18");

    private void ApplyChevronState(string key, Avalonia.Controls.Shapes.Path path)
    {
        var active  = _filters.ContainsKey(key) || _sortKey == key;
        path.Data   = active ? FunnelIcon : ChevronIcon;
        path.Stroke = active
            ? (IBrush)this.FindResource("BrushAccent")!
            : (IBrush)this.FindResource("BrushSecondaryText")!;
    }

    private void UpdateChevronStates()
    {
        foreach (var (key, path) in _chevronPaths)
            ApplyChevronState(key, path);
    }

    // ── Шаблоны ячеек ─────────────────────────────────────────────────────────

    private IDataTemplate BuildCellTemplate(TableColumnDef def) =>
        new FuncDataTemplate<TableRowVm>((row, _) =>
        {
            if (def.Key == TableColumnDef.NumKey)
                return new Border
                {
                    Background          = (IBrush)this.FindResource("BrushInactiveBg")!,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment   = VerticalAlignment.Stretch,
                    Child = new TextBlock
                    {
                        Text                = row.IsAddRow ? "+" : row.RowNumber.ToString(),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Foreground          = (IBrush)this.FindResource("BrushSecondaryText")!,
                    },
                };

            if (def.Key == TableColumnDef.AddPropKey)
                return new Border
                {
                    Background          = (IBrush)this.FindResource("BrushInactiveBg")!,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment   = VerticalAlignment.Stretch,
                };

            if (row.IsAddRow) return new Border();

            if (def.FieldType == "checkbox")
            {
                var cb = new CheckBox
                {
                    IsChecked           = IsTruthy(row[def.Key]),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                };
                cb.IsCheckedChanged += (_, _) => row[def.Key] = cb.IsChecked == true ? "1" : "0";
                return cb;
            }

            var binding = new Binding($"[{def.Key}]")
            {
                Converter = def.Key == TableRowVm.TitleKey
                    ? EmptyTitleConverter.Instance
                    : new FieldDisplayConverter(def.FieldType),
            };

            // Числа выравниваем вправо (для этого TextBlock должен занять всю ширину ячейки).
            var isNumber = def.FieldType == "number";
            return new TextBlock
            {
                Margin              = new Thickness(6, 0, 6, 0),
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = isNumber ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
                TextAlignment       = isNumber ? TextAlignment.Right : TextAlignment.Left,
                TextTrimming        = TextTrimming.CharacterEllipsis,
                [!TextBlock.TextProperty] = binding,
            };
        });

    private IDataTemplate BuildEditTemplate(TableColumnDef def) =>
        new FuncDataTemplate<TableRowVm>((row, _) =>
        {
            if (row.IsAddRow) return new Border();

            switch (def.FieldType)
            {
                case "date":
                {
                    var editor = new DateEditor
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Storage           = row[def.Key],
                    };
                    editor.Changed += (_, _) => row[def.Key] = editor.Storage ?? "";
                    return editor;
                }

                case "select":
                {
                    var combo = new ComboBox
                    {
                        BorderThickness     = new Thickness(0),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        ItemsSource         = def.Options,
                        SelectedItem        = row[def.Key],
                    };
                    combo.SelectionChanged += (_, _) =>
                        row[def.Key] = combo.SelectedItem?.ToString() ?? "";
                    return combo;
                }

                case "number":
                {
                    var tb = new TextBox
                    {
                        BorderThickness          = new Thickness(0),
                        VerticalContentAlignment = VerticalAlignment.Center,
                        TextAlignment            = TextAlignment.Right,
                        [!TextBox.TextProperty]  = new Binding($"[{def.Key}]", BindingMode.TwoWay),
                    };
                    // Пропускаем только цифры, один разделитель и ведущий минус.
                    tb.AddHandler(InputElement.TextInputEvent, OnNumberTextInput, RoutingStrategies.Tunnel);
                    return tb;
                }

                default:
                    return new TextBox
                    {
                        BorderThickness          = new Thickness(0),
                        VerticalContentAlignment = VerticalAlignment.Center,
                        Watermark                = def.FieldType == "url" ? "https://" : null,
                        [!TextBox.TextProperty]  = new Binding($"[{def.Key}]", BindingMode.TwoWay),
                    };
            }
        });

    // Фильтр ввода числовой ячейки: символ принимается только если итоговый текст
    // остаётся допустимым (частичным) числом — цифры, один разделитель, ведущий минус.
    private static void OnNumberTextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is not TextBox tb || string.IsNullOrEmpty(e.Text)) return;

        var text  = tb.Text ?? "";
        var start = Math.Min(tb.SelectionStart, tb.SelectionEnd);
        var end   = Math.Max(tb.SelectionStart, tb.SelectionEnd);
        var prospective = text[..start] + e.Text + text[end..];

        if (!IsPartialNumber(prospective)) e.Handled = true;
    }

    // Допускает незавершённый ввод: "", "-", "12", "12,", "-3.5".
    private static bool IsPartialNumber(string s)
    {
        if (s.Length == 0) return true;
        var i = s[0] == '-' ? 1 : 0;
        var seps = 0;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '.' or ',') { if (++seps > 1) return false; }
            else if (!char.IsDigit(c)) return false;
        }
        return true;
    }

    // ── Клики и контекстные меню ───────────────────────────────────────────────

    private void OnCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        // Клик по любой ячейке снимает выделение колонки.
        ClearColumnSelection();

        if (e.Row.DataContext is not TableRowVm row) return;

        if (row.IsAddRow)
        {
            RecordCreateRequested?.Invoke(this, (false, null)); // создать в конце, без открытия
            return;
        }

        // Shift/Ctrl + левый клик — точечно добавить/убрать строку из выделения (toggle,
        // по одной строке — как у колонок). Перехватываем до DataGrid (Handled), иначе он
        // сделал бы выделение диапазоном. Запись при этом не открываем.
        var props = e.PointerPressedEventArgs.GetCurrentPoint(null).Properties;
        var mods  = e.PointerPressedEventArgs.KeyModifiers;
        if (props.IsLeftButtonPressed
            && (mods.HasFlag(KeyModifiers.Shift) || mods.HasFlag(KeyModifiers.Control)))
        {
            ToggleRecordSelection(row);
            e.PointerPressedEventArgs.Handled = true;
            // Уводим фокус из возможного текстового поля (правая панель) в таблицу, чтобы
            // последующий Delete удалял записи, а не правил текст.
            Focus();
            return;
        }

        if (e.Column.Tag as string == TableColumnDef.AddPropKey)
        {
            PropertyCreateRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var colIdx = RecordsGrid.Columns.IndexOf(e.Column);
        if (colIdx >= 0 && colIdx < _colDefs.Count)
            _colDefs[colIdx].OnCellClick?.Invoke(row, _colDefs[colIdx]);

        // Открываем запись только обычным левым кликом (без модификаторов — они выше).
        if (e.Column.DisplayIndex == 0 && props.IsLeftButtonPressed)
            RecordClicked?.Invoke(this, row.RecordId);
    }

    // Точечный toggle строки в выделении (Extended-режим поддерживает Add/Remove).
    private void ToggleRecordSelection(TableRowVm row)
    {
        var sel = RecordsGrid.SelectedItems;
        if (sel.Contains(row)) sel.Remove(row);
        else                   sel.Add(row);
    }

    // ── Выделение колонок голубой рамкой (одиночное и Shift-диапазон) ──────────

    private void SelectColumn(string key, bool shift)
    {
        // Служебные колонки № и + не выделяем. «Название»/«Описание» выделять можно
        // (синяя рамка появляется), но при удалении они отфильтровываются.
        if (key == TableColumnDef.NumKey || key == TableColumnDef.AddPropKey) return;

        if (shift)
        {
            // Точечно добавить/убрать одну колонку (toggle), не трогая остальные.
            if (!_selectedColumnKeys.Remove(key))
                _selectedColumnKeys.Add(key);
        }
        else
        {
            // Обычный клик — выделить только эту колонку.
            _selectedColumnKeys.Clear();
            _selectedColumnKeys.Add(key);
        }

        // Порядок выделения держим по порядку колонок — для аккуратной отрисовки рамок.
        _selectedColumnKeys.Sort((a, b) =>
            _colDefs.FindIndex(d => d.Key == a).CompareTo(_colDefs.FindIndex(d => d.Key == b)));

        RefreshColumnHighlights();
    }

    private void ClearColumnSelection()
    {
        if (_selectedColumnKeys.Count == 0) return;
        _selectedColumnKeys.Clear();
        _lastHighlights.Clear();
        ColumnHighlightLayer.Children.Clear();
    }

    // Откладывает перестройку рамок на следующий тик диспетчера — безопасно вызывать
    // из LayoutUpdated (синхронная модификация Canvas там даёт «Infinite layout loop»).
    private void ScheduleHighlightRefresh()
    {
        if (_highlightPending) return;
        _highlightPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _highlightPending = false;
            RefreshColumnHighlights();
        }, DispatcherPriority.Background);
    }

    // Перерисовывает рамки поверх всех выделенных колонок: по одной на колонку.
    // X/ширина — от заголовка; высота — до низа области строк (RowsPresenter), чтобы
    // нижняя линия не уходила под горизонтальный скроллбар. Вызывается при выделении,
    // скролле и изменении размеров.
    // Геометрия последней отрисовки рамок — чтобы не трогать дерево, если ничего не
    // изменилось (иначе перестройка в LayoutUpdated самоподдерживается циклом).
    private readonly List<(double X, double W, double H)> _lastHighlights = [];

    private void RefreshColumnHighlights()
    {
        var panel = ColumnHighlightLayer;

        // Считаем целевую геометрию рамок, НЕ трогая дерево.
        var target = new List<(double X, double W, double H)>();

        if (_selectedColumnKeys.Count > 0)
        {
            var rowsPresenter = RecordsGrid.GetVisualDescendants()
                .FirstOrDefault(v => v.GetType().Name == "DataGridRowsPresenter") as Visual;

            double h;
            if (rowsPresenter is not null)
            {
                var bottomLeft = rowsPresenter.TranslatePoint(new Point(0, rowsPresenter.Bounds.Height), panel);
                h = bottomLeft?.Y ?? RecordsGrid.Bounds.Height;
            }
            else
            {
                h = RecordsGrid.Bounds.Height;
            }
            h = Math.Min(h, panel.Bounds.Height - 2);

            foreach (var key in _selectedColumnKeys)
            {
                var colIdx = _colDefs.FindIndex(d => d.Key == key);
                if (colIdx < 0 || colIdx >= RecordsGrid.Columns.Count) continue;

                var hdr = FindHeaderForColumn(colIdx);
                if (hdr is null) continue;

                var topLeft = hdr.TranslatePoint(new Point(0, 0), panel);
                if (topLeft is null) continue;

                var x = topLeft.Value.X;
                var w = hdr.Bounds.Width;

                // Колонка вне видимой области (за горизонтальным скроллом) — пропускаем.
                if (x + w <= 0 || x >= panel.Bounds.Width) continue;

                target.Add((x, w, h));
            }
        }

        // Соседние выделенные колонки сливаем в одну рамку: иначе на их общей
        // границе ложатся две линии по 2px и выглядят толстой полосой. Внутри
        // объединённой рамки границу колонок рисует обычная серая линия сетки.
        target.Sort((a, b) => a.X.CompareTo(b.X));
        var merged = new List<(double X, double W, double H)>();
        foreach (var r in target)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (r.X <= last.X + last.W + 1)   // смежные/перекрывающиеся колонки
                {
                    merged[^1] = (last.X, Math.Max(last.X + last.W, r.X + r.W) - last.X, Math.Max(last.H, r.H));
                    continue;
                }
            }
            merged.Add(r);
        }

        // Ничего не изменилось — дерево не трогаем (разрывает layout-цикл).
        if (HighlightsEqual(merged, _lastHighlights)) return;

        _lastHighlights.Clear();
        _lastHighlights.AddRange(merged);

        panel.Children.Clear();
        if (merged.Count == 0) return;

        var accent = (IBrush)this.FindResource("BrushAccent")!;
        foreach (var (x, w, h) in merged)
        {
            var border = new Border
            {
                Width            = w,
                Height           = h,
                BorderBrush      = accent,
                BorderThickness  = new Thickness(2),
                CornerRadius     = new CornerRadius(2),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, 0);
            panel.Children.Add(border);
        }
    }

    private static bool HighlightsEqual(
        List<(double X, double W, double H)> a, List<(double X, double W, double H)> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (Math.Abs(a[i].X - b[i].X) > 0.5 ||
                Math.Abs(a[i].W - b[i].W) > 0.5 ||
                Math.Abs(a[i].H - b[i].H) > 0.5) return false;
        }
        return true;
    }

    // Заголовок колонки по индексу в _colDefs. Заголовки в визуальном дереве идут в
    // порядке колонок и позиционированы по X — сортируем по фактической X-координате.
    // Скрытые колонки (IsVisible=false) заголовков не имеют, поэтому индекс _colDefs
    // не совпадает со списком видимых заголовков — переводим в «видимый ранг».
    private DataGridColumnHeader? FindHeaderForColumn(int colIdx)
    {
        if (colIdx < 0 || colIdx >= RecordsGrid.Columns.Count) return null;
        if (!RecordsGrid.Columns[colIdx].IsVisible) return null;

        var visibleRank = 0;
        for (var i = 0; i < colIdx; i++)
            if (RecordsGrid.Columns[i].IsVisible) visibleRank++;

        var headers = RecordsGrid.GetVisualDescendants()
            .OfType<DataGridColumnHeader>()
            .Where(h => h.Bounds.Width > 0)
            .OrderBy(h => h.TranslatePoint(new Point(0, 0), RecordsGrid)?.X ?? double.MaxValue)
            .ToList();

        return visibleRank < headers.Count ? headers[visibleRank] : null;
    }

    private ContextMenu BuildGridContextMenu()
    {
        var menu = new ContextMenu();
        var mi = new MenuItem { Header = Controls.Components.MenuIcons.CreateRecordHeader() };
        mi.Click += (_, _) => RecordCreateRequested?.Invoke(this, (false, null)); // в конце, без открытия
        menu.Items.Add(mi);

        // «Отображение» доступно и отсюда — на случай, если все колонки скрыты и до
        // меню заголовка не добраться (окно показывает полный список колонок).
        var miDisplay = new MenuItem { Header = Controls.Components.MenuIcons.DisplayHeader() };
        miDisplay.Click += (_, _) => ColumnDisplayRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miDisplay);

        return menu;
    }

    private ContextMenu BuildRowContextMenu(int recordId)
    {
        var menu = new ContextMenu();

        var miCreate = new MenuItem { Header = Controls.Components.MenuIcons.CreateRecordHeader() };
        miCreate.Click += (_, _) => RecordCreateRequested?.Invoke(this, (false, recordId)); // после этой строки, без открытия
        menu.Items.Add(miCreate);

        menu.Items.Add(new Separator());

        var miEdit = new MenuItem { Header = Controls.Components.MenuIcons.EditHeader() };
        miEdit.Click += (_, _) => RecordClicked?.Invoke(this, recordId);
        menu.Items.Add(miEdit);

        var miDuplicate = new MenuItem { Header = Controls.Components.MenuIcons.DuplicateHeader() };
        miDuplicate.Click += (_, _) => RecordDuplicateRequested?.Invoke(this, recordId);
        menu.Items.Add(miDuplicate);

        // «Отображение» (записей) — глобальное окно видимости/уровней строк (список всех
        // записей), инициатор-строка не важна.
        var miDisplay = new MenuItem { Header = Controls.Components.MenuIcons.DisplayHeader() };
        miDisplay.Click += (_, _) => RecordDisplayRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miDisplay);

        AddRecordMoveItems(menu, recordId);
        AddHeightModeItems(menu);

        menu.Items.Add(new Separator());

        var miDelete = MakeDeleteMenuItem();
        miDelete.Click += (_, _) => RequestRecordDeleteFromMenu(recordId);
        menu.Items.Add(miDelete);

        return menu;
    }

    // Удаление из меню записи. Если запись входит в текущее мультивыделение (правый
    // клик по выделенной строке его сохраняет) — удаляем весь набор; иначе только её.
    // В любом случае идём через пакетный путь с подтверждением (RecordsDeleteRequested) —
    // как и удаление свойств.
    private void RequestRecordDeleteFromMenu(int recordId)
    {
        var selected = SelectedRecordIds();
        List<int> ids = selected.Contains(recordId) && selected.Count > 1 ? selected : [recordId];
        RecordsDeleteRequested?.Invoke(this, ids);
    }

    // Пункты «Сдвинуть вверх/вниз» (именно в таком порядке) с разделителем перед ними.
    // Доступны только без активных фильтра и сортировки: при сортировке порядок задаёт
    // сортировка, а не позиция, поэтому ручной сдвиг был бы невидим. Неприменимое
    // направление (крайняя запись) скрываем; видимость уточняем при открытии меню
    // (меню кэшируется на строке при её отрисовке).
    private void AddRecordMoveItems(ContextMenu menu, int recordId)
    {
        if (_sortKey is not null || _filters.Count > 0) return;

        var sep    = new Separator();
        var miUp   = new MenuItem { Header = Controls.Components.MenuIcons.MoveUpHeader() };
        miUp.Click += (_, _) => MoveRecord(recordId, up: true);
        var miDown = new MenuItem { Header = Controls.Components.MenuIcons.MoveDownHeader() };
        miDown.Click += (_, _) => MoveRecord(recordId, up: false);

        menu.Items.Add(sep);
        menu.Items.Add(miUp);
        menu.Items.Add(miDown);

        void Refresh()
        {
            var canUp   = CanMoveRecord(recordId, up: true);
            var canDown = CanMoveRecord(recordId, up: false);
            miUp.IsVisible   = canUp;
            miDown.IsVisible = canDown;
            sep.IsVisible    = canUp || canDown;
        }

        Refresh();
        menu.Opening += (_, _) => Refresh();
    }

    // Красный пункт «Удалить» с иконкой корзины — общий для меню записей и свойств.
    private static MenuItem MakeDeleteMenuItem(bool enabled = true) =>
        new() { Header = Controls.Components.MenuIcons.DeleteHeader(), IsEnabled = enabled };

    // Некликабельная шапка контекстного меню (карточка свойства). IsHitTestVisible=
    // false — не подсвечивается при наведении и не реагирует на клик.
    private static MenuItem MakeMenuHeaderItem(Control content) => new()
    {
        Header           = content,
        IsHitTestVisible = false,
        Focusable        = false,
    };

    // ── Системные колонки «Название»/«Описание» ───────────────────────────────

    // Публично для контроллера: текущая подпись и имя по умолчанию по ключу колонки.
    public string GetSystemHeader(string key)        => SystemHeader(key);
    public string GetSystemDefaultName(string key)   => SystemDefaultName(key);

    // Имя по умолчанию для системной колонки.
    private static string SystemDefaultName(string key) =>
        key == TableRowVm.DescKey ? "Описание" : "Название";

    // Текущая подпись: пользовательская из метаданных, иначе имя по умолчанию.
    private string SystemHeader(string key) =>
        _systemNames.TryGetValue(key, out var name) ? name : SystemDefaultName(key);

    // Меню заголовка системной колонки «Название»/«Описание»:
    //   Изменить · Создать новое свойство · — · Сдвинуть вправо · Сдвинуть влево
    // Удаление/смена типа/дублирование для системных колонок недоступны.
    private ContextMenu BuildSystemHeaderContextMenu(string key)
    {
        var menu = new ContextMenu();

        // Шапка: иконка типа + подпись колонки + тип «Текст» (Title/Desc — текстовые).
        menu.Items.Add(MakeMenuHeaderItem(
            Controls.Components.MenuIcons.PropertyMenuHeader(SystemHeader(key), "text")));
        menu.Items.Add(new Separator());

        var miEdit = new MenuItem { Header = Controls.Components.MenuIcons.EditHeader() };
        miEdit.Click += (_, _) => SystemFieldRenameRequested?.Invoke(this, key);
        menu.Items.Add(miEdit);

        var miCreate = new MenuItem { Header = Controls.Components.MenuIcons.CreatePropertyHeader() };
        miCreate.Click += (_, _) => PropertyCreateAfterRequested?.Invoke(this, key);
        menu.Items.Add(miCreate);

        var miDisplay = new MenuItem { Header = Controls.Components.MenuIcons.DisplayHeader() };
        miDisplay.Click += (_, _) => ColumnDisplayRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miDisplay);

        AddMoveItems(menu, key);
        AddWidthModeItems(menu);
        return menu;
    }

    // Пункты «Сдвинуть вправо/влево» (именно в таком порядке) с разделителем перед
    // ними. Общие для меню системных и пользовательских колонок. Неприменимое
    // направление (крайняя колонка) скрываем.
    private void AddMoveItems(ContextMenu menu, string key)
    {
        var canLeft  = CanMoveColumn(key, left: true);
        var canRight = CanMoveColumn(key, left: false);
        if (!canLeft && !canRight) return;

        menu.Items.Add(new Separator());

        if (canRight)
        {
            var mi = new MenuItem { Header = Controls.Components.MenuIcons.MoveRightHeader() };
            mi.Click += (_, _) => MoveColumn(key, left: false);
            menu.Items.Add(mi);
        }
        if (canLeft)
        {
            var mi = new MenuItem { Header = Controls.Components.MenuIcons.MoveLeftHeader() };
            mi.Click += (_, _) => MoveColumn(key, left: true);
            menu.Items.Add(mi);
        }
    }

    // Пункт-переключатель режима ширины столбцов (общий для всей таблицы). Текст
    // показывает режим, в который переключит клик: при стандартной ширине — «Ручная
    // ширина столбцов», при ручной — «Стандартная ширина столбцов». Заголовок
    // пересчитывается при открытии меню (меню кэшируется на заголовке колонки).
    private void AddWidthModeItems(ContextMenu menu)
    {
        menu.Items.Add(new Separator());

        var item = new MenuItem();
        item.Click += (_, _) => SetColResizeMode(!_manualColResize);
        menu.Items.Add(item);

        void Refresh() => item.Header = Controls.Components.MenuIcons.WidthModeHeader(
            _manualColResize ? "Стандартная ширина столбцов" : "Ручная ширина столбцов");

        Refresh();
        menu.Opening += (_, _) => Refresh();
    }

    // Пункт-переключатель режима высоты строк (общий для всей таблицы) — в меню записи.
    // Текст показывает режим, в который переключит клик: при стандартной высоте —
    // «Ручная высота строк», при ручной — «Стандартная высота строк». Заголовок
    // пересчитывается при открытии меню (меню кэшируется на строке при её отрисовке).
    private void AddHeightModeItems(ContextMenu menu)
    {
        menu.Items.Add(new Separator());

        var item = new MenuItem();
        item.Click += (_, _) => SetRowResizeMode(!_manualRowResize);
        menu.Items.Add(item);

        void Refresh() => item.Header = Controls.Components.MenuIcons.HeightModeHeader(
            _manualRowResize ? "Стандартная высота строк" : "Ручная высота строк");

        Refresh();
        menu.Opening += (_, _) => Refresh();
    }

    // Меню заголовка пользовательского поля:
    //   Изменить · Дублировать · Создать новое свойство · — · Сдвинуть вправо ·
    //   Сдвинуть влево · — · Удалить
    private ContextMenu BuildHeaderContextMenu(int fieldId, bool isRequired, string name, string fieldType)
    {
        var menu = new ContextMenu();

        // Шапка: иконка типа + название свойства + тип данных (некликабельная).
        menu.Items.Add(MakeMenuHeaderItem(Controls.Components.MenuIcons.PropertyMenuHeader(name, fieldType)));
        menu.Items.Add(new Separator());

        var miEdit = new MenuItem { Header = Controls.Components.MenuIcons.EditHeader() };
        miEdit.Click += (_, _) => PropertyEditRequested?.Invoke(this, fieldId);
        menu.Items.Add(miEdit);

        var miDuplicate = new MenuItem { Header = Controls.Components.MenuIcons.DuplicateHeader() };
        miDuplicate.Click += (_, _) => PropertyDuplicateRequested?.Invoke(this, fieldId);
        menu.Items.Add(miDuplicate);

        var miCreate = new MenuItem { Header = Controls.Components.MenuIcons.CreatePropertyHeader() };
        miCreate.Click += (_, _) => PropertyCreateAfterRequested?.Invoke(this, fieldId.ToString());
        menu.Items.Add(miCreate);

        var miDisplay = new MenuItem { Header = Controls.Components.MenuIcons.DisplayHeader() };
        miDisplay.Click += (_, _) => ColumnDisplayRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miDisplay);

        AddMoveItems(menu, fieldId.ToString());
        AddWidthModeItems(menu);

        menu.Items.Add(new Separator());

        var miDelete = MakeDeleteMenuItem(enabled: !isRequired);
        miDelete.Click += (_, _) => RequestDeleteFromMenu(fieldId);
        menu.Items.Add(miDelete);

        return menu;
    }

    // Удаление из контекстного меню «Удалить». Если поле входит в текущее выделение
    // (несколько колонок) — удаляем весь выделенный набор; иначе только это поле.
    // В любом случае идём через пакетный путь с подтверждением (PropertiesDeleteRequested).
    private void RequestDeleteFromMenu(int fieldId)
    {
        var key = fieldId.ToString();

        List<int> ids;
        if (_selectedColumnKeys.Contains(key) && _selectedColumnKeys.Count > 1)
        {
            ids = _selectedColumnKeys
                .Where(k => int.TryParse(k, out _))
                .Select(int.Parse)
                .ToList();
        }
        else
        {
            ids = [fieldId];
        }

        if (ids.Count == 0) return;

        // Перед показом диалога оставляем рамки только на удаляемых колонках.
        KeepSelectionOnDeletableOnly();
        PropertiesDeleteRequested?.Invoke(this, ids);
    }

    // Убирает из выделения системные колонки («Название»/«Описание») — они не
    // удаляются, поэтому при запросе удаления их обводка гаснет, а рамки на
    // удаляемых полях остаются видимыми, пока открыт диалог подтверждения.
    private void KeepSelectionOnDeletableOnly()
    {
        var removed = _selectedColumnKeys.RemoveAll(k => !int.TryParse(k, out _));
        if (removed > 0) RefreshColumnHighlights();
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not TableRowVm row) return;
        if (row.IsAddRow)
        {
            e.Row.Height = double.NaN;   // строка «+» всегда стандартная
            return;
        }
        e.Row.ContextMenu = BuildRowContextMenu(row.RecordId);

        // Высота строки: в ручном режиме применяем сохранённую (если есть), иначе
        // сбрасываем на стандартную — важно для переиспользуемых строк (виртуализация).
        e.Row.Height = _manualRowResize && _rowHeights.TryGetValue(row.RecordId, out var h)
            ? h
            : double.NaN;

        ScheduleRowHandlesRefresh();   // строка реализована — обновим полоски ресайза
    }

    // ── Ручная высота строк: полоски-«грипперы» у нижней границы ───────────────

    private double StandardRowHeight() =>
        this.TryFindResource("RowHeightStandard", out var r) && r is double d ? d : 36;

    // Откладывает перестройку полосок на тик диспетчера (как у рамок колонок —
    // менять дерево Canvas прямо в LayoutUpdated нельзя). Во время drag не трогаем.
    private void ScheduleRowHandlesRefresh()
    {
        if (_rowHandlesPending || _isResizingRow) return;
        _rowHandlesPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rowHandlesPending = false;
            RefreshRowResizeHandles();
        }, DispatcherPriority.Background);
    }

    // Геометрия последней раскладки полосок — чтобы не трогать дерево, если ничего не
    // изменилось (иначе перестройка из LayoutUpdated самоподдерживается layout-циклом —
    // та же защита, что у рамок выделения колонок).
    private readonly List<(DataGridRow Row, double X, double W, double Top, int Rec)> _lastRowGrips = [];

    // Размещает по прозрачной полоске на нижней границе каждой видимой строки данных.
    // Полоска кликабельна и несёт курсор «↕»; перетаскивание меняет высоту строки.
    private void RefreshRowResizeHandles()
    {
        if (_isResizingRow) return;   // во время перетаскивания полоски не пересобираем

        var layer = RowResizeLayer;

        // Считаем целевую раскладку полосок, НЕ трогая дерево.
        var target = new List<(DataGridRow Row, double X, double W, double Top, int Rec)>();
        if (_manualRowResize)
        {
            foreach (var rowCtrl in RecordsGrid.GetVisualDescendants().OfType<DataGridRow>())
            {
                if (rowCtrl.DataContext is not TableRowVm vm || vm.IsAddRow) continue;
                if (rowCtrl.Bounds.Height <= 0) continue;

                var topLeft = rowCtrl.TranslatePoint(new Point(0, 0), layer);
                if (topLeft is null) continue;

                var bottomY = topLeft.Value.Y + rowCtrl.Bounds.Height;
                if (bottomY < 0 || bottomY > layer.Bounds.Height) continue;   // вне видимой зоны

                target.Add((rowCtrl, topLeft.Value.X, rowCtrl.Bounds.Width,
                            bottomY - RowGripThickness / 2, vm.RecordId));
            }
        }

        // Ничего не изменилось — дерево не трогаем (разрывает layout-цикл).
        if (RowGripsEqual(target, _lastRowGrips)) return;

        _lastRowGrips.Clear();
        _lastRowGrips.AddRange(target);

        layer.Children.Clear();
        foreach (var (rowCtrl, x, w, top, rec) in target)
        {
            var grip = new Border
            {
                Width      = w,
                Height     = RowGripThickness,
                Background = Brushes.Transparent,
                Cursor     = RowResizeCursor,
            };
            grip.PointerPressed  += (_, e) => BeginRowResize(rowCtrl, rec, e);
            grip.PointerMoved    += (_, e) => DragRowResize(e);
            grip.PointerReleased += (_, e) => EndRowResize(e);

            Canvas.SetLeft(grip, x);
            Canvas.SetTop(grip, top);
            layer.Children.Add(grip);
        }
    }

    private static bool RowGripsEqual(
        List<(DataGridRow Row, double X, double W, double Top, int Rec)> a,
        List<(DataGridRow Row, double X, double W, double Top, int Rec)> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!ReferenceEquals(a[i].Row, b[i].Row) || a[i].Rec != b[i].Rec) return false;
            if (Math.Abs(a[i].X   - b[i].X)   > 0.5) return false;
            if (Math.Abs(a[i].W   - b[i].W)   > 0.5) return false;
            if (Math.Abs(a[i].Top - b[i].Top) > 0.5) return false;
        }
        return true;
    }

    private void BeginRowResize(DataGridRow rowCtrl, int recordId, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(RowResizeLayer).Properties.IsLeftButtonPressed) return;

        var bottom = rowCtrl.TranslatePoint(new Point(0, rowCtrl.Bounds.Height), RowResizeLayer);

        _isResizingRow       = true;
        _resizeRow           = rowCtrl;
        _resizeRecordId      = recordId;
        _resizeStartPointerY = e.GetPosition(RowResizeLayer).Y;
        _resizeStartBottomY  = bottom?.Y ?? _resizeStartPointerY;
        _resizeStartHeight   = rowCtrl.Bounds.Height;
        e.Pointer.Capture(e.Source as IInputElement);
        e.Handled = true;
    }

    private void DragRowResize(PointerEventArgs e)
    {
        if (!_isResizingRow || _resizeRow is null) return;

        var delta = e.GetPosition(RowResizeLayer).Y - _resizeStartPointerY;
        var h     = Math.Clamp(_resizeStartHeight + delta, StandardRowHeight(), RowHeightMax);
        _resizeRow.Height = h;

        // Полоска следует за новой нижней границей (по clamp'нутой высоте, не по Bounds,
        // которые на этом проходе ещё не пересчитаны).
        if (e.Source is Control grip)
            Canvas.SetTop(grip, _resizeStartBottomY + (h - _resizeStartHeight) - RowGripThickness / 2);

        e.Handled = true;
    }

    private void EndRowResize(PointerReleasedEventArgs e)
    {
        if (!_isResizingRow) return;

        _isResizingRow = false;
        e.Pointer.Capture(null);
        e.Handled = true;

        if (_resizeRow is not null)
        {
            var h = (int)Math.Round(Math.Clamp(_resizeRow.Height, StandardRowHeight(), RowHeightMax));
            _rowHeights[_resizeRecordId] = h;
            _repository?.SaveRowHeights(_collectionId, _rowHeights);
        }

        _resizeRow = null;
        ScheduleRowHandlesRefresh();   // пересобрать полоски под новые позиции строк
    }

    // ── Фильтр и сортировка колонок (Excel-стиль) ──────────────────────────────

    // Текущий открытый flyout фильтра/сортировки — чтобы закрыть его при потере фокуса.
    private Flyout? _activeFilterFlyout;

    private void ShowFilterFlyout(Control anchor, TableColumnDef def)
    {
        if (_repository is null) return;

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.FlyoutPresenterClasses.Add("filterFlyout");
        flyout.Content = BuildFilterPanel(def, () => flyout.Hide());
        flyout.Closed += (_, _) => { if (ReferenceEquals(_activeFilterFlyout, flyout)) _activeFilterFlyout = null; };
        _activeFilterFlyout = flyout;
        flyout.ShowAt(anchor);
    }

    // Закрывает открытый flyout фильтра/сортировки (вызывается при деактивации окна).
    public void CloseTransientPopups() => _activeFilterFlyout?.Hide();

    private Control BuildFilterPanel(TableColumnDef def, Action close)
    {
        var secondary = (IBrush)this.FindResource("BrushSecondaryText")!;
        var fontBase  = (double)this.FindResource("FontSizeBase")!;
        var radius    = (CornerRadius)this.FindResource("RadiusBase")!;

        // Курсор-стрелка по умолчанию для всей панели (наследуется вниз): на пустых
        // местах и подложке — стрелка. Интерактивные элементы (кнопки сортировки,
        // чекбоксы значений, «Готово») задают курсор-руку явно и перебивают наследование.
        var panel = new StackPanel
        {
            Width  = 240,
            Margin = new Thickness(12),
            Spacing = 2,
            Cursor  = new Cursor(StandardCursorType.Arrow),
        };

        // ── Данные (грузятся в ФОНЕ; окно открывается мгновенно) ──
        // Уникальные значения с количеством считаются SQL-агрегатом на фоновом потоке
        // (отдельное read-only подключение, WAL → параллельное чтение). До прихода данных в
        // области списка показывается «Загрузка…». Счётчики — по всей колонке (без Excel-каскада
        // по другим фильтрам: на больших данных каскад неподъёмен, а список значений полный).
        var baseCounts = new List<(string Value, int Count)>();
        var domain     = new HashSet<string>();
        var loaded     = false;

        // ── Сортировка ──
        // Выбор не применяется сразу: копится в pendingSort и вступает в силу
        // вместе с фильтром по кнопке «Готово».
        var (ascLabel, descLabel) = def.FieldType switch
        {
            "number" => ("По возрастанию", "По убыванию"),
            "date"   => ("Сначала старые", "Сначала новые"),
            _        => ("От А до Я", "От Я до А"),
        };
        bool? pendingSort = _sortKey == def.Key ? _sortAsc : null;

        var sortSection = new StackPanel { Spacing = 2 };
        panel.Children.Add(sortSection);

        Action rebuildListRef = () => { };   // заполняется после объявления RebuildList

        List<(string Raw, int Count)> counts = [];
        void ReloadCounts()
        {
            // Порядок значений = выбранная сортировка ЭТОЙ колонки (pending или активная),
            // иначе по значению с пустыми в конце.
            bool? asc = pendingSort ?? (_sortKey == def.Key ? _sortAsc : (bool?)null);
            counts = SortValueCounts(baseCounts, def.FieldType, asc);
        }
        ReloadCounts();

        void RenderSort()
        {
            sortSection.Children.Clear();
            sortSection.Children.Add(MakeFlatItem(ascLabel,
                () => { pendingSort = true;  RenderSort(); ReloadCounts(); rebuildListRef(); },
                SortAzIcon, active: pendingSort == true));
            sortSection.Children.Add(MakeFlatItem(descLabel,
                () => { pendingSort = false; RenderSort(); ReloadCounts(); rebuildListRef(); },
                SortZaIcon, active: pendingSort == false));
            sortSection.Children.Add(MakeFlatItem("Снять сортировку",
                () => { pendingSort = null;  RenderSort(); ReloadCounts(); rebuildListRef(); },
                XIcon));
        }
        RenderSort();

        panel.Children.Add(MakeSeparator());

        // ── Поиск по значениям ──
        var search = new TextBox
        {
            Watermark    = "Поиск значений...",
            Height       = 30,
            CornerRadius = radius,
            Margin       = new Thickness(0, 0, 0, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(search);

        // Наполняется по приходе данных (см. фоновую загрузку ниже).
        var checkedSet       = new HashSet<string>();
        var syncingSelectAll = false;

        var selectAll = new CheckBox
        {
            Content   = "(Выделить всё)",
            FontSize  = fontBase,
            IsChecked = !_filters.ContainsKey(def.Key),
            Cursor    = new Cursor(StandardCursorType.Hand),
        };

        // Список значений — виртуализирующий ListBox: рендерятся только видимые строки,
        // поэтому показываются ВСЕ уникальные значения (без лимита) и без зависания даже
        // при сотнях тысяч значений. «Выделить всё» закреплён сверху.
        var shown    = new AvaloniaList<FilterValueVm>();
        var vmCache  = new Dictionary<string, FilterValueVm>();
        FilterValueVm Vm(string raw, int count) =>
            vmCache.TryGetValue(raw, out var v) ? v
                : vmCache[raw] = new FilterValueVm(raw, DisplayValue(raw, def.FieldType), count,
                                                   checkedSet, () => SyncSelectAll());

        var valuesList = new ListBox
        {
            Height          = 200,
            Background       = Brushes.Transparent,
            BorderThickness  = new Thickness(0),
            Padding          = new Thickness(0),
            ItemsSource      = shown,
            ItemTemplate     = BuildFilterValueTemplate(fontBase, secondary),
        };
        valuesList.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                        new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });

        var valuesBox = new StackPanel { Spacing = 4 };
        valuesBox.Children.Add(selectAll);
        valuesBox.Children.Add(valuesList);

        // До прихода данных — «Загрузка…»; по готовности заменяется на список значений.
        var loadingText = new TextBlock
        {
            Text                = "Загрузка…",
            Foreground          = secondary,
            FontSize            = fontBase,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        var valuesHost = new Border
        {
            BorderBrush     = (IBrush)this.FindResource("BrushSeparator")!,
            BorderThickness = new Thickness(1),
            CornerRadius    = radius,
            Padding         = new Thickness(8, 6),
            Height          = 232,   // фикс высота — список не «прыгает» при замене «Загрузка…»
            Child           = loadingText,
        };
        panel.Children.Add(valuesHost);

        List<(string Raw, int Count)> Matched()
        {
            var q = search.Text?.Trim() ?? "";
            return q.Length == 0
                ? counts
                : counts.Where(c => DisplayValue(c.Raw, def.FieldType)
                    .Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        void RebuildList()
        {
            shown.Clear();
            shown.AddRange(Matched().Select(c => Vm(c.Raw, c.Count)));
        }
        rebuildListRef = RebuildList;

        // «Выделить всё» отражает состояние видимых значений: снял одну галку —
        // снимается и общая. Guard защищает от каскада, когда IsChecked меняется
        // программно (иначе обработчик ниже снял бы галки со всех значений).
        void SyncSelectAll()
        {
            syncingSelectAll    = true;
            selectAll.IsChecked = Matched().All(c => checkedSet.Contains(c.Raw));
            syncingSelectAll    = false;
        }

        search.TextChanged += (_, _) => { RebuildList(); SyncSelectAll(); };
        selectAll.IsCheckedChanged += (_, _) =>
        {
            if (syncingSelectAll) return;
            // «Выделить всё» действует на видимое (после поиска) подмножество.
            var on = selectAll.IsChecked == true;
            foreach (var (raw, _) in Matched())
            {
                if (on) checkedSet.Add(raw);
                else    checkedSet.Remove(raw);
            }
            RebuildList();
        };
        // Первичное наполнение списка — после фоновой загрузки данных (см. ниже).

        panel.Children.Add(MakeSeparator());

        // ── Кнопка «Готово» ──
        // Сортировка и фильтр применяются разом, только по этой кнопке.
        var okBtn = new Button
        {
            Content             = "Готово",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 4, 0, 0),
        };
        okBtn.Classes.Add("dialog");
        okBtn.Click += (_, _) =>
        {
            // Данные ещё грузятся — применять нечего (фильтр по колонке не трогаем).
            if (!loaded)
            {
                if (pendingSort is bool a) { _sortKey = def.Key; _sortAsc = a; ApplyFilters(); }
                else if (_sortKey == def.Key) { _sortKey = null; ApplyFilters(); }
                close();
                return;
            }

            if (pendingSort is bool asc) { _sortKey = def.Key; _sortAsc = asc; }
            else if (_sortKey == def.Key) _sortKey = null;

            // Excel-режим «Заменить»: при активном поиске фильтр = только отмеченные
            // СРЕДИ найденного; значения вне зоны поиска отсекаются (в фильтр не входят).
            // Поиск пуст — фильтр = все отмеченные галки по всему домену.
            var hasSearch = !string.IsNullOrWhiteSpace(search.Text);
            var effective = hasSearch
                ? Matched().Select(c => c.Raw).Where(checkedSet.Contains).ToHashSet()
                : new HashSet<string>(checkedSet);

            // Отмечены все значения колонки (а не только видимые) — фильтр не нужен.
            if (domain.SetEquals(effective)) _filters.Remove(def.Key);
            else _filters[def.Key] = effective;
            ApplyFilters();
            close();
        };
        panel.Children.Add(okBtn);

        // ── Фоновая загрузка значений ──
        // Окно уже показано с «Загрузка…»; считаем значения на фоновом потоке и по готовности
        // (на UI-потоке) наполняем список. Если окно успели закрыть — операции над уже
        // отсоединёнными контролами безвредны.
        _repository!.GetColumnCountsAsync(_collectionId, def.Key).ContinueWith(t =>
        {
            if (t.IsFaulted || t.IsCanceled) return;
            var data = t.Result;
            Dispatcher.UIThread.Post(() =>
            {
                baseCounts.Clear();
                baseCounts.AddRange(data);
                domain.Clear();
                foreach (var c in data) domain.Add(c.Value);

                checkedSet.Clear();
                if (_filters.TryGetValue(def.Key, out var ff)) checkedSet.UnionWith(ff);
                else checkedSet.UnionWith(domain);

                ReloadCounts();
                valuesHost.Child = valuesBox;   // «Загрузка…» → список значений
                RebuildList();
                SyncSelectAll();
                loaded = true;
            });
        });

        return panel;
    }

    // Иконка в стандарте сайдбара: Viewbox 18×18 поверх сетки 24×24.
    private Viewbox MakeStrokeIcon(Geometry data, IBrush stroke) => new()
    {
        Width  = 18,
        Height = 18,
        VerticalAlignment = VerticalAlignment.Center,
        Child  = new Canvas
        {
            Width  = 24,
            Height = 24,
            Children =
            {
                new Avalonia.Controls.Shapes.Path
                {
                    Data            = data,
                    Stroke          = stroke,
                    StrokeThickness = 1.6,
                    StrokeLineCap   = PenLineCap.Round,
                    StrokeJoin      = PenLineJoin.Round,
                },
            },
        },
    };

    private Button MakeFlatItem(string text, Action onClick, Geometry? icon = null, bool active = false)
    {
        // Активный пункт (выбранная сортировка) — акцентный цвет и галка справа.
        var fg = (IBrush)this.FindResource(active ? "BrushAccent" : "BrushPrimaryText")!;

        object content = text;
        if (icon is not null)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            var iconBox = MakeStrokeIcon(icon, fg);
            Grid.SetColumn(iconBox, 0);
            row.Children.Add(iconBox);

            var label = new TextBlock
            {
                Text                = text,
                Foreground          = fg,
                Margin              = new Thickness(8, 0, 0, 0),
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            if (active)
            {
                var check = MakeStrokeIcon(CheckIcon, fg);
                Grid.SetColumn(check, 2);
                row.Children.Add(check);
            }

            content = row;
        }

        var btn = new Button
        {
            Content         = content,
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(8, 6),
            FontSize        = (double)this.FindResource("FontSizeBase")!,
            Foreground      = fg,
            CornerRadius    = (CornerRadius)this.FindResource("RadiusBase")!,
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = icon is not null
                ? HorizontalAlignment.Stretch
                : HorizontalAlignment.Left,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Border MakeSeparator() => new()
    {
        Height     = 1,
        Background = (IBrush)this.FindResource("BrushSeparator")!,
        Margin     = new Thickness(0, 6),
    };

    // Упорядочивает список значений с количеством для панели фильтра. asc задан — сортировка
    // тем же компаратором, что и таблица (text/number/date); null — порядок как в таблице
    // (репозиторий уже отдал по первой позиции, «(Пусто)» в конце). src — SQL-агрегат по колонке.
    private List<(string Raw, int Count)> SortValueCounts(
        List<(string Value, int Count)> src, string fieldType, bool? asc)
    {
        if (asc is bool a)
        {
            var cmp = BuildComparer(fieldType, a);
            return src.OrderBy(c => c.Value, Comparer<string>.Create((x, y) => cmp(x, y)))
                      .Select(c => (c.Value, c.Count)).ToList();
        }

        // Без сортировки — порядок как в таблице: репозиторий уже вернул значения по первой
        // позиции записи, «(Пусто)» в конце. Ничего не пересортировываем.
        return src.Select(c => (c.Value, c.Count)).ToList();
    }

    // Шаблон строки списка значений фильтра (чекбокс + количество) — для виртуализированного
    // ListBox: привязки следуют за DataContext при переиспользовании контейнеров.
    private static IDataTemplate BuildFilterValueTemplate(double fontBase, IBrush secondary) =>
        new FuncDataTemplate<FilterValueVm>((_, _) =>
        {
            var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(FilterValueVm.Display)));

            var cb = new CheckBox
            {
                Content   = name,
                FontSize  = fontBase,
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin    = new Thickness(0),
                Cursor    = new Cursor(StandardCursorType.Hand),
            };
            cb.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(FilterValueVm.Checked)) { Mode = BindingMode.TwoWay });

            var cnt = new TextBlock
            {
                Foreground          = secondary,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(0, 0, 12, 0),
            };
            cnt.Bind(TextBlock.TextProperty, new Binding(nameof(FilterValueVm.CountText)));

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(cb, 0);  row.Children.Add(cb);
            Grid.SetColumn(cnt, 1); row.Children.Add(cnt);
            return row;
        });

    // Строка списка значений фильтра. Checked читает/пишет общий checkedSet; onChanged
    // обновляет состояние «(Выделить всё)».
    private sealed class FilterValueVm : System.ComponentModel.INotifyPropertyChanged
    {
        public string Raw       { get; }
        public string Display   { get; }
        public string CountText { get; }

        private readonly HashSet<string> _checked;
        private readonly Action _onChanged;

        public FilterValueVm(string raw, string display, int count, HashSet<string> checkedSet, Action onChanged)
        {
            Raw = raw; Display = display; CountText = count.ToString();
            _checked = checkedSet; _onChanged = onChanged;
        }

        public bool Checked
        {
            get => _checked.Contains(Raw);
            set
            {
                if (value) _checked.Add(Raw); else _checked.Remove(Raw);
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Checked)));
                _onChanged();
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private static string DisplayValue(string raw, string fieldType)
    {
        if (raw == "") return "(Пусто)";
        if (fieldType == "checkbox") return IsTruthy(raw) ? "✓" : "—";
        return raw;
    }

    /// <summary>
    /// Применяет активные фильтры/сортировку. Без них таблица работает на
    /// виртуализированном списке; с ними — на материализованном (все записи
    /// загружаются, фильтруются и сортируются в памяти).
    /// </summary>
    private void ApplyFilters()
    {
        // Чистим фильтры по колонкам, которых больше нет (поле удалили).
        var validKeys = _colDefs.Select(d => d.Key).ToHashSet();
        foreach (var stale in _filters.Keys.Where(k => !validKeys.Contains(k)).ToList())
            _filters.Remove(stale);
        if (_sortKey is not null && !validKeys.Contains(_sortKey)) _sortKey = null;

        UpdateChevronStates();

        if (_repository is null) return;

        if (_filters.Count == 0 && _sortKey is null)
        {
            if (!ReferenceEquals(RecordsGrid.ItemsSource, DefaultItemsSource))
                RecordsGrid.ItemsSource = DefaultItemsSource;
            return;
        }

        // Виртуализация включена → фильтр уходит в SQL (только id совпавших записей),
        // сортировка — тем же компаратором по значениям ОДНОЙ колонки, показ виртуализируется
        // постранично по списку id. Не трогаем нефильтрованные _virtualRows/_virtualView —
        // создаём временный источник; при снятии фильтра вернёмся к DefaultItemsSource.
        if (_virtualView is not null)
        {
            var ids = _repository.GetFilteredRecordIds(_collectionId, _filters,
                                                       excludeHidden: _hiddenRecords.Count > 0);
            if (_sortKey is { } sk)
            {
                var def  = _colDefs.FirstOrDefault(d => d.Key == sk);
                var cmp  = BuildComparer(def?.FieldType ?? "text", _sortAsc);
                var vals = _repository.GetColumnValues(_collectionId, sk);
                ids.Sort((a, b) => cmp(vals.GetValueOrDefault(a, ""), vals.GetValueOrDefault(b, "")));
            }

            var filteredRows = new VirtualRowList(_repository, _collectionId, _fields, recordIds: ids);
            RecordsGrid.ItemsSource = new VirtualCollectionView(filteredRows);
            return;
        }

        // Виртуализация выключена → старый материализованный путь (всё в память).
        var rows = LoadAllRows();

        foreach (var (key, allowed) in _filters)
            rows = rows.Where(r => allowed.Contains(r[key])).ToList();

        if (_sortKey is { } sortKey)
        {
            var def = _colDefs.FirstOrDefault(d => d.Key == sortKey);
            var cmp = BuildComparer(def?.FieldType ?? "text", _sortAsc);
            rows.Sort((a, b) => cmp(a[sortKey], b[sortKey]));
        }

        for (var i = 0; i < rows.Count; i++)
            rows[i].SetRowNumber(i + 1);

        rows.Add(TableRowVm.AddRow());
        RecordsGrid.ItemsSource = rows;
    }

    // Все строки в память (материализованный путь: активный фильтр/сортировка колонок,
    // а также подсчёт значений в панели фильтра). Скрытые записи (окно «Отображение
    // записей») исключаем — как и виртуализованный путь через excludeHidden, чтобы оба
    // пути показывали один и тот же набор строк.
    private List<TableRowVm> LoadAllRows()
    {
        var records = _repository!.GetRecords(_collectionId);
        var values  = _repository.GetFieldValuesByCollection(_collectionId);

        var rows = new List<TableRowVm>(records.Count);
        var num  = 0;
        foreach (var rec in records)
        {
            var id = rec.Id!.Value;
            if (_hiddenRecords.Contains(id)) continue;
            rows.Add(new TableRowVm(_repository, id, ++num,
                values.TryGetValue(id, out var v) ? v : [],
                rec.Title ?? "", rec.Description));
        }
        return rows;
    }

    private static Func<string, string, int> BuildComparer(string fieldType, bool asc)
    {
        int Core(string a, string b) => fieldType switch
        {
            "number" => ParseNum(a).CompareTo(ParseNum(b)),
            "date"   => ParseDate(a).CompareTo(ParseDate(b)),
            _        => string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase),
        };

        return (a, b) =>
        {
            // Пустые значения всегда в конце, независимо от направления.
            var aEmpty = string.IsNullOrEmpty(a);
            var bEmpty = string.IsNullOrEmpty(b);
            if (aEmpty || bEmpty) return aEmpty == bEmpty ? 0 : (aEmpty ? 1 : -1);
            var r = Core(a, b);
            return asc ? r : -r;
        };
    }

    private static double ParseNum(string s) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d : double.MaxValue;

    private static DateTime ParseDate(string s) =>
        DateValue.TryParse(s, out var dv) ? dv.Start : DateTime.MaxValue;

    // ── Ширины колонок ─────────────────────────────────────────────────────────

    private void ApplyColumnWidths()
    {
        if (_repository is null) return;
        var saved = _repository.GetColWidths(_collectionId);

        for (var i = 0; i < _colDefs.Count && i < RecordsGrid.Columns.Count; i++)
        {
            var def = _colDefs[i];
            if (!def.IsStarWidth) continue;

            var col = RecordsGrid.Columns[i];

            if (_manualColResize && int.TryParse(def.Key, out var fieldId))
            {
                col.Width = saved.TryGetValue(fieldId, out var w) && w > 0
                    ? new DataGridLength(w)
                    : new DataGridLength(def.MinWidth);
            }
            else
            {
                col.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            }
        }
    }

    private void OnGridLayoutUpdated(object? s, EventArgs e)
    {
        // Рамки выделенных колонок следуют за скроллом/ресайзом. Перестройку откладываем
        // через Dispatcher: менять дерево Canvas прямо в LayoutUpdated нельзя — это
        // инвалидирует раскладку и вызывает «Infinite layout loop».
        if (_selectedColumnKeys.Count > 0)
            ScheduleHighlightRefresh();

        // Полоски ресайза строк следуют за скроллом/ресайзом (как и рамки колонок).
        if (_manualRowResize && !_isResizingRow)
            ScheduleRowHandlesRefresh();

        if (_snapPending || _manualColResize) return;

        // В авто-режиме снапим Star-колонки до целочисленных весов — иначе DataGrid
        // не рисует вертикальные грид-линии в сабпиксельных позициях. В ручном режиме
        // снап не нужен: UseLayoutRounding=True на DataGrid уже округляет рендер до
        // пикселей, а активный снап во время drag делает перетаскивание «дёрганым».
        var hasFractional = false;
        for (var i = 0; i < RecordsGrid.Columns.Count && i < _colDefs.Count; i++)
        {
            var def = _colDefs[i];
            if (!def.IsStarWidth) continue;
            var col = RecordsGrid.Columns[i];
            if (col.ActualWidth <= 0 || !col.Width.IsStar) continue;
            if (col.ActualWidth != Math.Floor(col.ActualWidth)) { hasFractional = true; break; }
        }
        if (!hasFractional) return;

        _snapPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _snapPending = false;
            SnapStarColumnWidths();
        }, DispatcherPriority.Background);
    }

    private void SnapStarColumnWidths()
    {
        var starCols = new List<DataGridColumn>();
        for (var i = 0; i < RecordsGrid.Columns.Count && i < _colDefs.Count; i++)
        {
            var def = _colDefs[i];
            if (def.IsStarWidth && RecordsGrid.Columns[i].Width.IsStar && RecordsGrid.Columns[i].ActualWidth > 0)
                starCols.Add(RecordsGrid.Columns[i]);
        }
        if (starCols.Count == 0) return;

        var total = (int)Math.Round(starCols.Sum(c => c.ActualWidth));
        var baseW = total / starCols.Count;
        var extra = total % starCols.Count;

        // Star-вес == целочисленным пикселям: сумма весов равна доступной ширине,
        // каждая колонка получает ровно weight px, ActualWidth выходит целым,
        // следующий проход LayoutUpdated уже не видит дробного — снап сходится.
        for (var i = 0; i < starCols.Count; i++)
            starCols[i].Width = new DataGridLength(baseW + (i < extra ? 1 : 0),
                                                   DataGridLengthUnitType.Star);
    }

    // ── Утилиты ────────────────────────────────────────────────────────────────

    private static bool IsTruthy(string v) => v is "1" or "true" or "True";

    private static IReadOnlyList<string> ParseSelectOptions(DbField field)
    {
        try
        {
            using var doc = JsonDocument.Parse(field.ConfigJson ?? "{}");
            if (doc.RootElement.TryGetProperty("options", out var opts) &&
                opts.ValueKind == JsonValueKind.Array)
                return opts.EnumerateArray().Select(o => o.ToString()).ToList();
        }
        catch { }
        return [];
    }
}

// ── Конвертеры ─────────────────────────────────────────────────────────────────

internal sealed class EmptyTitleConverter : IValueConverter
{
    public static readonly EmptyTitleConverter Instance = new();

    public object? Convert(object? v, Type t, object? p, CultureInfo c) =>
        string.IsNullOrWhiteSpace(v as string) ? "Без названия" : v;

    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        v?.ToString() ?? "";
}

internal sealed class FieldDisplayConverter(string fieldType) : IValueConverter
{
    public object? Convert(object? v, Type t, object? p, CultureInfo c)
    {
        var s = v?.ToString() ?? "";
        return fieldType switch
        {
            "checkbox" => s is "1" or "true" or "True" ? "✓" : "",
            "date"     => DateValue.TryParse(s, out var dv) ? dv.ToDisplay() : s,
            _          => s,
        };
    }

    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        v?.ToString() ?? "";
}
