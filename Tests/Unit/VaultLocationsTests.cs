using SmartBase.Core.Notes;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Список «Расположение» в диалогах заметок: полный путь вложенности, чтобы одноименные папки на
/// разных уровнях были различимы, и запрет предлагать папку саму себе целью переноса.
/// </summary>
public sealed class VaultLocationsTests
{
    private static NoteNode Folder(string name, string relative, params NoteNode[] children)
    {
        var node = new NoteNode
        {
            Kind = NoteKind.Folder, Name = name, RelativePath = relative, FullPath = relative,
        };
        node.Children.AddRange(children);
        return node;
    }

    private static NoteNode File(string name, string relative) => new()
    {
        Kind = NoteKind.File, Name = name, RelativePath = relative, FullPath = relative, IsMarkdown = true,
    };

    [Fact]
    public void Root_Comes_First()
    {
        var options = VaultLocations.Build([]);

        var only = Assert.Single(options);
        Assert.Equal(VaultLocations.RootLabel, only.Label);
        Assert.Equal("", only.RelativePath);
    }

    [Fact]
    public void Nested_Folders_Show_Their_Full_Path()
    {
        var tree = new[]
        {
            Folder("Работа", "Работа",
                Folder("Отчеты", "Работа/Отчеты")),
            Folder("Личное", "Личное"),
        };

        var options = VaultLocations.Build(tree);

        Assert.Equal(
            [VaultLocations.RootLabel, "Работа", "Работа / Отчеты", "Личное"],
            options.Select(o => o.Label));
    }

    [Fact]
    public void Same_Name_On_Different_Levels_Stays_Distinguishable()
    {
        var tree = new[]
        {
            Folder("Работа", "Работа", Folder("Отчеты", "Работа/Отчеты")),
            Folder("Личное", "Личное", Folder("Отчеты", "Личное/Отчеты")),
        };

        var labels = VaultLocations.Build(tree).Select(o => o.Label).ToList();

        Assert.Contains("Работа / Отчеты", labels);
        Assert.Contains("Личное / Отчеты", labels);
    }

    [Fact]
    public void Files_Are_Not_Offered_As_A_Location()
    {
        var tree = new[] { Folder("Работа", "Работа"), File("Заметка.md", "Заметка.md") };

        Assert.DoesNotContain("Заметка.md", VaultLocations.Build(tree).Select(o => o.Label));
    }

    [Fact]
    public void Excluded_Subtree_Disappears_With_Its_Children()
    {
        // Папку нельзя переместить внутрь самой себя или своего потомка.
        var tree = new[]
        {
            Folder("Работа", "Работа", Folder("Отчеты", "Работа/Отчеты")),
            Folder("Личное", "Личное"),
        };

        var labels = VaultLocations.Build(tree, excludeSubtree: "Работа").Select(o => o.Label).ToList();

        Assert.Equal([VaultLocations.RootLabel, "Личное"], labels);
    }
}
