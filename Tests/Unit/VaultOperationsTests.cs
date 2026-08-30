using System.Text;
using SmartBase.Core.Notes;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Файловые операции раздела «Заметки». Здесь трогаются НАСТОЯЩИЕ файлы, поэтому тесты стерегут не
/// только «получилось», но и «ничего лишнего не потерялось»: сохранность BOM и переводов строк,
/// отсутствие временных хвостов, отказ вместо перезаписи чужого файла.
/// </summary>
public sealed class VaultOperationsTests
{
    private static string Note(TempDir dir, string name, string text = "# заметка")
    {
        var path = Path.Combine(dir.Path, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    // ── Создание ────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_Folder_Makes_It()
    {
        using var dir = new TempDir();

        var r = VaultOperations.CreateFolder(dir.Path, "Работа");

        Assert.True(r.Ok);
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "Работа")));
    }

    [Fact]
    public void Create_Note_Adds_Md_Extension()
    {
        using var dir = new TempDir();

        var r = VaultOperations.CreateNote(dir.Path, "Идеи");

        Assert.True(r.Ok);
        Assert.True(File.Exists(Path.Combine(dir.Path, "Идеи.md")));
    }

    [Fact]
    public void Create_Refuses_A_Taken_Name()
    {
        using var dir = new TempDir();
        Note(dir, "Идеи.md");

        var r = VaultOperations.CreateNote(dir.Path, "Идеи");

        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void Create_Refuses_A_Bad_Name()
    {
        using var dir = new TempDir();

        Assert.False(VaultOperations.CreateFolder(dir.Path, "а/б").Ok);
        Assert.False(VaultOperations.CreateNote(dir.Path, "  ").Ok);
    }

    // ── Переименование ──────────────────────────────────────────────────────────

    [Fact]
    public void Rename_Moves_The_File()
    {
        using var dir = new TempDir();
        var path = Note(dir, "Старое.md");

        var r = VaultOperations.Rename(path, "Новое", isFolder: false);

        Assert.True(r.Ok);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(dir.Path, "Новое.md")));
    }

    [Fact]
    public void Rename_Of_A_Folder_Keeps_Its_Content()
    {
        using var dir = new TempDir();
        Note(dir, Path.Combine("Работа", "Итог.md"));

        var r = VaultOperations.Rename(Path.Combine(dir.Path, "Работа"), "Архив", isFolder: true);

        Assert.True(r.Ok);
        Assert.True(File.Exists(Path.Combine(dir.Path, "Архив", "Итог.md")));
    }

    [Fact]
    public void Rename_Changing_Only_The_Case_Works()
    {
        // На macOS и Windows файловая система обычно не различает регистр, и прямой ход
        // падает «файл уже существует». Операция обязана справляться сама.
        using var dir = new TempDir();
        var path = Note(dir, "заметка.md");

        var r = VaultOperations.Rename(path, "Заметка", isFolder: false);

        Assert.True(r.Ok);
        Assert.Equal("Заметка.md", Path.GetFileName(r.Path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.sbren"));   // промежуточное имя убрано
    }

    [Fact]
    public void Rename_Refuses_To_Overwrite_A_Neighbour()
    {
        using var dir = new TempDir();
        var path = Note(dir, "Первая.md", "первая");
        Note(dir, "Вторая.md", "вторая");

        var r = VaultOperations.Rename(path, "Вторая", isFolder: false);

        Assert.False(r.Ok);
        Assert.Equal("вторая", File.ReadAllText(Path.Combine(dir.Path, "Вторая.md")));
    }

    [Fact]
    public void Rename_To_The_Same_Name_Is_A_No_Op()
    {
        using var dir = new TempDir();
        var path = Note(dir, "Заметка.md");

        var r = VaultOperations.Rename(path, "Заметка", isFolder: false);

        Assert.True(r.Ok);
        Assert.Equal(path, r.Path);
    }

    // ── Дубль и перенос ─────────────────────────────────────────────────────────

    [Fact]
    public void Duplicate_Adds_Copy_Suffix_Before_Extension()
    {
        using var dir = new TempDir();
        var path = Note(dir, "Заметка.md", "тело");

        var r = VaultOperations.Duplicate(path, isFolder: false);

        Assert.True(r.Ok);
        Assert.Equal("Заметка (копия).md", Path.GetFileName(r.Path));
        Assert.Equal("тело", File.ReadAllText(r.Path!));
    }

    [Fact]
    public void Duplicate_Of_A_Folder_Copies_Content()
    {
        using var dir = new TempDir();
        Note(dir, Path.Combine("Работа", "Вложенная", "Итог.md"));

        var r = VaultOperations.Duplicate(Path.Combine(dir.Path, "Работа"), isFolder: true);

        Assert.True(r.Ok);
        Assert.True(File.Exists(Path.Combine(dir.Path, "Работа (копия)", "Вложенная", "Итог.md")));
    }

    [Fact]
    public void Move_Puts_The_File_Into_The_Target_Folder()
    {
        using var dir = new TempDir();
        var path = Note(dir, "Заметка.md");
        Directory.CreateDirectory(Path.Combine(dir.Path, "Работа"));

        var r = VaultOperations.Move(path, Path.Combine(dir.Path, "Работа"), isFolder: false);

        Assert.True(r.Ok);
        Assert.True(File.Exists(Path.Combine(dir.Path, "Работа", "Заметка.md")));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Move_Into_A_Taken_Name_Uniquifies_Instead_Of_Overwriting()
    {
        using var dir = new TempDir();
        var path = Note(dir, "Заметка.md", "моя");
        Note(dir, Path.Combine("Работа", "Заметка.md"), "чужая");

        var r = VaultOperations.Move(path, Path.Combine(dir.Path, "Работа"), isFolder: false);

        Assert.True(r.Ok);
        Assert.Equal("Заметка (копия).md", Path.GetFileName(r.Path));
        Assert.Equal("чужая", File.ReadAllText(Path.Combine(dir.Path, "Работа", "Заметка.md")));
    }

    [Fact]
    public void Folder_Cannot_Be_Moved_Into_Itself()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "Работа", "Отчеты"));

        var r = VaultOperations.Move(Path.Combine(dir.Path, "Работа"),
                                     Path.Combine(dir.Path, "Работа", "Отчеты"), isFolder: true);

        Assert.False(r.Ok);
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "Работа", "Отчеты")));
    }

    [Fact]
    public void Move_Into_The_Same_Folder_Is_A_No_Op()
    {
        using var dir = new TempDir();
        var path = Note(dir, "Заметка.md");

        var r = VaultOperations.Move(path, dir.Path, isFolder: false);

        Assert.True(r.Ok);
        Assert.Equal(path, r.Path);
    }

    // ── Чтение и запись ─────────────────────────────────────────────────────────

    [Fact]
    public void Read_Missing_File_Reports_An_Error()
    {
        using var dir = new TempDir();

        var (content, error) = VaultOperations.ReadNote(Path.Combine(dir.Path, "нет.md"));

        Assert.Null(content);
        Assert.NotNull(error);
    }

    [Fact]
    public void Read_Normalizes_Line_Endings_But_Remembers_Them()
    {
        using var dir = new TempDir();
        var path = Note(dir, "Заметка.md", "первая\r\nвторая");

        var (content, _) = VaultOperations.ReadNote(path);

        Assert.NotNull(content);
        Assert.Equal("первая\nвторая", content!.Text);
        Assert.Equal("\r\n", content.NewLine);
    }

    [Fact]
    public void Write_Restores_Windows_Line_Endings()
    {
        // Иначе система контроля версий покажет измененным весь файл, а не одну правку.
        using var dir = new TempDir();
        var path = Note(dir, "Заметка.md", "первая\r\nвторая");
        var (content, _) = VaultOperations.ReadNote(path);

        VaultOperations.WriteNoteAtomic(path, content!.Text + "\nтретья", content.Bom, content.NewLine);

        Assert.Equal("первая\r\nвторая\r\nтретья", File.ReadAllText(path));
    }

    [Fact]
    public void Byte_Order_Mark_Survives_A_Round_Trip()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "Заметка.md");
        File.WriteAllText(path, "тело", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var (content, _) = VaultOperations.ReadNote(path);
        Assert.True(content!.Bom);
        Assert.Equal("тело", content.Text);

        VaultOperations.WriteNoteAtomic(path, "тело", content.Bom, content.NewLine);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
    }

    [Fact]
    public void Write_Leaves_No_Temporary_Files()
    {
        using var dir = new TempDir();
        var path = Note(dir, "Заметка.md");

        VaultOperations.WriteNoteAtomic(path, "новое тело", bom: false, newLine: "\n");

        Assert.Equal("новое тело", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.sbtmp"));
    }

    [Fact]
    public void Write_Creates_The_File_When_It_Is_Gone()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "Заметка.md");

        var r = VaultOperations.WriteNoteAtomic(path, "тело", bom: false, newLine: "\n");

        Assert.True(r.Ok);
        Assert.Equal("тело", File.ReadAllText(path));
    }

    [Fact]
    public void Oversized_File_Is_Not_Loaded_Into_The_Editor()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "Большая.md");
        File.WriteAllBytes(path, new byte[VaultOperations.MaxOpenBytes + 1]);

        var (content, error) = VaultOperations.ReadNote(path);

        Assert.Null(content);
        Assert.NotNull(error);
    }

    // ── Снимок ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_Is_Null_For_A_Missing_File()
    {
        using var dir = new TempDir();
        Assert.Null(VaultOperations.Snapshot(Path.Combine(dir.Path, "нет.md")));
    }

    [Fact]
    public void Snapshot_Notices_A_Changed_Length()
    {
        using var dir = new TempDir();
        var path = Note(dir, "Заметка.md", "коротко");
        var before = VaultOperations.Snapshot(path);

        File.WriteAllText(path, "заметно длиннее прежнего");

        Assert.NotEqual(before, VaultOperations.Snapshot(path));
    }

    // ── Безвозвратное удаление (развязка, когда корзина недоступна) ──────────────

    [Fact]
    public void Permanent_Delete_Removes_Folder_With_Content()
    {
        using var dir = new TempDir();
        Note(dir, Path.Combine("Работа", "Итог.md"));

        var r = VaultOperations.DeletePermanently(Path.Combine(dir.Path, "Работа"), isFolder: true);

        Assert.True(r.Ok);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "Работа")));
    }
}
