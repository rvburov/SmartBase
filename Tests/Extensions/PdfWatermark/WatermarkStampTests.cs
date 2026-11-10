using SkiaSharp;
using SmartBase.Extensions.PdfWatermark;

namespace SmartBase.Tests.Extensions.PdfWatermark;

/// <summary>
/// Наложение знака.
///
/// Знак ВПЕЧАТАН в пиксели, поэтому и проверяется он по ПИКСЕЛЯМ: текстовых объектов в выданном
/// документе не остается вовсе, разбирать нечего. Тесты сравнивают страницу результата со
/// страницей исходника — ровно то, что увидит человек, — и отдельно проверяют светлую
/// сплошную заливку без темного контура букв.
/// </summary>
public sealed class WatermarkStampTests
{
    private static readonly string[] Lines =
    [
        "КОПИЯ",
        "Экземпляр № 3",
        "Выдан: Иванов Иван Иванович",
        "Дата выдачи: 29.08.2026",
        "Передача третьим лицам запрещена",
    ];

    // --- Угол и кегль: чистая математика, ни одного PDF -------------------------------------

    [Fact]
    public void Angle_on_a_square_page_is_the_approved_35_degrees_up()
    {
        // Угол согласован независимо от пропорций листа; минус означает «снизу вверх».
        Assert.Equal(-35, WatermarkStamp.AngleFor(300, 300), 6);
    }

    [Theory]
    [InlineData(595.3, 841.9, -35)]   // A4 портрет
    [InlineData(841.9, 595.3, -35)]   // A4 альбом
    public void Approved_angle_is_the_same_on_portrait_and_landscape_pages(double w, double h, double expected)
    {
        // Довороту страницы (/Rotate) здесь места нет: PDFium растеризует лист таким, каким его
        // видит читатель, поэтому холст всегда «как на экране». В прежнем пути через PDFsharp
        // холст был сырым MediaBox, и доворот приходилось снимать самим.
        Assert.Equal(expected, WatermarkStamp.AngleFor(w, h), 1);
    }

    [Fact]
    public void Long_warning_wraps_inside_the_block_at_a_readable_size()
    {
        const string warning = "Передача третьим лицам запрещена. Документ предназначен для внутреннего использования.";
        var block = StampTextLayout.Compose([warning], 75 * 4, 50 * 4, 4, WatermarkStamp.Default);
        Assert.True(block.Lines.Count > 1);
        Assert.Equal(warning, string.Join(" ", block.Lines.Select(line => line.Text)));
        Assert.InRange(block.FontPt, 10, 30);
        Assert.True(block.Height <= 50 * 4);
    }

    [Fact]
    public void Block_dimensions_remain_physical_on_a_larger_sheet()
    {
        var a4 = StampLayout.Default.Plan(210 * 4, 297 * 4, 4);
        var a1 = StampLayout.Default.Plan(594 * 4, 841 * 4, 4);
        Assert.Equal(a4.Width, a1.Width);
        Assert.Equal(a4.Height, a1.Height);
    }

    // --- Разрешение растра -------------------------------------------------------------------

    [Fact]
    public void Text_page_is_rendered_at_target_dpi()
    {
        // Родного разрешения у текстовой страницы нет, поэтому берется целевое — 200 dpi.
        Assert.Equal(200 / 72.0, PageRaster.ScaleForPage(595, 842, nativeWidthPx: 0), 3);
    }

    [Fact]
    public void Scan_is_not_blown_up_beyond_its_own_pixels()
    {
        // Скан с телефона: страница объявлена в 1877 pt и содержит снимок ровно в 1877 пикселей.
        // Рисовать его крупнее снимка — только раздувать файл, ничего не добавляя к картинке.
        Assert.Equal(1.0, PageRaster.ScaleForPage(1877, 2681, nativeWidthPx: 1877), 3);
    }

    [Fact]
    public void Huge_sheet_is_capped_by_memory()
    {
        // Лист в 4000 pt при 200 dpi дал бы 11 000 пикселей по длинной стороне — это сотни
        // мегабайт на страницу. Упор держит растр в разумных пределах.
        var scale = PageRaster.ScaleForPage(3000, 4000, nativeWidthPx: 0);

        Assert.True(4000 * scale <= PageRaster.MaxLongSidePx + 1,
                    $"длинная сторона вышла {4000 * scale:0} px");
    }

