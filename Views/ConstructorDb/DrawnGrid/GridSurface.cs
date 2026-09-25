using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using SmartBase.Logging;
using SmartBase.Controls.Behaviors;
using SmartBase.Controls.Components;
using SmartBase.Core.Models;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.ConstructorDb.DrawnGrid;

/// <summary>
/// Рисованная поверхность таблицы: шапка, строки и ячейки видимого окна рисуются напрямую в
/// <see cref="Render"/>, контролов у ячеек и заголовков нет. Строки берутся у виртуального списка
/// по индексу (первое обращение к странице заводит ее фоновую загрузку), колонки — у
/// <see cref="ColumnLayout"/>, строки по вертикали — у <see cref="RowLayout"/> (ручные высоты).
/// Цена кадра зависит только от размера окна: сколько строк и колонок в него попало, столько и
/// рисуется, что бы ни лежало в базе.
///
/// Вид — один в один с нынешней таблицей: те же токены (высота строки, шрифт, целая высота строки
/// текста, кисти, толщина линий, иконки), та же геометрия ячейки (отступ содержимого
/// <see cref="CellPad"/>, вертикальная линия у правого края, горизонтальная под строкой), тот же
/// отрисовщик на тип поля. Наведение подсвечивает ячейку и заголовок, как стили нынешней таблицы.
/// Токены перечитываются на смену зума, DPI, шрифта, высоты строк и темы.
///
/// Указатель: попадание считается арифметикой (<see cref="HitTest"/>), нажатия отдаются наверх
/// событием <see cref="Pressed"/>; жесты ширины колонки и высоты строки (полоски-захваты у
/// границ, видимые только курсором) поверхность ведет сама и сообщает панели стартом, сдвигом и
/// концом — модель ширин и высот живет у нее.
///
/// Выделение (этап 3) поверхность только рисует, модель — у фасада: прямоугольник ячеек
/// (<see cref="SetCellSelection"/>) — рамка в два физических пикселя внутрь границ и тонировка
/// акцентом у диапазона больше одной ячейки, в том же кадре, что ячейки; выделенные записи и
/// колонки (<see cref="IsRecordSelected"/>, <see cref="IsColumnSelected"/>) — заливка
/// <c>BrushAccentLight</c> строки и ячеек колонки, как у нынешней таблицы. Отдельного слоя,
/// отложенного пересчета и защиты от петли раскладки нет — рамка стоит по тем же границам, что
/// линии сетки, по построению.
/// </summary>
public sealed class GridSurface : Control
{
    /// <summary>Отступ содержимого ячейки от ее границ — как <c>CellContentPad</c> нынешней таблицы.</summary>
    public const double CellPad = 6;

    /// <summary>Половина ширины полоски-захвата (сама полоска 8 px, как у нынешних слоев ресайза).</summary>
    public const double GripHalf = 4;

    private const double IconSize      = 16;   // IconSizeSmall
    private const double ChevronButton = 20;   // кнопка фильтра в шапке 20×20
    private const double CheckSize     = 16;   // CheckBoxSize
    private const double CheckGlyph    = 12;   // CheckBoxGlyphSize
    private const double ClipPadLeft   = 4;    // Button.timeArrow Padding 4,0,8,0
    private const double ClipPadRight  = 8;

    private static readonly Cursor ColResizeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor RowResizeCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor HandCursor      = new(StandardCursorType.Hand);

    // ── Данные ────────────────────────────────────────────────────────────────
    private IList<TableRowVm>?          _rows;
    private INotifyCollectionChanged?   _rowsNotify;
    private ColumnLayout                _columns     = ColumnLayout.Empty;
    private RowLayout                   _rowLayout   = RowLayout.Uniform(37);
    private Func<int, RowStyle>         _rowStyle    = _ => RowStyle.None;
    private Func<string, RowStyle>      _columnStyle = _ => RowStyle.None;
    private RelationLinksCache?         _relLinks;

    // Строки, попавшие в последний кадр: их «Item» (страница дозаполнилась, правка) перерисовывает
    // сетку. Подписка снимается, как только строка уехала из окна.
    private readonly HashSet<TableRowVm> _watched    = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<TableRowVm> _visibleNow = new(ReferenceEqualityComparer.Instance);
    private readonly List<TableRowVm>    _toForget   = [];
    private bool _invalidatePending;

    // ── Прокрутка ─────────────────────────────────────────────────────────────
    private double _offsetX;
    private double _offsetY;

    // ── Наведение и жесты ─────────────────────────────────────────────────────
    private GridHit _hover = GridHit.None;
    private Point?  _lastPointer;   // где указатель: после прокрутки наведение пересчитывается без движения мыши
    private enum Drag { None, Column, Row }
    private Drag    _drag;
    private string? _dragKey;
    private int     _dragRow = -1;
    private Point   _dragStart;

    // ── Выделение (модель — у фасада, здесь только что рисовать) ──────────────
    // Прямоугольник ячеек: строки — индексы в срезе, колонки — КЛЮЧИ крайних видимых колонок:
    // раскладка меняется (ширины, скрытие, сдвиг), а ключ переживает любую пересборку, и позиции
    // считаются на кадре двоичным поиском по раскладке. Тонировка — у диапазона больше ячейки.
    private (int Top, int Bottom, string First, string Last)? _cellSel;
    private bool _cellSelTinted;

    // ── Токены вида ───────────────────────────────────────────────────────────
    private bool     _tokensReady;
    private IBrush   _bg          = Brushes.White;
    private IBrush   _inactiveBg  = Brushes.WhiteSmoke;
    private IBrush   _hoverBg     = Brushes.WhiteSmoke;   // BrushSecondaryBg — наведение на ячейку
    private IBrush   _hoverStrong = Brushes.LightGray;    // BrushHover — наведение на заголовок и кнопку
    private IBrush   _primary     = Brushes.Black;
    private IBrush   _secondary   = Brushes.Gray;
    private IBrush   _accent      = Brushes.RoyalBlue;
    private IBrush   _accentLight = Brushes.LightBlue;    // BrushAccentLight — выделенная запись и колонка
    private IBrush   _selectTint  = new SolidColorBrush(Colors.RoyalBlue, 0.08);   // тонировка диапазона ячеек
    private IBrush   _red         = Brushes.Red;
    private Pen      _separator   = new(Brushes.LightGray, 1);
    private Pen      _checkBorder = new(Brushes.Black, 1);
    private double   _lineWidth   = 1;
    private double   _rowHeight   = 36;
    private double   _lineHeight  = 16;     // LineHeightBase — целая высота строки текста ячеек
    private Typeface _typeface    = Typeface.Default;
    private double   _fontSize    = 12;
    private double   _iconStroke  = 1.8;
    private Geometry? _chevron;
    private Geometry? _funnel;
    private Geometry? _check;
    private Geometry? _paperclip;
    private TextLayoutCache? _text;
    private readonly Dictionary<string, Geometry?> _typeIcons = new();
    private readonly Dictionary<IBrush, Pen>       _iconPens  = new(ReferenceEqualityComparer.Instance);
    private static readonly TextDecorationCollection Underline = [new TextDecoration { Location = TextDecorationLocation.Underline }];

