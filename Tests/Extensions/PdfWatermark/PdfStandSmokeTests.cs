namespace SmartBase.Tests.Extensions.PdfWatermark;

/// <summary>
/// Сторож самого стенда. Сломается генератор PDF — упадет один понятный тест, а не два десятка
/// непонятных в наборах, которые им пользуются. Тот же прием, что у «Анализа PDF файлов».
/// </summary>
public sealed class PdfStandSmokeTests
{
    [Fact]
    public void Writes_a_readable_pdf_with_the_requested_pages()
    {
        using var dir = new TempDir("stand");
        var path = Path.Combine(dir.Path, "три страницы.pdf");

        PdfStand.Write(path, PdfStand.A4, PdfStand.A3, PdfStand.A1);

        Assert.Equal(3, PdfStand.PageCount(path));
    }

    [Fact]
    public void Page_size_survives_the_millimeter_to_point_conversion()
    {
        using var dir = new TempDir("stand");
        var path = Path.Combine(dir.Path, "a4.pdf");
        PdfStand.WriteA4(path);

        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var page = doc.Pages[0];

        // 210 x 297 мм в пунктах. Допуск — округление в самом переводе, а не «примерно так».
        Assert.Equal(595.3, page.Width.Point,  1);
        Assert.Equal(841.9, page.Height.Point, 1);
    }

    [Fact]
    public void Rotated_pages_carry_the_rotate_key()
    {
        using var dir = new TempDir("stand");
        var path = Path.Combine(dir.Path, "повернутая.pdf");

        PdfStand.WriteRotated(path, 90, PdfStand.A4);

        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(90, doc.Pages[0].Rotate);
    }

    [Fact]
    public void Protected_document_really_asks_for_a_password()
    {
        using var dir = new TempDir("stand");
        var path = Path.Combine(dir.Path, "под паролем.pdf");

        PdfStand.WriteProtected(path);

        Assert.Throws<PdfSharp.Pdf.IO.PdfReaderException>(
            () => PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify));
    }

    [Fact]
    public void Broken_file_is_not_a_pdf()
    {
        using var dir = new TempDir("stand");
        var path = Path.Combine(dir.Path, "битый.pdf");

        PdfStand.WriteBroken(path);

        Assert.ThrowsAny<Exception>(
            () => PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify));
    }

    [Fact]
    public void Clean_page_renders_white_and_matches_itself()
    {
        using var dir = new TempDir("stand");
        var path = Path.Combine(dir.Path, "чистый.pdf");
        PdfStand.WriteA4(path);

        using var once  = PdfStand.Render(path);
        using var twice = PdfStand.Render(path);

        // Сторож пиксельных проверок: они меряют РАСХОЖДЕНИЕ страницы со знаком и без него, и
        // если рендер сам по себе нестабилен или отдает страницу черной (PDFium рисует
        // незакрашенные места ПРОЗРАЧНЫМИ), все такие проверки станут ложными.
        Assert.Equal(0, PdfStand.DiffShare(once, twice));
        Assert.True(once.GetPixel(once.Width / 2, 5).Red > 240, "чистая страница отрисовалась не белой");
    }
}
