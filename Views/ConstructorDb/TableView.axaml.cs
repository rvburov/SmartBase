using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;
using MyPlanner.ViewModels.ConstructorDb;

namespace MyPlanner.Views.ConstructorDb;

public partial class TableView : UserControl
{
    // ── Публичные события ─────────────────────────────────────────────────────
    public event EventHandler<int>? RecordClicked;
    public event EventHandler<int>? RecordDeleteRequested;
    public event EventHandler<int>? RecordDuplicateRequested;
    // bool = открывать ли запись справа после создания. «+»-строка создаёт без
    // открытия (false), контекстное «Создать запись» — с открытием (true).
    public event EventHandler<bool>? RecordCreateRequested;
    public event EventHandler?      PropertyCreateRequested;
    // Создать свойство сразу справа от колонки (string = ключ колонки-инициатора).
    public event EventHandler<string>? PropertyCreateAfterRequested;
    public event EventHandler<int>? PropertyEditRequested;
    public event EventHandler<int>? PropertyDuplicateRequested;
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
    private bool _snapPending;
    private bool _highlightPending;   // отложенная перестройка рамок выделения
    private List<DbField>        _fields  = [];
    private List<TableColumnDef> _colDefs = [];
    private VirtualRowList?      _virtualRows;
    // Пользовательские подписи системных колонок: ключ → отображаемое имя.
    private Dictionary<string, string> _systemNames = [];
    // Сохранённый порядок колонок: ключ → позиция (пусто, если ещё не двигали).
    private Dictionary<string, int> _columnOrder = [];
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

