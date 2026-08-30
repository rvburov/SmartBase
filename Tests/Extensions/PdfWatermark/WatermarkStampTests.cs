using SkiaSharp;
using SmartBase.Extensions.PdfWatermark;

namespace SmartBase.Tests.Extensions.PdfWatermark;

/// <summary>
/// Наложение знака.
///
/// Знак ВПЕЧАТАН в пиксели, поэтому и проверяется он по ПИКСЕЛЯМ: текстовых объектов в выданном
/// документе не остается вовсе, разбирать нечего. Тесты сравнивают страницу результата со
/// страницей исходника — ровно то, что увидит человек, — и отдельно проверяют, что знак
/// переживает самую дешевую атаку.
/// </summary>
public sealed class WatermarkStampTests
{
    private static readonly string[] Lines =
    [
        "Выдан: Иванов Иван Иванович",
        "Экз. № 3 · 29.08.2026",
        "Передача третьим лицам запрещена",
    ];

    // --- Угол и кегль: чистая математика, ни одного PDF -------------------------------------

    [Fact]
    public void Angle_on_a_square_page_is_exactly_45_degrees_up()
    {
        // Диагональ квадрата — 45°, минус означает «снизу вверх» (ось Y холста смотрит вниз).
        // Если знак когда-нибудь перевернется, упадет именно этот тест.
        Assert.Equal(-45, WatermarkStamp.AngleFor(300, 300), 6);
    }

    [Theory]
    [InlineData(595.3, 841.9, -54.7)]   // A4 портрет
    [InlineData(841.9, 595.3, -35.3)]   // A4 альбом
    public void Angle_follows_the_page_diagonal(double w, double h, double expected)
    {
        // Довороту страницы (/Rotate) здесь места нет: PDFium растеризует лист таким, каким его
        // видит читатель, поэтому холст всегда «как на экране». В прежнем пути через PDFsharp
        // холст был сырым MediaBox, и доворот приходилось снимать самим.
        Assert.Equal(expected, WatermarkStamp.AngleFor(w, h), 1);
    }

    [Fact]
    public void Longer_warning_makes_the_stamp_smaller()
    {
        // Кегль задан длиной самой длинной строки: она обязана уложиться в диагональ листа.
        var shorter = WatermarkStamp.FontSizeFor(["Экз. № 3"], 595, 842, WatermarkStamp.Default);
        var longer  = WatermarkStamp.FontSizeFor(Lines,        595, 842, WatermarkStamp.Default);

        Assert.True(shorter > longer, $"короткая надпись {shorter:0.#} pt, длинная {longer:0.#} pt");
    }

    [Fact]
    public void Stamp_grows_with_the_sheet()
    {
        var a4 = WatermarkStamp.FontSizeFor(Lines,  595,  842, WatermarkStamp.Default);
        var a1 = WatermarkStamp.FontSizeFor(Lines, 1684, 2384, WatermarkStamp.Default);

        Assert.True(a1 > a4 * 2, $"A4 {a4:0.#} pt, A1 {a1:0.#} pt — знак не вырос вместе с листом");
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
        using var clean   = PdfStand.Render(src);
        using var stamped = PdfStand.Render(dst);
        Assert.True(PdfStand.DiffShare(stamped, clean) > 1,
                    "знака не видно на странице результата");
    }

    [Fact]
    public void Stamp_survives_the_cheapest_attack()
    {
        using var dir = new TempDir("stamp");
        var (src, dst) = Pair(dir, "чертеж");
        PdfStand.WriteA4(src);

        Stamp(src, dst);

        using var clean   = PdfStand.Render(src);
        using var stamped = PdfStand.Render(dst);

        // Самая дешевая атака на растровый знак — «уровни» в любом бесплатном редакторе: все
        // светлее порога становится белым. Одну лишь светлую заливку она сгоняет начисто,
        // поэтому у знака есть контур, по плотности сравнимый с текстом документа.
        using var attacked = PdfStand.Levels(stamped, 230);
        using var reference = PdfStand.Levels(clean, 230);

        var before = PdfStand.DiffShare(stamped, clean);
        var after  = PdfStand.DiffShare(attacked, reference);

        Assert.True(after > before / 2,
                    $"знак снят чисткой: было {before:0.00}%, осталось {after:0.00}%");
    }

    [Fact]
    public void Stamp_is_grey_not_coloured()
    {
        using var dir = new TempDir("stamp");
        var (src, dst) = Pair(dir, "чертеж");
        PdfStand.WriteA4(src);

        Stamp(src, dst);

        using var clean   = PdfStand.Render(src);
        using var stamped = PdfStand.Render(dst);
        var (r, g, b) = PdfStand.AverageDiffColor(stamped, clean);

        // Серый — это равные составляющие. Цветной знак спорит с содержимым документа, а на
        // черно-белой печати становится грязным пятном.
        Assert.True(Math.Abs(r - g) < 6 && Math.Abs(g - b) < 6,
                    $"знак не серый: R{r:0} G{g:0} B{b:0}");
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
            using var clean   = PdfStand.Render(src, page);
            using var stamped = PdfStand.Render(dst, page);
            Assert.True(PdfStand.DiffShare(stamped, clean) > 1, $"страница {page} без знака");
        }
    }

    [Fact]
    public void Rotated_page_is_stamped_too()
    {
        using var dir = new TempDir("stamp");
        var (src, dst) = Pair(dir, "повернутый");
        PdfStand.WriteRotated(src, 90, PdfStand.A4);

        Stamp(src, dst);

        using var clean   = PdfStand.Render(src);
        using var stamped = PdfStand.Render(dst);
        Assert.True(PdfStand.DiffShare(stamped, clean) > 1, "на повернутой странице знака нет");
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
        using var clean   = PdfStand.Render(src);
        using var stamped = PdfStand.Render(dst);
        Assert.True(PdfStand.DiffShare(stamped, clean) > 1);
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
        Assert.InRange(share, 1, 25);
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
