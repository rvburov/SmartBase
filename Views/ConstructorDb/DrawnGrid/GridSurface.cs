using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using SmartBase.Controls.Behaviors;
using SmartBase.Controls.Components;
using SmartBase.Core.Models;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.ConstructorDb.DrawnGrid;

/// <summary>
/// Рисованная поверхность таблицы: шапка, строки и ячейки видимого окна рисуются напрямую в
/// <see cref="Render"/>, контролов у ячеек нет. Строки берутся у виртуального списка по индексу
/// (первое обращение к странице заводит ее фоновую загрузку — как и в нынешней таблице), колонки —
/// у <see cref="ColumnLayout"/>. Цена кадра зависит только от размера окна: сколько строк и
/// колонок в него попало, столько и рисуется, что бы ни лежало в базе.
///
/// Прототип этапа 0 плана [docs/developer/architecture/performance/drawn-grid.md]: показ и
/// прокрутка по обеим осям на настоящих данных, без правки, выделения и меню. Токены вида — те
/// же ресурсы, что у нынешней таблицы (высота строки, шрифт, кисти, толщина линий, иконки).
/// </summary>
public sealed class GridSurface : Control
{
    /// <summary>Отступ содержимого ячейки от ее границ — как <c>CellContentPad</c> нынешней таблицы.</summary>
    public const double CellPad = 6;

    private const double IconSize = 16;   // IconSizeSmall

    // ── Данные ────────────────────────────────────────────────────────────────
    private IList<TableRowVm>?          _rows;
    private INotifyCollectionChanged?   _rowsNotify;
    private ColumnLayout                _columns     = ColumnLayout.Empty;
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

    // ── Токены вида ───────────────────────────────────────────────────────────
    private bool     _tokensReady;
    private IBrush   _bg          = Brushes.White;
    private IBrush   _inactiveBg  = Brushes.WhiteSmoke;
    private IBrush   _primary     = Brushes.Black;
    private IBrush   _secondary   = Brushes.Gray;
    private IBrush   _accent      = Brushes.RoyalBlue;
    private IBrush   _red         = Brushes.Red;
    private Pen      _separator   = new(Brushes.LightGray, 1);
    private double   _lineWidth   = 1;
    private double   _rowHeight   = 36;
    private double   _lineHeight  = 16;
    private Typeface _typeface    = Typeface.Default;
    private double   _fontSize    = 12;
    private double   _iconStroke  = 1.8;
    private Geometry? _chevron;
    private Geometry? _check;
    private Geometry? _paperclip;
    private TextLayoutCache? _text;
    private readonly Dictionary<string, Geometry?> _typeIcons = new();
    private readonly Dictionary<IBrush, Pen>       _iconPens  = new(ReferenceEqualityComparer.Instance);

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

    public event EventHandler? OffsetChanged;
    public event EventHandler? ExtentChanged;

    public GridSurface()
    {
        ClipToBounds = true;
        Focusable    = true;
    }

    // ── Публичный контракт ────────────────────────────────────────────────────

    public IList<TableRowVm>? Rows    => _rows;
    public ColumnLayout       Columns => _columns;
    public int RowCount => _rows?.Count ?? 0;

    /// <summary>Высота строки из токена «Оформления» (без линии).</summary>
    public double RowHeight    { get { EnsureTokens(); return _rowHeight; } }

    /// <summary>Шаг строк: высота плюс линия под ней — так же, как у нынешней сетки, где линия
    /// PART_BottomGridLine добавляется к высоте строки (36 + 1 = 37 при стандартных токенах).</summary>
    public double RowPitch     { get { EnsureTokens(); return _rowHeight + _lineWidth; } }
    public double HeaderHeight => RowPitch;

    public double ExtentWidth    => _columns.TotalWidth;
    public double ExtentHeight   => RowCount * RowPitch;
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

        _rows        = rows;
        _rowsNotify  = rows as INotifyCollectionChanged;
        _columns     = columns;
        _rowStyle    = rowStyle    ?? (_ => RowStyle.None);
        _columnStyle = columnStyle ?? (_ => RowStyle.None);
        _relLinks    = relLinks;

