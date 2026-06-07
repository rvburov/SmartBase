using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;
using MyPlanner.ViewModels.ConstructorDb;

namespace MyPlanner.Views.ConstructorDb;

public partial class TableView : UserControl
{
    /// <summary>Запрос открыть запись (Edit) — родитель открывает RecordPageView.</summary>
    public event EventHandler<int>? RecordClicked;

    /// <summary>Запрос удалить запись.</summary>
    public event EventHandler<int>? RecordDeleteRequested;

    /// <summary>Запрос создать новую запись (родитель создаёт и сразу открывает PageView).</summary>
    public event EventHandler? RecordCreateRequested;

    /// <summary>Запрос создать новое свойство (поле).</summary>
    public event EventHandler? PropertyCreateRequested;

    /// <summary>Запрос изменить свойство (передан field.Id).</summary>
    public event EventHandler<int>? PropertyEditRequested;

    /// <summary>Запрос удалить свойство (передан field.Id).</summary>
    public event EventHandler<int>? PropertyDeleteRequested;

    private const int MinColWidth  = 160;
    private const int NumColWidth   = 36;   // квадрат: совпадает с высотой строки
    private const int PlusColWidth  = 36;   // симметрично «№»

    private ConstructorRepository? _repository;
    private int  _collectionId;
    private bool _manualColResize;
    // Поля, которые отображаются как колонки таблицы (без markdown).
    private List<DbField> _fields = [];
    private System.Collections.ObjectModel.ObservableCollection<TableRowVm> _rows = [];

    public TableView()
    {
        InitializeComponent();
        RecordsGrid.CellPointerPressed += OnCellPointerPressed;
        RecordsGrid.ColumnReordered    += (_, _) => SaveColumnOrder();
        RecordsGrid.LoadingRow         += OnLoadingRow;
        // Сохранение ширин после ручного ресайза (нет отдельного события «resize finished»).
        RecordsGrid.AddHandler(PointerReleasedEvent, OnGridPointerReleased, RoutingStrategies.Tunnel);
        // Пересчёт ширин колонок в целых пикселях при изменении размера (как PyQt resizeEvent).
        RecordsGrid.SizeChanged += (_, _) => ApplyColumnWidths();
    }

