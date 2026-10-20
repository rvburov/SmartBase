using Microsoft.Data.Sqlite;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Перенос между базами SQLite (ATTACH) пакетами (<see cref="ConstructorRepository.MoveCollectionIsolated"/>):
/// у каждого пакета своя транзакция приемника, и при любой нарезке значение, строка календаря и
/// UI-строка попадают к записи того же ранга. Источник на все время копии заморожен (запись в него
/// ждет), а приемник между пакетами свободен. Удаление исходника после полной копии: не удалось
/// ничего — перенос откатывается; удалено частично — копия остается, остаток доудаляет уборка при
/// монтировании источника (прежде копию удаляли и тогда — данные пропадали в обеих базах).
/// </summary>
public sealed class CrossMoveBatchTests
{
    /// <summary>Нарезка на пакеты не путает записи: порядок источника (Position, Id) обратен
    /// порядку Id, пакет не кратен числу записей; у записи того же ранга в копии те же название,
    /// значения, строки календаря и высота строки; маркеров не остается ни в одной базе.</summary>
    [Fact]
    public void Batched_move_keeps_every_value_with_its_record()
    {
        using var src = new TempDatabase(dbId: 1, name: "src");
        using var tgt = new TempDatabase(dbId: 2, name: "tgt");
        var (colId, textId, dateId) = Seed(src.Constructor, records: 53);
        var srcRanked = Ranked(src.Path, colId);
        var srcValues = src.Constructor.GetFieldValuesByCollection(colId);
        var heights   = srcRanked.Where((_, i) => i % 7 == 0).ToDictionary(r => r.Id, r => 20 + r.Id % 13);
        src.Constructor.SaveRowHeights(colId, heights);
        var srcCalendar = Scalar(src.Path, $"SELECT COUNT(*) FROM calendar_index WHERE CollectionId = {colId}");

        var newColId = tgt.Constructor.MoveCollectionIsolated(src.Path, colId, targetGroupId: null, batchSize: 10);

        var copy = Ranked(tgt.Path, newColId);
        Assert.Equal(53, copy.Count);
        var fields     = tgt.Constructor.GetFields(newColId).ToDictionary(f => f.Name, f => f.Id!.Value);
        var copyValues = tgt.Constructor.GetFieldValuesByCollection(newColId);
        var copyHeights = tgt.Constructor.GetRowHeights(newColId);
        for (var i = 0; i < copy.Count; i++)
        {
            Assert.Equal(srcRanked[i].Title, copy[i].Title);
            Assert.Equal(srcValues[srcRanked[i].Id][textId], copyValues[copy[i].Id][fields["Текст"]]);
            Assert.Equal(srcValues[srcRanked[i].Id][dateId], copyValues[copy[i].Id][fields["Дата"]]);
            Assert.Equal(heights.TryGetValue(srcRanked[i].Id, out var h) ? h : (int?)null,
                         copyHeights.TryGetValue(copy[i].Id, out var ch) ? ch : (int?)null);
        }
        Assert.Equal(srcCalendar, Scalar(tgt.Path, $"SELECT COUNT(*) FROM calendar_index WHERE CollectionId = {newColId}"));
        Assert.Equal("0", Scalar(src.Path, $"SELECT COUNT(*) FROM db_collections WHERE Id = {colId}"));
        Assert.Equal("0", Scalar(src.Path, $"SELECT COUNT(*) FROM db_meta WHERE Key LIKE '{ConstructorRepository.PendingPrefix}%'"));
        Assert.Equal("0", Scalar(tgt.Path, $"SELECT COUNT(*) FROM db_meta WHERE Key LIKE '{ConstructorRepository.PendingPrefix}%'"));
    }

