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

namespace SmartBase.Controls.DrawnGrid;

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
/// акцентом у диапазона больше одной ячейки; выделенные записи и колонки
/// (<see cref="IsRecordSelected"/>, <see cref="IsColumnSelected"/>) — заливка
/// <c>BrushAccentLight</c> строки и ячеек колонки, как у нынешней таблицы. Рамку рисует
/// <see cref="SelectionOverlay"/> поверх редактора ячейки (<see cref="RenderSelectionFrame"/>): у
/// нынешней таблицы она тоже лежит над правкой. Отложенного пересчета и защиты от петли раскладки
/// нет — рамка стоит по тем же границам, что линии сетки, по построению.
///
/// Правка (этап 4): редактор — настоящий контрол в <see cref="EditorHost"/> над поверхностью, в
/// геометрии <see cref="EditorRect"/>; правящуюся ячейку (<see cref="SetEditingCell"/>) поверхность
/// рисует без значения и цвета — их показывает редактор, как шаблон правки нынешней таблицы.
/// </summary>
public sealed class GridSurface : Control, IEditorSurface
{
    /// <summary>Отступ содержимого ячейки от ее границ — как <c>CellContentPad</c> нынешней таблицы.</summary>
    public const double CellPad = 6;

    /// <summary>Половина ширины полоски-захвата (сама полоска 8 px, как у нынешних слоев ресайза).</summary>
    public const double GripHalf = 4;

    private const double IconSize      = SurfaceStyle.IconSize;
    private const double ChevronButton = 20;   // кнопка фильтра в шапке 20×20
    private const double CheckSize     = SurfaceStyle.CheckSize;
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

    // Правящаяся ячейка (строка среза, ключ колонки): значение показывает редактор над ней.
    private (int Row, string Key)? _editing;

    // ── Токены вида ───────────────────────────────────────────────────────────
    // Кисти, перья, шрифт, иконки и кеш раскладок — общий стиль рисованных поверхностей
    // (SurfaceStyle); перечитывается на смену зума, DPI, шрифта, высоты строк и темы.
    private bool         _tokensReady;
    private SurfaceStyle _s = SurfaceStyle.Fallback();
    private bool         _choiceInside;   // указатель внутри рамки ячейки-списка (рамка красится акцентом)

    private double _frameScale = 1;   // RenderScaling × зум на текущий кадр (снап на физический пиксель)

    // ── Замеры (стенды) ───────────────────────────────────────────────────────
    public TimeSpan LastRender     { get; private set; }
    public int      LastCellsDrawn { get; private set; }
    public int      TextCacheCount => _s.Text.Count;
    public int      TextCacheHits  => _s.Text.Hits;
    public int      TextCacheMisses => _s.Text.Misses;

    /// <summary>Отладочные выключатели для стендов: без текста / без ячеек вовсе (только линии и
    /// шапка). Ищут, какая часть кадра сколько стоит; в программе всегда false.</summary>
    public static bool DebugSkipText  { get; set; }
    public static bool DebugSkipCells { get; set; }

    // ── События ───────────────────────────────────────────────────────────────
    public event EventHandler? OffsetChanged;
    public event EventHandler? ExtentChanged;
    /// <summary>Сменилось что-то, от чего зависят положение ячеек и рамки: прокрутка, раскладки,
    /// размер, токены, рамка выделения. Слушают слой рамки и редактор ячейки.</summary>
    public event EventHandler? ViewChanged;
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

    // ── Служебные таблицы (ServiceGrid): те же отрисовщики, другие правила строки ─────────────
    // Все по умолчанию выключено — кадр таблицы коллекции не меняется.

    /// <summary>Наведение красит всю строку, а не ячейку (как строка прежних служебных таблиц);
    /// выделенная строка под указателем остается выделенной.</summary>
    public bool HoverWholeRow { get; set; }

    /// <summary>Токен высоты строки: у таблицы коллекции — высота из «Плотности строк», у служебных
    /// таблиц — RowHeightStandard, плотность конструктора их не касается.</summary>
    public string RowHeightResourceKey { get; set; } = ConstructorSettings.RowHeightResourceKey;

    /// <summary>Отступ текста ячейки от границ: 6 у таблицы коллекции, 8 у служебных таблиц.</summary>
    public double CellTextPad { get; set; } = CellPad;

    /// <summary>Сколько первых колонок закреплено: стоят на месте при прокрутке вбок, прокручиваемые
    /// рисуются правее них.</summary>
    public int FrozenColumns { get; set; }

    /// <summary>Галочка ровно по центру ячейки (CheckBox без отступа под подпись), а не со сдвигом на
    /// половину отступа, как у галочки таблицы коллекции.</summary>
    public bool CheckboxCentered { get; set; }

    /// <summary>Наведение на заголовок красит его фон (таблица коллекции); у служебных таблиц
    /// красится только кнопка фильтра.</summary>
    public bool HeaderHoverFill { get; set; } = true;

    /// <summary>Рисовать ли правую линию последней колонки в теле таблицы. Прежние служебные таблицы
    /// ее не рисовали: край и так очерчен краем таблицы или полосой.</summary>
    public bool LastColumnLine { get; set; } = true;

    /// <summary>Тянуть ли вертикальные линии ниже последней строки. Под последней строкой (у таблицы
    /// коллекции — строкой «+») поле чистое и у таблицы коллекции, и у служебных таблиц, как у
    /// прежних таблиц на контролах.</summary>
    public bool LinesBelowRows { get; set; }

    /// <summary>Ширина закрепленных колонок — полоса прокрутки считает окно без них.</summary>
    public double FrozenColumnsWidth => FrozenWidth;

    /// <summary>Обрезать надписи по границам их прямоугольника (как надпись с многоточием прежних
    /// служебных таблиц: выступ первой буквы за левый край не рисуется). У таблицы коллекции обрезка
    /// только при переполнении.</summary>
    public bool ClipText { get; set; }

