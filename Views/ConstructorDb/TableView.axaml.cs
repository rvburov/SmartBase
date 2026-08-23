using System.ComponentModel;
using Avalonia.Controls.Documents;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Collections;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Styling;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.ViewModels.ConstructorDb;
using SmartBase.Utils;
using SmartBase.Controls.Behaviors;
using SmartBase.Controls.Components;

namespace SmartBase.Views.ConstructorDb;

public partial class TableView : UserControl
{
    // ── Публичные события ─────────────────────────────────────────────────────
    public event EventHandler<int>? RecordClicked;
    // Переход к записи в ДРУГОЙ БД (чип межбазовой связи): (DbId цели, Id записи).
    public event EventHandler<(int DbId, int RecordId)>? CrossDbRecordRequested;
    // Значение записи изменено прямо в таблице (правка ячейки: имя/описание/поле). Аргумент —
    // RecordId. MainWindow обновляет открытую панель «Страница записи», если это та же запись.
    public event EventHandler<int>? RecordValueEdited;
    // Удаление записей из таблицы (одной или мультивыделения: Shift/Ctrl-клик → меню
    // «Удалить» или клавиша Delete) — всегда пакетно, с подтверждением; для одной записи
    // список из одного id (как удаление свойств).
    public event EventHandler<IReadOnlyList<int>>? RecordsDeleteRequested;
    // Дубль записей: список id (как удаление). Одна запись из меню строки — список из одного id;
    // если строка входит в мультивыделение — дублируется весь набор.
    public event EventHandler<IReadOnlyList<int>>? RecordDuplicateRequested;
    // «Расположение»: перенести записи в другую коллекцию/БД (список id, как дубль/удаление).
    // Одна запись из меню строки — список из одного id; если строка в мультивыделении — весь набор.
    public event EventHandler<IReadOnlyList<int>>? RecordRelocateRequested;
    // Open = открывать ли запись справа после создания. «+»-строка создает без
    // открытия (false), контекстное «Создать запись» — с открытием (true).
    // AfterId = id записи-инициатора (меню строки) — создать сразу после нее; null —
    // создать в конце («+»-строка и меню пустой области таблицы).
    public event EventHandler<(bool Open, int? AfterId)>? RecordCreateRequested;
    public event EventHandler?      PropertyCreateRequested;
    // Создать свойство сразу справа от колонки (string = ключ колонки-инициатора).
    public event EventHandler<string>? PropertyCreateAfterRequested;
    public event EventHandler<int>? PropertyEditRequested;
    // Дублирование свойств (одного или мультивыделения колонок: меню «Дублировать») — всегда
    // пакетно, как удаление; для одного поля список из одного id.
    public event EventHandler<IReadOnlyList<int>>? PropertyDuplicateRequested;
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
    // MinColWidth — СТАНДАРТНАЯ ширина движимой колонки: минимум авто-режима и ширина
    // колонки, для которой ручная ширина еще не задана. Ручное перетаскивание опускается
    // ниже — до MinManualColWidth (иначе «уменьшить столбец» было бы невыполнимо).
    private const int MinColWidth       = 160;
    private const int MinManualColWidth = 48;
    private const int NumColWidth       = 36;
    private const int PlusColWidth      = 36;

    // ── Состояние ─────────────────────────────────────────────────────────────
    private ConstructorRepository? _repository;
    private int  _collectionId;
    private bool _manualColResize;
    private bool _manualRowResize;
    private bool _snapPending;
    private List<DbField>        _fields  = [];
    private List<TableColumnDef> _colDefs = [];
    private VirtualRowList?      _virtualRows;
    // ВИДИМЫЙ список строк: = _virtualRows без фильтра/сортировки, = временный filteredRows при них.
    // Точечные обновления (RefreshRecordRow) адресуем ему — иначе в фильтр-режиме они били бы по
    // скрытому _virtualRows и не доходили до видимых строк.
    private VirtualRowList?      _activeRows;
    // Кеш связей-relation таблицы: батч видимой страницы вместо N точечных GetLinks. Общий для
    // виртуального списка (пишет при наполнении страниц) и ячеек-связей (читают). Чистится при смене
    // источника строк (ReloadRows/фильтр). Стабильный экземпляр — шаблоны ячеек ссылаются на него.
    private readonly RelationLinksCache _relLinks = new();
    // Виртуализирующая обертка над _virtualRows: кладется в ItemsSource — DataGrid берет ее
    // как IDataGridCollectionView без материализации и тянет только видимые строки.
    // Виртуализация всегда включена (часть программы), переключателя нет.
    private VirtualCollectionView? _virtualView;
    // Источник по умолчанию для грида (без активного фильтра/сортировки).
    private System.Collections.IEnumerable? DefaultItemsSource =>
        (System.Collections.IEnumerable?)_virtualView ?? _virtualRows;
    // Пользовательские подписи системных колонок: ключ → отображаемое имя.
    private Dictionary<string, string> _systemNames = [];
    // Сохраненный порядок колонок: ключ → позиция (пусто, если еще не двигали).
    private Dictionary<string, int> _columnOrder = [];
    // Скрытые колонки (окно «Отображение»): ключ есть в множестве ⇒ колонка скрыта.
    // Колонки из _colDefs не удаляются (порядок/перемещение целы) — гасим через IsVisible.
    private HashSet<string> _hiddenColumns = [];
    // Скрытые записи (окно «Отображение записей»): id есть в множестве ⇒ строка скрыта.
    // Применяется на уровне SQL (excludeHidden в запросах страницы/фильтра), виртуализация цела.
    private HashSet<int> _hiddenRecords = [];
    // Выделенные по клику в заголовок колонки (голубая заливка ячеек — как у выделенной
    // записи). Пусто — нет выделения. Обычный клик — одна колонка; Shift+клик — toggle.
    private readonly List<string> _selectedColumnKeys = [];

    // ── Фильтр/сортировка колонок (Excel-стиль) ───────────────────────────────
    // key колонки → набор разрешенных значений; null-наличие ключа = фильтра нет.
    private readonly Dictionary<string, HashSet<string>> _filters = [];
    // Шевроны шапки: ключ колонки → Path, чтобы подсвечивать активный фильтр.
    private readonly Dictionary<string, Avalonia.Controls.Shapes.Path> _chevronPaths = [];
    private string? _sortKey;
    private bool    _sortAsc = true;

    // ── Цвет строк, ячеек и текста ────────────────────────────────────────────
    // Локальное оформление ЭТОЙ коллекции: recordId → стиль строки, ключ колонки → стиль
    // колонки. Обе таблицы разрежены (строка только у покрашенного), поэтому читаются
    // целиком при загрузке — как высоты строк. Оформление ячеек живет в самих строках
    // (TableRowVm.CellStyles): оно приезжает постранично вместе с данными.
    private Dictionary<int, RowStyle>    _rowStyles    = [];
    private Dictionary<string, RowStyle> _columnStyles = [];
    // Ключ колонки последнего правого клика по ячейке: своего меню у ячейки нет (его открывает
    // строка), поэтому пункты «Фон ячейки»/«Цвет текста ячейки» узнают адресата отсюда.
    private string? _menuCellKey;

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

    // ── Ручная ширина колонок ─────────────────────────────────────────────────
    // ключ колонки → ширина (DIP). МОДЕЛЬ, а не снимок раскладки: меняется только жестом
    // пользователя, ресайз окна ее не трогает — поэтому «развернул окно и свернул обратно»
    // возвращает ту же картинку. Пусто = ручных ширин нет (колонки делят ширину поровну).
    private Dictionary<string, double> _colWidths = [];
    // Построенные ячейки шапки (ключ → контрол): от них поднимаемся к DataGridColumnHeader,
    // чтобы узнать положение правой границы колонки. OwningColumn у заголовка internal.
    private readonly Dictionary<string, Control> _headerHosts = [];

    private static readonly Cursor ColResizeCursor = new(StandardCursorType.SizeWestEast);
    private const double ColGripThickness = 8;     // ширина прозрачной полоски-захвата
    private bool _colHandlesPending;               // отложенная перестройка полосок
    // Состояние перетаскивания правой границы колонки (одно за раз).
    private bool    _isResizingCol;
    private string? _resizeColKey;
    private double  _resizeStartX;                 // в координатах ColResizeLayer
    private double  _resizeStartWidth;
    private double  _resizeStartHOffset;           // гориз. прокрутка на старте жеста
    private double  _resizeStartGripLeft;          // позиция полоски на старте жеста
    private double  _resizeFill;                   // ширина, при которой таблица ровно заполнена
    private Dictionary<string, double> _resizeBase = [];   // снимок модели на старте жеста
    private List<(string Key, double X, double Top, double H)> _lastColGrips = [];
    // Презентер шапки: по нему меряем доступную под колонки ширину (у него нет полосы
    // вертикальной прокрутки, в отличие от самого грида).
    private DataGridColumnHeadersPresenter? _headersPresenter;

