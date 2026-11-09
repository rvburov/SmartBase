using OpenCvSharp;
using SmartBase.Extensions.PdfRename.Imaging;
using SmartBase.Extensions.PdfRename.Pdf;
using SmartBase.Extensions.PdfRename.Templates;

namespace SmartBase.Extensions.PdfRename.Matching;

/// <summary>Шаблон, совмещенный со страницей: преобразование «точка картинки страницы → точка
/// картинки шаблона» (поворот, масштаб, сдвиг) и оценки совпадения.</summary>
internal sealed record PageMatch(
    DocTemplate Template,
    double[] Transform,
    double Score,
    double GlobalScore,
    double LocalScore,
    int Inliers,
    double Angle,
    double ScaleRatio,
    bool SizeAgrees);

/// <summary>Итог подбора шаблона для страницы: совмещение или причина, почему его нет.</summary>
internal sealed record MatchOutcome(PageMatch? Match, string? Problem, bool Ambiguous = false);

/// <summary>
/// Подбор шаблона для страницы и совмещение.
///
/// <para>1. Отбор по формату: отношение сторон страницы совпадает с шаблоном (с поворотом на 90°
/// или без) с допуском <see cref="AspectTolerance"/>. Физический размер из PDF — только
/// подсказка (<see cref="PageMatch.SizeAgrees"/>): у сканов он бывает записан неверно.</para>
/// <para>2. Совмещение: ключевые точки ORB страницы сопоставляются с точками шаблона
/// (тест отношения Лоу), по ним RANSAC находит поворот на любой угол, масштаб и сдвиг —
/// повороты на 90/180/270° и небольшой наклон получаются сразу.</para>
/// <para>3. Проверка формы: линии бланка (рамка листа, графы основной надписи, таблицы)
/// страницы, перенесенные в координаты шаблона, должны лечь на линии шаблона — по всей
/// странице (<see cref="MinGlobal"/>) и особенно рядом с размеченными полями
/// (<see cref="MinLocal"/>). Совпадение — F-мера с допуском в несколько точек.</para>
/// <para>4. Выбор: из прошедших проверку — лучший; если второй отстает меньше чем на
/// <see cref="Margin"/>, шаблон неоднозначен и страница пропускается.</para>
/// </summary>
internal static class TemplateMatcher
{
    public const double AspectTolerance = 0.08;
    public const double MaxSkewDegrees = 10;
    public const double MinScaleRatio = 0.7;
    public const double MaxScaleRatio = 1.45;
    public const int MinInliers = 20;
    public const double MinGlobal = 0.5;
    public const double MinLocal = 0.75;
    public const double Margin = 0.08;

    /// <summary>Допуск совпадения линий, точек картинки сопоставления.</summary>
    private const int LineTolerance = 3;

    public static MatchOutcome Match(PageFeatures page, PageSize pageSize, IReadOnlyList<DocTemplate> templates)
    {
        if (templates.Count == 0) return new MatchOutcome(null, "нет пригодных шаблонов");
        if (page.KeyPoints.Length < MinInliers || page.Descriptors.Empty())
            return new MatchOutcome(null, "на странице нет различимых элементов (пустая или нечитаемая)");

        var passed = new List<PageMatch>();
        PageMatch? bestRejected = null;
        foreach (var template in templates)
        {
            var m = TryMatch(page, pageSize, template);
            if (m is null) continue;
            if (m.GlobalScore >= MinGlobal && m.LocalScore >= MinLocal) passed.Add(m);
            else if (bestRejected is null || m.Score > bestRejected.Score) bestRejected = m;
        }

        if (passed.Count == 0)
            return new MatchOutcome(null, bestRejected is null
                ? "подходящий шаблон не найден"
                : $"подходящий шаблон не найден (ближе всех «{bestRejected.Template.Name}», совпадение формы {bestRejected.Score:P0})");

        // Если у части кандидатов совпал и физический размер листа — выбираем среди них.
        var pool = passed.Any(p => p.SizeAgrees) ? passed.Where(p => p.SizeAgrees).ToList() : passed;
        pool.Sort((a, b) => b.Score.CompareTo(a.Score));
        var best = pool[0];
        if (pool.Count > 1 && best.Score - pool[1].Score < Margin)
        {
            var names = string.Join(", ", pool.TakeWhile(p => best.Score - p.Score < Margin).Select(p => $"«{p.Template.Name}»"));
            return new MatchOutcome(null, $"подходят несколько шаблонов одинаково: {names}", Ambiguous: true);
        }
        return new MatchOutcome(best, null);
    }

