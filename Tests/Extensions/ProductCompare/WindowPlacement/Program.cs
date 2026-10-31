using Avalonia.Controls;
using SmartBase.Extensions.ProductCompare;
using SmartBase.Tests.Extensions.WindowPlacement;

namespace SmartBase.Tests.Extensions.ProductCompare.WindowPlacement;

internal static class Program
{
    [STAThread]
    private static int Main() => WindowPlacementHarness.Run("Сравнение составов изделий", () => new ProductCompareWindow(),
        new("Расшифровка", window => WindowPlacementHarness.Required<ComboBox>(window, "ModeCombo").SelectedIndex = 1),
        new("Заключение", window => WindowPlacementHarness.Required<ComboBox>(window, "ModeCombo").SelectedIndex = 0));
}