    private double _frameScale = 1;   // RenderScaling × зум на текущий кадр (снап на физический пиксель)

    // ── Замеры (стенды) ───────────────────────────────────────────────────────
    public TimeSpan LastRender     { get; private set; }
    public int      LastCellsDrawn { get; private set; }
    public int      TextCacheCount => _text?.Count  ?? 0;
    public int      TextCacheHits  => _text?.Hits   ?? 0;
    public int      TextCacheMisses => _text?.Misses ?? 0;

    /// <summary>Отладочные выключатели для стендов: без текста / без ячеек вовсе (только линии и
    /// шапка). Ищут, какая часть кадра сколько стоит; в программе всегда false.</summary>
    public static bool DebugSkipText  { get; set; }
    public static bool DebugSkipCells { get; set; }

    // ── События ───────────────────────────────────────────────────────────────
    public event EventHandler? OffsetChanged;
    public event EventHandler? ExtentChanged;
    /// <summary>Токены перечитаны (зум, DPI, шрифт, высота строк, тема): панели пора пересобрать раскладки.</summary>
    public event EventHandler? MetricsChanged;
    /// <summary>Нажатие указателя на ячейке, строке «+», заголовке, кнопке фильтра или «+» шапки.</summary>
    public event EventHandler<GridPointerEventArgs>? Pressed;
    /// <summary>Жест ширины колонки: старт (ключ), сдвиг курсора по X от старта, конец (true — зафиксировать).</summary>
    public event Action<string>?         ColumnResizeStarted;
    public event Action<string, double>? ColumnResizeMoved;
    public event Action<string, bool>?   ColumnResizeEnded;
    /// <summary>Жест высоты строки: старт (индекс строки), сдвиг курсора по Y от старта, конец (true — зафиксировать).</summary>
    public event Action<int>?         RowResizeStarted;
    public event Action<int, double>? RowResizeMoved;
    public event Action<int, bool>?   RowResizeEnded;

    public GridSurface()
    {
        ClipToBounds = true;
        Focusable    = true;
    }

    // ── Публичный контракт ────────────────────────────────────────────────────

    public IList<TableRowVm>? Rows      => _rows;
    public ColumnLayout       Columns   => _columns;
    public RowLayout          RowLayout => _rowLayout;
    public int RowCount => _rows?.Count ?? 0;

    /// <summary>Полоски-захваты ширины колонок активны (ручный режим ширины).</summary>
    public bool ColumnGripsEnabled { get; set; }

    /// <summary>Полоски-захваты высоты строк активны (ручный режим высоты).</summary>
    public bool RowGripsEnabled { get; set; }

    /// <summary>Колонки с активным фильтром или сортировкой — в шапке у них воронка вместо шеврона.</summary>
    public Func<string, bool> IsColumnFiltered { get; set; } = _ => false;

    /// <summary>Выделена ли запись (клик по «№»): строка заливается <c>BrushAccentLight</c> под ячейками,
    /// «№» и «+» остаются серыми — как <c>DataGridRow:selected</c> нынешней таблицы.</summary>
    public Func<int, bool> IsRecordSelected { get; set; } = _ => false;

    /// <summary>Выделена ли колонка (клик по заголовку): ячейки значений заливаются
    /// <c>BrushAccentLight</c>, заголовок не красится — как у нынешней таблицы.</summary>
    public Func<string, bool> IsColumnSelected { get; set; } = _ => false;

    /// <summary>Поставить рамку выделения ячеек: строки [top, bottom] среза, колонки — ключи крайних
    /// ВИДИМЫХ колонок диапазона (в порядке показа). Тонировка — когда ячеек больше одной
    /// (<paramref name="tinted"/>): скрытые колонки внутри диапазона в счет не идут, это знает фасад.</summary>
    public void SetCellSelection(int top, int bottom, string firstKey, string lastKey, bool tinted)
    {
        var next = (Math.Min(top, bottom), Math.Max(top, bottom), firstKey, lastKey);
        if (_cellSel == next && _cellSelTinted == tinted) return;
        _cellSel       = next;
        _cellSelTinted = tinted;
        InvalidateVisual();
    }

    /// <summary>Снять рамку выделения ячеек.</summary>
    public void ClearCellSelection()
    {
        if (_cellSel is null) return;
        _cellSel = null;
        InvalidateVisual();
    }

    /// <summary>Выделение записей или колонок сменилось — перерисовать (сами множества — у фасада).</summary>
    public void RefreshSelectionVisuals() => InvalidateVisual();

    /// <summary>Прямоугольник рамки выделения ячеек в координатах поверхности при текущей прокрутке
    /// (без обрезки по окну); null — рамки нет или ее колонки не показаны. Для стендов и меню.</summary>
    public Rect? CellSelectionBounds()
    {
        if (_cellSel is not { } sel) return null;
        var c0 = _columns.IndexOf(sel.First);
        var c1 = _columns.IndexOf(sel.Last);
        if (c0 < 0 || c1 < 0) return null;
        if (c1 < c0) (c0, c1) = (c1, c0);
        var x = _columns.Left(c0) - _offsetX;
        var y = RowTop(sel.Top);
        // Ячейка — до вертикальной линии включительно и без линии под строкой: так же стояли
        // границы DataGridCell, по которым рисовалась рамка нынешней таблицы.
        return new Rect(x, y, _columns.Right(c1) - _columns.Left(c0),
                        Math.Max(0, _rowLayout.Bottom(sel.Bottom) - _rowLayout.Top(sel.Top) - _lineWidth));
    }

    /// <summary>Высота строки из токена «Оформления» (без линии).</summary>
    public double RowHeight    { get { EnsureTokens(); return _rowHeight; } }

    /// <summary>Шаг строк: высота плюс линия под ней — так же, как у нынешней сетки, где линия
    /// PART_BottomGridLine добавляется к высоте строки (36 + 1 = 37 при стандартных токенах).</summary>
    public double RowPitch     { get { EnsureTokens(); return _rowHeight + _lineWidth; } }

    /// <summary>Высота шапки: высота строки, линия под шапкой лежит внутри нее (как
    /// PART_ColumnHeadersAndRowsSeparator, прижатый к низу строки заголовков).</summary>
    public double HeaderHeight => RowHeight;

    /// <summary>Толщина линий сетки (токен SeparatorThickness).</summary>
    public double LineWidth    { get { EnsureTokens(); return _lineWidth; } }

