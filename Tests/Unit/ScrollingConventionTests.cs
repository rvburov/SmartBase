using System.Xml.Linq;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Сторож стандарта прокрутки (docs/developer/ui/scrolling.md). Правила там словесные и
/// потому теряются: новый экран пишут «как удобнее», и полоса перестает идти во всю высоту
/// блока либо отходит от края. Так и вышло с markdown-описаниями — контрол приносил
/// СОБСТВЕННУЮ прокрутку, общее правило на него не легло, и в двух местах из трех вид
/// разъехался с остальным приложением.
///
/// Проверяется разметка экранов и компонентов. Темы и шаблоны контролов (Assets/Styles,
/// *Themes.axaml) под правила не подпадают: <c>ScrollViewer</c> внутри
/// <c>ControlTemplate</c> — часть шаблона, отступ ему задает <c>TemplateBinding</c>.
/// </summary>
public sealed class ScrollingConventionTests
{
    /// <summary>Разметка экранов и компонентов — то, что пишут руками при новой фиче.</summary>
    private static readonly string[] MarkupDirs = ["Views", "Controls", "Extensions"];

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
            {
                // Копии тем (DataGrid, ComboBox, попапы) — чужие шаблоны целиком, свои правила.
                if (Path.GetFileName(file).EndsWith("Themes.axaml", StringComparison.Ordinal)) continue;

                yield return (Path.GetRelativePath(root, file), XDocument.Load(file));
            }
        }
    }

    private static bool InsideTemplate(XElement element) =>
        element.Ancestors().Any(a => a.Name.LocalName == "ControlTemplate");

    private static string? NameOf(XElement element) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value;

    /// <summary>
    /// Отступ у самого <c>ScrollViewer</c> — самая частая ошибка: полоса отходит от края
    /// блока, а сверху и снизу остаются мертвые зазоры, в которые текст не заезжает. Воздух
    /// вешается на содержимое ВНУТРИ прокрутки — тогда он уезжает вместе с ним.
    /// </summary>
    [Fact]
    public void Scroll_viewer_has_no_padding_of_its_own()
    {
        var offenders = new List<string>();

        foreach (var (path, doc) in Markup(RepoRoot()))
            foreach (var sv in doc.Descendants().Where(e => e.Name.LocalName == "ScrollViewer"))
            {
                if (InsideTemplate(sv)) continue;

                foreach (var attribute in new[] { "Margin", "Padding" })
                    if (sv.Attribute(attribute) is { } value)
                        offenders.Add($"{path}: <ScrollViewer {NameOf(sv) ?? "без имени"}> {attribute}=\"{value.Value}\"");
            }

        Assert.True(offenders.Count == 0,
                    "Отступ должен висеть на содержимом ВНУТРИ прокрутки, а не на самом ScrollViewer\n" +
                    "(docs/developer/ui/scrolling.md):\n" + string.Join('\n', offenders));
    }

    /// <summary>
    /// Markdown ставится только виджетом <c>AppMarkdown</c>: голый <c>MarkdownScrollViewer</c>
    /// приходит со встроенной темой пакета и своей прокруткой — вид расходится с приложением,
    /// а полоса перестает идти во всю высоту блока.
    /// </summary>
    [Fact]
    public void Markdown_is_shown_by_the_widget()
    {
        var offenders = new List<string>();

        foreach (var (path, doc) in Markup(RepoRoot()))
            foreach (var md in doc.Descendants().Where(e => e.Name.LocalName == "MarkdownScrollViewer"))
                offenders.Add($"{path}: <MarkdownScrollViewer {NameOf(md) ?? "без имени"}>");

        Assert.True(offenders.Count == 0,
                    "Markdown показывается виджетом AppMarkdown (docs/developer/ui/markdown.md):\n" +
                    string.Join('\n', offenders));
    }

    /// <summary>
    /// У прокрутки с ВЫНЕСЕННОЙ полосой (<c>ScrollBarLink.Source</c>) своя обязана быть
    /// спрятана: иначе на блоке окажутся две полосы — вынесенная и штатная.
    /// </summary>
    [Fact]
    public void Linked_scroll_viewer_hides_its_own_bar()
    {
        var offenders = new List<string>();

        foreach (var (path, doc) in Markup(RepoRoot()))
        {
            var viewers = doc.Descendants()
                             .Where(e => e.Name.LocalName == "ScrollViewer" && NameOf(e) is not null)
                             .ToDictionary(e => NameOf(e)!, e => e);

            foreach (var bar in doc.Descendants().Where(e => e.Name.LocalName == "ScrollBar"))
            {
                var source = bar.Attributes()
                                .FirstOrDefault(a => a.Name.LocalName == "ScrollBarLink.Source")?.Value;
                if (source is null) continue;

                // «{Binding #TimeScroll}» → TimeScroll. Источник с точкой («#AllDayBarCtrl.Scroller»)
                // живет в чужой разметке — оттуда его и проверять, здесь пропускаем.
                var name = source.Split('#').ElementAtOrDefault(1)?.TrimEnd('}', ' ');
                if (name is null || name.Contains('.')) continue;
                if (!viewers.TryGetValue(name, out var viewer)) continue;

                var vertical = bar.Attribute("Orientation")?.Value != "Horizontal";
                var property = vertical ? "VerticalScrollBarVisibility" : "HorizontalScrollBarVisibility";
                if (viewer.Attribute(property)?.Value != "Hidden")
                    offenders.Add($"{path}: у ScrollViewer «{name}» нет {property}=\"Hidden\", " +
                                  "а полоса для него вынесена наружу");
            }
        }

        Assert.True(offenders.Count == 0,
                    "Вынесенная полоса требует спрятанной штатной (docs/developer/ui/scrolling.md):\n" +
                    string.Join('\n', offenders));
    }
}
