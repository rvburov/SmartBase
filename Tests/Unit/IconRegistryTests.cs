using System.Text.RegularExpressions;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Реестр иконок (Assets/Styles/Icons.axaml) против ключей, которыми код спрашивает
/// геометрию в рантайме. Опечатка в ключе («IconPencl») компилируется молча, а падает
/// уже при открытии экрана: хелперы вида <c>HistoryIcon</c>/<c>LogsIcon</c> бросают
/// KeyNotFoundException. Тест ловит это на сборке.
///
/// Проверяются только строковые литералы C# — ссылки {StaticResource IconX} из XAML
/// разбирает компилятор разметки.
/// </summary>
public sealed class IconRegistryTests
{
    // Папки, где код обращается к реестру по ключу-строке.
    // Settings/ здесь ради реестров разделов (SettingsRegistry, ProfileRegistry): они задают
    // иконку строкой, а SidebarList при отсутствии ключа молча рисует пустоту — опечатку не
    // видно ничем, кроме глаза.
    private static readonly string[] CodeDirs = ["Views", "Controls", "Utils", "Core", "Database", "Settings"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartBase.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);   // тест бесполезен, если корень репозитория не найден
        return dir!.FullName;
    }

    private static HashSet<string> RegistryKeys(string root)
    {
        var xaml = File.ReadAllText(Path.Combine(root, "Assets", "Styles", "Icons.axaml"));
        return Regex.Matches(xaml, "x:Key=\"(Icon[A-Za-z]+)\"")
                    .Select(m => m.Groups[1].Value)
                    .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void Every_icon_key_used_in_code_exists_in_the_registry()
    {
        var root = RepoRoot();
        var keys = RegistryKeys(root);
        Assert.NotEmpty(keys);

        var missing = new List<string>();
        foreach (var dir in CodeDirs)
        {
            var path = Path.Combine(root, dir);
            if (!Directory.Exists(path)) continue;

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(text, "\"(Icon[A-Za-z]+)\""))
                {
                    var key = m.Groups[1].Value;
                    if (!keys.Contains(key))
                        missing.Add($"{Path.GetRelativePath(root, file)}: {key}");
                }
            }
        }

        Assert.True(missing.Count == 0,
                    "Ключи иконок отсутствуют в Icons.axaml:\n" + string.Join('\n', missing));
    }
}
