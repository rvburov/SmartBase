using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering;
using SmartBase.Controls.Behaviors;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Views.ConstructorDb.Calendar;

// Рисованный «Месяц»: ячейки с плашками однодневных событий (MonthCellsSurface) и полосы
// многодневных поверх линий сетки (MonthBarsSurface). Раньше ячейка собиралась из восьми
// контролов (рамка, сетки, кружок числа, надпись, значок «+», подпись «+N еще»), плашка — из
// шести, полоса — из семи: месяц на стенде 10 млн строил 1,5–2 тысячи контролов, и построение
// (90–240 мс, а по приходе чисел дней — еще раз) почти целиком уходило на привязку их стилей.
// Здесь два контрола на месяц; раскладку (строки полос, свободные строки плашек, «+N еще»)
// по-прежнему считает MonthView, поверхности только рисуют и находят плашку под указателем.
// Геометрия повторяет прежние контролы: ячейка — клетка равной сетки 7 × 6 (начало к пикселю,
// размер вверх до пикселя), содержимое обрезано ячейкой; плашка — высотой 20 с отступом 2 по
// бокам, полоса — на холсте с дробными краями (начало к пикселю, ширина вверх); текст — теми же
// раскладками, что строил TextBlock под свой итоговый размер.

/// <summary>Плашка события в «Месяце» — однодневная в ячейке или сегмент полосы многодневной.</summary>
internal sealed class MonthChip
{
    internal MonthChip(CalendarRecord ev, bool bar, bool leftCap, bool rightCap)
    {
        Ev       = ev;
        IsBar    = bar;
        LeftCap  = leftCap;
        RightCap = rightCap;
        var color = Color.Parse(EventColors.Display(ev.Color));
        Normal  = new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B)).ToImmutable();
        HoverBg = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B)).ToImmutable();
        Solid   = new SolidColorBrush(color).ToImmutable();
        Dark    = new SolidColorBrush(ColorTools.Darken(color)).ToImmutable();
    }

    public CalendarRecord Ev { get; }
    public bool IsBar { get; }
    internal readonly bool   LeftCap;
    internal readonly bool   RightCap;
    internal readonly IBrush Normal;
    internal readonly IBrush HoverBg;
    internal readonly IBrush Solid;
    internal readonly IBrush Dark;

    /// <summary>Прямоугольник в координатах сетки месяца (обе поверхности лежат в ее начале).</summary>
    public Rect Bounds { get; internal set; }

    /// <summary>0.45 — исходное место перетаскиваемой плашки.</summary>
    public double Opacity { get; set; } = 1;

    // Раскладки текста под ширину и цвет.
    internal TextLayout? Title;
    internal TextLayout? Time;
    internal double      TimeW;
    internal double      LayoutWidth = -1;
    internal IBrush?     LayoutBrush;

    // Плашка: фон (у полосы — на белой подложке, закрывающей линии сетки), полоска у начала,
    // текст «ЧЧ:мм название» или название «весь день». size — размер плашки, rightPad — поле
    // текста справа (2 у плашки ячейки, 4 у полосы).
    internal void Draw(DrawingContext context, Size size, bool selected, bool hover, double rightPad,
                       FontWeight bold, FontWeight normal, double fontSize, FontFamily family, double scale)
    {
        var rect = new Rect(size);
        var r    = IsBar ? 4.0 : 4.0;
        var shape = IsBar
            ? new RoundedRect(rect, LeftCap ? r : 0, RightCap ? r : 0, RightCap ? r : 0, LeftCap ? r : 0)
            : new RoundedRect(rect, new CornerRadius(r));
        if (IsBar) context.DrawRectangle(Brushes.White, null, shape);
        context.DrawRectangle(selected ? Solid : hover ? HoverBg : Normal, null, shape);
        if (!selected && (!IsBar || LeftCap))
            context.DrawRectangle(Solid, null, new RoundedRect(new Rect(2, 2, 3, size.Height - 4), new CornerRadius(2)));

        var fore  = selected ? Brushes.White : Dark;
        var avail = Math.Max(0, size.Width - 8 - rightPad);
        if (Title is null || LayoutWidth != avail || !ReferenceEquals(LayoutBrush, fore))
        {
            var titleFace = new Typeface(family, FontStyle.Normal, bold);
            if (Ev.AllDay)
            {
                var h = Math.Ceiling(MonthText.Layout(Ev.DisplayTitle, titleFace, fontSize, fore, avail, size.Height, true).Height);
                Title = MonthText.Layout(Ev.DisplayTitle, titleFace, fontSize, fore,
                                         LayoutHelper.RoundLayoutSizeUp(new Size(avail, h), scale, scale).Width, h, true);
                Time  = null;
                TimeW = 0;
            }
            else
            {
                // Сетка [время Auto][название *]: время по содержимому, название — в остатке.
                var timeText = $"{Ev.StartDt:HH:mm} ";
                var timeFace = new Typeface(family, FontStyle.Normal, normal);
                var measure  = MonthText.Layout(timeText, timeFace, fontSize, fore, double.PositiveInfinity, double.PositiveInfinity, false);
                var ts       = MonthText.Desired(measure);
                TimeW = ts.Width;
                Time  = MonthText.Layout(timeText, timeFace, fontSize, fore, ts.Width, ts.Height, false);
                var titleW = Math.Max(0, avail - TimeW);
                var h = Math.Ceiling(MonthText.Layout(Ev.DisplayTitle, titleFace, fontSize, fore, titleW, double.PositiveInfinity, true).Height);
                Title = MonthText.Layout(Ev.DisplayTitle, titleFace, fontSize, fore, titleW, h, true);
            }
            LayoutWidth = avail;
            LayoutBrush = fore;
        }

        var top = LayoutHelper.RoundLayoutValue((size.Height - Math.Ceiling(Title.Height)) / 2, scale);
        if (Time is not null)
        {
            Time.Draw(context, new Point(8 + Time.OverhangLeading, top));
            Title.Draw(context, new Point(8 + TimeW + Title.OverhangLeading, top));
        }
        else Title.Draw(context, new Point(8 + Title.OverhangLeading, top));
    }
}