    public TableView()
    {
        InitializeComponent();
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

    private void OnTableKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete && e.Key != Key.Back) return;
        if (_selectedColumnKeys.Count == 0) return;

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
    }

    // ── Публичный API ─────────────────────────────────────────────────────────

    public void Load(ConstructorRepository repository, int collectionId)
    {
        _repository   = repository;
        _collectionId = collectionId;

        // Фильтры/сортировка не переносятся между коллекциями.
        _filters.Clear();
        _sortKey = null;

        var (colResize, _, _) = repository.GetUiSettings(collectionId);
        _manualColResize = colResize;

        Reload();
    }

    public void Clear()
    {
        _repository   = null;
        _collectionId = 0;
        _fields       = [];
        _colDefs      = [];
        _systemNames  = [];
        _columnOrder  = [];
        _virtualRows  = null;
        _filters.Clear();
        _chevronPaths.Clear();
        _sortKey = null;
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

    public void SetRowResizeMode(bool _) { }

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

        ClearColumnSelection();   // колонки пересобираются — снимаем выделение

        _colDefs = BuildColDefs();
        ColumnsBuilt?.Invoke(this, _colDefs);

        _virtualRows = new VirtualRowList(_repository, _collectionId, _fields);

        BuildColumns();

        RecordsGrid.ItemsSource = _virtualRows;
        RecordsGrid.ContextMenu = BuildGridContextMenu();

        ApplyColumnWidths();
        ApplyFilters();   // переустановит ItemsSource, если фильтр/сортировка активны
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

    // Перемещение колонки влево/вправо: пишем порядок в БД, перестраиваем таблицу.
    // Reload сохраняет активные фильтры/сортировку.
    private void MoveColumn(string key, bool left)
    {
        if (_repository is null) return;
        _repository.MoveColumn(_collectionId, key, left, MovableKeysInOrder());
        Reload();
    }

    // Можно ли двигать колонку влево/вправо (не крайняя ли она среди движимых).
    private bool CanMoveColumn(string key, bool left)
    {
        var keys = MovableKeysInOrder();
        var idx  = keys.IndexOf(key);
        if (idx < 0) return false;
        return left ? idx > 0 : idx < keys.Count - 1;
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

            return new TextBlock
            {
                Margin              = new Thickness(6, 0, 6, 0),
                VerticalAlignment   = VerticalAlignment.Center,
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
                    var picker = new CalendarDatePicker
                    {
                        BorderThickness   = new Thickness(0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    if (DateTime.TryParseExact(row[def.Key], "dd.MM.yyyy", null,
                            DateTimeStyles.None, out var dt))
                        picker.SelectedDate = dt;
                    picker.SelectedDateChanged += (_, _) =>
                        row[def.Key] = picker.SelectedDate?.ToString("dd.MM.yyyy") ?? "";
                    return picker;
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

    // ── Клики и контекстные меню ───────────────────────────────────────────────

    private void OnCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        // Клик по любой ячейке снимает выделение колонки.
        ClearColumnSelection();

        if (e.Row.DataContext is not TableRowVm row) return;

        if (row.IsAddRow)
        {
            RecordCreateRequested?.Invoke(this, false); // создать без открытия
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

        if (e.Column.DisplayIndex == 0
            && e.PointerPressedEventArgs.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            RecordClicked?.Invoke(this, row.RecordId);
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

        // Ничего не изменилось — дерево не трогаем (разрывает layout-цикл).
        if (HighlightsEqual(target, _lastHighlights)) return;

        _lastHighlights.Clear();
        _lastHighlights.AddRange(target);

        panel.Children.Clear();
        if (target.Count == 0) return;

        var accent = (IBrush)this.FindResource("BrushAccent")!;
        foreach (var (x, w, h) in target)
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

    // Заголовок колонки по индексу. Заголовки в визуальном дереве идут в порядке
    // колонок и позиционированы по X — сортируем по фактической X-координате и
    // берём colIdx-й (надёжнее матчинга по ширине, т.к. ширины могут совпадать).
    private DataGridColumnHeader? FindHeaderForColumn(int colIdx)
    {
        var headers = RecordsGrid.GetVisualDescendants()
            .OfType<DataGridColumnHeader>()
            .Where(h => h.Bounds.Width > 0)
            .OrderBy(h => h.TranslatePoint(new Point(0, 0), RecordsGrid)?.X ?? double.MaxValue)
            .ToList();

        return colIdx >= 0 && colIdx < headers.Count ? headers[colIdx] : null;
    }

    private ContextMenu BuildGridContextMenu()
    {
        var menu = new ContextMenu();
        var mi = new MenuItem { Header = "Создать запись" };
        mi.Click += (_, _) => RecordCreateRequested?.Invoke(this, true);
        menu.Items.Add(mi);
        return menu;
    }

    private ContextMenu BuildRowContextMenu(int recordId)
    {
        var menu = new ContextMenu();

        var miCreate = new MenuItem { Header = "Создать запись" };
        miCreate.Click += (_, _) => RecordCreateRequested?.Invoke(this, true);
        menu.Items.Add(miCreate);

        menu.Items.Add(new Separator());

        var miEdit = new MenuItem { Header = Controls.Components.MenuIcons.EditHeader() };
        miEdit.Click += (_, _) => RecordClicked?.Invoke(this, recordId);
        menu.Items.Add(miEdit);

        var miDuplicate = new MenuItem { Header = Controls.Components.MenuIcons.DuplicateHeader() };
        miDuplicate.Click += (_, _) => RecordDuplicateRequested?.Invoke(this, recordId);
        menu.Items.Add(miDuplicate);

        var miDelete = MakeDeleteMenuItem();
        miDelete.Click += (_, _) => RecordDeleteRequested?.Invoke(this, recordId);
        menu.Items.Add(miDelete);

        return menu;
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
        if (row.IsAddRow) return;
        e.Row.ContextMenu = BuildRowContextMenu(row.RecordId);
    }

    // ── Фильтр и сортировка колонок (Excel-стиль) ──────────────────────────────

    private void ShowFilterFlyout(Control anchor, TableColumnDef def)
    {
        if (_repository is null) return;

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.FlyoutPresenterClasses.Add("filterFlyout");
        flyout.Content = BuildFilterPanel(def, () => flyout.Hide());
        flyout.ShowAt(anchor);
    }

    private Control BuildFilterPanel(TableColumnDef def, Action close)
    {
        var secondary = (IBrush)this.FindResource("BrushSecondaryText")!;
        var fontBase  = (double)this.FindResource("FontSizeBase")!;
        var radius    = (CornerRadius)this.FindResource("RadiusBase")!;

        var panel = new StackPanel { Width = 240, Margin = new Thickness(12), Spacing = 2 };

        // ── Данные ──
        // Загружаем строки сразу, до RenderSort, чтобы замыкания могли ссылаться на allRows.
        var allRows = _repository is null ? [] : LoadAllRows();
        var domain  = allRows.Select(r => r[def.Key]).ToHashSet();

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
            var sk = pendingSort.HasValue ? def.Key : _sortKey;
            var sa = pendingSort ?? _sortAsc;
            counts = LoadValueCounts(def, allRows, sk, sa);
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

        var checkedSet = _filters.TryGetValue(def.Key, out var f)
            ? new HashSet<string>(f)
            : new HashSet<string>(domain);
        var syncingSelectAll = false;

        var selectAll = new CheckBox
        {
            Content   = "(Выделить всё)",
            FontSize  = fontBase,
            IsChecked = !_filters.ContainsKey(def.Key),
        };

        // «Выделить всё» прокручивается вместе со значениями (не закреплён),
        // весь список — в рамке.
        var listPanel     = new StackPanel { Spacing = 2 };
        var scrollContent = new StackPanel { Spacing = 2 };
        scrollContent.Children.Add(selectAll);
        scrollContent.Children.Add(listPanel);

        panel.Children.Add(new Border
        {
            BorderBrush     = (IBrush)this.FindResource("BrushSeparator")!,
            BorderThickness = new Thickness(1),
            CornerRadius    = radius,
            Padding         = new Thickness(8, 6),
            Child = new ScrollViewer
            {
                MaxHeight = 230,
                Content   = scrollContent,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            },
        });

        // Рендерим не более 500 чекбоксов за раз — при большем количестве уникальных
        // значений пользователь уточняет поиск (как в Excel).
        const int MaxRender = 500;

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
            listPanel.Children.Clear();
            var matched = Matched();
            foreach (var (raw, cnt) in matched.Take(MaxRender))
            {
                var nameText = new TextBlock
                {
                    Text              = DisplayValue(raw, def.FieldType),
                    TextTrimming      = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var cb = new CheckBox
                {
                    Content   = nameText,
                    FontSize  = fontBase,
                    IsChecked = checkedSet.Contains(raw),
                    HorizontalAlignment        = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Margin    = new Thickness(0),
                };
                var cntText = new TextBlock
                {
                    Text              = cnt.ToString(),
                    Foreground        = secondary,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    // Резерв под полосу прокрутки (overlay-скроллбар Fluent перекрывает
                    // контент) — иначе цифры заходят под бегунок и выглядят неровно.
                    Margin            = new Thickness(0, 0, 12, 0),
                };
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                Grid.SetColumn(cb, 0);
                Grid.SetColumn(cntText, 1);
                row.Children.Add(cb);
                row.Children.Add(cntText);
                cb.IsCheckedChanged += (_, _) =>
                {
                    if (cb.IsChecked == true) checkedSet.Add(raw);
                    else checkedSet.Remove(raw);
                    SyncSelectAll();
                };
                listPanel.Children.Add(row);
            }
            if (matched.Count > MaxRender)
                listPanel.Children.Add(new TextBlock
                {
                    Text       = $"…ещё {matched.Count - MaxRender} — уточните поиск",
                    Foreground = secondary,
                    FontSize   = fontBase,
                    Margin     = new Thickness(4, 4, 0, 0),
                });
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
        RebuildList();
        SyncSelectAll();

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
            if (pendingSort is bool asc) { _sortKey = def.Key; _sortAsc = asc; }
            else if (_sortKey == def.Key) _sortKey = null;

            // Выбраны все значения колонки (а не только видимые) — фильтр не нужен.
            if (domain.All(checkedSet.Contains)) _filters.Remove(def.Key);
            else _filters[def.Key] = new HashSet<string>(checkedSet);
            ApplyFilters();
            close();
        };
        panel.Children.Add(okBtn);

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

    // sortKey/sortAsc — pending-сортировка из панели фильтра (может отличаться от _sortKey).
    private List<(string Raw, int Count)> LoadValueCounts(
        TableColumnDef def, List<TableRowVm> allRows, string? sortKey, bool sortAsc)
    {
        // Excel-каскад: список значений и счётчики считаем по строкам, прошедшим
        // фильтры всех ОСТАЛЬНЫХ колонок. Собственный фильтр колонки не учитываем —
        // иначе снятые галки исчезали бы из списка и их нельзя было бы вернуть.
        IEnumerable<TableRowVm> rows = allRows;
        foreach (var (key, allowed) in _filters)
            if (key != def.Key)
                rows = rows.Where(r => allowed.Contains(r[key]));

        // Применяем pending-сортировку, чтобы порядок значений в списке совпадал
        // с порядком строк в таблице — как только пользователь сменит выбор сортировки,
        // список сразу перестроится. Если сортировки нет — порядок строк таблицы.
        if (sortKey is not null)
        {
            var cmp = BuildComparer(def.FieldType, sortAsc);
            rows = rows.OrderBy(r => r[sortKey], Comparer<string>.Create((a, b) => cmp(a, b)));
        }

        // Дедупликация с сохранением первого вхождения (= порядок из таблицы).
        // «(Пусто)» при отсутствии сортировки идёт в конец.
        var seen   = new HashSet<string>();
        var result = new List<(string Raw, int Count)>();
        var counts = rows.GroupBy(r => r[def.Key]).ToDictionary(g => g.Key, g => g.Count());

        foreach (var row in rows)
        {
            var v = row[def.Key];
            if (seen.Add(v))
                result.Add((v, counts[v]));
        }

        if (sortKey is null)
        {
            // Без сортировки: пустые в конец, остальное в порядке появления (по таблице).
            var nonEmpty = result.Where(x => x.Raw != "").ToList();
            var empty    = result.Where(x => x.Raw == "").ToList();
            result = [.. nonEmpty, .. empty];
        }

        return result;
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
            if (!ReferenceEquals(RecordsGrid.ItemsSource, _virtualRows))
                RecordsGrid.ItemsSource = _virtualRows;
            return;
        }

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

    private List<TableRowVm> LoadAllRows()
    {
        var records = _repository!.GetRecords(_collectionId);
        var values  = _repository.GetFieldValuesByCollection(_collectionId);

        var rows = new List<TableRowVm>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            var id  = rec.Id!.Value;
            rows.Add(new TableRowVm(_repository, id, i + 1,
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
        DateTime.TryParseExact(s, "dd.MM.yyyy", null, DateTimeStyles.None, out var dt)
            ? dt : DateTime.MaxValue;

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
        return fieldType == "checkbox" ? (s is "1" or "true" or "True" ? "✓" : "") : s;
    }

    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        v?.ToString() ?? "";
}
