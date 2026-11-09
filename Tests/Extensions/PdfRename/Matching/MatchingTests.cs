using OpenCvSharp;
using SmartBase.Extensions.PdfRename.Imaging;
using SmartBase.Extensions.PdfRename.Matching;
using SmartBase.Extensions.PdfRename.Pdf;
using SmartBase.Extensions.PdfRename.Templates;
using SmartBase.Tests.Extensions.PdfRename.Samples;

namespace SmartBase.Tests.Extensions.PdfRename.Matching;

/// <summary>
/// Группы ТЗ 5–7 (без OCR): повороты 90/180/270, наклон, масштаб, сдвиг; выбор среди
/// нескольких шаблонов одного формата; нет подходящего шаблона; одинаковые шаблоны —
/// неоднозначность; входные документы без цветных рамок; шаблоны в векторе (как из
/// редактора PDF) и с ошибками разметки.
/// </summary>
public sealed class MatchingTests : IDisposable
{
    private readonly Workbench _wb = new();
    private readonly List<DocTemplate> _templates = [];

    public void Dispose()
    {
        foreach (var t in _templates) t.Dispose();
        _wb.Dispose();
    }

    private List<DocTemplate> Load()
    {
        var loads = TemplateLibrary.LoadFolder(_wb.Templates);
        foreach (var l in loads) Assert.True(l.Template is not null, $"{l.FileName}: {l.Problem}");
        _templates.AddRange(loads.Select(l => l.Template!));
        return _templates;
    }

    private static MatchOutcome MatchFirstPage(string pdfPath, IReadOnlyList<DocTemplate> templates)
    {
        using var pdf = PdfDocument.Open(pdfPath);
        var size = pdf.GetPageSize(0);
        using var render = pdf.RenderPage(0, PageFeatures.LongSide);
        var gray = PageImage.ToGray(render);
        using var features = PageFeatures.Build(gray, gray.Width / size.Width);
        return TemplateMatcher.Match(features, size, templates);
    }

    public static TheoryData<int, double, double, double, double> Distortions => new()
    {
        { 0, 0, 1.0, 0, 0 },
        { 1, 0, 1.0, 0, 0 },
        { 2, 0, 1.0, 0, 0 },
        { 3, 0, 1.0, 0, 0 },
        { 0, 2.5, 1.0, 0, 0 },
        { 0, -4, 1.0, 0, 0 },
        { 1, 1.5, 0.92, 5, -4 },
        { 2, -1, 1.06, -6, 3 },
        { 3, 3, 0.9, 0, 8 },
    };

    [OcrTheory]
    [MemberData(nameof(Distortions))]
    public void Rotated_skewed_scaled_and_shifted_scans_match_the_right_template(int turns, double skew, double scale, double dx, double dy)
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        _wb.AddTemplate("Спецификация", FormKind.Specification);
        var templates = Load();
        var options = new ScanOptions { QuarterTurns = turns, SkewDegrees = skew, Scale = scale, ShiftXmm = dx, ShiftYmm = dy, Noise = 0.02 };

        var drawing = MatchFirstPage(_wb.AddDocument("d.pdf", FormKind.Drawing, "АБВГ.461411.123", "Корпус", options, seed: 3), templates);
        var spec = MatchFirstPage(_wb.AddDocument("s.pdf", FormKind.Specification, "АБВГ.461411.200", "Модуль", options, seed: 4), templates);

