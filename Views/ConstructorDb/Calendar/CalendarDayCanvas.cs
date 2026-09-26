using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Views.ConstructorDb.Calendar;

/// <summary>
/// Колонка дня в «Неделе» и «Дне»: все события дня нарисованы одним контролом. Блок события —
/// фон по цвету события (обычный, наведение, выделение), полоска слева, название жирным (перенос,
/// не больше помещающихся строк, многоточие) и время «ЧЧ:мм–ЧЧ:мм», если под названием есть место.
///
/// Раньше блок был контролом на событие (до этапа 8 — девятью), и на неделе с днями-гигантами
/// (стенд 10 млн: около 1270 событий в день, рисуются первые шесть подколонок — тысяча-полторы
/// блоков на неделю) построение вида почти целиком уходило на привязку стилей каждого нового
/// контрола и на сборку мусора после них: 150–180 мс на каждое открытие недели. Здесь контрол один
/// на колонку, блоки — записи с геометрией и состоянием.
///
/// Геометрия повторяет прежние контролы на холсте: левый край подколонки и верх округляются к
/// пикселю, ширина и высота — вверх до пикселя (так холст раскладывал блок с дробной шириной);
/// поле текста справа — 3 px при целой ширине и 2 px при дробной. Наведение — у верхнего блока
/// под указателем, как у контролов: он же дает курсор (растянуть у краев, рука внутри). Нажатия
/// разбирает сетка вида по геометрии записей, как и прежде; здесь только меню правой кнопкой —
/// меню блока, а мимо блоков нажатие уходит к меню пустого места сетки.
/// </summary>
internal sealed class CalendarDayCanvas : Control
{
    // Высота строки названия и места под время — те же числа, что считал вид для надписей.
    private const double LineH    = 16.0;
    private const double Margins  = 4.0;
    private const double TimeH    = 15.0;
    private const double TextLeft = 9;

    /// <summary>Зона растяжения у верхнего и нижнего края блока, px.</summary>
    public const double EdgeZone = 6;