    [Fact]
    public void Small_picture_does_not_set_the_resolution_of_the_page()
    {
        // Герб или печать в 300 пикселей на странице договора — не скан. Прими расширение это
        // за родное разрешение, и лист отрисовался бы в 300 пикселей шириной: документ пропал
        // бы целиком. Порог NativeFloorPx ровно об этом.
        Assert.Equal(200 / 72.0, PageRaster.ScaleForPage(595, 842, nativeWidthPx: 300), 3);
    }

    // --- Сквозные проверки на настоящих файлах -----------------------------------------------

    [Fact]
    public void Stamp_is_burned_into_the_pixels()
    {
        using var dir = new TempDir("stamp");
        var (src, dst) = Pair(dir, "чертеж");
        PdfStand.WriteA4(src);

        var pages = Stamp(src, dst);

        Assert.Equal(1, pages);

        // Текста в выданном документе НЕ ОСТАЕТСЯ — ни знака, ни документа. Это и есть то
        // свойство, ради которого расширение переведено на впечатывание: снимать нечего.
        Assert.Equal("", PdfStand.TextOf(dst));

        // А в пикселях знак есть.
        using var clean   = PdfStand.Render(src, scale: 1);
        using var stamped = PdfStand.Render(dst, scale: 1);
        Assert.True(PdfStand.DiffPixels(stamped, clean, tolerance: 15) > 1_000,
                    "знака не видно на странице результата");
    }

    [Theory]
    [InlineData(StampPlacement.Center)]
    [InlineData(StampPlacement.Tiled)]
    public void Letters_have_a_single_light_grey_blue_fill_without_dark_edges(StampPlacement placement)
    {
        using var bitmap = new SKBitmap(840, 1188);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            WatermarkStamp.Draw(canvas, bitmap.Width, bitmap.Height, Lines, WatermarkStamp.Default,
                                new StampLayout(placement), pixelsPerMm: 4);
        }

