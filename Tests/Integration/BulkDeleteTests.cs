using Microsoft.Data.Sqlite;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Удаление коллекции и ветки группы пакетами (<see cref="ConstructorRepository.DeleteCollectionData"/>,
/// <see cref="ConstructorRepository.DeleteGroupData"/>): записи пакета берутся первыми по индексу
/// коллекции, и при любой нарезке уходит ровно своя коллекция — ее записи, значения, строки
/// календаря, входящие связи и UI-строки, — а соседние коллекции не задеты.
/// </summary>
public sealed class BulkDeleteTests
{
    /// <summary>Пакет не кратен числу записей; у соседней коллекции записи с id по обе стороны
    /// удаляемых и связь на удаляемую запись: соседи целы, связь на удаленное снята.</summary>
    [Fact]
    public void Batched_delete_removes_only_its_collection()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var (victim, vText, vDate) = Seed(repo, "Удаляемая");
        var (other, oText, _)      = Seed(repo, "Соседняя");
        // Записи соседней коллекции и до, и после удаляемых по id.
        foreach (var i in Enumerable.Range(0, 23))
        {
            var r = SampleData.AddRecord(repo, i % 2 == 0 ? victim : other, $"X{i}", position: 5000 + i);
            repo.SetFieldValue(r.Id!.Value, i % 2 == 0 ? vText : oText, $"x{i}");
            if (i % 2 == 0) repo.SetFieldValue(r.Id!.Value, vDate, "2026-03-01");
        }
        var victimRecords = repo.CountRecords(victim);
        var otherRecords  = repo.CountRecords(other);
        var otherValues   = Scalar(db.Path, $"SELECT COUNT(*) FROM db_field_values fv JOIN db_records r ON r.Id = fv.RecordId WHERE r.CollectionId = {other}");
        var otherCalendar = Scalar(db.Path, $"SELECT COUNT(*) FROM calendar_index WHERE CollectionId = {other}");
        var rel = SampleData.AddField(repo, other, "Связь", "relation");
        rel.ConfigJson = new RelationConfig(victim, false).ToConfigJson();
        repo.UpdateField(rel);
        var from = repo.GetRecordsPage(other, 0, 1)[0].Id!.Value;
        repo.SetLinks(rel.Id!.Value, from, [repo.GetRecordsPage(victim, 0, 1)[0].Id!.Value]);
        repo.SaveRowHeights(victim, new Dictionary<int, int> { [repo.GetRecordsPage(victim, 0, 1)[0].Id!.Value] = 33 });

        var reported = new List<int>();
        repo.DeleteCollectionData(victim, new SyncProgress(reported.Add), batchSize: 4);

        Assert.DoesNotContain(repo.GetAllCollections(), c => c.Id == victim);
        Assert.Equal("0", Scalar(db.Path, $"SELECT COUNT(*) FROM db_records WHERE CollectionId = {victim}"));
        Assert.Equal("0", Scalar(db.Path, $"SELECT COUNT(*) FROM calendar_index WHERE CollectionId = {victim}"));
        Assert.Equal("0", Scalar(db.Path, $"SELECT COUNT(*) FROM db_field_values WHERE FieldId IN ({vText}, {vDate})"));
        Assert.Equal("0", Scalar(db.Path, $"SELECT COUNT(*) FROM ui_row_heights WHERE CollectionId = {victim}"));
        Assert.Equal("0", Scalar(db.Path, "SELECT COUNT(*) FROM db_record_links"));
        Assert.Equal(otherRecords, repo.CountRecords(other));
        Assert.Equal(otherValues, Scalar(db.Path, $"SELECT COUNT(*) FROM db_field_values fv JOIN db_records r ON r.Id = fv.RecordId WHERE r.CollectionId = {other} AND fv.FieldId <> {rel.Id}"));
        Assert.Equal(otherCalendar, Scalar(db.Path, $"SELECT COUNT(*) FROM calendar_index WHERE CollectionId = {other}"));
        // Прогресс нарастает по пакетам и кончается числом записей.
        Assert.Equal(reported.OrderBy(x => x), reported);
        Assert.Equal(victimRecords, reported[^1]);
    }

    /// <summary>Ветка группы пакетами: подгруппы и их коллекции уходят целиком, коллекция вне
    /// ветки цела.</summary>
    [Fact]
    public void Batched_group_delete_removes_the_whole_branch()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var root = repo.AddGroup(new DbGroup { Name = "Ветка" }).Id!.Value;
        var sub  = repo.AddGroup(new DbGroup { Name = "Подгруппа", ParentId = root }).Id!.Value;
        var a = SampleData.AddCollection(repo, "А", groupId: root).Id!.Value;
        var b = SampleData.AddCollection(repo, "Б", groupId: sub).Id!.Value;
        var keep = SampleData.AddCollection(repo, "Вне ветки").Id!.Value;
        SampleData.AddRecords(repo, a, count: 11);
        SampleData.AddRecords(repo, b, count: 6);
        SampleData.AddRecords(repo, keep, count: 7);

        repo.DeleteGroupData(root, batchSize: 3);

        Assert.DoesNotContain(repo.GetAllGroups(), g => g.Id == root || g.Id == sub);
        Assert.Equal([keep], repo.GetAllCollections().Where(c => c.Id == a || c.Id == b || c.Id == keep).Select(c => c.Id!.Value).ToArray());
        Assert.Equal(7, repo.CountRecords(keep));
        Assert.Equal("0", Scalar(db.Path, $"SELECT COUNT(*) FROM db_records WHERE CollectionId IN ({a}, {b})"));
    }

    // ── Стенд ─────────────────────────────────────────────────────────────────

    private static (int ColId, int TextId, int DateId) Seed(ConstructorRepository repo, string name)
    {
        var col  = SampleData.AddCollection(repo, name);
        var text = SampleData.AddField(repo, col.Id!.Value, "Текст", position: 1000);
        var date = SampleData.AddCalendarDateField(repo, col.Id!.Value, position: 2000);
        return (col.Id!.Value, text.Id!.Value, date.Id!.Value);
    }

    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private static string Scalar(string path, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }
}
