using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using SmartBase.Core.Calendar;
using SmartBase.Utils;

namespace SmartBase.Views.ConstructorDb.Calendar;

/// <summary>
/// Месяц вида «Год», нарисованный одним контролом: название, строка дней недели и 42 числа —
/// подсветка дней с событиями (четыре ступени по числу событий), кружок сегодняшнего дня, серые
/// числа соседних месяцев, красные выходные в заголовке.
///
/// Раньше месяц собирался из контролов: надпись на каждое число, рамка под ней и прозрачная
/// рамка нажатия поверх — около 140 на месяц, 1650 на год. Построение «Года» почти целиком
/// уходило на привязку стилей каждого нового контрола и на сборку мусора после них (стенд
/// Tests/Sections, база 10 млн событий: 140–290 мс, и столько же еще раз, когда приходили числа
/// дней). Здесь контрол один на месяц, стилей у него нет, а раскладки текста общие на все
/// месяцы.
///
/// Геометрия повторяет прежнюю раскладку до пикселя (кадры cal-year-x1/x2 стенда): каждое
/// положение считается в координатах своего прежнего родителя и округляется, как округляла
/// раскладка Avalonia (к ближайшему пикселю, половина — к четному); размер надписи — ширина и
/// высота текста вверх до целого DIP, как у TextBlock; текст рисуется той же раскладкой, что
/// строил TextBlock под свой итоговый размер, со сдвигом на выступ первого глифа.
/// </summary>
internal sealed class YearMonthCard : Control
{
    // Прежняя раскладка: карточка 220 × 200 (название 32 + сетка 7 строк по 24), сетка шириной
    // 7 × 31 = 217 по центру карточки; ячейка 31 × 24; число — в рамке 22 × 22 по центру ячейки
    // (сегодня — круг, день с событиями — плашка на всю ячейку без 1 px по бокам).
    public const double CardW  = 220;
    public const double TitleH = 32;
    public const double Dch    = 24;
    public const int    CellW  = 31;
    public const int    GridW  = 7 * CellW;
    private const double Box   = 22;

