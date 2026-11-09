using OpenCvSharp;
using SmartBase.Extensions.PdfRename.Imaging;
using SmartBase.Extensions.PdfRename.Matching;
using SmartBase.Extensions.PdfRename.Ocr;
using SmartBase.Extensions.PdfRename.Pdf;
using SmartBase.Extensions.PdfRename.Templates;

namespace SmartBase.Extensions.PdfRename.Recognition;

/// <summary>Пара движков одного рабочего потока. Пороги уверенности — у каждого свои
/// (шкалы несравнимы); подобраны на синтетических образцах, см. документацию расширения.</summary>
internal sealed class OcrPair(IOcrEngine first, IOcrEngine second) : IDisposable
{
    public IOcrEngine First { get; } = first;
    public IOcrEngine Second { get; } = second;

    public static double MinConfidence(IOcrEngine engine) => engine switch
    {
        TesseractEngine => 0.30,
        PaddleEngine => 0.5,
        _ => 0.5,
    };

    /// <summary>Настоящая пара: Tesseract и PaddleOCR из папки плагина.</summary>
    public static OcrPair CreateReal(string tessdata) =>
        new(new TesseractEngine(tessdata), new PaddleEngine(OcrComponents.PaddleDir));

    public void Dispose()
    {
        First.Dispose();
        Second.Dispose();
    }
}

/// <summary>Итог чтения документа: подтвержденные поля или причина пропуска.</summary>
internal sealed record DocumentResult(FieldValues? Values, string? Problem, string? Template = null, string? Pages = null)
{
    public static DocumentResult Fail(string problem) => new(null, problem);
}

/// <summary>
/// Чтение одного PDF.
///
/// <para>Многостраничные документы: просматриваются страницы по порядку, не больше
/// <see cref="MaxPages"/>. Страница, не подошедшая ни к одному шаблону, пропускается (листы
/// продолжения, приложения). Каждая подошедшая страница распознается; все они должны дать
/// одинаковые обозначение и наименование. Если хоть одна подошедшая страница не подтвердила
/// поля, подходит к нескольким шаблонам сразу или дает другие данные — файл пропускается
/// целиком: значит, в PDF может быть несколько документов.</para>
/// </summary>
internal static class DocumentReader
{
    public const int MaxPages = 10;

    public static DocumentResult Read(string path, IReadOnlyList<DocTemplate> templates, OcrPair ocr, CancellationToken ct)
    {
        PdfDocument pdf;
        try { pdf = PdfDocument.Open(path); }
        catch (PdfOpenException ex) { return DocumentResult.Fail(ex.Message); }

        using (pdf)
        {
            var checkedPages = Math.Min(pdf.PageCount, MaxPages);
            var confirmed = new List<(int Page, FieldValues Values, string Template)>();
            string? firstMiss = null;

            for (var i = 0; i < checkedPages; i++)
            {
                ct.ThrowIfCancellationRequested();
                PageSize size;
                PageFeatures features;
                try
                {
                    size = pdf.GetPageSize(i);
                    using var render = pdf.RenderPage(i, PageFeatures.LongSide);
                    var gray = PageImage.ToGray(render);
                    features = PageFeatures.Build(gray, gray.Width / size.Width);
                }
                catch (PdfOpenException ex) { return DocumentResult.Fail($"стр. {i + 1}: {ex.Message}"); }

                using (features)
                {
                    var outcome = TemplateMatcher.Match(features, size, templates);
                    if (outcome.Ambiguous) return DocumentResult.Fail($"стр. {i + 1}: {outcome.Problem}");
                    if (outcome.Match is null)
                    {
                        firstMiss ??= $"стр. {i + 1}: {outcome.Problem}";
                        continue;
                    }

                    var match = outcome.Match;
                    var (values, problem) = ReadFields(pdf, i, size, features, match, ocr, ct);
                    if (values is null)
                        return DocumentResult.Fail($"стр. {i + 1} (шаблон «{match.Template.Name}»): {problem}");
                    confirmed.Add((i, values, match.Template.Name));
                }
            }

            if (confirmed.Count == 0)
            {
                if (pdf.PageCount == 1) return DocumentResult.Fail(firstMiss?["стр. 1: ".Length..] ?? "подходящий шаблон не найден");
                var scope = pdf.PageCount > checkedPages ? $"первых {checkedPages} из {pdf.PageCount} страниц" : $"{checkedPages} страниц";
                return DocumentResult.Fail($"ни одна из {scope} не подошла к шаблонам ({firstMiss})");
            }

            var first = confirmed[0];
            var other = confirmed.FirstOrDefault(c => c.Values != first.Values);
            if (other.Values is not null)
                return DocumentResult.Fail(
                    $"страницы дают разные данные: стр. {first.Page + 1} «{first.Values.Designation} {first.Values.Name}», " +
                    $"стр. {other.Page + 1} «{other.Values.Designation} {other.Values.Name}»");

            var pages = string.Join(", ", confirmed.Select(c => c.Page + 1));
            var templatesUsed = string.Join(", ", confirmed.Select(c => c.Template).Distinct());
            return new DocumentResult(first.Values, null, templatesUsed, pages);
        }
    }

    /// <summary>Вырезать поля страницы и прочитать их обоими движками; сверить.</summary>
    public static (FieldValues? Values, string? Problem) ReadFields(PdfDocument pdf, int pageIndex, PageSize size,
        PageFeatures features, PageMatch match, OcrPair ocr, CancellationToken ct)
    {
        var frames = match.Template.Frames;
        var combined = frames.FirstOrDefault(f => f.Role == FieldRole.Combined);
        if (combined is not null)
        {
            using var crop = FieldCrop.Extract(pdf, pageIndex, size, features, match, combined.Inner);
            ct.ThrowIfCancellationRequested();
            var a = ReadCombined(ocr.First, crop);
            var b = ReadCombined(ocr.Second, crop);
            return EngineAgreement.Combine(a, b);
        }

        var designationFrame = frames.First(f => f.Role == FieldRole.Designation);
        var nameFrame = frames.First(f => f.Role == FieldRole.Name);
        using var designationCrop = FieldCrop.Extract(pdf, pageIndex, size, features, match, designationFrame.Inner);
        using var nameCrop = FieldCrop.Extract(pdf, pageIndex, size, features, match, nameFrame.Inner);
        ct.ThrowIfCancellationRequested();
        var first = ReadSeparate(ocr.First, designationCrop, nameCrop);
        var second = ReadSeparate(ocr.Second, designationCrop, nameCrop);
        return EngineAgreement.Combine(first, second);
    }

    private static EngineFields ReadCombined(IOcrEngine engine, Mat crop)
    {
        var reading = engine.Read(crop);
        var (designation, name) = FieldParser.ParseCombined(reading, OcrPair.MinConfidence(engine));
        return new EngineFields(engine.Name, designation, name);
    }

    private static EngineFields ReadSeparate(IOcrEngine engine, Mat designationCrop, Mat nameCrop)
    {
        var min = OcrPair.MinConfidence(engine);
        var designation = FieldParser.ParseDesignation(engine.Read(designationCrop), min);
        var name = FieldParser.ParseName(engine.Read(nameCrop), min);
        return new EngineFields(engine.Name, designation, name);
    }
}
