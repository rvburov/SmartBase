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
}
