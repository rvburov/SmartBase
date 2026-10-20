using SmartBase.Database;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Классификация типа диска Windows (<see cref="NetworkPath.ClassifyDrive"/>): сетевой диск —
/// сеть; локальные типы — не сеть; отсутствующий или неизвестный диск — сеть «до выяснения»,
/// без кеша, а с записью в постоянных подключениях — уверенно сеть. Именно неуверенный случай
/// раньше давал «локально» для подключенного диска, еще не восстановленного после входа в
/// систему, и общая база уезжала в WAL.
/// </summary>
public sealed class NetworkPathTests
{
    [Theory]
    [InlineData(DriveType.Network,   false, true,  true)]
    [InlineData(DriveType.Network,   true,  true,  true)]
    [InlineData(DriveType.Fixed,     false, false, true)]
    [InlineData(DriveType.Removable, false, false, true)]
    [InlineData(DriveType.CDRom,     false, false, true)]
    [InlineData(DriveType.Ram,       false, false, true)]
    public void Known_drive_types_are_classified_definitively(DriveType type, bool mapped, bool network, bool definitive) =>
        Assert.Equal((network, definitive), NetworkPath.ClassifyDrive(type, mapped));

    [Theory]
    [InlineData(DriveType.NoRootDirectory)]
    [InlineData(DriveType.Unknown)]
    public void Absent_drive_is_network_until_proven_otherwise_and_not_cached(DriveType type)
    {
        Assert.Equal((true, false), NetworkPath.ClassifyDrive(type, persistentMapping: false));
        // Буква числится в постоянных подключениях пользователя — это подключенный сетевой диск,
        // просто еще не восстановленный: уверенно сеть.
        Assert.Equal((true, true), NetworkPath.ClassifyDrive(type, persistentMapping: true));
    }

    [Fact]
    public void Unc_path_is_network_and_forget_clears_forced_mark()
    {
        Assert.True(NetworkPath.IsNetwork(@"\\server\share\folder\base.db"));

        var local = Path.Combine(Path.GetTempPath(), "np-forget-test.db");
        NetworkPath.ForceNetwork(local);
        Assert.True(NetworkPath.IsNetwork(local));
        NetworkPath.Forget(local);
        Assert.False(NetworkPath.IsNetwork(local));   // временная папка — локальный диск
    }

    // ── Общая папка на самом компьютере ───────────────────────────────────────

    /// <summary>
    /// Папки общих ресурсов Windows из реестра: только диски, путь из строки «Path=». Принтер
    /// (Type=1) и запись без пути — мимо.
    /// </summary>
    [Fact]
    public void Share_folders_are_read_from_registry_entries()
    {
        var folders = NetworkPath.ShareFolders(
        [
            ["CATimeout=0", "CSCFlags=0", "MaxUses=4294967295", @"Path=D:\Общая", "Permissions=0", "Remark=", "Type=0"],
            [@"Path=C:\Users\rv\Стенд", "Type=0"],
            [@"Path=C:\Windows\system32\spool\drivers", "Type=1"],
            ["Remark=без пути", "Type=0"],
        ]);
        Assert.Equal([@"D:\Общая", @"C:\Users\rv\Стенд"], folders);
    }

    [Theory]
    [InlineData(@"D:\Общая\wide10m.db",       true)]
    [InlineData(@"d:\общая\база\wide10m.db",  true)]    // регистр не важен
    [InlineData(@"D:\Общая2\wide10m.db",      false)]   // соседняя папка с тем же началом
    [InlineData(@"D:\Прочее\wide10m.db",      false)]
    [InlineData(@"E:\wide10m.db",              true)]    // раздан весь диск E:\
    public void Path_inside_a_shared_folder_is_recognized(string path, bool inside) =>
        Assert.Equal(inside, NetworkPath.IsInside(path, [@"D:\Общая\", @"E:\"]));

    /// <summary>
    /// Файл в папке, которую этот компьютер раздает другим, — общая база и для него самого:
    /// путь локальный, а файл открывают по сети коллеги. Иначе компьютер-хозяин переводил файл в
    /// WAL, и у остальных база стояла «недоступна (файл в режиме WAL, его держит другой компьютер)».
    /// </summary>
    [Fact]
    public void File_in_folder_shared_by_this_computer_is_network()
    {
        var dir  = Path.Combine(Path.GetTempPath(), "np-share-" + Guid.NewGuid().ToString("N")[..8]);
        var file = Path.Combine(dir, "wide10m.db");
        var other = Path.Combine(Path.GetTempPath(), "np-share-other.db");
        try
        {
            NetworkPath.LocalSharesOverride = () => [dir];
            NetworkPath.Forget(file);
            NetworkPath.Forget(other);
            Assert.True(NetworkPath.IsNetwork(file));
            Assert.False(NetworkPath.IsNetwork(other));
        }
        finally
        {
            NetworkPath.LocalSharesOverride = null;
            NetworkPath.Forget(file);
            NetworkPath.Forget(other);
        }
    }
}
