using Microsoft.EntityFrameworkCore;
using SmartBase.Database;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Перенос коллекции между базами (SQLite→SQLite через ATTACH) —
/// docs/architecture/multi-database.md: копия затем удаление источника, маппинг полей
/// по Position, уникализация имени в целевой базе.
/// </summary>
public sealed class CrossMoveTests
{
    private static int SeedSource(TempDatabase src, string name = "Данные")
    {
        var repo = src.Constructor;
        var col = SampleData.AddCollection(repo, name);
        var a = SampleData.AddField(repo, col.Id!.Value, "A", "text", position: 10);
        var b = SampleData.AddField(repo, col.Id!.Value, "B", "text", position: 20);
        var r1 = SampleData.AddRecord(repo, col.Id!.Value, "one", 10);
        var r2 = SampleData.AddRecord(repo, col.Id!.Value, "two", 20);
        repo.SetFieldValue(r1.Id!.Value, a.Id!.Value, "a1");
        repo.SetFieldValue(r1.Id!.Value, b.Id!.Value, "b1");
        repo.SetFieldValue(r2.Id!.Value, a.Id!.Value, "a2");
        repo.SetFieldValue(r2.Id!.Value, b.Id!.Value, "b2");
        return col.Id!.Value;
    }

    [Fact]
    public void Move_copies_to_target_with_values_and_clears_source()
    {
        using var src = new TempDatabase(dbId: 1, name: "src");
        using var tgt = new TempDatabase(dbId: 2, name: "tgt");
        var srcColId = SeedSource(src);

        var newColId = tgt.Constructor.MoveCollectionIsolated(src.Path, srcColId, targetGroupId: null);
        Assert.NotEqual(0, newColId);

        // Цель: две записи, поля в том же порядке (Position), значения на месте.
        Assert.Equal(2, tgt.Constructor.CountRecords(newColId));
        var fields = tgt.Constructor.GetFields(newColId).OrderBy(f => f.Position).ToList();
        Assert.Equal(["A", "B"], fields.Select(f => f.Name).ToArray());

        var recs   = tgt.Constructor.GetRecordsPage(newColId, 0, 100);
        var values = tgt.Constructor.GetFieldValuesBatch(recs.Select(r => r.Id!.Value).ToList());
        int fieldA = fields[0].Id!.Value, fieldB = fields[1].Id!.Value;
        var byTitle = recs.ToDictionary(r => r.Title, r => values[r.Id!.Value]);

        Assert.Equal("a1", byTitle["one"][fieldA]);
        Assert.Equal("b1", byTitle["one"][fieldB]);
        Assert.Equal("a2", byTitle["two"][fieldA]);
        Assert.Equal("b2", byTitle["two"][fieldB]);

        // Перенос сохраняет аудит-метаданные записи (созд./изм. дата/пользователь), а не обнуляет их.
        Assert.All(recs, r => Assert.False(string.IsNullOrEmpty(r.CreatedAt)));
        Assert.All(recs, r => Assert.Equal(CurrentUser.DisplayName, r.CreatedBy));

        // Источник: коллекция удалена (свежий контекст — без кеша трекера).
        using var srcCheck = new AppDbContext(src.Path);
        Assert.DoesNotContain(srcCheck.DbCollections.AsNoTracking(), c => c.Id == srcColId);
    }

    [Fact]
    public void Move_uniquifies_name_when_target_has_same_name()
    {
        using var src = new TempDatabase(dbId: 1, name: "src");
        using var tgt = new TempDatabase(dbId: 2, name: "tgt");
        var srcColId = SeedSource(src, "Данные");
        SampleData.AddCollection(tgt.Constructor, "Данные"); // одноимённая уже в цели

        var newColId = tgt.Constructor.MoveCollectionIsolated(src.Path, srcColId, targetGroupId: null);

        var all = tgt.Constructor.GetAllCollections();
        Assert.Equal(2, all.Count);
        var moved = all.First(c => c.Id == newColId);
        Assert.NotEqual("Данные", moved.Name);      // получила суффикс-уникализацию
        Assert.StartsWith("Данные", moved.Name);
    }
}
