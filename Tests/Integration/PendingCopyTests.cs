using Microsoft.Data.Sqlite;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using static SmartBase.Database.Repositories.ConstructorRepository;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Маркеры незавершенных массовых копий (ConstructorRepository.Pending): уборка при монтировании
/// удаляет только БРОШЕННУЮ копию. Копия соседа по общей базе, которая еще идет, переживает чужое
/// монтирование; брошенная (давно не отмечалась, процесс владельца умер) — удаляется; прерванное
/// удаление исходника после полной копии — доудаляется. Маркеры прежних сборок убираются, как раньше.
/// </summary>
public sealed class PendingCopyTests
{
    /// <summary>Коллега открыл общую базу посреди чужого дубля: копия соседа (отметка свежая,
    /// другой компьютер) остается вместе с маркером. Прежде ее удаляли как брошенную.</summary>
    [Fact]
    public void Fresh_copy_of_another_computer_survives_mount()
    {
        var (copyId, key) = Mounted(m => Marker(m, "OTHER-PC", pid: 4242, beatAgo: TimeSpan.FromMinutes(1)),
                                    out var survived, out var markers);
        Assert.True(survived);
        Assert.Contains(key, markers);
        Assert.NotEqual(0, copyId);
    }

    /// <summary>Отметка чужого компьютера давно не обновлялась — копия брошена: удаляется вместе с маркером.</summary>
    [Fact]
    public void Stale_copy_of_another_computer_is_swept()
    {
        Mounted(m => Marker(m, "OTHER-PC", pid: 4242, beatAgo: PendingStaleAfter + TimeSpan.FromMinutes(1)),
                out var survived, out var markers);
        Assert.False(survived);
        Assert.Empty(markers);
    }

    /// <summary>Копия одной транзакцией (дубль на PostgreSQL) посреди работы не отмечается: ее маркер
    /// ждет дольше обычного срока.</summary>
    [Fact]
    public void Single_transaction_copy_waits_longer()
    {
        Mounted(m => Marker(m, "OTHER-PC", pid: 4242, beatAgo: PendingStaleAfter + TimeSpan.FromMinutes(1), single: true),
                out var survived, out _);
        Assert.True(survived);

        Mounted(m => Marker(m, "OTHER-PC", pid: 4242, beatAgo: PendingStaleAfterSingleTx + TimeSpan.FromMinutes(1), single: true),
                out survived, out _);
        Assert.False(survived);
    }

    /// <summary>Свой компьютер, процесса владельца нет: копия брошена, но не моложе запаса на
    /// компьютер-тезку.</summary>
    [Fact]
    public void Copy_of_a_dead_process_on_this_computer_is_swept_after_grace()
    {
        Mounted(m => Marker(m, Environment.MachineName, DeadPid(), beatAgo: TimeSpan.FromSeconds(5)),
                out var survived, out _);
        Assert.True(survived);

        Mounted(m => Marker(m, Environment.MachineName, DeadPid(), beatAgo: PendingDeadOwnerGrace + TimeSpan.FromMinutes(1)),
                out survived, out var markers);
        Assert.False(survived);
        Assert.Empty(markers);
    }

    /// <summary>Повторное монтирование той же базы этим же процессом (монитор доступности после
    /// потери связи) не трогает копию, которая еще идет, даже с давней отметкой; та же копия,
    /// чья операция уже не идет, убирается сразу.</summary>
    [Fact]
    public void Running_copy_of_this_process_is_not_swept()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            int copyId, deadCopyId;
            PendingCopy running;
            using (var m = DatabaseManager.Open(1, "pending", path))
            {
                copyId     = SampleData.AddCollection(m.Constructor, "Идет").Id!.Value;
                deadCopyId = SampleData.AddCollection(m.Constructor, "Не идет").Id!.Value;
                SampleData.AddRecords(m.Constructor, copyId, count: 3);
                running = BeginPending(m.Context, PendingKind.DuplicateCollection, copyId);
                // Своя же операция, но не в реестре идущих (процесс тот же, ключ чужой).
                InsertMarker(path, $"{PendingPrefix}{PendingKind.DuplicateCollection}.dead",
                    Value(PendingKind.DuplicateCollection, deadCopyId, Environment.MachineName,
                          Environment.ProcessId, ProcessStart, DateTime.UtcNow));
            }
            AppDbContext.ResetPool(path);

