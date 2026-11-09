using OpenCvSharp;

namespace SmartBase.Extensions.PdfRename.Imaging;

/// <summary>Назначение области шаблона — по цвету рамки.</summary>
internal enum FieldRole
{
    /// <summary>Синяя рамка: десятичное обозначение.</summary>
    Designation,
    /// <summary>Зеленая рамка: наименование.</summary>
    Name,
    /// <summary>Красно-оранжевая рамка: обозначение и наименование вместе.</summary>
    Combined,
}

/// <summary>Прямоугольник в долях страницы (0..1) — не зависит от разрешения картинки.</summary>
internal readonly record struct NormRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public static NormRect FromPixels(Rect r, int imageWidth, int imageHeight) =>
        new(r.X / (double)imageWidth, r.Y / (double)imageHeight, r.Width / (double)imageWidth, r.Height / (double)imageHeight);

    public Rect2d ToPixels(double imageWidth, double imageHeight) =>
        new(X * imageWidth, Y * imageHeight, Width * imageWidth, Height * imageHeight);

    public bool Intersects(NormRect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
}

/// <summary>Найденная рамка: назначение, внутренняя область (без линии рамки) и толщина линии.</summary>
internal sealed record ColorFrame(FieldRole Role, NormRect Inner, Rect Outer, int Stroke);

/// <summary>Итог разбора рамок шаблона: рамки либо понятная причина, почему шаблон не годится.</summary>
internal sealed record FrameLayout(IReadOnlyList<ColorFrame> Frames, string? Problem)
{
    public bool IsValid => Problem is null;
}

/// <summary>
/// Поиск цветных рамок на странице шаблона.
///
/// Цвет определяется по тону (H в HSV), а не по точному RGB: рамка из PDF-редактора, рамка
/// после печати и сканирования и рамка в JPEG дают разные оттенки одного цвета. Диапазоны
/// тона (шкала OpenCV 0..180, градусы ×2):
/// <list type="bullet">
/// <item>красно-оранжевый: 0..22 и 165..180 (0..44° и 330..360°) — от малинового до оранжевого;</item>
/// <item>зеленый: 40..85 (80..170°);</item>
/// <item>синий: 95..130 (190..260°) — от голубого до ультрамарина.</item>
/// </list>
/// Промежутки (желтый 22..40, бирюзовый 85..95, фиолетовый 130..165) — «непонятный цвет»:
/// рамка такого цвета не назначается никакой роли. Цветной точкой считается насыщенность
/// не ниже 80 и яркость не ниже 70 из 255: серый скан, желтизна бумаги и черный текст
/// (S &lt; 60 даже с цветной каймой JPEG) в маску не попадают.
///
/// Рамкой считается цветной контур, у которого все четыре стороны прямоугольника закрыты
/// линией не меньше чем на 85%, а внутри почти нет цвета (заливка закрывала бы текст).
/// Подписи синей ручкой, круглые печати и пятна под это не подходят и просто игнорируются.
/// </summary>
internal static class ColorFrames
{
    public const int MinSaturation = 80;

    /// <summary>Причина «рамок нет» — страница без разметки.</summary>
    public const string NoFrames = "не найдено цветных рамок";
    public const int MinValue = 70;

    /// <summary>Доля точек рамки одного цвета, чтобы цвет считался определенным.</summary>
    public const double DominantShare = 0.8;

    /// <summary>Тон точки → роль; null — цвет вне трех диапазонов.</summary>
    public static FieldRole? RoleOfHue(int hue) => hue switch
    {
        <= 22 or >= 165 => FieldRole.Combined,
        >= 40 and <= 85 => FieldRole.Name,
        >= 95 and <= 130 => FieldRole.Designation,
        _ => null,
    };

    /// <summary>Маска цветных (насыщенных) точек картинки BGR.</summary>
    public static Mat ColoredMask(Mat bgr, out Mat hsv)
    {
        hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        var mask = new Mat();
        Cv2.InRange(hsv, new Scalar(0, MinSaturation, MinValue), new Scalar(180, 255, 255), mask);
        return mask;
    }

