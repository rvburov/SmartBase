using SmartBase.Core.Notes;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Правила дерева заметок. Здесь фиксируется то, что человек видит в сайдбаре: какие файлы скрыты,
/// что открывается, в каком порядке идут строки и как ведут себя относительные пути — на них
/// завязаны раскрытые ветки и «последняя открытая заметка» в настройках.
/// </summary>
public sealed class VaultRulesTests
{
    private static NoteNode Folder(string name) => new()
    {
        Kind = NoteKind.Folder, Name = name, RelativePath = name, FullPath = name,
    };

    private static NoteNode File(string name) => new()
    {
        Kind = NoteKind.File, Name = name, RelativePath = name, FullPath = name,
        IsMarkdown = VaultRules.IsMarkdown(name),
    };

    // ── Скрытые ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(".obsidian")]
    [InlineData(".git")]
    [InlineData(".trash")]
    [InlineData(".DS_Store")]
    public void Dot_Prefixed_Names_Are_Hidden(string name) =>
        Assert.True(VaultRules.IsHidden(name, FileAttributes.Normal));

    [Fact]
    public void Ordinary_Name_Is_Visible() =>
        Assert.False(VaultRules.IsHidden("Заметка.md", FileAttributes.Normal));

    [Theory]
    [InlineData(FileAttributes.Hidden)]
    [InlineData(FileAttributes.System)]
    public void System_Flags_Hide_Even_Without_Dot(FileAttributes attrs) =>
        Assert.True(VaultRules.IsHidden("Thumbs.db", attrs));

    [Fact]
    public void Reparse_Points_Are_Not_Traversed() =>
        Assert.False(VaultRules.IsTraversable(FileAttributes.Directory | FileAttributes.ReparsePoint));

    // ── Что открывается ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Заметка.md",       true)]
    [InlineData("Заметка.markdown", true)]
    [InlineData("ЗАМЕТКА.MD",       true)]
    [InlineData("Заметка.txt",      false)]
    [InlineData("Отчет.pdf",        false)]
    [InlineData("Заметка",          false)]
    public void Only_Markdown_Opens(string name, bool expected) =>
        Assert.Equal(expected, VaultRules.IsMarkdown(name));

    // ── Относительные пути ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"Работа\Отчеты", "Работа/Отчеты")]
    [InlineData("/Работа/",       "Работа")]
    [InlineData("",               "")]
    [InlineData(null,             "")]
    public void Normalize_Uses_Forward_Slashes_Without_Edges(string? input, string expected) =>
        Assert.Equal(expected, VaultRules.Normalize(input));

    [Fact]
    public void Combine_Skips_Separator_At_Root() =>
        Assert.Equal("Заметка.md", VaultRules.Combine("", "Заметка.md"));

    [Fact]
    public void Combine_Joins_Nested() =>
        Assert.Equal("Работа/Отчеты/Итог.md", VaultRules.Combine("Работа/Отчеты", "Итог.md"));

    [Theory]
    [InlineData("Работа/Отчеты/Итог.md", "Работа/Отчеты")]
    [InlineData("Итог.md",               "")]
    public void ParentOf_Cuts_Last_Segment(string path, string expected) =>
        Assert.Equal(expected, VaultRules.ParentOf(path));

    [Fact]
    public void Subtree_Contains_Itself_And_Children()
    {
        Assert.True(VaultRules.IsInside("Работа", "Работа"));
        Assert.True(VaultRules.IsInside("Работа/Отчеты/Итог.md", "Работа"));
        Assert.False(VaultRules.IsInside("Работать", "Работа"));   // не путать с префиксом имени
        Assert.False(VaultRules.IsInside("Личное", "Работа"));
    }

    [Fact]
    public void Everything_Is_Inside_The_Vault_Root() =>
        Assert.True(VaultRules.IsInside("Работа/Отчеты", ""));

    [Fact]
    public void Rebase_Moves_Whole_Branch()
    {
        Assert.Equal("Архив",                 VaultRules.Rebase("Работа", "Работа", "Архив"));
        Assert.Equal("Архив/Отчеты/Итог.md",  VaultRules.Rebase("Работа/Отчеты/Итог.md", "Работа", "Архив"));
        Assert.Equal("Личное/Итог.md",        VaultRules.Rebase("Личное/Итог.md", "Работа", "Архив"));
    }

    // ── Порядок ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Folders_Stand_Above_Files_Regardless_Of_Direction()
    {
        var folder = Folder("Яблоко");
        var file   = File("Абрикос.md");

        Assert.True(VaultRules.Compare(folder, file, descending: false) < 0);
        Assert.True(VaultRules.Compare(folder, file, descending: true)  < 0);
    }

    [Fact]
    public void Sort_Level_Puts_Folders_First_Then_Names()
    {
        var nodes = new List<NoteNode> { File("Бета.md"), Folder("Яуза"), File("Альфа.md"), Folder("Арбат") };

        VaultRules.SortLevel(nodes, descending: false);

        Assert.Equal(["Арбат", "Яуза", "Альфа.md", "Бета.md"], nodes.Select(n => n.Name));
    }

    [Fact]
    public void Descending_Flips_Names_But_Not_Folders_First()
    {
        var nodes = new List<NoteNode> { File("Альфа.md"), Folder("Арбат"), File("Бета.md"), Folder("Яуза") };

        VaultRules.SortLevel(nodes, descending: true);

        Assert.Equal(["Яуза", "Арбат", "Бета.md", "Альфа.md"], nodes.Select(n => n.Name));
    }

    [Fact]
    public void Names_Starting_With_Digit_Form_The_First_Group()
    {
        // То же правило, что в сайдбаре конструктора (SidebarTree.NameKey): нумерованные имена
        // держатся вместе и выше буквенных.
        var nodes = new List<NoteNode> { File("Альфа.md"), File("2 Заметка.md"), File("Бета.md") };

        VaultRules.SortLevel(nodes, descending: false);

        Assert.Equal("2 Заметка.md", nodes[0].Name);
    }
}
