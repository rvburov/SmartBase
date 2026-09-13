using System.Text.RegularExpressions;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Сторож ссылок на экраны: текст, который отправляет человека «на вкладку Х», обязан вести
/// туда, где эта вкладка есть.
///
/// <b>Откуда взялся.</b> Шаг 15 увез «Безопасность» из вкладок раздела «Интеграции» в модуль
/// «Профиль», а две подсказки остались висеть в настройках обмена и отправляли человека на
/// вкладку, которой больше нет. Компилятор такого не видит: это обычная строка. Заметить ее
/// можно было только глазами — и никто не заметил до сплошного разбора кода.
///
/// Ровно того же рода проверки в этом наборе: <c>IconRegistryTests</c> (ключ иконки —
/// строка) и <c>ScrollingConventionTests</c> (правило словесное). Переезды экранов будут и
/// дальше, и каждый оставляет такой хвост.
/// </summary>
public sealed class ScreenReferenceTests
{
    private static readonly string[] CodeDirs = ["Views", "Controls", "Settings"];

    /// <summary>
    /// Названия вкладок, которых в разделе «Интеграции» больше нет: ключ — имя, значение —
    /// куда экран уехал и как его теперь называть в подсказке.
    /// </summary>
    private static readonly (string Tab, string Where)[] Moved =
    [
        ("Безопасность", "раздел «Безопасность» в профиле"),
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartBase.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);   // тест бесполезен, если корень репозитория не найден
        return dir!.FullName;
    }

    /// <summary>
    /// Ни один текст для пользователя не зовет на переехавшую вкладку. Ищется именно оборот
    /// «вкладку «Х»» в винительном падеже — так пишут «см. вкладку …»; слово «вкладка» само по
    /// себе законно и встречается у вкладок, которые никуда не девались.
    /// </summary>
    [Fact]
    public void No_text_sends_the_user_to_a_tab_that_moved_away()
    {
        var root  = RepoRoot();
        var stale = new List<string>();

        foreach (var dir in CodeDirs)
        {
            var path = Path.Combine(root, dir);
            if (!Directory.Exists(path)) continue;

            foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            {
                if (Path.GetExtension(file) is not (".cs" or ".axaml")) continue;

                var text = File.ReadAllText(file);

                foreach (var (tab, where) in Moved)
                    if (Regex.IsMatch(text, $"вкладку\\s+«{Regex.Escape(tab)}»"))
                        stale.Add($"{Path.GetRelativePath(root, file)}: «вкладку «{tab}»» — "
                                + $"экран переехал, писать надо «{where}»");
            }
        }

        Assert.True(stale.Count == 0,
            "Подсказка ведет на вкладку, которой больше нет:\n" + string.Join("\n", stale));
    }

    /// <summary>
    /// Комментарии не ссылаются на файлы, которых нет. Тот же переезд оставил в двух местах
    /// отсылку к <c>SettingsIntegrationsStyle</c> — файлу, переименованному в
    /// <c>Controls/Widgets/SettingRows.cs</c>; читающий код шел искать его и не находил.
    /// </summary>
    [Fact]
    public void No_comment_points_at_a_file_that_does_not_exist()
    {
        var root  = RepoRoot();
        var stale = new List<string>();

        // Имена исходников, встречающиеся в комментариях как ориентир «смотри там».
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.StartsWith("artifacts", StringComparison.Ordinal)) continue;
            if (relative.StartsWith("packages",  StringComparison.Ordinal)) continue;

            names.Add(Path.GetFileNameWithoutExtension(file));
        }

        Assert.NotEmpty(names);

        foreach (var dir in CodeDirs)
        {
            var path = Path.Combine(root, dir);
            if (!Directory.Exists(path)) continue;

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
                foreach (Match m in Regex.Matches(File.ReadAllText(file),
                                                  @"—\s+в\s+(SettingsIntegrations\w+|SettingRows)\b"))
                    if (!names.Contains(m.Groups[1].Value))
                        stale.Add($"{Path.GetRelativePath(root, file)}: {m.Groups[1].Value}");
        }

        Assert.True(stale.Count == 0,
            "Комментарий отсылает к несуществующему файлу:\n" + string.Join("\n", stale));
    }
}
