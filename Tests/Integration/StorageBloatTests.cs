using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Importers;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Файл базы не должен раздуваться: пустые ячейки не хранятся, очистка ячейки убирает строку,
/// у локальной базы ограничен WAL, свободные страницы после удалений возвращаются по политике
/// (локально — сама при монтировании, общая — только руками), ручное сжатие и резервная копия
/// не портят данные. Стенд с числами — Tests/Benchmarks (dbsize, bloat).
/// </summary>
public sealed class StorageBloatTests
{
    // ── 1. Пустые ячейки не хранятся ──────────────────────────────────────────

    /// <summary>Импорт таблицы: строк в db_field_values ровно столько, сколько НЕПУСТЫХ ячеек
    /// (раньше каждая пустая ячейка давала строку «» плюс запись в индексе значений).</summary>
    [Fact]
    public void Import_does_not_store_empty_cells()
    {
        using var db = new TempDatabase();
        var schema = new ImportSchema("Название", ["A", "B", "C"], 3, 0);
        var rows = new[]
        {
            new ImportRow("r1", ["a1", "",   "c1"]),
            new ImportRow("r2", ["",   "",   ""  ]),
            new ImportRow("r3", ["a3", "b3", ""  ]),
        };
        var colId = db.Constructor.ImportStreamedCollectionIsolated("Импорт", null, schema, rows, CancellationToken.None);

        Assert.Equal(3, db.Constructor.CountRecords(colId));
        Assert.Equal(4, CountValues(db.Path, colId));   // a1, c1, a3, b3

        // Читается так же, как и раньше: отсутствие строки = пусто.
        var recs   = db.Constructor.GetRecordsPageAfter(colId, null, null, 10);
        var fields = db.Constructor.GetFields(colId).OrderBy(f => f.Position).ToList();
        var values = db.Constructor.GetFieldValuesBatch(recs.Select(r => r.Id!.Value).ToList());
        var r2 = recs.Single(r => r.Title == "r2").Id!.Value;
        var r3 = recs.Single(r => r.Title == "r3").Id!.Value;
        Assert.True(!values.TryGetValue(r2, out var v2) || v2.Values.All(string.IsNullOrEmpty));
        Assert.Equal("b3", values[r3][fields[1].Id!.Value]);
        Assert.False(values[r3].TryGetValue(fields[2].Id!.Value, out var c3) && c3.Length > 0);
    }

    /// <summary>Очистка ячейки удаляет строку значения, а не оставляет «» (инвариант
    /// «нет строки = пусто»); повторная очистка и запись нового значения работают.</summary>
    [Fact]
    public void Clearing_cell_removes_value_row()
    {
        using var db = new TempDatabase();
        var (colId, fid) = Shell(db.Constructor, "Очистка");
        var rec = db.Constructor.AddRecord(new DbRecord { CollectionId = colId, Title = "r" });
        var rid = rec.Id!.Value;

        db.Constructor.SetFieldValue(rid, fid, "значение");
        Assert.Equal(1, CountValues(db.Path, colId));

        db.Constructor.SetFieldValue(rid, fid, "");
        Assert.Equal(0, CountValues(db.Path, colId));
        Assert.False(db.Constructor.GetFieldValuesBatch([rid]).TryGetValue(rid, out var v) && v.ContainsKey(fid));

        db.Constructor.SetFieldValue(rid, fid, "");        // повторная очистка — тишина
        db.Constructor.SetFieldValue(rid, fid, "снова");   // и запись после очистки
        Assert.Equal("снова", db.Constructor.GetFieldValuesBatch([rid])[rid][fid]);
    }

    // ── 2. WAL локальной базы ─────────────────────────────────────────────────

    /// <summary>После массовой заливки на изолированном контексте чекпоинт переносит WAL в файл;
    /// лимит размера журнала выставлен на подключении (усечение — при следующем чекпоинте).</summary>
    [Fact]
    public void Bulk_context_checkpoints_wal()
    {
        using var db = new TempDatabase();
        var (colId, fid) = Shell(db.Constructor, "WAL");

        using (var ctx = new AppDbContext(db.Path))
        {
            ctx.ApplyBulkPerformancePragmas();
            var repo = new ConstructorRepository(ctx);
            repo.BulkAddRecords(colId, [fid], Rows(5000, 300), 5000, CancellationToken.None);
            Assert.True(new FileInfo(db.Path + "-wal").Length > 0, "после заливки WAL должен быть непустым");
            ctx.CheckpointWal();
            var conn = ctx.Database.GetDbConnection();
            conn.Open();
            Assert.Equal(AppDbContext.WalSizeLimitBytes.ToString(), Scalar(conn, "PRAGMA journal_size_limit;"));
            // Все кадры перенесены: следующий писатель начнет WAL с начала.
            Assert.Equal("0", Scalar(conn, "PRAGMA wal_checkpoint(PASSIVE);").Split(',')[0].Trim());
        }
        Assert.Equal(5000, db.Constructor.CountRecords(colId));
    }

