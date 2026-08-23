using System.Text.RegularExpressions;
using SmartBase.Extensions;

namespace SmartBase.Tests.Extensions.Registry;

/// <summary>
/// Контракт <see cref="IExtension"/> по ВСЕМ зарегистрированным расширениям: то, что нельзя
/// проверить изнутри одного расширения — уникальность Id и имен, форма Id, работа поиска.
///
/// Железное правило этого файла: ни один тест не называет конкретное расширение и не требует
/// минимального размера реестра. Только обход <see cref="ExtensionRegistry.All"/>. Поэтому
/// удаление любого расширения (папка + строка реестра) набор не ломает, а пустой реестр дает
/// пустые проходы — приложение с нулем расширений работоспособно (ExtensionsView показывает
/// «Нет доступных расширений»).
///
/// ВАЖНО: <c>CreateWindow()</c> здесь не вызывается и вызывать его нельзя — Avalonia в
/// тест-хосте не инициализирована (нет AppBuilder, Dispatcher и стилей Application.Current),
/// конструктор любого Window упадет. Сами объекты расширений — обычные POCO, их создание
/// реестром безопасно.
/// </summary>
public sealed class ExtensionContractTests
{
    // Id уходит в ключ настройки «ext.<Id>.<Key>» и в ключ строки сайдбара: слаг без пробелов,
    // регистра и точек читается глазами в app.db и не ломает разбор приставки.
    private static readonly Regex Slug = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    [Fact]
    public void Registry_ids_are_unique()
    {
        ExtensionRegistry.EnsureUniqueIds();   // не бросает — Id в реестре не повторяются

        var ids = ExtensionRegistry.All.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Ids_look_like_stable_slugs()
    {
        foreach (var extension in ExtensionRegistry.All)
            Assert.True(Slug.IsMatch(extension.Id),
                        $"Id «{extension.Id}» не слаг вида «pdf-rename»: {extension.GetType().Name}");
    }

    // Точка в Id сделала бы приставку «ext.<Id>.» неоднозначной, и уборка сирот
    // (AppSettings.RemoveOrphanExtensionSettings) могла бы снести чужие ключи.
    [Fact]
    public void Ids_contain_no_dot()
    {
        foreach (var extension in ExtensionRegistry.All)
            Assert.DoesNotContain('.', extension.Id);
    }

    [Fact]
    public void Names_are_not_empty()
    {
        foreach (var extension in ExtensionRegistry.All)
            Assert.False(string.IsNullOrWhiteSpace(extension.Name),
                         $"Пустое имя у {extension.GetType().Name} — строка сайдбара будет безымянной");
    }

    // Две одинаковые подписи в сайдбаре пользователь не различит.
    [Fact]
    public void Names_are_unique()
    {
        var names = ExtensionRegistry.All.Select(e => e.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.CurrentCultureIgnoreCase).Count());
    }

    // Описание — тело блока раздела «Расширения»; пустое означает пустой экран.
    [Fact]
    public void Markdown_descriptions_are_not_empty()
    {
        foreach (var extension in ExtensionRegistry.All)
            Assert.False(string.IsNullOrWhiteSpace(extension.MarkdownDescription),
                         $"Пустое описание у {extension.Id}");
    }

    [Fact]
    public void Every_extension_is_found_by_its_own_id()
    {
        foreach (var extension in ExtensionRegistry.All)
            Assert.Same(extension, ExtensionRegistry.FindById(extension.Id));
    }

    [Fact]
    public void Unknown_id_is_not_found()
    {
        Assert.Null(ExtensionRegistry.FindById("нет-такого-расширения"));
    }

    /// <summary>Структурный инвариант «одна папка = одно расширение»: тип лежит ровно в
    /// <c>SmartBase.Extensions.&lt;Имя&gt;</c>, один сегмент после Extensions. Без него
    /// «удалить расширение» перестает быть операцией над папкой — код расползается.</summary>
    [Fact]
    public void Every_extension_lives_in_its_own_namespace()
    {
        const string root = "SmartBase.Extensions.";

        foreach (var extension in ExtensionRegistry.All)
        {
            var ns = extension.GetType().Namespace ?? "";
            Assert.StartsWith(root, ns, StringComparison.Ordinal);

            var tail = ns[root.Length..];
            Assert.False(tail.Length == 0, $"{extension.Id} лежит прямо в SmartBase.Extensions");
            Assert.DoesNotContain('.', tail);
        }
    }

    // Граница проверок без UI: CanRun читается у всех и не роняет тест-хост. Дальше
    // (CreateWindow) не идем — см. комментарий к классу.
    [Fact]
    public void Can_run_is_readable_without_a_ui()
    {
        foreach (var extension in ExtensionRegistry.All)
        {
            var canRun = extension.CanRun;
            Assert.True(canRun || !canRun);   // важен сам факт чтения без падения
        }
    }
}