    /// <summary>Найти и проверить рамки на странице шаблона (BGR).</summary>
    public static FrameLayout Detect(Mat bgr)
    {
        using var mask = ColoredMask(bgr, out var hsv);
        using (hsv)
        {
            // Сжатие и сканирование рвут тонкую линию — сшиваем разрывы в пару точек.
            using var closed = new Mat();
            using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)))
                Cv2.MorphologyEx(mask, closed, MorphTypes.Close, k);

            Cv2.FindContours(closed, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            var minSide = Math.Max(12, Math.Min(bgr.Width, bgr.Height) / 100);

            var frames = new List<ColorFrame>();
            var problems = new List<string>();
            foreach (var contour in contours)
            {
                var box = Cv2.BoundingRect(contour);
                if (box.Width < minSide || box.Height < minSide) continue;
                if (!LooksLikeFrame(closed, box, out var stroke, out var filled))
                {
                    if (filled && box.Width > minSide * 2 && box.Height > minSide)
                        problems.Add("закрашенная цветом область (рамка должна быть контуром, заливка закрывает текст)");
                    continue;
                }

                var role = DominantRole(hsv, mask, box, out var share);
                if (role is null)
                {
                    problems.Add(share < 0
                        ? "рамка непонятного цвета (нужен синий, зеленый или красно-оранжевый)"
                        : "цвет рамки неоднозначен (смесь цветов)");
                    continue;
                }

                var inner = new Rect(box.X + stroke, box.Y + stroke, box.Width - 2 * stroke, box.Height - 2 * stroke);
                if (inner.Width < 4 || inner.Height < 4) continue;
                frames.Add(new ColorFrame(role.Value, NormRect.FromPixels(inner, bgr.Width, bgr.Height), box, stroke));

                if (CrossesText(bgr, closed, box, stroke))
                    problems.Add($"{Describe(role.Value)} рамка проходит по тексту (рамка не должна закрывать буквы и цифры)");
            }

            return Validate(frames, problems, bgr.Width, bgr.Height);
        }
    }

    public static string Describe(FieldRole role) => role switch
    {
        FieldRole.Designation => "синяя",
        FieldRole.Name => "зеленая",
        _ => "красно-оранжевая",
    };

    private static FrameLayout Validate(List<ColorFrame> frames, List<string> problems, int width, int height)
    {
        if (problems.Count > 0) return new FrameLayout(frames, problems[0]);
        if (frames.Count == 0) return new FrameLayout(frames, NoFrames);

        var blue = frames.Count(f => f.Role == FieldRole.Designation);
        var green = frames.Count(f => f.Role == FieldRole.Name);
        var red = frames.Count(f => f.Role == FieldRole.Combined);

        string? problem = null;
        if (blue > 1) problem = $"синих рамок {blue} — должна быть одна";
        else if (green > 1) problem = $"зеленых рамок {green} — должна быть одна";
        else if (red > 1) problem = $"красно-оранжевых рамок {red} — должна быть одна";
        else if (red == 1 && blue + green > 0)
            problem = "есть и красно-оранжевая рамка, и синяя/зеленая — непонятно, какую разметку использовать";
        else if (red == 0 && blue == 1 && green == 0) problem = "есть синяя рамка (обозначение), но нет зеленой (наименование)";
        else if (red == 0 && green == 1 && blue == 0) problem = "есть зеленая рамка (наименование), но нет синей (обозначение)";

        if (problem is null)
        {
            for (var i = 0; i < frames.Count && problem is null; i++)
                for (var j = i + 1; j < frames.Count && problem is null; j++)
                    if (frames[i].Outer.IntersectsWith(frames[j].Outer))
                        problem = "рамки перекрываются";
        }
        if (problem is null && frames.Any(f => f.Inner.Width * f.Inner.Height > 0.5))
            problem = "рамка занимает больше половины страницы";

        return new FrameLayout(frames, problem);
    }

    /// <summary>Все четыре стороны закрыты линией, внутри почти пусто. Толщина — по средней
    /// глубине линии от края.</summary>
    private static bool LooksLikeFrame(Mat mask, Rect box, out int stroke, out bool filled)
    {
        stroke = 0;
        filled = false;
        using var roi = new Mat(mask, box);
        var band = Math.Clamp((int)(Math.Min(box.Width, box.Height) * 0.12), 3, 25);

        double Coverage(Func<int, (int x, int y)> origin, int length, bool horizontal, int depthSign)
        {
            var covered = 0;
            for (var i = 0; i < length; i++)
            {
                var (x0, y0) = origin(i);
                for (var d = 0; d < band; d++)
                {
                    var x = horizontal ? x0 : x0 + d * depthSign;
                    var y = horizontal ? y0 + d * depthSign : y0;
                    if (roi.At<byte>(y, x) != 0) { covered++; break; }
                }
            }
            return covered / (double)length;
        }

        int w = box.Width, h = box.Height;
        var top = Coverage(i => (i, 0), w, true, 1);
        var bottom = Coverage(i => (i, h - 1), w, true, -1);
        var left = Coverage(i => (0, i), h, false, 1);
        var right = Coverage(i => (w - 1, i), h, false, -1);

        // Толщина: медиана длины сплошной линии от левого и верхнего края.
        var runs = new List<int>();
        for (var y = h / 4; y < h * 3 / 4; y += Math.Max(1, h / 20))
        {
            var run = 0;
            while (run < w / 2 && roi.At<byte>(y, run) != 0) run++;
            if (run > 0) runs.Add(run);
        }
        for (var x = w / 4; x < w * 3 / 4; x += Math.Max(1, w / 20))
        {
            var run = 0;
            while (run < h / 2 && roi.At<byte>(run, x) != 0) run++;
            if (run > 0) runs.Add(run);
        }
        runs.Sort();
        stroke = runs.Count == 0 ? 2 : runs[runs.Count / 2] + 1;

        var inner = new Rect(stroke + 1, stroke + 1, w - 2 * stroke - 2, h - 2 * stroke - 2);
        var fill = 1.0;
        if (inner.Width > 0 && inner.Height > 0)
        {
            using var innerRoi = new Mat(roi, inner);
            fill = Cv2.CountNonZero(innerRoi) / (double)(inner.Width * inner.Height);
        }
        filled = fill > 0.5;

        return top >= 0.85 && bottom >= 0.85 && left >= 0.85 && right >= 0.85 && fill < 0.15;
    }

    /// <summary>Роль по преобладающему тону точек рамки. share &lt; 0 — преобладает «непонятный»
    /// цвет; null при share ≥ 0 — цвета смешаны.</summary>
    private static FieldRole? DominantRole(Mat hsv, Mat mask, Rect box, out double share)
    {
        var counts = new int[4]; // Designation, Name, Combined, нет роли
        using var hsvRoi = new Mat(hsv, box);
        using var maskRoi = new Mat(mask, box);
        for (var y = 0; y < box.Height; y++)
            for (var x = 0; x < box.Width; x++)
            {
                if (maskRoi.At<byte>(y, x) == 0) continue;
                var hue = hsvRoi.At<Vec3b>(y, x).Item0;
                var role = RoleOfHue(hue);
                counts[role is null ? 3 : (int)role.Value]++;
            }
        var total = counts.Sum();
        share = 0;
        if (total == 0) return null;
        var best = Array.IndexOf(counts, counts.Max());
        share = counts[best] / (double)total;
        if (best == 3)
        {
            share = -1;
            return null;
        }
        return share >= DominantShare ? (FieldRole)best : null;
    }

    /// <summary>
    /// Рамка проходит по тексту: по обе стороны линии в одном и том же месте есть темные
    /// точки, которые не являются длинными линиями таблицы, — буква разрезана рамкой.
    /// </summary>
    private static bool CrossesText(Mat bgr, Mat colored, Rect box, int stroke)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var dark = new Mat();
        Cv2.Threshold(gray, dark, 128, 255, ThresholdTypes.BinaryInv);
        // Кайма линии после JPEG (темная, но уже не насыщенная) — тоже рамка, не текст.
        using var coloredWide = new Mat();
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)))
            Cv2.Dilate(colored, coloredWide, k);
        using var notColored = new Mat();
        Cv2.BitwiseNot(coloredWide, notColored);
        Cv2.BitwiseAnd(dark, notColored, dark);
        using var text = PageImage.RemoveLines(dark, Math.Max(30, Math.Max(bgr.Width, bgr.Height) / 40));

        var gap = 3;
        var reach = Math.Max(3, stroke);
        int hits = 0, length = 0;

        bool Dark(int x, int y) => x >= 0 && y >= 0 && x < text.Width && y < text.Height && text.At<byte>(y, x) != 0;
        bool AnyRun(int x, int y, int dx, int dy)
        {
            for (var d = 0; d < reach; d++)
                if (Dark(x + dx * d, y + dy * d)) return true;
            return false;
        }

        for (var x = box.Left + stroke; x < box.Right - stroke; x++)
        {
            length += 2;
            if (AnyRun(x, box.Top - gap, 0, -1) && AnyRun(x, box.Top + stroke + gap, 0, 1)) hits++;
            if (AnyRun(x, box.Bottom + gap, 0, 1) && AnyRun(x, box.Bottom - stroke - gap, 0, -1)) hits++;
        }
        for (var y = box.Top + stroke; y < box.Bottom - stroke; y++)
        {
            length += 2;
            if (AnyRun(box.Left - gap, y, -1, 0) && AnyRun(box.Left + stroke + gap, y, 1, 0)) hits++;
            if (AnyRun(box.Right + gap, y, 1, 0) && AnyRun(box.Right - stroke - gap, y, -1, 0)) hits++;
        }
        // Порог — в точках, а не в доле периметра: буква, разрезанная рамкой, дает пересечение
        // лишь на высоту строки, а рамка может быть длинной.
        return hits > Math.Max(4, length * 0.005);
    }
}
