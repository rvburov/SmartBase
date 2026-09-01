using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Стенд: коллекция-журнал регистрации с эталонной структурой — «Наименование» переименованной
/// системной колонкой записи и семью полями в порядке из <see cref="RegistrySchema.Columns"/>.
///
/// Расширение такую коллекцию НЕ создает (она заводится человеком в конструкторе), поэтому
/// стенд собирает ее сам — иначе проверять было бы нечего. Заодно он же служит образцом того,
/// что должно получиться у пользователя.
/// </summary>
internal static class JournalStand
{
    // Типы колонок: даты — «дата», остальное — текст. Порядок повторяет эталон.
    private static readonly (string Name, string Type)[] Fields =
    [
        (RegistrySchema.DesignationColumn, "text"),
        (RegistrySchema.FioColumn,         "text"),
        (RegistrySchema.DateColumn,        "date"),
        (RegistrySchema.ProjectColumn,       "text"),
        (RegistrySchema.AccountingColumn,  "text"),
        (RegistrySchema.FirstUseColumn,    "text"),
        (RegistrySchema.SummaryColumn,     "text"),
    ];

    /// <summary>Завести журнал регистрации и вернуть его id.</summary>
    public static int Create(ConstructorRepository repo, string name = "Журнал регистрации") =>
        Create(repo, name, Fields.Select(f => f.Name).ToArray());

    /// <summary>
    /// То же, но с ЗАДАННЫМИ именами полей (в заданном порядке) — для проверок отказа:
    /// переставленные или переименованные столбцы расширение обязано отклонить.
    /// </summary>
    public static int Create(ConstructorRepository repo, string name, IReadOnlyList<string> fieldNames)
    {
        var collection   = repo.AddCollection(new DbCollection { Name = name, Position = 1000 });
        var collectionId = collection.Id!.Value;

        repo.SetSystemFieldName(collectionId, RegistrySchema.TitleKey, RegistrySchema.TitleColumn, "Название");

        var keys     = new List<string> { RegistrySchema.TitleKey };
        var position = 1000;

        foreach (var fieldName in fieldNames)
        {
            // Тип берем из эталона по ИМЕНИ, а для незнакомых имен (тесты отказа) — текст.
            var type  = Fields.FirstOrDefault(f => f.Name == fieldName).Type ?? "text";
            var field = repo.AddField(new DbField
            {
                CollectionId = collectionId, Name = fieldName, FieldType = type, Position = position,
            });

            keys.Add(field.Id!.Value.ToString());
            position += 1000;
        }

        repo.SaveColumnOrder(collectionId, keys);
        return collectionId;
    }

    /// <summary>Раскладка полей журнала. Кидает, если стенд собран неправильно.</summary>
    public static RegistryShape Shape(ConstructorRepository repo, int collectionId, string name = "Журнал регистрации")
    {
        var check = RegistrySchema.Check(repo, collectionId, name);
        Assert.True(check.Ok, $"Стенд собран неверно: {check.Problem}");
        return check.Shape!;
    }

    /// <summary>Дописать в журнал готовую строку — так набирается уже занятая нумерация.</summary>
    public static int AddRow(ConstructorRepository repo, RegistryShape shape, string title, string designation)
    {
        var record = repo.AddRecordAfter(new DbRecord
        {
            CollectionId = shape.CollectionId,
            Title        = title,
        }, afterRecordId: 0);

        var recordId = record.Id!.Value;
        if (designation.Length > 0) repo.SetFieldValue(recordId, shape.DesignationFieldId, designation);
        return recordId;
    }
}
