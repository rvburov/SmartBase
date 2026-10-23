using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SmartBase.Controls.Behaviors;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Controls.DrawnGrid;

/// <summary>Вид карточки списка свойств: свойство записи, системное «Название», «Добавить свойство»,
/// подпись раздела ссылок («Ссылаются на эту запись») и карточка ссылок («Счета · Клиент»).</summary>
public enum PropertyCardKind { Field, Title, Add, Section, Backlink }

/// <summary>
/// Карточка списка свойств страницы записи — что рисовать: название и иконка типа, тип и значение
/// (читается из модели при каждом кадре), подсказка при пустом значении, форматы. Состояние
/// (выделение, ширина колонки названий) живет у панели и модели, карточка его не хранит.
/// </summary>
public sealed class PropertyCard
{
    public required string Name    { get; init; }
    /// <summary>Иконка типа по ключу реестра Icons.axaml (IconFieldText, IconPlus…).</summary>
    public required string IconKey { get; init; }
    public PropertyCardKind Kind   { get; init; } = PropertyCardKind.Field;
    /// <summary>Тип значения — какой контрол стоял бы в карточке (text, number, date, select, status,
    /// user, multiselect, checkbox, url, file, relation, вычисляемые).</summary>
    public string FieldType        { get; init; } = "text";
    /// <summary>Значение хранения из модели; у «Названия» — редактируемый текст названия.</summary>
    public Func<string> Value      { get; init; } = static () => "";
    /// <summary>Серая подсказка при пустом значении (url — «https://», название — «Без названия»,
    /// карточка ссылок — «ссылок нет»).</summary>
    public string Placeholder      { get; init; } = "";
    /// <summary>Значение приглушенным серым (вычисляемые, «связь не настроена»).</summary>
    public bool Muted              { get; init; }
    public NumberFormat NumberFormat { get; init; } = NumberFormat.Default;
    public string Separator        { get; init; } = ", ";
    public string FileAutoFolder   { get; init; } = "";
    /// <summary>Готовый текст показа вместо значения (связь — названия целей); null — по типу.</summary>
    public Func<string>? Display   { get; init; }
    public object? Tag             { get; init; }
}

/// <summary>Зона карточки под указателем: название (иконка, подпись и все пустое место карточки —
/// жест выделения), ручка ширины ⋮, контрол значения.</summary>
public enum PropertyZone { None, Name, Grip, Value }

/// <summary>Попадание по списку свойств: индекс карточки (−1 — мимо), зона и прямоугольник карточки в
/// координатах поверхности.</summary>
public readonly record struct PropertyHit(int Index, PropertyZone Zone, Rect Card)
{
    public static readonly PropertyHit None = new(-1, PropertyZone.None, default);
}

/// <summary>Нажатие указателя на карточке (или под ее редактором): попадание и само событие.</summary>
public sealed class PropertyPointerEventArgs(PropertyHit hit, PointerPressedEventArgs pointer) : EventArgs
{
    public PropertyHit             Hit     { get; } = hit;
    public PointerPressedEventArgs Pointer { get; } = pointer;
}

