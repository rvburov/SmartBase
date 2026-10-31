using Avalonia.Controls;
using SmartBase.Extensions.DocsCollection;
using SmartBase.Tests.Extensions.WindowPlacement;

namespace SmartBase.Tests.Extensions.DocsCollection.WindowPlacement;

internal static class Program
{
    // Меняем только представление прогресса: обход файлов и запись коллекции не запускаются.
    [STAThread]
    private static int Main() => WindowPlacementHarness.Run("Документы в коллекцию", () => new DocsCollectionWindow(),
    new("Прогресс", window =>
    {
        var current = WindowPlacementHarness.Required<TextBlock>(window, "CurrentFileText");
        current.Text = "Текущий документ.pdf";
        current.IsVisible = true;
    }),
    new("Ошибка в несколько строк", window =>
    {
        WindowPlacementHarness.Required<TextBlock>(window, "CurrentFileText").IsVisible = false;
        WindowPlacementHarness.Required<TextBlock>(window, "StatusText").Text =
            string.Join('\n', Enumerable.Repeat("Описание ошибки создания коллекции документов", 5));
    }),
    new("Короткий статус", window =>
    {
        WindowPlacementHarness.Required<TextBlock>(window, "CurrentFileText").IsVisible = false;
        WindowPlacementHarness.Required<TextBlock>(window, "StatusText").Text = "Готов к работе";
    }));
}
