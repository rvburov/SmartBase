using SmartBase.Extensions.AssemblyCalc;
using SmartBase.Extensions.PdfAnalyzer;
using SmartBase.Extensions.PdfRename;
using SmartBase.Extensions.ProductCompare;

namespace SmartBase.Extensions;

/// <summary>
/// Реестр доступных расширений. Пока — статический список зарегистрированных вручную.
/// Позже сюда можно подставить автообнаружение (рефлексия по сборке/папкам Extensions/*).
/// Оболочка (сайдбар и блок) работает только с этим списком и интерфейсом <see cref="IExtension"/>.
/// </summary>
public static class ExtensionRegistry
{
    /// <summary>Все известные расширения в порядке показа.</summary>
    public static IReadOnlyList<IExtension> All { get; } =
    [
        new PdfRenameExtension(),
        new ProductCompareExtension(),
        new AssemblyCalcExtension(),
        new PdfAnalyzerExtension(),
    ];

    /// <summary>Найти расширение по <see cref="IExtension.Id"/> (null, если нет).</summary>
    public static IExtension? FindById(string id) =>
        All.FirstOrDefault(e => e.Id == id);
}