    public TableView()
    {
        InitializeComponent();
        // Фокусируемый контрол: при Shift/Ctrl-выделении строк уводим фокус сюда из
        // текстовых полей (например, правой панели открытой записи), иначе Delete будет
        // трактоваться как «удалить символ», а не как удаление выделенных записей.
        Focusable = true;
        RecordsGrid.CellPointerPressed += OnCellPointerPressed;
        RecordsGrid.LoadingRow         += OnLoadingRow;
        // Сбрасываем адресата пунктов «цвет ячейки» ДО того, как его поставит сама ячейка:
        // туннелирование идет сверху вниз, поэтому этот обработчик всегда раньше. Иначе
        // правый клик по «№» или по пустой области открывал бы меню со старой ячейкой.
        RecordsGrid.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(RecordsGrid).Properties.IsRightButtonPressed) _menuCellKey = null;
        }, RoutingStrategies.Tunnel);
        // Snap дробных Star-ширин до целочисленных весов — без этого DataGrid
        // не рисует вертикальные грид-линии в сабпиксельных позициях. Снап сходится
        // за один шаг (Σ весов = доступная ширина → каждая колонка == вес px).
        RecordsGrid.LayoutUpdated      += OnGridLayoutUpdated;
        // Открыт ли редактор ячейки — фоновая синхронизация чужих правок (ChangePoller)
        // не обновляет строки, пока пользователь печатает, чтобы не затирать ввод.
        // Заодно точка отказа: без права изменения записей правку отменяем и объясняем
        // причину (сетка не read-only, иначе двойной клик молчал бы).
        RecordsGrid.BeginningEdit += (_, e) =>
        {
            if (!Can(PermAction.EditRecord))
            {
                e.Cancel = true;
                PermissionNotice.Show(this, PermAction.EditRecord);
                return;
            }
            IsCellEditing = true;
        };
        RecordsGrid.CellEditEnded      += (_, _) => IsCellEditing = false;
        // Delete по выделенным колонкам. Tunnel — чтобы перехватить раньше, чем
        // DataGrid обработает клавишу для редактирования/навигации.
        AddHandler(KeyDownEvent, OnTableKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Пользователь сейчас редактирует ячейку (открыт cell-editor DataGrid).</summary>
    public bool IsCellEditing { get; private set; }

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

        // Размер шрифта меняется живьем: ячейки строятся с размером из ресурса → пересобрать.
        FontScale.Changed += OnFontScaleChanged;
        if (_repository is not null && _renderedFontDelta != FontScale.Delta) Reload();

        // Токены толщины пересчитаны (зум/переезд на монитор с другим DPI): пересадить
        // границы колонок на новую физическую сетку. Линии заголовков обновляются сами
        // (GetResourceObservable в WrapHeaderContent), нативные линии — через DynamicResource.
        LineMetrics.Changed += OnLineMetricsChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keyHookTopLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _keyHookTopLevel = null;
        FontScale.Changed -= OnFontScaleChanged;
        LineMetrics.Changed -= OnLineMetricsChanged;
        base.OnDetachedFromVisualTree(e);
    }

    // Сменился зум или DPI: квант физического пиксела стал другим — переквантовать базу
    // ручных ширин под новую сетку (ApplyColumnWidths) и пересадить границы на нее (снап).
    private void OnLineMetricsChanged()
    {
        if (_repository is null) return;
        ApplyColumnWidths();
        SnapColumnWidths();
    }

    // Дельта последнего рендера — чтобы перерисовать таблицу при возврате, если размер
    // меняли, пока был открыт другой раздел.
    private int _renderedFontDelta = FontScale.Delta;
    private void OnFontScaleChanged() { if (_repository is not null) Reload(); }

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
            // («__title__») и №/+ не парсятся в int → отсекаются.
            var fieldIds = _selectedColumnKeys
                .Where(k => int.TryParse(k, out _))
                .Select(int.Parse)
                .ToList();
            if (fieldIds.Count == 0) return;

            // Перед показом диалога оставляем рамки только на удаляемых колонках:
            // с системных («Название»/«Описание») обводка снимается — их удаление не коснется.
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

    // Право текущего пользователя в открытой коллекции (нет сервиса прав — без ограничений).
    // Меню читают его при открытии, поэтому смена прав видна со следующего клика.
    private bool Can(PermAction a) => _repository?.Permissions?.Can(a, _collectionId) ?? true;

    // Запрещенное действие НЕ гасим, а объясняем: пункт остается кликабельным и по клику
    // показывает причину («Недостаточно прав: удаление записей запрещено администратором
    // базы данных») — как замок у коллекции без права просмотра. Молчаливо серый пункт
    // выглядел как поломка: непонятно, кто и что запретил.
    private void Guarded(MenuItem item, PermAction action, Action run) =>
        item.Click += (_, _) => PermissionNotice.Run(this, Can(action), action, run);

    public void Load(ConstructorRepository repository, int collectionId)
    {
        var sameCollection = _lastLoadedCollectionId == collectionId;

        _repository   = repository;
        _collectionId = collectionId;

        // Правку без права изменения записей отменяет BeginningEdit (там же — окно
        // с причиной): read-only сетка молча игнорировала бы двойной клик, и человек
        // не понимал бы, почему ячейка не открывается.
        RecordsGrid.IsReadOnly = false;

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
    // offset живет во внутреннем поле и доступен через internal-свойство
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
    // нет. Абсолютная прокрутка идет через _vScrollBar.Value + ProcessVerticalScroll
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
    // не сразу, поэтому ждем через LayoutUpdated, пока Maximum дорастет до offset,
    // затем выставляем Value и применяем через ProcessVerticalScroll (ThumbTrack).
    public void RestoreVerticalOffset(double offset)
    {
        if (offset <= 0 || VScrollBarProp is null || ProcessVScrollMethod is null) return;

        // Копии в ненулевые локальные переменные: проверка выше не «доезжает» внутрь Handler —
        // он локальная функция, и компилятор не берется утверждать, что поля не изменятся
        // между ее вызовами. Обращаться к полям напрямую пришлось бы через «!».
        PropertyInfo scrollBarProp  = VScrollBarProp;
        MethodInfo   processVScroll = ProcessVScrollMethod;

        var attempts = 0;

        void Handler(object? _, EventArgs __)
        {
            attempts++;
            if (scrollBarProp.GetValue(RecordsGrid) is not ScrollBar sb)
            {
                if (attempts > 20) RecordsGrid.LayoutUpdated -= Handler;
                return;
            }

            // Контент еще не дорос до нужного offset — ждем следующий layout-проход.
            if (sb.Maximum < offset && attempts <= 20) return;

            sb.Value = Math.Min(offset, sb.Maximum);
            processVScroll.Invoke(RecordsGrid, [ScrollEventType.ThumbTrack]);
            RecordsGrid.LayoutUpdated -= Handler;
        }

        RecordsGrid.LayoutUpdated += Handler;
    }

    // ── Навигация по строкам из нижней панели («‹‹ ‹ 10 – 50 из 125 437 › ››») ──────
    //
    // Кнопки двигают ту же вертикальную полосу, что и колесо мыши: у DataGrid она
    // пиксельная (Value/Maximum/ViewportSize в пикселях), поэтому «экран» = ViewportSize,
    // «в конец» = Maximum. Арифметика — в TableViewportScroll (покрыта тестами), здесь
    // только доступ к полосе и применение результата.

    /// <summary>Видимый кусок таблицы изменился (прокрутка, ресайз, перезагрузка строк).</summary>
    public event EventHandler<TableViewportRange>? VisibleRangeChanged;

    // Последний отданный наверх диапазон — событие поднимаем только на изменение
    // (пересчет идет из LayoutUpdated, который дергается на каждый проход разметки).
    private TableViewportRange _lastVisibleRange = TableViewportRange.Empty;

    private ScrollBar? VerticalScrollBar => VScrollBarProp?.GetValue(RecordsGrid) as ScrollBar;

    /// <summary>Экран назад (кнопка «‹»).</summary>
    public void ScrollRowsPageBack() =>
        ApplyVerticalScroll(sb => TableViewportScroll.PageBack(sb.Value, sb.ViewportSize, sb.Maximum));

    /// <summary>Экран вперед (кнопка «›»).</summary>
    public void ScrollRowsPageForward() =>
        ApplyVerticalScroll(sb => TableViewportScroll.PageForward(sb.Value, sb.ViewportSize, sb.Maximum));

    /// <summary>В начало таблицы (кнопка «‹‹»).</summary>
    public void ScrollRowsToStart() =>
        ApplyVerticalScroll(_ => TableViewportScroll.ToStart());

    /// <summary>В конец таблицы (кнопка «››»).</summary>
    public void ScrollRowsToEnd() =>
        ApplyVerticalScroll(sb => TableViewportScroll.ToEnd(sb.Maximum));

    // Ставит полосу в посчитанную позицию и применяет ее к строкам. ThumbTrack —
    // та же ветка, что и у перетаскивания ползунка: инвалидирует measure, поэтому
    // виртуализация подтягивает страницы новой позиции (см. RestoreVerticalOffset).
    private void ApplyVerticalScroll(Func<ScrollBar, double> position)
    {
        if (VerticalScrollBar is not { } sb || ProcessVScrollMethod is null) return;

        var target = position(sb);
        if (Math.Abs(target - sb.Value) < 0.5) return;

        sb.Value = target;
        ProcessVScrollMethod.Invoke(RecordsGrid, [ScrollEventType.ThumbTrack]);
        RefreshVisibleRange();
    }

    /// <summary>
    /// Номера строк, которые сейчас в окне, и общее число строк среза.
    ///
    /// Считаем по РЕАЛИЗОВАННЫМ строкам DataGrid, а не делением offset на высоту строки:
    /// высота строк бывает ручной (разная у каждой), и деление врало бы. Строка попадает
    /// в диапазон, если хоть частью пересекает область строк (RowResizeLayer лежит ровно
    /// над ней). Строка «+» (add-row) — не запись, ее пропускаем.
    /// </summary>
    public TableViewportRange VisibleRowRange()
    {
        // Count у списка = записи + строка «+».
        var total = Math.Max(0, (_activeRows?.Count ?? 0) - 1);
        if (total == 0) return TableViewportRange.Empty;

        var layer  = RowResizeLayer;
        var height = layer.Bounds.Height;
        if (height <= 0) return TableViewportRange.Of(0, 0, total);

        var first = 0;
        var last  = 0;
        foreach (var rowCtrl in RecordsGrid.GetVisualDescendants().OfType<DataGridRow>())
        {
            if (rowCtrl.DataContext is not TableRowVm vm || vm.IsAddRow) continue;
            if (vm.RowNumber <= 0 || rowCtrl.Bounds.Height <= 0) continue;

            var topLeft = rowCtrl.TranslatePoint(new Point(0, 0), layer);
            if (topLeft is null) continue;

            var top    = topLeft.Value.Y;
            var bottom = top + rowCtrl.Bounds.Height;
            if (bottom <= 0 || top >= height) continue;   // целиком выше или ниже окна

            if (first == 0 || vm.RowNumber < first) first = vm.RowNumber;
            if (vm.RowNumber > last) last = vm.RowNumber;
        }

        return TableViewportRange.Of(first, last, total);
    }

    // Пересчитывает диапазон и поднимает событие, только если он изменился.
    private void RefreshVisibleRange()
    {
        var range = VisibleRowRange();
        if (range == _lastVisibleRange) return;

        _lastVisibleRange = range;
        VisibleRangeChanged?.Invoke(this, range);
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
    // для прокрутки к правому краю передаем double.MaxValue (setter клампит до реального
    // максимума, останавливаемся, когда offset перестает расти). Отписываемся, когда
    // цель достигнута, offset вышел на плато (уперлись в максимум) или исчерпали попытки.
    private void ApplyHorizontalOffsetDeferred(double offsetX)
    {
        if (HOffsetProp is null || UpdateHOffsetMethod is null) return;

        // См. RestoreVerticalOffset: копии ради того, чтобы Handler видел их ненулевыми.
        PropertyInfo hOffsetProp     = HOffsetProp;
        MethodInfo   updateHOffset   = UpdateHOffsetMethod;

        // Отменяем предыдущее незавершенное восстановление (его offset устарел).
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
            updateHOffset.Invoke(RecordsGrid, [offsetX]);
            var actual = hOffsetProp.GetValue(RecordsGrid) is double a ? a : 0;

            if (actual > 0.5) grew = true;

            // Плато: offset перестал расти между проходами. Учитываем только после того,
            // как он реально вырос (grew) — иначе нулевой offset на ранних проходах (ширины
            // еще не готовы) приняли бы за «уперлись в максимум» и остановились бы на нуле.
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
        _colWidths = [];
        _headerHosts.Clear();
        _isResizingCol = false;
        _resizeColKey  = null;
        _lastColGrips.Clear();
        ColResizeLayer.Children.Clear();
        _headersPresenter = null;
        RecordsGrid.ItemsSource = null;
        RecordsGrid.Columns.Clear();
    }

    public void SetColResizeMode(bool manual)
    {
        _manualColResize = manual;

        // Выключение ручного режима сбрасывает все сохраненные ширины на стандарт
        // (как сброс высот при переходе на стандартную высоту строк).
        if (!manual)
        {
            _colWidths.Clear();
            _repository?.ClearColumnWidths(_collectionId);
        }

        // Запоминаем режим, чтобы он сохранялся между сессиями (раньше управлялся
        // из футера без персиста; теперь — из меню заголовка).
        if (_repository is not null)
        {
            var (_, rowResize, viewMode) = _repository.GetUiSettings(_collectionId);
            _repository.SaveUiSettings(_collectionId, manual, rowResize, viewMode);
        }

        // Колонки не пересобираем: полоски-захваты у границ наши (ColResizeLayer), их
        // достаточно перестроить. Включение режима вид таблицы НЕ меняет — ширины
        // появятся в модели только с первым перетаскиванием.
        if (!manual)
        {
            _lastColGrips.Clear();
            ColResizeLayer.Children.Clear();
        }
        ApplyColumnWidths();
    }

    // Текущий режим ширины столбцов (для отметки активного пункта в меню).
    public bool IsManualColResize => _manualColResize;

    // Переключение режима высоты строк (вызывается из меню записи). Запоминаем режим
    // между сессиями — как и режим ширины столбцов (раньше управлялся из футера).
    public void SetRowResizeMode(bool manual)
    {
        _manualRowResize = manual;

        // Выключение ручного режима сбрасывает все сохраненные высоты на стандарт
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

        // Применяем/сбрасываем высоту у уже ОТРИСОВАННЫХ строк на месте — без пересоздания
        // виртуального списка (как при сдвиге строки): нет мелькания «Загрузка…». Невидимые
        // строки получат правильную высоту в OnLoadingRow при реализации.
        ApplyRowHeightsToRealizedRows();
        ScheduleRowHandlesRefresh();   // показать (ручной) / убрать (стандартный) полоски ресайза
    }

    // Проставляет высоту уже реализованным строкам по текущему режиму/словарю (та же логика, что
    // в OnLoadingRow): ручной режим + сохраненная высота → она, иначе double.NaN (стандартная/авто).
    private void ApplyRowHeightsToRealizedRows()
    {
        foreach (var rowCtrl in RecordsGrid.GetVisualDescendants().OfType<DataGridRow>())
        {
            if (rowCtrl.DataContext is not TableRowVm vm || vm.IsAddRow) continue;
            rowCtrl.Height = _manualRowResize && _rowHeights.TryGetValue(vm.RecordId, out var h)
                ? h
                : double.NaN;
        }
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

    // Полная пересборка: и КОЛОНКИ, и строки. Тяжелая (при 1000 свойств строит 1000
    // DataGridColumn с шаблонами/заголовками/меню) — вызывать только когда реально
    // изменился НАБОР колонок: открытие коллекции, создание/удаление/смена типа свойства.
    // Для операций со строками есть ReloadRows — там колонки не трогаются (нет «моргания»
    // шапки и тяжелой пересборки), что и нужно при больших объемах.
    private void Reload()
    {
        if (_repository is null) return;

        _renderedFontDelta = FontScale.Delta;   // таблица отрисована с текущим размером

        LoadColumnState();

        ClearColumnSelection();   // колонки пересобираются — снимаем выделение

        _colDefs = BuildColDefs();
        ColumnsBuilt?.Invoke(this, _colDefs);

        BuildColumns();
        RecordsGrid.ContextMenu = BuildGridContextMenu();
        ApplyColumnWidths();

        ReloadRows();   // источник строк + фильтры + полоски ресайза (общий путь со строковыми операциями)
    }

    // Пересобирает ТОЛЬКО строки: новый источник данных, фильтры, высоты — НЕ трогая набор
    // колонок. Дешево при 1000 свойств: DataGrid не пересобирает заголовки/шаблоны колонок
    // (нет «моргания» шапки), перестраиваются лишь видимые строки (виртуализация). Вызывать
    // на операциях, меняющих набор/порядок ЗАПИСЕЙ (создание/удаление/дубль/сдвиг записи,
    // видимость записей) и при переключении ручной высоты строк. _fields берется из последнего
    // полного Reload — для строковых операций он не меняется.
    public void ReloadRows()
    {
        if (_repository is null) return;

        _hiddenRecords = _repository.GetRecordDisplay(_collectionId)
            .Where(kv => !kv.Value.Visible)
            .Select(kv => kv.Key)
            .ToHashSet();
        _rowHeights = _repository.GetRowHeights(_collectionId);
        _rowStyles  = _repository.GetRowStyles(_collectionId);

        _relLinks.Clear();   // новый источник строк — прежний прогрев связей неактуален
        _virtualRows = new VirtualRowList(_repository, _collectionId, _fields,
                                         excludeHidden: _hiddenRecords.Count > 0, relLinks: _relLinks);
        _virtualRows.RecordEdited += (_, rid) => RecordValueEdited?.Invoke(this, rid);
        // Виртуализирующая обертка: DataGrid не материализует источник, тянет только видимые
        // строки → большие коллекции открываются мгновенно. Всегда включена.
        _virtualView = new VirtualCollectionView(_virtualRows);
        _activeRows  = _virtualRows;   // без фильтра видимый список — он (ApplyFilters заменит при фильтре)

        RecordsGrid.ItemsSource = DefaultItemsSource;

        ApplyFilters();                // переустановит ItemsSource, если фильтр/сортировка активны
        ScheduleRowHandlesRefresh();   // полоски ресайза строк (если ручной режим)
    }

    // Структурное обновление строк (создание/удаление/дубль записей) БЕЗ пересоздания виртуального
    // списка и нового ItemsSource: число строк и видимые страницы обновляются на месте (теплый кеш),
    // затем мягкий Reset — грид перерисовывается сразу из кеша, без заглушек «Загрузка…» (это и есть
    // источник мелькания). При активном фильтре/сортировке нужен полный ReloadRows (пересобрать список
    // id) — туда и падаем; и при первом построении, когда списка еще нет.
    public void ReloadRowsStructural()
    {
        if (_repository is null) return;
        if (_virtualRows is null || _filters.Count > 0 || _sortKey is not null) { ReloadRows(); return; }

        _virtualRows.RefreshStructural();
        ScheduleRowHandlesRefresh();
    }

    // Пересобирает ТОЛЬКО колонки, СОХРАНЯЯ строки (источник _virtualRows не пересоздается).
    // Для операций, меняющих набор/порядок КОЛОНОК (сдвиг, удаление/дубль поля, «Отображение»,
    // создание после): строки не сбрасываются в заглушки «Загрузка…» → нет «белого» мелькания
    // на больших коллекциях. Полная пересборка колонок надежна (в отличие от точечной
    // перестановки/удаления, которые Avalonia DataGrid не отрабатывал). Горизонтальную
    // прокрутку держим на месте (Columns.Clear сбрасывает offset в 0).
    public void ReloadColumns()
    {
        if (_repository is null) return;

        var keepH = CurrentScrollOffset();

        LoadColumnState();
        ClearColumnSelection();   // колонки пересобираются — снимаем выделение

        _colDefs = BuildColDefs();
        ColumnsBuilt?.Invoke(this, _colDefs);

        BuildColumns();
        RecordsGrid.ContextMenu = BuildGridContextMenu();
        ApplyColumnWidths();

        if (keepH > 0) RestoreScrollOffset(keepH);
    }

    // Состояние, нужное для построения КОЛОНОК (поля, подписи системных, порядок, скрытые).
    // Вынесено из Reload, чтобы переиспользовать в точечных операциях со свойствами.
    private void LoadColumnState()
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
        _columnStyles = _repository.GetColumnStyles(_collectionId);
        // Ручные ширины (аспект column_meta.Width). Читаются здесь, потому что LoadColumnState
        // вызывается из всех путей пересборки колонок — отдельной точки чтения не нужно.
        _colWidths = _repository.GetColumnWidths(_collectionId);
    }

    // ── Точечные изменения набора колонок (без полной пересборки) ──────────────
    // RecordsGrid.Columns[i] выровнен 1:1 с _colDefs[i] (см. BuildColumns). Поэтому
    // вставку/удаление/замену ОДНОЙ колонки делаем по индексу — без Columns.Clear() и
    // пересборки всех DataGridColumn: нет «моргания» шапки и тяжелой работы при 1000 свойств.
    // Везде есть безопасный фоллбек на полную пересборку, если набор/порядок разошелся.

    // Создание/«создать после»/дубль свойства: порядок в БД уже выставлен контроллером
    // (AddField/MoveColumnAfter), здесь — одна вставка на ее место в порядке.
    public void AddColumnForField(int fieldId)
    {
        if (_repository is null) return;

        LoadColumnState();
        var newDefs = BuildColDefs();
        var idx     = newDefs.FindIndex(d => d.Key == fieldId.ToString());

        // Норма: добавилась ровно одна колонка, относительный порядок остальных не менялся.
        if (idx < 0 || newDefs.Count != RecordsGrid.Columns.Count + 1)
        {
            _colDefs = newDefs;
            BuildColumns();              // фоллбек: что-то разошлось — полная пересборка
            ApplyColumnWidths();
            return;
        }

        _colDefs = newDefs;
        RecordsGrid.Columns.Insert(idx, CreateDataGridColumn(newDefs[idx]));
        ColumnsBuilt?.Invoke(this, _colDefs);
        ApplyColumnWidths();
    }

    // Изменение свойства (имя/тип/опции) или переименование системного заголовка: позиция
    // не меняется → заменяем одну колонку на месте (мелькнет только она, не вся таблица).
    public void RebuildColumn(string key)
    {
        if (_repository is null) return;

        LoadColumnState();
        var newDefs = BuildColDefs();
        var idx     = newDefs.FindIndex(d => d.Key == key);

        if (idx < 0 || newDefs.Count != RecordsGrid.Columns.Count || idx >= RecordsGrid.Columns.Count)
        {
            _colDefs = newDefs;
            BuildColumns();              // фоллбек: набор/порядок изменился
            ApplyColumnWidths();
            return;
        }

        _colDefs = newDefs;
        RecordsGrid.Columns.RemoveAt(idx);
        RecordsGrid.Columns.Insert(idx, CreateDataGridColumn(newDefs[idx]));
        ColumnsBuilt?.Invoke(this, _colDefs);
        ApplyColumnWidths();
    }

    private List<TableColumnDef> BuildColDefs()
    {
        // Движимые колонки: системное «Название» и пользовательские поля (в т.ч. «Описание» —
        // оно обычное поле). Порядок — по эффективной позиции (из column_order, иначе дефолт).
        // № и «+» закреплены.
        var movable = new List<TableColumnDef>
        {
            new() { Key = TableRowVm.TitleKey, Header = SystemHeader(TableRowVm.TitleKey),
                    FieldType = "text",  IsStarWidth = true, MinWidth = MinColWidth,
                    BuildHeaderMenu = _ => BuildSystemHeaderContextMenu(TableRowVm.TitleKey) },
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
                // Чекбоксы, «файл» и «вычисляемые» типы (дата/автор создания-изменения) правятся не
                // текстовым редактором — read-only на уровне колонки (CellEditingTemplate не строится);
                // их собственный редактор живет прямо в ячейке показа.
                IsReadOnly  = f.FieldType is "checkbox" or "file" || FieldTypeInfo.IsComputed(f.FieldType),
                // «Пользователь» — как select, только опции живые: допущенные пользователи
                // базы. Снимок на момент построения колонок; редактор ячейки перечитывает
                // их сам (состав пользователей меняется, пока таблица открыта).
                Options     = f.FieldType == "user"
                    ? _repository?.GetUserOptions() ?? []
                    : ParseSelectOptions(f),
                // Формат склейки «Мультисписка» (запятая/пробел/«;»/перенос) — из ConfigJson поля.
                Separator   = MultiSelectValue.SeparatorFromConfig(f.ConfigJson),
                // Формат показа «Числа» (валюта/проценты/разряды) — из ConfigJson поля.
                NumberFormat = NumberFormatValue.FromConfig(f.ConfigJson),
                // Папка авто-привязки «Файла» (если правило задано) → ячейка read-only.
                FileAutoFolder = FileAutoRule.FromConfig(f.ConfigJson)?.Folder ?? "",
                // Настройка связи (целевая коллекция + one/many), если задана → ячейка рисует чипы.
                Relation    = f.FieldType == "relation" ? RelationConfig.FromConfig(f.ConfigJson) : null,
                Tag         = fid,
                BuildHeaderMenu = _ => BuildHeaderContextMenu(fid, f.IsRequired, f.Name, f.FieldType,
                    fileAuto: FileAutoRule.FromConfig(f.ConfigJson) is not null),
            });
        }

        // Стабильная сортировка по эффективной позиции (OrderBy сохраняет порядок
        // равных — важно, если у пары колонок совпали ключи дефолта).
        var ordered = movable.OrderBy(EffectiveColumnPosition).ToList();

        var defs = new List<TableColumnDef>
        {
            new() { Key = TableColumnDef.NumKey, Header = "№",
                    IsStarWidth = false, FixedWidth = NumColumnWidth(),
                    CanResize = false,   IsReadOnly = true },
        };
        defs.AddRange(ordered);
        defs.Add(new() { Key = TableColumnDef.AddPropKey, Header = "+",
                         IsStarWidth = false, FixedWidth = PlusColWidth,
                         CanResize = false,   IsReadOnly = true,
                         MaxWidth  = PlusColWidth });

        return defs;
    }

    // Ширина служебной колонки «№»: дефолт NumColWidth для коротких номеров, но
    // расширяется ровно под самый длинный номер (= числу записей), чтобы крупные
    // числа не обрезались. Фиксированная (не Auto) — иначе DataGrid сбивает экстент
    // горизонтального скролла, и последняя колонка «+» обрезается при прокрутке полосой.
    private double NumColumnWidth()
    {
        if (_repository is null) return NumColWidth;

        var maxRow = _repository.CountRecords(_collectionId, _hiddenRecords.Count > 0);
        var text   = Math.Max(1, maxRow).ToString(CultureInfo.InvariantCulture);

        var fontSize = (double)this.FindResource("FontSizeBase")!;
        var measured = new FormattedText(text, CultureInfo.InvariantCulture,
                                         FlowDirection.LeftToRight,
                                         new Typeface(this.FontFamily), fontSize, null).Width;

        // Поля по бокам, чтобы число не липло к границам колонки.
        return Math.Max(NumColWidth, Math.Ceiling(measured) + 16);
    }

    // Эффективная позиция движимой колонки: сохраненная (column_order) или дефолт.
    // Дефолт: Title=1000, Desc=2000, пользовательские = 3000 + DbFields.Position.
    private int EffectiveColumnPosition(TableColumnDef def)
    {
        if (_columnOrder.TryGetValue(def.Key, out var pos)) return pos;
        if (def.Key == TableRowVm.TitleKey) return 1000;
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

    // Перемещение колонки влево/вправо: пишем порядок в БД и двигаем ТОЛЬКО одну колонку
    // на месте (RemoveAt+Insert), не сбрасывая весь набор. Полная пересборка (BuildColumns →
    // Columns.Clear) на коллекции со множеством колонок и сотнями тысяч строк рушит все
    // визуальное дерево грида и дает «белое» мелькание на 1–2 сек, пока ячейки заново
    // материализуются. Сдвиг меняет позицию ровно одной колонки (своп с соседом), поэтому
    // переставляем ее точечно — как AddColumnForField/RebuildColumn. Строки не трогаем,
    // горизонтальную прокрутку держим на месте. Есть безопасный фоллбек на полную пересборку.
    private void MoveColumn(string key, bool left)
    {
        if (_repository is null) return;
        _repository.MoveColumn(_collectionId, key, left, MovableKeysInOrder());

        var keepH   = CurrentScrollOffset();
        var oldDefs = _colDefs;

        LoadColumnState();
        var newDefs = BuildColDefs();

        var oldIdx = oldDefs.FindIndex(d => d.Key == key);
        var newIdx = newDefs.FindIndex(d => d.Key == key);

        _colDefs = newDefs;
        ColumnsBuilt?.Invoke(this, _colDefs);
        ClearColumnSelection();          // колонки переставлены — снимаем выделение (как ReloadColumns)

        // Точечный сдвиг возможен, только если позиция изменилась ровно у этой колонки,
        // набор и относительный порядок прочих неизменны, а грид выровнен с _colDefs 1:1.
        var canTargeted =
            oldIdx >= 0 && newIdx >= 0 &&
            oldDefs.Count == newDefs.Count &&
            RecordsGrid.Columns.Count == oldDefs.Count &&
            SameOrderExcept(oldDefs, newDefs, key);

        if (canTargeted)
        {
            RecordsGrid.Columns.RemoveAt(oldIdx);
            RecordsGrid.Columns.Insert(newIdx, CreateDataGridColumn(newDefs[newIdx]));
        }
        else
        {
            BuildColumns();              // фоллбек: набор/порядок разошелся — полная пересборка
            RecordsGrid.ContextMenu = BuildGridContextMenu();
        }

        ApplyColumnWidths();
        if (keepH > 0) RestoreScrollOffset(keepH);
    }

    // true, если два набора колонок совпадают по ключам и порядку после исключения key
    // (т.е. сдвинулась ровно эта колонка, остальные на местах) — условие точечного сдвига.
    private static bool SameOrderExcept(
        IReadOnlyList<TableColumnDef> a, IReadOnlyList<TableColumnDef> b, string key) =>
        a.Where(d => d.Key != key).Select(d => d.Key)
         .SequenceEqual(b.Where(d => d.Key != key).Select(d => d.Key));

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

    // Перемещение записи вверх/вниз: пишем порядок в БД и обновляем уже загруженные строки НА МЕСТЕ
    // (Fill), без Reset — меняется только ПОРЯДОК записей, не их число, поэтому пересоздавать
    // виртуальный список не нужно. Так нет мелькания «Загрузка…» и прокрутка остается на месте сама.
    private void MoveRecord(int recordId, bool up)
    {
        if (_repository is null) return;

        _repository.MoveRecord(_collectionId, recordId, up);
        _virtualRows?.RefreshLoadedPages();   // порядок изменился — перечитываем видимые строки на месте

        // Выделение «следует» за сдвинутой записью. После пере-Fill строки остаются на своих ИНДЕКСАХ,
        // а запись переехала в соседний VM — поэтому переустанавливаем выделение на VM, который теперь
        // держит эту запись (иначе голубая подсветка осталась бы на прежней экранной позиции).
        var moved = _virtualRows?.FindCachedByRecordId(recordId);
        if (moved is not null)
        {
            RecordsGrid.SelectedItems.Clear();
            RecordsGrid.SelectedItems.Add(moved);
        }
    }

    // Точечное обновление ОДНОЙ строки (после правки записи в правой панели): перечитываем ее данные
    // и заполняем строку на месте, без пересборки таблицы. Полный Refresh на больших коллекциях с
    // множеством колонок давал тяжелую перезагрузку при каждой правке.
    public void RefreshRecordRow(int recordId)
    {
        // Снимаем активное редактирование ячейки. Редакторы даты/выбора задают значение императивно
        // (Storage/SelectedItem ставятся один раз) и НЕ обновляются по NotifyAllCells — открытый
        // редактор показывал бы устаревшее значение, а при коммите еще и затер бы свежее из панели.
        // CancelEdit отменяет (не коммитит) правку, после чего ячейка показывает биндинг-дисплей.
        RecordsGrid.CancelEdit();
        _activeRows?.RefreshRecord(recordId);
        // Строки, чьи связи ссылаются на измененную запись, тоже обновляем — иначе название цели в их
        // ячейке-связи оставалось бы старым до прокрутки/перезагрузки. Адресуем ВИДИМОМУ списку
        // (_activeRows), чтобы работало и в режиме фильтра/сортировки.
        _activeRows?.RefreshRelationTargets(recordId);
    }

    // Выделить голубым ровно заданные записи (по id), сняв прежнее выделение. Используется после
    // структурных операций (создание/дубль): после Reset грид мог «восстановить» выделение по индексам
    // на уже ДРУГИЕ записи (отсюда лишние/съехавшие подсветки) — мы переустанавливаем его явно на
    // результат операции. Подсветка появится у тех строк, что сейчас в кеше (видимые/прогретые).
    public void SelectRecords(IReadOnlyList<int> recordIds)
    {
        RecordsGrid.SelectedItems.Clear();
        if (_virtualRows is null) return;
        foreach (var id in recordIds)
            if (_virtualRows.FindCachedByRecordId(id) is { } row)
                RecordsGrid.SelectedItems.Add(row);
    }

    // Снять все выделение записей (после удаления — удаленные строки не должны оставаться «подсвеченными»).
    public void ClearRecordSelection() => RecordsGrid.SelectedItems.Clear();

    // Можно ли двигать запись вверх/вниз (не крайняя ли она в порядке коллекции).
    // O(1)-сик соседа: вызывается при открытии контекстного меню — никаких COUNT'ов.
    private bool CanMoveRecord(int recordId, bool up) =>
        _repository is not null && _repository.HasNeighborRecord(_collectionId, recordId, up);

    private void BuildColumns()
    {
        _chevronPaths.Clear();
        _headerHosts.Clear();
        RecordsGrid.Columns.Clear();

        foreach (var def in _colDefs)
            RecordsGrid.Columns.Add(CreateDataGridColumn(def));
    }

    private DataGridColumn CreateDataGridColumn(TableColumnDef def)
    {
        var width = def.IsStarWidth
            ? new DataGridLength(1, DataGridLengthUnitType.Star)
            : new DataGridLength(def.FixedWidth);

        // Ячейку шапки запоминаем: от нее поднимаемся к DataGridColumnHeader, когда нужно
        // положение правой границы колонки для полоски-захвата.
        var header = BuildHeaderCell(def);
        if (def.IsStarWidth) _headerHosts[def.Key] = header;

        var col = new DataGridTemplateColumn
        {
            Header         = header,
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
            // Размер «+» — как у «+» создания записи (add-row), т.е. стандартный FontSizeBase.
            var plus = MakeCenteredHeader("+", addRightBorder: false);
            plus.Cursor = new Cursor(StandardCursorType.Hand);
            plus.PointerPressed += (_, _) => PermissionNotice.Run(
                this, Can(PermAction.CreateField), PermAction.CreateField,
                () => PropertyCreateRequested?.Invoke(this, EventArgs.Empty));
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

    // Ячейка шапки строится КОНСТРУКЦИЕЙ 1-в-1 как шаблон DataGridCell: Grid "*,Auto",
    // контент в звездной колонке, вертикальная линия — Rectangle в Auto-колонке (как
    // PART_RightGridLine, см. TableViewDataGridThemes.axaml). Одинаковые слоты дают
    // одинаковое округление — линии заголовка совпадают с линиями строк по X и толщине
    // при любом зуме/DPI и не исчезают при смене числа/ширины колонок. Толщина и
    // UseLayoutRounding — живые токены LineMetrics через GetResourceObservable.
    private Control WrapHeaderContent(Control content, bool addRightLine)
    {
        var host = new Grid
        {
            Background          = Brushes.Transparent,   // hit-test всей области (клики/меню)
            ColumnDefinitions   = new ColumnDefinitions("*,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
        };

        Grid.SetColumn(content, 0);
        host.Children.Add(content);

        if (addRightLine)
        {
            var line = new Rectangle
            {
                Fill              = (IBrush)this.FindResource("BrushSeparator")!,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            line.Bind(Rectangle.WidthProperty,           line.GetResourceObservable("SeparatorThickness"));
            line.Bind(Layoutable.UseLayoutRoundingProperty, line.GetResourceObservable("LineUseLayoutRounding"));
            // Без сглаживания — как у нативных линий строк: на дробных позициях (зум ≠ 1)
            // линия защелкивается в целый пиксель, а не размазывается.
            Avalonia.Media.RenderOptions.SetEdgeMode(line, Avalonia.Media.EdgeMode.Aliased);
            Grid.SetColumn(line, 1);
            host.Children.Add(line);
        }
        return host;
    }

    private Control MakeCenteredHeader(string text, double fontSize = 0, bool addRightBorder = true) =>
        WrapHeaderContent(new Border
        {
            Background          = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
            Child = new TextBlock
            {
                Text                = text,
                FontSize            = fontSize > 0 ? fontSize : (double)this.FindResource("FontSizeBase")!,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Foreground          = (IBrush)this.FindResource("BrushSecondaryText")!,
            },
        }, addRightBorder);

    private Control MakeTextHeader(TableColumnDef def)
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

        // «Название» — единственное базовое свойство целостности: его нельзя удалить и сменить
        // тип. Его иконку типа красим красным, чтобы визуально выделять среди обычных свойств
        // (полезно, когда колонок много и легко перепутать). «Описание» — обычное поле и
        // рисуется как все остальные.
        var isSystemColumn = def.Key == TableRowVm.TitleKey;
        var iconStroke     = isSystemColumn ? (IBrush)this.FindResource("BrushRed")! : null;
        var typeIcon = BuildTypeIcon(def.FieldType, iconStroke);
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
            Child               = grid,
        };
        border.Classes.Add("colHeader");   // hover-фон через стиль (Assets/Styles/Controls.axaml)
        return WrapHeaderContent(border, addRightLine: true);
    }

    // Иконка типа свойства слева от названия колонки (серый цвет шапки). null —
    // если тип неизвестен (тогда колонка без иконки). stroke — цвет обводки: по умолчанию
    // серый как у шапки, для системных колонок передается красный (BrushRed).
    private Control? BuildTypeIcon(string? fieldType, IBrush? stroke = null)
    {
        var geo = Controls.Components.FieldTypeIcons.ForType(fieldType);
        if (geo is null) return null;

        return new Viewbox
        {
            Width             = IconSize("IconSizeSmall"),
            Height            = IconSize("IconSizeSmall"),
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
                        Stroke          = stroke ?? (IBrush)this.FindResource("BrushSecondaryText")!,
                        StrokeThickness = IconSize("IconStrokeSmall"),
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
            Content           = new Viewbox { Width = IconSize("IconSizeSmall"), Height = IconSize("IconSizeSmall"), Child = canvas },
        };
        btn.Classes.Add("icon");
        btn.Classes.Add("filterChevron");
        ToolTip.SetTip(btn, "Фильтр и сортировка");
        btn.Click += (_, _) => ShowFilterFlyout(btn, def);
        return btn;
    }

    // Иконки заголовка/меню фильтра — из общего словаря Assets/Styles/Icons.axaml.
    private static Geometry Icon(string key) =>
        Application.Current!.TryFindResource(key, out var res) && res is Geometry g
            ? g
            : throw new KeyNotFoundException($"Icon resource '{key}' not found in Icons.axaml");

    // Размер иконки (сторона Viewbox) — из токенов Icons.axaml.
    private static double IconSize(string key) =>
        Application.Current!.TryFindResource(key, out var r) && r is double d ? d : 16;

    private static Geometry ChevronIcon => Icon("IconChevronDown");
    private static Geometry FunnelIcon  => Icon("IconFilter");
    private static Geometry SortAzIcon  => Icon("IconSortAz");
    private static Geometry SortZaIcon  => Icon("IconSortZa");
    private static Geometry CheckIcon   => Icon("IconCheck");
    private static Geometry XIcon       => Icon("IconClose");

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

    // Шаблон ячейки = содержимое (inner) + заливка выделения колонки (кисть колонки на
    // ContentPresenter ячейки, AttachColumnFill) + слой пользовательского цвета (CellStyleHost).
    private IDataTemplate BuildCellTemplate(TableColumnDef def)
    {
        var inner = BuildCellContentTemplate(def);
        return new FuncDataTemplate<TableRowVm>((row, _) =>
        {
            var content = inner.Build(row);
            AttachColumnFill(content, def.Key);
            return def.Key is TableColumnDef.NumKey or TableColumnDef.AddPropKey
                ? content
                : CellStyleHost(content, def.Key);
        });
    }

    // ── Слой пользовательского цвета ячейки ───────────────────────────────────
    //
    // Заливка ложится на СВОЙ Border ВНУТРИ ContentPresenter'а, а не на его фон: там уже
    // живет заливка выделения колонки. Заливка ПОЛУПРОЗРАЧНА (ColorTools.Fill), поэтому
    // выделение колонки, выделение строки и наведение просвечивают сквозь цвет и
    // смешиваются с ним — вместо «цвет закрыл подсветку».
    //
    // Цвет ТЕКСТА ставится ЛОКАЛЬНЫМ значением самим надписям ячейки. Наследуемый
    // Foreground — со слоя, с DataGridCell, откуда угодно — до текста не доходит В ПРИНЦИПЕ:
    // цвет всех надписей приложения задан стилем «Style Selector="TextBlock"»
    // (Assets/Styles/Typography.axaml), а стиль в Avalonia приоритетнее унаследованного
    // значения. По той же причине не работает и штатный стиль «DataGridRow:selected
    // DataGridCell» — синим текст выделенной строки не становится. Локальное значение
    // приоритетнее стиля, поэтому цвет виден и на выделенной строке.
    //
    // Исходную кисть надписи запоминаем: у части ячеек цвет свой («вычисляемые» значения
    // приглушены), и сброс обязан вернуть именно ее, а не обнулить.
    //
    // Контейнеры ячеек переиспользуются виртуализацией, поэтому цвет пересчитывается на
    // КАЖДУЮ смену DataContext (пришла другая строка) и на «Item» строки (страница-заглушка
    // дозаполнилась данными — DataContext при этом не меняется, см. TableRowVm.Fill).
    private Border CellStyleHost(Control? content, string key)
    {
        var host = new Border
        {
            Child               = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
        };

        // Надписи, которым поставили цвет → их СВОЯ кисть (null — своей не было, цвет пришел
        // от глобального стиля, и сброс = ClearValue). «Своя» — это ровно LocalValue: IsSet
        // здесь не годится, он True и для стилевого значения, и сброс запирал бы стилевую
        // кисть локально. Словарь заводится только у покрашенной ячейки — у подавляющего
        // большинства так и остается null, лишней работы на строку нет.
        Dictionary<TextBlock, IBrush?>? painted = null;

        void PaintText(IBrush? fg)
        {
            if (fg is null)
            {
                if (painted is null) return;             // не красили — и снимать нечего
                foreach (var (text, original) in painted)
                {
                    if (original is null) text.ClearValue(TextBlock.ForegroundProperty);
                    else                  text.Foreground = original;
                }
                painted = null;
                return;
            }

            painted ??= [];
            foreach (var text in host.GetVisualDescendants().OfType<TextBlock>())
            {
                if (!painted.ContainsKey(text))
                    painted[text] = text.GetDiagnostic(TextBlock.ForegroundProperty).Priority
                                    == BindingPriority.LocalValue ? text.Foreground : null;
                text.Foreground = fg;
            }
        }

        void Apply() => ApplyCellStyle(host, key, PaintText);

        // Loaded, а не AttachedToVisualTree: у составных ячеек (связь, файл) на момент
        // подключения шаблон еще не применен — надписей в дереве нет и красить нечего.
        host.Loaded += (_, _) => Apply();

        // Правый клик по ячейке запоминает ее колонку: меню открывает СТРОКА, и без этого
        // пункты «Фон ячейки»/«Цвет текста ячейки» не знали бы, какую ячейку красить.
        // Ловим на туннелировании — до того, как событие дойдет до строки и откроет меню.
        host.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(host).Properties.IsRightButtonPressed) _menuCellKey = key;
        }, RoutingStrategies.Tunnel);

        TrackRowStyle(host, Apply);
        return host;
    }

    // Держит визуал контрола в согласии со строкой-владельцем: пересчет на смену DataContext
    // (виртуализация подставила другую строку) и на «Item» строки (заглушка «Загрузка…»
    // дозаполнилась данными — DataContext при этом тот же, см. TableRowVm.Fill). Подписка
    // снимается при отсоединении от дерева, чтобы строки не держали мертвые контейнеры.
    private static void TrackRowStyle(Control host, Action apply)
    {
        TableRowVm? bound = null;
        void OnRowChanged(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is "Item") apply();
        }
        void Rebind()
        {
            if (bound is not null) bound.PropertyChanged -= OnRowChanged;
            bound = host.DataContext as TableRowVm;
            if (bound is not null) bound.PropertyChanged += OnRowChanged;
            apply();
        }

        host.DataContextChanged += (_, _) => Rebind();
        host.DetachedFromVisualTree += (_, _) =>
        {
            if (bound is not null) bound.PropertyChanged -= OnRowChanged;
            bound = null;
        };
        Rebind();
    }

    // Итоговый цвет ячейки: ячейка → строка → цвет записи → колонка (RowStyle.Resolve —
    // единственное место, где описан приоритет). Заливка — слою, цвет текста — в paintText:
    // надписи ячейки и их исходные кисти знает он (см. CellStyleHost).
    private void ApplyCellStyle(Border host, string key, Action<IBrush?> paintText)
    {
        var style = host.DataContext is TableRowVm vm && !vm.IsAddRow
            ? RowStyle.Resolve(vm.CellStyle(key), RowStyleOf(vm.RecordId), ColumnStyleOf(key))
            : RowStyle.None;

        host.Background = ColorTools.Fill(style.Bg);
        paintText(ColorTools.Text(style.Fg));
    }

    private RowStyle RowStyleOf(int recordId) =>
        _rowStyles.TryGetValue(recordId, out var s) ? s : RowStyle.None;

    private RowStyle ColumnStyleOf(string key) =>
        _columnStyles.TryGetValue(key, out var s) ? s : RowStyle.None;

    private IDataTemplate BuildCellContentTemplate(TableColumnDef def) =>
        new FuncDataTemplate<TableRowVm>((row, _) =>
        {
            // Серые заливки «№»/«+» — без сглаживания (Aliased): на дробных позициях
            // (зум ≠ 1) верх/низ непрозрачной заливки размазывался бы рядом с четкими
            // линиями сетки и линией под шапкой (в прозрачных колонках смаз не виден —
            // белое по белому). ВАЖНО: Aliased — только на прямоугольнике заливки, ТЕКСТ
            // вне его поддерева: EdgeMode наследуется и переключает сглаживание текста
            // (цифры становились тонкими).
            if (def.Key == TableColumnDef.NumKey)
            {
                var fill = new Rectangle { Fill = (IBrush)this.FindResource("BrushInactiveBg")! };
                Avalonia.Media.RenderOptions.SetEdgeMode(fill, Avalonia.Media.EdgeMode.Aliased);

                var num = new Panel();
                num.Children.Add(fill);
                num.Children.Add(new TextBlock
                {
                    Text                = row.IsAddRow ? "+" : row.RowNumber.ToString(),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Foreground          = (IBrush)this.FindResource("BrushSecondaryText")!,
                });

                return num;
            }

            if (def.Key == TableColumnDef.AddPropKey)
            {
                var plusCell = new Border
                {
                    Background          = (IBrush)this.FindResource("BrushInactiveBg")!,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment   = VerticalAlignment.Stretch,
                };
                Avalonia.Media.RenderOptions.SetEdgeMode(plusCell, Avalonia.Media.EdgeMode.Aliased);
                return plusCell;
            }

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

            // «Связь»: read-ячейка — названия связанных записей ТЕКСТОМ через разделитель (пусто →
            // «связи нет»), фон прозрачный. Иконка/список — только в РЕЖИМЕ ПРАВКИ ячейки (как чеклист
            // «Мультисписка», см. BuildEditTemplate). Данные тянет сам по RecordId строки (GetLinks);
            // строка приходит асинхронно (виртуализация: заглушка → Fill → "Item", без смены
            // DataContext) и меняется при переиспользовании контейнера — переустанавливаем RecordId.
            if (def.FieldType == "relation" && def.Relation is not null && _repository is not null && def.Tag is int relFid)
            {
                var rel = new RelationSelectBox(_repository, relFid, def.Relation, editing: false, contentLeftPadding: 6, cache: _relLinks);
                BindRelationRow(rel, row);
                return rel;
            }

            // «Файл»: ссылка на файл (клик — открыть через ОС), ручной ввод пути + иконка-скрепка
            // (системный выбор файла). Колонка read-only на уровне DataGrid (см. IsReadOnly ниже) —
            // редактор живет прямо в ячейке показа, отдельного CellEditingTemplate нет (как у чекбокса).
            if (def.FieldType == "file")
            {
                var editor = new FileEditor
                {
                    Flat               = true,
                    ContentLeftPadding = 6,   // как Margin(6,…) у TextBlock-ячеек — общий левый отступ колонок
                    VerticalAlignment  = VerticalAlignment.Center,
                    AutoFolder         = def.FileAutoFolder,   // непусто → read-only авто-режим
                };
                // Запись правки — в СТРОКУ ИЗ DataContext (контейнеры переиспользуются между строками),
                // а не в захваченную при построении шаблона.
                editor.Changed += (_, _) =>
                {
                    if (editor.DataContext is TableRowVm r) r[def.Key] = editor.Storage ?? "";
                };
                // Значение приходит асинхронно (виртуализация: заглушка «Загрузка…» → Fill → "Item")
                // и меняется из панели записи. FileEditor читает Storage разово, поэтому обновляем
                // показ по уведомлению строки; при смене DataContext переподписываемся.
                System.ComponentModel.INotifyPropertyChanged? bound = null;
                void OnRowProp(object? _, System.ComponentModel.PropertyChangedEventArgs e)
                {
                    if ((e.PropertyName is "Item" or null) && editor.DataContext is TableRowVm r)
                        editor.SyncStorage(r[def.Key]);
                }
                void Rebind()
                {
                    if (bound is not null) bound.PropertyChanged -= OnRowProp;
                    bound = editor.DataContext as System.ComponentModel.INotifyPropertyChanged;
                    if (bound is not null) bound.PropertyChanged += OnRowProp;
                    editor.SyncStorage((editor.DataContext as TableRowVm)?[def.Key] ?? "");
                }
                editor.DataContextChanged += (_, _) => Rebind();
                editor.DetachedFromVisualTree += (_, _) =>
                {
                    if (bound is not null) { bound.PropertyChanged -= OnRowProp; bound = null; }
                };
                // DataContext ставит фреймворк (наследование от строки) — не задаем его явно,
                // иначе переиспользование контейнера не переприсвоит строку. Первичный показ —
                // из строки шаблона; дальше ведет DataContextChanged.
                editor.SyncStorage(row[def.Key]);
                return editor;
            }

            // «Вычисляемые» типы: значение — из аудит-метаданных записи (свойства строки
            // CreatedAt/UpdatedAt/CreatedBy/UpdatedBy), а не из индексатора значений полей.
            // Read-only: биндим Text к именованному свойству через формат-конвертер.
            if (FieldTypeInfo.IsComputed(def.FieldType))
            {
                var metaBinding = new Binding(ComputedBindingPath(def.FieldType))
                {
                    Converter = new ComputedFieldDisplayConverter(def.FieldType),
                };
                return new TextBlock
                {
                    Margin              = new Thickness(6, 0, 6, 0),
                    VerticalAlignment   = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    TextTrimming        = TextTrimming.CharacterEllipsis,
                    Foreground          = (IBrush)this.FindResource("BrushSecondaryText")!,   // авто-значение — приглушенно
                    [!TextBlock.TextProperty] = metaBinding,
                };
            }

            // «Мультисписок»: read-ячейка — выбранные значения обычным текстом через запятую
            // (FieldDisplayConverter → MultiSelectValue.Display); чипы/чеклист — только в edit.
            var binding = new Binding($"[{def.Key}]")
            {
                Converter = def.Key == TableRowVm.TitleKey
                    ? EmptyTitleConverter.Instance
                    : new FieldDisplayConverter(def.FieldType, def.Separator, def.NumberFormat),
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

    // Имя свойства TableRowVm-источника для «вычисляемого» типа свойства (аудит-метаданные).
    private static string ComputedBindingPath(string? fieldType) => fieldType switch
    {
        "created_at" => nameof(TableRowVm.CreatedAt),
        "updated_at" => nameof(TableRowVm.UpdatedAt),
        "created_by" => nameof(TableRowVm.CreatedBy),
        "updated_by" => nameof(TableRowVm.UpdatedBy),
        _            => nameof(TableRowVm.CreatedAt),
    };

    // Привязать поле-связь к строке: RecordId берется у строки (в шаблоне DataContext еще может быть
    // не проставлен), обновляется по ее уведомлению "Item" (Fill заглушки / правка) и при
    // переиспользовании контейнера (DataContextChanged). Отписка при отсоединении.
    private static void BindRelationRow(RelationSelectBox rel, TableRowVm initialRow)
    {
        TableRowVm? bound = null;
        void OnRowProp(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if ((e.PropertyName is "Item" or null) && bound is not null) rel.OnRowFilled(bound.RecordId);
        }
        void Bind(TableRowVm? r)
        {
            if (ReferenceEquals(bound, r)) { if (r is not null) rel.RecordId = r.RecordId; return; }
            if (bound is not null) bound.PropertyChanged -= OnRowProp;
            bound = r;
            if (bound is not null) bound.PropertyChanged += OnRowProp;
            rel.RecordId = bound?.RecordId ?? 0;
        }
        rel.DataContextChanged     += (_, _) => Bind(rel.DataContext as TableRowVm);
        rel.DetachedFromVisualTree += (_, _) => { if (bound is not null) { bound.PropertyChanged -= OnRowProp; bound = null; } };
        Bind(initialRow);
    }

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
                case "status":  // как select: значение — строка из опций (ConfigJson)
                case "user":   // как select: значение — отображаемое имя пользователя
                {
                    // «Пользователь»: опции берем в момент входа в правку, а не из
                    // построенных колонок, — состав пользователей базы меняют соседи
                    // (присоединился/допущен/отозван), список обязан быть свежим.
                    // Плюс текущее значение остается в опциях, даже если такого
                    // пользователя в базе больше нет (исторический снапшот).
                    var options = def.FieldType == "user" && _repository is not null
                        ? _repository.GetUserOptions(row[def.Key])
                        : def.Options;
                    var combo = new ComboBox
                    {
                        BorderThickness     = new Thickness(0),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        ItemsSource         = options,
                        SelectedItem        = row[def.Key],
                    };
                    combo.SelectionChanged += (_, _) =>
                        row[def.Key] = combo.SelectedItem?.ToString() ?? "";
                    return combo;
                }

                case "multiselect":   // несколько опций: чипы + выпадающий чеклист, значение — JSON-массив
                {
                    var box = new Controls.Components.MultiSelectBox(def.Options, row[def.Key], flat: true, separator: def.Separator);
                    box.Changed += (_, _) => row[def.Key] = box.Storage;
                    return box;
                }

                case "relation" when def.Relation is not null && _repository is not null && def.Tag is int relFid:
                {
                    // Правка ячейки: текст + иконка-связь справа, по клику по иконке — чеклист выбора
                    // (как чеклист «Мультисписка»). «→» у пункта → открыть цель. По выходе из правки
                    // read-ячейка (editing:false) переустановится и перечитает связи (AttachedToVisualTree).
                    var rel = new RelationSelectBox(_repository, relFid, def.Relation, editing: true, contentLeftPadding: 6, cache: _relLinks);
                    rel.Navigate += (_, t) =>
                    {
                        if (t.DbId > 0) CrossDbRecordRequested?.Invoke(this, t);   // межбазовый переход
                        else RecordClicked?.Invoke(this, t.RecordId);              // в текущей БД
                    };
                    rel.Changed  += (_, _) =>
                    {
                        if (rel.DataContext is TableRowVm r) RecordValueEdited?.Invoke(this, r.RecordId);
                    };
                    BindRelationRow(rel, row);
                    return rel;
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
    // остается допустимым (частичным) числом — цифры, один разделитель, ведущий минус.
    private static void OnNumberTextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is not TextBox tb || string.IsNullOrEmpty(e.Text)) return;

        var text  = tb.Text ?? "";
        var start = Math.Min(tb.SelectionStart, tb.SelectionEnd);
        var end   = Math.Max(tb.SelectionStart, tb.SelectionEnd);
        var prospective = text[..start] + e.Text + text[end..];

        if (!IsPartialNumber(prospective)) e.Handled = true;
    }

    // Допускает незавершенный ввод: "", "-", "12", "12,", "-3.5".
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
            // Создать в конце, без открытия; без права — окно с причиной.
            PermissionNotice.Run(this, Can(PermAction.CreateRecord), PermAction.CreateRecord,
                                 () => RecordCreateRequested?.Invoke(this, (false, null)));
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
            PermissionNotice.Run(this, Can(PermAction.CreateField), PermAction.CreateField,
                                 () => PropertyCreateRequested?.Invoke(this, EventArgs.Empty));
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

    // ── Выделение колонок голубой заливкой (одиночное и Shift-диапазон) ────────

    private void SelectColumn(string key, bool shift)
    {
        // Служебные колонки № и + не выделяем. «Название»/«Описание» выделять можно
        // (заливка появляется), но при удалении они отфильтровываются.
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

        // Порядок выделения держим по порядку колонок — как в списке ключей на удаление.
        _selectedColumnKeys.Sort((a, b) =>
            _colDefs.FindIndex(d => d.Key == a).CompareTo(_colDefs.FindIndex(d => d.Key == b)));

        ApplyColumnSelection();
    }

    private void ClearColumnSelection()
    {
        if (_selectedColumnKeys.Count == 0) return;
        _selectedColumnKeys.Clear();
        ApplyColumnSelection();
    }

    // Заливка выделенной колонки — ОДНА кисть на колонку (ключ → кисть): ее держат ячейки
    // ЗНАЧЕНИЙ (фон ContentPresenter, см. AttachColumnFill). Шапка не красится — заголовок
    // остается прежним, выделение видно только по ячейкам свойства.
    // Выделение/снятие — смена Color у самой кисти, поэтому виртуализация не мешает: новая
    // ячейка получает ту же кисть и сразу верный цвет, а перекраска не требует обхода
    // дерева и не зависит от скролла (прежний слой рамок пересчитывался в LayoutUpdated).
    private readonly Dictionary<string, SolidColorBrush> _colSelBrushes = [];

    private SolidColorBrush ColumnSelectionBrush(string key)
    {
        if (!_colSelBrushes.TryGetValue(key, out var brush))
            _colSelBrushes[key] = brush = new SolidColorBrush(Colors.Transparent);
        return brush;
    }

    // Красит кисти колонок под текущее выделение: цвет — тот же BrushAccentLight, что у
    // выделенной записи (DataGridRow:selected, TableView.axaml).
    private void ApplyColumnSelection()
    {
        var on = (this.FindResource("BrushAccentLight") as ISolidColorBrush)?.Color ?? Colors.Transparent;
        foreach (var (key, brush) in _colSelBrushes)
            brush.Color = _selectedColumnKeys.Contains(key) ? on : Colors.Transparent;
    }

    // Вешает заливку выделения на ячейку: фон ставится ContentPresenter'у шаблона
    // DataGridCell — это звездная колонка сетки ячейки, поэтому заливка доходит ровно до
    // вертикальной линии сетки и не перекрывает ни ее, ни линию соседа слева. Контейнеры
    // переиспользуются между строками, но не между колонками — кисть на ячейке всегда «своя».
    private void AttachColumnFill(Control? content, string key)
    {
        if (content is null) return;
        if (key == TableColumnDef.NumKey || key == TableColumnDef.AddPropKey) return;

        var brush = ColumnSelectionBrush(key);
        content.AttachedToVisualTree += (_, _) =>
        {
            if (content.FindAncestorOfType<Avalonia.Controls.Presenters.ContentPresenter>() is { } cp)
                cp.Background = brush;
        };
    }

    private ContextMenu BuildGridContextMenu()
    {
        var menu = new ContextMenu();
        var mi = new MenuItem { Header = Controls.Components.MenuIcons.CreateRecordHeader() };
        // В конце, без открытия.
        Guarded(mi, PermAction.CreateRecord, () => RecordCreateRequested?.Invoke(this, (false, null)));
        menu.Items.Add(mi);

        // «Отображение» доступно и отсюда — на случай, если все колонки скрыты и до
        // меню заголовка не добраться (окно показывает полный список колонок).
        var miDisplay = new MenuItem { Header = Controls.Components.MenuIcons.DisplayHeader() };
        miDisplay.Click += (_, _) => ColumnDisplayRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miDisplay);

        return menu;
    }

    // Меню строится с ЛЕНИВЫМ резолвом RecordId из текущего DataContext строки (см. RowRecordId),
    // а не с захватом id в момент BuildRowContextMenu. Причина: при фоновой подгрузке строк
    // строка сперва realized как заглушка «Загрузка…» с RecordId=0,
    // и только потом Fill проставляет реальный id в ТОТ ЖЕ VM — без повторного realized, т.е. без
    // повторного OnLoadingRow. Захваченный id остался бы 0: дубль падал «Record 0 not found»,
    // удаление/изменение/сдвиг — тихий no-op. Чтение на момент клика всегда дает актуальный id.
    private ContextMenu BuildRowContextMenu(DataGridRow row)
    {
        var menu = new ContextMenu();

        var miCreate = new MenuItem { Header = Controls.Components.MenuIcons.CreateRecordHeader() };
        // После этой строки, без открытия. Если строка еще не заполнена (id=0) — создаем в конце.
        Guarded(miCreate, PermAction.CreateRecord, () =>
        {
            var rid = RowRecordId(row);
            RecordCreateRequested?.Invoke(this, (false, rid > 0 ? rid : (int?)null));
        });
        menu.Items.Add(miCreate);

        menu.Items.Add(new Separator());

        var miEdit = new MenuItem { Header = Controls.Components.MenuIcons.EditHeader() };
        miEdit.Click += (_, _) => { var rid = RowRecordId(row); if (rid > 0) RecordClicked?.Invoke(this, rid); };
        menu.Items.Add(miEdit);

        var miDuplicate = new MenuItem { Header = Controls.Components.MenuIcons.DuplicateHeader() };
        Guarded(miDuplicate, PermAction.DuplicateRecord, () => RequestRecordDuplicateFromMenu(row));
        menu.Items.Add(miDuplicate);

        // Перенос = изъятие записи из коллекции: право как у удаления.
        var miRelocate = new MenuItem { Header = Controls.Components.MenuIcons.LocationHeader() };
        Guarded(miRelocate, PermAction.DeleteRecord, () => RequestRecordRelocateFromMenu(row));
        menu.Items.Add(miRelocate);

        // «Отображение» (записей) — глобальное окно видимости/уровней строк (список всех
        // записей), инициатор-строка не важна.
        var miDisplay = new MenuItem { Header = Controls.Components.MenuIcons.DisplayHeader() };
        miDisplay.Click += (_, _) => RecordDisplayRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miDisplay);

        AddColorItems(menu, row);
        AddRecordMoveItems(menu, row);
        AddHeightModeItems(menu);

        menu.Items.Add(new Separator());

        var miDelete = MakeDeleteMenuItem();
        Guarded(miDelete, PermAction.DeleteRecord, () => RequestRecordDeleteFromMenu(row));
        menu.Items.Add(miDelete);

        return menu;
    }

    // Текущий RecordId строки из ее DataContext (0 — заглушка «Загрузка…» или add-row).
    // Резолвится на момент вызова, поэтому видит id, проставленный async-заполнением строки.
    private static int RowRecordId(DataGridRow row) =>
        (row.DataContext as TableRowVm)?.RecordId ?? 0;

    // ── Пункты цвета в меню строки ────────────────────────────────────────────
    //
    // Подменю с общей палитрой (ColorMenu): «Фон строки», «Цвет текста строки», а под
    // чертой — «Фон ячейки» и «Цвет текста ячейки». Все это оформление ЭТОЙ таблицы: за ее
    // пределами цвет не виден (сквозной цвет записи был и убран — см. RowStyle.Resolve).
    // Все пункты применяются ко ВСЕМУ мультивыделению строк — как удаление и дубль.
    // Состав пунктов пересчитывается в menu.Opening: цвета строки к моменту клика могли
    // измениться, а «текущий» цвет в палитре должен быть отмечен верно.
    private void AddColorItems(ContextMenu menu, DataGridRow row)
    {
        var host = new MenuItem { Header = Controls.Components.MenuIcons.ColorHeader() };
        Controls.Components.MenuIcons.FixSubmenuGap(host);
        host.IsEnabled = Can(PermAction.EditRecord);

        menu.Opening += (_, _) =>
        {
            host.Items.Clear();
            var vm  = row.DataContext as TableRowVm;
            var ids = TargetRecordIds(row);
            if (ids.Count == 0) { host.IsEnabled = false; return; }
            host.IsEnabled = Can(PermAction.EditRecord);

            // Отмечаем цвет ТОЙ строки, по которой кликнули (в выделении она не обязательно
            // первая), а применяем ко всему набору — как у колонок.
            var rowStyle = RowStyleOf(RowRecordId(row));
            host.Items.Add(ColorMenu.Build("Фон строки", rowStyle.Bg,
                hex => ApplyRowStyle(ids, StyleAspect.Background, hex)));
            host.Items.Add(ColorMenu.Build("Цвет текста строки", rowStyle.Fg,
                hex => ApplyRowStyle(ids, StyleAspect.Text, hex)));

            // Ячейка — та, по которой был правый клик (запомнена в CellStyleHost). Своего
            // контекстного меню у ячейки нет: меню открывает строка, поэтому пункты ячейки
            // живут здесь же, отделенные чертой.
            if (_menuCellKey is not { } cellKey) return;
            var cells = ids.Select(id => (RecordId: id, Key: cellKey)).ToList();
            var cur   = vm?.CellStyle(cellKey) ?? RowStyle.None;

            host.Items.Add(new Separator());
            host.Items.Add(ColorMenu.Build("Фон ячейки", cur.Bg,
                hex => ApplyCellStyleChange(cells, StyleAspect.Background, hex)));
            host.Items.Add(ColorMenu.Build("Цвет текста ячейки", cur.Fg,
                hex => ApplyCellStyleChange(cells, StyleAspect.Text, hex)));
        };

        menu.Items.Add(new Separator());
        menu.Items.Add(host);
    }

    // Записи, к которым применяется команда меню строки: все мультивыделение, если строка
    // в него входит (правый клик по выделенной строке выделение сохраняет), иначе только она.
    private List<int> TargetRecordIds(DataGridRow row)
    {
        var recordId = RowRecordId(row);
        if (recordId <= 0) return [];
        var selected = SelectedRecordIds();
        return selected.Contains(recordId) && selected.Count > 1 ? selected : [recordId];
    }

    private void ApplyRowStyle(List<int> recordIds, StyleAspect aspect, string? hex)
    {
        if (_repository is null) return;
        try { _repository.SetRowStyle(_collectionId, recordIds, aspect, hex); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { return; }

        foreach (var id in recordIds)
        {
            var cur  = RowStyleOf(id);
            var next = aspect == StyleAspect.Background ? cur with { Bg = hex } : cur with { Fg = hex };
            if (next.IsEmpty) _rowStyles.Remove(id); else _rowStyles[id] = next;
        }
        RefreshRowStyleVisuals(recordIds);
    }

    // Перерисовать цвет уже загруженных строк: ячейки слушают «Item» строки (см. TrackRowStyle).
    // Строки берем из ВИДИМОГО списка (_activeRows): при фильтре/сортировке на экране живут
    // строки отфильтрованного списка, а не _virtualRows — как и в точечных рефрешах правки.
    private void RefreshRowStyleVisuals(IEnumerable<int> recordIds)
    {
        foreach (var id in recordIds)
            _activeRows?.FindCachedByRecordId(id)?.NotifyCellsChanged();
    }

    private void ApplyCellStyleChange(List<(int RecordId, string Key)> cells, StyleAspect aspect, string? hex)
    {
        if (_repository is null) return;
        try { _repository.SetCellStyle(_collectionId, cells, aspect, hex); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { return; }

        foreach (var (recordId, key) in cells)
        {
            var row = _activeRows?.FindCachedByRecordId(recordId);
            if (row is null) continue;
            var next = new Dictionary<string, RowStyle>();
            if (row.CellStyles is { } existing)
                foreach (var (k, v) in existing) next[k] = v;

            var cur     = next.TryGetValue(key, out var c) ? c : RowStyle.None;
            var updated = aspect == StyleAspect.Background ? cur with { Bg = hex } : cur with { Fg = hex };
            if (updated.IsEmpty) next.Remove(key); else next[key] = updated;

            row.SetStyle(next);
        }
    }

    private void ApplyColumnStyle(List<string> keys, StyleAspect aspect, string? hex)
    {
        if (_repository is null || keys.Count == 0) return;
        try { _repository.SetColumnStyle(_collectionId, keys, aspect, hex); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { return; }

        foreach (var key in keys)
        {
            var cur  = ColumnStyleOf(key);
            var next = aspect == StyleAspect.Background ? cur with { Bg = hex } : cur with { Fg = hex };
            if (next.IsEmpty) _columnStyles.Remove(key); else _columnStyles[key] = next;
        }

        // Цвет колонки касается всех загруженных строк — уведомляем их разом.
        _activeRows?.NotifyAllCachedRows();

        // Снимаем выделение колонок: его голубая заливка лежит ПОД полупрозрачным цветом,
        // и на выделенной колонке разницу «покрасил / снял цвет» почти не видно. Выделение
        // здесь — жест «эти колонки», он свое отработал (так же гаснет после дубля и
        // удаления свойств). Выделение СТРОК не трогаем: это основное состояние таблицы.
        ClearColumnSelection();
    }

    // Удаление из меню записи. Если запись входит в текущее мультивыделение (правый
    // клик по выделенной строке его сохраняет) — удаляем весь набор; иначе только ее.
    // В любом случае идем через пакетный путь с подтверждением (RecordsDeleteRequested) —
    // как и удаление свойств.
    private void RequestRecordDeleteFromMenu(DataGridRow row)
    {
        var recordId = RowRecordId(row);
        if (recordId <= 0) return;   // строка-заглушка еще не заполнена — удалять нечего

        var selected = SelectedRecordIds();
        List<int> ids = selected.Contains(recordId) && selected.Count > 1 ? selected : [recordId];
        RecordsDeleteRequested?.Invoke(this, ids);
    }

    // Дубль из меню записи (по аналогии с удалением): запись в мультивыделении → дублируем весь
    // набор; иначе только ее. Контроллер сам решает: одна — копия после исходной мгновенно,
    // несколько — bulk в фоне с индикатором.
    private void RequestRecordDuplicateFromMenu(DataGridRow row)
    {
        var recordId = RowRecordId(row);
        if (recordId <= 0) return;   // строка-заглушка еще не заполнена — дублировать нечего

        var selected = SelectedRecordIds();
        List<int> ids = selected.Contains(recordId) && selected.Count > 1 ? selected : [recordId];
        RecordDuplicateRequested?.Invoke(this, ids);
    }

    // «Расположение» из меню записи (как дубль/удаление): запись в мультивыделении →
    // переносим весь набор; иначе только ее. Контроллер открывает окно выбора БД/коллекции.
    private void RequestRecordRelocateFromMenu(DataGridRow row)
    {
        var recordId = RowRecordId(row);
        if (recordId <= 0) return;   // строка-заглушка еще не заполнена — переносить нечего

        var selected = SelectedRecordIds();
        List<int> ids = selected.Contains(recordId) && selected.Count > 1 ? selected : [recordId];
        RecordRelocateRequested?.Invoke(this, ids);
    }

    // Пункты «Сдвинуть вверх/вниз» (именно в таком порядке) с разделителем перед ними.
    // Доступны только без активных фильтра и сортировки: при сортировке порядок задает
    // сортировка, а не позиция, поэтому ручной сдвиг был бы невидим. Неприменимое
    // направление (крайняя запись) скрываем; видимость уточняем при открытии меню — тогда
    // же резолвится RecordId строки (async-заполнение к моменту открытия уже прошло).
    private void AddRecordMoveItems(ContextMenu menu, DataGridRow row)
    {
        if (_sortKey is not null || _filters.Count > 0) return;

        var sep    = new Separator();
        var miUp   = new MenuItem { Header = Controls.Components.MenuIcons.MoveUpHeader() };
        miUp.Click += (_, _) => { var rid = RowRecordId(row); if (rid > 0) MoveRecord(rid, up: true); };
        var miDown = new MenuItem { Header = Controls.Components.MenuIcons.MoveDownHeader() };
        miDown.Click += (_, _) => { var rid = RowRecordId(row); if (rid > 0) MoveRecord(rid, up: false); };

        menu.Items.Add(sep);
        menu.Items.Add(miUp);
        menu.Items.Add(miDown);

        void Refresh()
        {
            var rid     = RowRecordId(row);
            var canUp   = rid > 0 && CanMoveRecord(rid, up: true);
            var canDown = rid > 0 && CanMoveRecord(rid, up: false);
            miUp.IsVisible   = canUp;
            miDown.IsVisible = canDown;
            sep.IsVisible    = canUp || canDown;
        }

        // ТОЛЬКО при открытии меню, БЕЗ жадного вызова при построении: меню строится в
        // OnLoadingRow на КАЖДУЮ реализацию строки — обращения к БД здесь подвешивали
        // прокрутку колесом/кнопками скроллбара (каждая пересеченная строка платила
        // за проверку соседей; Opening же случается только при правом клике).
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

    // ── Системная колонка «Название» ──────────────────────────────────────────

    // Публично для контроллера: текущая подпись и имя по умолчанию по ключу колонки.
    public string GetSystemHeader(string key)        => SystemHeader(key);
    public string GetSystemDefaultName(string key)   => SystemDefaultName(key);

    // Имя по умолчанию для системной колонки (она одна — «Название»).
    private static string SystemDefaultName(string key) => "Название";

    // Текущая подпись: пользовательская из метаданных, иначе имя по умолчанию.
    private string SystemHeader(string key) =>
        _systemNames.TryGetValue(key, out var name) ? name : SystemDefaultName(key);

    // Меню заголовка системной колонки «Название»:
    //   Изменить · Создать новое свойство · — · Сдвинуть вправо · Сдвинуть влево
    // Удаление/смена типа/дублирование для нее недоступны.
    private ContextMenu BuildSystemHeaderContextMenu(string key)
    {
        var menu = new ContextMenu();

        // Шапка: иконка типа + подпись колонки + тип «Текст» (Title — текстовый).
        menu.Items.Add(MakeMenuHeaderItem(
            Controls.Components.MenuIcons.PropertyMenuHeader(SystemHeader(key), "text")));
        menu.Items.Add(new Separator());

        var miEdit = new MenuItem { Header = Controls.Components.MenuIcons.EditHeader() };
        Guarded(miEdit, PermAction.EditField, () => SystemFieldRenameRequested?.Invoke(this, key));
        menu.Items.Add(miEdit);

        var miCreate = new MenuItem { Header = Controls.Components.MenuIcons.CreatePropertyHeader() };
        Guarded(miCreate, PermAction.CreateField, () => PropertyCreateAfterRequested?.Invoke(this, key));
        menu.Items.Add(miCreate);

        var miDisplay = new MenuItem { Header = Controls.Components.MenuIcons.DisplayHeader() };
        miDisplay.Click += (_, _) => ColumnDisplayRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miDisplay);

        AddColumnColorItems(menu, key);
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
    // пересчитывается при открытии меню (меню кэшируется на строке при ее отрисовке).
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
    private ContextMenu BuildHeaderContextMenu(int fieldId, bool isRequired, string name, string fieldType,
                                               bool fileAuto = false)
    {
        var menu = new ContextMenu();

        // Шапка: иконка типа + название свойства + тип данных (некликабельная).
        menu.Items.Add(MakeMenuHeaderItem(Controls.Components.MenuIcons.PropertyMenuHeader(name, fieldType)));
        menu.Items.Add(new Separator());

        var miEdit = new MenuItem { Header = Controls.Components.MenuIcons.EditHeader() };
        Guarded(miEdit, PermAction.EditField, () => PropertyEditRequested?.Invoke(this, fieldId));
        menu.Items.Add(miEdit);

        var miDuplicate = new MenuItem { Header = Controls.Components.MenuIcons.DuplicateHeader() };
        Guarded(miDuplicate, PermAction.DuplicateField, () => RequestDuplicateFromMenu(fieldId));
        menu.Items.Add(miDuplicate);

        var miCreate = new MenuItem { Header = Controls.Components.MenuIcons.CreatePropertyHeader() };
        Guarded(miCreate, PermAction.CreateField, () => PropertyCreateAfterRequested?.Invoke(this, fieldId.ToString()));
        menu.Items.Add(miCreate);

        var miDisplay = new MenuItem { Header = Controls.Components.MenuIcons.DisplayHeader() };
        miDisplay.Click += (_, _) => ColumnDisplayRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miDisplay);

        AddColumnColorItems(menu, fieldId.ToString());

        // «Файл» с авто-привязкой: пересканировать папку и пересобрать значения (файлы на диске
        // могли поменяться / появились новые). Значения read-only ведет правило.
        if (fileAuto)
        {
            var miRefresh = new MenuItem { Header = "Обновить привязку" };
            Guarded(miRefresh, PermAction.EditRecord, () => RefreshAutoFileFromMenu(fieldId));
            menu.Items.Add(miRefresh);
        }

        AddMoveItems(menu, fieldId.ToString());
        AddWidthModeItems(menu);

        menu.Items.Add(new Separator());

        var miDelete = MakeDeleteMenuItem(enabled: !isRequired);
        Guarded(miDelete, PermAction.DeleteField, () => RequestDeleteFromMenu(fieldId));
        menu.Items.Add(miDelete);

        return menu;
    }

    // Цвет колонки — тот же аспект column_meta, что видимость и подпись, поэтому право
    // как у правки свойства. Состав пересчитывается в Opening: и отметка текущего цвета,
    // и НАБОР колонок — выделение к моменту клика могло смениться.
    private void AddColumnColorItems(ContextMenu menu, string key)
    {
        var host = new MenuItem { Header = Controls.Components.MenuIcons.ColorHeader() };
        Controls.Components.MenuIcons.FixSubmenuGap(host);
        host.IsEnabled = Can(PermAction.EditField);

        menu.Opening += (_, _) =>
        {
            host.Items.Clear();
            var keys = TargetColumnKeys(key);
            var cur  = ColumnStyleOf(key);   // отмечаем цвет колонки, по которой кликнули
            host.Items.Add(ColorMenu.Build("Фон колонки", cur.Bg,
                hex => ApplyColumnStyle(keys, StyleAspect.Background, hex)));
            host.Items.Add(ColorMenu.Build("Цвет текста колонки", cur.Fg,
                hex => ApplyColumnStyle(keys, StyleAspect.Text, hex)));
        };

        menu.Items.Add(host);
    }

    // Колонки, к которым применяется цвет: все выделение, если колонка в него входит
    // (как дубль и удаление свойств), иначе только она. В отличие от них системные
    // колонки НЕ отбрасываются: «Название» красится наравне с остальными.
    private List<string> TargetColumnKeys(string key) =>
        _selectedColumnKeys.Contains(key) && _selectedColumnKeys.Count > 1
            ? [.. _selectedColumnKeys]
            : [key];

    // «Обновить привязку» файла: пересканировать папку, пересобрать значения и перечитать строки.
    // Окно прогресса — только если долго (маленькая коллекция обновляется без мелькания).
    private async void RefreshAutoFileFromMenu(int fieldId)
    {
        if (_repository is null) return;
        var win = VisualRoot as Avalonia.Controls.Window;

        using var cts = new System.Threading.CancellationTokenSource();
        Controls.Dialogs.ProgressDialog? dlg = null;
        var progress = new Progress<double>(f => dlg?.SetProgress(f, "Поиск файлов…"));
        var bg = _repository.ResolveAutoFileFieldAsync(fieldId, progress, cts.Token);
        if (win is not null && await System.Threading.Tasks.Task.WhenAny(bg, System.Threading.Tasks.Task.Delay(400)) != bg)
        {
            dlg = new Controls.Dialogs.ProgressDialog { Title = "Привязка файлов" };
            dlg.SetProgress(0, "Поиск файлов…");
            dlg.EnableCancel(() => cts.Cancel());
            var closed = dlg.ShowDialog(win);
            await bg;
            dlg.Done();
            await closed;
        }
        var result = await bg;
        ReloadRows();
        if (result == ConstructorRepository.FileAutoResolveResult.FolderInaccessible && win is not null)
            await Controls.Dialogs.ConfirmDialog.Info("Папка недоступна").ShowDialog(win);
    }

    // Удаление из контекстного меню «Удалить». Если поле входит в текущее выделение
    // (несколько колонок) — удаляем весь выделенный набор; иначе только это поле.
    // В любом случае идем через пакетный путь с подтверждением (PropertiesDeleteRequested).
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

    // Дублирование из контекстного меню «Дублировать». Если поле входит в текущее
    // мультивыделение (несколько колонок) — дублируем весь выделенный набор; иначе только это
    // поле. Системные колонки (нечисловые ключи) в набор не попадают. Пакетный путь — как удаление.
    private void RequestDuplicateFromMenu(int fieldId)
    {
        var key = fieldId.ToString();

        List<int> ids =
            _selectedColumnKeys.Contains(key) && _selectedColumnKeys.Count > 1
                ? _selectedColumnKeys.Where(k => int.TryParse(k, out _)).Select(int.Parse).ToList()
                : [fieldId];

        if (ids.Count == 0) return;
        PropertyDuplicateRequested?.Invoke(this, ids);
    }

    // Убирает из выделения системные колонки («Название»/«Описание») — они не
    // удаляются, поэтому при запросе удаления их заливка гаснет, а на удаляемых
    // полях остается видимой, пока открыт диалог подтверждения.
    private void KeepSelectionOnDeletableOnly()
    {
        var removed = _selectedColumnKeys.RemoveAll(k => !int.TryParse(k, out _));
        if (removed > 0) ApplyColumnSelection();
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not TableRowVm row) return;
        if (row.IsAddRow)
        {
            e.Row.Height = double.NaN;   // строка «+» всегда стандартная
            return;
        }
        e.Row.ContextMenu = BuildRowContextMenu(e.Row);

        // Высота строки: в ручном режиме применяем сохраненную (если есть), иначе
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
    // Полоска кликабельна и несет курсор «↕»; перетаскивание меняет высоту строки.
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
        // которые на этом проходе еще не пересчитаны).
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

    // ── Ручная ширина колонок: полоски-«грипперы» у правой границы ─────────────
    //
    // Встроенный ресайз DataGrid не используется (CanUserResizeColumns остается False):
    //   • у него нет события «перетаскивание закончено» — ширину негде сохранить;
    //   • он запрещает тянуть последнюю видимую колонку и вообще любой ресайз, когда
    //     колонки не помещаются в окно, — а это ровно наш рабочий режим;
    //   • он перекладывает дельту на соседей справа, сжимая их, вместо того чтобы
    //     увеличивать общую ширину таблицы.
    // Поэтому полоски-захваты свои — точная калька слоя высоты строк выше.

    private void ScheduleColHandlesRefresh()
    {
        if (_colHandlesPending || _isResizingCol) return;
        _colHandlesPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _colHandlesPending = false;
            RefreshColResizeHandles();
        }, DispatcherPriority.Background);
    }

    // Размещает по прозрачной полоске на правой границе каждой видимой движимой колонки.
    // Полоска кликабельна и несет курсор «↔»; перетаскивание меняет ширину колонки.
    private void RefreshColResizeHandles()
    {
        if (_isResizingCol) return;   // во время перетаскивания полоски не пересобираем

        var layer = ColResizeLayer;

        // Считаем целевую раскладку полосок, НЕ трогая дерево.
        var target = new List<(string Key, double X, double Top, double H)>();
        if (_manualColResize)
        {
            foreach (var (def, _) in MovableColumns())
            {
                if (!_headerHosts.TryGetValue(def.Key, out var host)) continue;
                var hdr = host.GetVisualAncestors().OfType<DataGridColumnHeader>().FirstOrDefault();
                if (hdr is null || hdr.Bounds.Width <= 0 || hdr.Bounds.Height <= 0) continue;

                var topRight = hdr.TranslatePoint(new Point(hdr.Bounds.Width, 0), layer);
                if (topRight is null) continue;

                // Заголовки виртуализованы: за краем видимой зоны полоски не нужны.
                if (topRight.Value.X < 0 || topRight.Value.X > layer.Bounds.Width) continue;

                target.Add((def.Key, topRight.Value.X - ColGripThickness / 2,
                            topRight.Value.Y, hdr.Bounds.Height));
            }
        }

        // Ничего не изменилось — дерево не трогаем (разрывает layout-цикл).
        if (ColGripsEqual(target, _lastColGrips)) return;

        _lastColGrips.Clear();
        _lastColGrips.AddRange(target);

        layer.Children.Clear();
        foreach (var (key, x, top, h) in target)
        {
            var grip = new Border
            {
                Width      = ColGripThickness,
                Height     = h,
                Background = Brushes.Transparent,
                Cursor     = ColResizeCursor,
            };
            grip.PointerPressed     += (_, e) => BeginColResize(key, e);
            grip.PointerMoved       += (_, e) => DragColResize(e);
            grip.PointerReleased    += (_, e) => EndColResize(e);
            grip.PointerCaptureLost += (_, _) => CancelColResize();

            Canvas.SetLeft(grip, x);
            Canvas.SetTop(grip, top);
            layer.Children.Add(grip);
        }
    }

    private static bool ColGripsEqual(
        List<(string Key, double X, double Top, double H)> a,
        List<(string Key, double X, double Top, double H)> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Key != b[i].Key) return false;
            if (Math.Abs(a[i].X   - b[i].X)   > 0.5) return false;
            if (Math.Abs(a[i].Top - b[i].Top) > 0.5) return false;
            if (Math.Abs(a[i].H   - b[i].H)   > 0.5) return false;
        }
        return true;
    }

    private void BeginColResize(string key, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ColResizeLayer).Properties.IsLeftButtonPressed) return;

        // Фиксируем в модели текущую раскладку: до первого перетаскивания модель пуста,
        // и жест должен отталкиваться от того, что человек видит.
        if (!MaterializeColumnWidths()) return;
        if (!_colWidths.TryGetValue(key, out var startWidth)) return;

        _isResizingCol       = true;
        _resizeColKey        = key;
        _resizeStartWidth    = startWidth;
        _resizeStartX        = e.GetPosition(ColResizeLayer).X;
        _resizeStartHOffset  = CurrentScrollOffset();
        _resizeStartGripLeft = e.Source is Control grip ? Canvas.GetLeft(grip) : 0;
        _resizeFill          = MovableFillWidth();
        _resizeBase          = MovableColumns().ToDictionary(c => c.Def.Key, c => ModelWidth(c.Def.Key));

        e.Pointer.Capture(e.Source as IInputElement);
        e.Handled = true;
    }

    private void DragColResize(PointerEventArgs e)
    {
        if (!_isResizingCol || _resizeColKey is null) return;

        var keys = MovableColumns().Select(c => c.Def.Key).ToList();
        var idx  = keys.IndexOf(_resizeColKey);
        if (idx < 0) return;

        double Base(string k) => _resizeBase.TryGetValue(k, out var w) ? w : ModelWidth(k);

        // Слагаемое с прокруткой компенсирует ее клампинг: когда таблица шире окна и
        // прокрутка сама доезжает до края, без поправки граница отлипает от курсора.
        var scrolled = CurrentScrollOffset() - _resizeStartHOffset;
        var raw = _resizeStartWidth + (e.GetPosition(ColResizeLayer).X - _resizeStartX) + scrolled;

        var right  = keys.Skip(idx + 1).ToList();
        var others = keys.Where(k => k != _resizeColKey).Sum(Base);

        // У последней движимой колонки правую границу нельзя утащить левее точки «таблица
        // ровно заполнена»: справа некому принять освободившееся место, и появилась бы пустота.
        var floor = right.Count > 0
            ? MinManualColWidth
            : Math.Max(MinManualColWidth, _resizeFill - others);

        var target  = Math.Max(floor, raw);
        var surplus = _resizeFill - (others + target);

        _colWidths[_resizeColKey] = target;

        // Колонки СЛЕВА не трогаем никогда — иначе перетаскиваемая граница уехала бы
        // из-под курсора. Освободившееся место (surplus > 0) раздаем колонкам справа
        // пропорционально их ширине; при росте колонки (surplus < 0) соседи сохраняют
        // ширину и просто уезжают вправо — таблица становится шире окна.
        var rightSum = right.Sum(Base);
        foreach (var k in right)
            _colWidths[k] = surplus > 0 && rightSum > 0
                ? Base(k) + surplus * Base(k) / rightSum
                : Base(k);

        ApplyColumnWidths();

        // Полоска следует за новой границей (Bounds на этом проходе еще не пересчитаны).
        // Ширина выросла на (target - старт) — на столько же правее уехала граница; прокрутка
        // сдвигает содержимое влево, поэтому вычитается.
        if (e.Source is Control grip)
            Canvas.SetLeft(grip, _resizeStartGripLeft + (target - _resizeStartWidth) - scrolled);

        e.Handled = true;
    }

    private void EndColResize(PointerReleasedEventArgs e)
    {
        if (!_isResizingCol) return;

        _isResizingCol = false;
        _resizeColKey  = null;
        e.Pointer.Capture(null);
        e.Handled = true;

        // Сохраняем один раз на отпускание мыши — точечным upsert'ом, а не на каждый кадр.
        _repository?.SaveColumnWidths(_collectionId, _colWidths);

        ScheduleColHandlesRefresh();   // пересобрать полоски под новые границы
    }

    // Захват потерян (Alt+Tab, модальное окно поверх): жест закрываем без записи в БД,
    // иначе зависший флаг заблокировал бы перестройку полосок.
    private void CancelColResize()
    {
        if (!_isResizingCol) return;
        _isResizingCol = false;
        _resizeColKey  = null;
        ScheduleColHandlesRefresh();
    }

    // ── Фильтр и сортировка колонок (Excel-стиль) ──────────────────────────────

    // Текущий открытый flyout фильтра/сортировки — чтобы закрыть его при потере фокуса.
    private Flyout? _activeFilterFlyout;

    private void ShowFilterFlyout(Control anchor, TableColumnDef def)
    {
        if (_repository is null) return;

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.FlyoutPresenterClasses.Add("filterFlyout");
        // Флайаут рендерится в отдельном попап-окне вне зума приложения — контент
        // оборачивается в зум-трансформ (плашка-презентер остается вне зума).
        flyout.Content = WindowZoom.WrapPopupContent(BuildFilterPanel(def, () => flyout.Hide()));
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
        var family    = this.TryFindResource("FontFamilyBase", out var f) ? f as FontFamily : null;
        // Ширины строк и закрепленный справа счетчик — см. FilterValueMetrics.
        var metrics   = new FilterValueMetrics();

        // Курсор-стрелка по умолчанию для всей панели (наследуется вниз): на пустых
        // местах и подложке — стрелка. Интерактивные элементы (кнопки сортировки,
        // чекбоксы значений, «Готово») задают курсор-руку явно и перебивают наследование.
        var panel = new StackPanel
        {
            // Ширина и высота карточки списка — общий стандарт всплывающих списков
            // (PopupContentWidth/PopupListHeight в Controls.axaml).
            Width  = (double)this.FindResource("PopupContentWidth")!,
            Margin = new Thickness(12),
            Spacing = 2,
            Cursor  = new Cursor(StandardCursorType.Arrow),
        };

        // ── Данные (грузятся в ФОНЕ; окно открывается мгновенно) ──
        // Уникальные значения с количеством считаются SQL-агрегатом на фоновом потоке
        // (отдельное read-only подключение, WAL → параллельное чтение). До прихода данных в
        // области списка показывается «Загрузка…». Счетчики — по всей колонке (без Excel-каскада
        // по другим фильтрам: на больших данных каскад неподъемен, а список значений полный).
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
            Background    = Brushes.White,   // белая карточка на серой подложке (как в сайдбаре)
            Margin       = new Thickness(0, 0, 0, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(search);

        // Наполняется по приходе данных (см. фоновую загрузку ниже).
        var checkedSet       = new HashSet<string>();
        var syncingSelectAll = false;

        var selectAll = new CheckBox
        {
            Content   = "(Выделить все)",
            FontSize  = fontBase,
            IsChecked = !_filters.ContainsKey(def.Key),
            Cursor    = new Cursor(StandardCursorType.Hand),
        };

        // Список значений — виртуализирующий ListBox: рендерятся только видимые строки,
        // поэтому показываются ВСЕ уникальные значения (без лимита) и без зависания даже
        // при сотнях тысяч значений. «Выделить все» закреплен сверху.
        var shown    = new AvaloniaList<FilterValueVm>();
        var vmCache  = new Dictionary<string, FilterValueVm>();
        FilterValueVm Vm(string raw, int count) =>
            vmCache.TryGetValue(raw, out var v) ? v
                : vmCache[raw] = new FilterValueVm(raw, DisplayValue(raw, def.FieldType, def.Separator, def.NumberFormat), count,
                                                   checkedSet, () => SyncSelectAll());

        var valuesList = new ListBox
        {
            // Высоты нет: список занимает остаток строки карточки фиксированной высоты и
            // сам ужимается, когда снизу появляется горизонтальная полоса.
            Background       = Brushes.Transparent,
            BorderThickness  = new Thickness(0),
            Padding          = new Thickness(0),
            ItemsSource      = shown,
            ItemTemplate     = BuildFilterValueTemplate(fontBase, secondary, metrics),
        };
        valuesList.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            // Вертикальный отступ строк — как у списка свойств (StackPanel Spacing=4):
            // 2px сверху/снизу дают такой же воздух между чекбоксами, иначе они слипаются.
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0, 2)),
                        new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });

        // Строки без подсветки наведения/выбора (синей/серой) — взаимодействуем только
        // с чекбоксом, поэтому фон контейнера строки прозрачен во всех состояниях.
        void FlatRow(params string[] pseudo) =>
            valuesList.Styles.Add(new Style(x =>
            {
                Avalonia.Styling.Selector s = x.OfType<ListBoxItem>();
                foreach (var p in pseudo) s = s.Class(p);
                return s.Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter");
            })
            {
                Setters = { new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Brushes.Transparent) },
            });
        FlatRow();
        FlatRow(":pointerover");
        FlatRow(":selected");
        FlatRow(":selected", ":pointerover");

        // «Выделить все» — закрепленная шапка списка; полоса прокрутки стоит на ее уровне
        // (отдельной колонкой), как вертикальная полоса таблицы стоит на уровне шапки колонок.
        // gap=2: вместе с верхним отступом первой строки (Padding 0,2) дает под шапкой
        // ровно такой же просвет, как между значениями.
        var valuesBox = ScrollBarLink.HeaderedList(selectAll, valuesList, gap: 2,
                                                   scroller: metrics.Track);

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
            Background       = Brushes.White,   // белая карточка списка на серой подложке (как в сайдбаре)
            BorderBrush     = (IBrush)this.FindResource("BrushSeparator")!,
            CornerRadius    = radius,
            // Padding НУЛЕВОЙ: полосы прокрутки списка прилегают ко всем краям карточки без
            // белого зазора, а воздух вокруг содержимого задает ScrollBarLink.HeaderedList.
            // ClipToBounds — квадратные углы полос срезаются скруглением карточки.
            Padding         = new Thickness(0),
            ClipToBounds    = true,
            // Фикс высота (стандарт) — список не «прыгает» при замене «Загрузка…».
            Height          = (double)this.FindResource("PopupListHeight")!,
            Child           = loadingText,
        };
        // Карточка живет в контенте флайаута, обернутом в зум-трансформ — токен обычный.
        valuesHost.Bind(Border.BorderThicknessProperty, valuesHost.GetResourceObservable("BorderLine"));
        panel.Children.Add(valuesHost);

        List<(string Raw, int Count)> Matched()
        {
            var q = search.Text?.Trim() ?? "";
            return q.Length == 0
                ? counts
                : counts.Where(c => DisplayValue(c.Raw, def.FieldType, def.Separator, def.NumberFormat)
                    .Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        void RebuildList()
        {
            shown.Clear();
            shown.AddRange(Matched().Select(c => Vm(c.Raw, c.Count)));
            // Ширина строки фиксируется по текущему набору — иначе экстент горизонтальной
            // прокрутки считался бы только по видимым строкам и полоса прыгала бы.
            metrics.Measure(shown.Select(v => v.Display), shown.Select(v => v.CountText),
                            fontBase, family);
        }
        rebuildListRef = RebuildList;

        // «Выделить все» отражает состояние видимых значений: снял одну галку —
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
            // «Выделить все» действует на видимое (после поиска) подмножество.
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
            // Данные еще грузятся — применять нечего (фильтр по колонке не трогаем).
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
        // отсоединенными контролами безвредны.
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
                // «Загрузка…» → список значений; пустой список показывает надпись
                // «Список пуст» / «Ничего не найдено» вместо голой белой карточки.
                valuesHost.Child = Controls.Components.EmptyListHint.Wrap(
                    valuesList, search, secondary, fontBase, content: valuesBox);
                RebuildList();
                SyncSelectAll();
                loaded = true;
            });
        });

        return panel;
    }

    // Иконка в стандарте сайдбара: Viewbox «Средний» поверх сетки 24×24.
    private Viewbox MakeStrokeIcon(Geometry data, IBrush stroke) => new()
    {
        Width  = IconSize("IconSizeMedium"),
        Height = IconSize("IconSizeMedium"),
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
                    StrokeThickness = IconSize("IconStrokeMedium"),
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

    // Разделитель в попапе фильтра: контент флайаута обернут в зум-трансформ
    // (WrapPopupContent) → токен обычный; Border пустой → Aliased защелкивает линию.
    private Border MakeSeparator()
    {
        var sep = new Border
        {
            Background = (IBrush)this.FindResource("BrushSeparator")!,
            Margin     = new Thickness(0, 6),
        };
        sep.Bind(HeightProperty, sep.GetResourceObservable("SeparatorThickness"));
        sep.Bind(UseLayoutRoundingProperty, sep.GetResourceObservable("LineUseLayoutRounding"));
        Avalonia.Media.RenderOptions.SetEdgeMode(sep, Avalonia.Media.EdgeMode.Aliased);
        return sep;
    }

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
    private static IDataTemplate BuildFilterValueTemplate(double fontBase, IBrush secondary,
                                                          FilterValueMetrics metrics) =>
        new FuncDataTemplate<FilterValueVm>((_, _) =>
        {
            var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(FilterValueVm.Display)));
            // Ширина ВСЕХ строк одинаковая (по самому широкому значению) — иначе экстент
            // горизонтальной прокрутки считался бы по видимым строкам и полоса дергалась.
            name.Bind(Layoutable.MinWidthProperty,
                      new Binding(nameof(FilterValueMetrics.TextMinWidth)) { Source = metrics });

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

            var cnt = new TextBlock { Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
            cnt.Bind(TextBlock.TextProperty, new Binding(nameof(FilterValueVm.CountText)));

            // Счетчик ЗАКРЕПЛЕН у правого края видимой области: сдвиг компенсирует
            // горизонтальную прокрутку, фон непрозрачный — длинный текст уезжает под него.
            // Плашка обхватывает САМО число (Gap слева), поэтому просвет до закрываемого
            // текста одинаковый в любой строке — не зависит от количества цифр.
            var pin = new TranslateTransform();
            pin.Bind(TranslateTransform.XProperty,
                     new Binding(nameof(FilterValueMetrics.Pin)) { Source = metrics });
            var cntHost = new Border
            {
                Background          = Brushes.White,
                Padding             = new Thickness(FilterValueMetrics.Gap, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child               = cnt,
                RenderTransform     = pin,
            };

            // Прозрачное МЕСТО под счетчик — одинаковой ширины во всех строках, чтобы от
            // количества цифр не зависела ширина строки, а с ней и экстент прокрутки.
            var cntSlot = new Border { Child = cntHost };
            cntSlot.Bind(Layoutable.MinWidthProperty,
                         new Binding(nameof(FilterValueMetrics.CountMinWidth)) { Source = metrics });

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(cb, 0);      row.Children.Add(cb);
            Grid.SetColumn(cntSlot, 1); row.Children.Add(cntSlot);
            return row;
        });

    // Строка списка значений фильтра. Checked читает/пишет общий checkedSet; onChanged
    // обновляет состояние «(Выделить все)».
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

    private static string DisplayValue(string raw, string fieldType, string separator = ", ", NumberFormat? numberFormat = null)
    {
        // Связь: значение фильтра = название связанной записи (Title); пусто → «(нет связей)».
        if (fieldType == "relation") return raw == "" ? "(нет связей)" : OneLine(raw);
        if (raw == "") return "(Пусто)";
        if (fieldType == "checkbox") return IsTruthy(raw) ? "✓" : "—";
        if (fieldType == "multiselect") return OneLine(MultiSelectValue.Display(raw, separator));
        if (fieldType == "number") return NumberFormatValue.Format(raw, numberFormat ?? NumberFormat.Default);
        if (fieldType == "file") return FileValue.DisplayName(raw);   // имя файла, а не абсолютный путь
        return OneLine(raw);
    }

    // Значение в списке фильтра — всегда ОДНА строка: многострочный текст ячейки иначе
    // растягивает строку списка по высоте (и ширину меряет по самой длинной из своих
    // строк). Схлопываем переводы строк в пробел — как Detail в истории (ChangeDetail).
    // Фильтруется все равно исходное значение (FilterValueVm.Raw), меняется только показ.
    private static string OneLine(string s) =>
        s.Contains('\n') || s.Contains('\r') ? s.ReplaceLineEndings(" ").Trim() : s;

    /// <summary>
    /// Применяет активные фильтры/сортировку. Без них таблица работает на полном
    /// виртуализированном списке; с ними — фильтр считается в SQL (список id), показ
    /// тоже виртуализирован по этому списку. Все постранично, без загрузки в память.
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
            _activeRows = _virtualRows;   // вернулись к нефильтрованному видимому списку
            return;
        }

        // Фильтр уходит в SQL (только id совпавших записей), сортировка — тем же компаратором
        // по значениям ОДНОЙ колонки, показ виртуализируется постранично по списку id. Не трогаем
        // нефильтрованные _virtualRows/_virtualView — создаем временный источник; при снятии
        // фильтра вернемся к DefaultItemsSource.
        var ids = _repository.GetFilteredRecordIds(_collectionId, _filters,
                                                   excludeHidden: _hiddenRecords.Count > 0);
        if (_sortKey is { } sk)
        {
            var def  = _colDefs.FirstOrDefault(d => d.Key == sk);
            var cmp  = BuildComparer(def?.FieldType ?? "text", _sortAsc);
            var vals = _repository.GetColumnValues(_collectionId, sk);
            ids.Sort((a, b) => cmp(vals.GetValueOrDefault(a, ""), vals.GetValueOrDefault(b, "")));
        }

        _relLinks.Clear();   // новый срез строк (фильтр/сортировка) — прежний прогрев связей неактуален
        var filteredRows = new VirtualRowList(_repository, _collectionId, _fields, recordIds: ids, relLinks: _relLinks);
        filteredRows.RecordEdited += (_, rid) => RecordValueEdited?.Invoke(this, rid);
        _activeRows = filteredRows;   // видимый список теперь отфильтрованный — точечные рефреши сюда
        RecordsGrid.ItemsSource = new VirtualCollectionView(filteredRows);
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
    //
    // ГЛАВНОЕ ПРАВИЛО: движимые колонки ВСЕГДА остаются звездными (Star), в обоих режимах.
    // Именно наличие хотя бы одной видимой Star-колонки заставляет Avalonia каждым
    // проходом разметки доводить сумму ширин до ширины вьюпорта, а внутренняя filler-колонка
    // получает нулевую ширину — только поэтому служебная «+» прижата к правому краю.
    // Перевод колонок в пиксельную ширину это ломает: справа от «+» появляется полоса
    // filler'а с лишней вертикальной линией. Пришпилено к Avalonia 11.2.3.
    //
    // Ручная ширина выражается так: звездный ВЕС колонки == ее ширине в пикселях, и та же
    // величина ставится в MinWidth. Дальше все делает сам движок:
    //   сумма > вьюпорта — колонки упираются в MinWidth, не сжимаются, появляется
    //                      горизонтальная прокрутка (растянутый столбец «уводит» соседей вправо);
    //   сумма < вьюпорта — остаток раздается пропорционально весам, пустоты справа нет.
    // Наш код задает базу, движок — гарантированно попадает в ширину вьюпорта.

    // Модель ручных ширин применима: включен ручной режим и хоть у одной видимой движимой
    // колонки ширина задана. Пока модель пуста, ручной режим выглядит как стандартный —
    // включение режима больше не схлопывает все колонки до минимума.
    private bool HasManualWidths =>
        _manualColResize && _colWidths.Count > 0 &&
        MovableColumns().Any(c => _colWidths.ContainsKey(c.Def.Key));

    // Квант раскладки — один ФИЗИЧЕСКИЙ пиксель в DIP (учитывает DPI монитора и зум).
    private double Quant() => 1.0 / LineMetrics.EffectiveScale(this);

    private double Quantize(double w)
    {
        var q = Quant();
        return Math.Round(w / q) * q;
    }

    // Ширина колонки по модели; не задана — стандартная.
    private double ModelWidth(string key) =>
        _colWidths.TryGetValue(key, out var w) && w > 0 ? w : MinColWidth;

    // Видимые движимые колонки (без служебных «№» и «+») в порядке слева направо.
    // Связка _colDefs[i] ↔ Columns[i] держится на индексе, поэтому сверяем Tag: если
    // наборы разошлись, лучше отдать пусто, чем править чужие колонки.
    private IEnumerable<(TableColumnDef Def, DataGridColumn Col)> MovableColumns()
    {
        if (_colDefs.Count != RecordsGrid.Columns.Count) yield break;

        for (var i = 0; i < _colDefs.Count; i++)
        {
            var def = _colDefs[i];
            var col = RecordsGrid.Columns[i];
            if (!def.IsStarWidth || !col.IsVisible) continue;
            if (!Equals(col.Tag, def.Tag ?? def.Key)) yield break;
            yield return (def, col);
        }
    }

    // Запись ТОЧНОГО звездного веса (вес == ширине в пикселях). Присваивание Width с ТЕМ ЖЕ
    // типом длины Avalonia трактует как ресайз и перекладывает дельту на соседей справа;
    // присваивание со СМЕНОЙ типа — просто запись. Поэтому сначала переключаем тип, потом
    // ставим целевое значение. Явные desired/display убирают NaN-коэрцию.
    // Пришпилено к Avalonia 11.2.3 (DataGridColumn.Width → Resize).
    private static void SetColWidth(DataGridColumn col, double px)
    {
        var target = new DataGridLength(px, DataGridLengthUnitType.Star, px, px);
        if (col.Width.IsStar)
            col.Width = new DataGridLength(Math.Max(1, col.ActualWidth));
        col.Width = target;
    }

    private void ApplyColumnWidths()
    {
        if (_repository is null) return;

        var manual = HasManualWidths;
        var cols   = MovableColumns().ToList();

        // Два прохода обязательны: сеттер MinWidth изнутри дергает пересчет ширин соседей
        // справа и портит уже выставленные веса. Второй проход переписывает их из модели.
        foreach (var (def, col) in cols)
            col.MinWidth = manual ? Quantize(ModelWidth(def.Key)) : def.MinWidth;

        foreach (var (def, col) in cols)
        {
            if (manual) SetColWidth(col, Quantize(ModelWidth(def.Key)));
            // Стандартный режим — равные веса, ширину считает движок (как было всегда).
            else col.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        }

        ScheduleColHandlesRefresh();
    }

    // Ширина, доступная под движимые колонки: столько, чтобы таблица ровно заполнила окно.
    // Занижаем на один физический пиксель — остаток дораздаст звездный движок, а фантомная
    // горизонтальная полоса прокрутки от промаха в доли пиксела не появится.
    private double MovableFillWidth()
    {
        _headersPresenter ??= RecordsGrid.GetVisualDescendants()
                                         .OfType<DataGridColumnHeadersPresenter>()
                                         .FirstOrDefault();

        var total = _headersPresenter?.Bounds.Width ?? RecordsGrid.Bounds.Width;
        if (total <= 0) return 0;

        var service = 0.0;
        for (var i = 0; i < _colDefs.Count && i < RecordsGrid.Columns.Count; i++)
            if (!_colDefs[i].IsStarWidth && RecordsGrid.Columns[i].IsVisible)
                service += RecordsGrid.Columns[i].ActualWidth;

        return Math.Max(0, total - service - Quant());
    }

    // Зафиксировать в модели то, что человек сейчас видит на экране. Вызывается в начале
    // жеста: до первого перетаскивания модель пуста (колонки поровну), и жест должен
    // отталкиваться именно от текущей раскладки, а не от стандартных ширин.
    private bool MaterializeColumnWidths()
    {
        var cols = MovableColumns().ToList();
        if (cols.Count == 0 || cols.Any(c => c.Col.ActualWidth <= 0)) return false;

        foreach (var (def, col) in cols)
            _colWidths[def.Key] = Quantize(col.ActualWidth);

        return true;
    }

    private void OnGridLayoutUpdated(object? s, EventArgs e)
    {
        // Заливка выделенных колонок за скроллом не следит: цвет живет в кисти колонки,
        // которую держат сами ячейки (см. ColumnSelectionBrush) — пересчет по
        // LayoutUpdated ей не нужен.

        // Индикатор нижней панели следит за прокруткой и ресайзом: пересчет дешевый
        // (обход реализованных строк), наверх уходит только изменение диапазона.
        RefreshVisibleRange();

        // Полоски ресайза строк следуют за скроллом/ресайзом.
        if (_manualRowResize && !_isResizingRow)
            ScheduleRowHandlesRefresh();

        // То же для полосок ресайза колонок.
        if (_manualColResize && !_isResizingCol)
            ScheduleColHandlesRefresh();

        // Во время самого перетаскивания снап выключен — иначе жест становится дерганым.
        if (_snapPending || _isResizingCol) return;

        // Снапим Star-колонки к ФИЗИЧЕСКОЙ пиксельной сетке (квант = 1 физ. пиксель в DIP
        // с учетом DPI и зума; при зуме 1 и DPI 1x — целые DIP) — иначе DataGrid рисует
        // вертикальные грид-линии в сабпиксельных позициях и они округляются по-разному
        // с линиями заголовков. Нужен в ОБОИХ режимах: ручные ширины тоже дают дробные
        // доли при раздаче остатка.
        var q = Quant();
        var hasFractional = false;
        foreach (var (_, col) in MovableColumns())
        {
            if (col.ActualWidth <= 0 || !col.Width.IsStar) continue;
            var units = col.ActualWidth / q;
            if (Math.Abs(units - Math.Round(units)) > 0.001) { hasFractional = true; break; }
        }
        if (!hasFractional) return;

        _snapPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _snapPending = false;
            SnapColumnWidths();
        }, DispatcherPriority.Background);
    }

    // Посадить границы колонок на физическую пиксельную сетку, сохранив пропорции.
    // totalUnits берем из ФАКТИЧЕСКОЙ раскладки (сумма ActualWidth), а не из своей оценки
    // ширины вьюпорта: только так сумма весов гарантированно равна доступной ширине и
    // следующий проход разметки уже не видит дробного — снап сходится за один шаг.
    private void SnapColumnWidths()
    {
        var cols = MovableColumns()
            .Where(c => c.Col.Width.IsStar && c.Col.ActualWidth > 0)
            .ToList();
        if (cols.Count == 0) return;

        var q          = Quant();
        var totalUnits = (int)Math.Round(cols.Sum(c => c.Col.ActualWidth) / q);
        var units      = new int[cols.Count];

        if (HasManualWidths)
        {
            // Ручные ширины: доли считаем ОТ МОДЕЛИ, а не от текущей раскладки — иначе
            // многократные ресайзы окна накапливали бы дрейф. Ниже своей ширины колонка
            // не опускается (это и дает горизонтальную прокрутку при переполнении).
            var minUnits = cols.Select(c => Math.Max(1, (int)Math.Round(ModelWidth(c.Def.Key) / q))).ToArray();
            var minSum   = minUnits.Sum();

            if (totalUnits <= minSum)
            {
                Array.Copy(minUnits, units, units.Length);
            }
            else
            {
                // Остаток — методом наибольших остатков пропорционально ширинам колонок:
                // сумма долей точно равна остатку, без потерянных и лишних пикселей.
                var extra = totalUnits - minSum;
                var share = new double[cols.Count];
                for (var i = 0; i < cols.Count; i++)
                {
                    share[i] = extra * (double)minUnits[i] / minSum;
                    units[i] = minUnits[i] + (int)Math.Floor(share[i]);
                }
                var left = extra - (units.Sum() - minSum);
                foreach (var i in Enumerable.Range(0, cols.Count)
                             .OrderByDescending(i => share[i] - Math.Floor(share[i]))
                             .Take(Math.Max(0, left)))
                    units[i]++;
            }
        }
        else
        {
            // Стандартный режим: колонки делят ширину поровну.
            var baseU = totalUnits / cols.Count;
            var rem   = totalUnits % cols.Count;
            for (var i = 0; i < cols.Count; i++) units[i] = baseU + (i < rem ? 1 : 0);
        }

        // Star-вес == целому числу ФИЗИЧЕСКИХ пикселей: сумма весов равна доступной ширине,
        // каждая колонка получает ровно weight DIP, границы ложатся на физическую сетку.
        // MinWidth и саму модель снап НЕ трогает — он выравнивает только показ.
        for (var i = 0; i < cols.Count; i++)
            SetColWidth(cols[i].Col, units[i] * q);
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

internal sealed class FieldDisplayConverter(string fieldType, string separator = ", ", NumberFormat? numberFormat = null) : IValueConverter
{
    public object? Convert(object? v, Type t, object? p, CultureInfo c)
    {
        var s = v?.ToString() ?? "";
        return fieldType switch
        {
            "checkbox"    => s is "1" or "true" or "True" ? "✓" : "",
            "date"        => DateValue.TryParse(s, out var dv) ? dv.ToDisplay() : s,
            "multiselect" => MultiSelectValue.Display(s, separator),
            "number"      => NumberFormatValue.Format(s, numberFormat ?? NumberFormat.Default),
            _             => s,
        };
    }

    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        v?.ToString() ?? "";
}

// Отображение «вычисляемых» типов (аудит-метаданные записи). Даты создания/изменения хранятся
// как ISO "o" (полный момент с долями секунды и смещением — DateValue его не парсит), поэтому
// разбираем round-trip и показываем «дд.MM.гггг чч:мм»; создатель/изменивший — снимок имени,
// показываем как есть. Пустое (старые записи без автора) — пустая ячейка.
internal sealed class ComputedFieldDisplayConverter(string fieldType) : IValueConverter
{
    public object? Convert(object? v, Type t, object? p, CultureInfo c) =>
        FieldTypeInfo.FormatComputedDisplay(v?.ToString(), fieldType);

    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        v?.ToString() ?? "";
}
