using UglyToad.PdfPig;

namespace SmartBase.Tests.Extensions.PdfAnalyzer;

/// <summary>
/// Проверка САМОГО СТЕНДА, а не расширения: сгенерированный PdfPig-писателем документ должен
/// читаться обратно и отдавать заданные размеры страниц.
///
/// Тест стоит первым намеренно. Сквозные тесты движка целиком опираются на этот генератор, и
/// если писатель однажды перестанет отдавать читаемый документ (обновление пакета, смена API),
/// упадет один понятный тест здесь, а не два десятка непонятных в PdfAnalyzerRunTests.
/// </summary>
public sealed class PdfStandSmokeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sbext_pa_stand_{Guid.NewGuid():N}");

    public PdfStandSmokeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Generated_pdf_is_read_back()
    {
        var path = Path.Combine(_root, "документ.pdf");
        PdfStand.WriteA4(path);

        using var doc = PdfDocument.Open(path);

        Assert.Equal(1, doc.NumberOfPages);
    }

    [Fact]
    public void Page_count_matches_the_stand()
    {
        var path = Path.Combine(_root, "трехстраничный.pdf");
        PdfStand.Write(path, PdfStand.A4, PdfStand.A3, PdfStand.A0);

        using var doc = PdfDocument.Open(path);

        Assert.Equal(3, doc.NumberOfPages);
    }

    /// <summary>Размеры доезжают до документа с точностью, которой хватает распознаванию
    /// форматов (допуск движка — 30 мм, здесь проверяем куда жестче).</summary>
    [Fact]
    public void Page_size_survives_the_round_trip()
    {
        var path = Path.Combine(_root, "а3.pdf");
        PdfStand.Write(path, PdfStand.A3);

        using var doc = PdfDocument.Open(path);
        var page = doc.GetPage(1);

        Assert.Equal(297, page.Width  * 25.4 / 72, precision: 0);
        Assert.Equal(420, page.Height * 25.4 / 72, precision: 0);
    }

    [Fact]
    public void Broken_file_is_not_a_readable_document()
    {
        var path = Path.Combine(_root, "битый.pdf");
        PdfStand.WriteBroken(path);

        Assert.ThrowsAny<Exception>(() => PdfDocument.Open(path));
    }
}