internal static class MonthText
{
    internal static TextLayout Layout(string text, Typeface face, double fontSize, IBrush brush,
                                      double maxWidth, double maxHeight, bool trim,
                                      TextAlignment align = TextAlignment.Left) =>
        new(text, face, fontSize, brush, align, TextWrapping.NoWrap,
            trim ? TextTrimming.CharacterEllipsis : null, null, FlowDirection.LeftToRight,
            maxWidth, maxHeight, double.NaN, 0, 0);

    // Размер надписи как у TextBlock: ширина и высота текста вверх до целого DIP.
    internal static Size Desired(TextLayout l) =>
        new(Math.Ceiling(l.OverhangLeading + l.WidthIncludingTrailingWhitespace + l.OverhangTrailing),
            Math.Ceiling(l.Height));

    internal static double FontSizeBase => (double)Application.Current!.FindResource("FontSizeBase")!;
    internal static FontFamily Family => Application.Current?.FindResource("FontFamilyBase") as FontFamily ?? FontFamily.Default;
    internal static FontWeight Weight(string key) => (FontWeight)Application.Current!.FindResource(key)!;
}

/// <summary>Ячейка дня «Месяца»: число, плашки однодневных, «+N еще».</summary>
internal sealed class MonthCell
{
    public DateTime Date;
    public bool IsCurrentMonth;
    public bool IsToday;
    public bool IsWeekend;
    public int  Index;                    // 0–41 в сетке 7 × 6
    public readonly List<(MonthChip Chip, int Row)> Chips = [];
    public HashSet<int> Occupied = [];    // строки, занятые полосами и плашками
    public int  OrigHidden;               // «+N еще» по раскладке
    public int? More;                     // показанный «+N еще» (null — не показывать)
    public Rect Bounds;                   // клетка в координатах сетки
}