    public double ExtentWidth    => _columns.TotalWidth;
    public double ExtentHeight   => _rowLayout.TotalHeight(RowCount);
    public double ViewportWidth  => Math.Max(0, Bounds.Width);
    public double ViewportHeight => Math.Max(0, Bounds.Height - HeaderHeight);

    public double OffsetX { get => _offsetX; set => SetOffsets(value, _offsetY); }
    public double OffsetY { get => _offsetY; set => SetOffsets(_offsetX, value); }

    /// <summary>Подключить источник строк и раскладку колонок. Стили строк и колонок — словари
    /// таблицы (оформление одной таблицы), кеш связей — тот же, что прогревает список строк.</summary>
    public void Attach(IList<TableRowVm> rows, ColumnLayout columns,
                       Func<int, RowStyle>? rowStyle, Func<string, RowStyle>? columnStyle,
                       RelationLinksCache? relLinks)
    {
        if (_rowsNotify is not null) _rowsNotify.CollectionChanged -= OnRowsChanged;
        ForgetWatched();
        CancelDrag();

        _rows        = rows;
        _rowsNotify  = rows as INotifyCollectionChanged;
        _columns     = columns;
        _rowStyle    = rowStyle    ?? (_ => RowStyle.None);
        _columnStyle = columnStyle ?? (_ => RowStyle.None);
        _relLinks    = relLinks;
        _hover       = GridHit.None;

        if (_rowsNotify is not null) _rowsNotify.CollectionChanged += OnRowsChanged;

        _lastColumnWindow = (-1, -1);
        ClampOffsets();
        ExtentChanged?.Invoke(this, EventArgs.Empty);
        UpdateWindow();
        InvalidateVisual();
    }