    private static readonly string[] MonthNames =
        ["Январь", "Февраль", "Март", "Апрель", "Май", "Июнь",
         "Июль",   "Август",  "Сентябрь", "Октябрь", "Ноябрь", "Декабрь"];
    private static readonly string[] DayNames = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];

    // Подсветка дня по числу событий: 1, 2–5, 6–8, 9 и больше.
    private static readonly (IBrush Bg, IBrush Text)[] Steps =
    [
        (Solid(Color.FromArgb(40, 255, 204,   0)), Solid(Color.FromArgb(255, 140, 100,   0))),
        (Solid(Color.FromArgb(40, 255, 149,   0)), Solid(Color.FromArgb(255, 160,  70,   0))),
        (Solid(Color.FromArgb(40, 255,  90, 140)), Solid(Color.FromArgb(255, 180,  30,  80))),
        (Solid(Color.FromArgb(40, 255,  59,  48)), Solid(Color.FromArgb(255, 160,  20,  10))),
    ];
    private static readonly IBrush OtherMonth = Solid(Color.Parse("#E5E5EA"));
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private static IBrush Solid(Color c) => new SolidColorBrush(c).ToImmutable();

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var r) && r is IBrush b ? b : Solid(Color.Parse(fallbackHex));

    // Раскладки текста — общие на все месяцы: число «12» тем же цветом и шрифтом одинаково в любом
    // месяце. Ключ несет размер шрифта — смена масштаба текста строит новые.
    private sealed record TextKey(string Text, double FontSize, FontWeight Weight, IBrush Brush, TextAlignment Align, double MaxWidth, double MaxHeight);
    private static readonly Dictionary<TextKey, TextLayout> Layouts = new();

    /// <summary>Дни месяца, по которым можно нажать (числа текущего месяца, при 5-дневке — без
    /// выходных): нажатие открывает список событий дня под ячейкой.</summary>
    public event Action<YearMonthCard, Rect, DateTime>? DayTapped;

    private readonly int _year;
    private readonly int _month;
    private IReadOnlyDictionary<DateTime, int> _counts;

    public YearMonthCard(int year, int month, IReadOnlyDictionary<DateTime, int> counts)
    {
        _year   = year;
        _month  = month;
        _counts = counts;
        // Как прежняя StackPanel карточки.
        Width               = CardW;
        Height              = TitleH + 7 * Dch;
        Margin              = new Thickness(13);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment   = VerticalAlignment.Top;
    }

    public int Year  => _year;
    public int Month => _month;

    /// <summary>Новые числа дней (карта счетчиков доехала второй фазой) — перерисовка без
    /// перестройки.</summary>
    public void SetCounts(IReadOnlyDictionary<DateTime, int> counts)
    {
        _counts = counts;
        InvalidateVisual();
    }

    private double Scale => LayoutHelper.GetLayoutScale(this);
    private double Round(double v) => LayoutHelper.RoundLayoutValue(v, Scale);
    private static double FontSizeBase => (double)Application.Current!.FindResource("FontSizeBase")!;
    private static FontFamily Family => Application.Current?.FindResource("FontFamilyBase") as FontFamily ?? FontFamily.Default;
    private static FontWeight Weight(string key) => (FontWeight)Application.Current!.FindResource(key)!;

    // Сетка чисел: левый верхний угол в координатах карточки (прежняя UniformGrid шириной 217 по
    // центру карточки 220 под названием высотой 32).
    private Point GridOrigin => new(Round((CardW - GridW) / 2), Round(TitleH));

    private DateTime DayAt(int i)
    {
        var first  = new DateTime(_year, _month, 1);
        var offset = ((int)first.DayOfWeek + 6) % 7;
        return first.AddDays(i - offset);
    }

    private bool Clickable(DateTime d) =>
        d.Month == _month && !(CalendarSettings.FiveDayWeek && CalendarMath.IsWeekend(d));

    // Ячейка числа i (0–41) в координатах карточки.
    private Rect CellRect(int i)
    {
        var o = GridOrigin;
        return new Rect(o.X + i % 7 * CellW, o.Y + (1 + i / 7) * Dch, CellW, Dch);
    }

    private int? CellAt(Point p)
    {
        for (var i = 0; i < 42; i++)
            if (CellRect(i).Contains(p)) return i;
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var cell = CellAt(e.GetPosition(this));
        Cursor = cell is { } i && Clickable(DayAt(i)) ? HandCursor : null;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Cursor = null;
    }

    /// <summary>Нажатие по числу — на весь прямоугольник ячейки, как прежняя рамка нажатия.</summary>
    public void OnTapped(Point p)
    {
        if (CellAt(p) is not { } i) return;
        var d = DayAt(i);
        if (Clickable(d)) DayTapped?.Invoke(this, CellRect(i), d.Date);
    }

    // Надпись как TextBlock: размер — текст вверх до целого DIP (при ширине по содержимому);
    // затем раскладка под итоговый размер, выравнивание строки — ее собственное.
    private static TextLayout Layout(string text, FontWeight weight, IBrush brush, TextAlignment align,
                                     double maxWidth, double maxHeight)
    {
        var key = new TextKey(text, FontSizeBase, weight, brush, align, maxWidth, maxHeight);
        if (Layouts.TryGetValue(key, out var hit)) return hit;
        var layout = new TextLayout(text, new Typeface(Family, FontStyle.Normal, weight), key.FontSize, brush,
                                    align, TextWrapping.NoWrap, null, null, FlowDirection.LeftToRight,
                                    maxWidth, maxHeight, double.NaN, 0, 0);
        Layouts[key] = layout;
        return layout;
    }

    private static Size Desired(TextLayout l) =>
        new(Math.Ceiling(l.OverhangLeading + l.WidthIncludingTrailingWhitespace + l.OverhangTrailing),
            Math.Ceiling(l.Height));

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        // Прозрачный фон — карточка ловит нажатия всей площадью (двойное — переход в месяц).
        context.FillRectangle(Brushes.Transparent, new Rect(size));

        var red       = Brush("BrushRed", "#FF3B30");
        var primary   = Brush("BrushPrimaryText", "#1C1C1E");
        var secondary = Brush("BrushSecondaryText", "#8E8E93");
        var bold      = Weight("FontWeightBold");
        var normal    = Weight("FontWeightNormal");

        // Название: TextBlock высотой 32 с отступами (6, 6, 0, 2), текст с левого края.
        var isTitleMonth = _year == DateTime.Today.Year && _month == DateTime.Today.Month;
        var title = Layout(MonthNames[_month - 1], bold, isTitleMonth ? red : primary, TextAlignment.Left,
                           CardW - 6, TitleH - 8);
        title.Draw(context, new Point(6 + title.OverhangLeading, 6));

        var o = GridOrigin;

        // Дни недели: надпись на всю ширину ячейки, по центру по вертикали.
        for (var c = 0; c < 7; c++)
        {
            var brush   = c >= 5 ? red : secondary;
            var measure = Layout(DayNames[c], normal, brush, TextAlignment.Center, CellW, Dch);
            var h       = Desired(measure).Height;
            var label   = Layout(DayNames[c], normal, brush, TextAlignment.Center, CellW, h);
            label.Draw(context, new Point(o.X + c * CellW + label.OverhangLeading, o.Y + Round((Dch - h) / 2)));
        }

        var today = DateTime.Today;
        for (var i = 0; i < 42; i++)
        {
            var d              = DayAt(i);
            var isCurrentMonth = d.Month == _month;
            var isToday        = d.Date == today;
            var count          = isCurrentMonth ? _counts.GetValueOrDefault(d.Date) : 0;
            var cell           = CellRect(i);

            // Рамка числа в координатах ячейки: сегодня и обычный день — 22 × 22 по центру,
            // день с событиями — на всю ширину без 1 px по бокам, высота 22 по центру.
            // Центрирование округлялось в координатах родителя рамки: у чисел, по которым можно
            // нажать, это была рамка нажатия во всю ячейку, у остальных — сама сетка, где
            // середина 4,5 px ложится на ячейку со своим сдвигом (к четному пикселю).
            var slot = new Point(i % 7 * CellW, (1 + i / 7) * Dch);
            var wrapped = Clickable(d);
            double Center(double slotAt, double span, double size) =>
                wrapped ? Round((span - size) / 2) : Round(slotAt + (span - size) / 2) - slotAt;
            Rect box;
            IBrush fore;
            if (isToday && isCurrentMonth)
            {
                box  = new Rect(Center(slot.X, CellW, Box), Center(slot.Y, Dch, Box), Box, Box);
                fore = Brushes.White;
                context.DrawRectangle(red, null, new Rect(cell.X + box.X, cell.Y + box.Y, box.Width, box.Height), Box / 2, Box / 2);
            }
            else if (count >= 1 && isCurrentMonth)
            {
                var step = count == 1 ? 0 : count <= 5 ? 1 : count <= 8 ? 2 : 3;
                box  = new Rect(wrapped ? Round(1) : Round(slot.X + 1) - slot.X, Center(slot.Y, Dch, Box), CellW - 2, Box);
                fore = Steps[step].Text;
                context.DrawRectangle(Steps[step].Bg, null, new Rect(cell.X + box.X, cell.Y + box.Y, box.Width, box.Height), 3, 3);
            }
            else
            {
                box  = new Rect(Center(slot.X, CellW, Box), Center(slot.Y, Dch, Box), Box, Box);
                fore = !isCurrentMonth ? OtherMonth : i % 7 >= 5 ? secondary : primary;
            }

            // Число: надпись по содержимому, по центру рамки.
            var text    = d.Day.ToString();
            var measure = Layout(text, normal, fore, TextAlignment.Center, box.Width, box.Height);
            var ds      = Desired(measure);
            var w       = Math.Min(box.Width, ds.Width);
            var h       = Math.Min(box.Height, ds.Height);
            var label   = Layout(text, normal, fore, TextAlignment.Center, w, h);
            var at      = new Point(cell.X + box.X + Round((box.Width - w) / 2),
                                    cell.Y + box.Y + Round((box.Height - h) / 2));
            label.Draw(context, new Point(at.X + label.OverhangLeading, at.Y));
        }
    }
}