/// <summary>Ячейки «Месяца» с плашками однодневных событий — один контрол вместо ~340 и плашек.</summary>
internal sealed class MonthCellsSurface : Control
{
    private static readonly IBrush White      = new SolidColorBrush(Color.Parse("#FFFFFF")).ToImmutable();
    private static readonly IBrush OtherMonth = new SolidColorBrush(Color.Parse("#F9F9F9")).ToImmutable();
    private static readonly IBrush HoverCell  = new SolidColorBrush(Color.Parse("#E5F0FF")).ToImmutable();
    private static readonly IBrush HoverCircle = new SolidColorBrush(Color.Parse("#F2F2F7")).ToImmutable();
    private static readonly IBrush HoverCircleLine = new SolidColorBrush(Color.Parse("#E8E8E8")).ToImmutable();
    private static readonly IBrush Red        = new SolidColorBrush(Color.Parse("#FF3B30")).ToImmutable();
    private static readonly IBrush DayText    = new SolidColorBrush(Color.Parse("#1C1C1E")).ToImmutable();
    private static readonly IBrush OtherText  = new SolidColorBrush(Color.Parse("#8E8E93")).ToImmutable();
    private static readonly IBrush PlusStroke = new SolidColorBrush(Color.Parse("#636366")).ToImmutable();
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    public const double DateH = 34, BarH = 20, BarStep = 22, MoreH = 16, BPad = 4;

    private readonly Dictionary<(string, IBrush, FontWeight, double), (TextLayout Measure, Size Size)> _labels = new();

    public List<MonthCell> Cells { get; } = [];

    /// <summary>Выделено ли событие (набор выделенных держит вид).</summary>
    public Func<CalendarRecord, bool> IsSelected { get; set; } = _ => false;

    public DateTime? HoveredDay;          // ячейка под указателем (подсветка — у дней этого месяца)
    public DateTime? HoveredCircleDay;    // кружок числа под указателем — «+»
    public MonthChip? HoveredChip;

    /// <summary>Клетка ячейки i — как у прежней равной сетки 7 × 6 (начало к пикселю, размер вверх).</summary>
    public Rect CellRect(int i, Size size)
    {
        var scale = LayoutHelper.GetLayoutScale(this);
        var cw = size.Width / 7.0;
        var ch = size.Height / 6.0;
        var s  = LayoutHelper.RoundLayoutSizeUp(new Size(cw, ch), scale, scale);
        return new Rect(LayoutHelper.RoundLayoutValue(i % 7 * cw, scale), LayoutHelper.RoundLayoutValue(i / 7 * ch, scale),
                        s.Width, s.Height);
    }

    /// <summary>Разложить ячейки и плашки под размер сетки.</summary>
    public void Relayout(Size size)
    {
        var scale = LayoutHelper.GetLayoutScale(this);
        foreach (var cell in Cells)
        {
            cell.Bounds = CellRect(cell.Index, size);
            var chipW = LayoutHelper.RoundLayoutSizeUp(new Size(Math.Max(0, cell.Bounds.Width - 4), BarH), scale, scale).Width;
            foreach (var (chip, row) in cell.Chips)
                chip.Bounds = new Rect(cell.Bounds.X + LayoutHelper.RoundLayoutValue(2, scale),
                                       cell.Bounds.Y + LayoutHelper.RoundLayoutValue(DateH + row * BarStep, scale), chipW, BarH);
        }
        InvalidateVisual();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Relayout(finalSize);
        return finalSize;
    }

    /// <summary>Плашка под точкой (координаты поверхности).</summary>
    public MonthChip? ChipAt(Point p)
    {
        foreach (var cell in Cells)
        {
            if (!cell.Bounds.Contains(p)) continue;
            for (var i = cell.Chips.Count - 1; i >= 0; i--)
                if (cell.Chips[i].Chip.Bounds.Contains(p)) return cell.Chips[i].Chip;
            return null;
        }
        return null;
    }

    /// <summary>Ячейка под точкой.</summary>
    public MonthCell? CellAt(Point p) => Cells.FirstOrDefault(c => c.Bounds.Contains(p));

    // Кружок числа: 24 × 24 у правого верхнего угла ячейки с отступами 4.
    public Rect CircleRect(MonthCell cell) =>
        new(cell.Bounds.X + LayoutHelper.RoundLayoutValue(cell.Bounds.Width - 28, LayoutHelper.GetLayoutScale(this)),
            cell.Bounds.Y + 4, 24, 24);

