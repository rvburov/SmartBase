using Avalonia.Controls;
using SmartBase.Extensions.AssemblyCalc;
using SmartBase.Tests.Extensions.WindowPlacement;

namespace SmartBase.Tests.Extensions.AssemblyCalc.WindowPlacement;

internal static class Program
{
    [STAThread]
    private static int Main() => WindowPlacementHarness.Run("Расчет сборочных единиц", () => new AssemblyCalcWindow(),
        new("Длинный результат", window => WindowPlacementHarness.Required<TextBlock>(window, "StatusText").Text =
            string.Join('\n', Enumerable.Repeat("Готово: расчет сборочных единиц изделия с подробным результатом", 5))),
        new("Ошибка", window => WindowPlacementHarness.Required<TextBlock>(window, "StatusText").Text =
            string.Join('\n', Enumerable.Repeat("Ошибка исходного файла: подробное описание причины", 3))),
        new("Короткий статус", window => WindowPlacementHarness.Required<TextBlock>(window, "StatusText").Text = "Готов к работе"));
}
