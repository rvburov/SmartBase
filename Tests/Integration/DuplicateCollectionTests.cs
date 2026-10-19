using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Дубль коллекции пакетами (<see cref="ConstructorRepository.CopyCollectionData"/>): каждое
/// значение и строка проекции календаря попадают к записи того же ранга, что и в источнике, при
/// любой нарезке на пакеты; между пакетами база отпускается (общая база в сетевой папке не
/// заперта на всю копию); сбой посреди копии и выход из программы не оставляют недописанную копию.
/// </summary>
public sealed class DuplicateCollectionTests
{
    /// <summary>Нарезка на пакеты не путает записи: порядок источника (Position, Id) отличается от
    /// порядка Id, пакет не кратен числу записей, а в копии у записи того же ранга те же название,
    /// значения и строки календаря.</summary>
    [Fact]
    public void Duplicate_in_batches_copies_every_value_to_the_matching_record()
    {
        using var db = new TempDatabase();
        var (colId, textId, dateId) = Seed(db.Constructor, records: 53);

        var newColId = Duplicate(db, colId, batchSize: 10);

        var src  = Ranked(db.Path, colId);
        var copy = Ranked(db.Path, newColId);
        Assert.Equal(53, copy.Count);
        var fields = db.Constructor.GetFields(newColId).ToDictionary(f => f.Name, f => f.Id!.Value);
        var srcValues  = db.Constructor.GetFieldValuesByCollection(colId);
        var copyValues = db.Constructor.GetFieldValuesByCollection(newColId);
        for (var i = 0; i < src.Count; i++)
        {
            Assert.Equal(src[i].Title, copy[i].Title);
            Assert.Equal(srcValues[src[i].Id][textId], copyValues[copy[i].Id][fields["Текст"]]);
            Assert.Equal(srcValues[src[i].Id][dateId], copyValues[copy[i].Id][fields["Дата"]]);
        }
        Assert.Equal(Scalar(db.Path, $"SELECT COUNT(*) FROM calendar_index WHERE CollectionId = {colId}"),
                     Scalar(db.Path, $"SELECT COUNT(*) FROM calendar_index WHERE CollectionId = {newColId}"));
        Assert.Equal("0", Scalar(db.Path, $"SELECT COUNT(*) FROM db_meta WHERE Key = '{ConstructorRepository.PendingDuplicateKey}'"));
    }

    /// <summary>Между пакетами копия не держит базу: у общей базы в сетевой папке (журнал DELETE)
    /// долгая транзакция запирала ее для интерфейса и соседей до конца копии. Посреди дубля другое
    /// подключение успевает записать без ожидания.</summary>
    [Fact]
    public void Duplicate_releases_the_database_between_batches()
    {
        using var db = new TempDatabase();
        var (colId, _, _) = Seed(db.Constructor, records: 40);

        var probes = new List<bool>();
        Duplicate(db, colId, batchSize: 10, progress: done =>
        {
            if (done == 0 || done >= 40) return;
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = db.Path, Pooling = false, DefaultTimeout = 1 }.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            // Настоящая запись: пока копия держит транзакцию, она ждет таймаут и падает «database is locked».
            cmd.CommandText = "INSERT INTO db_meta(Key, Value) VALUES ('test.probe', '1'); DELETE FROM db_meta WHERE Key = 'test.probe';";
            try { cmd.ExecuteNonQuery(); probes.Add(true); }
            catch (SqliteException) { probes.Add(false); }
        });

