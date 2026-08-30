using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Extensions.PdfWatermark;

namespace SmartBase.Tests.Extensions.PdfWatermark;

/// <summary>
/// Стенд: коллекция-журнал выдачи с эталонной структурой — «Документ» переименованной системной
/// колонкой записи и тремя полями в порядке из <see cref="JournalSchema.Columns"/>.
///
/// Расширение такую коллекцию НЕ создает (ее заводит человек в конструкторе), поэтому стенд
/// собирает ее сам — иначе проверять было бы нечего. Заодно он служит образцом того, что должно
/// получиться у пользователя.
/// </summary>
internal static class IssueStand
{
    public const string DefaultName = "Журнал выдачи";

    /// <summary>Эталонные поля: имя и тип, в порядке слева направо.</summary>
    public static readonly (string Name, string Type)[] Fields =
    [
        (JournalSchema.CopyColumn,   "number"),
        (JournalSchema.HolderColumn, "text"),
        (JournalSchema.DateColumn,   "date"),
    ];

    /// <summary>Завести эталонный журнал и вернуть его id.</summary>
    public static int Create(ConstructorRepository repo, string name = DefaultName) =>
        Create(repo, name, Fields);

    /// <summary>
    /// То же, но с ЗАДАННЫМИ полями (в заданном порядке и с заданными типами) — для проверок
    /// отказа: переставленные, переименованные или не того типа столбцы расширение обязано
    /// отклонить.
    /// </summary>
    /// <param name="dateInCalendar">Включить тумблер календаря у первого поля-даты. Так
    /// собирается ровно тот случай, который расширение обязано отвергнуть, — и так же
    /// репозиторий поступает по умолчанию, если поле заводить без <c>autoCalendar: false</c>.</param>
    public static int Create(ConstructorRepository repo, string name,
                             IReadOnlyList<(string Name, string Type)> fields,
                             bool dateInCalendar = false)
    {
        var collection   = repo.AddCollection(new DbCollection { Name = name, Position = 1000 });
        var collectionId = collection.Id!.Value;

        repo.SetSystemFieldName(collectionId, JournalSchema.TitleKey, JournalSchema.TitleColumn, "Название");

        var keys     = new List<string> { JournalSchema.TitleKey };
        var position = 1000;

        foreach (var (fieldName, type) in fields)
        {
            // autoCalendar: false — иначе репозиторий сам включит тумблер первому полю-дате, и
            // эталонный стенд не прошел бы собственную проверку.
            var field = repo.AddField(new DbField
            {
                CollectionId = collectionId, Name = fieldName, FieldType = type, Position = position,
            }, autoCalendar: type == "date" && dateInCalendar);

            keys.Add(field.Id!.Value.ToString());
            position += 1000;
        }

        repo.SaveColumnOrder(collectionId, keys);
        return collectionId;
    }

    /// <summary>Раскладка полей журнала. Кидает, если стенд собран неправильно.</summary>
    public static JournalShape Shape(ConstructorRepository repo, int collectionId, string name = DefaultName)
    {
        var check = JournalSchema.Check(repo, collectionId, name);
        Assert.True(check.Ok, $"Стенд собран неверно: {check.Problem}");
        return check.Shape!;
    }

    /// <summary>Дописать в журнал готовую строку — так набирается уже занятая нумерация.</summary>
    public static int AddRow(ConstructorRepository repo, JournalShape shape, string document,
                             int? number, string holder = "Петров Петр Петрович", DateTime? date = null)
    {
        var record = repo.AddRecordAfter(new DbRecord
        {
            CollectionId = shape.CollectionId,
            Title        = document,
        }, afterRecordId: 0);

        var recordId = record.Id!.Value;

        if (number is { } n) repo.SetFieldValue(recordId, shape.CopyFieldId, n.ToString());
        if (holder.Length > 0) repo.SetFieldValue(recordId, shape.HolderFieldId, holder);
        if (date is { } d) repo.SetFieldValue(recordId, shape.DateFieldId, IssueJournal.StampFor(d));

        return recordId;
    }
}
