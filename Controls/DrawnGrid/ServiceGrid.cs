using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Behaviors;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Controls.DrawnGrid;

/// <summary>Кнопка фильтра колонки нажата: ключ колонки и якорь для окна фильтра.</summary>
public sealed class ServiceFilterEventArgs(string key, Control anchor) : EventArgs
{
    public string  Key    { get; } = key;
    public Control Anchor { get; } = anchor;
}

/// <summary>Действие в ячейке служебной таблицы: строка (индекс и источник), колонка и выбранное
/// значение (у списка; у галочки и кнопки пусто).</summary>
public sealed class ServiceCellEventArgs(int row, TableRowVm vm, TableColumnDef def, string value) : EventArgs
{
    public int            Row    { get; } = row;
    public TableRowVm     Vm     { get; } = vm;
    public object?        Source => Vm.Source;
    public TableColumnDef Def    { get; } = def;
    public string         Value  { get; } = value;
}

/// <summary>
/// Рисованная сетка служебных таблиц — «Диагностика», «История изменений», «Пользователи», журнал
/// безопасности (этап 6а плана drawn-grid.md). Поверхность та же, что у таблицы коллекции
/// (<see cref="GridSurface"/>), модель проще: список готовых строк (<see cref="TableRowVm.Static"/>
/// над строкой источника — журнала, пользователя), колонки с дробным дележом окна по правилам прежних
/// таблиц (<see cref="ColumnLayout.BuildRounded"/>) или пиксельной ширины, одиночное выделение
/// строки, клавиши по строкам, подгрузка страниц при прокрутке к концу (<see cref="NearEnd"/>),
/// кнопка фильтра в шапке (<see cref="FilterRequested"/>), ячейки-галочки, ячейки-списки и
/// ячейки-кнопки. Список — настоящий ComboBox над нарисованной ячейкой (<see cref="EditorHost"/>),
/// раскрывается по клику. Вид — как у прежних таблиц на DataGrid: наведение красит строку, высота
/// строк RowHeightStandard, отступ текста 8, галочка по центру, закрепленные колонки
/// (<see cref="FrozenColumns"/>). Данные читают сами разделы; сетка ничего не знает об их источниках.
/// </summary>
public sealed class ServiceGrid : Avalonia.Controls.Grid
{
    // Порог подгрузки: столько пикселей до конца, как у прежней подписки на полосу прокрутки.
    private const double NearEndThreshold = 300;