    // ── 3. Политика свободного места ──────────────────────────────────────────

    [Theory]
    [InlineData(65L * 1024 * 1024, 0.30, true)]    // > 64 МБ и четверть файла — сжимаем
    [InlineData(10L * 1024 * 1024, 0.90, false)]   // мало байт, пусть и 90 % — не трогаем
    [InlineData(600L * 1024 * 1024, 0.05, true)]   // доля мала, но 600 МБ мертвых страниц — сжимаем
    [InlineData(100L * 1024 * 1024, 0.10, false)]  // 100 МБ и 10 % — ниже обоих порогов
    public void Compaction_threshold(long freeBytes, double ratio, bool expected) =>
        Assert.Equal(expected, DatabaseManager.NeedsCompaction(freeBytes, ratio));

    /// <summary>Локальная база с мертвыми страницами (свод залили, удалили) ужимается сама при
    /// следующем монтировании; сведения о файле после монтирования — без свободных страниц.</summary>
    [Fact]
    public void Local_bloated_database_is_compacted_at_mount()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            Bloat(path, rows: 40_000, valueLength: 1500);   // ~120 МБ мертвых страниц, почти весь файл
            var before = FreeSpace(path);
            Assert.True(DatabaseManager.NeedsCompaction(before.FreeBytes, before.FreeRatio),
                        $"стенд не раздут: свободно {before.FreeBytes} байт ({before.FreeRatio:P0})");
            var fileBefore = new FileInfo(path).Length;

            // В пуле остается простаивающее подключение с mmap (так живет любой фоновый
            // контекст): без сброса пула перед VACUUM Windows не усекает файл, и он оставался бы
            // прежнего размера при пустом freelist.
            using (var stray = new AppDbContext(path))
            {
                stray.ApplyPerformancePragmas();
                stray.DbCollections.Count();
            }