    /// <summary>Отключить источник строк: пустая поверхность (коллекция закрыта).</summary>
    public void Detach()
    {
        if (_rowsNotify is not null) _rowsNotify.CollectionChanged -= OnRowsChanged;
        ForgetWatched();
        CancelDrag();
        _rows       = null;
        _rowsNotify = null;
        _columns    = ColumnLayout.Empty;
        _relLinks   = null;
        _hover      = GridHit.None;
        _lastColumnWindow = (-1, -1);
        _offsetX = _offsetY = 0;
        ExtentChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Сменить раскладку колонок (ресайз окна, скрытие, сдвиг, ширины) — строки не трогаются.</summary>
    public void SetColumns(ColumnLayout columns)
    {
        _columns = columns;
        _lastColumnWindow = (-1, -1);
        ClampOffsets();
        ExtentChanged?.Invoke(this, EventArgs.Empty);
        UpdateWindow();
        InvalidateVisual();
    }

    /// <summary>Сменить раскладку строк (ручные высоты, смена высоты строк «Оформления»).</summary>
    public void SetRowLayout(RowLayout layout)
    {
        _rowLayout = layout;
        ClampOffsets();
        ExtentChanged?.Invoke(this, EventArgs.Empty);
        UpdateWindow();
        InvalidateVisual();
    }

    // ── Окно строк и колонок для списка ───────────────────────────────────────

    private (int First, int Last) _lastColumnWindow = (-1, -1);

    // Сообщить списку, что сейчас видно: страницы видимых строк (плюс одна упреждающая в сторону
    // прокрутки) и поля видимых колонок с запасом в одно окно по обе стороны — чтобы при прокрутке
    // вбок значения соседних колонок уже лежали в строках, а дочитка шла реже. Зовется на каждом
    // сдвиге; дешево — арифметика и пара обращений к словарю страниц.
    private void UpdateWindow()
    {
        if (_rows is not VirtualRowList list || _columns.Count == 0) return;
        var w = ViewportWidth;
        if (w <= 0 || ViewportHeight <= 0) return;
        var t0 = GridTimings.Start();

        var (c0, c1) = _columns.VisibleRange(_offsetX, w);
        var span = Math.Max(1, c1 - c0 + 1);
        var from = Math.Max(0, c0 - span);
        var to   = Math.Min(_columns.Count - 1, c1 + span);
        if ((from, to) != _lastColumnWindow)
        {
            _lastColumnWindow = (from, to);
            var fields = new List<int>(to - from + 1);
            for (var c = from; c <= to; c++)
                if (_columns[c].Tag is int fid) fields.Add(fid);
            list.SetFieldWindow(fields);
        }

        var (r0, r1) = VisibleRows();
        if (r1 >= r0) list.EnsureRange(r0, r1);
        GridTimings.Stop(t0, "окно строк и колонок (UI)");
    }

    /// <summary>Все видимые ячейки с данными: строки заполнены и значения видимых колонок дочитаны
    /// (для стендов: «кадр с данными» после прокрутки вбок).</summary>
    public bool VisibleCellsLoaded()
    {
        if (!VisibleRowsLoaded()) return false;
        if (_rows is not VirtualRowList list || ViewportWidth <= 0) return true;
        var (c0, c1) = _columns.VisibleRange(_offsetX, ViewportWidth);
        var (r0, r1) = VisibleRows();
        for (var r = r0; r <= r1; r++)
            for (var c = c0; c <= c1; c++)
                if (_columns[c].Tag is int fid && !list.IsCellLoaded(r, fid)) return false;
        return true;
    }

    /// <summary>Строки, попадающие в окно при текущей прокрутке (включая частично видимые).</summary>
    public (int First, int Last) VisibleRows() => _rowLayout.VisibleRange(_offsetY, ViewportHeight, RowCount);

    /// <summary>Все видимые строки уже с данными (заглушек «Загрузка…» в окне нет).</summary>
    public bool VisibleRowsLoaded()
    {
        if (_rows is null) return true;
        var (first, last) = VisibleRows();
        for (var i = first; i <= last; i++)
            if (_rows[i].IsLoading) return false;
        return true;
    }

    /// <summary>Верх строки в координатах поверхности при текущей прокрутке.</summary>
    public double RowTop(int index) => HeaderHeight + _rowLayout.Top(index) - _offsetY;

    /// <summary>Прямоугольник ячейки в координатах поверхности (без учета видимости).</summary>
    public Rect CellRect(int row, int column) =>
        new(_columns.Left(column) - _offsetX, RowTop(row), _columns.Width(column), _rowLayout.Height(row));

    // ── Попадание ─────────────────────────────────────────────────────────────

    /// <summary>Что лежит под точкой поверхности. Полоски-захваты отвечают только в ручных режимах.</summary>
    public GridHit HitTest(Point p)
    {
        EnsureTokens();
        if (_rows is null || _columns.Count == 0) return GridHit.None;
        var headH = HeaderHeight;
        var t     = _lineWidth;

        var xContent = p.X + _offsetX;
        var c = _columns.IndexAt(xContent);
        if (c < 0) return GridHit.None;

        if (p.Y < headH)
        {
            // Полоска ширины: правая граница колонки c или левая граница (= правая у c − 1).
            if (ColumnGripsEnabled)
            {
                if (_columns[c].IsStarWidth && Math.Abs(p.X - (_columns.Right(c) - _offsetX)) <= GripHalf)
                    return new GridHit(GridHitKind.ColumnGrip, -1, c, _columns[c], HeaderRect(c));
                if (c > 0 && _columns[c - 1].IsStarWidth && Math.Abs(p.X - (_columns.Left(c) - _offsetX)) <= GripHalf)
                    return new GridHit(GridHitKind.ColumnGrip, -1, c - 1, _columns[c - 1], HeaderRect(c - 1));
            }
            var def  = _columns[c];
            var rect = HeaderRect(c);
            if (def.Key == TableColumnDef.AddPropKey) return new GridHit(GridHitKind.HeaderAdd, -1, c, def, rect);
            if (def.Key == TableColumnDef.NumKey)     return new GridHit(GridHitKind.Header, -1, c, def, rect);
            var btn = ChevronRect(rect, t);
            if (btn.Contains(p)) return new GridHit(GridHitKind.HeaderFilter, -1, c, def, btn);
            return new GridHit(GridHitKind.Header, -1, c, def, rect);
        }

        var yContent = p.Y - headH + _offsetY;
        var count = RowCount;
        var r = _rowLayout.IndexAt(yContent, count);
        if (r < 0) return GridHit.None;

        if (RowGripsEnabled)
        {
            // Нижняя граница строки r или верхняя (= нижняя у r − 1); у строки «+» захвата нет.
            if (IsDataRow(r) && Math.Abs(yContent - _rowLayout.Bottom(r)) <= GripHalf)
                return new GridHit(GridHitKind.RowGrip, r, c, _columns[c], CellRect(r, c));
            if (r > 0 && IsDataRow(r - 1) && Math.Abs(yContent - _rowLayout.Top(r)) <= GripHalf)
                return new GridHit(GridHitKind.RowGrip, r - 1, c, _columns[c], CellRect(r - 1, c));
        }
        var row = _rows[r];
        return new GridHit(row.IsAddRow ? GridHitKind.AddRow : GridHitKind.Cell, r, c, _columns[c], CellRect(r, c));
    }

    private bool IsDataRow(int index) => _rows is not null && index >= 0 && index < _rows.Count && !_rows[index].IsAddRow;

    private Rect HeaderRect(int c) => new(_columns.Left(c) - _offsetX, 0, _columns.Width(c), HeaderHeight);

    // Кнопка фильтра 20×20 у правого края заголовка за отступом (Border Padding 6 у нынешней шапки),
    // перед вертикальной линией; по вертикали — по центру.
    private static Rect ChevronRect(Rect header, double lineWidth) =>
        new(header.Right - lineWidth - CellPad - ChevronButton, header.Y + (header.Height - ChevronButton) / 2,
            ChevronButton, ChevronButton);

    // ── Прокрутка ─────────────────────────────────────────────────────────────

    private void SetOffsets(double x, double y)
    {
        var maxX = Math.Max(0, ExtentWidth  - ViewportWidth);
        var maxY = Math.Max(0, ExtentHeight - ViewportHeight);
        x = Math.Clamp(double.IsNaN(x) ? 0 : x, 0, maxX);
        y = Math.Clamp(double.IsNaN(y) ? 0 : y, 0, maxY);
        if (x == _offsetX && y == _offsetY) return;
        _offsetX = x;
        _offsetY = y;
        OffsetChanged?.Invoke(this, EventArgs.Empty);
        UpdateWindow();
        RefreshHover();
        InvalidateVisual();
    }

    // Под указателем теперь другая ячейка (прокрутка колесом или полосой, приход строк) —
    // подсветка следует за содержимым, а не за прежним индексом.
    private void RefreshHover()
    {
        if (_drag != Drag.None || _lastPointer is not { } p) return;
        SetHover(HitTest(p));
    }

    private void ClampOffsets() => SetOffsets(_offsetX, _offsetY);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var dy = -e.Delta.Y * RowPitch * 3;
        var dx = -e.Delta.X * 48;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && dx == 0) { dx = dy; dy = 0; }
        SetOffsets(_offsetX + dx, _offsetY + dy);
        e.Handled = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ClampOffsets();
        ExtentChanged?.Invoke(this, EventArgs.Empty);
        UpdateWindow();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _tokensReady = false;
        EnsureTokens();
        LineMetrics.Changed          += OnMetricsChanged;
        FontScale.Changed            += OnMetricsChanged;
        ConstructorSettings.Changed  += OnMetricsChanged;
        ActualThemeVariantChanged    += OnThemeChanged;
        FileProbe.Resolved           += RequestInvalidate;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LineMetrics.Changed          -= OnMetricsChanged;
        FontScale.Changed            -= OnMetricsChanged;
        ConstructorSettings.Changed  -= OnMetricsChanged;
        ActualThemeVariantChanged    -= OnThemeChanged;
        FileProbe.Resolved           -= RequestInvalidate;
        base.OnDetachedFromVisualTree(e);
        ForgetWatched();
        CancelDrag();
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ClampOffsets();
        ExtentChanged?.Invoke(this, EventArgs.Empty);
        UpdateWindow();
        RequestInvalidate();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => OnMetricsChanged();