    public override void Render(DrawingContext context)
    {
        var scale    = LayoutHelper.GetLayoutScale(this);
        var bold     = MonthText.Weight("FontWeightBold");
        var normal   = MonthText.Weight("FontWeightNormal");
        var fontSize = MonthText.FontSizeBase;
        var family   = MonthText.Family;
        foreach (var cell in Cells)
        {
            var r = cell.Bounds;
            using var clip = context.PushClip(r);   // содержимое обрезано ячейкой (ClipToBounds)
            var bg = cell.IsCurrentMonth && HoveredDay == cell.Date ? HoverCell : cell.IsCurrentMonth ? White : OtherMonth;
            context.FillRectangle(bg, r);

            // Кружок числа: сегодня — красный с белым жирным числом; под указателем — серый
            // с обводкой и «+» вместо числа.
            var circle = CircleRect(cell);
            var plus   = HoveredCircleDay == cell.Date;
            if (plus)
            {
                var t = LineMetrics.ThicknessFor(this);
                context.DrawRectangle(HoverCircle, new Pen(HoverCircleLine, t), new RoundedRect(circle.Deflate(t / 2), new CornerRadius(12 - t / 2)));
                DrawPlus(context, circle, t);
            }
            else
            {
                if (cell.IsToday) context.DrawRectangle(Red, null, new RoundedRect(circle, new CornerRadius(12)));
                var fore = cell.IsToday ? Brushes.White : cell.IsWeekend ? Red : cell.IsCurrentMonth ? DayText : OtherText;
                var weight = cell.IsToday ? bold : normal;
                var text = cell.Date.Day.ToString();
                if (!_labels.TryGetValue((text, fore, weight, fontSize), out var lbl))
                {
                    var m = MonthText.Layout(text, new Typeface(family, FontStyle.Normal, weight), fontSize, fore, 24, 24, false, TextAlignment.Center);
                    var h = MonthText.Desired(m).Height;
                    lbl = (MonthText.Layout(text, new Typeface(family, FontStyle.Normal, weight), fontSize, fore, 24, h, false, TextAlignment.Center),
                           new Size(24, h));
                    _labels[(text, fore, weight, fontSize)] = lbl;
                }
                var top = LayoutHelper.RoundLayoutValue((24 - lbl.Size.Height) / 2, scale);
                lbl.Measure.Draw(context, new Point(circle.X + lbl.Measure.OverhangLeading, circle.Y + top));
            }

            foreach (var (chip, _) in cell.Chips)
            {
                var cr = chip.Bounds;
                using var move = context.PushTransform(Matrix.CreateTranslation(cr.X, cr.Y));
                if (chip.Opacity < 1)
                {
                    using var fade = context.PushOpacity(chip.Opacity);
                    chip.Draw(context, cr.Size, IsSelected(chip.Ev), ReferenceEquals(HoveredChip, chip), 2, bold, normal, fontSize, family, scale);
                }
                else chip.Draw(context, cr.Size, IsSelected(chip.Ev), ReferenceEquals(HoveredChip, chip), 2, bold, normal, fontSize, family, scale);
            }

            // «+N еще» — прижата к нижнему краю ячейки, отступ 6 слева и 4 снизу.
            if (cell.More is { } more && more > 0)
            {
                var text = $"+{more} еще";
                var m    = MonthText.Layout(text, new Typeface(family), fontSize, OtherText, double.PositiveInfinity, double.PositiveInfinity, false);
                var ds   = MonthText.Desired(m);
                var l    = MonthText.Layout(text, new Typeface(family), fontSize, OtherText, ds.Width, ds.Height, false);
                l.Draw(context, new Point(r.X + LayoutHelper.RoundLayoutValue(6, scale) + l.OverhangLeading,
                                          r.Y + LayoutHelper.RoundLayoutValue(r.Height - 4 - ds.Height, scale)));
            }
        }
    }

