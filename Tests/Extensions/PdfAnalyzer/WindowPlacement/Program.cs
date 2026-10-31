using Avalonia.Controls;
using SmartBase.Extensions.PdfAnalyzer;
using SmartBase.Tests.Extensions.WindowPlacement;

namespace SmartBase.Tests.Extensions.PdfAnalyzer.WindowPlacement;

internal static class Program
{
    // Реальные строки окна без чтения PDF и записи отчета.
    [STAThread]
    private static int Main() => WindowPlacementHarness.Run("Анализ PDF файлов", () => new PdfAnalyzerWindow(),
    new("Прогресс", window =>
    {
        var current = WindowPlacementHarness.Required<TextBlock>(window, "CurrentFileText");
        current.Text = "Текущий документ.pdf";
        current.IsVisible = true;
    }),
    new("Длинный результат", window =>
    {
        WindowPlacementHarness.Required<TextBlock>(window, "CurrentFileText").IsVisible = false;
        WindowPlacementHarness.Required<TextBlock>(window, "StatusText").Text =
            string.Join('\n', Enumerable.Repeat("Готово: отчет анализа PDF с подробным результатом", 5));
    }),
    new("Короткий статус", window =>
    {
        WindowPlacementHarness.Required<TextBlock>(window, "CurrentFileText").IsVisible = false;
        WindowPlacementHarness.Required<TextBlock>(window, "StatusText").Text = "Готов к анализу";
    }));
}