    private static readonly Cursor HandCursor   = new(StandardCursorType.Hand);
    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeNorthSouth);

    // Кисти по цвету события — одни на все блоки этого цвета.
    internal sealed record Palette(IBrush Normal, IBrush Hover, IBrush Solid, IBrush Dark);
    private static readonly Dictionary<string, Palette> Palettes = new(StringComparer.Ordinal);

    /// <summary>Блок события колонки: место в подколонках, состояние рисунка и раскладки текста.</summary>
    public sealed class Item
    {
        private readonly CalendarDayCanvas _owner;
        private bool   _selected;
        private double _opacity = 1;

        internal readonly double  Top;
        internal readonly double  Height;
        internal readonly int     SubCol;
        internal readonly int     SubColSpan;
        internal readonly int     TotalCols;
        internal readonly string  Title;
        internal readonly string? Time;
        internal readonly int     TitleMaxLines;
        internal readonly double  FontSize;
        internal readonly Typeface TitleFace;
        internal readonly Typeface TimeFace;
        internal readonly Palette Colors;

        internal bool        Hover;
        internal double      RawWidth;          // ширина до округления — от нее поле текста справа
        internal TextLayout? TitleLayout;
        internal TextLayout? TimeLayout;
        internal double      LayoutWidth = -1;
        internal IBrush?     LayoutBrush;
        internal ContextMenu? Menu;

        internal Item(CalendarDayCanvas owner, CalendarRecord ev, double top, double height,
                      int subCol, int subColSpan, int totalCols, double fontSize, FontWeight titleWeight)
        {
            _owner     = owner;
            Ev         = ev;
            Top        = top;
            Height     = height;
            SubCol     = subCol;
            SubColSpan = subColSpan;
            TotalCols  = totalCols;

            var maxTitleLines = (int)((height - Margins - TimeH) / LineH);
            var showTime      = maxTitleLines >= 1;
            TitleMaxLines     = showTime ? maxTitleLines : Math.Max(1, (int)((height - Margins) / LineH));
            Title             = ev.DisplayTitle;
            Time              = showTime ? $"{ev.StartDt:HH:mm}–{ev.EndDt:HH:mm}" : null;
            FontSize          = fontSize;

            var family = Application.Current?.FindResource("FontFamilyBase") as FontFamily ?? FontFamily.Default;
            TitleFace  = new Typeface(family, FontStyle.Normal, titleWeight);
            TimeFace   = new Typeface(family);
            Colors     = PaletteOf(EventColors.Display(ev.Color));
        }

        public CalendarRecord Ev { get; }

        /// <summary>Колонка, в которой нарисован блок.</summary>
        public CalendarDayCanvas Owner => _owner;

        /// <summary>Запись вида, которой принадлежит блок.</summary>
        public object? Tag { get; set; }

        /// <summary>Прямоугольник блока в координатах колонки — как Bounds прежнего контрола.</summary>
        public Rect Bounds { get; internal set; }

        /// <summary>Выделено: сплошной фон цвета события и белый текст.</summary>
        public bool Selected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; _owner.InvalidateVisual(); }
        }

        /// <summary>Непрозрачность блока: 0.45 — приглушен, 0 — спрятан на время растяжения.</summary>
        public double Opacity
        {
            get => _opacity;
            set { if (_opacity == value) return; _opacity = value; _owner.InvalidateVisual(); }
        }
    }

    private readonly List<Item> _items = [];
    private Item? _hovered;
    private ScrollViewer? _scroll;   // прокрутка часов: рисуются только блоки в ее видимой части

    public CalendarDayCanvas()
    {
        // Меню правой кнопкой — до разбора нажатия сеткой (как у прежнего блока — туннелем).
        AddHandler(PointerPressedEvent, OnTunnelPressed, RoutingStrategies.Tunnel);
    }

    public IReadOnlyList<Item> Items => _items;

    // Колонка — во всю высоту суток (1440 px), а видна в прокрутке примерно половина: блоки
    // вне видимой части не раскладывают текст и не рисуются (на неделе с днями-гигантами это
    // сотни раскладок текста на каждый показ). Прокрутка перерисовывает колонку.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroll = this.FindAncestorOfType<ScrollViewer>();
        if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
        _scroll = null;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    // Видимая часть колонки с запасом в полэкрана по вертикали; без прокрутки — вся колонка.
    private Rect VisibleArea()
    {
        var all = new Rect(Bounds.Size);
        if (_scroll is null || _scroll.TranslatePoint(new Point(0, 0), this) is not { } at) return all;
        var view = new Rect(at, _scroll.Viewport);
        return view.Inflate(new Thickness(0, view.Height / 2)).Intersect(all);
    }

    /// <summary>Меню блока — строится при первом правом нажатии по нему.</summary>
    public Func<Item, ContextMenu>? MenuFactory { get; set; }

    /// <summary>Правое нажатие по блоку — перед открытием его меню (вид выделяет событие).</summary>
    public event Action<Item>? ItemRightPressed;

    public Item Add(CalendarRecord ev, double top, double height, int subCol, int subColSpan, int totalCols,
                    double fontSize, FontWeight titleWeight)
    {
        var item = new Item(this, ev, top, height, subCol, subColSpan, totalCols, fontSize, titleWeight);
        _items.Add(item);
        InvalidateArrange();
        return item;
    }

    private static Palette PaletteOf(string colorKey)
    {
        if (Palettes.TryGetValue(colorKey, out var hit)) return hit;
        var color = Color.Parse(colorKey);
        var palette = new Palette(
            new SolidColorBrush(ColorTools.BlendWithWhite(color, 40)).ToImmutable(),
            new SolidColorBrush(ColorTools.BlendWithWhite(color, 90)).ToImmutable(),
            new SolidColorBrush(color).ToImmutable(),
            new SolidColorBrush(ColorTools.Darken(color)).ToImmutable());
        Palettes[colorKey] = palette;
        return palette;
    }

    // Раскладка блоков по ширине колонки: подколонка — доля ширины, отступ 1 px слева и 3 px на
    // зазор справа. Прежний блок стоял на холсте с дробными левым краем и шириной: холст
    // округлял край к пикселю, а ширину — вверх до пикселя.
    protected override Size ArrangeOverride(Size finalSize)
    {
        var scale = LayoutHelper.GetLayoutScale(this);
        foreach (var item in _items)
        {
            var seg  = finalSize.Width / item.TotalCols;
            var left = item.SubCol * seg + 1.0;
            var w    = Math.Max(1, item.SubColSpan * seg - 3.0);
            var size = LayoutHelper.RoundLayoutSizeUp(new Size(w, item.Height), scale, scale);
            item.RawWidth = w;
            item.Bounds   = new Rect(LayoutHelper.RoundLayoutValue(left, scale),
                                     LayoutHelper.RoundLayoutValue(item.Top, scale), size.Width, size.Height);
        }
        InvalidateVisual();
        return finalSize;
    }

    // Верхний блок под точкой — как у контролов: позже добавленный лежит выше.
    private Item? ItemAt(Point p)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
            if (_items[i].Opacity > 0 && _items[i].Bounds.Contains(p)) return _items[i];
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p    = e.GetPosition(this);
        var item = ItemAt(p);
        SetHover(item);
        if (item is null) { Cursor = null; return; }
        var y = p.Y - item.Bounds.Y;
        Cursor = y < EdgeZone || y >= item.Bounds.Height - EdgeZone ? ResizeCursor : HandCursor;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(null);
        Cursor = null;
    }

    private void SetHover(Item? item)
    {
        if (ReferenceEquals(item, _hovered)) return;
        if (_hovered is not null) _hovered.Hover = false;
        _hovered = item;
        if (item is not null) item.Hover = true;
        InvalidateVisual();
    }

    private void OnTunnelPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        var item = ItemAt(e.GetPosition(this));
        if (item is null) { ContextMenu = null; return; }   // мимо блоков — меню пустого места сетки
        item.Menu ??= MenuFactory?.Invoke(item);
        ContextMenu = item.Menu;
        ItemRightPressed?.Invoke(item);
    }

    public override void Render(DrawingContext context)
    {
        // Прозрачный фон — колонка ловит нажатия всей площадью, как прежний холст.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var visible = VisibleArea();
        foreach (var item in _items)
        {
            var r = item.Bounds;
            if (item.Opacity <= 0 || r.Width <= 0 || r.Height <= 0 || !r.Intersects(visible)) continue;
            using var move = context.PushTransform(Matrix.CreateTranslation(r.X, r.Y));
            using var clip = context.PushClip(new Rect(r.Size));
            if (item.Opacity < 1)
            {
                using var fade = context.PushOpacity(item.Opacity);
                DrawBlock(context, item, new Rect(r.Size));
            }
            else DrawBlock(context, item, new Rect(r.Size));
        }
    }

    private static void DrawBlock(DrawingContext context, Item item, Rect rect)
    {
        // Как Border со скруглением и ClipToBounds: все содержимое обрезано скругленным краем
        // (сглаживание угла ложится дважды — край фона чуть светлее, как у прежнего блока).
        using var round = context.PushClip(new RoundedRect(rect, 3));

        var colors     = item.Colors;
        var background = item.Selected ? colors.Solid : item.Hover ? colors.Hover : colors.Normal;
        context.DrawRectangle(background, null, rect, 3, 3);
        context.DrawRectangle(colors.Solid, null,
                              new RoundedRect(new Rect(2, 3, 3, Math.Max(0, rect.Height - 6)), new CornerRadius(2)));

        // Поле текста: 9 px слева; справа 3 px при целой ширине и 2 px при дробной (ширина
        // округлялась вверх до пикселя, и поле текста получало этот остаток).
        var textRight  = Math.Abs(item.RawWidth - Math.Round(item.RawWidth)) < 1e-6 ? 3 : 2;
        var foreground = item.Selected ? Brushes.White : colors.Dark;
        var width      = Math.Max(0, rect.Width - TextLeft - textRight);
        if (item.TitleLayout is null || item.LayoutWidth != width || !ReferenceEquals(item.LayoutBrush, foreground))
        {
            item.TitleLayout?.Dispose();
            item.TimeLayout?.Dispose();
            item.TitleLayout = new TextLayout(item.Title, item.TitleFace, item.FontSize, foreground, TextAlignment.Left,
                                              TextWrapping.Wrap, TextTrimming.CharacterEllipsis, null,
                                              FlowDirection.LeftToRight, width, double.PositiveInfinity,
                                              double.NaN, 0, item.TitleMaxLines);
            item.TimeLayout = item.Time is null ? null
                : new TextLayout(item.Time, item.TimeFace, item.FontSize, foreground, TextAlignment.Left,
                                 TextWrapping.NoWrap, null, null, FlowDirection.LeftToRight,
                                 width, double.PositiveInfinity, double.NaN, 0, 0);
            item.LayoutWidth = width;
            item.LayoutBrush = foreground;
        }

        // Текст — в своем поле: время без переноса в узком блоке обрезается у правого поля, как
        // обрезалась прежняя надпись по своей границе.
        using var field = context.PushClip(new Rect(TextLeft, 0, width, rect.Height));
        // Как TextBlock: раскладка рисуется со сдвигом на выступ первого глифа (OverhangLeading).
        item.TitleLayout.Draw(context, new Point(TextLeft + item.TitleLayout.OverhangLeading, 2));
        // Надпись занимает целые DIP (раскладка округляет ее высоту вверх) — время ниже на 1.
        if (item.TimeLayout is not null)
            item.TimeLayout.Draw(context, new Point(TextLeft + item.TimeLayout.OverhangLeading,
                                                    2 + Math.Ceiling(item.TitleLayout.Height) + 1));
    }
}
