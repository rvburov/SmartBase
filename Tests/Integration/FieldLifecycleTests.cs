namespace SmartBase.Tests.Integration;

/// <summary>
/// Полный жизненный цикл свойств (полей) сверх создания/смены типа: дубль со значениями,
/// удаление с каскадом значений и чисткой column_meta, перемещение колонок (своп и «после»),
/// переименование системной колонки и сброс на дефолт. SQLite; ключевые PG-пути — в PgProviderTests.
/// </summary>
public sealed class FieldLifecycleTests
{
    // Ключ системной колонки «Название» (см. ConstructorRepository / TableRowVm.TitleKey).
    private const string TitleKey = "__title__";

    [Fact]
    public void DuplicateField_copies_values_to_new_field()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var field = SampleData.AddField(repo, col.Id!.Value, "Поле", "text");
        var r1 = SampleData.AddRecord(repo, col.Id!.Value, "R1", 1000);
        var r2 = SampleData.AddRecord(repo, col.Id!.Value, "R2", 2000);
        repo.SetFieldValue(r1.Id!.Value, field.Id!.Value, "a");
        repo.SetFieldValue(r2.Id!.Value, field.Id!.Value, "b");

        var copy = repo.DuplicateField(field.Id!.Value);

        Assert.NotEqual(field.Id!.Value, copy.Id!.Value);
        Assert.NotEqual(field.Name, copy.Name);                                    // авто-имя «(копия)»
        Assert.Equal(2, repo.GetFields(col.Id!.Value).Count(f => f.FieldType == "text"));
        // Значения исходного поля скопированы в новое во всех записях.
        var vals = repo.GetFieldValuesBatch([r1.Id!.Value, r2.Id!.Value]);
        Assert.Equal("a", vals[r1.Id!.Value][copy.Id!.Value]);
        Assert.Equal("b", vals[r2.Id!.Value][copy.Id!.Value]);
    }

    [Fact]
    public void DeleteField_removes_field_and_cascades_its_values()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var f1 = SampleData.AddField(repo, col.Id!.Value, "F1", "text", position: 1000);
        var f2 = SampleData.AddField(repo, col.Id!.Value, "F2", "text", position: 2000);
        var rec = SampleData.AddRecord(repo, col.Id!.Value, "R");
        repo.SetFieldValue(rec.Id!.Value, f1.Id!.Value, "x");
        repo.SetFieldValue(rec.Id!.Value, f2.Id!.Value, "y");

        repo.DeleteField(f1.Id!.Value);

        var fields = repo.GetFields(col.Id!.Value);
        Assert.DoesNotContain(fields, f => f.Id == f1.Id);
        Assert.Contains(fields, f => f.Id == f2.Id);
        // Значение удаленного поля ушло каскадом; поле f2 — на месте.
        var vals = repo.GetFieldValuesBatch([rec.Id!.Value])[rec.Id!.Value];
        Assert.False(vals.ContainsKey(f1.Id!.Value));
        Assert.Equal("y", vals[f2.Id!.Value]);
    }

    [Fact]
    public void MoveColumn_swaps_with_neighbor()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var keys = AddThreeFieldKeys(repo, col.Id!.Value);

        repo.MoveColumn(col.Id!.Value, keys[1], left: true, keys);   // F2 влево → F2, F1, F3

        Assert.Equal([keys[1], keys[0], keys[2]], OrderedKeys(repo, col.Id!.Value));
    }

    [Fact]
    public void MoveColumnAfter_places_key_after_target()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var keys = AddThreeFieldKeys(repo, col.Id!.Value);

        repo.MoveColumnAfter(col.Id!.Value, keys[0], keys[2], keys);   // F1 после F3 → F2, F3, F1

        Assert.Equal([keys[1], keys[2], keys[0]], OrderedKeys(repo, col.Id!.Value));
    }

    [Fact]
    public void SetSystemFieldName_renames_and_resets_to_default()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);

        repo.SetSystemFieldName(col.Id!.Value, TitleKey, "Задача", defaultName: "Название");
        Assert.Equal("Задача", repo.GetSystemFieldNames(col.Id!.Value)[TitleKey]);

        // Сброс на дефолт (значение == defaultName) снимает переопределение.
        repo.SetSystemFieldName(col.Id!.Value, TitleKey, "Название", defaultName: "Название");
        Assert.DoesNotContain(TitleKey, repo.GetSystemFieldNames(col.Id!.Value).Keys);
    }

    private static string[] AddThreeFieldKeys(SmartBase.Database.Repositories.ConstructorRepository repo, int colId)
    {
        var f1 = SampleData.AddField(repo, colId, "F1", "text", position: 1000);
        var f2 = SampleData.AddField(repo, colId, "F2", "text", position: 2000);
        var f3 = SampleData.AddField(repo, colId, "F3", "text", position: 3000);
        return [f1.Id!.Value.ToString(), f2.Id!.Value.ToString(), f3.Id!.Value.ToString()];
    }

    private static List<string> OrderedKeys(SmartBase.Database.Repositories.ConstructorRepository repo, int colId) =>
        repo.GetColumnOrder(colId).OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
}
