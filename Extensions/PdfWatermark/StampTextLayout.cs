using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace SmartBase.Extensions.PdfWatermark;

/// <summary>Перенос строк по настоящим метрикам встроенного узкого полужирного шрифта.</summary>
public static class StampTextLayout
{
    public static StampTextBlock Compose(IReadOnlyList<string> paragraphs, double width, double height,
                                          double pixelsPerMm, StampLook look)
    {
        if (paragraphs.Count == 0 || paragraphs.All(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Знак без текста", nameof(paragraphs));

        var pxPerPt = pixelsPerMm * 25.4 / 72;
        var pad = Math.Max(1, pxPerPt);
        var availableW = width - pad * 2;
        var availableH = height - pad * 2;
        if (availableW <= 0 || availableH <= 0) throw TooMuchText();

        StampTextBlock? fitted = null;
        // Полпункта не меняет читаемость, зато дает устойчивый подбор без зависимостей от
        // округления измерений шрифта на разных платформах.
        for (var pt = look.MaxFontPt; pt >= look.MinFontPt; pt -= 0.5)
        {
            var lines = new List<StampTextLine>();
            var top = 0f;
            using var ruler = new SKPaint { Typeface = WatermarkFont.Typeface, IsAntialias = true };
            for (var index = 0; index < paragraphs.Count; index++)
            {
                // Только заголовок готовой выдачи крупнее. Свободный текст остается именно
                // введенным пользователем; никаких служебных строк сюда не добавляется.
                var title = index == 0 && paragraphs[index] == "КОПИЯ";
                ruler.TextSize = (float)(pt * pxPerPt * (title ? 1.35 : 1));
                var metrics = ruler.FontMetrics;
                var step = Math.Max(ruler.TextSize, metrics.Descent - metrics.Ascent) * (float)look.LineSpacing;
                foreach (var text in Wrap(paragraphs[index], ruler, (float)availableW))
                {
                    if (ruler.MeasureText(text) > availableW)
                    {
                        top = float.PositiveInfinity;
                        break;
                    }
                    var baseline = top + (step - (metrics.Descent - metrics.Ascent)) / 2 - metrics.Ascent;
                    lines.Add(new(text, ruler.TextSize, baseline));
                    top += step;
                }
                // Ранний выход важен для свободного текста из тысяч символов.
                if (top > availableH) break;
            }
            if (top <= availableH)
            {
                fitted = new(lines, top, pt);
                break;
            }
        }
        return fitted ?? throw TooMuchText();
    }

    private static IEnumerable<string> Wrap(string paragraph, SKPaint ruler, float width)
    {
        var words = Regex.Split(paragraph.Trim(), @"\s+");
        var current = "";
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (ruler.MeasureText(candidate) <= width)
            {
                current = candidate;
                continue;
            }
            if (current.Length > 0) { yield return current; current = ""; }
            // Длинный идентификатор тоже переносится. Границы берем по текстовым элементам,
            // чтобы не отрезать половину суррогатной пары или комбинируемый знак.
            var elements = StringInfo.GetTextElementEnumerator(word);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                if (current.Length > 0 && ruler.MeasureText(current + element) > width)
                {
                    yield return current;
                    current = "";
                }
                current += element;
            }
        }
        yield return current;
    }

    private static PdfWatermarkException TooMuchText() => new(
        "Текст не помещается в блок с читаемым размером букв. Увеличьте ширину или высоту блока либо сократите текст.");
}

public sealed record StampTextLine(string Text, float Size, float Baseline);
public sealed record StampTextBlock(IReadOnlyList<StampTextLine> Lines, double Height, double FontPt);
