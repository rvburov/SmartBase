using System.IO;
using SkiaSharp;
using SmartBase.Core.Documents;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Страницы PDF для панели чтения (<see cref="DocPageSource"/>): размеры, картинка страницы и
/// слова с их местами.
///
/// Тесты интеграционные: работают два настоящих разборщика чужого формата — PDFium рисует, PdfPig
/// отдает слова, — и подменять их нечем. Главное, что сторожат проверки, — СОГЛАСИЕ этих двоих:
/// подсветка ставится по координатам от PdfPig поверх картинки от PDFium, и разъехаться им
/// нельзя. Разъезд был бы тихим: желтый прямоугольник честно лег бы на страницу, просто не на то
/// слово.
/// </summary>
public sealed class DocPageSourceTests : IDisposable
{
    private readonly string _dir;

    public DocPageSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb-docpage-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* занят — уберет система */ }
    }

    /// <summary>
    /// Документ, где на каждой странице ОДНО слово в известном месте: сверху слева. По нему
    /// проверяется и разбор, и перевод координат.
    /// </summary>
    private string WritePdf(string name, int pages = 1, double width = 595, double height = 842)
    {
        var path = Path.Combine(_dir, name);
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        for (var p = 1; p <= pages; p++)
        {
            var page = builder.AddPage(width, height);
            // 40 пунктов от левого края и 60 от верхнего.
            page.AddText($"Dogovor{p}", 14, new PdfPoint(40, height - 60), font);
        }
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    /// <summary>
    /// Документ из листов РАЗНОГО размера: книжный, вытянутый вдвое и альбомный. Аспекты у них
    /// разные намеренно — на одинаковых (А4 и А3 отличаются только величиной) расхождение не
    /// вылезло бы вовсе.
    /// </summary>
    private string WriteMixedPdf(string name)
    {
        var path = Path.Combine(_dir, name);
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var (width, height) in new[] { (595.0, 842.0), (595.0, 1400.0), (1191.0, 842.0) })
        {
            var page = builder.AddPage(width, height);
            page.AddText("Dogovor", 14, new PdfPoint(40, height - 60), font);
        }
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    // ── Открытие ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Листы разного размера выходят в ОДНУ запрошенную ширину — то самое «по ширине окна»,
    /// что и в любом просмотрщике, — а высота у каждого своя, по его собственной пропорции.
    ///
    /// Пока высота рамки вписывания считалась по ЗАПРОШЕННОЙ странице, рамка оставалась от той,
    /// что нарисовали первой, и лист с более вытянутой пропорцией PDFium вписывал уже по
    /// ВЫСОТЕ: тот выходил вдвое уже соседей, мельче текстом и с пустым местом по бокам. Какой
    /// именно лист задаст рамку, решал порядок прокрутки, поэтому один и тот же документ
    /// выглядел по-разному в зависимости от того, с какого места его открыли.
    /// </summary>
    [Fact]
    public void Pages_of_different_shapes_render_at_one_width()
    {
        using var source = DocPageSource.Open(WriteMixedPdf("разный.pdf"));
        Assert.NotNull(source);
        Assert.Equal(3, source!.PageCount);

        // Первым рисуем книжный лист — именно он раньше и задавал рамку всем остальным.
        var widths  = new int[3];
        var heights = new int[3];
        foreach (var i in new[] { 0, 1, 2 })
        {
            var png = source.Render(i, 400);
            Assert.NotNull(png);

            using var bitmap = SKBitmap.Decode(png);
            widths[i]  = bitmap.Width;
            heights[i] = bitmap.Height;
        }

        // Ширина одна на все листы. PDFium округляет вписывание, поэтому допуск в пиксель.
        Assert.InRange(widths[0], 399, 400);
        Assert.True(widths.Max() - widths.Min() <= 1,
                    $"ширины разошлись: {string.Join(", ", widths)}; высоты: {string.Join(", ", heights)}");

        // Высота — от пропорции САМОГО листа, а не от соседнего.
        foreach (var i in new[] { 0, 1, 2 })
        {
            var size     = source.Size(i);
            var expected = (int)Math.Round(widths[i] * size.Height / size.Width);
            Assert.InRange(heights[i], expected - 2, expected + 2);
        }

        // Вытянутый лист обязан выйти заметно выше книжного, а альбомный — ниже.
        Assert.True(heights[1] > heights[0], "вытянутый лист не выше книжного");
        Assert.True(heights[2] < heights[0], "альбомный лист не ниже книжного");
    }

    /// <summary>
    /// Картинка страницы закрашена целиком: снизу не остается незаполненной полосы. Так
    /// выглядел лист, «обрезанный наполовину», — холст строился по размерам страницы, а байт
    /// приходило меньше, и хвост оставался нетронутым.
    /// </summary>
    [Fact]
    public void A_rendered_page_has_no_untouched_tail()
    {
        using var source = DocPageSource.Open(WritePdf("целый.pdf"));
        Assert.NotNull(source);

        var png = source!.Render(0, 400);
        Assert.NotNull(png);

        using var bitmap = SKBitmap.Decode(png);
        Assert.True(bitmap.Height > 10, "страница вышла слишком низкой для проверки");

        // Нижняя строка листа должна быть непрозрачной: незакрашенный холст — прозрачный.
        for (var x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 20))
            Assert.True(bitmap.GetPixel(x, bitmap.Height - 1).Alpha > 0,
                        $"низ страницы не закрашен в точке {x}");
    }

    [Fact]
    public void Pdf_opens_with_its_pages_and_sizes()
    {
        using var source = DocPageSource.Open(WritePdf("doc.pdf", pages: 3));

        Assert.NotNull(source);
        Assert.Equal(3, source!.PageCount);
        Assert.Equal(595, source.Size(0).Width,  0);
        Assert.Equal(842, source.Size(0).Height, 0);
    }

    [Theory]
    [InlineData("текст.txt")]
    [InlineData("нет-такого.pdf")]
    public void Anything_but_a_readable_pdf_opens_as_null(string name)
    {
        var path = Path.Combine(_dir, name);
        if (name.EndsWith(".txt", StringComparison.Ordinal)) File.WriteAllText(path, "не документ");

        // Для панели это не сбой: режим «как в документе» просто останется недоступным.
        Assert.Null(DocPageSource.Open(path));
    }

    /// <summary>
    /// Том толще потолка показывается не весь, но знает, сколько в нем страниц: иначе панель
    /// не сказала бы «показаны страницы 1–300 из 302» и оборвала бы том молча.
    /// </summary>
    [Fact]
    public void A_tome_past_the_cap_knows_its_full_size()
    {
        using var source = DocPageSource.Open(WritePdf("tome.pdf", DocPageSource.MaxPages + 2));

        Assert.NotNull(source);
        Assert.Equal(DocPageSource.MaxPages, source.PageCount);
        Assert.Equal(DocPageSource.MaxPages + 2, source.TotalPages);
    }

    [Fact]
    public void A_short_document_is_shown_whole()
    {
        using var source = DocPageSource.Open(WritePdf("short.pdf", 3));

        Assert.NotNull(source);
        Assert.Equal(3, source.PageCount);
        Assert.Equal(3, source.TotalPages);
    }

    [Fact]
    public void Broken_pdf_opens_as_null()
    {
        var path = Path.Combine(_dir, "broken.pdf");
        File.WriteAllText(path, "%PDF-1.4 это не документ");

        Assert.Null(DocPageSource.Open(path));
    }

    // ── Картинка страницы ─────────────────────────────────────────────────────

    [Fact]
    public void Page_renders_at_the_asked_width()
    {
        using var source = DocPageSource.Open(WritePdf("doc.pdf"));
        var png = source!.Render(0, 400);

        Assert.NotNull(png);
        using var image = SKBitmap.Decode(png);
        // PDFium округляет вписывание, поэтому ширина может отличаться на пиксель — но не более.
        Assert.InRange(image.Width, 399, 400);
        // Пропорции листа сохранены.
        Assert.Equal(842.0 / 595, (double)image.Height / image.Width, 2);
    }

    /// <summary>
    /// Альбомная страница обязана нарисоваться. У миниатюр этот случай ронял отрисовку
    /// исключением, которое глотал catch, и документы молча оставались без картинки.
    /// </summary>
    [Fact]
    public void Landscape_page_renders_too()
    {
        using var source = DocPageSource.Open(WritePdf("land.pdf", width: 842, height: 595));
        var png = source!.Render(0, 400);

        Assert.NotNull(png);
        using var image = SKBitmap.Decode(png);
        Assert.True(image.Width > image.Height, "альбомная страница нарисована как книжная");
    }

    [Fact]
    public void Render_of_a_page_that_is_not_there_gives_null()
    {
        using var source = DocPageSource.Open(WritePdf("doc.pdf"));

        Assert.Null(source!.Render(-1, 400));
        Assert.Null(source.Render(99, 400));
    }

    // ── Слова и их места ──────────────────────────────────────────────────────

    [Fact]
    public void Words_come_with_their_boxes()
    {
        using var source = DocPageSource.Open(WritePdf("doc.pdf", pages: 2));

        var words = source!.Words(1);
        Assert.Contains(words, w => w.Text.Contains("Dogovor2", StringComparison.Ordinal));

        var word = words.First(w => w.Text.Contains("Dogovor2", StringComparison.Ordinal));
        Assert.True(word.Box.Width  > 0, "у слова нулевая ширина");
        Assert.True(word.Box.Height > 0, "у слова нулевая высота");
    }

    /// <summary>
    /// Слово, написанное в 40 пунктах от левого края и в 60 от ВЕРХНЕГО, обязано оказаться в
    /// левом верхнем углу показанной страницы. Это и есть проверка согласия двух разборщиков:
    /// координаты дает PdfPig, размер картинки — PDFium, а сводит их
    /// <see cref="DocPageGeometry"/>.
    /// </summary>
    [Fact]
    public void Word_box_lands_where_the_word_is_drawn()
    {
        using var source = DocPageSource.Open(WritePdf("doc.pdf"));
        var size = source!.Size(0);
        var word = source.Words(0).First(w => w.Text.Contains("Dogovor", StringComparison.Ordinal));

        const double DisplayWidth = 595;   // показываем один к одному с пунктами
        var scale  = DocPageGeometry.ScaleFor(DisplayWidth, size.Width);
        var screen = DocPageGeometry.ToScreen(word.Box, size.Height, scale);

        Assert.InRange(screen.X, 35, 50);   // 40 пунктов от левого края
        Assert.InRange(screen.Y, 45, 75);   // 60 пунктов от верхнего
        Assert.True(screen.Y < size.Height / 3, "слово из шапки уехало в середину листа");
    }

    [Fact]
    public void Words_of_a_page_that_is_not_there_are_empty()
    {
        using var source = DocPageSource.Open(WritePdf("doc.pdf"));

        Assert.Empty(source!.Words(-1));
        Assert.Empty(source.Words(42));
    }

    /// <summary>
    /// После закрытия сеанс молчит, а не падает: панель закрывают в любой момент, в том числе
    /// пока фоновая задача рисует страницу.
    /// </summary>
    [Fact]
    public void Closed_source_answers_with_nothing()
    {
        var source = DocPageSource.Open(WritePdf("doc.pdf"));
        source!.Dispose();

        Assert.Null(source.Render(0, 400));
        Assert.Empty(source.Words(0));
        source.Dispose();   // повторное закрытие — тоже не беда
    }
}