    // «+» — как в кнопке «Создать» сайдбара: контур значка 24 × 24 в рамке 18 × 18 по центру кружка.
    private void DrawPlus(DrawingContext context, Rect circle, double border)
    {
        if (Application.Current?.FindResource("IconPlus") is not Geometry plus) return;
        var stroke = Application.Current?.FindResource("IconStrokeMediumBold") is double ps ? ps : 2;
        var size   = Application.Current?.FindResource("IconSizeMedium") is double sz ? sz : 18d;
        var k      = size / 24.0;
        var at     = new Point(circle.X + (24 - size) / 2, circle.Y + (24 - size) / 2);
        using var move = context.PushTransform(Matrix.CreateScale(k, k) * Matrix.CreateTranslation(at.X, at.Y));
        context.DrawGeometry(null, new Pen(PlusStroke, stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), plus);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p    = e.GetPosition(this);
        var chip = ChipAt(p);
        if (!ReferenceEquals(chip, HoveredChip)) { HoveredChip = chip; InvalidateVisual(); }
        var cell = CellAt(p);
        Cursor = chip is not null || (cell is not null && CircleRect(cell).Contains(p)) ? HandCursor : null;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (HoveredChip is not null) { HoveredChip = null; InvalidateVisual(); }
        Cursor = null;
    }
}

/// <summary>Полосы многодневных событий «Месяца» поверх линий сетки — один контрол. Ловит
/// указатель только над полосами: мимо них нажатия уходят к ячейкам.</summary>
internal sealed class MonthBarsSurface : Control, ICustomHitTest
{
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    public List<MonthChip> Bars { get; } = [];

    /// <summary>Выделено ли событие — подсвечиваются все его сегменты.</summary>
    public Func<CalendarRecord, bool> IsSelected { get; set; } = _ => false;

    /// <summary>Событие под указателем (по номеру записи) — подсвечиваются все его сегменты.</summary>
    public int? HoveredId;

    public MonthChip? BarAt(Point p)
    {
        for (var i = Bars.Count - 1; i >= 0; i--)
            if (Bars[i].Bounds.Contains(p)) return Bars[i];
        return null;
    }

    public bool HitTest(Point point) => BarAt(point) is not null;

    /// <summary>Добавить полосу: левый край, верх, ширина — как у прежнего контрола на холсте
    /// (начало к пикселю, ширина вверх до пикселя).</summary>
    public MonthChip Add(CalendarRecord ev, double x, double y, double w, bool leftCap, bool rightCap)
    {
        var scale = LayoutHelper.GetLayoutScale(this);
        var bar   = new MonthChip(ev, bar: true, leftCap, rightCap);
        var size  = LayoutHelper.RoundLayoutSizeUp(new Size(w, MonthCellsSurface.BarH), scale, scale);
        bar.Bounds = new Rect(LayoutHelper.RoundLayoutValue(x, scale), LayoutHelper.RoundLayoutValue(y, scale), size.Width, size.Height);
        Bars.Add(bar);
        InvalidateVisual();
        return bar;
    }

    public override void Render(DrawingContext context)
    {
        var scale    = LayoutHelper.GetLayoutScale(this);
        var bold     = MonthText.Weight("FontWeightBold");
        var normal   = MonthText.Weight("FontWeightNormal");
        var fontSize = MonthText.FontSizeBase;
        var family   = MonthText.Family;
        foreach (var bar in Bars)
        {
            var r = bar.Bounds;
            var selected = IsSelected(bar.Ev);
            var hover    = !selected && bar.Ev.Id is { } id && HoveredId == id;
            using var move = context.PushTransform(Matrix.CreateTranslation(r.X, r.Y));
            if (bar.Opacity < 1)
            {
                using var fade = context.PushOpacity(bar.Opacity);
                bar.Draw(context, r.Size, selected, hover, 4, bold, normal, fontSize, family, scale);
            }
            else bar.Draw(context, r.Size, selected, hover, 4, bold, normal, fontSize, family, scale);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var bar = BarAt(e.GetPosition(this));
        var id  = bar?.Ev.Id;
        if (id != HoveredId) { HoveredId = id; InvalidateVisual(); }
        Cursor = bar is null ? null : HandCursor;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (HoveredId is not null) { HoveredId = null; InvalidateVisual(); }
        Cursor = null;
    }
}
