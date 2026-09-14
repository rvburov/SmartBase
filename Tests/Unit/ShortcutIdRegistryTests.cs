using System.Text.RegularExpressions;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Реестр действий (Core/Models/ShortcutActions.cs) против id, которыми код спрашивает
/// сочетание. Опечатка в id компилируется молча и ведет себя как «сочетания нет»:
/// <c>ShortcutSettings.Matches</c> никогда не совпадет, а пункт меню останется без подсказки —
/// поломка тихая, ее видно только глазом на экране. Тест ловит это на сборке.
///
/// Тот же прием, что у <see cref="IconRegistryTests"/>: строковые литералы кода против словаря.
/// </summary>
public sealed class ShortcutIdRegistryTests
{
    private static readonly string[] CodeDirs = ["Views", "Controls", "Utils", "Core", "Database", "Settings"];

    // Литерал вида «блок.действие» или «блок.ref.действие» с блоком из реестра. Имена файлов
    // (app.db, notes.md) отсеиваются по расширению: у id расширений не бывает.
    private static readonly Regex IdLiteral = new(
        "\"((?:app|sidebar|table|record|calendar|tasks|notes|notesTree|windows)\\.[A-Za-z0-9_.]+)\"",
        RegexOptions.Compiled);

    private static readonly string[] FileSuffixes = [".db", ".md", ".json", ".cs", ".axaml", ".log", ".txt"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartBase.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Every_shortcut_id_used_in_code_exists_in_the_registry()
    {
        var root    = RepoRoot();
        var unknown = new List<string>();

        foreach (var dir in CodeDirs)
        {
            var path = Path.Combine(root, dir);
            if (!Directory.Exists(path)) continue;

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                // Сам реестр — источник списка, в нем id и объявлены.
                if (Path.GetFileName(file) == "ShortcutActions.cs") continue;

                foreach (Match m in IdLiteral.Matches(File.ReadAllText(file)))
                {
                    var id = m.Groups[1].Value;
                    if (FileSuffixes.Any(s => id.EndsWith(s, StringComparison.OrdinalIgnoreCase))) continue;
                    if (ShortcutActions.Find(id) is null)
                        unknown.Add($"{Path.GetRelativePath(root, file)}: {id}");
                }
            }
        }

        Assert.True(unknown.Count == 0,
                    "Id действий нет в ShortcutActions.All:\n" + string.Join('\n', unknown));
    }
}