        Assert.NotEmpty(probes);
        Assert.All(probes, Assert.True);
    }

    /// <summary>Сбой посреди копии (здесь — на третьем пакете значений): копия убирается целиком,
    /// источник цел, маркера незавершенного дубля не остается.</summary>
    [Fact]
    public void Failed_duplicate_leaves_no_partial_copy()
    {
        using var db = new TempDatabase();
        var (colId, _, _) = Seed(db.Constructor, records: 40);
        var collectionsBefore = db.Constructor.GetAllCollections().Count;

        using (var c = Open(db.Path))
            Exec(c, $@"CREATE TRIGGER test_boom BEFORE INSERT ON db_field_values
                       WHEN NEW.RecordId > (SELECT MAX(Id) FROM db_records WHERE CollectionId = {colId}) + 25
                       BEGIN SELECT RAISE(ABORT, 'test boom'); END;");

        Assert.ThrowsAny<Exception>(() => Duplicate(db, colId, batchSize: 10));

        Assert.Equal(collectionsBefore, db.Constructor.GetAllCollections().Count);
        Assert.Equal(40, db.Constructor.CountRecords(colId));
        Assert.Equal("0", Scalar(db.Path, $"SELECT COUNT(*) FROM db_meta WHERE Key = '{ConstructorRepository.PendingDuplicateKey}'"));
    }

    /// <summary>Выход из программы посреди копии оставляет маркер: следующее монтирование базы
    /// убирает недописанную копию и маркер.</summary>
    [Fact]
    public void Unfinished_duplicate_is_swept_on_mount()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            int colId, partialId;
            using (var m = DatabaseManager.Open(1, "sweep", path))
            {
                (colId, _, _) = Seed(m.Constructor, records: 12);
                partialId = SampleData.AddCollection(m.Constructor, "Коллекция (копия)").Id!.Value;
                SampleData.AddRecords(m.Constructor, partialId, count: 5);
                using var c = Open(path);
                Exec(c, $"INSERT INTO db_meta(Key, Value) VALUES ('{ConstructorRepository.PendingDuplicateKey}', '{partialId}');");
            }
            AppDbContext.ResetPool(path);

            using (var m = DatabaseManager.Open(1, "sweep", path))
            {
                Assert.DoesNotContain(m.Constructor.GetAllCollections(), c => c.Id == partialId);
                Assert.Equal(12, m.Constructor.CountRecords(colId));
            }
            Assert.Equal("0", Scalar(path, $"SELECT COUNT(*) FROM db_meta WHERE Key = '{ConstructorRepository.PendingDuplicateKey}'"));
        }
        finally { TempDatabase.Cleanup(path); }
    }

    // ── Стенд ─────────────────────────────────────────────────────────────────

    // Коллекция с текстом и датой (дата — источник календаря, у каждой записи строка проекции).
    // Позиции идут против порядка Id: ранг (Position, Id) у копии обязан совпасть с источником.
    private static (int ColId, int TextId, int DateId) Seed(ConstructorRepository repo, int records)
    {
        var col  = SampleData.AddCollection(repo);
        var text = SampleData.AddField(repo, col.Id!.Value, "Текст", position: 1000);
        var date = SampleData.AddCalendarDateField(repo, col.Id!.Value, position: 2000);
        for (var i = 0; i < records; i++)
        {
            var rec = SampleData.AddRecord(repo, col.Id!.Value, $"R{i:D3}", position: (records - i) * 10);
            repo.SetFieldValue(rec.Id!.Value, text.Id!.Value, $"значение {i}");
            repo.SetFieldValue(rec.Id!.Value, date.Id!.Value, new DateTime(2026, 1, 1).AddDays(i).ToString("yyyy-MM-dd"));
        }
        return (col.Id!.Value, text.Id!.Value, date.Id!.Value);
    }

    // Дубль так же, как его делает боковая панель: шелл и данные на отдельном подключении.
    private static int Duplicate(TempDatabase db, int colId, int batchSize, Action<int>? progress = null)
    {
        using var ctx = new AppDbContext(db.Path);
        var repo  = new ConstructorRepository(ctx);
        var shell = repo.CreateCollectionShell(colId);
        repo.CopyCollectionData(colId, shell.Collection.Id!.Value,
                                progress is null ? null : new SyncProgress(progress), batchSize);
        return shell.Collection.Id!.Value;
    }

    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private static List<(int Id, string Title)> Ranked(string path, int colId)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT Id, Title FROM db_records WHERE CollectionId = {colId} ORDER BY Position, Id";
        using var r = cmd.ExecuteReader();
        var list = new List<(int, string)>();
        while (r.Read()) list.Add((r.GetInt32(0), r.GetString(1)));
        return list;
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

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
