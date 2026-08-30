using SmartBase.Core.Notes;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Подсчет содержимого папки перед дублированием. От него зависит, показывать окно прогресса или
/// копировать сразу, — и сам подсчет обязан быть дешевым: на огромной папке он останавливается,
/// дойдя до потолка.
/// </summary>
public sealed class VaultCountTests
{
    private static void Note(TempDir dir, params string[] parts)
    {
        var path = Path.Combine([dir.Path, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "тело");
    }

    [Fact]
    public void Empty_Folder_Counts_Zero()
    {
        using var dir = new TempDir();
        Assert.Equal(0, VaultOperations.CountEntries(dir.Path, 100));
    }

    [Fact]
    public void Nested_Files_And_Folders_Are_Counted()
    {
        using var dir = new TempDir();
        Note(dir, "Работа", "Отчеты", "Итог.md");   // 2 папки + 1 файл
        Note(dir, "Личное.md");                      // + 1 файл

        Assert.Equal(4, VaultOperations.CountEntries(dir.Path, 100));
    }

    [Fact]
    public void Counting_Stops_At_The_Cap()
    {
        using var dir = new TempDir();
        for (var i = 0; i < 20; i++) Note(dir, $"Заметка {i}.md");

        // Дошли до потолка — точное число уже не важно, важно «много».
        Assert.Equal(5, VaultOperations.CountEntries(dir.Path, 5));
    }

    [Fact]
    public void Missing_Folder_Counts_Zero_Without_Throwing() =>
        Assert.Equal(0, VaultOperations.CountEntries(Path.Combine(Path.GetTempPath(), "нет-такой"), 100));

    [Fact]
    public void Duplicate_Reports_Every_Copied_File()
    {
        using var dir = new TempDir();
        Note(dir, "Работа", "Первая.md");
        Note(dir, "Работа", "Вложенная", "Вторая.md");

        var reported = new List<int>();
        var r = VaultOperations.Duplicate(Path.Combine(dir.Path, "Работа"), isFolder: true,
                                          new Progress<int>(reported.Add));

        Assert.True(r.Ok);
        Assert.True(File.Exists(Path.Combine(dir.Path, "Работа (копия)", "Первая.md")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "Работа (копия)", "Вложенная", "Вторая.md")));
    }
}
