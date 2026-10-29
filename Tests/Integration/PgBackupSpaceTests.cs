using SmartBase.Database;
using SmartBase.Database.Backup;

namespace SmartBase.Tests.Integration;

/// <summary>Проверка места PostgreSQL не объявляет запись и не удаляет старые файлы при отказе.</summary>
public sealed class PgBackupSpaceTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refused_preflight_keeps_the_previous_archive_and_partial_file(bool unknown)
    {
        RequirePostgres();
        using var dir = new TempDir();
        var target = dir.File("copy.dump");
        var part = target + DatabaseManager.BackupPartSuffix;
        var previous = "previous-verified-archive"u8.ToArray();
        var pending = "unfinished-earlier-attempt"u8.ToArray();
        await File.WriteAllBytesAsync(target, previous);
        await File.WriteAllBytesAsync(part, pending);
        var stages = new List<string>();
        var before = BackupSpace.AvailableBytesOverride.Value;
        BackupSpace.AvailableBytesOverride.Value = folder =>
        {
            Assert.Equal(Path.GetFullPath(dir.Path), folder);
            return unknown ? null : 0;
        };
        string? error;
        try
        {
            error = await PgDump.RunAsync(Connection(), "source", target,
                stage: (message, _) => stages.Add(message));
        }
        finally { BackupSpace.AvailableBytesOverride.Value = before; }
        Assert.NotNull(error);
        Assert.Contains(Path.GetFullPath(dir.Path), error);
        Assert.Contains(unknown ? "проверить" : "Недостаточно", error);
        Assert.DoesNotContain(stages, message => message.StartsWith("Сохраняю", StringComparison.Ordinal));
        Assert.Equal(previous, await File.ReadAllBytesAsync(target));
        Assert.Equal(pending, await File.ReadAllBytesAsync(part));
    }

    [SkippableFact]
    public async Task Dump_is_announced_only_after_the_target_capacity_was_checked()
    {
        RequirePostgres();
        using var dir = new TempDir();
        var target = dir.File("copy.dump");
        var checkedSpace = false;
        var stages = new List<string>();
        var before = BackupSpace.AvailableBytesOverride.Value;
        BackupSpace.AvailableBytesOverride.Value = folder =>
        {
            Assert.Equal(Path.GetFullPath(dir.Path), folder);
            checkedSpace = true;
            return long.MaxValue;
        };
        string? error;
        try
        {
            error = await PgDump.RunAsync(Connection(), "source", target, stage: (message, _) =>
            {
                if (message.StartsWith("Сохраняю", StringComparison.Ordinal))
                    Assert.True(checkedSpace, "Запись объявлена до проверки целевой папки");
                stages.Add(message);
            });
        }
        finally { BackupSpace.AvailableBytesOverride.Value = before; }
        Assert.Null(error);
        Assert.True(checkedSpace);
        Assert.StartsWith("Проверяю свободное место", stages[0]);
        Assert.Contains(stages, message => message.StartsWith("Сохраняю", StringComparison.Ordinal));
        var archive = await File.ReadAllBytesAsync(target);
        Assert.True(archive.Length > 5);
        Assert.Equal("PGDMP"u8.ToArray(), archive.Take(5).ToArray());
        Assert.False(File.Exists(target + DatabaseManager.BackupPartSuffix));
    }

    private static void RequirePostgres()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL на 5455 не поднят");
        Skip.If(PgDump.FindBinary() is null, "Клиент pg_dump не установлен");
    }

    private static PgConnectionInfo Connection() => new(PgTestServer.Host, PgTestServer.Port,
        PgTestServer.Database1, PgTestServer.User, PgTestServer.Password, PgConnectionInfo.DefaultSslMode);
}
