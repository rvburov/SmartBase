using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using SmartBase.Core.Notes;

namespace SmartBase.Views.Notes.Live;

/// <summary>
/// Живое оформление заметки: накладывает разобранную разметку на видимые строки редактора.
///
/// Сам ничего не разбирает — берет готовый <see cref="NoteMarkupLayout"/>. Это важно:
/// <c>ColorizeLine</c> зовется на КАЖДУЮ видимую строку КАЖДОЙ перерисовки, в том числе на каждое
/// нажатие клавиши. Разбор или LINQ здесь превратили бы набор текста в слайд-шоу.
/// </summary>
internal sealed class NoteMarkupColorizer : DocumentColorizingTransformer
{
    private readonly List<NoteSpan>   _spanBuffer   = new(32);
    private readonly List<NoteMarker> _markerBuffer = new(16);

    /// <summary>Разметка документа. Меняется целиком, по паузе в наборе.</summary>
    public NoteMarkupLayout Layout { get; set; } = NoteMarkupLayout.Empty;

    /// <summary>Выключено в режиме «Просмотр исходного кода»: там текст показывают как есть.</summary>
    public bool Enabled { get; set; }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!Enabled || line.Length == 0) return;

        var start = line.Offset;
        var end   = line.EndOffset;   // без символов перевода строки

        _spanBuffer.Clear();
        Layout.SpansIn(start, end, _spanBuffer);

        foreach (var s in _spanBuffer)
        {
            // Зажим — страховка: ChangeLinePart бросает исключение за границами строки, а
            // разметку мог посчитать чуть отставший разбор, пока человек уже стер полстроки.
            var a = Math.Max(s.Start, start);
            var b = Math.Min(s.End,   end);
            if (b <= a) continue;

            var style = s.Style;
            ChangeLinePart(a, b, el => Apply(el, style));
        }

        // Служебные символы — приглушенными. На строках, где они спрятаны, это ничего не стоит
        // (красить нечего), а на строке каретки они сразу читаются как подсобные, а не как текст.
        _markerBuffer.Clear();
        Layout.MarkersIn(start, end, _markerBuffer);

        foreach (var m in _markerBuffer)
        {
            var a = Math.Max(m.Start, start);
            var b = Math.Min(m.End,   end);
            if (b > a) ChangeLinePart(a, b, static el => Apply(el, NoteStyle.Faint));
        }
    }

    // Оформление КОМБИНИРУЕТСЯ, а не перезаписывается: на один и тот же кусок ложатся и заголовок,
    // и жирный, поэтому каждый случай читает текущее начертание и меняет одну ось. Иначе
    // «**_жирный курсив_**» терял бы одно из двух.
    private static void Apply(VisualLineElement el, NoteStyle style)
    {
        var p  = el.TextRunProperties;
        var tf = p.Typeface;

        switch (style)
        {
            case NoteStyle.Bold:
                p.SetTypeface(new Typeface(tf.FontFamily, tf.Style, Weight("FontWeightBold", FontWeight.SemiBold), tf.Stretch));
                break;

            case NoteStyle.Italic:
                p.SetTypeface(new Typeface(tf.FontFamily, FontStyle.Italic, tf.Weight, tf.Stretch));
                break;

            case NoteStyle.Strike:
                p.SetTextDecorations(TextDecorations.Strikethrough);
                break;

            case NoteStyle.Code:
            case NoteStyle.CodeBlock:
                p.SetTypeface(new Typeface(Family("FontFamilyMono", tf.FontFamily), tf.Style, tf.Weight, tf.Stretch));
                // Фон — на элементе, а не в свойствах прогона: он рисуется прямоугольником на всю
                // высоту строки, а фон прогона закрасил бы только коробку глифов.
                el.BackgroundBrush = Brush("BrushCardBg", Brushes.Transparent);
                break;

            case NoteStyle.Heading1: Heading(p, tf, "FontSizeLarge");  break;
            case NoteStyle.Heading2: Heading(p, tf, "FontSizeMedium"); break;
            // H3..H6 идут базовым размером — ровно как в Assets/Styles/Markdown.axaml. Заголовок
            // обязан выглядеть одинаково в редакторе и в режиме «Просмотр», а промежуточных
            // размеров в наборе токенов нет.
            case NoteStyle.Heading3:
            case NoteStyle.Heading4:
            case NoteStyle.Heading5:
            case NoteStyle.Heading6: Heading(p, tf, null); break;

            case NoteStyle.Quote:
                p.SetForegroundBrush(Brush("BrushSecondaryText", Brushes.Gray));
                p.SetTypeface(new Typeface(tf.FontFamily, FontStyle.Italic, tf.Weight, tf.Stretch));
                break;

            case NoteStyle.Link:
                p.SetForegroundBrush(Brush("BrushAccent", Brushes.RoyalBlue));
                break;

            case NoteStyle.Faint:
                p.SetForegroundBrush(Brush("BrushSecondaryText", Brushes.Gray));
                break;
        }
    }

    private static void Heading(VisualLineElementTextRunProperties p, Typeface tf, string? sizeKey)
    {
        p.SetTypeface(new Typeface(tf.FontFamily, tf.Style, Weight("FontWeightBold", FontWeight.SemiBold), tf.Stretch));
        // Размер читаем в момент отрисовки: FontScale кладет в ресурсы уже готовое значение
        // (стандарт плюс пользовательская дельта), и следующая перерисовка подхватит новое.
        if (sizeKey is not null && Resource(sizeKey) is double size and > 0) p.SetFontRenderingEmSize(size);
    }

    // ── Ресурсы приложения ──────────────────────────────────────────────────────

    private static object? Resource(string key) =>
        Application.Current!.TryFindResource(key, out var res) ? res : null;

    private static IBrush Brush(string key, IBrush fallback) =>
        Resource(key) is IBrush b ? b : fallback;

    private static FontWeight Weight(string key, FontWeight fallback) =>
        Resource(key) is FontWeight w ? w : fallback;

    private static FontFamily Family(string key, FontFamily fallback) =>
        Resource(key) is FontFamily f ? f : fallback;
}
