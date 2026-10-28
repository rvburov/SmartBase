using System.Diagnostics;
using Npgsql;
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
    public async Task Canceling_a_running_dump_stops_its_server_session_and_preserves_the_previous_archive()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        Skip.If(PgDump.FindBinary() is null, "Клиент pg_dump на этой машине не найден");
        using var dir = new TempDir();
        var target = dir.File("copy.dump");
        var previous = "previous-confirmed-archive"u8.ToArray();
        File.WriteAllBytes(target, previous);
        var info = new PgConnectionInfo(PgTestServer.Host, PgTestServer.Port, PgTestServer.Database1,
            PgTestServer.User, PgTestServer.Password, PgConnectionInfo.DefaultSslMode);
        await using var owner = new NpgsqlConnection(PgTestServer.ConnectionString(PgTestServer.Database1));
        await owner.OpenAsync();
        var table = "backup_cancel_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE TABLE {table} (value integer);", owner))
            await create.ExecuteNonQueryAsync();
        using var cancellation = new CancellationTokenSource();
        Task<string?>? dump = null;
        try
        {
            // pg_dump действительно запущен, но ждет блокировку таблицы. Это дает
            // детерминированную точку отмены без огромной базы и гонки по длительности.
            await using (var transaction = await owner.BeginTransactionAsync())
            {
                await using (var locked = new NpgsqlCommand($"LOCK TABLE {table} IN ACCESS EXCLUSIVE MODE;", owner, transaction))
                    await locked.ExecuteNonQueryAsync();
                dump = PgDump.RunAsync(info, "source", target, cancellation.Token);
                await using var observer = new NpgsqlConnection(PgTestServer.ConnectionString(PgTestServer.Database1));
                await observer.OpenAsync();
                var watch = Stopwatch.StartNew();
                int? pid = null;
                while (pid is null)
                {
                    Assert.False(dump.IsCompleted, "pg_dump завершился до проверки отмены");
                    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "pg_dump не дошел до ожидания блокировки");
                    await using var query = new NpgsqlCommand(
                        "SELECT pid FROM pg_stat_activity WHERE application_name LIKE 'SmartBase.backup.%' " +
                        "AND datname=current_database() AND wait_event_type='Lock' LIMIT 1;", observer);
                    if (await query.ExecuteScalarAsync() is int value) pid = value;
                    else await Task.Delay(10);
                }
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dump.WaitAsync(TimeSpan.FromSeconds(10)));
                watch.Restart();
                while (true)
                {
                    await using var stillRunning = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE pid=$1;", observer);
                    stillRunning.Parameters.AddWithValue(pid.Value);
                    if (Equals(0L, await stillRunning.ExecuteScalarAsync())) break;
                    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "После отмены серверная сессия pg_dump осталась открыта");
                    await Task.Delay(10);
                }
                await transaction.RollbackAsync();
            }
            Assert.Equal(previous, await File.ReadAllBytesAsync(target));
            Assert.False(File.Exists(target + DatabaseManager.BackupPartSuffix));
        }
        finally
        {
            cancellation.Cancel();
            if (dump is not null)
            {
                try { await dump.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) { }
            }
            await using var drop = new NpgsqlCommand($"DROP TABLE IF EXISTS {table};", owner);
            await drop.ExecuteNonQueryAsync();
        }
    }

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
