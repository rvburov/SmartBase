using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Билдеры данных поверх <see cref="ConstructorRepository"/> для интеграционных тестов.
/// Позиции задаются явно (репозиторий их уважает), чтобы порядок записей был
/// детерминированным — это важно для тестов keyset-пагинации.
/// </summary>
public static class SampleData
{
    public static DbCollection AddCollection(ConstructorRepository repo, string name = "Коллекция", int? groupId = null) =>
        repo.AddCollection(new DbCollection { Name = name, GroupId = groupId });

    public static DbField AddField(ConstructorRepository repo, int collectionId, string name,
                                   string type = "text", int position = 1000) =>
        repo.AddField(new DbField { CollectionId = collectionId, Name = name, FieldType = type, Position = position });

    /// <summary>
    /// Поле-дата, ВИДИМОЕ в календаре. Обычный <see cref="AddField"/> заводит дату выключенной
    /// (тумблер поднимает человек), поэтому тесты, которые читают события, отмечают поле явно —
    /// ровно как это делает пользователь в свойствах столбца.
    /// </summary>
    public static DbField AddCalendarDateField(ConstructorRepository repo, int collectionId,
                                               string name = "Дата", int position = 1000) =>
        repo.AddField(new DbField
        {
            CollectionId = collectionId, Name = name, FieldType = "date",
            Position     = position,     IsCalStart = true,
        });

    public static DbRecord AddRecord(ConstructorRepository repo, int collectionId, string title = "",
                                     int position = 1000) =>
        repo.AddRecord(new DbRecord { CollectionId = collectionId, Title = title, Position = position });

    /// <summary>Добавляет <paramref name="count"/> записей с шагом позиций
    /// (position = i·<paramref name="step"/>) — строгий возрастающий порядок (Position, Id).</summary>
    public static List<DbRecord> AddRecords(ConstructorRepository repo, int collectionId, int count, int step = 10)
    {
        var list = new List<DbRecord>(count);
        for (var i = 1; i <= count; i++)
            list.Add(AddRecord(repo, collectionId, $"R{i:D4}", i * step));
        return list;
    }
}
