using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Полный жизненный цикл записей (единица информации) сверх базового CRUD: дубль одной и
/// пакетом (со значениями), перемещение вверх/вниз и явный порядок, пакетное удаление с
/// каскадом значений. SQLite; PG-путь дубля (отдельный код) — в PgProviderTests.
/// </summary>
public sealed class RecordLifecycleTests
{
    // Коллекция с одним текстовым полем и <records> записями: значение i-й записи = "vi".
    private static (ConstructorRepository Repo, int ColId, int FieldId) Seed(TempDatabase db, int records)
    {
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var field = SampleData.AddField(repo, col.Id!.Value, "A", "text");
        for (var i = 1; i <= records; i++)
        {
            var r = SampleData.AddRecord(repo, col.Id!.Value, $"R{i}", i * 1000);
            repo.SetFieldValue(r.Id!.Value, field.Id!.Value, $"v{i}");
        }
        return (repo, col.Id!.Value, field.Id!.Value);
    }

    private static List<int> OrderIds(ConstructorRepository repo, int colId) =>
        repo.GetRecordsPage(colId, 0, 100).Select(r => r.Id!.Value).ToList();

    [Fact]
    public void DuplicateRecord_places_copy_after_source_with_values()
    {
        using var db = new TempDatabase();
        var (repo, colId, fieldId) = Seed(db, 2);
        var srcId = OrderIds(repo, colId)[0];   // R1

        var copy = repo.DuplicateRecord(srcId);

        Assert.Equal(3, repo.CountRecords(colId));
        // Копия встает сразу после исходной: R1, копия, R2.
        var order = OrderIds(repo, colId);
        Assert.Equal(copy.Id!.Value, order[order.IndexOf(srcId) + 1]);
        // Значение исходной записи скопировано.
        Assert.Equal("v1", repo.GetFieldValuesBatch([copy.Id!.Value])[copy.Id!.Value][fieldId]);
    }

    [Fact]
    public void DuplicateRecordsData_copies_batch_with_values()
    {
        using var db = new TempDatabase();
        var (repo, colId, fieldId) = Seed(db, 3);
        var page = OrderIds(repo, colId);
        var ids = new[] { page[0], page[1] };   // R1, R2

        var newIds = repo.DuplicateRecordsData(colId, ids);

        Assert.Equal(2, newIds.Count);
        Assert.Equal(5, repo.CountRecords(colId));
        var vals = repo.GetFieldValuesBatch(newIds);
        var copied = newIds.Select(id => vals[id][fieldId]).OrderBy(v => v).ToList();
        Assert.Equal(["v1", "v2"], copied);
    }

    // Календарь дублирует теми же методами, что и таблица, но ему нужна ПАРА «источник → копия»:
    // из копий строятся события (дельта Журнала, якорь, перенос выделения). Сшивка держится на
    // контракте «id копий приходят в порядке ранга источников (Position, Id)» — проверяем, что
    // пара действительно указывает на свой источник, а не просто совпала по длине.
    [Fact]
    public void DuplicateRecordsMapped_pairs_each_source_with_its_copy()
    {
        using var db = new TempDatabase();
        var (repo, colId, fieldId) = Seed(db, 4);
        var page = OrderIds(repo, colId);
        // Порядок В ЗАПРОСЕ намеренно перемешан и с пропуском: сшивка обязана идти по рангу
        // (Position, Id), а не по порядку переданного списка.
        var ids = new[] { page[3], page[0], page[2] };   // R4, R1, R3

        var map = repo.DuplicateRecordsMappedIsolated(colId, ids);

        Assert.Equal(3, map.Count);
        Assert.Equal([page[0], page[2], page[3]], map.Select(p => p.SrcId).ToList());   // по рангу
        var vals = repo.GetFieldValuesBatch(map.Select(p => p.NewId).ToList());
        foreach (var (srcId, newId) in map)
        {
            var expected = "v" + (page.IndexOf(srcId) + 1);
            Assert.Equal(expected, vals[newId][fieldId]);   // копия несет значение СВОЕГО источника
        }
    }

    [Fact]
    public void MoveRecord_swaps_with_neighbor()
    {
        using var db = new TempDatabase();
        var (repo, colId, _) = Seed(db, 3);
        var page = OrderIds(repo, colId);   // R1, R2, R3

        repo.MoveRecord(colId, page[1], up: true);   // R2 вверх → R2, R1, R3

        Assert.Equal([page[1], page[0], page[2]], OrderIds(repo, colId));
    }

    [Fact]
    public void ReorderRecords_sets_explicit_order()
    {
        using var db = new TempDatabase();
        var (repo, colId, _) = Seed(db, 3);
        var reversed = OrderIds(repo, colId).AsEnumerable().Reverse().ToList();

        repo.ReorderRecords(reversed);

        Assert.Equal(reversed, OrderIds(repo, colId));
    }

    [Fact]
    public void DeleteRecordsData_removes_batch_and_cascades_values()
    {
        using var db = new TempDatabase();
        var (repo, colId, fieldId) = Seed(db, 3);
        var page = OrderIds(repo, colId);
        var del = new[] { page[0], page[1] };
        var kept = page[2];

        repo.DeleteRecordsData(del);

        Assert.Equal(1, repo.CountRecords(colId));
        Assert.Empty(repo.GetFieldValuesBatch(del));                                    // значения ушли каскадом
        Assert.Equal("v3", repo.GetFieldValuesBatch([kept])[kept][fieldId]);           // оставшаяся — цела
    }
}