    /// <summary>Совместить страницу с одним шаблоном; null — не совмещается вовсе.</summary>
    public static PageMatch? TryMatch(PageFeatures page, PageSize pageSize, DocTemplate template)
    {
        var pageAspect = pageSize.Width / pageSize.Height;
        var same = Math.Abs(Math.Log(pageAspect / template.Aspect)) < Math.Log(1 + AspectTolerance);
        var turned = Math.Abs(Math.Log(1 / pageAspect / template.Aspect)) < Math.Log(1 + AspectTolerance);
        if (!same && !turned) return null;

        var t = template.Features;
        if (t.Descriptors.Empty() || t.KeyPoints.Length < MinInliers) return null;

        using var matcher = new BFMatcher(NormTypes.Hamming);
        var knn = matcher.KnnMatch(page.Descriptors, t.Descriptors, 2);
        var src = new List<Point2f>();
        var dst = new List<Point2f>();
        foreach (var pair in knn)
        {
            if (pair.Length < 2 || pair[0].Distance >= 0.8f * pair[1].Distance) continue;
            src.Add(page.KeyPoints[pair[0].QueryIdx].Pt);
            dst.Add(t.KeyPoints[pair[0].TrainIdx].Pt);
        }
        if (src.Count < MinInliers) return null;

        using var inliers = new Mat();
        using var srcMat = InputArray.Create(src);
        using var dstMat = InputArray.Create(dst);
        using var affine = Cv2.EstimateAffinePartial2D(srcMat, dstMat, inliers, RobustEstimationAlgorithms.RANSAC,
            ransacReprojThreshold: Math.Max(3, PageFeatures.LongSide * 0.004), maxIters: 5000, confidence: 0.995);
        if (affine is null || affine.Empty()) return null;
        var inlierCount = Cv2.CountNonZero(inliers);
        if (inlierCount < MinInliers) return null;

        var m = new double[6];
        for (var i = 0; i < 6; i++) m[i] = affine.At<double>(i / 3, i % 3);
        var scale = Math.Sqrt(m[0] * m[0] + m[3] * m[3]);
        var angle = Math.Atan2(m[3], m[0]) * 180 / Math.PI;
        var quarter = Math.Round(angle / 90) * 90;
        if (Math.Abs(angle - quarter) > MaxSkewDegrees) return null;
        var quarterTurn = ((int)Math.Round(quarter / 90) % 2 + 2) % 2 == 1;
        if (quarterTurn ? !turned : !same) return null;

        // Ожидаемый масштаб: обе картинки приведены к одной длинной стороне.
        var expected = Math.Max(t.Width, t.Height) / (double)Math.Max(page.Width, page.Height);
        var ratio = scale / expected;
        if (ratio < MinScaleRatio || ratio > MaxScaleRatio) return null;

        // Линии страницы ищутся ПОСЛЕ переноса в координаты шаблона: на наклонном скане
        // тонкая линия не совпадает со строкой точек, и поиск по горизонтали/вертикали
        // ее бы не увидел.
        using var warpedGray = new Mat();
        Cv2.WarpAffine(page.Gray, warpedGray, affine, new Size(t.Width, t.Height), InterpolationFlags.Linear,
            BorderTypes.Constant, Scalar.All(255));
        using var warped = PageFeatures.LineMaskOf(warpedGray);

        var whole = new Rect(0, 0, t.Width, t.Height);
        var global = LineAgreement(warped, t.Lines, whole);
        var local = LineAgreement(warped, t.Lines, FieldsNeighborhood(template));

        var templateLong = Math.Max(template.PageSize.Width, template.PageSize.Height);
        var pageLong = Math.Max(pageSize.Width, pageSize.Height);
        var sizeAgrees = Math.Abs(Math.Log(templateLong / pageLong)) < Math.Log(1.12);

        return new PageMatch(template, m, (global + 2 * local) / 3, global, local, inlierCount,
            angle, ratio, sizeAgrees);
    }

    /// <summary>Окрестность размеченных полей: их общий прямоугольник, расширенный на свой
    /// размер и не меньше чем на 8% страницы в каждую сторону.</summary>
    public static Rect FieldsNeighborhood(DocTemplate template)
    {
        var t = template.Features;
        double x0 = 1, y0 = 1, x1 = 0, y1 = 0;
        foreach (var f in template.Frames)
        {
            x0 = Math.Min(x0, f.Inner.X);
            y0 = Math.Min(y0, f.Inner.Y);
            x1 = Math.Max(x1, f.Inner.Right);
            y1 = Math.Max(y1, f.Inner.Bottom);
        }
        var padX = Math.Max(x1 - x0, 0.08);
        var padY = Math.Max(y1 - y0, 0.08);
        var left = (int)(Math.Max(0, x0 - padX) * t.Width);
        var top = (int)(Math.Max(0, y0 - padY) * t.Height);
        var right = (int)(Math.Min(1, x1 + padX) * t.Width);
        var bottom = (int)(Math.Min(1, y1 + padY) * t.Height);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>F-мера совпадения линий в прямоугольнике: доля линий шаблона, которые есть на
    /// странице, и доля линий страницы, которые есть в шаблоне (с допуском).</summary>
    public static double LineAgreement(Mat pageLines, Mat templateLines, Rect area)
    {
        using var p = new Mat(pageLines, area);
        using var t = new Mat(templateLines, area);
        var pCount = Cv2.CountNonZero(p);
        var tCount = Cv2.CountNonZero(t);
        if (pCount == 0 || tCount == 0) return 0;

        using var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * LineTolerance + 1, 2 * LineTolerance + 1));
        using var pGrown = new Mat();
        using var tGrown = new Mat();
        Cv2.Dilate(p, pGrown, k);
        Cv2.Dilate(t, tGrown, k);
        using var hitT = new Mat();
        using var hitP = new Mat();
        Cv2.BitwiseAnd(t, pGrown, hitT);
        Cv2.BitwiseAnd(p, tGrown, hitP);
        var recall = Cv2.CountNonZero(hitT) / (double)tCount;
        var precision = Cv2.CountNonZero(hitP) / (double)pCount;
        return recall + precision == 0 ? 0 : 2 * recall * precision / (recall + precision);
    }
}
