using SmartBase.Core.Notes;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Чтение хранилища с диска. Обход ленивый, поэтому проверяется главное: один уровень остается
/// одним уровнем, скрытое не показывается, а полный обход честно сознается, что уперся в потолок,
/// — молчаливое усечение читалось бы как «в хранилище больше ничего нет».
/// </summary>
public sealed class VaultScannerTests
{
    private static void Note(TempDir dir, params string[] parts)
    {
        var path = Path.Combine([dir.Path, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# заметка");
    }

    private static void Folder(TempDir dir, params string[] parts) =>
        Directory.CreateDirectory(Path.Combine([dir.Path, .. parts]));

    [Fact]
    public void Missing_Vault_Reads_As_Empty_Without_Throwing()
    {
        Assert.Empty(VaultScanner.ScanChildren(Path.Combine(Path.GetTempPath(), "нет-такой-папки"), ""));
        Assert.Empty(VaultScanner.ScanChildren("", ""));
        Assert.Empty(VaultScanner.ScanChildren(null, ""));
    }

    [Fact]
    public void Empty_Vault_Reads_As_Empty()
    {
        using var dir = new TempDir();
        Assert.Empty(VaultScanner.ScanChildren(dir.Path, ""));
    }

    [Fact]
    public void Scan_Children_Reads_Only_One_Level()
    {
        using var dir = new TempDir();
        Note(dir, "Верх.md");
        Note(dir, "Работа", "Внутри.md");

        var roots = VaultScanner.ScanChildren(dir.Path, "");

        Assert.Equal(2, roots.Count);
        Assert.All(roots, n => Assert.Empty(n.Children));
        Assert.Contains(roots, n => n is { Name: "Верх.md", IsMarkdown: true, Kind: NoteKind.File });
        Assert.Contains(roots, n => n is { Name: "Работа", Kind: NoteKind.Folder });
    }

    [Fact]
    public void Hidden_Entries_Are_Skipped()
    {
        using var dir = new TempDir();
        Note(dir, "Видимая.md");
        Note(dir, ".DS_Store");
        Folder(dir, ".obsidian");
        Folder(dir, ".git");

        var roots = VaultScanner.ScanChildren(dir.Path, "");

        Assert.Single(roots);
        Assert.Equal("Видимая.md", roots[0].Name);
    }

    [Fact]
    public void Non_Markdown_Files_Are_Shown_But_Not_Openable()
    {
        using var dir = new TempDir();
        Note(dir, "Отчет.pdf");

        var node = Assert.Single(VaultScanner.ScanChildren(dir.Path, ""));

        Assert.Equal("Отчет.pdf", node.Name);
        Assert.False(node.IsMarkdown);
    }

    [Fact]
    public void Relative_Path_Is_Built_From_The_Vault_Root()
    {
        using var dir = new TempDir();
        Note(dir, "Работа", "Отчеты", "Итог.md");

        var node = Assert.Single(VaultScanner.ScanChildren(dir.Path, "Работа/Отчеты"));

        Assert.Equal("Работа/Отчеты/Итог.md", node.RelativePath);
        Assert.Equal(Path.Combine(dir.Path, "Работа", "Отчеты", "Итог.md"), node.FullPath);
    }

    [Fact]
    public void Scan_All_Loads_The_Whole_Tree()
    {
        using var dir = new TempDir();
        Note(dir, "Работа", "Отчеты", "Итог.md");
        Note(dir, "Личное.md");

        var (roots, truncated) = VaultScanner.ScanAll(dir.Path);

        Assert.False(truncated);
        var work = Assert.Single(roots, n => n.Name == "Работа");
        Assert.True(work.IsLoaded);
        var reports = Assert.Single(work.Children, n => n.Name == "Отчеты");
        Assert.Single(reports.Children, n => n.Name == "Итог.md");
    }

    [Fact]
    public void Scan_All_Admits_That_It_Stopped_At_The_Ceiling()
    {
        using var dir = new TempDir();
        for (var i = 0; i < 10; i++) Note(dir, $"Заметка {i}.md");

        var (_, truncated) = VaultScanner.ScanAll(dir.Path, maxNodes: 5);

        Assert.True(truncated);
    }

    [Fact]
    public void Scan_Folders_Ignores_Files()
    {
        using var dir = new TempDir();
        Note(dir, "Личное.md");
        Note(dir, "Работа", "Отчеты", "Итог.md");

        var roots = VaultScanner.ScanFolders(dir.Path);

        var work = Assert.Single(roots);
        Assert.Equal("Работа", work.Name);
        Assert.Single(work.Children, n => n.Name == "Отчеты");
    }

    [Fact]
    public void Full_Path_Of_Root_Is_The_Vault_Itself()
    {
        using var dir = new TempDir();

        Assert.Equal(dir.Path, VaultScanner.FullPathOf(dir.Path, ""));
        Assert.Equal(Path.Combine(dir.Path, "Работа", "Итог.md"),
                     VaultScanner.FullPathOf(dir.Path, "Работа/Итог.md"));
        Assert.Equal("", VaultScanner.FullPathOf("", "Работа"));
    }
}
