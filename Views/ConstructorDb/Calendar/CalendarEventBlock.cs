using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Views.ConstructorDb.Calendar;

/// <summary>
/// Блок события в сетке «День» и «Неделя», нарисованный одним контролом: фон по цвету события
/// (обычный, наведение, выделение), полоска слева, название жирным (перенос, не больше
/// помещающихся строк, многоточие) и время «ЧЧ:мм–ЧЧ:мм», если под названием есть место.
///
/// Раньше то же самое собиралось из девяти контролов — рамка, сетка, полоска, стопка с двумя
/// надписями, рамка выделения и две невидимые ручки растяжения поверх. На неделе с тысячей
/// событий (день-гигант: 1270 часовых событий в день, из них видны шесть колонок) это девять
/// тысяч контролов, и почти все время уходило на привязку стилей каждого нового контрола и на
/// раскладку надписей: 1,8 с построения и еще секунда раскладки. Здесь контрол один, стилей у
/// него нет, текст раскладывается при первой отрисовке.
///
/// Геометрия повторяет прежнюю: скругление 3, полоска 3 px с отступом (2, 3, 0, 3) и
/// скруглением 2, текст с отступом (9, 2), время на 1 px ниже названия. Ручки растяжения —
/// теперь зоны по 6 px сверху и снизу: курсор над ними «растянуть», а само растяжение, как и
/// прежде, решает сетка по геометрии записи (OnGridPointerPressed вида).
/// </summary>
internal sealed class CalendarEventBlock : Control
{
    // Высота строки названия и места под время — те же числа, что считал вид для надписей.
    private const double LineH   = 16.0;
    private const double Margins = 4.0;
    private const double TimeH   = 15.0;

    // Поле текста — как у прежней раскладки (замерено по ее визуальному дереву): текст с 9 px,
    // справа 3 px при целой ширине блока и 2 px при дробной (узкие подколонки пересекающихся
    // событий: ширина округляется вверх до пикселя, и поле текста получает этот остаток).
    private const double TextLeft = 9;

    private double TextRight => double.IsNaN(Width) || Math.Abs(Width - Math.Round(Width)) < 1e-6 ? 3 : 2;

    /// <summary>Зона растяжения у верхнего и нижнего края, px.</summary>
    public const double EdgeZone = 6;

    private static readonly Cursor HandCursor   = new(StandardCursorType.Hand);
    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeNorthSouth);

    // Кисти по цвету события — одни на все блоки этого цвета.
    private sealed record Palette(IBrush Normal, IBrush Hover, IBrush Solid, IBrush Dark);
    private static readonly Dictionary<string, Palette> Palettes = new(StringComparer.Ordinal);

    private readonly string  _title;
    private readonly string? _time;
    private readonly int     _titleMaxLines;
    private readonly double  _fontSize;
    private readonly Typeface _titleFace;
    private readonly Typeface _timeFace;
    private readonly Palette _palette;

    private bool _hover;
    private bool _selected;

    // Раскладки текста — под текущую ширину и цвет; пересобираются, когда они меняются.
    private TextLayout? _titleLayout;
    private TextLayout? _timeLayout;
    private double      _layoutWidth = -1;
    private IBrush?     _layoutBrush;

    public CalendarEventBlock(CalendarRecord ev, double height, double fontSize, FontWeight titleWeight)
    {
        Height       = height;
        ClipToBounds = true;
        Cursor       = HandCursor;

        var maxTitleLines = (int)((height - Margins - TimeH) / LineH);
        var showTime      = maxTitleLines >= 1;
        _titleMaxLines    = showTime ? maxTitleLines : Math.Max(1, (int)((height - Margins) / LineH));
        _title            = ev.DisplayTitle;
        _time             = showTime ? $"{ev.StartDt:HH:mm}–{ev.EndDt:HH:mm}" : null;
        _fontSize         = fontSize;

        var family = Application.Current?.FindResource("FontFamilyBase") as FontFamily ?? FontFamily.Default;
        _titleFace = new Typeface(family, FontStyle.Normal, titleWeight);
        _timeFace  = new Typeface(family);
        _palette   = PaletteOf(EventColors.Display(ev.Color));
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

    /// <summary>Выделено: сплошной фон цвета события и белый текст.</summary>
    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; InvalidateVisual(); }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hover = true;
        if (!_selected) InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = false;
        Cursor = HandCursor;
        if (!_selected) InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var y = e.GetPosition(this).Y;
        Cursor = y < EdgeZone || y >= Bounds.Height - EdgeZone ? ResizeCursor : HandCursor;
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // Как Border со скруглением и ClipToBounds: все содержимое обрезано скругленным краем
        // (сглаживание угла ложится дважды — край фона чуть светлее, как у прежнего блока).
        using var round = context.PushClip(new RoundedRect(rect, 3));

        var background = _selected ? _palette.Solid : _hover ? _palette.Hover : _palette.Normal;
        context.DrawRectangle(background, null, rect, 3, 3);
        context.DrawRectangle(_palette.Solid, null,
                              new RoundedRect(new Rect(2, 3, 3, Math.Max(0, rect.Height - 6)), new CornerRadius(2)));

        var foreground = _selected ? Brushes.White : _palette.Dark;
        var width      = Math.Max(0, rect.Width - TextLeft - TextRight);
        if (_titleLayout is null || _layoutWidth != width || !ReferenceEquals(_layoutBrush, foreground))
        {
            _titleLayout?.Dispose();
            _timeLayout?.Dispose();
            _titleLayout = new TextLayout(_title, _titleFace, _fontSize, foreground, TextAlignment.Left,
                                          TextWrapping.Wrap, TextTrimming.CharacterEllipsis, null,
                                          FlowDirection.LeftToRight, width, double.PositiveInfinity,
                                          double.NaN, 0, _titleMaxLines);
            _timeLayout = _time is null ? null
                : new TextLayout(_time, _timeFace, _fontSize, foreground, TextAlignment.Left,
                                 TextWrapping.NoWrap, null, null, FlowDirection.LeftToRight,
                                 width, double.PositiveInfinity, double.NaN, 0, 0);
            _layoutWidth = width;
            _layoutBrush = foreground;
        }

        // Текст — в своем поле: время без переноса в узком блоке обрезается у правого поля, как
        // обрезалась прежняя надпись по своей границе.
        using var field = context.PushClip(new Rect(TextLeft, 0, width, rect.Height));
        // Как TextBlock: раскладка рисуется со сдвигом на выступ первого глифа (OverhangLeading).
        _titleLayout.Draw(context, new Point(TextLeft + _titleLayout.OverhangLeading, 2));
        // Надпись занимает целые DIP (раскладка округляет ее высоту вверх) — время ниже на 1.
        if (_timeLayout is not null)
            _timeLayout.Draw(context, new Point(TextLeft + _timeLayout.OverhangLeading, 2 + Math.Ceiling(_titleLayout.Height) + 1));
    }
}
