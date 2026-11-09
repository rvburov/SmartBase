using OpenCvSharp;
using SmartBase.Extensions.PdfRename.Ocr;
using SmartBase.Extensions.PdfRename.Recognition;
using SmartBase.Extensions.PdfRename.Rename;
using SmartBase.Extensions.PdfRename.Templates;
using SmartBase.Tests.Extensions.PdfRename.Samples;

namespace SmartBase.Tests.Extensions.PdfRename.Ocr;

/// <summary>
/// Группы ТЗ 8, 9, 10, 13 и сквозной прогон — с НАСТОЯЩИМИ Tesseract и PaddleOCR на
/// синтетических сканах. Главное свойство: документ либо переименован верно, либо пропущен;
/// неверного имени не бывает. Точность на реальных сканах эти тесты не подтверждают — для
/// этого нужны образцы (см. документацию расширения).
/// </summary>
[Collection("OCR")]
public sealed class OcrEndToEndTests(OcrFixture ocr) : IDisposable
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

    private DocumentResult Read(string path) => DocumentReader.Read(path, _templates, ocr.Pair, CancellationToken.None);

    [OcrFact]
    public void Both_engines_really_run_and_read_cyrillic_latin_and_digits()
    {
        using var page = SyntheticForms.Render(FormKind.Drawing, "ЮИЛГ.461411.123", "Plata ЦОС-2 rev.1", 300);
        var f = SyntheticForms.Fields(FormKind.Drawing);
        var px = 300 / 25.4;
        using var nameCrop = new Mat(page, new Rect((int)(f.Name.X * px), (int)(f.Name.Y * px), (int)(f.Name.Width * px), (int)(f.Name.Height * px)));
        using var desCrop = new Mat(page, new Rect((int)(f.Designation.X * px), (int)(f.Designation.Y * px), (int)(f.Designation.Width * px), (int)(f.Designation.Height * px)));
        using var nameGray = FieldCrop.Prepare(nameCrop);
        using var desGray = FieldCrop.Prepare(desCrop);

        foreach (var engine in new[] { ocr.Pair.First, ocr.Pair.Second })
        {
            var min = OcrPair.MinConfidence(engine);
            Assert.Equal("ЮИЛГ.461411.123", FieldParser.ParseDesignation(engine.Read(desGray), min).Value);
            Assert.Equal("Plata ЦОС-2 rev.1", FieldParser.ParseName(engine.Read(nameGray), min).Value);
        }
        Assert.IsType<TesseractEngine>(ocr.Pair.First);
        Assert.IsType<PaddleEngine>(ocr.Pair.Second);
    }

    [OcrTheory]
    [InlineData(0, "ЮИЛГ.301318.001-01", "Плата управления")]
    [InlineData(1, "ВКЕМ.461411.124", "Корпус")]
    [InlineData(2, "ПРМД.461411.125СБ", "Блок питания")]
    [InlineData(3, "ЮИЛГ.461411.126", "Крышка\nверхняя")]
    public void Separate_fields_on_rotated_scans(int turns, string designation, string name)
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        _wb.AddTemplate("Спецификация", FormKind.Specification);
        Load();
        var path = _wb.AddDocument("doc.pdf", FormKind.Drawing, designation, name,
            new ScanOptions { QuarterTurns = turns, SkewDegrees = turns == 0 ? 0 : 1.2, Dpi = 250 });

        var result = Read(path);

        Assert.True(result.Values is not null, result.Problem);
        Assert.Equal(designation, result.Values!.Designation);
        Assert.Equal(name.Replace('\n', ' '), result.Values.Name);
        Assert.Equal("Чертеж", result.Template);
    }

    [OcrTheory]
    [InlineData("АБРС.713656.909", "Корпус")]           // PaddleOCR путает Б и В
    [InlineData("БВЖЗ.275158.272", "Кронштейн")]        // З и цифра 3
    [InlineData("ЮИЛГ.461411.123", "Щит распределительный")] // Щ и Ц
    public void Known_engine_confusions_give_a_skip_not_a_wrong_name(string designation, string name)
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        Load();
        var result = Read(_wb.AddDocument("doc.pdf", FormKind.Drawing, designation, name));

        if (result.Values is not null) Assert.Equal(new FieldValues(designation, name), result.Values);
        else Assert.Matches("по-разному|не подтверждено", result.Problem);
    }

    [OcrFact]
    public void Specification_template_is_chosen_for_specification()
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        _wb.AddTemplate("Спецификация", FormKind.Specification);
        Load();
        var result = Read(_wb.AddDocument("s.pdf", FormKind.Specification, "ВКЕМ.461411.200", "Модуль связи", new ScanOptions { Dpi = 300 }));
        Assert.Equal(new FieldValues("ВКЕМ.461411.200", "Модуль связи"), result.Values);
        Assert.Equal("Спецификация", result.Template);
    }

    [OcrFact]
    public void Combined_red_orange_area_is_split_into_designation_and_name()
    {
        _wb.AddTemplate("Чертеж A3", FormKind.DrawingA3, Marking.Combined);
        Load();
        var result = Read(_wb.AddDocument("a3.pdf", FormKind.DrawingA3, "ПРМД.301261.010", "Рама сварная", new ScanOptions { Dpi = 200 }));
        Assert.True(result.Values is not null, result.Problem);
        Assert.Equal(new FieldValues("ПРМД.301261.010", "Рама сварная"), result.Values);
    }

    [OcrTheory]
    [InlineData(1, 0.06, 1.2, 55, 150)]
    [InlineData(2, 0.09, 1.6, 40, 120)]
    [InlineData(3, 0.12, 2.2, 30, 100)]
    public void Bad_scans_are_either_read_correctly_or_skipped(int seed, double noise, double blur, int jpeg, int dpi)
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        Load();
        const string designation = "ВКЕМ.461411.123";
        const string name = "Кронштейн";
        var path = _wb.AddDocument("bad.pdf", FormKind.Drawing, designation, name,
            new ScanOptions { Noise = noise, Blur = blur, JpegQuality = jpeg, Dpi = dpi, SkewDegrees = seed * 0.7 }, seed);

        var result = Read(path);

        if (result.Values is not null)
            Assert.Equal(new FieldValues(designation, name), result.Values);   // неверного имени не бывает
        else
            Assert.False(string.IsNullOrWhiteSpace(result.Problem));            // пропуск — с причиной
    }

    [OcrFact]
    public void Multipage_consistent_pages_are_accepted()
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        Load();
        using var first = SyntheticForms.Render(FormKind.Drawing, "ЮИЛГ.461411.123", "Корпус", 200, seed: 1);
        using var second = SyntheticForms.Render(FormKind.Drawing, "ЮИЛГ.461411.123", "Корпус", 200, seed: 2);
        using var text = new Mat(first.Size(), MatType.CV_8UC3, Scalar.All(255));
        Cv2.PutText(text, "Lorem ipsum 12345", new Point(200, 400), HersheyFonts.HersheySimplex, 2, Scalar.All(0), 3);

        var result = Read(_wb.AddPages("multi.pdf", [first, text, second], 200));

        Assert.True(result.Values is not null, result.Problem);
        Assert.Equal(new FieldValues("ЮИЛГ.461411.123", "Корпус"), result.Values);
        Assert.Equal("1, 3", result.Pages);
    }

    [OcrFact]
    public void Multipage_with_contradicting_pages_is_skipped()
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        Load();
        using var first = SyntheticForms.Render(FormKind.Drawing, "ЮИЛГ.461411.123", "Корпус", 200, seed: 1);
        using var second = SyntheticForms.Render(FormKind.Drawing, "ЮИЛГ.461411.999", "Крышка", 200, seed: 2);

        var result = Read(_wb.AddPages("two-docs.pdf", [first, second], 200));

        Assert.Null(result.Values);
        Assert.Contains("разные данные", result.Problem);
    }

    [OcrFact]
    public void Fields_are_not_assumed_to_be_on_the_first_page()
    {
        _wb.AddTemplate("Спецификация", FormKind.Specification);
        Load();
        using var cover = new Mat(2339, 1654, MatType.CV_8UC3, Scalar.All(255));
        Cv2.PutText(cover, "COVER", new Point(500, 1000), HersheyFonts.HersheySimplex, 4, Scalar.All(0), 6);
        using var spec = SyntheticForms.Render(FormKind.Specification, "ВКЕМ.461411.200", "Модуль", 200);

        var result = Read(_wb.AddPages("cover.pdf", [cover, spec], 200));

        Assert.True(result.Values is not null, result.Problem);
        Assert.Equal(new FieldValues("ВКЕМ.461411.200", "Модуль"), result.Values);
        Assert.Equal("2", result.Pages);
    }

    [OcrFact]
    public void Damaged_empty_and_non_pdf_files_are_skipped_with_reason()
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        Load();
        var empty = Path.Combine(_wb.Source, "empty.pdf");
        File.WriteAllBytes(empty, []);
        var text = Path.Combine(_wb.Source, "text.pdf");
        File.WriteAllText(text, "это не PDF");
        var good = _wb.AddDocument("cut.pdf", FormKind.Drawing, "АБВГ.461411.123", "Корпус");
        var bytes = File.ReadAllBytes(good);
        File.WriteAllBytes(good, bytes[..(bytes.Length / 3)]);   // оборванный файл

        Assert.Contains("пустой", Read(empty).Problem);
        Assert.Contains("не является PDF", Read(text).Problem);
        Assert.Null(Read(good).Values);
        Assert.Contains("не найден", Read(Path.Combine(_wb.Source, "нет такого.pdf")).Problem);
    }

    [OcrFact]
    public void Full_run_renames_correct_files_only()
    {
        // Шаблоны лежат внутри обрабатываемого дерева — сами не переименовываются.
        var templates = Path.Combine(_wb.Source, "Шаблоны");
        Directory.CreateDirectory(templates);
        _wb.AddTemplate("Чертеж", FormKind.Drawing, folder: templates);
        _wb.AddTemplate("Спецификация", FormKind.Specification, folder: templates);
        var a = _wb.AddDocument(Path.Combine("Изделие 1", "001.pdf"), FormKind.Drawing, "ЮИЛГ.461411.123", "Корпус");
        var b = _wb.AddDocument(Path.Combine("Изделие 2", "001.pdf"), FormKind.Specification, "ВКЕМ.461411.200", "Модуль связи", new ScanOptions { QuarterTurns = 1 });
        using var blank = new Mat(2339, 1654, MatType.CV_8UC3, Scalar.All(255));
        var c = _wb.AddPages("пустой.pdf", [blank], 200);
        var hashA = File.ReadAllBytes(a);

        var sink = new CollectingSink();
        var summary = OcrRenameRun.Execute(new RunSettings(templates, _wb.Source, Recursive: true, Workers: 2),
            () => OcrPair.CreateReal(OcrComponents.FindTessdata()!), sink, CancellationToken.None);

        Assert.Null(summary.Fatal);
        Assert.Equal(3, summary.Found);
        Assert.True(summary.Renamed == 2, string.Join("\n", sink.Entries.Select(e => $"{e.RelativePath}: {e.Status} {e.NewName} {e.Reason}")));
        var renamedA = Path.Combine(_wb.Source, "Изделие 1", "ЮИЛГ.461411.123 Корпус.pdf");
        Assert.True(File.Exists(renamedA));
        Assert.Equal(hashA, File.ReadAllBytes(renamedA));
        Assert.True(File.Exists(Path.Combine(_wb.Source, "Изделие 2", "ВКЕМ.461411.200 Модуль связи.pdf")));
        Assert.True(File.Exists(c));
        Assert.True(File.Exists(Path.Combine(templates, "Чертеж.pdf")));
        Assert.True(File.Exists(Path.Combine(templates, "Спецификация.pdf")));
        Assert.Contains(sink.Messages, m => m.Contains("Шаблон «Чертеж»"));
        Assert.Contains(sink.Entries, e => e.RelativePath == "пустой.pdf" && e.Status == FileStatus.Skipped);
    }

    [OcrFact]
    public void Missing_ocr_component_stops_the_run_honestly()
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        var doc = _wb.AddDocument("a.pdf", FormKind.Drawing, "АБВГ.461411.123", "Корпус");
        var sink = new CollectingSink();

        var summary = OcrRenameRun.Execute(new RunSettings(_wb.Templates, _wb.Source, false, 1),
            () => throw new DllNotFoundException("libtesseract.so.5"), sink, CancellationToken.None);

        Assert.NotNull(summary.Fatal);
        Assert.Contains("OCR не запускается", summary.Fatal);
        Assert.Equal(0, summary.Renamed);
        Assert.True(File.Exists(doc));
    }

    private sealed class CollectingSink : IRunSink
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Messages = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<JournalEntry> Entries = new();
        public void Message(string text) => Messages.Enqueue(text);
        public void Found(int count) { }
        public void File(JournalEntry entry) => Entries.Enqueue(entry);
    }
}
