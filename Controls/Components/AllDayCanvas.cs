using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Controls.Components;

/// <summary>
/// Плашки полосы «весь день» («Неделя», «День»), нарисованные одним контролом поверх колонок
/// сетки полосы. Плашка — фон цвета события (обычный, наведение, выделение), у многодневной —
/// подложка цвета полосы (закрывает линии между днями), скругления только с тех краев, где у
/// события есть начало или конец, полоска у начала, название жирным с многоточием; у события со
/// временем (длиннее суток) — еще «, ЧЧ:мм» сразу за названием.
///
/// Раньше плашка собиралась из четырех–семи контролов, и неделя со многими событиями «весь день»
/// (стенд 10 млн: 175 плашек) строилась почти целиком на привязке их стилей — 70–100 мс на каждое
/// открытие недели. Здесь контрол один, плашки — записи с геометрией и состоянием.
///
/// Геометрия повторяет прежнюю раскладку: клетка плашки — колонки сетки полосы (их ширины считает
/// сама сетка, отсюда они и берутся), строка 26 px; плашка высотой 24 с отступами 1 px слева и
/// 2 px справа там, где у события край; положение округляется к пикселю, ширина — вверх; текст —
/// теми же раскладками, что строил TextBlock под свой итоговый размер.
/// </summary>
internal sealed class AllDayCanvas : Control
{
    public const double RowH  = 26.0;
    private const double BlockH = 24.0;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    /// <summary>Плашка события: место в колонках и строке, состояние рисунка, раскладки текста.</summary>
    public sealed class Item
    {
        private readonly AllDayCanvas _owner;
        private bool   _selected;
        private double _opacity = 1;

        internal readonly int  Col;
        internal readonly int  Span;
        internal readonly int  Row;
        internal readonly bool LeftCap;
        internal readonly bool RightCap;
        internal readonly IBrush Normal;
        internal readonly IBrush HoverBg;
        internal readonly IBrush Solid;
        internal readonly IBrush Dark;
        internal bool Hover;

        // Раскладки текста под ширину и цвет; пересобираются, когда они меняются.
        internal TextLayout? Title;
        internal TextLayout? Time;
        internal double      TitleW;
        internal double      LayoutWidth = -1;
        internal IBrush?     LayoutBrush;

