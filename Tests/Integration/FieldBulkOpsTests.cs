using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Дубль и удаление столбца пакетами (<c>DuplicateFieldData</c>, <c>DeleteFieldData</c>): огромный
/// столбец коммитится порциями, чтобы журнал базы не держал страницы всей таблицы до конца
/// операции, — итог тот же, что у одной транзакции. Копия поля создается в транзакции своих значений.
/// </summary>
public sealed class FieldBulkOpsTests
{
    private static (int Col, int Field, List<int> Ids) Seed(ConstructorRepository repo, int count)
    {
        var col   = SampleData.AddCollection(repo, "Столбцы").Id!.Value;
        var field = SampleData.AddField(repo, col, "Код", position: 10).Id!.Value;
        var ids   = SampleData.AddRecords(repo, col, count).Select(r => r.Id!.Value).ToList();
        for (var i = 0; i < ids.Count; i++)
            if (i % 7 != 3) repo.SetFieldValue(ids[i], field, $"значение {i}");   // у части записей пусто
        return (col, field, ids);
    }

    private static Dictionary<int, string> ValuesOf(ConstructorRepository repo, List<int> ids, int field) =>
        repo.GetFieldValuesBatch(ids).Where(kv => kv.Value.ContainsKey(field))
            .ToDictionary(kv => kv.Key, kv => kv.Value[field]);

    [Theory]
    [InlineData(10, 20)]                 // порциями: коммит каждые 20 значений
    [InlineData(10000, 500_000)]         // одной транзакцией
    public void Duplicate_and_delete_give_same_result_by_chunks(int batchSize, int chunkValues)
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var (col, field, ids) = Seed(repo, 55);
        var source = ValuesOf(repo, ids, field);

        var copy = repo.DuplicateFieldData(field, batchSize: batchSize, chunkValues: chunkValues);
        Assert.Equal(source, ValuesOf(repo, ids, copy.Id!.Value));
        Assert.Contains(repo.GetFields(col), f => f.Id == copy.Id && f.Name == "Код (копия)");

        repo.DeleteFieldData(copy.Id!.Value, batchSize: batchSize, chunkValues: chunkValues);
        Assert.DoesNotContain(repo.GetFields(col), f => f.Id == copy.Id);
        Assert.Empty(ValuesOf(repo, ids, copy.Id!.Value));
        Assert.Equal(source, ValuesOf(repo, ids, field));   // исходник не тронут
    }
}
