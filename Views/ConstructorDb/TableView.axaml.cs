using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;
using MyPlanner.ViewModels.ConstructorDb;

namespace MyPlanner.Views.ConstructorDb;

public partial class TableView : UserControl
{
    // ── Публичные события ─────────────────────────────────────────────────────
    public event EventHandler<int>? RecordClicked;
    public event EventHandler<int>? RecordDeleteRequested;
    // bool = открывать ли запись справа после создания. «+»-строка создаёт без
    // открытия (false), контекстное «Создать запись» — с открытием (true).
    public event EventHandler<bool>? RecordCreateRequested;
    public event EventHandler?      PropertyCreateRequested;
    public event EventHandler<int>? PropertyEditRequested;
    public event EventHandler<int>? PropertyDeleteRequested;

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
    private List<DbField>        _fields  = [];
    private List<TableColumnDef> _colDefs = [];
    private VirtualRowList?      _virtualRows;

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

        // Пересобирать колонки нельзя — DataGrid теряет ресайз-грипперы. Вместо
        // этого переключаем IsVisible у filler-колонки и пересчитываем ширины.
        ApplyColumnWidths();
    }

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
        var defs = new List<TableColumnDef>
        {
            new() { Key = TableColumnDef.NumKey,    Header = "№",
                    IsStarWidth = false, FixedWidth = NumColWidth,
                    CanResize = false,   IsReadOnly = true },

            new() { Key = TableRowVm.TitleKey, Header = "Название",
                    FieldType = "text",  IsStarWidth = true, MinWidth = MinColWidth },

            new() { Key = TableRowVm.DescKey,  Header = "Описание",
                    FieldType = "text",  IsStarWidth = true, MinWidth = MinColWidth },
        };

        foreach (var f in _fields)
        {
            var fid = f.Id!.Value;
            defs.Add(new TableColumnDef
            {
                Key         = fid.ToString(),
                Header      = f.Name,
                FieldType   = f.FieldType,
                IsStarWidth = true,
                MinWidth    = MinColWidth,
                IsReadOnly  = f.FieldType == "checkbox",
                Options     = ParseSelectOptions(f),
                Tag         = fid,
                BuildHeaderMenu = _ => BuildHeaderContextMenu(fid, f.IsRequired),
            });
        }

        defs.Add(new() { Key = TableColumnDef.AddPropKey, Header = "+",
                         IsStarWidth = false, FixedWidth = PlusColWidth,
                         CanResize = false,   IsReadOnly = true,
                         MaxWidth  = PlusColWidth });

        return defs;
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

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var chevron = BuildFilterChevron(def);
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);

        return new Border
        {
            Background          = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
            Padding             = new Thickness(6, 0),
            BorderBrush         = (IBrush)this.FindResource("BrushSeparator")!,
            BorderThickness     = new Thickness(0, 0, 1, 0),
            Child               = grid,
        };
    }

    /// <summary>Стрелка в шапке колонки — открывает окно фильтра/сортировки.</summary>
    private Control BuildFilterChevron(TableColumnDef def)
    {
        // Обводочный шеврон (как в полях даты/навбаре), не PathIcon-заливка.
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data            = Geometry.Parse("M6 9 L12 15 L18 9"),
            Stroke          = ChevronBrush(def.Key),
            StrokeThickness = 2,
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        };
        _chevronPaths[def.Key] = path;

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
        ToolTip.SetTip(btn, "Фильтр и сортировка");
        btn.Click += (_, _) => ShowFilterFlyout(btn, def);
        return btn;
    }

    private IBrush ChevronBrush(string key) =>
        _filters.ContainsKey(key) || _sortKey == key
            ? (IBrush)this.FindResource("BrushAccent")!
            : (IBrush)this.FindResource("BrushSecondaryText")!;

    private void UpdateChevronStates()
    {
        foreach (var (key, path) in _chevronPaths)
            path.Stroke = ChevronBrush(key);
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

        if (e.Column.DisplayIndex == 0)
            RecordClicked?.Invoke(this, row.RecordId);
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

        var miEdit = new MenuItem { Header = "Изменить" };
        miEdit.Click += (_, _) => RecordClicked?.Invoke(this, recordId);
        menu.Items.Add(miEdit);

        var miDelete = new MenuItem { Header = "Удалить" };
        miDelete.Click += (_, _) => RecordDeleteRequested?.Invoke(this, recordId);
        menu.Items.Add(miDelete);

        return menu;
    }

    private ContextMenu BuildHeaderContextMenu(int fieldId, bool isRequired)
    {
        var menu = new ContextMenu();

        var miCreate = new MenuItem { Header = "Создать свойство" };
        miCreate.Click += (_, _) => PropertyCreateRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miCreate);

        var miEdit = new MenuItem { Header = "Изменить свойство" };
        miEdit.Click += (_, _) => PropertyEditRequested?.Invoke(this, fieldId);
        menu.Items.Add(miEdit);

        menu.Items.Add(new Separator());

        var miDelete = new MenuItem { Header = "Удалить", IsEnabled = !isRequired };
        miDelete.Click += (_, _) => PropertyDeleteRequested?.Invoke(this, fieldId);
        menu.Items.Add(miDelete);

        return menu;
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

        // ── Сортировка ──
        var (ascLabel, descLabel) = def.FieldType switch
        {
            "number" => ("По возрастанию", "По убыванию"),
            "date"   => ("Сначала старые", "Сначала новые"),
            _        => ("От А до Я", "От Я до А"),
        };
        panel.Children.Add(MakeFlatItem($"↑  {ascLabel}",
            () => { _sortKey = def.Key; _sortAsc = true;  ApplyFilters(); close(); }));
        panel.Children.Add(MakeFlatItem($"↓  {descLabel}",
            () => { _sortKey = def.Key; _sortAsc = false; ApplyFilters(); close(); }));

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

        // ── Список значений ──
        var counts     = LoadValueCounts(def);
        var checkedSet = _filters.TryGetValue(def.Key, out var f)
            ? new HashSet<string>(f)
            : new HashSet<string>(counts.Select(c => c.Raw));

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
                var cntText = new TextBlock
                {
                    Text              = cnt.ToString(),
                    Foreground        = secondary,
                    Margin            = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                Grid.SetColumn(nameText, 0);
                Grid.SetColumn(cntText, 1);
                g.Children.Add(nameText);
                g.Children.Add(cntText);

                var cb = new CheckBox
                {
                    Content   = g,
                    FontSize  = fontBase,
                    IsChecked = checkedSet.Contains(raw),
                    HorizontalAlignment        = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                cb.IsCheckedChanged += (_, _) =>
                {
                    if (cb.IsChecked == true) checkedSet.Add(raw);
                    else checkedSet.Remove(raw);
                };
                listPanel.Children.Add(cb);
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

        search.TextChanged += (_, _) => RebuildList();
        selectAll.IsCheckedChanged += (_, _) =>
        {
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

        panel.Children.Add(MakeSeparator());

        // ── Нижние кнопки ──
        // Крестик и текст — отдельными TextBlock с центрированием по вертикали,
        // иначе глиф «✕» визуально сидит выше строки.
        var clearContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        clearContent.Children.Add(new TextBlock
        {
            Text = "✕", FontSize = fontBase, VerticalAlignment = VerticalAlignment.Center,
        });
        clearContent.Children.Add(new TextBlock
        {
            Text = "Снять фильтр", FontSize = fontBase, VerticalAlignment = VerticalAlignment.Center,
        });

        var clearBtn = new Button
        {
            Content   = clearContent,
            Padding   = new Thickness(4, 6),
            IsEnabled = _filters.ContainsKey(def.Key) || _sortKey == def.Key,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        clearBtn.Classes.Add("icon");
        clearBtn.Click += (_, _) =>
        {
            _filters.Remove(def.Key);
            if (_sortKey == def.Key) _sortKey = null;
            ApplyFilters();
            close();
        };

        var okBtn = new Button { Content = "Готово", Padding = new Thickness(16, 6) };
        okBtn.Classes.Add("accent");
        okBtn.Click += (_, _) =>
        {
            // Все значения выбраны — фильтр не нужен.
            if (checkedSet.Count >= counts.Count) _filters.Remove(def.Key);
            else _filters[def.Key] = new HashSet<string>(checkedSet);
            ApplyFilters();
            close();
        };

        var bottom = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin            = new Thickness(0, 4, 0, 0),
        };
        Grid.SetColumn(clearBtn, 0);
        Grid.SetColumn(okBtn, 1);
        bottom.Children.Add(clearBtn);
        bottom.Children.Add(okBtn);
        panel.Children.Add(bottom);

        return panel;
    }

    private Button MakeFlatItem(string text, Action onClick)
    {
        var btn = new Button
        {
            Content         = text,
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(8, 6),
            FontSize        = (double)this.FindResource("FontSizeBase")!,
            Foreground      = (IBrush)this.FindResource("BrushPrimaryText")!,
            CornerRadius    = (CornerRadius)this.FindResource("RadiusBase")!,
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
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

    private List<(string Raw, int Count)> LoadValueCounts(TableColumnDef def)
    {
        if (_repository is null) return [];

        var raw =
            def.Key == TableRowVm.TitleKey ? _repository.GetTitleCounts(_collectionId) :
            def.Key == TableRowVm.DescKey  ? _repository.GetDescriptionCounts(_collectionId) :
            int.TryParse(def.Key, out var fid)
                ? _repository.GetFieldValueCounts(_collectionId, fid)
                : [];

        // «(Пусто)» — в конец, остальное по алфавиту.
        return raw.OrderBy(v => v.Value == "" ? 1 : 0)
                  .ThenBy(v => v.Value, StringComparer.CurrentCultureIgnoreCase)
                  .Select(v => (Raw: v.Value, v.Count))
                  .ToList();
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