            using (var m = DatabaseManager.Open(1, "pending", path))
            {
                var ids = m.Constructor.GetAllCollections().Select(c => c.Id).ToList();
                Assert.Contains(copyId, ids);
                Assert.DoesNotContain(deadCopyId, ids);
                Assert.Equal(3, m.Constructor.CountRecords(copyId));
                FinishPending(m.Context, running);
            }
            Assert.Equal("0", Scalar(path, $"SELECT COUNT(*) FROM db_meta WHERE Key LIKE '{PendingPrefix}%'"));
        }
        finally { TempDatabase.Cleanup(path); }
    }

    /// <summary>Прерванное удаление исходника после полной копии (маркер источника переноса):
    /// монтирование источника доудаляет перенесенную коллекцию — рядом с полной копией в базе
    /// назначения не остается ее половины.</summary>
    [Fact]
    public void Interrupted_source_deletion_is_finished_on_mount()
    {
        Mounted(m => Marker(m, "OTHER-PC", pid: 4242, beatAgo: PendingStaleAfter + TimeSpan.FromMinutes(1),
                            kind: PendingKind.MovedCollection),
                out var survived, out var markers);
        Assert.False(survived);
        Assert.Empty(markers);
    }

    /// <summary>Маркер ветки группы удаляет ветку целиком, с подгруппами и их коллекциями.</summary>
    [Fact]
    public void Abandoned_group_copy_is_swept_with_its_branch()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            int rootId, subId, colId, keepId;
            using (var m = DatabaseManager.Open(1, "pending", path))
            {
                rootId = m.Constructor.AddGroup(new SmartBase.Core.Models.DbGroup { Name = "Ветка (копия)" }).Id!.Value;
                subId  = m.Constructor.AddGroup(new SmartBase.Core.Models.DbGroup { Name = "Подгруппа", ParentId = rootId }).Id!.Value;
                colId  = SampleData.AddCollection(m.Constructor, "В ветке", groupId: subId).Id!.Value;
                keepId = SampleData.AddCollection(m.Constructor, "Вне ветки").Id!.Value;
                SampleData.AddRecords(m.Constructor, colId, count: 4);
                InsertMarker(path, $"{PendingPrefix}{PendingKind.DuplicateGroup}.x",
                    Value(PendingKind.DuplicateGroup, rootId, "OTHER-PC", 4242, "2026-01-01T00:00:00.0000000Z",
                          DateTime.UtcNow - PendingStaleAfter - TimeSpan.FromMinutes(1)));
            }
            AppDbContext.ResetPool(path);

            using (var m = DatabaseManager.Open(1, "pending", path))
            {
                Assert.Equal([keepId], m.Constructor.GetAllCollections().Select(c => c.Id!.Value).ToArray());
                Assert.DoesNotContain(m.Constructor.GetAllGroups(), g => g.Id == rootId || g.Id == subId);
            }
        }
        finally { TempDatabase.Cleanup(path); }
    }

    /// <summary>Маркеры прежних сборок (один на базу, без владельца) убираются, как раньше.</summary>
    [Fact]
    public void Legacy_markers_are_still_swept()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            int colId, rootId, keepId;
            using (var m = DatabaseManager.Open(1, "pending", path))
            {
                colId  = SampleData.AddCollection(m.Constructor, "Перенос").Id!.Value;
                rootId = m.Constructor.AddGroup(new SmartBase.Core.Models.DbGroup { Name = "Ветка" }).Id!.Value;
                SampleData.AddCollection(m.Constructor, "В ветке", groupId: rootId);
                keepId = SampleData.AddCollection(m.Constructor, "Своя").Id!.Value;
                InsertMarker(path, "crossmove.pending.collection", colId.ToString());
                InsertMarker(path, "crossmove.pending.group", rootId.ToString());
            }
            AppDbContext.ResetPool(path);

            using (var m = DatabaseManager.Open(1, "pending", path))
                Assert.Equal([keepId], m.Constructor.GetAllCollections().Select(c => c.Id!.Value).ToArray());
            Assert.Equal("0", Scalar(path, "SELECT COUNT(*) FROM db_meta WHERE Key LIKE 'crossmove.pending.%'"));
        }
        finally { TempDatabase.Cleanup(path); }
    }

    // ── Стенд ─────────────────────────────────────────────────────────────────

    // База с «копией» (коллекция с записями) и маркером на нее; затем повторное монтирование.
    // survived — пережила ли копия монтирование, markers — ключи маркеров после него.
    private static (int CopyId, string Key) Mounted(Func<DatabaseManager, (int CopyId, string Key)> arrange,
                                                    out bool survived, out List<string> markers)
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            (int CopyId, string Key) made;
            using (var m = DatabaseManager.Open(1, "pending", path))
                made = arrange(m);
            AppDbContext.ResetPool(path);

            using (var m = DatabaseManager.Open(1, "pending", path))
                survived = m.Constructor.GetAllCollections().Any(c => c.Id == made.CopyId);
            markers = Column(path, $"SELECT Key FROM db_meta WHERE Key LIKE '{PendingPrefix}%'");
            return made;
        }
        finally { TempDatabase.Cleanup(path); }
    }

    private static (int CopyId, string Key) Marker(DatabaseManager m, string machine, int pid, TimeSpan beatAgo,
                                                   bool single = false, string kind = PendingKind.DuplicateCollection)
    {
        var copyId = SampleData.AddCollection(m.Constructor, "Коллекция (копия)").Id!.Value;
        SampleData.AddRecords(m.Constructor, copyId, count: 5);
        var key = $"{PendingPrefix}{kind}.{Guid.NewGuid():N}";
        InsertMarker(m.Context.DbPath, key,
            Value(kind, copyId, machine, pid, "2026-01-01T00:00:00.0000000Z", DateTime.UtcNow - beatAgo, single));
        return (copyId, key);
    }

    private static string Value(string kind, int target, string machine, int pid, string pstart, DateTime beatUtc,
                                bool single = false) =>
        SerializePending(new PendingValue(1, kind, target, machine, CurrentUser.OwnKey, pid, pstart,
            beatUtc.ToString("o"), beatUtc.ToString("o"), 0, single));

    // Номер процесса, которого нет на этой машине.
    private static int DeadPid()
    {
        for (var pid = 999_999; ; pid--)
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); }
            catch (ArgumentException) { return pid; }
        }
    }

    private static void InsertMarker(string path, string key, string value)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO db_meta(Key, Value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private static string Scalar(string path, string sql)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static List<string> Column(string path, string sql)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }
}
