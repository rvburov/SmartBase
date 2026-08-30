namespace SmartBase.Extensions.PdfWatermark;

/// <summary>
/// Отказ расширения с текстом, ГОТОВЫМ к показу человеку. Наружу из расширения не выходит
/// ничего другого: ни <c>PdfReaderException</c> из PDFsharp, ни <c>IOException</c> — их
/// формулировки написаны для разработчика библиотеки, а не для того, кто выдает документы.
/// Тот же прием, что у <c>ExcelOpenException</c>, <c>PdfAnalyzeException</c> и
/// <c>DocRegistryException</c> у соседей.
/// </summary>
public sealed class PdfWatermarkException(string message, Exception? inner = null)
    : Exception(message, inner);
