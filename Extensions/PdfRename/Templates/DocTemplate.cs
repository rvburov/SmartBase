using OpenCvSharp;
using SmartBase.Extensions.PdfRename.Imaging;
using SmartBase.Extensions.PdfRename.Matching;
using SmartBase.Extensions.PdfRename.Pdf;

namespace SmartBase.Extensions.PdfRename.Templates;

/// <summary>
/// Шаблон типа документа: имя типа (из имени файла), параметры размеченной страницы, области
/// по цветам рамок (в долях страницы) и признаки для сопоставления — серая картинка без
/// цветных рамок и без содержимого полей, линии и ключевые точки.
/// </summary>
internal sealed class DocTemplate : IDisposable
{
    public required string Name { get; init; }
    public required string FilePath { get; init; }
    public required int PageIndex { get; init; }
    public required PageSize PageSize { get; init; }
    public required IReadOnlyList<ColorFrame> Frames { get; init; }
    public required PageFeatures Features { get; init; }

    /// <summary>Отношение сторон страницы (ширина / высота).</summary>
    public double Aspect => PageSize.Width / PageSize.Height;

    public bool HasCombined => Frames.Any(f => f.Role == FieldRole.Combined);

    public void Dispose() => Features.Dispose();
}

/// <summary>Итог чтения файла шаблона: шаблон или понятная причина.</summary>
internal sealed record TemplateLoad(string FilePath, DocTemplate? Template, string? Problem)
{
    public string FileName => Path.GetFileName(FilePath);
}

/// <summary>
/// Чтение шаблонов из папки. Каждый запуск читает файлы заново: изменили разметку — новая
/// учтется сразу, устаревших координат из кэша не бывает. Шаблон — первая страница PDF с
/// цветными рамками; рамки на нескольких страницах — ошибка (непонятно, какая страница — образец).
/// </summary>
internal static class TemplateLibrary
{
    /// <summary>Длинная сторона картинки для поиска рамок: крупнее, чем для сопоставления, —
    /// тонкая линия рамки не должна теряться при отрисовке.</summary>
    public const int FrameLongSide = 3000;

    /// <summary>Сколько страниц шаблона просматривать в поисках рамок.</summary>
    public const int MaxTemplatePages = 5;

    public static IReadOnlyList<string> ListFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static List<TemplateLoad> LoadFolder(string folder, CancellationToken ct = default)
    {
        var result = new List<TemplateLoad>();
        foreach (var file in ListFiles(folder))
        {
            ct.ThrowIfCancellationRequested();
            result.Add(Load(file));
        }

        // Одинаковые имена типов (Спецификация.pdf и спецификация.PDF) — неоднозначность.
        foreach (var group in result.Where(r => r.Template is not null)
                     .GroupBy(r => r.Template!.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Where(g => g.Count() > 1))
            foreach (var dup in group)
            {
                var index = result.IndexOf(dup);
                dup.Template!.Dispose();
                result[index] = dup with { Template = null, Problem = "несколько шаблонов с одним названием типа" };
            }
        return result;
    }

    public static TemplateLoad Load(string file)
    {
        var name = TypeName(file);
        if (name.Length == 0) return new TemplateLoad(file, null, "у файла нет названия типа документа");
        try
        {
            using var pdf = PdfDocument.Open(file);
            int? marked = null;
            FrameLayout? layout = null;
            Mat? page = null;
            try
            {
                for (var i = 0; i < Math.Min(pdf.PageCount, MaxTemplatePages); i++)
                {
                    var render = pdf.RenderPage(i, FrameLongSide);
                    var found = ColorFrames.Detect(render);
                    if (found.Frames.Count == 0 && found.Problem == ColorFrames.NoFrames)
                    {
                        render.Dispose();
                        continue;
                    }
                    if (marked is not null)
                    {
                        render.Dispose();
                        return new TemplateLoad(file, null,
                            $"цветные рамки на нескольких страницах ({marked + 1} и {i + 1}) — оставьте одну размеченную страницу");
                    }
                    marked = i;
                    layout = found;
                    page = render;
                }

                if (marked is null || layout is null || page is null)
                    return new TemplateLoad(file, null, "не найдено цветных рамок (синяя, зеленая или красно-оранжевая)");
                if (!layout.IsValid)
                    return new TemplateLoad(file, null, $"стр. {marked + 1}: {layout.Problem}");

                var size = pdf.GetPageSize(marked.Value);
                var features = BuildFeatures(page, layout, size);
                return new TemplateLoad(file, new DocTemplate
                {
                    Name = name,
                    FilePath = file,
                    PageIndex = marked.Value,
                    PageSize = size,
                    Frames = layout.Frames,
                    Features = features,
                }, null);
            }
            finally
            {
                page?.Dispose();
            }
        }
        catch (PdfOpenException ex)
        {
            return new TemplateLoad(file, null, ex.Message);
        }
        catch (Exception ex) when (ex is OpenCvSharp.OpenCVException or OutOfMemoryException)
        {
            return new TemplateLoad(file, null, $"не удалось обработать страницу: {ex.Message}");
        }
    }

    /// <summary>Название типа: имя файла без «.pdf», пробелы по краям убраны.</summary>
    public static string TypeName(string file) => Path.GetFileNameWithoutExtension(file).Trim();

    /// <summary>
    /// Признаки шаблона: цветные рамки и все цветное (печати, подписи) — в белый, содержимое
    /// полей — в белый (у каждого документа оно свое), затем уменьшение до размера
    /// сопоставления.
    /// </summary>
    private static PageFeatures BuildFeatures(Mat page, FrameLayout layout, PageSize size)
    {
        using var mask = ColorFrames.ColoredMask(page, out var hsv);
        hsv.Dispose();
        using var grown = new Mat();
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)))
            Cv2.Dilate(mask, grown, k);

        using var gray = PageImage.ToGray(page);
        gray.SetTo(new Scalar(255), grown);
        foreach (var frame in layout.Frames)
        {
            var r = frame.Inner.ToPixels(gray.Width, gray.Height);
            var rect = new Rect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height)
                .Intersect(new Rect(0, 0, gray.Width, gray.Height));
            if (rect.Width > 0 && rect.Height > 0)
                using (var roi = new Mat(gray, rect))
                    roi.SetTo(new Scalar(255));
        }

        var small = PageImage.FitLongSide(gray, PageFeatures.LongSide);
        var scale = small.Width / size.Width;
        var features = PageFeatures.Build(small, scale);
        // Линии шаблона считаются сразу: дальше шаблон читают несколько потоков одновременно.
        _ = features.Lines;
        return features;
    }
}