    // Зум, DPI, размер шрифта, высота строк «Оформления», тема: перечитать токены. Событие Changed
    // настроек раздела поднимают и посторонние настройки — пересборку раскладок панели просим
    // только когда изменилось что-то из геометрии.
    private void OnMetricsChanged()
    {
        var before = (_rowHeight, _lineWidth, _fontSize, _lineHeight, _typeface);
        _tokensReady = false;
        EnsureTokens();
        InvalidateVisual();
        if (before != (_rowHeight, _lineWidth, _fontSize, _lineHeight, _typeface))
            MetricsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Наблюдение за видимыми строками ───────────────────────────────────────

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => RequestInvalidate();

    // Одна перерисовка на кадр, сколько бы строк ни дозаполнилось разом (страница — это 200 «Item»).
    private void RequestInvalidate()
    {
        if (_invalidatePending) return;
        _invalidatePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _invalidatePending = false;
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    private void SyncWatched()
    {
        _toForget.Clear();
        foreach (var row in _watched)
            if (!_visibleNow.Contains(row)) _toForget.Add(row);
        foreach (var row in _toForget)
        {
            row.PropertyChanged -= OnRowChanged;
            _watched.Remove(row);
        }
        foreach (var row in _visibleNow)
            if (_watched.Add(row)) row.PropertyChanged += OnRowChanged;
    }

    private void ForgetWatched()
    {
        foreach (var row in _watched) row.PropertyChanged -= OnRowChanged;
        _watched.Clear();
        _visibleNow.Clear();
    }

    // ── Указатель: наведение и жесты ──────────────────────────────────────────

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _lastPointer = p;

        if (_drag == Drag.Column && _dragKey is not null)
        {
            ColumnResizeMoved?.Invoke(_dragKey, p.X - _dragStart.X);
            e.Handled = true;
            return;
        }
        if (_drag == Drag.Row && _dragRow >= 0)
        {
            RowResizeMoved?.Invoke(_dragRow, p.Y - _dragStart.Y);
            e.Handled = true;
            return;
        }

        SetHover(HitTest(p));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _lastPointer = null;
        if (_drag == Drag.None) SetHover(GridHit.None);
    }

    private void SetHover(GridHit hit)
    {
        var same = hit.Kind == _hover.Kind && hit.Row == _hover.Row && hit.Column == _hover.Column;
        if (same) return;

        // Перерисовывается только то, что меняет вид: ячейка, заголовок и его кнопка.
        var visual = HoverVisible(hit) || HoverVisible(_hover);
        _hover = hit;
        Cursor = hit.Kind switch
        {
            GridHitKind.ColumnGrip => ColResizeCursor,
            GridHitKind.RowGrip    => RowResizeCursor,
            GridHitKind.HeaderAdd  => HandCursor,
            GridHitKind.HeaderFilter => HandCursor,   // Button.icon несет курсор-руку
            _                      => null,
        };
        ToolTip.SetTip(this, hit.Kind == GridHitKind.HeaderFilter ? "Фильтр и сортировка" : null);
        if (visual) InvalidateVisual();
    }

    private static bool HoverVisible(GridHit hit) =>
        hit.Kind is GridHitKind.Cell or GridHitKind.AddRow or GridHitKind.Header or GridHitKind.HeaderFilter;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p   = e.GetPosition(this);
        var hit = HitTest(p);
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsLeftButtonPressed && hit.Kind == GridHitKind.ColumnGrip && hit.Def is not null)
        {
            _drag      = Drag.Column;
            _dragKey   = hit.Def.Key;
            _dragStart = p;
            e.Pointer.Capture(this);
            ColumnResizeStarted?.Invoke(_dragKey);
            e.Handled = true;
            return;
        }
        if (props.IsLeftButtonPressed && hit.Kind == GridHitKind.RowGrip)
        {
            _drag      = Drag.Row;
            _dragRow   = hit.Row;
            _dragStart = p;
            e.Pointer.Capture(this);
            RowResizeStarted?.Invoke(_dragRow);
            e.Handled = true;
            return;
        }