        internal Item(AllDayCanvas owner, CalendarRecord ev, int col, int span, int row, bool leftCap, bool rightCap)
        {
            _owner   = owner;
            Ev       = ev;
            Col      = col;
            Span     = span;
            Row      = row;
            LeftCap  = leftCap;
            RightCap = rightCap;
            var color = Color.Parse(EventColors.Display(ev.Color));
            Normal  = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)).ToImmutable();
            HoverBg = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B)).ToImmutable();
            Solid   = new SolidColorBrush(color).ToImmutable();
            Dark    = new SolidColorBrush(ColorTools.Darken(color)).ToImmutable();
        }

        public CalendarRecord Ev { get; }

        /// <summary>Прямоугольник плашки в координатах поверхности.</summary>
        public Rect Bounds { get; internal set; }

        /// <summary>Выделено: сплошной фон цвета события, белый текст, без полоски.</summary>
        public bool Selected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; _owner.InvalidateVisual(); }
        }

        /// <summary>Непрозрачность плашки: 0.45 — исходное место перетаскиваемой.</summary>
        public double Opacity
        {
            get => _opacity;
            set { if (_opacity == value) return; _opacity = value; _owner.InvalidateVisual(); }
        }
    }

    private readonly List<Item> _items = [];
    private Item? _hovered;

    public IReadOnlyList<Item> Items => _items;

    public Item Add(CalendarRecord ev, int col, int span, int row, bool leftCap, bool rightCap)
    {
        var item = new Item(this, ev, col, span, row, leftCap, rightCap);
        _items.Add(item);
        InvalidateArrange();
        return item;
    }

    private static double FontSizeBase => (double)Application.Current!.FindResource("FontSizeBase")!;
    private static FontFamily Family => Application.Current?.FindResource("FontFamilyBase") as FontFamily ?? FontFamily.Default;
    private static FontWeight Weight(string key) => (FontWeight)Application.Current!.FindResource(key)!;

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var r) && r is IBrush b ? b : new SolidColorBrush(Color.Parse(fallbackHex));

    /// <summary>Дней (колонок) в полосе.</summary>
    public int Days { get; set; } = 1;

    // Клетка плашки — дни полосы по тем же границам, что делители дней и колонки сетки времени
    // (равные доли ширины, начало каждой — к пикселю). Прежние плашки стояли в клетках сетки со
    // звездными колонками, и ее округление зависело от содержимого — на широком окне плашки с
    // пятницы уезжали от линии дня на пиксель. Плашка — высотой 24 с отступом 1 px слева и 2 px
    // справа у краев события; положение — к пикселю, ширина — вверх до пикселя.
    protected override Size ArrangeOverride(Size finalSize)
    {
        var scale = LayoutHelper.GetLayoutScale(this);
        var cell  = finalSize.Width / Math.Max(1, Days);
        double Offset(int col) => LayoutHelper.RoundLayoutValue(col * cell, scale);
        foreach (var item in _items)
        {
            var cx = Offset(item.Col);
            var cw = Offset(item.Col + item.Span) - cx;
            var ml = item.LeftCap  ? 1.0 : 0.0;
            var mr = item.RightCap ? 2.0 : 0.0;
            var size = LayoutHelper.RoundLayoutSizeUp(new Size(Math.Max(0, cw - ml - mr), BlockH), scale, scale);
            var ox = cx + ml + (Math.Max(0, cw - ml - mr) - size.Width) / 2;
            item.Bounds = new Rect(LayoutHelper.RoundLayoutValue(ox, scale),
                                   LayoutHelper.RoundLayoutValue(item.Row * RowH, scale), size.Width, size.Height);
        }
        InvalidateVisual();
        return finalSize;
    }

    /// <summary>Плашка под точкой (в координатах поверхности).</summary>
    public Item? ItemAt(Point p)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
            if (_items[i].Bounds.Contains(p)) return _items[i];
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var item = ItemAt(e.GetPosition(this));
        SetHover(item);
        Cursor = item is null ? null : HandCursor;
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

    public override void Render(DrawingContext context)
    {
        var underlay = Brush("BrushSecondaryBg", "#F2F2F7");
        var bold     = Weight("FontWeightBold");
        var normal   = Weight("FontWeightNormal");
        var fontSize = FontSizeBase;
        var scale    = LayoutHelper.GetLayoutScale(this);
        foreach (var item in _items)
        {
            var r = item.Bounds;
            if (r.Width <= 0) continue;
            using var move = context.PushTransform(Matrix.CreateTranslation(r.X, r.Y));
            if (item.Opacity < 1)
            {
                using var fade = context.PushOpacity(item.Opacity);
                DrawBlock(context, item, r.Size, underlay, bold, normal, fontSize, scale);
            }
            else DrawBlock(context, item, r.Size, underlay, bold, normal, fontSize, scale);
        }
    }

    private static void DrawBlock(DrawingContext context, Item item, Size size, IBrush underlay,
                                  FontWeight bold, FontWeight normal, double fontSize, double scale)
    {
        var rect = new Rect(size);
        var l = item.LeftCap  ? 3 : 0;
        var rr = item.RightCap ? 3 : 0;
        var shape = new RoundedRect(rect, l, rr, rr, l);

        // Многодневная — на подложке цвета полосы: она закрывает линии между днями под плашкой.
        if (item.Span > 1) context.DrawRectangle(underlay, null, shape);
        var background = item.Selected ? item.Solid : item.Hover ? item.HoverBg : item.Normal;
        context.DrawRectangle(background, null, shape);

        // Полоска у начала события: рамка 3 px с отступом (2, 2, 0, 2) в первой колонке блока.
        // Видимая, она шире колонки в 4 px (3 + 2) и раздвигала ее до 5 — текст во второй колонке
        // стоит тогда на пиксель правее; спрятанная (выделение, событие без начала) — колонка 4.
        var stripShown = item.LeftCap && !item.Selected;
        if (stripShown)
            context.DrawRectangle(item.Solid, null, new RoundedRect(new Rect(2, 2, 3, BlockH - 4), new CornerRadius(2)));
        var textX = stripShown ? 5.0 : 4.0;

        // Текст — во второй колонке блока: название с отступом 4 слева, по центру по вертикали. У
        // события «весь день» название на всю ширину без 4 px справа; у события со временем —
        // название не шире «ширина колонки − (время + 4)», время сразу за ним. Ширину под обрезку
        // прежняя надпись брала с колонки при первой колонке 4 px (до того, как полоска ее
        // раздвигала), а рисовалась уже со сдвигом — так и здесь.
        var fore   = item.Selected ? Brushes.White : item.Dark;
        var colW   = Math.Max(0, size.Width - 4);
        if (item.Title is null || item.LayoutWidth != colW || !ReferenceEquals(item.LayoutBrush, fore))
        {
            var family    = Family;
            var titleFace = new Typeface(family, FontStyle.Normal, bold);
            if (item.Ev.AllDay)
            {
                var avail = Math.Max(0, colW - 8);
                var h     = Desired(Layout(item.Ev.DisplayTitle, titleFace, fontSize, fore, avail, BlockH, true)).Height;
                var w     = LayoutHelper.RoundLayoutSizeUp(new Size(avail, h), scale, scale).Width;
                item.Title  = Layout(item.Ev.DisplayTitle, titleFace, fontSize, fore, w, h, true);
                item.TitleW = w;
                item.Time   = null;
            }
            else
            {
                var timeText = $", {item.Ev.StartDt:HH:mm}";
                var time     = Layout(timeText, new Typeface(family, FontStyle.Normal, normal), fontSize, fore,
                                      double.PositiveInfinity, BlockH, false);
                var timeW    = Desired(time).Width;
                var maxTitle = Math.Max(0, colW - (timeW + 4));
                var measure  = Layout(item.Ev.DisplayTitle, titleFace, fontSize, fore, maxTitle, BlockH, true);
                var titleW   = Math.Min(Desired(measure).Width, maxTitle);
                var titleH   = Desired(measure).Height;
                item.Title  = Layout(item.Ev.DisplayTitle, titleFace, fontSize, fore, titleW, titleH, true);
                item.TitleW = titleW;
                item.Time   = Layout(timeText, new Typeface(family, FontStyle.Normal, normal), fontSize, fore,
                                     timeW, Desired(time).Height, false);
            }
            item.LayoutWidth = colW;
            item.LayoutBrush = fore;
        }

        var titleTop = LayoutHelper.RoundLayoutValue((BlockH - Math.Ceiling(item.Title.Height)) / 2, scale);
        item.Title.Draw(context, new Point(textX + 4 + item.Title.OverhangLeading, titleTop));
        if (item.Time is not null)
        {
            var timeTop = LayoutHelper.RoundLayoutValue((BlockH - Math.Ceiling(item.Time.Height)) / 2, scale);
            item.Time.Draw(context, new Point(textX + 4 + item.TitleW + item.Time.OverhangLeading, timeTop));
        }
    }

    private static TextLayout Layout(string text, Typeface face, double fontSize, IBrush brush,
                                     double maxWidth, double maxHeight, bool trim) =>
        new(text, face, fontSize, brush, TextAlignment.Left, TextWrapping.NoWrap,
            trim ? TextTrimming.CharacterEllipsis : null, null, FlowDirection.LeftToRight,
            maxWidth, maxHeight, double.NaN, 0, 0);

    // Размер надписи как у TextBlock: ширина и высота текста вверх до целого DIP.
    private static Size Desired(TextLayout l) =>
        new(Math.Ceiling(l.OverhangLeading + l.WidthIncludingTrailingWhitespace + l.OverhangTrailing),
            Math.Ceiling(l.Height));
}