    /// <summary>Перечитать токены после смены опций (ключа высоты строки).</summary>
    public void RefreshTokens() => OnMetricsChanged();

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
        RaiseViewChanged();
    }

    /// <summary>Снять рамку выделения ячеек.</summary>
    public void ClearCellSelection()
    {
        if (_cellSel is null) return;
        _cellSel = null;
        RaiseViewChanged();
    }

    /// <summary>Ячейка правится: ее значение и цвет показывает редактор над поверхностью, поверхность
    /// рисует только фон с наведением — как у нынешней таблицы, где шаблон правки подменяет показ.</summary>
    public void SetEditingCell(int row, string key)
    {
        if (_editing == (row, key)) return;
        _editing = (row, key);
        InvalidateVisual();
    }

    public void ClearEditingCell()
    {
        if (_editing is null) return;
        _editing = null;
        InvalidateVisual();
    }

    /// <summary>Где стоит редактор ячейки: ее область содержимого (без вертикальной линии справа и
    /// линии под строкой — как содержимое <c>DataGridCell</c>) при текущей прокрутке; null — колонки
    /// нет в раскладке или строки нет в срезе.</summary>
    public Rect? EditorRect(int row, string key)
    {
        EnsureTokens();
        var c = _columns.IndexOf(key);
        if (c < 0 || row < 0 || row >= RowCount) return null;
        return ContentRect(CellRect(row, c), c);
    }

    /// <summary>Область редактора — тело таблицы под шапкой (<see cref="IEditorSurface"/>).</summary>
    public Rect EditorClip => new(0, HeaderHeight, ViewportWidth, ViewportHeight);

    /// <summary>
    /// Куда раскрыть поле ввода под длинный текст (<see cref="IEditorSurface.EditorGrowRect"/>): от
    /// ячейки вправо целыми колонками и вниз целыми строками, пока не хватит места, но не дальше
    /// видимой области — раскрытое поле не уезжает за край окна. Правее последней колонки и под
    /// последней строкой поле растет по пустому месту (вниз — шагом стандартной строки).
    /// </summary>
    public Rect? EditorGrowRect(int row, string key, Size need)
    {
        EnsureTokens();
        var c = _columns.IndexOf(key);
        if (c < 0 || row < 0 || row >= RowCount) return null;
        var cell = CellRect(row, c);
        var t    = _s.LineWidth;

        var maxRight = Math.Max(cell.Right, ViewportWidth);
        var right    = cell.Right;
        for (var k = c + 1; right - cell.X - t < need.Width && k < _columns.Count && right < maxRight; k++)
            right += _columns.Width(k);
        // Колонки кончились раньше окна — дальше пустое место справа от таблицы, поле идет по нему.
        if (right - cell.X - t < need.Width) right = cell.X + need.Width + t;
        right = Math.Min(right, maxRight);

        var maxBottom = Math.Max(cell.Bottom, HeaderHeight + ViewportHeight);
        var bottom    = cell.Bottom;
        for (var k = row + 1; bottom - cell.Y - t < need.Height && bottom < maxBottom; k++)
            bottom += k < RowCount ? _rowLayout.Height(k) : RowPitch;
        bottom = Math.Min(bottom, maxBottom);

        return ContentRect(new Rect(cell.X, cell.Y, right - cell.X, bottom - cell.Y), c);
    }

    /// <summary>Подложка раскрытого поля — фон тела таблицы.</summary>
    public IBrush? EditorGrowBackground { get { EnsureTokens(); return _s.Bg; } }

    // Раскрытое поле правки: рамка выделения обводит его целиком, а не одну ячейку под ним.
    private Rect? _editorOverflow;

    /// <summary>Рамка выделения сменила место без смены выделения (раскрылось или свернулось поле
    /// правки) — слою рамки пора перерисоваться.</summary>
    public event EventHandler? FrameChanged;

    public void SetEditorOverflow(Rect? area)
    {
        if (_editorOverflow == area) return;
        _editorOverflow = area;
        FrameChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Цвет правящейся ячейки (ячейка → строка → колонка). Поверхность рисует ее без значения
    /// и цвета, а цвет кладет под редактор держатель правки — на всю ячейку: редактор текста,
    /// прижатого к верху или низу высокой строки, занимает лишь полосу у края. null — без цвета.</summary>
    public IBrush? EditorFill(int row, string key)
    {
        if (RowAt(row) is not { IsAddRow: false, IsLoading: false } vm) return null;
        var style = ResolveStyle(vm, key, _rowStyle(vm.RecordId));
        return style.Bg is not null ? ColorTools.Fill(style.Bg) : null;
    }

    /// <summary>
    /// Отступы редактора внутри области ячейки: у текста по центру их нет, у текста сверху и снизу
    /// редактор стоит в полосе у края (<see cref="CellAlign.EditorInset"/>) и центрует в ней свое
    /// содержимое — оно встает ровно туда, где нарисован текст показа, вместе с кнопкой редактора.
    /// <paramref name="lines"/> — сколько строк текста в редакторе сейчас (у поля ввода растет с
    /// набором переводов строки); больше, чем помещается в ячейку, не бывает.
    /// </summary>
    public Thickness EditorInset(int row, string key, int lines)
    {
        EnsureTokens();
        var c = _columns.IndexOf(key);
        if (c < 0 || RowAt(row) is not { IsAddRow: false, IsLoading: false } vm) return default;
        if (!CellAlign.Applies(_columns[c].FieldType)) return default;

        var valign = ResolveStyle(vm, key, _rowStyle(vm.RecordId)).VAlign ?? CellVAlign.Center;
        if (valign == CellVAlign.Center) return default;

        var area  = ContentRect(CellRect(row, c), c);
        var textH = Math.Min(Math.Max(1, lines), FitLines(area.Height)) * _s.LineHeight;
        var (top, bottom) = CellAlign.EditorInset(valign, area.Height, textH, _s.TextEdge);
        return new Thickness(0, top, 0, bottom);
    }

    /// <summary>Квадрат галочки в ячейке (координаты поверхности) — по нему щелкают, как по квадрату
    /// <c>CheckBox</c> нынешней таблицы.</summary>
    public Rect CheckboxRect(Rect cell)
    {
        EnsureTokens();
        return CheckboxBox(ContentRect(cell), LineMetrics.EffectiveScale(this));
    }

    /// <summary>То же с учетом колонки (у последней без линии в теле квадрат считается от всей ячейки).</summary>
    public Rect CheckboxRect(Rect cell, int column)
    {
        EnsureTokens();
        return CheckboxBox(ContentRect(cell, column), LineMetrics.EffectiveScale(this));
    }

    /// <summary>Кнопка-скрепка ячейки «Файла» (координаты поверхности); у авто-привязки кнопки нет.</summary>
    public Rect? FileButtonRect(Rect cell, TableColumnDef def)
    {
        EnsureTokens();
        if (def.FileAutoFolder.Length > 0) return null;
        var inner = ContentRect(cell);
        var w = Math.Min(inner.Width, ClipPadLeft + IconSize + ClipPadRight);
        return new Rect(inner.Right - w, inner.Y, w, inner.Height);
    }

    /// <summary>Файл под видимым именем. null — пустое место, разделитель или многоточие;
    /// пустая строка — плашка недоступной папки авто-привязки.</summary>
    public string? FilePathAt(GridHit hit, Point point)
    {
        EnsureTokens();
        if (!IsFileCell(hit) || !hit.Bounds.Contains(point)) return null;
        var def = hit.Def!;
        if (FileButtonRect(hit.Bounds, def) is { } button && button.Contains(point)) return null;
        if (def.FileAutoFolder.Length > 0 && FileProbe.FolderExists(def.FileAutoFolder) == false) return "";
        var row = _rows![hit.Row];
        var style = ResolveStyle(row, def.Key, _rowStyle(row.RecordId));
        var fg = style.Fg is not null ? ColorTools.Text(style.Fg) : null;
        var inner = ContentRect(hit.Bounds, hit.Column);
        var clipWidth = def.FileAutoFolder.Length > 0 ? 0 : ClipPadLeft + IconSize + ClipPadRight;
        var area = new Rect(inner.X, inner.Y, Math.Max(1, inner.Width - clipWidth), inner.Height);
        return _s.FileLinks(row[def.Key], def.FileAutoFolder, area,
                            ToTextAlignment(style.HAlign ?? CellAlign.DefaultH(def.FieldType)),
                            style.VAlign ?? CellVAlign.Center, CellTextPad, LineMetrics.EffectiveScale(this),
                            fg: fg, fgId: style.Fg ?? "").PathAt(point);
    }

    // Область содержимого ячейки: без вертикальной линии справа и линии под строкой.
    private Rect ContentRect(Rect cell) =>
        new(cell.X, cell.Y, Math.Max(0, cell.Width - _s.LineWidth), Math.Max(0, cell.Height - _s.LineWidth));

    // То же для колонки: у последней колонки без линии в теле (служебные таблицы) содержимое идет
    // до самого края ячейки, как в ячейке прежней таблицы со спрятанной линией.
    private bool CellHasLine(int column) => LastColumnLine || column < _columns.Count - 1;

    private Rect ContentRect(Rect cell, int column) =>
        CellHasLine(column) ? ContentRect(cell) : new Rect(cell.X, cell.Y, cell.Width, Math.Max(0, cell.Height - _s.LineWidth));

    /// <summary>Указатель над редактором ячейки (он лежит над поверхностью и забирает события): наведение
    /// ведется так же, как над самой поверхностью, — подсветка ячейки видна сквозь редактор.
    /// null — указатель ушел.</summary>
    public void HoverAt(Point? p)
    {
        _lastPointer = p;
        if (_drag != Drag.None) return;
        SetHover(p is { } q ? HitTest(q) : GridHit.None);
    }

    /// <summary>Колесо над редактором ячейки — таблица прокручивается, как под ним.</summary>
    public void ScrollByWheel(PointerWheelEventArgs e)
    {
        var dy = -e.Delta.Y * RowPitch * 3;
        var dx = -e.Delta.X * 48;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && dx == 0) { dx = dy; dy = 0; }
        SetOffsets(_offsetX + dx, _offsetY + dy);
        e.Handled = true;
    }

    private void RaiseViewChanged()
    {
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
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
        var x = ScreenLeft(c0);
        var y = RowTop(sel.Top);
        // Ячейка — до вертикальной линии включительно и без линии под строкой: так же стояли
        // границы DataGridCell, по которым рисовалась рамка нынешней таблицы.
        return new Rect(x, y, _columns.Right(c1) - _columns.Left(c0),
                        Math.Max(0, _rowLayout.Bottom(sel.Bottom) - _rowLayout.Top(sel.Top) - _s.LineWidth));
    }

    /// <summary>Высота строки из токена «Оформления» (без линии).</summary>
    public double RowHeight    { get { EnsureTokens(); return _s.RowHeight; } }

    /// <summary>Шаг строк: высота плюс линия под ней — так же, как у нынешней сетки, где линия
    /// PART_BottomGridLine добавляется к высоте строки (36 + 1 = 37 при стандартных токенах).</summary>
    public double RowPitch     { get { EnsureTokens(); return _s.RowHeight + _s.LineWidth; } }

    /// <summary>Высота шапки: высота строки, линия под шапкой лежит внутри нее (как
    /// PART_ColumnHeadersAndRowsSeparator, прижатый к низу строки заголовков).</summary>
    public double HeaderHeight => RowHeight;

    /// <summary>Толщина линий сетки (токен SeparatorThickness).</summary>
    public double LineWidth    { get { EnsureTokens(); return _s.LineWidth; } }

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
        RaiseViewChanged();
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
        RaiseViewChanged();
    }

    /// <summary>Сменить раскладку колонок (ресайз окна, скрытие, сдвиг, ширины) — строки не трогаются.</summary>
    public void SetColumns(ColumnLayout columns)
    {
        _columns = columns;
        _lastColumnWindow = (-1, -1);
        ClampOffsets();
        ExtentChanged?.Invoke(this, EventArgs.Empty);
        UpdateWindow();
        RaiseViewChanged();
    }

    /// <summary>Сменить раскладку строк (ручные высоты, смена высоты строк «Оформления»).</summary>
    public void SetRowLayout(RowLayout layout)
    {
        _rowLayout = layout;
        ClampOffsets();
        ExtentChanged?.Invoke(this, EventArgs.Empty);
        UpdateWindow();
        RaiseViewChanged();
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
        new(ScreenLeft(column), RowTop(row), _columns.Width(column), _rowLayout.Height(row));

    // ── Попадание ─────────────────────────────────────────────────────────────

    /// <summary>Что лежит под точкой поверхности. Полоски-захваты отвечают только в ручных режимах.</summary>
    public GridHit HitTest(Point p)
    {
        EnsureTokens();
        if (_rows is null || _columns.Count == 0) return GridHit.None;
        var headH = HeaderHeight;
        var t     = _s.LineWidth;

        var c = ColumnAtScreenX(p.X);
        if (c < 0) return GridHit.None;

        if (p.Y < headH)
        {
            // Полоска ширины: правая граница колонки c или левая граница (= правая у c − 1).
            if (ColumnGripsEnabled)
            {
                if (_columns[c].IsStarWidth && Math.Abs(p.X - ScreenRight(c)) <= GripHalf)
                    return new GridHit(GridHitKind.ColumnGrip, -1, c, _columns[c], HeaderRect(c));
                if (c > 0 && _columns[c - 1].IsStarWidth && Math.Abs(p.X - ScreenLeft(c)) <= GripHalf)
                    return new GridHit(GridHitKind.ColumnGrip, -1, c - 1, _columns[c - 1], HeaderRect(c - 1));
            }
            var def  = _columns[c];
            var rect = HeaderRect(c);
            if (def.Key == TableColumnDef.AddPropKey) return new GridHit(GridHitKind.HeaderAdd, -1, c, def, rect);
            if (def.Key == TableColumnDef.NumKey)     return new GridHit(GridHitKind.Header, -1, c, def, rect);
            var btn = ChevronRect(rect, def.HeaderRightLine ? t : 0);
            if (def.Filterable && btn.Contains(p)) return new GridHit(GridHitKind.HeaderFilter, -1, c, def, btn);
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

    private Rect HeaderRect(int c) => new(ScreenLeft(c), 0, _columns.Width(c), HeaderHeight);

    // Закрепленные колонки стоят на месте, прокручиваемые сдвинуты на смещение. Экранная координата
    // X → колонка: сначала закрепленные, потом прокручиваемые; уехавшая под закрепленные не считается.
    private double FrozenWidth =>
        FrozenColumns > 0 && _columns.Count > 0 ? _columns.Left(Math.Min(FrozenColumns, _columns.Count)) : 0;

    private double ScreenLeft(int c)  => c < FrozenColumns ? _columns.Left(c)  : _columns.Left(c)  - _offsetX;
    private double ScreenRight(int c) => c < FrozenColumns ? _columns.Right(c) : _columns.Right(c) - _offsetX;

    private int ColumnAtScreenX(double px)
    {
        if (px < FrozenWidth) return _columns.IndexAt(px);
        var c = _columns.IndexAt(px + _offsetX);
        return c >= FrozenColumns ? c : -1;
    }

    // Прокручиваемые колонки в окне — правее закрепленных.
    private (int First, int Last) ScrollingRange(double w)
    {
        var frozenW = FrozenWidth;
        var (c0, c1) = _columns.VisibleRange(_offsetX + frozenW, w - frozenW);
        if (c0 < FrozenColumns) c0 = FrozenColumns;
        return (c0, c1);
    }

    /// <summary>Кнопка фильтра в заголовке колонки (координаты поверхности при текущей прокрутке);
    /// null — колонки нет среди показанных или это «№» и «+».</summary>
    public Rect? FilterButtonRect(string key)
    {
        EnsureTokens();
        var c = _columns.IndexOf(key);
        if (c < 0 || key is TableColumnDef.NumKey or TableColumnDef.AddPropKey || !_columns[c].Filterable) return null;
        return ChevronRect(HeaderRect(c), _columns[c].HeaderRightLine ? _s.LineWidth : 0);
    }

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
        RaiseViewChanged();
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
        ScrollByWheel(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ClampOffsets();
        ExtentChanged?.Invoke(this, EventArgs.Empty);
        UpdateWindow();
        RaiseViewChanged();
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
        var before = (_s.RowHeight, _s.LineWidth, _s.FontSize, _s.LineHeight, _s.Typeface);
        _tokensReady = false;
        EnsureTokens();
        RaiseViewChanged();
        if (before != (_s.RowHeight, _s.LineWidth, _s.FontSize, _s.LineHeight, _s.Typeface))
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
        if (same)
        {
            // В ячейке «Файла» курсор и подсказка разные над именем и над скрепкой — внутри одной ячейки;
            // у ячейки-кнопки рука только над квадратом кнопки.
            if (IsFileCell(hit) || IsButtonCell(hit)) { _hover = hit; ApplyPointerLook(hit); }
            // Рамка ячейки-списка красится акцентом, пока указатель внутри нее.
            if (IsChoiceCell(hit))
            {
                var inside = _lastPointer is { } lp && ChoiceBox(ContentRect(hit.Bounds, hit.Column)).Contains(lp);
                if (inside != _choiceInside) { _choiceInside = inside; InvalidateVisual(); }
            }
            return;
        }

        // Перерисовывается только то, что меняет вид: ячейка, заголовок и его кнопка.
        var visual = HoverVisible(hit) || HoverVisible(_hover);
        _hover = hit;
        _choiceInside = IsChoiceCell(hit) && _lastPointer is { } cp && ChoiceBox(ContentRect(hit.Bounds, hit.Column)).Contains(cp);
        ApplyPointerLook(hit);
        if (visual) InvalidateVisual();
    }

    private bool IsButtonCell(GridHit hit) => hit.Kind == GridHitKind.Cell && hit.Def is { FieldType: "button" };
    private bool IsChoiceCell(GridHit hit) => hit.Kind == GridHitKind.Cell && hit.Def is { FieldType: "choice" };

    private void ApplyPointerLook(GridHit hit)
    {
        var overClip   = IsFileCell(hit) && _lastPointer is { } p && FileButtonRect(hit.Bounds, hit.Def!) is { } clip && clip.Contains(p);
        var overButton = IsButtonCell(hit) && _lastPointer is { } bp && ButtonBox(ContentRect(hit.Bounds, hit.Column)).Contains(bp) && CellEnabled(hit);
        Cursor = hit.Kind switch
        {
            GridHitKind.ColumnGrip   => ColResizeCursor,
            GridHitKind.RowGrip      => RowResizeCursor,
            GridHitKind.HeaderAdd    => HandCursor,
            GridHitKind.HeaderFilter => HandCursor,   // Button.icon несет курсор-руку
            GridHitKind.Cell when overClip || overButton || FileLinkClickable(hit) => HandCursor,
            _                        => null,
        };
        ToolTip.SetTip(this, hit.Kind == GridHitKind.HeaderFilter ? hit.Def?.FilterTip ?? "Фильтр и сортировка"
                             : hit.Kind == GridHitKind.Header ? hit.Def?.HeaderTip
                             : overClip ? "Выбрать файл"
                             : hit.Kind == GridHitKind.Cell && hit.Def?.CellTip is { } tipOf && RowAt(hit.Row) is { } tipRow ? tipOf(tipRow)
                             : null);
    }

    private bool IsFileCell(GridHit hit) =>
        hit.Kind == GridHitKind.Cell && hit.Def is { FieldType: "file" } && _rows is not null
        && hit.Row >= 0 && hit.Row < _rows.Count && !_rows[hit.Row].IsLoading && !_rows[hit.Row].IsAddRow;

    // Имя файла кликабельно (рука), как LinkHost у FileEditor: путь есть — открыть или поправить; у
    // авто-привязки — недоступная папка (плашка) или найденный файл; пустое «Выбрать файл…» — нет.
    private bool FileLinkClickable(GridHit hit)
    {
        return _lastPointer is { } point && FilePathAt(hit, point) is not null;
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

    private void EnsureTokens()
    {
        if (_tokensReady) return;
        _tokensReady = true;
        _s = SurfaceStyle.Load(this, RowHeightResourceKey);
    }

    // ── Рисование ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        var sw = Stopwatch.StartNew();
        EnsureTokens();

        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.FillRectangle(_s.Bg, new Rect(0, 0, w, h));

        var cells = 0;
        _visibleNow.Clear();

        if (_rows is not null && _columns.Count > 0 && w > 0 && h > 0)
        {
            var scale = _frameScale = LineMetrics.EffectiveScale(this);
            var t     = _s.LineWidth;
            var headH = HeaderHeight;

            var (c0, c1) = ScrollingRange(w);
            var (r0, r1) = VisibleRows();
            var frozen   = Math.Min(FrozenColumns, _columns.Count);
            // Прокручиваемые колонки рисуются в области правее закрепленных, закрепленные — поверх.
            var scrollArea = new Rect(FrozenWidth, 0, Math.Max(0, w - FrozenWidth), h);

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
                    var editKey  = _editing is { } ed && ed.Row == r ? ed.Key : null;

                    // Выделенная запись — заливка всей строки под ячейками (BackgroundRectangle строки
                    // нынешней таблицы); «№» и «+» закроют ее своей серой заливкой.
                    var rowSelected = !row.IsAddRow && row.RecordId > 0 && IsRecordSelected(row.RecordId);
                    if (rowSelected)
                        ctx.FillRectangle(_s.AccentLight, new Rect(0, y, w, Math.Max(0, pitch - t)));
                    // Служебные таблицы: наведение красит всю строку, выделенная остается голубой и под
                    // указателем; ячейки тогда наведение не красят.
                    if (HoverWholeRow)
                    {
                        if (hoverCol >= 0 && !rowSelected)
                            ctx.FillRectangle(_s.HoverBg, new Rect(0, y, w, Math.Max(0, pitch - t)));
                        hoverCol = -1;
                    }

                    var tc = GridTimings.Start();
                    using (ctx.PushClip(scrollArea))
                        for (var c = c0; c <= c1; c++)
                        {
                            var cell = new Rect(ScreenLeft(c), y, _columns.Width(c), pitch);
                            DrawCell(ctx, row, _columns[c], cell, rowStyle, hoverCol == c, _columns[c].Key == editKey, scale, CellHasLine(c));
                            cells++;
                        }
                    for (var c = 0; c < frozen; c++)
                    {
                        var cell = new Rect(ScreenLeft(c), y, _columns.Width(c), pitch);
                        DrawCell(ctx, row, _columns[c], cell, rowStyle, hoverCol == c, _columns[c].Key == editKey, scale, CellHasLine(c));
                        cells++;
                    }
                    if (tc != 0) tRows += Stopwatch.GetElapsedTime(tc).TotalMilliseconds;

                    // Нижняя линия строки — внутри строки, как PART_BottomGridLine.
                    var ly = Snap(y + pitch - t, scale) + t / 2;
                    ctx.DrawLine(_s.Separator, new Point(0, ly), new Point(w, ly));
                }
            }
            GridTimings.Record("рисование: ячейки тела", tRows, $"ячеек {cells}");

            var th = GridTimings.Start();
            DrawHeader(ctx, c0, c1, frozen, scrollArea, w, headH, scale);
            GridTimings.Stop(th, "рисование: шапка");

            // Вертикальные линии — у правой границы каждой колонки, сквозь шапку и тело.
            using (ctx.PushClip(scrollArea))
                for (var c = c0; c <= c1; c++) DrawColumnLine(ctx, c, headH, h, scale);
            for (var c = 0; c < frozen; c++) DrawColumnLine(ctx, c, headH, h, scale);

        }

        var ts = GridTimings.Start();
        SyncWatched();
        GridTimings.Stop(ts, "рисование: подписки строк");
        LastCellsDrawn = cells;
        LastRender     = sw.Elapsed;
        GridTimings.Record("рисование кадра (UI)", LastRender.TotalMilliseconds, $"ячеек {cells}");
    }

    private static double Snap(double v, double scale) => Math.Round(v * scale) / scale;

    // Линия у правой границы колонки: в шапке — если колонка ее рисует, в теле — у всех, кроме
    // последней без права на линию (служебные таблицы), и не ниже последней строки, если так велено.
    private void DrawColumnLine(DrawingContext ctx, int c, double headH, double h, double scale)
    {
        var t      = _s.LineWidth;
        var lx     = Snap(ScreenRight(c) - t, scale) + t / 2;
        var header = _columns[c].HeaderRightLine;
        var body   = LastColumnLine || c < _columns.Count - 1;
        var bottom = LinesBelowRows ? h : Math.Min(h, RowTop(RowCount));
        if (header && body && bottom >= h) { ctx.DrawLine(_s.Separator, new Point(lx, 0), new Point(lx, h)); return; }
        if (header) ctx.DrawLine(_s.Separator, new Point(lx, 0), new Point(lx, headH));
        if (body && bottom > headH) ctx.DrawLine(_s.Separator, new Point(lx, headH), new Point(lx, bottom));
    }

    /// <summary>Рамка выделения ячеек — для слоя над редактором (<see cref="SelectionOverlay"/>, того же
    /// размера, что поверхность): поверх линий и редактора, обрезана по области строк (не ложится на
    /// шапку; полосы прокрутки — отдельные контролы за краем поверхности).</summary>
    public void RenderSelectionFrame(DrawingContext ctx, double scale)
    {
        EnsureTokens();
        if (_rows is null || _columns.Count == 0) return;
        var body = new Rect(0, HeaderHeight, Bounds.Width, Math.Max(0, Bounds.Height - HeaderHeight));
        if (CellSelectionBounds() is not { } sel) return;
        // Раскрытое поле — до вертикальной линии справа включительно, как и сама ячейка.
        if (_editorOverflow is { } o) sel = sel.Union(new Rect(o.X, o.Y, o.Width + _s.LineWidth, o.Height));
        if (!sel.Intersects(body)) return;
        using (ctx.PushClip(body))
            DrawSelectionFrame(ctx, sel, scale);
    }

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

        if (_cellSelTinted) ctx.FillRectangle(_s.SelectTint, new Rect(x0, y0, x1 - x0, y1 - y0));
        ctx.FillRectangle(_s.Accent, new Rect(x0, y0, x1 - x0, ft));             // верх
        ctx.FillRectangle(_s.Accent, new Rect(x0, y1 - ft, x1 - x0, ft));        // низ
        ctx.FillRectangle(_s.Accent, new Rect(x0, y0 + ft, ft, y1 - y0 - 2 * ft));      // лево
        ctx.FillRectangle(_s.Accent, new Rect(x1 - ft, y0 + ft, ft, y1 - y0 - 2 * ft)); // право
    }

    private void DrawHeader(DrawingContext ctx, int c0, int c1, int frozen, Rect scrollArea, double w, double headH, double scale)
    {
        var t = _s.LineWidth;
        // Фон строки заголовков одним прямоугольником на всю ширину — как в шаблоне нынешней таблицы.
        ctx.FillRectangle(_s.InactiveBg, new Rect(0, 0, w, headH));

        using (ctx.PushClip(scrollArea))
            for (var c = c0; c <= c1; c++) DrawHeaderColumn(ctx, c, headH, scale);
        for (var c = 0; c < frozen; c++) DrawHeaderColumn(ctx, c, headH, scale);

        // Линия под шапкой — как PART_ColumnHeadersAndRowsSeparator, прижатый к низу строки заголовков.
        var ly = Snap(headH - t, scale) + t / 2;
        ctx.DrawLine(_s.Separator, new Point(0, ly), new Point(w, ly));
    }

    private void DrawHeaderColumn(DrawingContext ctx, int c, double headH, double scale)
    {
        var t    = _s.LineWidth;
        var def  = _columns[c];
        var x    = ScreenLeft(c);
        var cw   = _columns.Width(c);
        var rect = new Rect(x, 0, cw, headH);
        // Область заголовка без вертикальной линии (Border в звездной колонке, линия в Auto);
        // у колонки без линии шапки — во всю ширину.
        var lineW = def.HeaderRightLine ? t : 0;
        var inner = new Rect(x, 0, Math.Max(0, cw - lineW), headH);

        if (def.Key is TableColumnDef.NumKey or TableColumnDef.AddPropKey)
        {
            // «№» и «+» без подсветки наведения — как центрованные заголовки нынешней шапки.
            DrawText(ctx, def.Header, inner, _s.Secondary, "s", TextAlignment.Center, 1, double.NaN, pad: 0);
            return;
        }

        // Наведение на заголовок — как Border.colHeader:pointerover.
        if (HeaderHoverFill && _hover.Kind is GridHitKind.Header or GridHitKind.HeaderFilter && _hover.Column == c)
            ctx.FillRectangle(_s.HoverStrong, inner);

        // [иконка типа][название][кнопка фильтра] в отступах CellPad — как MakeTextHeader.
        var left = x + CellPad;
        var icon = HeaderIcon(def);
        if (icon is not null)
        {
            var stroke = def.Key == TableRowVm.TitleKey ? _s.Red : _s.Secondary;
            DrawIcon(ctx, icon, left, Snap((headH - IconSize) / 2, scale), stroke);
            left += IconSize + CellPad;
        }

        // Кнопка фильтра; у колонки без нее (служебные таблицы) название идет до отступа у линии.
        var btn = ChevronRect(rect, lineW);
        var textRight = inner.Right - CellPad;
        if (def.Filterable)
        {
            if (_hover.Kind == GridHitKind.HeaderFilter && _hover.Column == c)
                ctx.DrawRectangle(_s.HoverStrong, null, new RoundedRect(btn, 4));
            var filtered = IsColumnFiltered(def.Key);
            var glyph    = filtered ? _s.Funnel ?? _s.Chevron : _s.Chevron;
            if (glyph is not null)
                DrawIcon(ctx, glyph, btn.X + (ChevronButton - IconSize) / 2, Snap(btn.Y + (ChevronButton - IconSize) / 2, scale),
                         filtered ? _s.Accent : _s.Secondary, thickness: 2);
            textRight = btn.X;
        }

        var textRect = new Rect(left, 0, Math.Max(1, textRight - left), headH);
        DrawText(ctx, def.Header, textRect, _s.Secondary, "s", TextAlignment.Left, 1, double.NaN, pad: 0);
    }

    // Иконка шапки: у служебных таблиц — по ключу из определения колонки, у таблицы коллекции — иконка
    // типа поля.
    private Geometry? HeaderIcon(TableColumnDef def)
    {
        if (def.HeaderIconKey is not { } key) return TypeIcon(def.FieldType);
        return CachedIcon(key);
    }

    private Geometry? CachedIcon(string key) => _s.CachedIcon(key);

    private void DrawCell(DrawingContext ctx, TableRowVm row, TableColumnDef def, Rect cell, RowStyle rowStyle,
                          bool hovered, bool editing, double scale, bool hasLine = true)
    {
        if (DebugSkipCells) return;
        // Область содержимого: без вертикальной линии справа (если она у колонки есть) и линии под строкой.
        var inner = hasLine ? ContentRect(cell) : new Rect(cell.X, cell.Y, cell.Width, Math.Max(0, cell.Height - _s.LineWidth));

        // «№» и «+»: непрозрачная серая заливка поверх подсветки наведения — как сегодня, где
        // прямоугольник заливки закрывает фон ячейки целиком.
        if (def.Key == TableColumnDef.NumKey)
        {
            ctx.FillRectangle(_s.InactiveBg, inner);
            DrawText(ctx, row.IsAddRow ? "+" : row.RowNumber.ToString(), inner, _s.Secondary, "s", TextAlignment.Center, 1, _s.LineHeight);
            return;
        }
        if (def.Key == TableColumnDef.AddPropKey)
        {
            ctx.FillRectangle(_s.InactiveBg, inner);
            return;
        }

        // Наведение подсвечивает ячейку (DataGridCell:pointerover), цвет ячейки ложится поверх полупрозрачно.
        if (hovered) ctx.FillRectangle(_s.HoverBg, inner);
        if (row.IsAddRow) return;

        // Выделенная колонка — заливка ячеек значений (фон ContentPresenter'а ячейки у нынешней
        // таблицы): до вертикальной линии, поверх наведения, под полупрозрачным цветом ячейки.
        if (IsColumnSelected(def.Key)) ctx.FillRectangle(_s.AccentLight, inner);

        // Правящаяся ячейка: значение и цвет ячейки показывает редактор над поверхностью.
        if (editing) return;

        // Цвет: ячейка → строка → колонка; заливка полупрозрачная, текст — насыщенный.
        var style = ResolveStyle(row, def.Key, rowStyle);
        if (style.Bg is not null && ColorTools.Fill(style.Bg) is { } fill)
            ctx.FillRectangle(fill, inner);
        var fg   = style.Fg is not null ? ColorTools.Text(style.Fg) : null;
        var fgId = style.Fg ?? "";

        // Расположение текста — тем же каскадом; не задано — правило типа (число вправо, остальное
        // влево) и по вертикали центр. У служебных таблиц оформления нет — там всегда правило типа.
        var align  = ToTextAlignment(style.HAlign ?? CellAlign.DefaultH(def.FieldType));
        var valign = style.VAlign ?? CellVAlign.Center;
        var centering = EditsInTextBox(def) ? TextCentering.Lines : TextCentering.BlockInArea;

        if (row.IsLoading)
        {
            if (def.Key == TableRowVm.TitleKey)
                DrawText(ctx, row[TableRowVm.TitleKey], inner, _s.Primary, "p", align, 1, _s.LineHeight,
                         valign: valign, centering: centering);
            return;
        }

        // Служебные таблицы: тон текста и доступность ячейки задает колонка.
        var enabled = def.CellEnabled?.Invoke(row) ?? true;
        if (def.CellTone is { } toneOf)
            (fg, fgId) = ToneBrush(toneOf(row));

        switch (def.FieldType)
        {
            case "checkbox":
                DrawCheckbox(ctx, inner, IsTruthy(row[def.Key]), scale, enabled);
                return;

            case "choice":
                DrawChoice(ctx, row, def, inner, enabled);
                return;

            case "button":
                DrawButton(ctx, def, inner, fg ?? _s.Primary);
                return;

            case "relation":
            {
                // Названия целей через разделитель поля; пусто — серое «связи нет» (RelationSelectBox).
                var text = RelationText(row, def);
                if (text.Length == 0) DrawText(ctx, "связи нет", inner, _s.Secondary, "s", align, 1, _s.LineHeight, valign: valign, centering: centering);
                else DrawText(ctx, text, inner, fg ?? _s.Primary, fg is null ? "p" : fgId, align, 1, _s.LineHeight, valign: valign, centering: centering);
                return;
            }

            case "file":
                DrawFile(ctx, row, def, inner, fg, fgId, scale, align, valign);
                return;
        }

        if (FieldTypeInfo.IsAuditComputed(def.FieldType))
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
                     fg ?? _s.Secondary, fg is null ? "s" : fgId, align, 1, _s.LineHeight, valign: valign, centering: centering);
            return;
        }

        if (FieldTypeInfo.IsRelationComputed(def.FieldType) || def.FieldType == "date_period")
        {
            DrawText(ctx, FieldTypeInfo.FormatComputedDisplay(row[def.Key], def.FieldType), inner,
                     fg ?? _s.Secondary, fg is null ? "s" : fgId, align, FitLines(inner.Height), _s.LineHeight,
                     valign: valign, centering: centering);
            return;
        }

        // Столько строк, сколько влезает по высоте ячейки (FitLinesToCell) — только явные переводы
        // строки, переноса по словам у ячеек нет.
        var lines = FitLines(inner.Height);

        if (def.Key == TableRowVm.TitleKey)
        {
            var title = row[TableRowVm.TitleKey];
            if (string.IsNullOrWhiteSpace(title)) title = TitleText.Placeholder;
            var muted = title == TitleText.Placeholder;
            DrawText(ctx, title, inner, fg ?? (muted ? _s.Secondary : _s.Primary), fg is null ? (muted ? "s" : "p") : fgId,
                     align, lines, _s.LineHeight, valign: valign, centering: centering);
            return;
        }

        var display = CellDisplayText.Of(def.FieldType, row[def.Key], def.NumberFormat, def.Separator);
        if (display.Length == 0) return;

        // Число — одной строкой (поле ввода числа переводов строки не держит).
        DrawText(ctx, display, inner, fg ?? _s.Primary, fg is null ? "p" : fgId, align,
                 def.FieldType == "number" ? 1 : lines, _s.LineHeight, valign: valign, centering: centering);
    }

    // «Файл» как у FileEditor в плоском режиме: имя ссылкой (акцент, подчеркнуто), когда файл на
    // месте; «имя — файл не найден» серым, когда его нет; пусто — серое «Выбрать файл…»; у
    // авто-привязки — «папка недоступна» / имя / «файл не найден». Справа — кнопка-скрепка
    // (Button.timeArrow: отступы 4 и 8, иконка 16). Есть ли файл — из кеша FileProbe, диск с
    // UI-потока не спрашивается. Расположение текста двигает надпись в полосе левее скрепки, а
    // скрепку по вертикали — вместе со строкой текста.
    private void DrawFile(DrawingContext ctx, TableRowVm row, TableColumnDef def, Rect inner, IBrush? fg, string fgId,
                          double scale, TextAlignment align, CellVAlign valign)
    {
        var path = row[def.Key];
        var auto = def.FileAutoFolder.Length > 0;

        // У авто-привязки скрепки нет (FileEditor прячет кнопку выбора) — надпись во всю ширину.
        var clipW    = auto ? 0 : ClipPadLeft + IconSize + ClipPadRight;
        var textRect = new Rect(inner.X, inner.Y, Math.Max(1, inner.Width - clipW), inner.Height);
        if (FileValue.ReadPaths(path).Count > 1)
        {
            if (!DebugSkipText)
                _s.FileLinks(path, def.FileAutoFolder, textRect, align, valign, CellTextPad, scale, fg: fg, fgId: fgId).Draw(ctx);
        }
        else
        {
            var (text, brush, brushId, underline) = _s.FileLook(path, def.FileAutoFolder, fg, fgId);
            DrawText(ctx, text, textRect, brush, brushId, align, 1, _s.LineHeight, underline: underline, valign: valign,
                     centering: TextCentering.BlockInArea);
        }
        if (!auto && _s.Paperclip is not null)
        {
            var line = CellAlign.TextTop(valign, inner.Height, _s.LineHeight, _s.TextEdge);
            DrawIcon(ctx, _s.Paperclip, inner.Right - ClipPadRight - IconSize,
                     Snap(inner.Y + line + (_s.LineHeight - IconSize) / 2, scale), _s.Secondary);
        }
    }

    private RowStyle ResolveStyle(TableRowVm row, string key, RowStyle rowStyle)
    {
        var column = _columnStyle(key);
        var cell   = row.CellStyle(key);
        if (rowStyle.IsEmpty && column.IsEmpty && cell.IsEmpty) return RowStyle.None;
        return RowStyle.Resolve(cell, rowStyle, column);
    }

    // Сколько строк текста помещается в ячейку этой высоты (не меньше одной).
    private int FitLines(double height) => Math.Max(1, (int)(height / _s.LineHeight));

    private static TextAlignment ToTextAlignment(CellHAlign align) => align switch
    {
        CellHAlign.Center => TextAlignment.Center,
        CellHAlign.Right  => TextAlignment.Right,
        _                 => TextAlignment.Left,
    };

    // «По центру» у ячейки, которая правится полем ввода («Название», текст, ссылка, число, дата),
    // центрует каждую строку, как само поле; у остальных — блок надписи внутри ячейки, как TextBlock
    // списка, мультисписка и связи (SurfaceStyle.DrawText, TextCentering). Иначе текст сдвигался бы
    // при входе в правку.
    private static bool EditsInTextBox(TableColumnDef def) => def.FieldType is "text" or "url" or "number" or "date";

    private string RelationText(TableRowVm row, TableColumnDef def)
    {
        if (_relLinks is null || def.Relation is null || def.Tag is not int fid) return "";
        if (!_relLinks.TryGet(fid, row.RecordId, out var links) || links.Count == 0) return "";
        // Название цели — первой строкой (TitleText): закрытый вид — одна строка.
        return string.Join(def.Relation.SeparatorString, links.Select(l => TitleText.FirstLine(l.Title)));
    }

    private static bool IsTruthy(string value) => value is "1" or "true" or "True";

    private (IBrush Brush, string Id) ToneBrush(CellTone tone) => tone switch
    {
        CellTone.Secondary => (_s.Secondary, "s"),
        CellTone.Green     => (_s.Green, "g"),
        CellTone.Red       => (_s.Red, "r"),
        _                  => (_s.Primary, "p"),
    };

    // ── Ячейка-список и ячейка-кнопка служебных таблиц ────────────────────────

    // Закрытый ComboBox приложения (Controls.axaml, ComboBoxTheme.axaml): отступ 6 по бокам, высота 26
    // по центру ячейки, серый фон, рамка в линию, скругление RadiusMedium, текст за отступом 10,
    // стрелка темы в квадрате 12×12 столбца 32 за отступом 10 от правого края (кисть и фигура —
    // локальные значения шаблона, стиль приложения их не перебивает). Наведение — рамка акцентом.
    // Недоступный — фон, рамка и стрелка из темы (стиль состояния перебивает значение шаблона), текст
    // прежний (цвет презентера до надписи не доходит — стиль надписи приоритетнее, см. theming.md).
    private const double ChoiceHeight      = 26;
    private const double ChoiceMargin      = 6;
    private const double ChoicePadLeft     = 10;
    private const double ChoicePadRight    = 8;
    private const double ChoiceGlyphColumn = 32;
    private const double ChoiceGlyphBox    = 12;   // PathIcon 12×12 шаблона (Stretch=Uniform внутри)
    private const double ChoiceGlyphMargin = 10;

    /// <summary>Рамка ячейки-списка (координаты поверхности) — по ней открывается настоящий список.</summary>
    public Rect ChoiceRect(Rect cell, int column)
    {
        EnsureTokens();
        return ChoiceBox(ContentRect(cell, column));
    }

    private Rect ChoiceBox(Rect inner) =>
        new(inner.X + ChoiceMargin, Snap(inner.Y + (inner.Height - ChoiceHeight) / 2, _frameScale),
            Math.Max(0, inner.Width - 2 * ChoiceMargin), ChoiceHeight);

    private void DrawChoice(DrawingContext ctx, TableRowVm row, TableColumnDef def, Rect inner, bool enabled)
    {
        var box = ChoiceBox(inner);
        if (box.Width <= 0) return;
        var hover  = enabled && _lastPointer is { } p && box.Contains(p);
        var bg     = enabled ? _s.ChoiceBg : _s.ChoiceBgDisabled;
        var border = !enabled ? _s.ChoiceBorderDisabled : hover ? _s.Accent : _s.ChoiceBorder;
        // Как Border с равномерной рамкой: заливка и обводка одним прямоугольником, обводка по центру
        // сдвинутого внутрь на полтолщины края.
        ctx.DrawRectangle(bg, new Pen(border, _s.LineWidth), new RoundedRect(box.Deflate(_s.LineWidth / 2), _s.ChoiceRadius));

        var value = row[def.Key];
        var text  = value.Length > 0 ? value : def.Placeholder;
        var (brush, id) = def.CellTone is { } toneOf ? ToneBrush(toneOf(row)) : (_s.Primary, "p");
        var textRect = new Rect(box.X + ChoicePadLeft, box.Y,
                                Math.Max(1, box.Width - ChoiceGlyphColumn - ChoicePadLeft - ChoicePadRight), box.Height);
        DrawText(ctx, text, textRect, brush, id, TextAlignment.Left, 1, double.NaN, pad: 0);

        var glyphRect = new Rect(box.Right - ChoiceGlyphMargin - ChoiceGlyphBox, box.Y + (box.Height - ChoiceGlyphBox) / 2,
                                 ChoiceGlyphBox, ChoiceGlyphBox);
        DrawFilledGlyph(ctx, SurfaceStyle.ChoiceGlyph, glyphRect, enabled ? _s.ChoiceGlyphBrush : _s.ChoiceGlyphDisabled);
    }

    private static void DrawFilledGlyph(DrawingContext ctx, Geometry geometry, Rect target, IBrush fill) =>
        SurfaceStyle.DrawFilledGlyph(ctx, geometry, target, fill);

    // Ячейка-кнопка: Button.icon 28×28 по центру ячейки с иконкой 16 в обводку IconStrokeSmall; фон
    // прозрачный и при наведении, курсор-рука над квадратом кнопки.
    private const double ButtonSize = 28;

    /// <summary>Квадрат кнопки ячейки-кнопки (координаты поверхности).</summary>
    public Rect ButtonRect(Rect cell, int column)
    {
        EnsureTokens();
        return ButtonBox(ContentRect(cell, column));
    }

    private Rect ButtonBox(Rect inner) =>
        new(Snap(inner.X + (inner.Width - ButtonSize) / 2, _frameScale), Snap(inner.Y + (inner.Height - ButtonSize) / 2, _frameScale),
            ButtonSize, ButtonSize);

    private void DrawButton(DrawingContext ctx, TableColumnDef def, Rect inner, IBrush stroke)
    {
        if (def.CellIconKey is not { } key || CachedIcon(key) is not { } icon) return;
        var btn = ButtonBox(inner);
        DrawIcon(ctx, icon, btn.X + (ButtonSize - IconSize) / 2, btn.Y + (ButtonSize - IconSize) / 2, stroke);
    }

    private TableRowVm? RowAt(int index) => _rows is not null && index >= 0 && index < _rows.Count ? _rows[index] : null;

    private bool CellEnabled(GridHit hit) =>
        hit.Def?.CellEnabled is not { } enabledOf || RowAt(hit.Row) is not { } row || enabledOf(row);

    // Надпись в области ячейки (SurfaceStyle.DrawText): отступ pad с обеих сторон (по умолчанию —
    // отступ ячейки: 6 у таблицы коллекции, 8 у служебных), по вертикали — по расположению текста
    // (по умолчанию по центру) на целом пикселе; обрезка по надписи у служебных таблиц (ClipText).
    private void DrawText(DrawingContext ctx, string text, Rect area, IBrush brush, string brushId,
                          TextAlignment align, int maxLines, double lineHeight, double pad = double.NaN, bool underline = false,
                          CellVAlign valign = CellVAlign.Center, TextCentering centering = TextCentering.Block)
    {
        if (DebugSkipText) return;
        if (double.IsNaN(pad)) pad = CellTextPad;
        _s.DrawText(ctx, text, area, brush, brushId, align, maxLines, lineHeight, pad, underline, ClipText, _frameScale,
                    valign: valign, centering: centering);
    }

    // Галочка — как единый CheckBox приложения (AppCheckBox): квадрат 16×16, радиус 2, рамка в
    // толщину линии цветом текста; отмеченная — заливка и рамка акцентом, белый глиф 12×12 внутри.
    // По горизонтали центрируется не сам квадрат, а контрол шириной 16 + 8 (у пустой подписи
    // остается отступ Padding=8) — квадрат стоит на 4 px левее центра ячейки, как в нынешней таблице.
    private const double CheckControlWidth = CheckSize + 8;

    private Rect CheckboxBox(Rect inner, double scale) =>
        new(Snap(inner.X + (inner.Width  - (CheckboxCentered ? CheckSize : CheckControlWidth)) / 2, scale),
            Snap(inner.Y + (inner.Height - CheckSize) / 2, scale), CheckSize, CheckSize);

    private void DrawCheckbox(DrawingContext ctx, Rect inner, bool isChecked, double scale, bool enabled = true) =>
        _s.DrawCheckbox(ctx, CheckboxBox(inner, scale), isChecked, enabled);

    private Geometry? TypeIcon(string? fieldType) => _s.TypeIcon(fieldType);

    // Иконки словаря Icons.axaml нарисованы в поле 24×24; масштабируем в size и обводим той же
    // толщиной, что Viewbox нынешней шапки (IconStrokeSmall в координатах поля).
    private void DrawIcon(DrawingContext ctx, Geometry geometry, double x, double y, IBrush stroke,
                          double size = IconSize, double? thickness = null) =>
        _s.DrawIcon(ctx, geometry, x, y, stroke, size, thickness);
}
