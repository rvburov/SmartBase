using SmartBase.Extensions.AssemblyCalc;
using SmartBase.Extensions.Automations;
using SmartBase.Extensions.DocRegistry;
using SmartBase.Extensions.DocsCollection;
using SmartBase.Extensions.PdfAnalyzer;
using SmartBase.Extensions.PdfRename;
using SmartBase.Extensions.PdfWatermark;
using SmartBase.Extensions.ProductCompare;
using SmartBase.Extensions.TimeCollector;

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
        new DocsCollectionExtension(),
        new TimeCollectorExtension(),
        new DocRegistryExtension(),
        new PdfWatermarkExtension(),
        new AutomationsExtension(),
    ];

    /// <summary>Найти расширение по <see cref="IExtension.Id"/> (null, если нет).</summary>
    public static IExtension? FindById(string id) =>
        All.FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// Проверить, что Id расширений не повторяются. Совпадение молча ломает две вещи:
    /// <see cref="FindById"/> начинает отдавать не то расширение, а настройки (ключ
    /// <c>ext.&lt;Id&gt;.…</c>) у тезок становятся общими — одно читает чужое значение.
    /// Ошибка возможна только при правке реестра, поэтому падаем на старте с явным
    /// сообщением, а не оставляем разбираться с загадочным поведением.
    /// </summary>
    public static void EnsureUniqueIds()
    {
        var duplicates = All.GroupBy(e => e.Id, StringComparer.Ordinal)
                            .Where(g => g.Count() > 1)
                            .Select(g => g.Key)
                            .ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                $"Повторяющийся Id расширения: {string.Join(", ", duplicates)}. " +
                "Id должен быть уникальным — см. ExtensionRegistry.All.");
    }
}
