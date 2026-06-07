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
using Avalonia.VisualTree;
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
    // Кастомный sticky-ряд заголовков. Строится в BuildCustomHeader, ширины колонок
    // синхронизируются с RecordsGrid.Columns в SyncHeaderWidths по событию LayoutUpdated.
    private Grid? _customHeader;

    public TableView()
    {
        InitializeComponent();
        RecordsGrid.CellPointerPressed += OnCellPointerPressed;
        RecordsGrid.LoadingRow         += OnLoadingRow;
        // Пересчёт ширин колонок в целых пикселях при изменении размера (как PyQt resizeEvent).
        RecordsGrid.SizeChanged += (_, _) => ApplyColumnWidths();
        // Ширины кастомных колонок шапки повторяют ActualWidth колонок DataGrid.
        RecordsGrid.LayoutUpdated += (_, _) => SyncHeaderWidths();
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
        HeaderRowHost.Child = null;
        _customHeader = null;
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

    /// <summary>
    /// Прокручивает таблицу к строке записи, чтобы кнопка «+» (add-row) осталась
    /// в видимой зоне после создания. Вызывается родителем после Refresh().
    /// </summary>
    public void ScrollToRecord(int recordId)
    {
        var row = _rows.FirstOrDefault(r => !r.IsAddRow && r.RecordId == recordId);
        if (row is null) return;
        // Откладываем до следующего тика, иначе ItemsSource ещё не успел
        // материализовать строки и ScrollIntoView ничего не найдёт.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RecordsGrid.ScrollIntoView(row, RecordsGrid.Columns.FirstOrDefault());
            // Дополнительно докручиваем до add-row — он рисуется ниже последней
            // записи, и без второго прокрута «+» может остаться вне видимости.
            var addRow = _rows.LastOrDefault(r => r.IsAddRow);
            if (addRow is not null)
                RecordsGrid.ScrollIntoView(addRow, RecordsGrid.Columns.FirstOrDefault());
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

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
        for (var i = 0; i < records.Count; i++)
        {
            var id     = records[i].Id ?? 0;
            var values = _repository.GetFieldValues(id);
            _rows.Add(new TableRowVm(_repository, id, i + 1, values,
                                      records[i].Title, records[i].Description));
        }
        // Завершающая строка «+ Новая запись».
        _rows.Add(TableRowVm.AddRow());

        RecordsGrid.ItemsSource = _rows;
        RecordsGrid.ContextMenu  = BuildGridContextMenu();

        ApplyColumnWidths();

        // Нативная шапка DataGrid скрыта (HeadersVisibility="None") — рисуем свою.
        BuildCustomHeader();
    }

    private void BuildColumns()
    {
        RecordsGrid.Columns.Clear();

        // Колонка «№». Сам заголовок «№» рендерит кастомная sticky-шапка
        // (см. BuildCustomHeader); здесь — только ячейки с номером строки / «+».
        RecordsGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header        = "№",
            IsReadOnly    = true,
            CanUserSort   = false,
            CanUserResize = false,
            CanUserReorder = false,
            Width         = new DataGridLength(NumColWidth),
            CellTemplate = new FuncDataTemplate<TableRowVm>((row, _) =>
                new Border
                {
                    Background           = (IBrush)this.FindResource("BrushInactiveBg")!,
                    HorizontalAlignment  = HorizontalAlignment.Stretch,
                    VerticalAlignment    = VerticalAlignment.Stretch,
                    Child = new TextBlock
                    {
                        Text                = row.IsAddRow ? "+" : row.RowNumber.ToString(),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Foreground          = (IBrush)this.FindResource("BrushSecondaryText")!,
                    },
                }),
        });

        // Системные колонки записи: «Название» и «Описание» хранятся в DbRecord
        // (а не в db_field_values), поэтому отображаются как фиксированные колонки.
        RecordsGrid.Columns.Add(BuildRecordTextColumn("Название",  TableRowVm.TitleKey));
        RecordsGrid.Columns.Add(BuildRecordTextColumn("Описание",  TableRowVm.DescKey));

        // Колонки данных
        foreach (var field in _fields)
            RecordsGrid.Columns.Add(BuildFieldColumn(field));

        // Завершающая «+»-колонка для создания нового свойства.
        RecordsGrid.Columns.Add(BuildAddPropertyColumn());
    }

    /// <summary>
    /// Колонка для системных текстовых полей записи (Название/Описание).
    /// Их нельзя удалить или переупорядочить — это часть схемы DbRecord.
    /// </summary>
    private DataGridColumn BuildRecordTextColumn(string headerText, string rowKey)
    {
        var isTitle = rowKey == TableRowVm.TitleKey;
        return new DataGridTemplateColumn
        {
            Header        = headerText,
            Tag           = rowKey,                          // не int → не попадает в SaveColumnOrder
            CanUserSort   = false,
            CanUserReorder = false,
            Width         = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth      = MinColWidth,
            IsReadOnly    = false,
            CellTemplate  = new FuncDataTemplate<TableRowVm>((row, _) =>
            {
                if (row.IsAddRow) return new Border();

                var binding = new Binding($"[{rowKey}]");
                if (isTitle) binding.Converter = EmptyTitleConverter.Instance;

                return new TextBlock
                {
                    Margin              = new Thickness(6, 0, 6, 0),
                    VerticalAlignment   = VerticalAlignment.Center,
                    TextTrimming        = TextTrimming.CharacterEllipsis,
                    [!TextBlock.TextProperty] = binding,
                };
            }),
            CellEditingTemplate = new FuncDataTemplate<TableRowVm>((row, _) =>
            {
                if (row.IsAddRow) return new Border();
                return new TextBox
                {
                    BorderThickness = new Thickness(0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    [!TextBox.TextProperty] = new Binding($"[{rowKey}]", BindingMode.TwoWay),
                };
            }),
        };
    }

    private DataGridColumn BuildAddPropertyColumn()
    {
        // «+» в заголовке — клик создаёт свойство. Header — строка (см. комментарий
        // в BuildColumns); центрирование и PointerPressed навешиваем на сам
        // DataGridColumnHeader через ApplyHeaderCustomizations().
        return new DataGridTemplateColumn
        {
            Header        = "+",
            Tag           = "__add_property__",
            IsReadOnly    = true,
            CanUserSort   = false,
            CanUserResize = false,
            CanUserReorder = false,
            Width         = new DataGridLength(PlusColWidth),
            MinWidth      = PlusColWidth,
            MaxWidth      = PlusColWidth,
            CellTemplate  = new FuncDataTemplate<TableRowVm>((_, _) => new Border
            {
                Background          = (IBrush)this.FindResource("BrushInactiveBg")!,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Stretch,
            }),
        };
    }

    private DataGridColumn BuildFieldColumn(DbField field)
    {
        var key   = field.Id!.Value.ToString();
        var fid   = field.Id!.Value;

        // Header = строка (см. комментарий в BuildColumns). ContextMenu свойств
        // вешается на DataGridColumnHeader через ApplyHeaderCustomizations.
        return new DataGridTemplateColumn
        {
            Header       = field.Name,
            Tag          = fid,                            // для восстановления порядка колонок
            CanUserSort  = false,
            Width        = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth     = MinColWidth,
            IsReadOnly   = field.FieldType == "checkbox",   // чекбокс редактируется кликом в CellTemplate
            CellTemplate = BuildCellTemplate(field, key),
            CellEditingTemplate = field.FieldType == "checkbox" ? null : BuildEditTemplate(field, key),
        };
    }

    // ── Кастомная sticky-шапка ─────────────────────────────────────────────────
    //
    // Нативная DataGridColumnHeader в Avalonia упрямо не рендерит ни Control-заголовок,
    // ни узкие текстовые заголовки («№», «+»). Чтобы получить надёжную шапку с
    // центрированными «№»/«+» и кликабельным «+», рисуем свой Grid поверх:
    //  • ColumnDefinitions повторяют RecordsGrid.Columns (1:1) + хвост под скроллбар;
    //  • ширины колонок шапки синхронизируются с ActualWidth колонок DataGrid в
    //    SyncHeaderWidths (вызывается на LayoutUpdated).

    // Ширина вертикального скроллбара — резервируется в правом крае шапки, чтобы
    // вертикальные разделители колонок совпадали с разделителями таблицы.
    private const int VScrollbarReserve = 14;

    private void BuildCustomHeader()
    {
        var grid = new Grid();

        for (var i = 0; i < RecordsGrid.Columns.Count; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        // Колонка-«заглушка» под полосу прокрутки DataGrid.
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(VScrollbarReserve)));

        for (var i = 0; i < RecordsGrid.Columns.Count; i++)
        {
            var cell = BuildHeaderCell(RecordsGrid.Columns[i]);
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }

        HeaderRowHost.Child = grid;
        _customHeader       = grid;

        SyncHeaderWidths();
    }

    /// <summary>Создаёт визуал ячейки шапки под конкретную колонку DataGrid.</summary>
    private Control BuildHeaderCell(DataGridColumn col)
    {
        var tag = col.Tag;

        // «+»-колонка справа — кликабельная.
        if (tag as string == "__add_property__")
        {
            var plus = MakeCenteredHeader("+", fontSize: 16);
            plus.Cursor = new Cursor(StandardCursorType.Hand);
            plus.PointerPressed += (_, _) =>
                PropertyCreateRequested?.Invoke(this, EventArgs.Empty);
            return plus;
        }

        // Колонка свойства (Tag = field.Id).
        if (tag is int fid)
        {
            var field = _fields.FirstOrDefault(f => f.Id == fid);
            var cell  = MakeTextHeader(field?.Name ?? col.Header as string ?? "");
            if (field != null)
                cell.ContextMenu = BuildHeaderContextMenu(fid, field.IsRequired);
            return cell;
        }

        // Системные «Название»/«Описание» — col.Tag = TableRowVm.TitleKey / DescKey.
        if (tag as string == TableRowVm.TitleKey || tag as string == TableRowVm.DescKey)
            return MakeTextHeader(col.Header as string ?? "");

        // «№»-колонка (без Tag) и любая другая — по центру.
        return MakeCenteredHeader(col.Header as string ?? "");
    }

    private Border MakeCenteredHeader(string text, double fontSize = 0) => new()
    {
        Background          = Brushes.Transparent,  // прозрачный — чтобы ловить клики
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment   = VerticalAlignment.Stretch,
        BorderBrush         = (IBrush)this.FindResource("BrushSeparator")!,
        BorderThickness     = new Thickness(0, 0, 1, 0),
        Child = new TextBlock
        {
            Text                = text,
            FontSize            = fontSize > 0 ? fontSize
                                              : (double)this.FindResource("FontSizeBase")!,
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

    /// <summary>Подгоняет ширины колонок шапки под ActualWidth колонок DataGrid.</summary>
    private void SyncHeaderWidths()
    {
        if (_customHeader is null) return;
        var n = Math.Min(RecordsGrid.Columns.Count, _customHeader.ColumnDefinitions.Count - 1);
        for (var i = 0; i < n; i++)
        {
            var w = RecordsGrid.Columns[i].ActualWidth;
            if (w <= 0) continue;
            _customHeader.ColumnDefinitions[i].Width = new GridLength(w);
        }
    }

    // ── Шаблоны отображения ────────────────────────────────────────────────────

    private IDataTemplate BuildCellTemplate(DbField field, string key) =>
        new FuncDataTemplate<TableRowVm>((row, _) =>
        {
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
        if (row.IsAddRow) return;
        e.Row.ContextMenu = BuildRowContextMenu(row.RecordId);
    }

    // ── Ширины колонок ─────────────────────────────────────────────────────────

    // Смещение data-колонок в RecordsGrid.Columns: «№» + 2 системные (Название/Описание).
    private const int DataColumnsOffset = 3;

    private void ApplyColumnWidths()
    {
        if (_repository is null) return;
        var saved = _repository.GetColWidths(_collectionId);

        // Ручной режим: фиксированные сохранённые ширины (целые пиксели).
        if (_manualColResize)
        {
            for (var i = 0; i < _fields.Count; i++)
            {
                var col = RecordsGrid.Columns[i + DataColumnsOffset];
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
            RecordsGrid.Columns[i + DataColumnsOffset].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
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

// Пустой Title в режиме отображения подменяется на «Без названия».
// Для CellEditingTemplate этот конвертер не применяется — там редактируется сырой Title.
internal sealed class EmptyTitleConverter : IValueConverter
{
    public static readonly EmptyTitleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? "Без названия" : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() ?? "";
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
