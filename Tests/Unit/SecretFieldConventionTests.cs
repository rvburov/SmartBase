using System.Xml.Linq;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Сторож поля секрета (docs/developer/ui/theming.md, «Поле секрета: точки и глаз»). Правило
/// словесное и теряется ровно так, как теряются все словесные правила: новое окно с паролем
/// пишут по образцу соседнего, а образец берут старый. Цена ошибки здесь не косметическая —
/// поле без класса показывает пароль открытым текстом.
///
/// Проверяется разметка окон и экранов; сами стили (Assets/Styles) под правила не подпадают —
/// класс там и объявлен.
/// </summary>
public sealed class SecretFieldConventionTests
{
    /// <summary>Разметка, которую пишут руками при новой фиче.</summary>
    private static readonly string[] MarkupDirs = ["Views", "Controls", "Extensions", "Setup"];

    /// <summary>
    /// Поле, которое показывает набранное НАРОЧНО, с объяснением прямо в разметке. Пароль роли
    /// PostgreSQL придумывает сама программа кнопкой «Сгенерировать», и его тут же копируют и
    /// передают тому, кто будет подключаться: точки в этом поле мешали бы единственному, ради
    /// чего оно есть. Список исключений держится коротким нарочно — каждая строка здесь это
    /// пароль, который видно через плечо.
    /// </summary>
    private static readonly string[] OpenOnPurpose = ["RolePasswordBox"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartBase.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);   // тест бесполезен, если корень репозитория не найден
        return dir!.FullName;
    }

    private static IEnumerable<(string Path, XDocument Doc)> Markup(string root)
    {
        foreach (var dir in MarkupDirs)
        {
            var path = Path.Combine(root, dir);
            if (!Directory.Exists(path)) continue;

            foreach (var file in Directory.EnumerateFiles(path, "*.axaml", SearchOption.AllDirectories))
                yield return (Path.GetRelativePath(root, file), XDocument.Load(file));
        }
    }

    private static string Value(XElement element, string attribute) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == attribute)?.Value ?? "";

    /// <summary>Похоже ли поле на секрет — по имени: другого признака в разметке нет.</summary>
    private static bool LooksSecret(string name) =>
        name.Contains("Password", StringComparison.Ordinal)
        || name.Contains("Secret", StringComparison.Ordinal)
        || name.Contains("Repeat", StringComparison.Ordinal);

    private static IEnumerable<(string Path, XElement Box, string Name)> TextBoxes(string root) =>
        from file in Markup(root)
        from box in file.Doc.Descendants().Where(e => e.Name.LocalName == "TextBox")
        select (file.Path, box, Value(box, "Name"));

    /// <summary>
    /// Точки задает класс, а не разметка окна. Своя <c>PasswordChar</c> в окне означает поле,
    /// которое завели мимо класса: точки в нем будут, а глаза не будет — и человек снова набирает
    /// пароль вслепую, без возможности проверить раскладку.
    /// </summary>
    [Fact]
    public void Markup_never_sets_the_password_char_itself()
    {
        var offenders = TextBoxes(RepoRoot())
            .Where(t => Value(t.Box, "PasswordChar").Length > 0)
            .Select(t => $"{t.Path}: <TextBox {(t.Name.Length > 0 ? t.Name : "без имени")}>")
            .ToList();

        Assert.True(offenders.Count == 0,
                    "Точки и глаз дает класс Classes=\"password\", своя PasswordChar в разметке "
                    + "означает поле без глаза (docs/developer/ui/theming.md):\n"
                    + string.Join('\n', offenders));
    }

    /// <summary>
    /// Поле с секретом в имени обязано нести класс. Признак по имени грубый, но другого в
    /// разметке нет, а забывают класс именно на поле, которое назвали «...PasswordBox» и
    /// скопировали из соседнего окна. Поле, которое показывает набранное нарочно, вносится в
    /// <see cref="OpenOnPurpose"/> — списком, чтобы такое решение принимали, а не забывали.
    /// </summary>
    [Fact]
    public void Every_secret_field_carries_the_password_class()
    {
        var offenders = TextBoxes(RepoRoot())
            .Where(t => LooksSecret(t.Name)
                        && !OpenOnPurpose.Contains(t.Name)
                        && !Value(t.Box, "Classes").Split(' ').Contains("password"))
            .Select(t => $"{t.Path}: <TextBox {t.Name}>")
            .ToList();

        Assert.True(offenders.Count == 0,
                    "Поле секрета помечается Classes=\"password\" — иначе пароль набирается "
                    + "открытым текстом (docs/developer/ui/theming.md):\n"
                    + string.Join('\n', offenders));
    }

    /// <summary>
    /// Список исключений не разрастается молча: каждое поле в нем существует, и каждое
    /// объяснено комментарием в своей разметке. Исчезло поле — исчезает и строка списка.
    /// </summary>
    [Fact]
    public void Fields_that_stay_open_still_exist()
    {
        var names = TextBoxes(RepoRoot()).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        Assert.All(OpenOnPurpose, name => Assert.Contains(name, names));
    }
}
