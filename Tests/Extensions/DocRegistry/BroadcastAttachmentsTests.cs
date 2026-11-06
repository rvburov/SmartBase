using SmartBase.Extensions.DocRegistry;
using SmartBase.Tests.Extensions.Shared;

namespace SmartBase.Tests.Extensions.DocRegistry;

public sealed class BroadcastAttachmentsTests
{
    [Fact]
    public void A_single_file_keeps_its_name_and_binary_length()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "Чертеж.bin");
        File.WriteAllBytes(path, [0, 1, 255, 0]);

        var file = Assert.Single(BroadcastAttachments.FromFile(path));

        Assert.Equal(System.IO.Path.GetFullPath(path), file.Path);
        Assert.Equal("Чертеж.bin", file.Name);
        Assert.Equal(4, file.Length);
        BroadcastAttachments.Validate([file]);
    }

    [Fact]
    public void A_folder_is_a_sorted_snapshot_without_subfolders()
    {
        using var dir = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "b.txt"), "b");
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "a.txt"), "a");
        var nested = Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, "nested"));
        File.WriteAllText(System.IO.Path.Combine(nested.FullName, "other.txt"), "other");

        var files = BroadcastAttachments.FromFolder(dir.Path);
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "new.txt"), "new");

        Assert.Equal(["a.txt", "b.txt"], files.Select(f => f.Name));
        BroadcastAttachments.Validate(files);
    }

    [Fact]
    public void An_empty_folder_does_not_create_a_silent_empty_send()
    {
        using var dir = new TempDir();
        Assert.Contains("нет файлов", Assert.Throws<InvalidOperationException>(
            () => BroadcastAttachments.FromFolder(dir.Path)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_removed_file_is_reported_before_sending()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "document.txt");
        File.WriteAllText(path, "test");
        var files = BroadcastAttachments.FromFile(path);
        File.Delete(path);

        Assert.Throws<FileNotFoundException>(() => BroadcastAttachments.Validate(files));
    }

    [Fact]
    public void A_changed_length_requires_selecting_the_files_again()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "document.txt");
        File.WriteAllText(path, "test");
        var files = BroadcastAttachments.FromFile(path);
        File.AppendAllText(path, "more");

        Assert.Contains("изменился", Assert.Throws<InvalidOperationException>(
            () => BroadcastAttachments.Validate(files)).Message, StringComparison.Ordinal);
    }
}