    /// <summary>Посреди копии источник заморожен — запись в него ждет (иначе добавленное посреди
    /// копии стерло бы удаление источника), а приемник свободен: интерфейс и соседи по базе
    /// назначения пишут между пакетами. Прежде вся копия шла одной транзакцией приемника и держала
    /// его до конца.</summary>
    [Fact]
    public void Move_freezes_the_source_but_not_the_target()
    {
        using var src = new TempDatabase(dbId: 1, name: "src");
        using var tgt = new TempDatabase(dbId: 2, name: "tgt");
        var (colId, _, _) = Seed(src.Constructor, records: 40);

        var probes = new List<(bool Source, bool Target)>();
        tgt.Constructor.MoveCollectionIsolated(src.Path, colId, targetGroupId: null,
            new SyncProgress(done =>
            {
                if (done == 0 || done >= 40) return;
                probes.Add((TryWrite(src.Path), TryWrite(tgt.Path)));
            }), batchSize: 10);

        Assert.NotEmpty(probes);
        Assert.All(probes, p => Assert.False(p.Source, "источник открыт для записи посреди копии"));
        Assert.All(probes, p => Assert.True(p.Target, "приемник заперт посреди копии"));
    }

    /// <summary>Исходник не удалить (сбой на первом же пакете удаления): перенос откатывается —
    /// копии в базе назначения нет, источник цел, маркеров не остается.</summary>
    [Fact]
    public void Move_is_rolled_back_when_nothing_of_the_source_was_deleted()
    {
        using var src = new TempDatabase(dbId: 1, name: "src");
        using var tgt = new TempDatabase(dbId: 2, name: "tgt");
        var (colId, _, _) = Seed(src.Constructor, records: 20);
        var targetBefore = tgt.Constructor.GetAllCollections().Count;
        using (var c = Open(src.Path))
            Exec(c, "CREATE TRIGGER test_boom BEFORE DELETE ON db_records BEGIN SELECT RAISE(ABORT, 'test boom'); END;");

        Assert.ThrowsAny<Exception>(() => tgt.Constructor.MoveCollectionIsolated(src.Path, colId, targetGroupId: null, batchSize: 5));

        Assert.Equal(targetBefore, tgt.Constructor.GetAllCollections().Count);
        Assert.Equal("20", Scalar(src.Path, $"SELECT COUNT(*) FROM db_records WHERE CollectionId = {colId}"));
        Assert.Equal("0", Scalar(src.Path, $"SELECT COUNT(*) FROM db_meta WHERE Key LIKE '{ConstructorRepository.PendingPrefix}%'"));
        Assert.Equal("0", Scalar(tgt.Path, $"SELECT COUNT(*) FROM db_meta WHERE Key LIKE '{ConstructorRepository.PendingPrefix}%'"));
    }