    private void OnGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_manualColResize) PersistColumnWidths();
    }

    public void Load(ConstructorRepository repository, int collectionId)
    {
        _repository   = repository;
        _collectionId = collectionId;

        var (colResize, _, _) = repository.GetUiSettings(collectionId);
        _manualColResize = colResize;

        Reload();
    }

    /// <summary>Очистить таблицу (например, после удаления открытой коллекции).</summary>
    public void Clear()
    {
        _repository   = null;
        _collectionId = 0;
        _fields       = [];
        _rows         = [];
        RecordsGrid.ItemsSource = null;
        RecordsGrid.Columns.Clear();
    }

    public void SetColResizeMode(bool manual)
    {
        _manualColResize = manual;
        RecordsGrid.CanUserResizeColumns = manual;
        if (!manual)
        {
            _repository?.SaveColWidths(_collectionId, []);
            ApplyColumnWidths();
        }
    }

    public void SetRowResizeMode(bool _) { /* построчный ресайз пока не поддержан DataGrid */ }

    // ── Построение таблицы ────────────────────────────────────────────────────

    private void Reload()
    {
        if (_repository is null) return;

        // markdown-поле (Описание) редактируется только в RecordPageView,
        // в таблице оно не отображается как колонка.
        _fields = _repository.GetFields(_collectionId)
            .Where(f => f.FieldType != "markdown")
            .ToList();
        var records = _repository.GetRecords(_collectionId);

        BuildColumns();

        _rows = [];
        // Первая строка — шапка (имена полей).
        var fieldNames = _fields.ToDictionary(f => f.Id!.Value, f => f.Name);
        _rows.Add(TableRowVm.HeaderRow(fieldNames));

        for (var i = 0; i < records.Count; i++)
        {
            var id     = records[i].Id ?? 0;
            var values = _repository.GetFieldValues(id);
            _rows.Add(new TableRowVm(_repository, id, i + 1, values));
        }
        // Завершающая строка «+ Новая запись».
        _rows.Add(TableRowVm.AddRow());

        RecordsGrid.ItemsSource = _rows;
        RecordsGrid.ContextMenu  = BuildGridContextMenu();

        RecordsGrid.CanUserResizeColumns  = _manualColResize;
        RecordsGrid.CanUserReorderColumns = true;
        ApplyColumnWidths();
    }

    private void BuildColumns()
    {
        RecordsGrid.Columns.Clear();

        // Колонка «№»
        RecordsGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header        = "№",
            IsReadOnly    = true,
            CanUserSort   = false,
            CanUserResize = false,
            CanUserReorder = false,
            Width         = new DataGridLength(NumColWidth),
            // Ячейка номера залита фоном заголовка (серым), как «угловой» столбец.
            CellTemplate = new FuncDataTemplate<TableRowVm>((row, _) =>
                new Border
                {
                    Background           = (IBrush)this.FindResource("BrushInactiveBg")!,
                    HorizontalAlignment  = HorizontalAlignment.Stretch,
                    VerticalAlignment    = VerticalAlignment.Stretch,
                    Child = new TextBlock
                    {
                        Text                = row.IsHeaderRow ? "№"
                                            : row.IsAddRow    ? "+"
                                            : row.RowNumber.ToString(),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Foreground          = (IBrush)this.FindResource("BrushSecondaryText")!,
                    },
                }),
        });

        // Колонки данных
        foreach (var field in _fields)
            RecordsGrid.Columns.Add(BuildFieldColumn(field));

        // Завершающая «+»-колонка для создания нового свойства.
        RecordsGrid.Columns.Add(BuildAddPropertyColumn());
    }

    private DataGridColumn BuildAddPropertyColumn()
    {
        return new DataGridTemplateColumn
        {
            Header        = "",   // нативный header скрыт (HeadersVisibility="None")
            Tag           = "__add_property__",
            IsReadOnly    = true,
            CanUserSort   = false,
            CanUserResize = false,
            CanUserReorder = false,
            Width         = new DataGridLength(PlusColWidth),
            MinWidth      = PlusColWidth,
            MaxWidth      = PlusColWidth,
            CellTemplate  = new FuncDataTemplate<TableRowVm>((row, _) => new Border
            {
                Background          = (IBrush)this.FindResource("BrushInactiveBg")!,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Stretch,
                Child = row.IsHeaderRow
                    ? new TextBlock
                    {
                        Text                = "+",
                        FontSize            = 16,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Foreground          = (IBrush)this.FindResource("BrushSecondaryText")!,
                    }
                    : null,
            }),
        };
    }

    private DataGridColumn BuildFieldColumn(DbField field)
    {
        var key   = field.Id!.Value.ToString();
        var fid   = field.Id!.Value;

        // Заголовок колонки оборачиваем в TextBlock с контекстным меню для CRUD свойств.
        var headerText = new TextBlock
        {
            Text                = field.Name,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextTrimming        = TextTrimming.CharacterEllipsis,
            Foreground          = (IBrush)this.FindResource("BrushSecondaryText")!,
            ContextMenu         = BuildHeaderContextMenu(fid, field.IsRequired),
        };

        return new DataGridTemplateColumn
        {
            Header       = headerText,
            Tag          = fid,                            // для восстановления порядка колонок
            CanUserSort  = false,
            Width        = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth     = MinColWidth,
            IsReadOnly   = field.FieldType == "checkbox",   // чекбокс редактируется кликом в CellTemplate
            CellTemplate = BuildCellTemplate(field, key),
            CellEditingTemplate = field.FieldType == "checkbox" ? null : BuildEditTemplate(field, key),
        };
    }

    // ── Шаблоны отображения ────────────────────────────────────────────────────

    private IDataTemplate BuildCellTemplate(DbField field, string key) =>
        new FuncDataTemplate<TableRowVm>((row, _) =>
        {
            // Шапка: имя поля на сером фоне + контекстное меню «свойство».
            if (row.IsHeaderRow)
            {
                return new Border
                {
                    Background          = (IBrush)this.FindResource("BrushInactiveBg")!,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment   = VerticalAlignment.Stretch,
                    Padding             = new Thickness(6, 0),
                    ContextMenu         = BuildHeaderContextMenu(field.Id!.Value, field.IsRequired),
                    Child = new TextBlock
                    {
                        Text                = field.Name,
                        VerticalAlignment   = VerticalAlignment.Center,
                        TextTrimming        = TextTrimming.CharacterEllipsis,
                        Foreground          = (IBrush)this.FindResource("BrushSecondaryText")!,
                    },
                };
            }

            // У add-row нет данных — ячейка пустая (визуально только «+» в первой колонке).
            if (row.IsAddRow) return new Border();

            if (field.FieldType == "checkbox")
            {
                var cb = new CheckBox
                {
                    IsChecked           = IsTruthy(row[key]),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                };
                cb.IsCheckedChanged += (_, _) => row[key] = cb.IsChecked == true ? "1" : "0";
                return cb;
            }

            // Без inline Foreground — цвет берётся от стиля DataGridCell,
            // чтобы при выборе строки текст становился синим (как в SidebarTree).
            // Одна строка + многоточие — высота строки фиксирована (36px), как в сайдбаре.
            return new TextBlock
            {
                Margin              = new Thickness(6, 0, 6, 0),
                VerticalAlignment   = VerticalAlignment.Center,
                TextTrimming        = TextTrimming.CharacterEllipsis,
                [!TextBlock.TextProperty] = new Binding($"[{key}]")
                {
                    Converter = new FieldDisplayConverter(field),
                },
            };
        });

    private IDataTemplate BuildEditTemplate(DbField field, string key) =>
        new FuncDataTemplate<TableRowVm>((row, _) =>
        {
            switch (field.FieldType)
            {
                case "number":
                    return new TextBox
                    {
                        BorderThickness = new Thickness(0),
                        VerticalContentAlignment = VerticalAlignment.Center,
                        [!TextBox.TextProperty] = new Binding($"[{key}]", BindingMode.TwoWay),
                    };

                case "date":
                {
                    var picker = new CalendarDatePicker
                    {
                        BorderThickness = new Thickness(0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    if (DateTime.TryParseExact(row[key], "dd.MM.yyyy", null, DateTimeStyles.None, out var dt))
                        picker.SelectedDate = dt;
                    picker.SelectedDateChanged += (_, _) =>
                        row[key] = picker.SelectedDate?.ToString("dd.MM.yyyy") ?? "";
                    return picker;
                }

                case "select":
                {
                    var combo = new ComboBox
                    {
                        BorderThickness     = new Thickness(0),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        ItemsSource         = SelectOptions(field),
                        SelectedItem        = row[key],
                    };
                    combo.SelectionChanged += (_, _) =>
                        row[key] = combo.SelectedItem?.ToString() ?? "";
                    return combo;
                }

                default:   // text, url
                    return new TextBox
                    {
                        BorderThickness = new Thickness(0),
                        VerticalContentAlignment = VerticalAlignment.Center,
                        Watermark = field.FieldType == "url" ? "https://" : null,
                        [!TextBox.TextProperty] = new Binding($"[{key}]", BindingMode.TwoWay),
                    };
            }
        });

    // ── Клики и контекстные меню ───────────────────────────────────────────────

    // Клик по ячейке «№» открывает запись (привычный shortcut).
    // Создание/удаление/редактирование выполняется через контекстные меню.
    private void OnCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (e.Row.DataContext is not TableRowVm row) return;

        // Шапка-строка: клик по «+»-колонке → создание свойства.
        if (row.IsHeaderRow)
        {
            if (e.Column.Tag as string == "__add_property__")
                PropertyCreateRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Клик по любой ячейке add-row → создание новой записи.
        if (row.IsAddRow)
        {
            RecordCreateRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Клик по «+»-колонке справа → создание нового свойства.
        if (e.Column.Tag as string == "__add_property__")
        {
            PropertyCreateRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.Column.DisplayIndex == 0)
            RecordClicked?.Invoke(this, row.RecordId);
    }

    /// <summary>Контекстное меню для всей сетки — открывается на пустой области.</summary>
    private ContextMenu BuildGridContextMenu()
    {
        var menu = new ContextMenu();
        var miCreate = new MenuItem { Header = "Создать запись" };
        miCreate.Click += (_, _) => RecordCreateRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miCreate);
        return menu;
    }

    /// <summary>Контекстное меню для строки данных.</summary>
    private ContextMenu BuildRowContextMenu(int recordId)
    {
        var menu = new ContextMenu();

        var miOpen = new MenuItem { Header = "Открыть" };
        miOpen.Click += (_, _) => RecordClicked?.Invoke(this, recordId);
        menu.Items.Add(miOpen);

        var miCreate = new MenuItem { Header = "Создать запись" };
        miCreate.Click += (_, _) => RecordCreateRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miCreate);

        menu.Items.Add(new Separator());

        var miDelete = new MenuItem { Header = "Удалить" };
        miDelete.Click += (_, _) => RecordDeleteRequested?.Invoke(this, recordId);
        menu.Items.Add(miDelete);

        return menu;
    }

    /// <summary>Контекстное меню для заголовка колонки (свойства).</summary>
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
        if (row.IsAddRow || row.IsHeaderRow) return;
        e.Row.ContextMenu = BuildRowContextMenu(row.RecordId);
    }

    // ── Ширины колонок ─────────────────────────────────────────────────────────

    private void ApplyColumnWidths()
    {
        if (_repository is null || _fields.Count == 0) return;
        var saved = _repository.GetColWidths(_collectionId);

        // Ручной режим: фиксированные сохранённые ширины (целые пиксели).
        if (_manualColResize)
        {
            for (var i = 0; i < _fields.Count; i++)
            {
                var col = RecordsGrid.Columns[i + 1];   // +1 за колонку «№»
                var id  = _fields[i].Id!.Value;
                col.Width = saved.TryGetValue(id, out var w) && w > 0
                    ? new DataGridLength(w)
                    : new DataGridLength(MinColWidth);
            }
            return;
        }

        // Авто-режим: data-колонки растягиваются на оставшееся пространство (Star),
        // фиксированные «№» и «+»-колонка не трогаются. Это надёжнее ручного
        // подсчёта пикселей — DataGrid сам учтёт скроллбар и разделители.
        for (var i = 0; i < _fields.Count; i++)
            RecordsGrid.Columns[i + 1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
    }

    /// <summary>
    /// Делит доступную ширину между data-колонками в целых пикселях.
    /// Остаток от деления отдаётся последней колонке, чтобы сумма точно
    /// совпадала с доступной шириной (целочисленные границы → линии не пропадают).
    /// </summary>
    private int[] CalcDataWidths(double gridWidth, int dataCols)
    {
        // Вертикальный скроллбар всегда видим — резервируем под него место,
        // чтобы колонки не вылезали и не появлялся лишний горизонтальный скролл.
        const int vScrollReserve = 14;
        // +2 пикселя на вертикальные разделители колонок: между «№»/первой data
        // и между последней data/«+».
        var available = (int)gridWidth - NumColWidth - PlusColWidth - vScrollReserve - 2;

        if (dataCols == 0 || available <= 0)
            return Enumerable.Repeat(MinColWidth, dataCols).ToArray();

        var natural = available / dataCols;
        var widths  = new int[dataCols];

        if (natural >= MinColWidth)
        {
            for (var i = 0; i < dataCols; i++) widths[i] = natural;
            widths[dataCols - 1] += available - natural * dataCols;   // остаток последней колонке
        }
        else
        {
            for (var i = 0; i < dataCols; i++) widths[i] = MinColWidth;  // не влезает → горизонтальный скролл
        }
        return widths;
    }

    private void PersistColumnWidths()
    {
        if (_repository is null || _fields.Count == 0) return;
        var widths = new Dictionary<int, int>();
        for (var i = 0; i < _fields.Count; i++)
        {
            var col = RecordsGrid.Columns[i + 1];
            var w   = (int)col.ActualWidth;
            if (w >= MinColWidth) widths[_fields[i].Id!.Value] = w;
        }
        _repository.SaveColWidths(_collectionId, widths);
    }

    private void SaveColumnOrder()
    {
        if (_repository is null || _fields.Count == 0) return;
        var ordered = RecordsGrid.Columns
            .OrderBy(c => c.DisplayIndex)
            .Select(c => c.Tag)
            .OfType<int>()
            .ToList();
        if (ordered.Count == _fields.Count)
            _repository.ReorderFields(ordered);
    }

    // ── Хелперы значений ───────────────────────────────────────────────────────

    private static bool IsTruthy(string v) => v is "1" or "true" or "True";

    private static List<string> SelectOptions(DbField field)
    {
        try
        {
            using var doc = JsonDocument.Parse(field.ConfigJson ?? "{}");
            if (doc.RootElement.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                return opts.EnumerateArray().Select(o => o.ToString()).ToList();
        }
        catch { /* ignore bad json */ }
        return [];
    }
}

// Конвертер отображения для TextBlock-ячеек (чекбокс → «✓», прочее как есть).
internal sealed class FieldDisplayConverter(DbField field) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value?.ToString() ?? "";
        return field.FieldType == "checkbox" ? (s is "1" or "true" or "True" ? "✓" : "") : s;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() ?? "";
}
