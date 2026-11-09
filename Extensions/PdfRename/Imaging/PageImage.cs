using OpenCvSharp;

namespace SmartBase.Extensions.PdfRename.Imaging;

/// <summary>Общие операции над картинкой страницы: оттенки серого, линии таблиц, бинаризация.</summary>
internal static class PageImage
{
    public static Mat ToGray(Mat bgr)
    {
        var gray = new Mat();
        if (bgr.Channels() == 1) bgr.CopyTo(gray);
        else Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    /// <summary>Темное на светлом → маска темного (255), порог Оцу.</summary>
    public static Mat InkMask(Mat gray)
    {
        var ink = new Mat();
        Cv2.Threshold(gray, ink, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        return ink;
    }

    /// <summary>Длинные горизонтальные и вертикальные линии (рамки, сетка таблиц) из маски
    /// темного: штрихи короче <paramref name="minLength"/> точек — текст — отбрасываются.</summary>
    public static Mat LineMask(Mat ink, int minLength)
    {
        using var horizontal = new Mat();
        using var vertical = new Mat();
        using (var kh = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(minLength, 1)))
            Cv2.MorphologyEx(ink, horizontal, MorphTypes.Open, kh);
        using (var kv = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, minLength)))
            Cv2.MorphologyEx(ink, vertical, MorphTypes.Open, kv);
        var lines = new Mat();
        Cv2.BitwiseOr(horizontal, vertical, lines);
        return lines;
    }

    /// <summary>Маска темного без длинных линий — то, что остается от текста.</summary>
    public static Mat RemoveLines(Mat ink, int minLength)
    {
        using var lines = LineMask(ink, minLength);
        using var grown = new Mat();
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)))
            Cv2.Dilate(lines, grown, k);
        var text = new Mat();
        using var notLines = new Mat();
        Cv2.BitwiseNot(grown, notLines);
        Cv2.BitwiseAnd(ink, notLines, text);
        return text;
    }

    /// <summary>Уменьшить так, чтобы длинная сторона была не больше <paramref name="longSide"/>.</summary>
    public static Mat FitLongSide(Mat src, int longSide)
    {
        var scale = longSide / (double)Math.Max(src.Width, src.Height);
        if (scale >= 1) return src.Clone();
        var dst = new Mat();
        Cv2.Resize(src, dst, new Size(), scale, scale, InterpolationFlags.Area);
        return dst;
    }
}
