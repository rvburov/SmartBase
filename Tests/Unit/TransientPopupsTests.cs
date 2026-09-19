using System.Text.RegularExpressions;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Сторож правила «показал всплывающую панель — отметь ее в реестре».
///
/// Повод: панель, о которой окно не знает, остается висеть поверх ЧУЖОЙ программы, когда
/// человек переключается из SmartBase (на macOS popup живет отдельным окном и сам не гаснет).
/// Закрытие было написано ровно под один вид панелей — фильтр колонки таблицы, — и все, что
/// появилось позже (фильтр журналов, пикер коллекций, «Тип документа» библиотеки, меню
/// действий записи), молча завело ту же дыру.
///
/// Проверяем исходник: поймать это на живом экране можно только переключением в другую
/// программу, а забыть строку — одной правкой.
/// </summary>
public sealed class TransientPopupsTests
{
    // Toast: всплывашка-уведомление живет секунды, не забирает фокус и гаснет по своему
    // таймеру; закрытие «на деактивации» съедало бы сообщение при открытии собственной
    // модалки — на macOS она тоже приходит как потеря фокуса.
    // PopupPlate: фабрика плашки. Она флайаут только СОЗДАЕТ, а отмечает его тот, кто
    // показывает, — там же, где вызывается ShowAt.
    private static readonly string[] Exempt = ["Toast.cs", "PopupPlate.cs"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartBase.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);   // тест бесполезен, если корень репозитория не найден
        return dir!.FullName;
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        new[] { "Controls", "Views" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories));

    [Fact]
    public void Every_flyout_is_registered_so_the_window_can_close_it()
    {
        var root    = RepoRoot();
        // Показать панель можно двумя способами: собрать флайаут самому или взять готовую
        // плашку у фабрики. Оба обязаны отметиться в реестре.
        var opens   = new Regex(@"new (Menu)?Flyout\b|PopupPlate\.Filter\(");
        var missing = new List<string>();

        foreach (var file in SourceFiles(root))
        {
            if (Exempt.Contains(Path.GetFileName(file))) continue;

            var code = File.ReadAllText(file);
            if (!opens.IsMatch(code)) continue;
            if (!code.Contains("TransientPopups.Track", StringComparison.Ordinal))
                missing.Add(Path.GetRelativePath(root, file));
        }

        Assert.True(missing.Count == 0,
            "Панель показывается, но не отмечена в TransientPopups — окно не сможет закрыть ее " +
            "при потере фокуса, и на macOS она зависнет поверх другой программы:\n  " +
            string.Join("\n  ", missing));
    }

    /// <summary>
    /// Окно закрывает панели через общий реестр, а не обходом дерева по конкретному типу:
    /// именно такой обход и знал про одну-единственную таблицу.
    /// </summary>
    [Fact]
    public void Window_closes_popups_through_the_registry()
    {
        var main = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.axaml.cs"));

        Assert.Contains("TransientPopups.CloseAll()", main);
        Assert.DoesNotContain("CloseTransientPopups()", main);
    }
}