/// <summary>
/// Рисованный список карточек свойств страницы записи (этап 7 плана drawn-grid.md). Карточки
/// видимого окна рисуются напрямую в <see cref="Render"/>: белая плашка с рамкой и скруглением на
/// серой подложке, иконка типа и подпись, ручка ⋮ между колонками, значение по типу — так же, как
/// выглядели контролы в карточках (TextBox, ComboBox, галочка, дата, файл, связь, мультисписок в
/// плоском режиме). Геометрия — один в один с прежней карточкой: Border.proprow (рамка 1, отступы
/// 10,3, скругление 8, высота 40, зазор 6), колонка названий заданной ширины, ручка 18, значение
/// правее ручки с отступом 2,5 (положение округляется как раскладка Avalonia). Цена кадра зависит
/// только от числа карточек в окне; тысяча свойств стоит столько же, сколько двадцать.
///
/// Настоящие редакторы (те же контролы, что раньше стояли в карточках) панель
/// (<see cref="PropertyList"/>) кладет над карточкой под указателем и при фокусе —
/// накладным редактором <see cref="EditorHost"/>; карточка с редактором рисуется без значения.
/// Наведение красит всю карточку, выделение — голубой фон, синяя рамка, синие подпись и иконка
/// (модель выделения — у панели записи, <see cref="IsSelected"/>). Ручку ⋮ поверхность тянет сама
/// (<see cref="NameColumnWidthChanged"/>), остальные нажатия отдает наверх (<see cref="Pressed"/>).
/// </summary>
public sealed class PropertySurface : Control, IEditorSurface
{
    // Геометрия карточки (DIP) — как у Border.proprow и ItemsControl с полями 16,16,16,10.
    public const double CardHeight   = 40;
    public const double CardGap      = 6;
    public const double Pitch        = CardHeight + CardGap;
    public const double MarginTop    = 16;
    public const double MarginSide   = 16;
    public const double MarginBottom = 10;
    public const double Pad          = 10;   // Padding карточки по горизонтали
    public const double PadV         = 3;    // Padding карточки по вертикали
    public const double BorderWidth  = 1;
    public const double Radius       = 8;
    public const double GripWidth    = 18;
    public const double ValueGap     = 2.5;  // отступ значения от ручки (Margin контрола значения)
    public const double NameColMin   = 70;
    public const double NameColDefault = 150;
    private const double IconSize    = SurfaceStyle.IconSize;
    private const double NameGap     = 8;    // подпись от иконки (Margin 8,0,4,0)
    private const double NameGapRight = 4;
    private const double DotSize     = 3;    // точка ручки и шаг между точками (Spacing 3)
    private const double ClipPadLeft = 4;    // Button.timeArrow Padding 4,0,8,0 (иконка даты, файла, связи)
    private const double ClipPadRight = 8;
    private const double ChoiceGlyphColumn = 32;   // столбец стрелки ComboBox
    private const double ChoiceGlyphBox    = 12;
    private const double ChoiceGlyphMargin = 10;
    private const double ChoicePadRight    = 8;    // ComboBox.propValue Padding 0,0,8,0
    private const double BoxMinHeight  = 30;       // MultiSelectBox и RelationSelectBox MinHeight
    private const double MultiPadV     = 4;        // MultiSelectBox Padding 8,4 (по горизонтали 0 в карточке)

