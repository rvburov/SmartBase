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
    private const int NumColWidth   = 36;   // квадрат: совпадает с высотой строки
    private const int PlusColWidth  = 38;

    private ConstructorRepository? _repository;
    private int  _collectionId;
    private bool _manualColResize;
    private List<DbField> _fields = [];
    private System.Collections.ObjectModel.ObservableCollection<TableRowVm> _rows = [];

    public TableView()
    {
        InitializeComponent();
        RecordsGrid.CellPointerPressed += OnCellPointerPressed;
        RecordsGrid.ColumnReordered    += (_, _) => SaveColumnOrder();
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

        // Колонка «№» — заголовок пустой
        RecordsGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header        = "",
            IsReadOnly    = true,
            CanUserSort   = false,
            CanUserResize = false,
            CanUserReorder = false,
            Width         = new DataGridLength(NumColWidth),
            CellTemplate = new FuncDataTemplate<TableRowVm>((row, _) =>
                row.IsAddRow
                    ? PlusGlyph()
                    // Ячейка номера залита фоном заголовка (серым), как «угловой» столбец.
                    : new Border
                    {
                        Background           = (IBrush)this.FindResource("BrushInactiveBg")!,
                        HorizontalAlignment  = HorizontalAlignment.Stretch,
                        VerticalAlignment    = VerticalAlignment.Stretch,
                        Child = new TextBlock
                        {
                            Text                = row.RowNumber.ToString(),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment   = VerticalAlignment.Center,
                            Foreground          = (IBrush)this.FindResource("BrushSecondaryText")!,
                        },
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

    // Иконка lucide plus, как кнопки навбара.
    private Control PlusGlyph()
    {
        var brush = (IBrush)this.FindResource("BrushSecondaryText")!;

        var path = new Avalonia.Controls.Shapes.Path
        {
            Data            = Avalonia.Media.Geometry.Parse("M5 12 H19 M12 5 V19"),
            Stroke          = brush,
            StrokeThickness = 2,
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        };

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);

        return new Viewbox
        {
            Width               = 18,
            Height              = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Child               = canvas,
        };
    }

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
        // ObservableCollection.Insert обновляет грид без пересоздания ItemsSource —
        // прокрутка не сбрасывается наверх.
        _rows.Insert(_rows.Count - 1, newRow);   // перед строкой-плюсом
        Renumber();

        // Оставить новую строку в поле зрения (внизу), а не прыгать наверх.
        RecordsGrid.ScrollIntoView(newRow, RecordsGrid.Columns.Count > 0 ? RecordsGrid.Columns[0] : null);

        // Снять выделение со строки-«плюс» (DataGrid ставит его после нашего обработчика).
        Avalonia.Threading.Dispatcher.UIThread.Post(() => RecordsGrid.SelectedItem = null);
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

        // Авто-режим: ширины вычисляются в ЦЕЛЫХ пикселях (как PyQt _calc_data_widths),
        // чтобы границы колонок были целочисленными и вертикальные линии не «схлопывались».
        var widths = CalcDataWidths(RecordsGrid.Bounds.Width, _fields.Count);
        for (var i = 0; i < _fields.Count; i++)
            RecordsGrid.Columns[i + 1].Width = new DataGridLength(widths[i]);
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
        var available = (int)gridWidth - NumColWidth - vScrollReserve;

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