        if (_rowsNotify is not null) _rowsNotify.CollectionChanged += OnRowsChanged;

        ClampOffsets();
        ExtentChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Сменить раскладку колонок (ресайз окна, скрытие, сдвиг) — строки не трогаются.</summary>
    public void SetColumns(ColumnLayout columns)
    {
        _columns = columns;
        ClampOffsets();
        ExtentChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Строки, попадающие в окно при текущей прокрутке (включая частично видимые).</summary>
    public (int First, int Last) VisibleRows()
    {
        var count = RowCount;
        if (count == 0 || ViewportHeight <= 0) return (0, -1);
        var rowH  = RowPitch;
        var first = Math.Clamp((int)Math.Floor(_offsetY / rowH), 0, count - 1);
        var last  = Math.Clamp((int)Math.Floor((_offsetY + ViewportHeight - 1e-6) / rowH), first, count - 1);
        return (first, last);
    }

    /// <summary>Все видимые строки уже с данными (заглушек «Загрузка…» в окне нет).</summary>
    public bool VisibleRowsLoaded()
    {
        if (_rows is null) return true;
        var (first, last) = VisibleRows();
        for (var i = first; i <= last; i++)
            if (_rows[i].IsLoading) return false;
        return true;
    }

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
        InvalidateVisual();
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
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _tokensReady = false;
        EnsureTokens();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ForgetWatched();
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ClampOffsets();
        ExtentChanged?.Invoke(this, EventArgs.Empty);
        RequestInvalidate();
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

        _bg         = Brush("BrushBg",            Brushes.White);
        _inactiveBg = Brush("BrushInactiveBg",    Brushes.WhiteSmoke);
        _primary    = Brush("BrushPrimaryText",   Brushes.Black);
        _secondary  = Brush("BrushSecondaryText", Brushes.Gray);
        _accent     = Brush("BrushAccent",        Brushes.RoyalBlue);
        _red        = Brush("BrushRed",           Brushes.Red);
        _lineWidth  = Dbl("SeparatorThickness", 1);
        _separator  = new Pen(Brush("BrushSeparator", Brushes.LightGray), _lineWidth);
        _rowHeight  = Dbl(ConstructorSettings.RowHeightResourceKey, ConstructorSettings.RowHeight);
        _fontSize   = Dbl("FontSizeBase", 12);
        _typeface   = Res("FontFamilyBase") is FontFamily family ? new Typeface(family) : Typeface.Default;
        _iconStroke = Dbl("IconStrokeSmall", 1.8);
        _chevron    = Geo("IconChevronDown");
        _check      = Geo("IconCheck");
        _paperclip  = Geo("IconFieldFile");

        _text = new TextLayoutCache(_typeface, _fontSize);
        _typeIcons.Clear();
        _iconPens.Clear();

        // Высота строки текста — для предела строк многострочной ячейки (как FitLinesToCell).
        var probe = new TextLayout("Ag", _typeface, _fontSize, _primary);
        _lineHeight = probe.TextLines.Count > 0 && probe.TextLines[0].Height > 0
            ? probe.TextLines[0].Height
            : _fontSize * 1.35;
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
            var scale = LineMetrics.EffectiveScale(this);
            var t     = _lineWidth;
            var rowH  = _rowHeight + t;   // шаг строки: высота + линия под ней
            var headH = rowH;

            var (c0, c1) = _columns.VisibleRange(_offsetX, w);
            var (r0, r1) = VisibleRows();

            // Тело таблицы — под шапкой.
            var body = new Rect(0, headH, w, Math.Max(0, h - headH));
            using (ctx.PushClip(body))
            {
                for (var r = r0; r <= r1; r++)
                {
                    var row = _rows[r];
                    _visibleNow.Add(row);

                    var y        = headH + r * rowH - _offsetY;
                    var rowStyle = row.IsAddRow || row.IsLoading ? RowStyle.None : _rowStyle(row.RecordId);

                    for (var c = c0; c <= c1; c++)
                    {
                        var cell = new Rect(_columns.Left(c) - _offsetX, y, _columns.Width(c), rowH);
                        DrawCell(ctx, row, _columns[c], cell, rowStyle);
                        cells++;
                    }

                    // Нижняя линия строки — внутри строки, как PART_BottomGridLine.
                    var ly = Snap(y + rowH - t, scale) + t / 2;
                    ctx.DrawLine(_separator, new Point(0, ly), new Point(w, ly));
                }
            }

            // Вертикальные линии — у правой границы каждой колонки, сквозь шапку и тело.
            for (var c = c0; c <= c1; c++)
            {
                var lx = Snap(_columns.Right(c) - _offsetX - t, scale) + t / 2;
                ctx.DrawLine(_separator, new Point(lx, 0), new Point(lx, h));
            }

            DrawHeader(ctx, c0, c1, w, headH, scale);
        }