            using var m = DatabaseManager.Open(3, "local", path);
            var after = FreeSpace(path);
            Assert.Equal(0, after.FreeBytes);
            Assert.NotNull(m.Storage);
            Assert.Equal(0, m.Storage!.FreeBytes);
            Assert.False(m.Storage.NeedsCompaction);
            // Главное — файл на диске действительно уменьшился, а не только счетчик страниц.
            Assert.True(new FileInfo(path).Length < fileBefore / 4,
                        $"файл не усечен: было {fileBefore}, стало {new FileInfo(path).Length}");
        }
        finally { TempDatabase.Cleanup(path); }
    }

    /// <summary>Общая база (сетевой профиль) при монтировании НЕ сжимается, как бы раздута ни
    /// была: VACUUM через сеть под эксклюзивным замком — только руками администратора. Сведения
    /// о файле при этом честно говорят «стоит сжать».</summary>
    [Fact]
    public void Shared_bloated_database_is_not_compacted_at_mount()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            NetworkPath.ForceNetwork(path);
            Bloat(path, rows: 40_000, valueLength: 1500);
            var before = FreeSpace(path);
            Assert.True(before.FreeBytes > 0);

            using var m = DatabaseManager.Open(4, "shared", path);
            Assert.True(m.IsShared);
            var after = FreeSpace(path);
            Assert.Equal(before.FreeBytes, after.FreeBytes);
            Assert.True(m.Storage!.NeedsCompaction);
        }
        finally { TempDatabase.Cleanup(path); }
    }

    // ── 4. Ручное сжатие и резервная копия ────────────────────────────────────

    /// <summary>Ручное сжатие возвращает место и сохраняет данные; прогресс приходит с фазой
    /// «Сжимаю файл».</summary>
    [Fact]
    public void CompactFile_returns_space_and_keeps_data()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            NetworkPath.ForceNetwork(path);   // общая: сама при монтировании не ужмется
            var live = Bloat(path, rows: 20_000, valueLength: 1500, keepSecond: true);
            var before = FreeSpace(path);
            Assert.True(before.FreeBytes > 0);

            var fileBefore = new FileInfo(path).Length;
            var phases = new List<string>();
            DatabaseManager.CompactFile(path, "shared", (msg, _) => { lock (phases) phases.Add(msg); });

            Assert.Equal(0, FreeSpace(path).FreeBytes);
            Assert.True(new FileInfo(path).Length < fileBefore * 2 / 3,
                        $"файл не усечен: было {fileBefore}, стало {new FileInfo(path).Length}");
            using var m = DatabaseManager.Open(5, "shared", path);
            Assert.Equal(live.Rows, m.Constructor.CountRecords(live.CollectionId));
            Assert.Equal("ok", Integrity(path));
        }
        finally { TempDatabase.Cleanup(path); }
    }

    /// <summary>Файл занят чужим писателем: сжатие отказывает ошибкой «database is locked»,
    /// не дожидаясь вечно, и данные целы.</summary>
    [Fact]
    public void CompactFile_on_locked_file_fails_cleanly()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            var live = Bloat(path, rows: 2_000, valueLength: 200, keepSecond: true);

            using (var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                holder.Open();
                Exec(holder, "BEGIN IMMEDIATE; INSERT INTO db_meta (Key, Value) VALUES ('lock.test', '1');");
                var ex = Assert.ThrowsAny<Exception>(() => DatabaseManager.CompactFile(path, "busy", null));
                Assert.True(DbHealthMonitor.IsBusyError(ex), $"ожидалась занятость файла, получено: {ex.GetType().Name}: {ex.Message}");
                Exec(holder, "ROLLBACK;");
            }

            Assert.Equal("ok", Integrity(path));
            using var m = DatabaseManager.Open(6, "busy", path);
            Assert.Equal(live.Rows, m.Constructor.CountRecords(live.CollectionId));
        }
        finally { TempDatabase.Cleanup(path); }
    }

    /// <summary>Резервная копия — компактная (без свободных страниц), целая, с теми же данными;
    /// источник при этом не меняется.</summary>
    [Fact]
    public void Backup_copy_is_compact_and_valid()
    {
        var path   = TempDatabase.NewTempPath();
        var target = TempDatabase.NewTempPath();
        try
        {
            var live = Bloat(path, rows: 5_000, valueLength: 500, keepSecond: true);
            var srcBefore = FreeSpace(path);
            Assert.True(srcBefore.FreeBytes > 0);

            var phases = new List<string>();
            DatabaseManager.BackupTo(path, "src", target, (msg, _) => { lock (phases) phases.Add(msg); });

            Assert.Equal(srcBefore.FreeBytes, FreeSpace(path).FreeBytes);   // источник не трогали
            Assert.Equal(0, FreeSpace(target).FreeBytes);                   // копия без freelist
            Assert.Equal("ok", Integrity(target));
            Assert.Contains(phases, p => p.StartsWith("Проверяю копию"));

            using var copy = DatabaseManager.Open(7, "copy", target);
            Assert.Equal(live.Rows, copy.Constructor.CountRecords(live.CollectionId));
        }
        finally { TempDatabase.Cleanup(path); TempDatabase.Cleanup(target); }
    }

    // ── Стенд ─────────────────────────────────────────────────────────────────

    private sealed record Live(int CollectionId, int Rows);

    /// <summary>Раздуть файл: залить коллекцию с длинными значениями и удалить ее (мертвые
    /// страницы остаются в freelist). С <paramref name="keepSecond"/> рядом остается вторая,
    /// живая коллекция вдвое меньше — чтобы было что сверять после сжатия/копии.</summary>
    private static Live Bloat(string path, int rows, int valueLength, bool keepSecond = false)
    {
        Live live;
        using (var m = DatabaseManager.Open(2, "bloat", path))
        {
            var (colId, fid) = Shell(m.Constructor, "Свод 1");
            m.Constructor.BulkAddRecords(colId, [fid], Rows(rows, valueLength), rows, CancellationToken.None);

            live = new Live(0, 0);
            if (keepSecond)
            {
                var (col2, fid2) = Shell(m.Constructor, "Свод 2");
                m.Constructor.BulkAddRecords(col2, [fid2], Rows(rows / 2, valueLength), rows / 2, CancellationToken.None);
                live = new Live(col2, rows / 2);
            }

            m.Constructor.DeleteCollectionIsolated(colId);
            // Мертвые страницы должны оказаться в самом файле, а не в -wal.
            m.Context.CheckpointWal();
        }
        return live;
    }

    private static (int CollectionId, int FieldId) Shell(ConstructorRepository repo, string name)
    {
        var col = repo.AddCollection(new DbCollection { Name = name, Position = 1000 });
        var fld = repo.AddField(new DbField { CollectionId = col.Id!.Value, Name = "Текст", FieldType = "text", Position = 1000 });
        return (col.Id!.Value, fld.Id!.Value);
    }

    private static IEnumerable<BulkRecordRow> Rows(int count, int valueLength)
    {
        for (var i = 0; i < count; i++)
            yield return new BulkRecordRow($"Запись {i}", [new string('x', valueLength - 8) + i.ToString("D8")]);
    }

    private static (long FreeBytes, double FreeRatio) FreeSpace(string path)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        var ps = long.Parse(Scalar(c, "PRAGMA page_size;"));
        var pc = long.Parse(Scalar(c, "PRAGMA page_count;"));
        var fl = long.Parse(Scalar(c, "PRAGMA freelist_count;"));
        return (fl * ps, pc == 0 ? 0 : fl / (double)pc);
    }

    private static string Integrity(string path)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        return Scalar(c, "PRAGMA integrity_check;");
    }

    private static long CountValues(string path, int collectionId)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        return long.Parse(Scalar(c,
            $"SELECT COUNT(*) FROM db_field_values v JOIN db_records r ON r.Id = v.RecordId WHERE r.CollectionId = {collectionId};"));
    }

    private static string Scalar(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