        Assert.Equal("Чертеж", drawing.Match?.Template.Name);
        Assert.Equal("Спецификация", spec.Match?.Template.Name);
        var expected = -90.0 * turns + skew;   // поворот скана «назад» к шаблону
        Assert.True(AngleDiff(drawing.Match!.Angle, expected) < 1.0, $"угол {drawing.Match.Angle:F1}, ожидался {expected:F1}");
    }

    private static double AngleDiff(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    [OcrFact]
    public void Same_page_size_does_not_decide_the_type()
    {
        // Чертеж и спецификация — оба A4 книжные: выбор по форме бланка, а не по размеру.
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        _wb.AddTemplate("Спецификация", FormKind.Specification);
        var templates = Load();
        Assert.All(templates, t => Assert.Equal(210, t.PageSize.WidthMm, 1.0));

        var outcome = MatchFirstPage(_wb.AddDocument("s.pdf", FormKind.Specification, "АБВГ.461411.200", "Модуль"), templates);
        Assert.Equal("Спецификация", outcome.Match?.Template.Name);
        Assert.True(outcome.Match!.Score - TemplateMatcher.TryMatch(Features(_wb.Source + "/s.pdf", out var size), size,
            templates.Single(t => t.Name == "Чертеж"))!.Score > TemplateMatcher.Margin);
    }

    private static PageFeatures Features(string pdfPath, out PageSize size)
    {
        using var pdf = PdfDocument.Open(pdfPath);
        size = pdf.GetPageSize(0);
        using var render = pdf.RenderPage(0, PageFeatures.LongSide);
        var gray = PageImage.ToGray(render);
        return PageFeatures.Build(gray, gray.Width / size.Width);
    }

    [OcrFact]
    public void Two_identical_templates_are_ambiguous()
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        _wb.AddTemplate("Чертеж детали", FormKind.Drawing);
        var templates = Load();

        var outcome = MatchFirstPage(_wb.AddDocument("d.pdf", FormKind.Drawing, "АБВГ.461411.123", "Корпус"), templates);

        Assert.Null(outcome.Match);
        Assert.True(outcome.Ambiguous);
        Assert.Contains("несколько шаблонов", outcome.Problem);
    }

    [OcrFact]
    public void Unrelated_page_has_no_template()
    {
        _wb.AddTemplate("Спецификация", FormKind.Specification);
        var templates = Load();

        // Лист без бланка: только текст.
        using var page = new Mat(2339, 1654, MatType.CV_8UC3, Scalar.All(255));
        for (var y = 200; y < 2200; y += 80)
            Cv2.PutText(page, "Lorem ipsum dolor sit amet 12345", new Point(150, y), HersheyFonts.HersheySimplex, 1.6, Scalar.All(0), 3);
        var path = _wb.AddPages("text.pdf", [page], 200);

        var outcome = MatchFirstPage(path, templates);

        Assert.Null(outcome.Match);
        Assert.False(outcome.Ambiguous);
        Assert.Contains("не найден", outcome.Problem);
    }

    [OcrFact]
    public void Different_format_is_not_matched()
    {
        _wb.AddTemplate("Чертеж A3", FormKind.DrawingA3, Marking.Combined);
        var templates = Load();
        var outcome = MatchFirstPage(_wb.AddDocument("a4.pdf", FormKind.Drawing, "АБВГ.461411.123", "Корпус"), templates);
        Assert.Null(outcome.Match);
    }

    [OcrFact]
    public void Blank_page_is_reported_not_guessed()
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        var templates = Load();
        using var page = new Mat(2339, 1654, MatType.CV_8UC3, Scalar.All(255));
        var outcome = MatchFirstPage(_wb.AddPages("blank.pdf", [page], 200), templates);
        Assert.Null(outcome.Match);
    }

    [OcrFact]
    public void Vector_frames_from_pdf_editor_are_detected()
    {
        // Рамки — векторные прямоугольники поверх скана, как рисует редактор PDF.
        _wb.AddTemplate("Чертеж", FormKind.Drawing, Marking.SeparateVector);
        var template = Assert.Single(Load());
        Assert.Equal(2, template.Frames.Count);
        var fields = SyntheticForms.Fields(FormKind.Drawing);
        var blue = template.Frames.Single(f => f.Role == FieldRole.Designation).Inner;
        Assert.Equal((fields.Designation.X + 1) / fields.PageWidthMm, blue.X, 0.01);
        Assert.Equal((fields.Designation.Y + 1) / fields.PageHeightMm, blue.Y, 0.01);
    }

    [OcrFact]
    public void Template_file_name_is_the_document_type()
    {
        _wb.AddTemplate("  Спецификация ", FormKind.Specification);
        Assert.Equal("Спецификация", Assert.Single(Load()).Name);
    }

    [OcrFact]
    public void Broken_templates_are_reported_and_do_not_block_good_ones()
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        _wb.AddTemplate("Без зеленой", FormKind.Specification, customize: null, green: new Scalar(255, 255, 255));
        File.WriteAllText(Path.Combine(_wb.Templates, "Поврежденный.pdf"), "это не PDF");

        var loads = TemplateLibrary.LoadFolder(_wb.Templates);

        Assert.NotNull(loads.Single(l => l.FileName == "Чертеж.pdf").Template);
        Assert.Contains("нет зеленой", loads.Single(l => l.FileName == "Без зеленой.pdf").Problem);
        Assert.NotNull(loads.Single(l => l.FileName == "Поврежденный.pdf").Problem);
        foreach (var l in loads) l.Template?.Dispose();
    }

    [OcrFact]
    public void Template_without_frames_is_reported()
    {
        var fields = SyntheticForms.Fields(FormKind.Drawing);
        using var page = SyntheticForms.Render(FormKind.Drawing, "ОБРЗ.000000.000", "Образец", 200);
        SyntheticForms.SavePdf(Path.Combine(_wb.Templates, "Пустой.pdf"), [page], 200);
        var load = Assert.Single(TemplateLibrary.LoadFolder(_wb.Templates));
        Assert.Null(load.Template);
        Assert.Contains("не найдено цветных рамок", load.Problem);
    }

    [OcrFact]
    public void Edited_template_is_read_again_on_next_load()
    {
        var path = _wb.AddTemplate("Чертеж", FormKind.Drawing);
        var first = Assert.Single(TemplateLibrary.LoadFolder(_wb.Templates)).Template!;
        var before = first.Frames.Single(f => f.Role == FieldRole.Designation).Inner;
        first.Dispose();

        // Тот же файл, рамка обозначения сужена и сдвинута на 20 мм вправо.
        File.Delete(path);
        _wb.AddTemplate("Чертеж", FormKind.Drawing, blue: new Scalar(255, 255, 255), customize: (page, f) =>
            SyntheticForms.DrawFrame(page, f.Designation.Inflate(-1) with { X = f.Designation.X + 21, Width = f.Designation.Width - 42 },
                FrameColors.Blue, Workbench.TemplateDpi));

        var second = Assert.Single(TemplateLibrary.LoadFolder(_wb.Templates)).Template!;
        var after = second.Frames.Single(f => f.Role == FieldRole.Designation).Inner;
        second.Dispose();
        Assert.True(after.X - before.X > 0.08);
    }
}
