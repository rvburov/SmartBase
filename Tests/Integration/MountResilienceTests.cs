using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SmartBase.Database;
using SmartBase.Database.Entities;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Монтирование общей базы не виснет и не портит файл: признак общей базы внутри файла
/// (db_meta storage.shared) сильнее определения по пути, чужой живой замок миграций и файл в
/// WAL у другого подключения прерывают монтирование с причиной словами вместо бесконечного
/// ожидания, очередь не начинает монтирование на папке, которая не ответила на пробу, а выход
/// закрывает базы с бюджетом времени.
/// </summary>
public sealed class MountResilienceTests
{
    // ── 1. Признак общей базы внутри файла ───────────────────────────────────────

    [Fact]
    public void Shared_mark_in_file_forces_network_mode_even_when_path_looks_local()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            NetworkPath.ForceNetwork(path);
            using (var m = DatabaseManager.Open(1, "общая", path))
            {
                Assert.True(m.IsShared);
                Assert.Equal("delete", m.JournalMode);
            }
            AppDbContext.ResetPool(path);
            using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                raw.Open();
                Assert.Equal("1", Convert.ToString(Scalar(raw, "SELECT Value FROM db_meta WHERE Key = 'storage.shared';")));
            }

            // Тот же файл открывает копия, для которой путь «локальный» (диск еще не восстановлен
            // после входа в систему, локальный путь на машине с папкой): признак в файле побеждает.
            NetworkPath.Forget(path);
            Assert.False(NetworkPath.IsNetwork(path));   // временная папка — локальный диск

            using (var again = DatabaseManager.Open(1, "общая", path))
            {
                Assert.True(again.IsShared);
                Assert.Equal("delete", again.JournalMode);
            }
            Assert.True(NetworkPath.IsNetwork(path));    // и путь на весь сеанс помечен сетевым
        }
        finally
        {
            NetworkPath.Forget(path);
            TempDatabase.Cleanup(path);
        }
    }

    [Fact]
    public void Local_database_is_not_marked_shared()
    {
        using var db = new TempDatabase();
        Assert.False(db.Manager.IsShared);
        using var raw = new SqliteConnection($"Data Source={db.Path};Pooling=False");
        raw.Open();
        Assert.Equal(0L, Convert.ToInt64(Scalar(raw, "SELECT COUNT(*) FROM db_meta WHERE Key = 'storage.shared';")));
    }

    // ── 2. Чужой живой замок миграций: отказ с причиной, а не вечное ожидание ────

    [Fact]
    public void Live_migration_lock_fails_mount_with_reason_instead_of_hanging()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            using (DatabaseManager.Open(1, "t", path)) { }
            AppDbContext.ResetPool(path);

            using var holder = new SqliteConnection($"Data Source={path};Pooling=False");
            holder.Open();
            Exec(holder, "INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, 'live');");
            Exec(holder, "BEGIN IMMEDIATE;");   // писатель жив: чтение разрешено, запись — BUSY

            var sw = Stopwatch.StartNew();
            var ex = Assert.Throws<MountBlockedException>(() => DatabaseManager.Open(1, "t", path));
            Assert.Equal(MountBlockedException.MigrationElsewhere, ex.Reason);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"монтирование ждало {sw.Elapsed}");
            Exec(holder, "ROLLBACK;");
        }
        finally { TempDatabase.Cleanup(path); }
    }

    [Fact]
    public void Stale_migration_lock_of_dead_owner_is_cleared_and_mount_succeeds()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            using (DatabaseManager.Open(1, "t", path)) { }
            AppDbContext.ResetPool(path);
            using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                raw.Open();
                Exec(raw, "INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, 'stale');");
            }

            var sw = Stopwatch.StartNew();
            using (var m = DatabaseManager.Open(1, "t", path))
                Assert.Equal(1, m.DbId);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"монтирование ждало {sw.Elapsed}");

            AppDbContext.ResetPool(path);
            using var check = new SqliteConnection($"Data Source={path};Pooling=False");
            check.Open();
            Assert.Equal(0L, Convert.ToInt64(Scalar(check, "SELECT COUNT(*) FROM \"__EFMigrationsLock\";")));
        }
        finally { TempDatabase.Cleanup(path); }
    }

    // ── 3. Файл общей базы в WAL держит другое подключение ────────────────────────

    [Fact]
    public void Wal_file_held_by_another_connection_fails_mount_with_wal_reason()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            using (DatabaseManager.Open(1, "t", path)) { }   // локальная база — файл в WAL
            AppDbContext.ResetPool(path);

            using var holder = new SqliteConnection($"Data Source={path};Pooling=False");
            holder.Open();
            Exec(holder, "BEGIN;");
            Scalar(holder, "SELECT COUNT(*) FROM db_meta;");   // открытая читающая транзакция держит WAL

            NetworkPath.ForceNetwork(path);   // теперь тот же файл открывают как общий
            var sw = Stopwatch.StartNew();
            var ex = Assert.Throws<MountBlockedException>(() => DatabaseManager.Open(1, "t", path));
            Assert.Equal(MountBlockedException.WalHeldElsewhere, ex.Reason);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(90), $"монтирование ждало {sw.Elapsed}");
            Exec(holder, "COMMIT;");
        }
        finally
        {
            NetworkPath.Forget(path);
            TempDatabase.Cleanup(path);
        }
    }

    // ── 4. Очередь: папка не ответила на пробу — монтирование не начинается ───────

    [Fact]
    public void Mount_is_not_started_when_share_probe_times_out_and_recovers_later()
    {
        var appDb  = TempDatabase.NewTempPath();
        var def    = TempDatabase.NewTempPath();
        var shared = TempDatabase.NewTempPath();
        var savedWait = MountQueue.PreMountProbeWait;
        try
        {
            using var registry = DatabaseRegistry.Open(def, appDb);
            var defDb = DatabaseManager.Open(registry.GetAll().First(d => d.IsDefault).Id, "def", def);
            using var ws = Workspace.Attach(registry, defDb);
            NetworkPath.ForceNetwork(shared);
            var entry = new DatabaseRefEntity { Id = 31, Name = "S", Path = shared, Provider = "sqlite" };

            var queue = new MountQueue(ws);
            var done  = new ManualResetEventSlim();
            DatabaseManager? result = null;
            queue.Completed += (_, m) => { result = m; done.Set(); };

            MountQueue.PreMountProbeWait = TimeSpan.FromMilliseconds(300);
            DbHealthMonitor.ProbeOverride = _ => { Thread.Sleep(1500); return (true, true); };   // папка «висит»
            queue.Enqueue(entry);
            Assert.True(done.Wait(TimeSpan.FromSeconds(20)), "очередь не завершила попытку");
            Assert.Null(result);
            Assert.Null(ws.Get(31));
            var st = queue.StatusOf(31);
            Assert.NotNull(st);
            Assert.Equal(MountQueue.ItemState.Failed, st!.State);
            Assert.Equal(MountBlockedException.ShareNotResponding, st.Reason);
            Assert.Contains("папка не отвечает", st.Text);

            // Папка ответила — повторная постановка монтирует как обычно. Ожидание пробы — штатное:
            // здесь проверяется возврат папки, а не предел времени. Короткое ожидание из первой
            // части тут ни к чему, а на занятом раннере CI оно роняло тест: проба здорового пути
            // не успевала за 300 мс, и вторая попытка тоже заканчивалась «папка не отвечает».
            DbHealthMonitor.ProbeOverride = null;
            MountQueue.PreMountProbeWait = savedWait;
            done.Reset();
            queue.Enqueue(entry);
            Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "повторное монтирование не завершилось");
            Assert.NotNull(result);
            Assert.NotNull(ws.Get(31));
        }
        finally
        {
            MountQueue.PreMountProbeWait = savedWait;
            DbHealthMonitor.ProbeOverride = null;
            NetworkPath.Forget(shared);
            foreach (var p in new[] { shared, def, appDb }) TempDatabase.Cleanup(p);
        }
    }

    // ── 5. Выход: все базы закрываются в пределах бюджета ─────────────────────────

    [Fact]
    public void CloseAll_closes_every_database_within_budget()
    {
        var appDb = TempDatabase.NewTempPath();
        var def   = TempDatabase.NewTempPath();
        var other = TempDatabase.NewTempPath();
        try
        {
            using var registry = DatabaseRegistry.Open(def, appDb);
            var defDb = DatabaseManager.Open(registry.GetAll().First(d => d.IsDefault).Id, "def", def);
            var ws    = Workspace.Attach(registry, defDb);
            Assert.NotNull(ws.Mount(new DatabaseRefEntity { Id = 41, Name = "O", Path = other, Provider = "sqlite" }));

            var sw = Stopwatch.StartNew();
            Assert.Equal(2, ws.CloseAll(TimeSpan.FromSeconds(30)));
            Assert.Empty(ws.Mounted);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));

            // Файл свободен: пул подключений сброшен вместе с базой.
            File.Delete(other);
            Assert.False(File.Exists(other));
        }
        finally { foreach (var p in new[] { other, def, appDb }) TempDatabase.Cleanup(p); }
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
