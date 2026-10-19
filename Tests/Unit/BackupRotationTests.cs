using SmartBase.Database.Backup;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Имена файлов-копий и ротация «хранить N последних»: копии одной базы узнаются по
/// точному шаблону (имя + штамп времени + расширение), чужие файлы и копии соседних
/// баз не задеваются, а на удаление уходят только самые старые сверх лимита.
/// </summary>
public sealed class BackupRotationTests
{
    [Fact]
    public void Copy_file_name_carries_name_stamp_and_extension()
    {
        Assert.Equal("Моя база (копия 2026-09-05_03-00).db",
            BackupRotation.CopyFileName("Моя база", new DateTime(2026, 9, 5, 3, 0, 0), "db"));
        Assert.Equal("Склад (копия 2026-12-31_23-59).dump",
            BackupRotation.CopyFileName("Склад", new DateTime(2026, 12, 31, 23, 59, 0), "dump"));
    }

    [Fact]
    public void Safe_name_replaces_filesystem_hostile_characters()
    {
        // Разделители путей в имени базы не должны раскидать копии по подпапкам.
        Assert.DoesNotContain('/', BackupRotation.SafeName("до/после"));
        Assert.Equal("база", BackupRotation.SafeName(""));
        Assert.Equal("Обычное имя", BackupRotation.SafeName("Обычное имя"));
    }

    [Fact]
    public void Copy_is_recognized_only_for_its_own_database()
    {
        const string file = "base (копия 2026-09-05_03-00).db";
        Assert.True(BackupRotation.IsCopyOf(file, "base", "db"));
        // «base2» — другая база: ее копии начинаются с «base2 (копия », а не «base (копия ».
        Assert.False(BackupRotation.IsCopyOf("base2 (копия 2026-09-05_03-00).db", "base", "db"));
        Assert.False(BackupRotation.IsCopyOf(file, "base2", "db"));
    }

    public static TheoryData<string> ForeignFiles =>
    [
        "base.db",                                  // сама база, не копия
        "base (копия 2026-09-05_03-00).dump",       // чужое расширение
        "base (копия вчера).db",                    // штамп не по формату
        "base (копия 2026-13-05_03-00).db",         // несуществующий месяц
        "base (копия 2026-09-05_03-00).db.tmp",     // хвост после расширения
        "заметки.txt",                              // посторонний файл в папке
    ];

    [Theory]
    [MemberData(nameof(ForeignFiles))]
    public void Foreign_files_are_not_recognized_as_copies(string fileName)
    {
        Assert.False(BackupRotation.IsCopyOf(fileName, "base", "db"));
    }

    [Fact]
    public void Rotation_keeps_newest_copies_and_deletes_the_rest()
    {
        var copies = new[]
        {
            "base (копия 2026-09-01_03-00).db",
            "base (копия 2026-09-03_03-00).db",
            "base (копия 2026-09-02_03-00).db",
            "base (копия 2026-09-05_03-00).db",
            "base (копия 2026-09-04_03-00).db",
        };

        var doomed = BackupRotation.SelectForDeletion(copies, keep: 2);

        // Остаются два новейших (5-е и 4-е), удаляются три старейших.
        Assert.Equal(
            ["base (копия 2026-09-03_03-00).db",
             "base (копия 2026-09-02_03-00).db",
             "base (копия 2026-09-01_03-00).db"],
            doomed);
    }

    [Fact]
    public void Rotation_deletes_nothing_within_limit()
    {
        var copies = new[] { "base (копия 2026-09-01_03-00).db", "base (копия 2026-09-02_03-00).db" };
        Assert.Empty(BackupRotation.SelectForDeletion(copies, keep: 2));
        Assert.Empty(BackupRotation.SelectForDeletion([], keep: 1));
    }

    [Fact]
    public void Rotation_never_deletes_the_only_copy_even_with_zero_keep()
    {
        // keep вне диапазона (мусор в app.db) не должен снести все копии подчистую.
        var copies = new[] { "base (копия 2026-09-01_03-00).db", "base (копия 2026-09-02_03-00).db" };
        Assert.Equal(["base (копия 2026-09-01_03-00).db"],
                     BackupRotation.SelectForDeletion(copies, keep: 0));
    }

    [Fact]
    public void Unfinished_copy_is_recognized_and_never_counted_as_a_copy()
    {
        // Недописанная копия лежит под именем копии с суффиксом .part: уборка ее находит, а
        // ротация не считает копией — иначе оборванный файл вытеснял бы целую копию.
        const string part = "base (копия 2026-09-05_03-00).db.part";
        Assert.True(BackupRotation.IsPartOf(part, "base", "db"));
        Assert.False(BackupRotation.IsCopyOf(part, "base", "db"));
        Assert.False(BackupRotation.IsPartOf("base2 (копия 2026-09-05_03-00).db.part", "base", "db"));
        Assert.False(BackupRotation.IsPartOf("base (копия 2026-09-05_03-00).db", "base", "db"));
        Assert.False(BackupRotation.IsPartOf("заметки.part", "base", "db"));
    }

    [Fact]
    public void Extension_follows_provider()
    {
        Assert.Equal("db",   BackupRotation.Extension(isPostgres: false));
        Assert.Equal("dump", BackupRotation.Extension(isPostgres: true));
    }
}
