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

    // ── Открытие ──────────────────────────────────────────────────────────────

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