        if (hit.Kind != GridHitKind.None)
            Pressed?.Invoke(this, new GridPointerEventArgs(hit, e));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None) return;
        EndDrag(commit: true);
        e.Pointer.Capture(null);
        e.Handled = true;
        SetHover(HitTest(e.GetPosition(this)));
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // Захват потерян (Alt+Tab, модальное окно поверх): жест закрываем без записи — иначе
        // зависший флаг заблокировал бы наведение и следующий клик продолжил бы перетаскивание.
        CancelDrag();
    }

    private void CancelDrag() { if (_drag != Drag.None) EndDrag(commit: false); }

    private void EndDrag(bool commit)
    {
        var kind = _drag;
        _drag = Drag.None;
        if (kind == Drag.Column && _dragKey is not null) ColumnResizeEnded?.Invoke(_dragKey, commit);
        if (kind == Drag.Row && _dragRow >= 0)           RowResizeEnded?.Invoke(_dragRow, commit);
        _dragKey = null;
        _dragRow = -1;
    }

    // ── Токены вида ───────────────────────────────────────────────────────────

    private object? Res(string key) =>
        this.TryFindResource(key, out var v) ? v
        : Application.Current is { } app && app.TryFindResource(key, out v) ? v
        : null;

    private IBrush    Brush(string key, IBrush fallback) => Res(key) is IBrush b ? b : fallback;
    private double    Dbl(string key, double fallback)   => Res(key) is double d ? d : fallback;
    private Geometry? Geo(string key)                    => Res(key) as Geometry;

    private void EnsureTokens()
    {
        if (_tokensReady) return;
        _tokensReady = true;

        _bg          = Brush("BrushBg",            Brushes.White);
        _inactiveBg  = Brush("BrushInactiveBg",    Brushes.WhiteSmoke);
        _hoverBg     = Brush("BrushSecondaryBg",   Brushes.WhiteSmoke);
        _hoverStrong = Brush("BrushHover",         Brushes.LightGray);
        _primary     = Brush("BrushPrimaryText",   Brushes.Black);
        _secondary   = Brush("BrushSecondaryText", Brushes.Gray);
        _accent      = Brush("BrushAccent",        Brushes.RoyalBlue);
        _accentLight = Brush("BrushAccentLight",   Brushes.LightBlue);
        // Тонировка диапазона — акцент с прозрачностью 0,08, как Background рамки нынешнего слоя.
        _selectTint  = new SolidColorBrush(_accent is ISolidColorBrush sc ? sc.Color : Colors.RoyalBlue, 0.08);
        _red         = Brush("BrushRed",           Brushes.Red);
        _lineWidth   = Dbl("SeparatorThickness", 1);
        _separator   = new Pen(Brush("BrushSeparator", Brushes.LightGray), _lineWidth);
        _checkBorder = new Pen(_primary, _lineWidth);
        _rowHeight   = Dbl(ConstructorSettings.RowHeightResourceKey, ConstructorSettings.RowHeight);
        _fontSize    = Dbl("FontSizeBase", 12);
        _typeface    = Res("FontFamilyBase") is FontFamily family ? new Typeface(family) : Typeface.Default;
        _iconStroke  = Dbl("IconStrokeSmall", 1.8);
        _chevron     = Geo("IconChevronDown");
        _funnel      = Geo("IconFilter");
        _check       = Geo("CheckBoxGlyphGeometry");
        _paperclip   = Geo("IconFieldFile");

        // Целая высота строки текста — тот же токен, что у надписей и редакторов ячеек нынешней
        // таблицы (LineHeightBase пишет FontScale): боксы показа и правки одной высоты на любой сетке.
        var probe = new TextLayout("Ag", _typeface, _fontSize, _primary);
        var natural = probe.TextLines.Count > 0 && probe.TextLines[0].Height > 0 ? probe.TextLines[0].Height : _fontSize * 1.35;
        _lineHeight = Dbl("LineHeightBase", Math.Ceiling(natural));

        _text = new TextLayoutCache(_typeface, _fontSize);
        _typeIcons.Clear();
        _iconPens.Clear();
    }

    // ── Рисование ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        var sw = Stopwatch.StartNew();
        EnsureTokens();

        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.FillRectangle(_bg, new Rect(0, 0, w, h));

        var cells = 0;
        _visibleNow.Clear();

        if (_rows is not null && _columns.Count > 0 && w > 0 && h > 0)
        {
            var scale = _frameScale = LineMetrics.EffectiveScale(this);
            var t     = _lineWidth;
            var headH = HeaderHeight;

            var (c0, c1) = _columns.VisibleRange(_offsetX, w);
            var (r0, r1) = VisibleRows();

            // Тело таблицы — под шапкой.
            var body = new Rect(0, headH, w, Math.Max(0, h - headH));
            var tRows = 0.0;
            using (ctx.PushClip(body))
            {
                for (var r = r0; r <= r1; r++)
                {
                    var tr  = GridTimings.Start();
                    var row = _rows[r];
                    GridTimings.Stop(tr, "рисование: строка из списка");
                    _visibleNow.Add(row);

                    var pitch    = _rowLayout.Height(r);
                    var y        = headH + _rowLayout.Top(r) - _offsetY;
                    var rowStyle = row.IsAddRow || row.IsLoading ? RowStyle.None : _rowStyle(row.RecordId);
                    var hoverCol = _hover.Kind is GridHitKind.Cell or GridHitKind.AddRow && _hover.Row == r ? _hover.Column : -1;

                    // Выделенная запись — заливка всей строки под ячейками (BackgroundRectangle строки
                    // нынешней таблицы); «№» и «+» закроют ее своей серой заливкой.
                    if (!row.IsAddRow && row.RecordId > 0 && IsRecordSelected(row.RecordId))
                        ctx.FillRectangle(_accentLight, new Rect(0, y, w, Math.Max(0, pitch - t)));

                    var tc = GridTimings.Start();
                    for (var c = c0; c <= c1; c++)
                    {
                        var cell = new Rect(_columns.Left(c) - _offsetX, y, _columns.Width(c), pitch);
                        DrawCell(ctx, row, _columns[c], cell, rowStyle, hoverCol == c, scale);
                        cells++;
                    }
                    if (tc != 0) tRows += Stopwatch.GetElapsedTime(tc).TotalMilliseconds;

                    // Нижняя линия строки — внутри строки, как PART_BottomGridLine.
                    var ly = Snap(y + pitch - t, scale) + t / 2;
                    ctx.DrawLine(_separator, new Point(0, ly), new Point(w, ly));
                }
            }
            GridTimings.Record("рисование: ячейки тела", tRows, $"ячеек {cells}");

            var th = GridTimings.Start();
            DrawHeader(ctx, c0, c1, w, headH, scale);
            GridTimings.Stop(th, "рисование: шапка");

            // Вертикальные линии — у правой границы каждой колонки, сквозь шапку и тело.
            for (var c = c0; c <= c1; c++)
            {
                var lx = Snap(_columns.Right(c) - _offsetX - t, scale) + t / 2;
                ctx.DrawLine(_separator, new Point(lx, 0), new Point(lx, h));
            }

            // Рамка выделения ячеек — поверх линий, обрезана по области строк (не ложится на шапку;
            // полосы прокрутки — отдельные контролы за краем поверхности).
            if (CellSelectionBounds() is { } sel && sel.Intersects(body))
                using (ctx.PushClip(body))
                    DrawSelectionFrame(ctx, sel, scale);
        }

        var ts = GridTimings.Start();
        SyncWatched();
        GridTimings.Stop(ts, "рисование: подписки строк");
        LastCellsDrawn = cells;
        LastRender     = sw.Elapsed;
        GridTimings.Record("рисование кадра (UI)", LastRender.TotalMilliseconds, $"ячеек {cells}");
    }

    private static double Snap(double v, double scale) => Math.Round(v * scale) / scale;

    // Одна рамка на все выделение (а не вокруг каждой ячейки), как в табличных редакторах: два
    // физических пикселя ВНУТРЬ границ, одинаково со всех сторон, края на целых пикселях — при
    // зуме и масштабе экрана не размывается (нынешний Border давал 2 DIP × зум, здесь то же:
    // толщина округляется в целые пиксели от эффективного масштаба). Диапазон больше ячейки
    // тонируется акцентом с прозрачностью 0,08 — значения читаются, размер прямоугольника виден.
    private void DrawSelectionFrame(DrawingContext ctx, Rect sel, double scale)
    {
        var x0 = Snap(sel.X, scale);
        var y0 = Snap(sel.Y, scale);
        var x1 = Snap(sel.Right, scale);
        var y1 = Snap(sel.Bottom, scale);
        if (x1 <= x0 || y1 <= y0) return;
        var ft = Math.Min(Math.Max(1.0, Math.Round(2.0 * scale)) / scale, Math.Min(x1 - x0, y1 - y0) / 2);

        if (_cellSelTinted) ctx.FillRectangle(_selectTint, new Rect(x0, y0, x1 - x0, y1 - y0));
        ctx.FillRectangle(_accent, new Rect(x0, y0, x1 - x0, ft));             // верх
        ctx.FillRectangle(_accent, new Rect(x0, y1 - ft, x1 - x0, ft));        // низ
        ctx.FillRectangle(_accent, new Rect(x0, y0 + ft, ft, y1 - y0 - 2 * ft));      // лево
        ctx.FillRectangle(_accent, new Rect(x1 - ft, y0 + ft, ft, y1 - y0 - 2 * ft)); // право
    }

    private void DrawHeader(DrawingContext ctx, int c0, int c1, double w, double headH, double scale)
    {
        var t = _lineWidth;
        // Фон строки заголовков одним прямоугольником на всю ширину — как в шаблоне нынешней таблицы.
        ctx.FillRectangle(_inactiveBg, new Rect(0, 0, w, headH));

        for (var c = c0; c <= c1; c++)
        {
            var def  = _columns[c];
            var x    = _columns.Left(c) - _offsetX;
            var cw   = _columns.Width(c);
            var rect = new Rect(x, 0, cw, headH);
            // Область заголовка без вертикальной линии (Border в звездной колонке, линия в Auto).
            var inner = new Rect(x, 0, Math.Max(0, cw - t), headH);

            if (def.Key is TableColumnDef.NumKey or TableColumnDef.AddPropKey)
            {
                // «№» и «+» без подсветки наведения — как центрованные заголовки нынешней шапки.
                DrawText(ctx, def.Header, inner, _secondary, "s", TextAlignment.Center, 1, double.NaN, pad: 0);
                continue;
            }

            // Наведение на заголовок — как Border.colHeader:pointerover.
            if (_hover.Kind is GridHitKind.Header or GridHitKind.HeaderFilter && _hover.Column == c)
                ctx.FillRectangle(_hoverStrong, inner);

            // [иконка типа][название][кнопка фильтра] в отступах CellPad — как MakeTextHeader.
            var left = x + CellPad;
            var icon = TypeIcon(def.FieldType);
            if (icon is not null)
            {
                var stroke = def.Key == TableRowVm.TitleKey ? _red : _secondary;
                DrawIcon(ctx, icon, left, Snap((headH - IconSize) / 2, scale), stroke);
                left += IconSize + CellPad;
            }

            var btn = ChevronRect(rect, t);
            if (_hover.Kind == GridHitKind.HeaderFilter && _hover.Column == c)
                ctx.DrawRectangle(_hoverStrong, null, new RoundedRect(btn, 4));
            var filtered = IsColumnFiltered(def.Key);
            var glyph    = filtered ? _funnel ?? _chevron : _chevron;
            if (glyph is not null)
                DrawIcon(ctx, glyph, btn.X + (ChevronButton - IconSize) / 2, Snap(btn.Y + (ChevronButton - IconSize) / 2, scale),
                         filtered ? _accent : _secondary, thickness: 2);

            var textRect = new Rect(left, 0, Math.Max(1, btn.X - left), headH);
            DrawText(ctx, def.Header, textRect, _secondary, "s", TextAlignment.Left, 1, double.NaN, pad: 0);
        }

        // Линия под шапкой — как PART_ColumnHeadersAndRowsSeparator, прижатый к низу строки заголовков.
        var ly = Snap(headH - t, scale) + t / 2;
        ctx.DrawLine(_separator, new Point(0, ly), new Point(w, ly));
    }

    private void DrawCell(DrawingContext ctx, TableRowVm row, TableColumnDef def, Rect cell, RowStyle rowStyle,
                          bool hovered, double scale)
    {
        if (DebugSkipCells) return;
        var t = _lineWidth;
        // Область содержимого: без вертикальной линии справа и линии под строкой.
        var inner = new Rect(cell.X, cell.Y, Math.Max(0, cell.Width - t), Math.Max(0, cell.Height - t));

        // «№» и «+»: непрозрачная серая заливка поверх подсветки наведения — как сегодня, где
        // прямоугольник заливки закрывает фон ячейки целиком.
        if (def.Key == TableColumnDef.NumKey)
        {
            ctx.FillRectangle(_inactiveBg, inner);
            DrawText(ctx, row.IsAddRow ? "+" : row.RowNumber.ToString(), inner, _secondary, "s", TextAlignment.Center, 1, _lineHeight);
            return;
        }
        if (def.Key == TableColumnDef.AddPropKey)
        {
            ctx.FillRectangle(_inactiveBg, inner);
            return;
        }

        // Наведение подсвечивает ячейку (DataGridCell:pointerover), цвет ячейки ложится поверх полупрозрачно.
        if (hovered) ctx.FillRectangle(_hoverBg, inner);
        if (row.IsAddRow) return;

        // Выделенная колонка — заливка ячеек значений (фон ContentPresenter'а ячейки у нынешней
        // таблицы): до вертикальной линии, поверх наведения, под полупрозрачным цветом ячейки.
        if (IsColumnSelected(def.Key)) ctx.FillRectangle(_accentLight, inner);

        // Цвет: ячейка → строка → колонка; заливка полупрозрачная, текст — насыщенный.
        var style = ResolveStyle(row, def.Key, rowStyle);
        if (style.Bg is not null && ColorTools.Fill(style.Bg) is { } fill)
            ctx.FillRectangle(fill, inner);
        var fg   = style.Fg is not null ? ColorTools.Text(style.Fg) : null;
        var fgId = style.Fg ?? "";

        if (row.IsLoading)
        {
            if (def.Key == TableRowVm.TitleKey)
                DrawText(ctx, row[TableRowVm.TitleKey], inner, _primary, "p", TextAlignment.Left, 1, _lineHeight);
            return;
        }

        switch (def.FieldType)
        {
            case "checkbox":
                DrawCheckbox(ctx, inner, IsTruthy(row[def.Key]), scale);
                return;

            case "relation":
            {
                // Названия целей через разделитель поля; пусто — серое «связи нет» (RelationSelectBox).
                var text = RelationText(row, def);
                if (text.Length == 0) DrawText(ctx, "связи нет", inner, _secondary, "s", TextAlignment.Left, 1, _lineHeight);
                else DrawText(ctx, text, inner, fg ?? _primary, fg is null ? "p" : fgId, TextAlignment.Left, 1, _lineHeight);
                return;
            }

            case "file":
                DrawFile(ctx, row, def, inner, fg, fgId, scale);
                return;
        }

        if (FieldTypeInfo.IsComputed(def.FieldType))
        {
            var raw = def.FieldType switch
            {
                "created_at" => row.CreatedAt,
                "updated_at" => row.UpdatedAt,
                "created_by" => row.CreatedBy,
                "updated_by" => row.UpdatedBy,
                _            => row.CreatedAt,
            };
            DrawText(ctx, FieldTypeInfo.FormatComputedDisplay(raw, def.FieldType), inner,
                     fg ?? _secondary, fg is null ? "s" : fgId, TextAlignment.Left, 1, _lineHeight);
            return;
        }

        // Столько строк, сколько влезает по высоте ячейки (FitLinesToCell) — только явные переводы
        // строки, переноса по словам у ячеек нет.
        var lines = Math.Max(1, (int)(inner.Height / _lineHeight));

        if (def.Key == TableRowVm.TitleKey)
        {
            var title = row[TableRowVm.TitleKey];
            if (string.IsNullOrWhiteSpace(title)) title = TitleText.Placeholder;
            var muted = title == TitleText.Placeholder;
            DrawText(ctx, title, inner, fg ?? (muted ? _secondary : _primary), fg is null ? (muted ? "s" : "p") : fgId,
                     TextAlignment.Left, lines, _lineHeight);
            return;
        }

        var display = CellDisplayText.Of(def.FieldType, row[def.Key], def.NumberFormat, def.Separator);
        if (display.Length == 0) return;

        if (def.FieldType == "number")
        {
            DrawText(ctx, display, inner, fg ?? _primary, fg is null ? "p" : fgId, TextAlignment.Right, 1, _lineHeight);
            return;
        }
        DrawText(ctx, display, inner, fg ?? _primary, fg is null ? "p" : fgId, TextAlignment.Left, lines, _lineHeight);
    }

    // «Файл» как у FileEditor в плоском режиме: имя ссылкой (акцент, подчеркнуто), когда файл на
    // месте; «имя — файл не найден» серым, когда его нет; пусто — серое «Выбрать файл…»; у
    // авто-привязки — «папка недоступна» / имя / «файл не найден». Справа — кнопка-скрепка
    // (Button.timeArrow: отступы 4 и 8, иконка 16). Есть ли файл — из кеша FileProbe, диск с
    // UI-потока не спрашивается.
    private void DrawFile(DrawingContext ctx, TableRowVm row, TableColumnDef def, Rect inner, IBrush? fg, string fgId, double scale)
    {
        var path = row[def.Key];
        var name = FileValue.DisplayName(path);
        var auto = def.FileAutoFolder.Length > 0;

        string text;
        IBrush brush;
        string brushId;
        var underline = false;
        if (auto && FileProbe.FolderExists(def.FileAutoFolder) == false)
        {
            text = "папка недоступна"; brush = _secondary; brushId = "s";
        }
        else if (path.Length == 0)
        {
            text = auto ? "файл не найден" : "Выбрать файл…"; brush = _secondary; brushId = "s";
        }
        else if (FileProbe.FileExists(path) != false)
        {
            text = name; brush = fg ?? _accent; brushId = fg is null ? "a" : fgId; underline = true;
        }
        else
        {
            text = name + " — файл не найден"; brush = _secondary; brushId = "s";
        }

        var clipW    = ClipPadLeft + IconSize + ClipPadRight;
        var textRect = new Rect(inner.X, inner.Y, Math.Max(1, inner.Width - clipW), inner.Height);
        DrawText(ctx, text, textRect, brush, brushId, TextAlignment.Left, 1, _lineHeight, underline: underline);
        if (_paperclip is not null)
            DrawIcon(ctx, _paperclip, inner.Right - ClipPadRight - IconSize, Snap(inner.Y + (inner.Height - IconSize) / 2, scale), _secondary);
    }

    private RowStyle ResolveStyle(TableRowVm row, string key, RowStyle rowStyle)
    {
        var column = _columnStyle(key);
        var cell   = row.CellStyle(key);
        if (rowStyle.IsEmpty && column.IsEmpty && cell.IsEmpty) return RowStyle.None;
        return RowStyle.Resolve(cell, rowStyle, column);
    }

    private string RelationText(TableRowVm row, TableColumnDef def)
    {
        if (_relLinks is null || def.Relation is null || def.Tag is not int fid) return "";
        if (!_relLinks.TryGet(fid, row.RecordId, out var links) || links.Count == 0) return "";
        // Название цели — первой строкой (TitleText): закрытый вид — одна строка.
        return string.Join(def.Relation.SeparatorString, links.Select(l => TitleText.FirstLine(l.Title)));
    }

    private static bool IsTruthy(string value) => value is "1" or "true" or "True";

    // Надпись в области ячейки: отступ pad с обеих сторон, по вертикали по центру на целом пикселе
    // (как TextBlock с VerticalAlignment=Center при округлении раскладки). lineHeight NaN —
    // естественная высота строки (заголовки), иначе целый токен LineHeightBase (ячейки).
    private void DrawText(DrawingContext ctx, string text, Rect area, IBrush brush, string brushId,
                          TextAlignment align, int maxLines, double lineHeight, double pad = CellPad, bool underline = false)
    {
        if (string.IsNullOrEmpty(text) || _text is null || DebugSkipText) return;

        var maxW   = Math.Max(1, area.Width - pad * 2);
        var scale  = _frameScale;   // масштаб кадра, посчитан один раз в Render
        // Центрованная надпись (номер строки, «№» и «+» шапки) — как TextBlock с
        // HorizontalAlignment=Center: раскладка естественной ширины, а ее левый край ложится на
        // целый пиксель (округление раскладки), а не на половинку от выравнивания внутри полосы.
        var layout = _text.Get(text, maxW, align == TextAlignment.Center ? TextAlignment.Left : align, brush, brushId,
                               maxLines, lineHeight, underline ? Underline : null);
        // Ширина надписи — с правым свесом последнего глифа и округленная вверх до пикселя, как
        // DesiredSize у TextBlock при округлении раскладки.
        var textW  = Math.Min(Math.Ceiling(layout.WidthIncludingTrailingWhitespace * scale) / scale, maxW);
        var x      = align == TextAlignment.Center
            ? Snap(area.X + (area.Width - textW) / 2, scale)
            : area.X + pad;
        var y      = Snap(area.Y + Math.Max(0, (area.Height - layout.Height) / 2), scale);

        if (layout.Height > area.Height || layout.Width > maxW + 0.5)
        {
            using (ctx.PushClip(area))
                layout.Draw(ctx, new Point(x, y));
            return;
        }
        layout.Draw(ctx, new Point(x, y));
    }

    // Галочка — как единый CheckBox приложения (AppCheckBox): квадрат 16×16, радиус 2, рамка в
    // толщину линии цветом текста; отмеченная — заливка и рамка акцентом, белый глиф 12×12 внутри.
    // По горизонтали центрируется не сам квадрат, а контрол шириной 16 + 8 (у пустой подписи
    // остается отступ Padding=8) — квадрат стоит на 4 px левее центра ячейки, как в нынешней таблице.
    private const double CheckControlWidth = CheckSize + 8;

    private void DrawCheckbox(DrawingContext ctx, Rect inner, bool isChecked, double scale)
    {
        var x   = Snap(inner.X + (inner.Width  - CheckControlWidth) / 2, scale);
        var y   = Snap(inner.Y + (inner.Height - CheckSize) / 2, scale);
        var box = new RoundedRect(new Rect(x, y, CheckSize, CheckSize), 2);
        if (isChecked)
        {
            ctx.DrawRectangle(_accent, null, box);
            if (_check is not null)
                DrawIcon(ctx, _check, x + (CheckSize - CheckGlyph) / 2, y + (CheckSize - CheckGlyph) / 2, Brushes.White, CheckGlyph, 2.5);
        }
        else
        {
            var half = _lineWidth / 2;
            ctx.DrawRectangle(null, _checkBorder, new RoundedRect(new Rect(x + half, y + half, CheckSize - _lineWidth, CheckSize - _lineWidth), 2));
        }
    }

    private Geometry? TypeIcon(string? fieldType)
    {
        var key = fieldType ?? "";
        if (!_typeIcons.TryGetValue(key, out var geo))
        {
            geo = FieldTypeIcons.ForType(fieldType);
            _typeIcons[key] = geo;
        }
        return geo;
    }

    // Иконки словаря Icons.axaml нарисованы в поле 24×24; масштабируем в size и обводим той же
    // толщиной, что Viewbox нынешней шапки (IconStrokeSmall в координатах поля).
    private void DrawIcon(DrawingContext ctx, Geometry geometry, double x, double y, IBrush stroke,
                          double size = IconSize, double? thickness = null)
    {
        var s   = size / 24.0;
        var pen = thickness is null ? IconPen(stroke) : new Pen(stroke, thickness.Value, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(x, y)))
            ctx.DrawGeometry(null, pen, geometry);
    }

    private Pen IconPen(IBrush stroke)
    {
        if (!_iconPens.TryGetValue(stroke, out var pen))
        {
            pen = new Pen(stroke, _iconStroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            _iconPens[stroke] = pen;
        }
        return pen;
    }
}
