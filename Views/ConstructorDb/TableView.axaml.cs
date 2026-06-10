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

        var cell = MakeTextHeader(def.Header);

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

    private Border MakeTextHeader(string text) => new()
    {
        Background          = Brushes.Transparent,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment   = VerticalAlignment.Stretch,
        Padding             = new Thickness(6, 0),
        BorderBrush         = (IBrush)this.FindResource("BrushSeparator")!,
        BorderThickness     = new Thickness(0, 0, 1, 0),
        Child = new TextBlock
        {
            Text              = text,
            FontSize          = (double)this.FindResource("FontSizeBase")!,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Foreground        = (IBrush)this.FindResource("BrushSecondaryText")!,
        },
    };

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

        var miEdit = new MenuItem { Header = "Редактировать" };
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
