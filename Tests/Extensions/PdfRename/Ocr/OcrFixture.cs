using SmartBase.Extensions.PdfRename.Ocr;
using SmartBase.Extensions.PdfRename.Recognition;

namespace SmartBase.Tests.Extensions.PdfRename.Ocr;

/// <summary>
/// Настоящие движки (Tesseract и PaddleOCR) — одна пара на все тесты коллекции «OCR»:
/// создание PaddleOCR занимает полсекунды. Компоненты не готовы — тесты падают с понятной
/// причиной, а не пропускаются: проверка двумя движками должна быть настоящей.
/// </summary>
public sealed class OcrFixture : IDisposable
{
    private readonly Lazy<OcrPair> _pair = new(() =>
    {
        var problems = OcrComponents.CheckReady();
        if (problems.Count > 0)
            throw new InvalidOperationException("OCR не готов: " + string.Join("; ", problems) +
                ". На Linux: sudo apt install tesseract-ocr tesseract-ocr-rus");
        return OcrPair.CreateReal(OcrComponents.FindTessdata()!);
    });

    internal OcrPair Pair => _pair.Value;

    public void Dispose()
    {
        if (_pair.IsValueCreated) _pair.Value.Dispose();
    }
}

[CollectionDefinition("OCR")]
public sealed class OcrCollection : ICollectionFixture<OcrFixture>;