    /// <summary>Исходник удален частично (сбой на втором пакете удаления): полная копия остается —
    /// удалить ее значило бы потерять записи, уже удаленные из источника. Ошибка говорит, что
    /// остаток удалится; монтирование источника его доудаляет.</summary>
    [Fact]
    public void Partly_deleted_source_keeps_the_copy_and_is_finished_on_mount()
    {
        using var src = new TempDatabase(dbId: 1, name: "src");
        using var tgt = new TempDatabase(dbId: 2, name: "tgt");
        var (colId, _, _) = Seed(src.Constructor, records: 20);
        using (var c = Open(src.Path))
            Exec(c, $@"CREATE TRIGGER test_boom BEFORE DELETE ON db_records
                       WHEN (SELECT COUNT(*) FROM db_records WHERE CollectionId = {colId}) <= 15
                       BEGIN SELECT RAISE(ABORT, 'test boom'); END;");

        Assert.Throws<ConstructorRepository.MoveIncompleteException>(() =>
            tgt.Constructor.MoveCollectionIsolated(src.Path, colId, targetGroupId: null, batchSize: 5));

        var copy = tgt.Constructor.GetAllCollections().Single(c => c.Name == "Коллекция");
        Assert.Equal(20, tgt.Constructor.CountRecords(copy.Id!.Value));
        Assert.Equal("15", Scalar(src.Path, $"SELECT COUNT(*) FROM db_records WHERE CollectionId = {colId}"));
        Assert.Equal("1", Scalar(src.Path,
            $"SELECT COUNT(*) FROM db_meta WHERE Key LIKE '{ConstructorRepository.PendingPrefix}{ConstructorRepository.PendingKind.MovedCollection}.%'"));

        using (var c = Open(src.Path)) Exec(c, "DROP TRIGGER test_boom;");
        using (DatabaseManager.Open(1, "src-again", src.Path)) { }

        Assert.Equal("0", Scalar(src.Path, $"SELECT COUNT(*) FROM db_collections WHERE Id = {colId}"));
        Assert.Equal("0", Scalar(src.Path, $"SELECT COUNT(*) FROM db_meta WHERE Key LIKE '{ConstructorRepository.PendingPrefix}%'"));
        Assert.Equal(20, tgt.Constructor.CountRecords(copy.Id!.Value));
    }

    /// <summary>Перенос ветки группы пакетами: подгруппы и коллекции на месте, у каждой коллекции
    /// все записи и значения, имя корня уникально на целевом уровне, источник пуст.</summary>
    [Fact]
    public void Batched_group_move_copies_the_whole_branch()
    {
        using var src = new TempDatabase(dbId: 1, name: "src");
        using var tgt = new TempDatabase(dbId: 2, name: "tgt");
        var root = src.Constructor.AddGroup(new DbGroup { Name = "Ветка" }).Id!.Value;
        var sub  = src.Constructor.AddGroup(new DbGroup { Name = "Подгруппа", ParentId = root }).Id!.Value;
        var a = SampleData.AddCollection(src.Constructor, "А", groupId: root).Id!.Value;
        var b = SampleData.AddCollection(src.Constructor, "Б", groupId: sub).Id!.Value;
        var fa = SampleData.AddField(src.Constructor, a, "Текст");
        foreach (var r in SampleData.AddRecords(src.Constructor, a, count: 17))
            src.Constructor.SetFieldValue(r.Id!.Value, fa.Id!.Value, $"а{r.Title}");
        SampleData.AddRecords(src.Constructor, b, count: 9);
        tgt.Constructor.AddGroup(new DbGroup { Name = "Ветка" });

        var newRoot = tgt.Constructor.MoveGroupIsolated(src.Path, root, targetParentId: null, batchSize: 4);

        var groups = tgt.Constructor.GetAllGroups();
        Assert.NotEqual("Ветка", groups.Single(g => g.Id == newRoot).Name);
        var newSub = groups.Single(g => g.ParentId == newRoot).Id!.Value;
        var cols   = tgt.Constructor.GetAllCollections();
        var newA   = cols.Single(c => c.GroupId == newRoot && c.Name == "А").Id!.Value;
        var newB   = cols.Single(c => c.GroupId == newSub && c.Name == "Б").Id!.Value;
        Assert.Equal(17, tgt.Constructor.CountRecords(newA));
        Assert.Equal(9, tgt.Constructor.CountRecords(newB));
        var field  = tgt.Constructor.GetFields(newA).Single(f => f.Name == "Текст").Id!.Value;
        var values = tgt.Constructor.GetFieldValuesByCollection(newA);
        foreach (var r in tgt.Constructor.GetRecordsPage(newA, 0, 100))
            Assert.Equal($"а{r.Title}", values[r.Id!.Value][field]);
        Assert.Equal("0", Scalar(src.Path, $"SELECT COUNT(*) FROM db_groups WHERE Id IN ({root}, {sub})"));
        Assert.Equal("0", Scalar(tgt.Path, $"SELECT COUNT(*) FROM db_meta WHERE Key LIKE '{ConstructorRepository.PendingPrefix}%'"));
    }

    // ── Стенд ─────────────────────────────────────────────────────────────────

    // Коллекция с текстом и датой (дата — источник календаря). Позиции идут против порядка Id.
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

    // Настоящая запись с коротким ожиданием: занята база — «database is locked».
    private static bool TryWrite(string path)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, DefaultTimeout = 1 }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO db_meta(Key, Value) VALUES ('test.probe', '1'); DELETE FROM db_meta WHERE Key = 'test.probe';";
        try { cmd.ExecuteNonQuery(); return true; }
        catch (SqliteException) { return false; }
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
