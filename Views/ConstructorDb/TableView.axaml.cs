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
    public event EventHandler<int>? RecordClicked;

    private const int MinColWidth  = 160;
    private const int NumColWidth   = 50;
    private const int PlusColWidth  = 38;

    private ConstructorRepository? _repository;
    private int  _collectionId;
    private bool _manualColResize;
    private List<DbField> _fields = [];
    private List<TableRowVm> _rows = [];

    public TableView()
    {
        InitializeComponent();
        RecordsGrid.CellPointerPressed += OnCellPointerPressed;
        RecordsGrid.ColumnReordered    += (_, _) => SaveColumnOrder();
        // Сохранение ширин после ручного ресайза (нет отдельного события «resize finished»).
        RecordsGrid.AddHandler(PointerReleasedEvent, OnGridPointerReleased, RoutingStrategies.Tunnel);
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

        _fields = _repository.GetFields(_collectionId);
        var records = _repository.GetRecords(_collectionId);

        BuildColumns();

        _rows = [];
        for (var i = 0; i < records.Count; i++)
        {
            var id     = records[i].Id ?? 0;
            var values = _repository.GetFieldValues(id);
            _rows.Add(new TableRowVm(_repository, id, i + 1, values));
        }
        _rows.Add(TableRowVm.AddRow());

        RecordsGrid.ItemsSource = _rows;

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
            CellTemplate = new FuncDataTemplate<TableRowVm>((row, _) =>
                row.IsAddRow
                    ? PlusGlyph()
                    : new TextBlock
                    {
                        Text                = row.RowNumber.ToString(),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Foreground          = (IBrush)this.FindResource("BrushSecondaryText")!,
                    }),
        });

        // Колонки данных
        foreach (var field in _fields)
            RecordsGrid.Columns.Add(BuildFieldColumn(field));
    }

    private DataGridColumn BuildFieldColumn(DbField field)
    {
        var key = field.Id!.Value.ToString();

        return new DataGridTemplateColumn
        {
            Header       = field.Name,
            Tag          = field.Id!.Value,                 // для восстановления порядка колонок
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
            if (row.IsAddRow) return new Control();   // пустая ячейка в строке-плюсе

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

            return new TextBlock
            {
                Margin              = new Thickness(6, 0, 6, 0),
                VerticalAlignment   = VerticalAlignment.Center,
                TextTrimming        = TextTrimming.CharacterEllipsis,
                Foreground          = (IBrush)this.FindResource("BrushPrimaryText")!,
                [!TextBlock.TextProperty] = new Binding($"[{key}]")
                {
                    Converter = new FieldDisplayConverter(field),
                },
            };
        });

    private IDataTemplate BuildEditTemplate(DbField field, string key) =>
        new FuncDataTemplate<TableRowVm>((row, _) =>
        {
            if (row.IsAddRow) return new Control();

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

    private Control PlusGlyph() => new TextBlock
    {
        Text                = "+",
        FontSize            = 18,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment   = VerticalAlignment.Center,
        Foreground          = (IBrush)this.FindResource("BrushSecondaryText")!,
    };

    // ── Клики ──────────────────────────────────────────────────────────────────

    private void OnCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (e.Row.DataContext is not TableRowVm row) return;

        if (row.IsAddRow)
        {
            AddRecordInline();
            return;
        }

        // Клик по колонке «№» открывает запись в диалоге (как record_clicked в PyQt).
        if (e.Column.DisplayIndex == 0)
            RecordClicked?.Invoke(this, row.RecordId);
    }

    private void AddRecordInline()
    {
        if (_repository is null) return;

        var dataCount = _rows.Count(r => !r.IsAddRow);
        var rec = _repository.AddRecord(new DbRecord
        {
            CollectionId = _collectionId,
            Position     = (dataCount + 1) * 1000,
        });

        var newRow = new TableRowVm(_repository, rec.Id ?? 0, _rows.Count, []);
        _rows.Insert(_rows.Count - 1, newRow);   // перед строкой-плюсом
        Renumber();
        RecordsGrid.ItemsSource = null;
        RecordsGrid.ItemsSource = _rows;
    }

    private void Renumber()
    {
        var n = 1;
        foreach (var r in _rows)
            if (!r.IsAddRow) r.SetRowNumber(n++);
    }

    // ── Ширины колонок ─────────────────────────────────────────────────────────

    private void ApplyColumnWidths()
    {
        if (_repository is null) return;
        var saved = _repository.GetColWidths(_collectionId);

        for (var i = 0; i < _fields.Count; i++)
        {
            var col = RecordsGrid.Columns[i + 1];   // +1 за колонку «№»
            var id  = _fields[i].Id!.Value;
            if (_manualColResize && saved.TryGetValue(id, out var w) && w > 0)
                col.Width = new DataGridLength(w);
            else
                col.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        }
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
