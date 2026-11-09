using SmartBase.Extensions.AssemblyCalc;
using SmartBase.Extensions.Automations;
using SmartBase.Extensions.DocRegistry;
using SmartBase.Extensions.DocsCollection;
using SmartBase.Extensions.PdfAnalyzer;
using SmartBase.Extensions.PdfWatermark;
using SmartBase.Extensions.ProductCompare;
using SmartBase.Extensions.TimeCollector;

namespace SmartBase.Extensions;

/// <summary>
/// Реестр доступных расширений: встроенные в программу (список ниже, зарегистрированы вручную)
/// и расширения-плагины, установленные в свою папку (<see cref="ExtensionPlugins"/>).
/// Оболочка (сайдбар и блок) работает только с этим списком и интерфейсом <see cref="IExtension"/>.
/// </summary>
public static class ExtensionRegistry
{
    /// <summary>Встроенные расширения — собраны вместе с программой.</summary>
    public static IReadOnlyList<IExtension> BuiltIn { get; } =
    [
        new ProductCompareExtension(),
        new AssemblyCalcExtension(),
        new PdfAnalyzerExtension(),
        new DocsCollectionExtension(),
        new TimeCollectorExtension(),
        new DocRegistryExtension(),
        new PdfWatermarkExtension(),
        new AutomationsExtension(),
    ];

    /// <summary>Id встроенных — плагин с таким же Id не грузится.</summary>
    public static IReadOnlySet<string> BuiltInIds { get; } =
        BuiltIn.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<IExtension>? _all;

    /// <summary>Все известные расширения в порядке показа: встроенные, за ними плагины. Первое
    /// обращение загружает плагины (<see cref="ExtensionPlugins.Initialize"/>).</summary>
    public static IReadOnlyList<IExtension> All => _all ??= [.. BuiltIn, .. ExtensionPlugins.Loaded];

    /// <summary>Id всех расширений, чьи настройки надо беречь: загруженных и установленных, но не
    /// загрузившихся (несовместимая версия, недоустановка) — иначе уборка «сирот» стерла бы
    /// настройки расширения, которое просто ждет обновления.</summary>
    public static IEnumerable<string> KnownIds =>
        All.Select(e => e.Id).Concat(ExtensionPlugins.Installed.Select(p => p.Id)).Distinct(StringComparer.Ordinal);

    /// <summary>Только для тестов: пересобрать список после смены папки плагинов.</summary>
    internal static void Reset() => _all = null;

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
