using SmartBase.Core.Models;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Базовый CRUD конструктора БД на временной SQLite: группы, коллекции, поля, записи,
/// значения. Проверяет присвоение Id, проверки уникальности имён и каскадное удаление.
/// </summary>
public sealed class ConstructorRepositoryCrudTests
{
    [Fact]
    public void Group_add_query_and_delete()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var g = repo.AddGroup(new DbGroup { Name = "Проекты" });
        Assert.NotNull(g.Id);
        Assert.Contains(repo.GetAllGroups(), x => x.Id == g.Id && x.Name == "Проекты");

        Assert.True(repo.GroupNameExists("Проекты", null));
        Assert.False(repo.GroupNameExists("Проекты", null, excludeId: g.Id)); // сам себя не считает
        Assert.False(repo.GroupNameExists("Другое", null));

        repo.DeleteGroup(g.Id!.Value);
        Assert.DoesNotContain(repo.GetAllGroups(), x => x.Id == g.Id);
    }

    [Fact]
    public void Collection_add_query_and_name_uniqueness()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Задачи");
        Assert.NotNull(col.Id);
        Assert.Contains(repo.GetAllCollections(), c => c.Id == col.Id);
        Assert.Contains(repo.GetCollectionsByGroup(null), c => c.Id == col.Id);

        Assert.True(repo.CollectionNameExists("Задачи", null));
        Assert.False(repo.CollectionNameExists("Задачи", null, excludeId: col.Id));
    }

    [Fact]
    public void Field_add_get_and_duplicate_name_rejected()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);

        var f1 = SampleData.AddField(repo, col.Id!.Value, "Текст", "text", position: 10);
        var f2 = SampleData.AddField(repo, col.Id!.Value, "Число", "number", position: 20);

        var fields = repo.GetFields(col.Id!.Value);
        Assert.Contains(fields, f => f.Id == f1.Id && f.Name == "Текст");
        Assert.Contains(fields, f => f.Id == f2.Id);

        Assert.Throws<InvalidOperationException>(() =>
            SampleData.AddField(repo, col.Id!.Value, "Текст", "text"));
    }

    [Fact]
    public void Record_add_count_get_and_delete()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);

        var r = SampleData.AddRecord(repo, col.Id!.Value, "Первая", 100);
        Assert.NotNull(r.Id);
        Assert.Equal(1, repo.CountRecords(col.Id!.Value));

        var loaded = repo.GetRecord(r.Id!.Value);
        Assert.NotNull(loaded);
        Assert.Equal("Первая", loaded!.Title);

        repo.DeleteRecord(r.Id!.Value);
        Assert.Equal(0, repo.CountRecords(col.Id!.Value));
        Assert.Null(repo.GetRecord(r.Id!.Value));
    }

    [Fact]
    public void SetFieldValue_persists_and_is_read_back()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var field = SampleData.AddField(repo, col.Id!.Value, "Текст", "text");
        var rec = SampleData.AddRecord(repo, col.Id!.Value, "Запись");

        repo.SetFieldValue(rec.Id!.Value, field.Id!.Value, "привет");

        var values = repo.GetFieldValuesBatch([rec.Id!.Value]);
        Assert.Equal("привет", values[rec.Id!.Value][field.Id!.Value]);
    }

    [Fact]
    public void Deleting_collection_cascades_records_and_values()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var field = SampleData.AddField(repo, col.Id!.Value, "Текст");
        var rec = SampleData.AddRecord(repo, col.Id!.Value, "Запись");
        repo.SetFieldValue(rec.Id!.Value, field.Id!.Value, "значение");

        repo.DeleteCollection(col.Id!.Value);

        // FK ON DELETE CASCADE: ни записей, ни значений, ни полей не остаётся.
        Assert.DoesNotContain(db.Context.DbRecords, r => r.CollectionId == col.Id!.Value);
        Assert.DoesNotContain(db.Context.DbFieldValues, v => v.RecordId == rec.Id!.Value);
        Assert.DoesNotContain(db.Context.DbFields, f => f.CollectionId == col.Id!.Value);
    }
}