    private readonly ScrollBar _vertical   = new() { Orientation = Orientation.Vertical,   AllowAutoHide = false };
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal, AllowAutoHide = false };
    private readonly Border    _corner     = new();
    private readonly Canvas    _anchors    = new() { IsHitTestVisible = false };
    private readonly Border    _filterAnchor = new();
    private readonly AvaloniaList<TableRowVm> _rows = [];
    private List<TableColumnDef> _defs = [];
    private int  _selected = -1;
    private bool _syncingBars;

    // Кнопка в ячейке или шапке нажата: действие — на отпускании над тем же местом (как у настоящих кнопок).
    private (int Row, string Key, Rect Area, CellButton Kind)? _press;
    private enum CellButton { Filter, Checkbox, Choice, Button }

    public GridSurface Surface { get; } = new();

    /// <summary>Накладной редактор: настоящий список над ячейкой-списком.</summary>
    public EditorHost Editor { get; }

    /// <summary>Строка выбрана кликом или клавишей (и повторным кликом по той же строке).</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Прокрутка близко к концу — пора подгружать следующую страницу.</summary>
    public event EventHandler? NearEnd;

    /// <summary>Кнопка фильтра и сортировки в шапке отпущена.</summary>
    public event EventHandler<ServiceFilterEventArgs>? FilterRequested;

    /// <summary>Доступная галочка кликнута.</summary>
    public event EventHandler<ServiceCellEventArgs>? CheckToggled;

    /// <summary>Доступная кнопка ячейки кликнута.</summary>
    public event EventHandler<ServiceCellEventArgs>? ButtonClicked;

    /// <summary>В ячейке-списке выбрано новое значение (<see cref="ServiceCellEventArgs.Value"/>).</summary>
    public event EventHandler<ServiceCellEventArgs>? ChoicePicked;

    public ServiceGrid()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        RowDefinitions    = new RowDefinitions("*,Auto");

        Editor = new EditorHost(Surface);

        Surface.HoverWholeRow        = true;
        Surface.HeaderHoverFill      = false;
        Surface.LastColumnLine       = false;
        Surface.LinesBelowRows       = false;
        Surface.RowHeightResourceKey = "RowHeightStandard";
        Surface.CellTextPad          = 8;
        Surface.CheckboxCentered     = true;
        Surface.ClipText             = true;

        SetColumn(Surface, 0);      SetRow(Surface, 0);
        SetColumn(Editor, 0);       SetRow(Editor, 0);
        SetColumn(_anchors, 0);     SetRow(_anchors, 0);
        SetColumn(_vertical, 1);    SetRow(_vertical, 0);
        SetColumn(_horizontal, 0);  SetRow(_horizontal, 1);
        SetColumn(_corner, 1);      SetRow(_corner, 1);

        _anchors.Children.Add(_filterAnchor);
        Children.Add(Surface);
        Children.Add(Editor);
        Children.Add(_anchors);
        Children.Add(_vertical);
        Children.Add(_horizontal);
        Children.Add(_corner);

        Surface.ExtentChanged  += (_, _) => SyncBars();
        Surface.OffsetChanged  += (_, _) => { SyncBars(); CheckNearEnd(); };
        Surface.SizeChanged    += (_, _) => Relayout();
        Surface.MetricsChanged += (_, _) => Relayout();
        Surface.Pressed        += OnPressed;
        Surface.AddHandler(PointerReleasedEvent,    OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        Surface.AddHandler(PointerCaptureLostEvent, (_, _) => _press = null, RoutingStrategies.Bubble, handledEventsToo: true);
        Surface.KeyDown += OnKeyDown;

        _vertical.Scroll   += (_, e) => { if (!_syncingBars) Surface.OffsetY = e.NewValue; };
        _horizontal.Scroll += (_, e) => { if (!_syncingBars) Surface.OffsetX = e.NewValue; };

        Surface.IsRecordSelected = rid => rid == _selected + 1;

        // Список лежит над поверхностью и забирает указатель: наведение и колесо — ей; колесо из
        // раскрытого списка (свое окно-попап) таблицу не крутит.
        Editor.AddHandler(PointerMovedEvent, (_, e) => Surface.HoverAt(e.GetPosition(Surface)),
                          RoutingStrategies.Bubble, handledEventsToo: true);
        Editor.PointerEntered += (_, e) => Surface.HoverAt(e.GetPosition(Surface));
        Editor.PointerExited  += (_, _) => Surface.HoverAt(null);
        Editor.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.Handled || e.Source is not Visual v || v.GetVisualRoot() is PopupRoot) return;
            Surface.ScrollByWheel(e);
        }, RoutingStrategies.Bubble);

        AttachedToVisualTree += (_, _) =>
        {
            if (this.TryFindResource("BrushScrollbarTrack", out var b) && b is IBrush brush)
                _corner.Background = brush;
        };

        Surface.Attach(_rows, ColumnLayout.Empty, null, null, null);
    }

    // ── Колонки ───────────────────────────────────────────────────────────────

    public IReadOnlyList<TableColumnDef> Columns => _defs;

    /// <summary>Задать колонки; раскладка считается от ширины окна и пересчитывается на ресайз.</summary>
    public void SetColumns(IEnumerable<TableColumnDef> defs)
    {
        _defs = defs.ToList();
        Relayout();
    }

    /// <summary>Колонки с активным фильтром или сортировкой — воронка вместо шеврона в шапке.</summary>
    public Func<string, bool> IsColumnFiltered
    {
        get => Surface.IsColumnFiltered;
        set { Surface.IsColumnFiltered = value; Surface.RefreshSelectionVisuals(); }
    }

    /// <summary>Сколько первых колонок закреплено при прокрутке вбок.</summary>
    public int FrozenColumns
    {
        get => Surface.FrozenColumns;
        set { Surface.FrozenColumns = value; Surface.RefreshSelectionVisuals(); }
    }

    /// <summary>Перерисовать шапку (сменился фильтр или сортировка).</summary>
    public void RefreshHeader() => Surface.RefreshSelectionVisuals();

    /// <summary>Ширины показанных колонок (после округления раскладки) — для стендов.</summary>
    public double[] ColumnWidths => Enumerable.Range(0, Surface.Columns.Count).Select(Surface.Columns.Width).ToArray();

    private void Relayout()
    {
        if (_defs.Count == 0) return;
        Surface.SetColumns(ColumnLayout.BuildRounded(_defs, Surface.Bounds.Width, LineMetrics.EffectiveScale(this)));
    }

    // ── Строки ────────────────────────────────────────────────────────────────

    public int RowCount => _rows.Count;

    public IReadOnlyList<TableRowVm> Rows => _rows;

    /// <summary>Источник строки по индексу (строка журнала, пользователь); null — нет такой.</summary>
    public object? ItemAt(int index) => index >= 0 && index < _rows.Count ? _rows[index].Source : null;

    /// <summary>Заменить строки: выделение снимается, прокрутка возвращается к началу — как при подмене
    /// источника у прежней таблицы.</summary>
    public void SetRows(IEnumerable<TableRowVm> rows)
    {
        Editor.Close();
        ClearSelection();
        _rows.Clear();
        _rows.AddRange(rows);
    }

    /// <summary>Дописать страницу в конец: прокрутка и выделение на месте.</summary>
    public void AppendRows(IEnumerable<TableRowVm> rows) => _rows.AddRange(rows);

    public void Clear() => SetRows([]);

    /// <summary>Значения строк читаются из источников при рисовании — после правки источника достаточно
    /// перерисовать.</summary>
    public void RefreshRows() => Surface.RefreshSelectionVisuals();

    private TableRowVm? RowAt(int index) => index >= 0 && index < _rows.Count ? _rows[index] : null;

    // ── Выделение ─────────────────────────────────────────────────────────────

    /// <summary>Индекс выделенной строки; −1 — нет.</summary>
    public int SelectedIndex => _selected;

    /// <summary>Источник выделенной строки; null — выделения нет.</summary>
    public object? SelectedItem => ItemAt(_selected);

    public void ClearSelection()
    {
        if (_selected < 0) return;
        _selected = -1;
        Surface.RefreshSelectionVisuals();
    }

    /// <summary>Выделить строку, показать ее в окне и сообщить наверх — даже если она уже была выделена:
    /// повторный клик по строке открывает панель деталей заново, как у прежних таблиц.</summary>
    public void Select(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        _selected = index;
        Surface.RefreshSelectionVisuals();
        ScrollIntoView(index);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Прокрутить так, чтобы строка была в окне целиком — минимальным сдвигом.</summary>
    public void ScrollIntoView(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        var layout = Surface.RowLayout;
        var top    = layout.Top(index);
        var bottom = layout.Bottom(index);
        var view   = Surface.ViewportHeight;
        if (top < Surface.OffsetY) Surface.OffsetY = top;
        else if (bottom > Surface.OffsetY + view) Surface.OffsetY = bottom - view;
    }

    /// <summary>Горизонтальная прокрутка в пикселях — для стендов.</summary>
    public double OffsetX
    {
        get => Surface.OffsetX;
        set => Surface.OffsetX = value;
    }

    private int PageRows()
    {
        var (first, last) = Surface.VisibleRows();
        return Math.Max(1, last - first);
    }

    // Стрелки и клавиши страниц двигают выделение по строкам, панель деталей следует за ним.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_rows.Count == 0 || Editor.IsOpen) return;
        var last = _rows.Count - 1;
        int? target = e.Key switch
        {
            Key.Down     => Math.Min(last, _selected + 1),
            Key.Up       => Math.Max(0, _selected - 1),
            Key.PageDown => Math.Min(last, Math.Max(0, _selected) + PageRows()),
            Key.PageUp   => Math.Max(0, _selected - PageRows()),
            Key.Home     => 0,
            Key.End      => last,
            _            => null,
        };
        if (target is not { } t) return;
        Select(t);
        e.Handled = true;
    }

    // ── Нажатия ───────────────────────────────────────────────────────────────

    private void OnPressed(object? sender, GridPointerEventArgs e)
    {
        var props = e.Pointer.GetCurrentPoint(Surface).Properties;
        if (!props.IsLeftButtonPressed) return;
        Surface.Focus();
        _press = null;
        var hit = e.Hit;

        if (hit.Kind == GridHitKind.HeaderFilter && hit.Def is { } fdef)
        {
            _press = (-1, fdef.Key, hit.Bounds, CellButton.Filter);
            e.Pointer.Handled = true;
            return;
        }
        if (hit.Kind != GridHitKind.Cell || hit.Def is not { } def || RowAt(hit.Row) is not { } vm) return;

        // Контролы в ячейке: действие на отпускании над тем же местом, строку они не выделяют — как
        // галочка, список и кнопка внутри ячейки прежней таблицы; недоступный контрол клик глотает.
        var point   = e.Pointer.GetPosition(Surface);
        var enabled = def.CellEnabled?.Invoke(vm) ?? true;
        var control = def.FieldType switch
        {
            "checkbox" => (Area: Surface.CheckboxRect(hit.Bounds, hit.Column), Kind: CellButton.Checkbox),
            "choice"   => (Area: Surface.ChoiceRect(hit.Bounds, hit.Column),   Kind: CellButton.Choice),
            "button"   => (Area: Surface.ButtonRect(hit.Bounds, hit.Column),   Kind: CellButton.Button),
            _          => (Area: default(Rect),                     Kind: CellButton.Filter),
        };
        if (control.Area.Width > 0 && control.Area.Contains(point))
        {
            if (enabled) _press = (hit.Row, def.Key, control.Area, control.Kind);
            e.Pointer.Handled = true;
            return;
        }

        Editor.Close();
        Select(hit.Row);
        e.Pointer.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_press is not { } press) return;
        _press = null;
        if (e.InitialPressMouseButton != MouseButton.Left || !press.Area.Contains(e.GetPosition(Surface))) return;
        if (_defs.FirstOrDefault(d => d.Key == press.Key) is not { } def) return;

        switch (press.Kind)
        {
            case CellButton.Filter:
                FilterRequested?.Invoke(this, new ServiceFilterEventArgs(press.Key, AnchorAt(press.Area)));
                break;
            case CellButton.Checkbox:
                if (RowAt(press.Row) is { } checkRow) CheckToggled?.Invoke(this, new ServiceCellEventArgs(press.Row, checkRow, def, ""));
                break;
            case CellButton.Button:
                if (RowAt(press.Row) is { } buttonRow) ButtonClicked?.Invoke(this, new ServiceCellEventArgs(press.Row, buttonRow, def, ""));
                break;
            case CellButton.Choice:
                if (RowAt(press.Row) is { } choiceRow) OpenChoice(press.Row, choiceRow, def);
                break;
        }
    }

    // Настоящий список приложения над нарисованной ячейкой: те же отступы и высота, что у списка в
    // ячейке прежней таблицы; раскрывается сразу, закрытие снимает его с ячейки, выбор идет наверх.
    private void OpenChoice(int row, TableRowVm vm, TableColumnDef def)
    {
        var options = def.CellOptions?.Invoke(vm) ?? def.Options;
        var value   = vm[def.Key];
        var combo   = new ComboBox
        {
            ItemsSource         = options,
            SelectedItem        = value.Length > 0 && options.Contains(value) ? value : null,
            PlaceholderText     = def.Placeholder,
            Margin              = new Thickness(6, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Center,
            Height              = 26,
            MinHeight           = 26,
        };
        if (def.CellTone?.Invoke(vm) == CellTone.Red && this.TryFindResource("BrushRed", out var red) && red is IBrush redBrush)
            combo.PlaceholderForeground = redBrush;

        var picked = false;
        combo.SelectionChanged += (_, _) =>
        {
            if (picked || combo.SelectedItem is not string s || s == value) return;
            picked = true;
            ChoicePicked?.Invoke(this, new ServiceCellEventArgs(row, vm, def, s));
        };
        combo.DropDownClosed += (_, _) =>
        {
            if (ReferenceEquals(Editor.Editor, combo)) Editor.Close();
            Surface.Focus();
        };
        Editor.Open(row, def.Key, combo);
        // Раскрыть после раскладки: попап встает по границам уже размещенного списка.
        Dispatcher.UIThread.Post(() => { if (ReferenceEquals(Editor.Editor, combo)) combo.IsDropDownOpen = true; },
                                 DispatcherPriority.Loaded);
    }

    /// <summary>Невидимый якорь на месте прямоугольника поверхности — к нему крепится окно фильтра
    /// (Flyout открывается только у контрола).</summary>
    public Control AnchorAt(Rect surfaceRect)
    {
        Canvas.SetLeft(_filterAnchor, surfaceRect.X);
        Canvas.SetTop(_filterAnchor, surfaceRect.Y);
        _filterAnchor.Width  = Math.Max(1, surfaceRect.Width);
        _filterAnchor.Height = Math.Max(1, surfaceRect.Height);
        _anchors.UpdateLayout();
        return _filterAnchor;
    }

    // ── Прокрутка ─────────────────────────────────────────────────────────────

    private void CheckNearEnd()
    {
        var max = Surface.ExtentHeight - Surface.ViewportHeight;
        if (max > 0 && Surface.OffsetY >= max - NearEndThreshold) NearEnd?.Invoke(this, EventArgs.Empty);
    }

    private double BarThickness() =>
        this.TryFindResource("ScrollBarThickness", out var v) && v is double d ? d : 14;

    // Полосы только когда содержимое не помещается (как Auto у прежней таблицы); решение от размеров
    // панели, а не поверхности, — иначе показ одной полосы требовал бы вторую, та первую, и раскладка
    // мигала бы. Те же правила, что у панели таблицы коллекции.
    private void SyncBars()
    {
        _syncingBars = true;
        try
        {
            var bar   = BarThickness();
            var w     = Bounds.Width;
            var h     = Bounds.Height;
            var headH = Surface.HeaderHeight;
            var extW  = Surface.ExtentWidth;
            var extH  = Surface.ExtentHeight;
            var needV = extH > h - headH + 0.5;
            var needH = extW > w - (needV ? bar : 0) + 0.5;
            needV     = extH > h - headH - (needH ? bar : 0) + 0.5;
            _vertical.IsVisible   = needV;
            _horizontal.IsVisible = needH;
            _corner.IsVisible     = needV && needH;

            _vertical.Minimum      = 0;
            _vertical.Maximum      = Math.Max(0, extH - Surface.ViewportHeight);
            _vertical.ViewportSize = Surface.ViewportHeight;
            _vertical.SmallChange  = Surface.RowPitch;
            _vertical.LargeChange  = Math.Max(Surface.RowPitch, Surface.ViewportHeight);
            _vertical.Value        = Surface.OffsetY;

            // Окно горизонтальной полосы — без закрепленных колонок, как у прежней таблицы: ползунок
            // показывает долю прокручиваемой части, щелчок по дорожке листает на нее.
            var scrollW = Math.Max(0, Surface.ViewportWidth - Surface.FrozenColumnsWidth);
            _horizontal.Minimum      = 0;
            _horizontal.Maximum      = Math.Max(0, extW - Surface.ViewportWidth);
            _horizontal.ViewportSize = scrollW;
            _horizontal.SmallChange  = 48;
            _horizontal.LargeChange  = Math.Max(48, scrollW);
            _horizontal.Value        = Surface.OffsetX;
        }
        finally { _syncingBars = false; }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SyncBars();
    }
}
