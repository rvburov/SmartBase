using SmartBase.Database;
using SmartBase.Database.Backup;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Дамп PostgreSQL через pg_dump на стенде sb-pg (порт 5455): файл появляется, непустой
/// и начинается с магии «PGDMP» (формат custom — восстанавливается pg_restore), а
/// неверный пароль дает честную ошибку без недописанного файла. Требует поднятого
/// стенда И клиента pg_dump на машине — иначе тесты пропускаются, как остальные PG-тесты.
/// </summary>
public sealed class PgBackupTests
{
    [SkippableFact]
    public async Task Pg_dump_produces_custom_format_archive()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        Skip.If(PgDump.FindBinary() is null, "Клиент pg_dump на этой машине не найден");

        using var dir = new TempDir();
        var target = dir.File("smartbase (копия 2026-09-05_03-00).dump");
        var info = new PgConnectionInfo(PgTestServer.Host, PgTestServer.Port, PgTestServer.Database1,
                                        PgTestServer.User, PgTestServer.Password,
                                        PgConnectionInfo.DefaultSslMode);

        var error = await PgDump.RunAsync(info, "smartbase", target);

        Assert.Null(error);
        var bytes = await File.ReadAllBytesAsync(target);
        Assert.True(bytes.Length > 5, "дамп подозрительно короткий");
        Assert.Equal("PGDMP"u8.ToArray(), bytes.Take(5).ToArray());
    }

    [SkippableFact]
    public async Task Wrong_password_yields_error_and_no_partial_file()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        Skip.If(PgDump.FindBinary() is null, "Клиент pg_dump на этой машине не найден");

        using var dir = new TempDir();
        var target = dir.File("smartbase (копия 2026-09-05_03-01).dump");
        var info = new PgConnectionInfo(PgTestServer.Host, PgTestServer.Port, PgTestServer.Database1,
                                        PgTestServer.User, "неверный-пароль",
                                        PgConnectionInfo.DefaultSslMode);

        var error = await PgDump.RunAsync(info, "smartbase", target);

        Assert.NotNull(error);
        Assert.False(File.Exists(target), "недописанный дамп должен быть убран");
    }
}