    private static readonly Cursor HandCursor   = new(StandardCursorType.Hand);
    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);

    private IReadOnlyList<PropertyCard> _cards = [];
    private double _nameColWidth = NameColDefault;
    private double _offsetY;

    private int          _hover = -1;
    private PropertyZone _hoverZone;
    private Point?       _lastPointer;
    private int          _editing = -1;

    private bool   _dragging;
    private double _dragStartX;
    private double _dragStartWidth;

    private bool         _tokensReady;
    private SurfaceStyle _s = SurfaceStyle.Fallback();
    private double       _frameScale = 1;
    private bool         _invalidatePending;
    private static readonly Geometry Dot = new EllipseGeometry(new Rect(0, 0, DotSize, DotSize));

    /// <summary>Выделена ли карточка (модель — у панели записи).</summary>
    public Func<int, bool> IsSelected { get; set; } = _ => false;

    public event EventHandler? OffsetChanged;
    public event EventHandler? ExtentChanged;
    public event EventHandler? ViewChanged;
    /// <summary>Нажатие на карточке (кроме ручки ⋮ — ее поверхность тянет сама).</summary>
    public event EventHandler<PropertyPointerEventArgs>? Pressed;
    /// <summary>Под указателем другая карточка или другая зона (индекс −1 — мимо карточек).</summary>
    public event EventHandler<PropertyHit>? HoverChanged;
    /// <summary>Ширина колонки названий меняется ручкой ⋮ (значение уже ограничено).</summary>
    public event EventHandler<double>? NameColumnWidthChanged;
    /// <summary>Ручку ⋮ отпустили — пора сохранять.</summary>
    public event EventHandler? NameColumnResizeEnded;

    public PropertySurface()
    {
        ClipToBounds = true;
        Focusable    = true;
    }

    // ── Контракт ──────────────────────────────────────────────────────────────

    public IReadOnlyList<PropertyCard> Cards => _cards;
    public int Count => _cards.Count;

    /// <summary>Заменить карточки; прокрутка держится (в пределах новой протяженности).</summary>
    public void SetCards(IReadOnlyList<PropertyCard> cards)
    {
        _cards   = cards;
        _hover   = -1;
        _editing = -1;
        ClampOffset();
        ExtentChanged?.Invoke(this, EventArgs.Empty);
        RaiseViewChanged();
    }

    /// <summary>Ширина колонки названий (общая для всех карточек). Хранится как задана, а при
    /// рисовании и попадании ограничивается снизу <see cref="NameColMin"/> и сверху так, чтобы
    /// значению осталось место (ширина списка минус 160): до первой раскладки ширина списка
    /// неизвестна, и ограничивать заданное значение тогда было бы нечем.</summary>
    public double NameColumnWidth
    {
        get => NameWidth;
        set
        {
            var v = double.IsNaN(value) ? NameColDefault : value;
            if (Math.Abs(v - _nameColWidth) < 1e-9) return;
            _nameColWidth = v;
            RaiseViewChanged();
        }
    }

    // Действующая ширина колонки названий: названиям не даем съесть все значение.
    private double NameWidth => ClampNameWidth(_nameColWidth);

    private double ClampNameWidth(double v)
    {
        var max = Math.Max(NameColMin, CardWidth - 160);
        return Math.Clamp(double.IsNaN(v) ? NameColDefault : v, NameColMin, max);
    }

    public double OffsetY
    {
        get => _offsetY;
        set
        {
            var max = Math.Max(0, ExtentHeight - ViewportHeight);
            var y   = Math.Clamp(double.IsNaN(value) ? 0 : value, 0, max);
            if (y == _offsetY) return;
            _offsetY = y;
            OffsetChanged?.Invoke(this, EventArgs.Empty);
            RefreshHover();
            RaiseViewChanged();
        }
    }

    public double ExtentHeight   => _cards.Count == 0 ? 0 : MarginTop + _cards.Count * Pitch + MarginBottom;
    public double ViewportHeight => Math.Max(0, Bounds.Height);
    public double CardWidth      => Math.Max(0, Bounds.Width - 2 * MarginSide);

    /// <summary>Прямоугольник карточки в координатах поверхности при текущей прокрутке.</summary>
    public Rect CardRect(int index) => new(MarginSide, MarginTop + index * Pitch - _offsetY, CardWidth, CardHeight);

    /// <summary>Область содержимого карточки (внутри рамки и отступов).</summary>
    private static Rect ContentRect(Rect card) =>
        new(card.X + BorderWidth + Pad, card.Y + BorderWidth + PadV,
            Math.Max(0, card.Width - 2 * (BorderWidth + Pad)), Math.Max(0, card.Height - 2 * (BorderWidth + PadV)));

    /// <summary>Левый край колонки значения: за колонкой названий и ручкой, отступ 2,5 округлен как
    /// положение контрола раскладкой (к ближайшему физическому пикселю, половина — к четному).</summary>
    private double ValueLeft(Rect content) =>
        content.X + SurfaceStyle.Snap(NameWidth + GripWidth + ValueGap, CurrentScale());

    /// <summary>Колонка значения во всю высоту содержимого — сюда кладется редактор
    /// (контрол с VerticalAlignment=Center встает так же, как стоял в сетке карточки).</summary>
    public Rect ValueRect(int index)
    {
        var content = ContentRect(CardRect(index));
        var x = ValueLeft(content);
        return new Rect(x, content.Y, Math.Max(0, content.Right - x), content.Height);
    }

    /// <summary>Полоса самого контрола значения — как он стоял в карточке (поле в 14 по центру, дата и
    /// файл 16, мультисписок и связь 30, галочка 24×16); клик по ней — правка, мимо нее в карточке —
    /// выделение. У «Добавить свойство» и подписи раздела значения нет.</summary>
    public Rect ValueControlRect(int index)
    {
        if (index < 0 || index >= _cards.Count || _cards[index].Kind is PropertyCardKind.Add or PropertyCardKind.Section) return default;
        var value = ValueRect(index);
        var scale = CurrentScale();
        var textH = TextBoxHeight(scale);
        return _cards[index].FieldType switch
        {
            "date" or "file"            => Band(value, IconSize, scale),
            "multiselect" or "relation" => Band(value, BoxMinHeight, scale),
            "checkbox"                  => new Rect(value.X, Band(value, IconSize, scale).Y, IconSize + 8, IconSize),
            _                           => Band(value, textH, scale),
        };
    }

    // Контрол высотой h, стоящий по центру колонки значения (как VerticalAlignment=Center: смещение
    // округляется раскладкой).
    private static Rect Band(Rect value, double h, double scale) =>
        new(value.X, value.Y + SurfaceStyle.Snap((value.Height - h) / 2, scale), value.Width, h);

    /// <summary>Ручка ⋮ — полоса 18 во всю высоту содержимого между названием и значением.</summary>
    public Rect GripRect(int index)
    {
        var content = ContentRect(CardRect(index));
        return new Rect(content.X + NameWidth, content.Y, GripWidth, content.Height);
    }

    /// <summary>Карточки, попадающие в окно при текущей прокрутке (включая частично видимые).</summary>
    public (int First, int Last) VisibleRange()
    {
        if (_cards.Count == 0 || ViewportHeight <= 0) return (0, -1);
        var first = (int)Math.Floor((_offsetY - MarginTop) / Pitch);
        var last  = (int)Math.Floor((_offsetY - MarginTop + ViewportHeight) / Pitch);
        return (Math.Clamp(first, 0, _cards.Count - 1), Math.Clamp(last, 0, _cards.Count - 1));
    }

    /// <summary>Прокрутить так, чтобы карточка была в окне целиком — минимальным сдвигом.</summary>
    public void ScrollIntoView(int index)
    {
        if (index < 0 || index >= _cards.Count) return;
        var top    = MarginTop + index * Pitch;
        var bottom = top + CardHeight;
        var view   = ViewportHeight;
        if (top < _offsetY) OffsetY = top - MarginTop;
        else if (bottom > _offsetY + view) OffsetY = bottom + CardGap - view;
    }

    /// <summary>Значения читаются из модели при рисовании — после правки достаточно перерисовать.</summary>
    public void RefreshValues() => InvalidateVisual();

    /// <summary>Что лежит под точкой поверхности.</summary>
    public PropertyHit HitTest(Point p)
    {
        if (_cards.Count == 0 || CardWidth <= 0) return PropertyHit.None;
        var i = (int)Math.Floor((p.Y + _offsetY - MarginTop) / Pitch);
        if (i < 0 || i >= _cards.Count) return PropertyHit.None;
        var card = CardRect(i);
        // Подпись раздела — не карточка: ни наведения, ни нажатия.
        if (!card.Contains(p) || _cards[i].Kind == PropertyCardKind.Section) return PropertyHit.None;
        if (GripRect(i).Contains(p)) return new PropertyHit(i, PropertyZone.Grip, card);
        var control = ValueControlRect(i);
        if (control.Width > 0 && control.Contains(p)) return new PropertyHit(i, PropertyZone.Value, card);
        return new PropertyHit(i, PropertyZone.Name, card);
    }

    /// <summary>Последнее попадание указателя (карточка и зона под ним; −1 — мимо).</summary>
    public PropertyHit LastHit => new(_hover, _hoverZone, _hover >= 0 && _hover < _cards.Count ? CardRect(_hover) : default);

    /// <summary>Указатель над редактором карточки (он лежит над поверхностью и забирает события):
    /// наведение ведется так же, как над самой поверхностью. null — указатель ушел.</summary>
    public void HoverAt(Point? p)
    {
        _lastPointer = p;
        if (_dragging) return;
        SetHover(p is { } q ? HitTest(q) : PropertyHit.None);
    }

    // ── IEditorSurface ────────────────────────────────────────────────────────

    public Rect? EditorRect(int row, string key) => row >= 0 && row < _cards.Count ? ValueRect(row) : null;

    public Rect EditorClip => new(0, 0, Math.Max(0, Bounds.Width), Math.Max(0, Bounds.Height));

    public void SetEditingCell(int row, string key)
    {
        if (_editing == row) return;
        _editing = row;
        InvalidateVisual();
    }

    public void ClearEditingCell()
    {
        if (_editing < 0) return;
        _editing = -1;
        InvalidateVisual();
    }

    // ── Прокрутка и размер ────────────────────────────────────────────────────

    private void ClampOffset() => OffsetY = _offsetY;

    private void RaiseViewChanged()
    {
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ClampOffset();
        ExtentChanged?.Invoke(this, EventArgs.Empty);
        RaiseViewChanged();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _tokensReady = false;
        EnsureTokens();
        LineMetrics.Changed       += OnMetricsChanged;
        FontScale.Changed         += OnMetricsChanged;
        ActualThemeVariantChanged += OnThemeChanged;
        FileProbe.Resolved        += RequestInvalidate;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LineMetrics.Changed       -= OnMetricsChanged;
        FontScale.Changed         -= OnMetricsChanged;
        ActualThemeVariantChanged -= OnThemeChanged;
        FileProbe.Resolved        -= RequestInvalidate;
        base.OnDetachedFromVisualTree(e);
        CancelDrag();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => OnMetricsChanged();

    private void OnMetricsChanged()
    {
        _tokensReady = false;
        EnsureTokens();
        RaiseViewChanged();
    }

    private void EnsureTokens()
    {
        if (_tokensReady) return;
        _tokensReady = true;
        _s = SurfaceStyle.Load(this, "RowHeightStandard");
    }

    // Одна перерисовка на кадр (ответы диска о файлах приходят пачкой).
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

    private double CurrentScale() => LineMetrics.EffectiveScale(this);

    // Высота поля ввода (TextBox.propValue без отступов и рамки) — высота строки текста, округленная
    // вверх до целого DIP: так меряются надписи и поля (по замеру прежних карточек — 14 при любом
    // масштабе).
    private double TextBoxHeight(double scale)
    {
        EnsureTokens();
        return Math.Ceiling(NaturalTextHeight() - 1e-6);
    }

    private double _naturalTextHeight;

    private double NaturalTextHeight()
    {
        if (_naturalTextHeight <= 0)
        {
            var probe = _s.Text.Get("Ag", 1000, TextAlignment.Left, _s.Primary, "p", 1);
            _naturalTextHeight = probe.Height > 0 ? probe.Height : _s.LineHeight;
        }
        return _naturalTextHeight;
    }

    // ── Указатель ─────────────────────────────────────────────────────────────

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _lastPointer = p;
        if (_dragging)
        {
            NameColumnWidth = ClampNameWidth(_dragStartWidth + (p.X - _dragStartX));
            NameColumnWidthChanged?.Invoke(this, NameWidth);
            e.Handled = true;
            return;
        }
        SetHover(HitTest(p));
    }

    // Уход указателя с поверхности сам по себе наведение не снимает: он мог уйти в редактор,
    // стоящий над этой же карточкой (тогда карточка остается под указателем, как Border с
    // контролом внутри). Снимает панель (HoverAt(null)), когда указателя нет ни над ней, ни над
    // редактором.
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        PointerLeft?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Указатель ушел с поверхности (возможно, в редактор над ней).</summary>
    public event EventHandler? PointerLeft;

    private void RefreshHover()
    {
        if (_dragging || _lastPointer is not { } p) return;
        SetHover(HitTest(p));
    }

    private void SetHover(PropertyHit hit)
    {
        if (hit.Index == _hover && hit.Zone == _hoverZone) return;
        var repaint = hit.Index != _hover;
        _hover     = hit.Index;
        _hoverZone = hit.Zone;
        Cursor = hit.Zone switch
        {
            PropertyZone.Grip => ResizeCursor,
            PropertyZone.None => null,
            _                 => HandCursor,
        };
        if (repaint) InvalidateVisual();
        HoverChanged?.Invoke(this, hit);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p   = e.GetPosition(this);
        var hit = HitTest(p);
        if (hit.Zone == PropertyZone.None)
        {
            // Мимо карточек (подпись раздела, пустое место) меню нет: иначе открылось бы меню
            // карточки, по которой щелкали правой кнопкой раньше.
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) ContextMenu = null;
            return;
        }

        if (hit.Zone == PropertyZone.Grip && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging       = true;
            _dragStartX     = p.X;
            _dragStartWidth = NameWidth;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        Pressed?.Invoke(this, new PropertyPointerEventArgs(hit, e));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
        SetHover(HitTest(e.GetPosition(this)));
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        CancelDrag();
    }

    private void CancelDrag() { if (_dragging) EndDrag(); }

    private void EndDrag()
    {
        _dragging = false;
        NameColumnResizeEnded?.Invoke(this, EventArgs.Empty);
    }

    // ── Рисование ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        EnsureTokens();
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (_cards.Count == 0 || w <= 0 || h <= 0) return;

        var scale = _frameScale = LineMetrics.EffectiveScale(this);
        var (first, last) = VisibleRange();
        for (var i = first; i <= last; i++)
            DrawCard(ctx, i, scale);
    }

    private void DrawCard(DrawingContext ctx, int i, double scale)
    {
        var card     = _cards[i];
        var rect     = CardRect(i);
        var content  = ContentRect(rect);
        if (card.Kind == PropertyCardKind.Section)
        {
            // Подпись раздела без плашки и ручки: серый текст у нижнего края места карточки, над
            // карточками своего раздела, — во всю ширину (имя базы бывает длинным).
            var label = new Rect(content.X, content.Bottom - IconSize, content.Width, IconSize);
            DrawLabel(ctx, card.Name, label, _s.Secondary, "s", scale, TextTrimming.CharacterEllipsis);
            return;
        }
        var selected = card.Kind is PropertyCardKind.Field or PropertyCardKind.Title && IsSelected(i);
        var hovered  = _hover == i;

        // Плашка: как Border с равномерной рамкой — заливка и обводка одним прямоугольником по краю,
        // сдвинутому внутрь на полтолщины; hover — притемненный белый, выделение — голубой фон и
        // синяя рамка (Border.proprow:pointerover / .selected).
        var bg     = selected ? _s.AccentLight : hovered ? _s.InactiveBg : _s.Bg;
        var border = selected ? _s.Accent : _s.SeparatorBrush;
        ctx.DrawRectangle(bg, new Pen(border, BorderWidth), new RoundedRect(rect.Deflate(BorderWidth / 2), Radius));

        // Название: иконка типа и подпись в полосе высотой иконки по центру содержимого; подпись
        // серая, в выделенной карточке — синяя (у «Добавить свойство» — всегда серая).
        var nameBrush = selected ? _s.Accent : _s.Secondary;
        var nameId    = selected ? "a" : "s";
        var rowY      = content.Y + SurfaceStyle.Snap((content.Height - IconSize) / 2, scale);
        if (_s.CachedIcon(card.IconKey) is { } icon)
            _s.DrawIcon(ctx, icon, content.X, rowY, nameBrush);
        var nameArea = new Rect(content.X + IconSize + NameGap, rowY, Math.Max(1, NameWidth - IconSize - NameGap - NameGapRight), IconSize);
        DrawLabel(ctx, card.Name, nameArea, nameBrush, nameId, scale, TextTrimming.CharacterEllipsis);

        // Ручка ⋮: три точки 3×3 с шагом 3 по центру полосы 18 (положение округлено как раскладка:
        // половина — к четному).
        var gripX = content.X + NameWidth + SurfaceStyle.Snap((GripWidth - DotSize) / 2, scale);
        var gripY = content.Y + SurfaceStyle.Snap((content.Height - (DotSize * 5)) / 2, scale);
        for (var k = 0; k < 3; k++)
            using (ctx.PushTransform(Matrix.CreateTranslation(gripX, gripY + k * DotSize * 2)))
                ctx.DrawGeometry(_s.Secondary, null, Dot);

        if (card.Kind == PropertyCardKind.Add || _editing == i) return;

        var value = new Rect(ValueLeft(content), content.Y, Math.Max(0, content.Right - ValueLeft(content)), content.Height);
        if (value.Width <= 0) return;
        DrawValue(ctx, card, value, scale);
    }

    // Надпись, стоящая в полосе area так, как TextBlock (VerticalAlignment=Center): высота надписи
    // округляется вверх до пикселя, смещение — к ближайшему (половина — к четному), обрезка по
    // прямоугольнику самой надписи. Обрезка без многоточия — как поле ввода.
    private void DrawLabel(DrawingContext ctx, string text, Rect area, IBrush brush, string brushId, double scale,
                           TextTrimming trimming, bool underline = false)
    {
        if (string.IsNullOrEmpty(text) || area.Width <= 0) return;
        var layout = _s.Text.Get(text, area.Width, TextAlignment.Left, brush, brushId, 1, double.NaN,
                                 underline ? SurfaceStyle.Underline : null, trimming);
        var boxH   = Math.Ceiling(layout.Height - 1e-6);   // размер надписи — вверх до целого DIP, как DesiredSize
        var y      = area.Y + SurfaceStyle.Snap((area.Height - boxH) / 2, scale);
        using (ctx.PushClip(new Rect(area.X, y, area.Width, boxH)))
            layout.Draw(ctx, new Point(area.X, y));
    }

    private void DrawValue(DrawingContext ctx, PropertyCard card, Rect value, double scale)
    {
        var raw = card.Value();
        switch (card.FieldType)
        {
            case "checkbox":
            {
                var square = new Rect(value.X, value.Y + SurfaceStyle.Snap((value.Height - IconSize) / 2, scale), IconSize, IconSize);
                _s.DrawCheckbox(ctx, square, raw is "1" or "true" or "True");
                return;
            }
            case "select":
            case "status":
            case "user":
            {
                // ComboBox.propValue: текст в поле высотой строки, столбец стрелки 32 справа с отступом
                // 8, стрелка темы 12×12 за отступом 10 от правого края.
                var textArea = new Rect(value.X, value.Y, Math.Max(1, value.Width - ChoiceGlyphColumn - ChoicePadRight), value.Height);
                if (raw.Length > 0) DrawLabel(ctx, raw, textArea, _s.Primary, "p", scale, TextTrimming.CharacterEllipsis);
                var boxH  = TextBoxHeight(scale);
                var boxY  = value.Y + SurfaceStyle.Snap((value.Height - boxH) / 2, scale);
                var glyph = new Rect(value.Right - ChoiceGlyphMargin - ChoiceGlyphBox,
                                     boxY + SurfaceStyle.Snap((boxH - ChoiceGlyphBox) / 2, scale), ChoiceGlyphBox, ChoiceGlyphBox);
                SurfaceStyle.DrawFilledGlyph(ctx, SurfaceStyle.ChoiceGlyph, glyph, _s.ChoiceGlyphBrush);
                return;
            }
            case "multiselect":
            {
                // MultiSelectBox в плоском режиме: бокс 30 по центру, отступ 4 сверху и снизу, внутри
                // сетка «текст | ▾» по центру; стрелка — символ шрифта с отступами 4 и 2.
                var text  = card.Display?.Invoke() ?? MultiSelectValue.Display(raw, card.Separator);
                var caret = _s.Text.Get("▾", 100, TextAlignment.Left, _s.Secondary, "s", 1);
                var caretW = Math.Ceiling(caret.WidthIncludingTrailingWhitespace - 1e-6);
                var caretH = Math.Ceiling(caret.Height - 1e-6);
                var box    = Band(value, BoxMinHeight, scale);
                var inner  = new Rect(box.X, box.Y + MultiPadV, box.Width, box.Height - 2 * MultiPadV);
                var rowH   = Math.Max(caretH, TextBoxHeight(scale));
                var rowY   = inner.Y + SurfaceStyle.Snap((inner.Height - rowH) / 2, scale);
                var row    = new Rect(inner.X, rowY, inner.Width, rowH);
                var caretX = row.Right - 2 - caretW;
                using (ctx.PushClip(new Rect(caretX, rowY, caretW, caretH)))
                    caret.Draw(ctx, new Point(caretX, rowY));
                var textArea = new Rect(row.X, row.Y, Math.Max(1, caretX - 4 - row.X), row.Height);
                if (text.Length > 0) DrawLabel(ctx, text, textArea, _s.Primary, "p", scale, TextTrimming.CharacterEllipsis);
                else DrawLabel(ctx, "Выберите…", textArea, _s.Secondary, "s", scale, TextTrimming.CharacterEllipsis);
                return;
            }
            case "relation":
            {
                // RelationSelectBox (правка): текст в панели 30 по центру, справа иконка-связь в
                // рамке с отступами 4 и 8 (как кнопка файла).
                var box  = Band(value, BoxMinHeight, scale);
                var text = card.Display?.Invoke() ?? raw;
                var textArea = new Rect(box.X, box.Y, Math.Max(1, box.Width - (ClipPadLeft + IconSize + ClipPadRight)), box.Height);
                if (card.Muted) DrawLabel(ctx, text, textArea, _s.Secondary, "s", scale, TextTrimming.CharacterEllipsis);
                else if (text.Length > 0) DrawLabel(ctx, text, textArea, _s.Primary, "p", scale, TextTrimming.CharacterEllipsis);
                else DrawLabel(ctx, card.Placeholder.Length > 0 ? card.Placeholder : "связи нет", textArea, _s.Secondary, "s", scale,
                               TextTrimming.CharacterEllipsis);
                if (!card.Muted && _s.CachedIcon("IconFieldRelation") is { } link)
                    _s.DrawIcon(ctx, link, box.Right - ClipPadRight - IconSize, box.Y + SurfaceStyle.Snap((box.Height - IconSize) / 2, scale), _s.Secondary);
                return;
            }
            case "file":
            {
                // FileEditor в плоском режиме: ссылка/статус слева, скрепка справа (у авто-привязки
                // скрепки нет).
                var box  = Band(value, IconSize, scale);
                var auto = card.FileAutoFolder.Length > 0;
                var clipW    = auto ? 0 : ClipPadLeft + IconSize + ClipPadRight;
                var textArea = new Rect(box.X, box.Y, Math.Max(1, box.Width - clipW), box.Height);
                if (FileValue.ReadPaths(raw).Count > 1)
                    _s.FileLinks(raw, card.FileAutoFolder, textArea, TextAlignment.Left, CellVAlign.Center,
                                  0, scale, label: true).Draw(ctx);
                else
                {
                    var (text, brush, id, underline) = _s.FileLook(raw, card.FileAutoFolder);
                    DrawLabel(ctx, text, textArea, brush, id, scale, TextTrimming.CharacterEllipsis, underline);
                }
                if (!auto && _s.Paperclip is { } clip)
                    _s.DrawIcon(ctx, clip, box.Right - ClipPadRight - IconSize, box.Y, _s.Secondary);
                return;
            }
            case "date":
            {
                // DateEditor в плоском режиме: поле даты (высота 16) с текстом по центру и подсказкой
                // «дд.мм.гггг», справа иконка календаря в кнопке с отступами 4 и 8.
                var box  = Band(value, IconSize, scale);
                var text = CellDisplayText.Of("date", raw, card.NumberFormat, card.Separator);
                var textArea = new Rect(box.X, box.Y, Math.Max(1, box.Width - (ClipPadLeft + IconSize + ClipPadRight)), box.Height);
                if (text.Length > 0) DrawLabel(ctx, text, textArea, _s.Primary, "p", scale, TextTrimming.None);
                else DrawLabel(ctx, "дд.мм.гггг", textArea, _s.Secondary, "s", scale, TextTrimming.None);
                if (_s.CachedIcon("IconCalendarDate") is { } calendar)
                    _s.DrawIcon(ctx, calendar, box.Right - ClipPadRight - IconSize, box.Y, _s.Secondary);
                return;
            }
            default:
            {
                // TextBox.propValue: текст в поле высотой строки по центру, без многоточия (поле режет
                // по краю); пусто — подсказка серым; вычисляемые — серым.
                var box  = Band(value, TextBoxHeight(scale), scale);
                var text = card.Display?.Invoke() ?? (card.FieldType == "number"
                    ? NumberFormatValue.Format(raw, card.NumberFormat)
                    : TitleText.FirstLine(raw));
                if (text.Length > 0)
                    DrawLabel(ctx, text, box, card.Muted ? _s.Secondary : _s.Primary, card.Muted ? "s" : "p", scale, TextTrimming.None);
                else if (card.Placeholder.Length > 0)
                    DrawLabel(ctx, card.Placeholder, box, _s.Secondary, "s", scale, TextTrimming.None);
                return;
            }
        }
    }
}