        var ink = bitmap.Pixels.Where(pixel => pixel.Red < 250).ToArray();
        Assert.True(ink.Length > 500, "светлая надпись должна быть видна");
        Assert.All(ink, pixel =>
        {
            // Заливка серо-голубая, сглаживание лишь приближает края к белому.
            // Редкие стыки очертаний могут быть темнее основной заливки, но темной
            // обводки вокруг букв быть не должно.
            Assert.True(pixel.Red <= pixel.Green && pixel.Green <= pixel.Blue);
            Assert.InRange(pixel.Red, (byte)190, (byte)249);
        });
        Assert.True(ink.Count(pixel => pixel.Red < 210) * 1_000 < ink.Length,
                    "темные пиксели образуют контур вместо единой светлой заливки");
        Assert.InRange(ink.Min(pixel => pixel.Red), (byte)190, (byte)220);
        Assert.True(ink.Count(pixel => pixel.Blue - pixel.Red >= 10) > ink.Length / 2,
                    "заливка потеряла согласованный серо-голубой оттенок");
    }

    [Fact]
    public void Saved_pdf_preserves_the_light_letter_fill()
    {
        using var dir = new TempDir("stamp");
        var (src, dst) = Pair(dir, "чертеж");
        PdfStand.WriteA4(src);

        Stamp(src, dst);

        // Проверяем уже сохраненный и заново открытый PDF. Допуск учитывает JPEG и
        // повторное сглаживание просмотрщика, но прежняя темная обводка сюда не проходит.
        using var stamped = PdfStand.Render(dst, scale: 2);
        var ink = stamped.Pixels.Where(pixel => pixel.Red < 245).ToArray();
        Assert.True(ink.Length > 1_000, "знака не видно после сохранения PDF");
        Assert.InRange(ink.Min(pixel => pixel.Red), (byte)185, (byte)215);
    }

    [Fact]
    public void Saved_stamp_has_the_approved_light_grey_blue_tone()
    {
        using var dir = new TempDir("stamp");
        var (src, dst) = Pair(dir, "чертеж");
        PdfStand.WriteA4(src);

        Stamp(src, dst);

        using var clean   = PdfStand.Render(src, scale: 1);
        using var stamped = PdfStand.Render(dst, scale: 1);
        var (r, g, b) = PdfStand.AverageDiffColor(stamped, clean, tolerance: 15);

        // Проверяем сохраненный PDF, включая JPEG и повторное сглаживание просмотрщика.
        // Оттенок холодный серо-голубой, без возврата к нейтральному серому или темному синему.
        Assert.InRange(r, 200, 242);
        Assert.InRange(g - r, 4, 14);
        Assert.InRange(b - g, 3, 13);
        Assert.InRange(b - r, 9, 25);
    }

    [Fact]
    public void Page_size_survives_rasterization()
    {
        using var dir = new TempDir("stamp");
        var (src, dst) = Pair(dir, "форматы");
        PdfStand.Write(src, PdfStand.A4, PdfStand.A3);

        Stamp(src, dst);

        // Лист остается тем же в пунктах, сколько бы пикселей ни было в картинке: иначе
        // выданный документ печатался бы в другом масштабе.
        var (w1, h1) = PdfStand.PageSize(src);
        var (w2, h2) = PdfStand.PageSize(dst);
        Assert.Equal(w1, w2, 0);
        Assert.Equal(h1, h2, 0);

        var (w3, h3) = PdfStand.PageSize(src, 2);
        var (w4, h4) = PdfStand.PageSize(dst, 2);
        Assert.Equal(w3, w4, 0);
        Assert.Equal(h3, h4, 0);
    }

    [Fact]
    public void Every_page_gets_the_stamp()
    {
        using var dir = new TempDir("stamp");
        var (src, dst) = Pair(dir, "альбом");
        PdfStand.Write(src, PdfStand.A4, PdfStand.A3, PdfStand.A1, PdfStand.A4Land);

        var pages = Stamp(src, dst);

        Assert.Equal(4, pages);
        Assert.Equal(4, PdfStand.PageCount(dst));

        for (var page = 1; page <= 4; page++)
        {
            using var clean   = PdfStand.Render(src, page, scale: 1);
            using var stamped = PdfStand.Render(dst, page, scale: 1);
            Assert.True(PdfStand.DiffPixels(stamped, clean, tolerance: 15) > 1_000, $"страница {page} без знака");
        }
    }

    [Fact]
    public void Rotated_page_is_stamped_too()
    {
        using var dir = new TempDir("stamp");
        var (src, dst) = Pair(dir, "повернутый");
        PdfStand.WriteRotated(src, 90, PdfStand.A4);

        Stamp(src, dst);

        using var clean   = PdfStand.Render(src, scale: 1);
        using var stamped = PdfStand.Render(dst, scale: 1);
        Assert.True(PdfStand.DiffPixels(stamped, clean, tolerance: 15) > 1_000, "на повернутой странице знака нет");
    }

    [Fact]
    public void Old_pdf_version_is_handled()
    {
        using var dir = new TempDir("stamp");
        var (src, dst) = Pair(dir, "скан из телефона");
        PdfStand.WriteOldVersion(src, 13);
        Assert.Equal("1.3", PdfStand.VersionOf(src));

        Stamp(src, dst);

        // PDF 1.3 выдают сканеры и «Файлы» на iOS — самые частые здесь документы. На прежнем
        // пути они были отдельной бедой: прозрачности в 1.3 нет, и знак ложился глухим. Растру
        // версия исходника безразлична, но проверка остается — документы-то те же.
        using var clean   = PdfStand.Render(src, scale: 1);
        using var stamped = PdfStand.Render(dst, scale: 1);
        Assert.True(PdfStand.DiffPixels(stamped, clean, tolerance: 15) > 1_000);
    }

    [Fact]
    public void Source_file_is_left_untouched()
    {
        using var dir = new TempDir("stamp");
        var (src, dst) = Pair(dir, "исходник");
        PdfStand.WriteA4(src);
        var before = File.ReadAllBytes(src);

        Stamp(src, dst);

        // Побайтово: обещание «расширения не изменяют исходные документы» дано пользователю в
        // документации, и держать его должен тест, а не аккуратность.
        Assert.Equal(before, File.ReadAllBytes(src));
    }

    // --- Отказы -----------------------------------------------------------------------------

    [Fact]
    public void Broken_file_gives_our_exception_not_the_libraries()
    {
        using var dir = new TempDir("stamp");
        var (src, dst) = Pair(dir, "битый");
        PdfStand.WriteBroken(src);

        var ex = Assert.Throws<PdfWatermarkException>(() => WatermarkStamp.Open(src));

        Assert.Equal("файл не является PDF или поврежден", ex.Message);
        Assert.False(File.Exists(dst));
    }

    [Fact]
    public void Password_protected_file_says_so()
    {
        using var dir = new TempDir("stamp");
        var (src, _) = Pair(dir, "под паролем");
        PdfStand.WriteProtected(src);

        var ex = Assert.Throws<PdfWatermarkException>(() => WatermarkStamp.Open(src));

        Assert.Equal("документ защищен паролем", ex.Message);
    }

    [Fact]
    public void Cancellation_leaves_no_half_written_result()
    {
        using var dir = new TempDir("stamp");
        var (src, dst) = Pair(dir, "отмена");
        PdfStand.Write(src, PdfStand.A4, PdfStand.A3);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Stamp(src, dst, cts.Token));

        // Обрезанный PDF на диске выглядел бы как готовый документ — этого быть не должно.
        Assert.False(File.Exists(dst));
    }

    [Fact]
    public void Stamp_without_text_is_refused()
    {
        using var dir = new TempDir("stamp");
        var (src, dst) = Pair(dir, "пустой знак");
        PdfStand.WriteA4(src);

        using var doc = WatermarkStamp.Open(src);

        // Молча сохранить копию было бы хуже всего: человек получил бы документ БЕЗ знака,
        // считая, что знак на нем есть.
        Assert.Throws<ArgumentException>(() =>
            WatermarkStamp.StampAndSave(src, doc, dst, [], CancellationToken.None));
        Assert.False(File.Exists(dst));
    }

    // --- Шрифт -------------------------------------------------------------------------------

    [Fact]
    public void Embedded_font_is_truetype_and_readable_by_skia()
    {
        var bytes = WatermarkFont.Bytes;

        // 00 01 00 00 — очертания TrueType (glyf).
        Assert.Equal<byte[]>([0x00, 0x01, 0x00, 0x00], bytes[..4]);
        Assert.True(bytes.Length > 100_000);

        // И Skia их принимает: без этого знак рисовался бы подстановочным шрифтом системы, а на
        // чужой машине — чем придется.
        Assert.NotNull(WatermarkFont.Typeface);
        Assert.True(WatermarkFont.Typeface.CountGlyphs("Экз. № 3") > 0, "кириллицы в шрифте нет");
    }

    [Fact]
    public void Draw_puts_the_stamp_on_a_bare_canvas()
    {
        // Отрисовка проверяется без PDF вокруг нее: холст, знак, счет пикселей.
        using var bitmap = new SKBitmap(new SKImageInfo(600, 850, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            WatermarkStamp.Draw(canvas, 600, 850, Lines, WatermarkStamp.Default);
        }

        var touched = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            if (bitmap.GetPixel(x, y).Red < 250) touched++;

        var share = touched * 100.0 / (bitmap.Width * bitmap.Height);
        Assert.True(touched > 1_000, "знак не появился на холсте");
        Assert.InRange(share, 0.5, 10);
    }

    /// <summary>Тот же путь, которым идет прогон: предполет открывает документ, запись идет по
    /// нему же.</summary>
    private static int Stamp(string source, string output, CancellationToken ct = default)
    {
        using var doc = WatermarkStamp.Open(source);
        return WatermarkStamp.StampAndSave(source, doc, output, Lines, ct);
    }

    private static (string Source, string Output) Pair(TempDir dir, string name) =>
        (Path.Combine(dir.Path, $"{name}.pdf"), Path.Combine(dir.Path, $"{name} экз. 3.pdf"));
}