        SyncWatched();
        LastCellsDrawn = cells;
        LastRender     = sw.Elapsed;
    }

    private static double Snap(double v, double scale) => Math.Round(v * scale) / scale;

    private void DrawHeader(DrawingContext ctx, int c0, int c1, double w, double headH, double scale)
    {
        ctx.FillRectangle(_bg, new Rect(0, 0, w, headH));

        for (var c = c0; c <= c1; c++)
        {
            var def  = _columns[c];
            var x    = _columns.Left(c) - _offsetX;
            var cw   = _columns.Width(c);
            var rect = new Rect(x, 0, cw, headH);

            if (def.Key is TableColumnDef.NumKey or TableColumnDef.AddPropKey)
            {
                DrawText(ctx, def.Header, rect, _secondary, "s", TextAlignment.Center, 1);
                continue;
            }

            // [иконка типа][название][шеврон фильтра] — как MakeTextHeader.
            var left = x + CellPad;
            var icon = TypeIcon(def.FieldType);
            if (icon is not null)
            {
                var stroke = def.Key == TableRowVm.TitleKey ? _red : _secondary;
                DrawIcon(ctx, icon, left, (headH - IconSize) / 2, stroke);
                left += IconSize + CellPad;
            }

            var chevronX = x + cw - CellPad - IconSize;
            if (_chevron is not null) DrawIcon(ctx, _chevron, chevronX, (headH - IconSize) / 2, _secondary);

            var textRect = new Rect(left, 0, Math.Max(1, chevronX - CellPad - left), headH);
            DrawText(ctx, def.Header, textRect, _secondary, "s", TextAlignment.Left, 1, pad: 0);
        }

        // Линия под шапкой — как PART_ColumnHeadersAndRowsSeparator.
        var ly = Snap(headH - _lineWidth, scale) + _lineWidth / 2;
        ctx.DrawLine(_separator, new Point(0, ly), new Point(w, ly));
    }

    private void DrawCell(DrawingContext ctx, TableRowVm row, TableColumnDef def, Rect cell, RowStyle rowStyle)
    {
        if (DebugSkipCells) return;

        if (def.Key == TableColumnDef.NumKey)
        {
            ctx.FillRectangle(_inactiveBg, cell);
            DrawText(ctx, row.IsAddRow ? "+" : row.RowNumber.ToString(), cell, _secondary, "s", TextAlignment.Center, 1);
            return;
        }
        if (def.Key == TableColumnDef.AddPropKey)
        {
            ctx.FillRectangle(_inactiveBg, cell);
            return;
        }
        if (row.IsAddRow) return;

        // Цвет: ячейка → строка → колонка; заливка полупрозрачная, текст — насыщенный.
        var style = ResolveStyle(row, def.Key, rowStyle);
        if (style.Bg is not null && ColorTools.Fill(style.Bg) is { } fill)
            ctx.FillRectangle(fill, cell);
        var fg   = style.Fg is not null ? ColorTools.Text(style.Fg) : null;
        var fgId = style.Fg ?? "";

        if (row.IsLoading)
        {
            if (def.Key == TableRowVm.TitleKey)
                DrawText(ctx, row[TableRowVm.TitleKey], cell, _primary, "p", TextAlignment.Left, 1);
            return;
        }

        switch (def.FieldType)
        {
            case "checkbox":
                DrawCheckbox(ctx, cell, IsTruthy(row[def.Key]));
                return;

            case "relation":
                DrawText(ctx, RelationText(row, def), cell, fg ?? _primary, fg is null ? "p" : fgId, TextAlignment.Left, 1);
                return;

            case "file":
            {
                var name = FileValue.DisplayName(row[def.Key]);
                if (name.Length == 0) return;
                var textRect = new Rect(cell.X, cell.Y, Math.Max(1, cell.Width - IconSize - CellPad * 2), cell.Height);
                DrawText(ctx, name, textRect, fg ?? _accent, fg is null ? "a" : fgId, TextAlignment.Left, 1);
                if (_paperclip is not null)
                    DrawIcon(ctx, _paperclip, cell.Right - CellPad - IconSize - 2, cell.Y + (cell.Height - IconSize) / 2, _secondary);
                return;
            }
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
            DrawText(ctx, FieldTypeInfo.FormatComputedDisplay(raw, def.FieldType), cell,
                     fg ?? _secondary, fg is null ? "s" : fgId, TextAlignment.Left, 1);
            return;
        }

        if (def.Key == TableRowVm.TitleKey)
        {
            var title = row[TableRowVm.TitleKey];
            var muted = TitleText.IsPlaceholder(title);
            DrawText(ctx, title, cell, fg ?? (muted ? _secondary : _primary), fg is null ? (muted ? "s" : "p") : fgId,
                     TextAlignment.Left, 1);
            return;
        }

        var text = CellDisplayText.Of(def.FieldType, row[def.Key], def.NumberFormat, def.Separator);
        if (text.Length == 0) return;

        if (def.FieldType == "number")
        {
            DrawText(ctx, text, cell, fg ?? _primary, fg is null ? "p" : fgId, TextAlignment.Right, 1);
            return;
        }

        // Текст, ссылка, описание: столько строк, сколько влезает по высоте (FitLinesToCell) —
        // только для явных переводов строки, переноса по словам у ячеек нет.
        var lines = def.FieldType is "text" or "url"
            ? Math.Max(1, (int)(cell.Height / _lineHeight))
            : 1;
        DrawText(ctx, text, cell, fg ?? _primary, fg is null ? "p" : fgId, TextAlignment.Left, lines);
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
        return string.Join(def.Relation.SeparatorString, links.Select(l => l.Title));
    }

    private static bool IsTruthy(string value) => value is "1" or "true" or "True";

    private void DrawText(DrawingContext ctx, string text, Rect cell, IBrush brush, string brushId,
                          TextAlignment align, int maxLines, double pad = CellPad)
    {
        if (string.IsNullOrEmpty(text) || _text is null || DebugSkipText) return;

        var maxW   = Math.Max(1, cell.Width - pad * 2);
        var layout = _text.Get(text, maxW, align, brush, brushId, maxLines);
        var y      = cell.Y + Math.Max(0, (cell.Height - layout.Height) / 2);

        if (layout.Height > cell.Height || layout.Width > maxW + 0.5)
        {
            using (ctx.PushClip(cell))
                layout.Draw(ctx, new Point(cell.X + pad, y));
            return;
        }
        layout.Draw(ctx, new Point(cell.X + pad, y));
    }

    // Галочка: рамка 18×18 по центру, отмеченная — заливка акцентом и белый знак IconCheck.
    private void DrawCheckbox(DrawingContext ctx, Rect cell, bool isChecked)
    {
        const double size = 18;
        var x   = cell.X + (cell.Width - size) / 2;
        var y   = cell.Y + (cell.Height - size) / 2;
        var box = new RoundedRect(new Rect(x, y, size, size), 3);
        if (isChecked)
        {
            ctx.DrawRectangle(_accent, null, box);
            if (_check is not null) DrawIcon(ctx, _check, x + 2, y + 2, Brushes.White, size - 4, 2.4);
        }
        else
        {
            ctx.DrawRectangle(null, new Pen(_secondary, 1), box);
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
