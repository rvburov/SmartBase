using SmartBase.Core.Models;
using SmartBase.Extensions.PdfWatermark;

namespace SmartBase.Tests.Extensions.PdfWatermark;

/// <summary>
/// Проверка структуры коллекции-журнала. Расширение пишет в ЧУЖУЮ коллекцию, поэтому отказ
/// обязан быть точным: сказать, какой именно столбец не на месте, а не «не подходит».
/// </summary>
public sealed class JournalSchemaTests
{
    private static readonly (string Name, string Type)[] Reference = IssueStand.Fields;

    [Fact]
    public void Reference_collection_passes_and_gives_three_field_ids()
    {
        using var db = new TempDatabase("wm-schema");
        var id = IssueStand.Create(db.Constructor);

        var check = JournalSchema.Check(db.Constructor, id, IssueStand.DefaultName);

        Assert.True(check.Ok, check.Problem);
        Assert.NotEqual(0, check.Shape!.CopyFieldId);
        Assert.NotEqual(0, check.Shape.HolderFieldId);
        Assert.NotEqual(0, check.Shape.DateFieldId);
        Assert.Equal(id, check.Shape.CollectionId);
    }

    // --- Число столбцов -------------------------------------------------------------------

    [Fact]
    public void Missing_column_is_named_by_count()
    {
        using var db = new TempDatabase("wm-schema");
        var id = IssueStand.Create(db.Constructor, IssueStand.DefaultName, Reference[..2]);

        var check = JournalSchema.Check(db.Constructor, id, IssueStand.DefaultName);

        Assert.False(check.Ok);
        Assert.Equal("столбцов 3, а должно быть 4: Документ, Экземпляр, Кому выдан, Дата выдачи",
                     check.Problem);
    }

    [Fact]
    public void Extra_column_is_rejected_too()
    {
        using var db = new TempDatabase("wm-schema");
        var id = IssueStand.Create(db.Constructor, IssueStand.DefaultName,
                                   [.. Reference, ("Примечание", "text")]);

        var check = JournalSchema.Check(db.Constructor, id, IssueStand.DefaultName);

        Assert.False(check.Ok);
        Assert.StartsWith("столбцов 5", check.Problem);
    }

    // --- Имена и порядок -------------------------------------------------------------------

    [Fact]
    public void Renamed_column_says_both_the_fact_and_the_reference()
    {
        using var db = new TempDatabase("wm-schema");
        var id = IssueStand.Create(db.Constructor, IssueStand.DefaultName,
                                   [("Копия", "number"), Reference[1], Reference[2]]);

        var check = JournalSchema.Check(db.Constructor, id, IssueStand.DefaultName);

        Assert.False(check.Ok);
        Assert.Equal("2-й столбец — «Копия», а должен быть «Экземпляр»", check.Problem);
    }

    [Fact]
    public void Swapped_columns_are_rejected_even_though_the_names_are_all_there()
    {
        // Именно ради этого случая проверяется порядок, а не только состав: номер экземпляра
        // ушел бы в графу «кому выдан».
        using var db = new TempDatabase("wm-schema");
        var id = IssueStand.Create(db.Constructor, IssueStand.DefaultName,
                                   [Reference[1], Reference[0], Reference[2]]);

        var check = JournalSchema.Check(db.Constructor, id, IssueStand.DefaultName);

        Assert.False(check.Ok);
        Assert.Equal("2-й столбец — «Кому выдан», а должен быть «Экземпляр»", check.Problem);
    }

    [Theory]
    [InlineData("  экземпляр  ")]
    [InlineData("ЭКЗЕМПЛЯР")]
    public void Case_and_spaces_are_forgiven(string written)
    {
        using var db = new TempDatabase("wm-schema");
        var id = IssueStand.Create(db.Constructor, IssueStand.DefaultName,
                                   [(written, "number"), Reference[1], Reference[2]]);

        Assert.True(JournalSchema.Check(db.Constructor, id, IssueStand.DefaultName).Ok);
    }

    [Fact]
    public void Both_spellings_of_the_letter_e_mean_the_same_column()
    {
        // Журнал заводит человек, и «Дата учёта»-подобные написания встречаются сплошь. Такая
        // коллекция обязана подходить без переименования столбца.
        Assert.True(JournalSchema.SameName("Отметка учёта", "Отметка учета"));
        Assert.Equal(JournalSchema.Fold("Ёж"), JournalSchema.Fold("еж"));
    }

    // --- Системная колонка ------------------------------------------------------------------

    [Fact]
    public void Document_must_be_the_record_title_not_a_field()
    {
        using var db = new TempDatabase("wm-schema");

        // Коллекция, где все четыре имени на своих местах, но «Документ» — обычное поле, а
        // системную колонку переименовали в «Кому выдан» и увели на третье место. По одним
        // именам такая прошла бы, а имя документа ушло бы в поле, где его никто не ищет.
        var collection = db.Constructor.AddCollection(new DbCollection { Name = "Подделка", Position = 1000 });
        var id = collection.Id!.Value;

        db.Constructor.SetSystemFieldName(id, JournalSchema.TitleKey, JournalSchema.HolderColumn, "Название");

        var keys     = new List<string>();
        var position = 1000;
        (string Name, string Type)[] asFields =
            [(JournalSchema.TitleColumn, "text"), (JournalSchema.CopyColumn, "number"),
             (JournalSchema.DateColumn, "date")];

        foreach (var (name, type) in asFields)
        {
            var field = db.Constructor.AddField(new DbField
            {
                CollectionId = id, Name = name, FieldType = type, Position = position,
            });
            keys.Add(field.Id!.Value.ToString());
            position += 1000;
        }
        keys.Insert(2, JournalSchema.TitleKey);
        db.Constructor.SaveColumnOrder(id, keys);

        var check = JournalSchema.Check(db.Constructor, id, "Подделка");

        Assert.False(check.Ok);
        Assert.Equal("«Документ» должен быть системным столбцом записи, а не обычным полем",
                     check.Problem);
    }

    // --- Типы столбцов -----------------------------------------------------------------------

    [Theory]
    [InlineData(0, "text",   "Экземпляр",   "Число")]
    [InlineData(1, "number", "Кому выдан",  "Текст")]
    [InlineData(2, "text",   "Дата выдачи", "Дата")]
    public void Wrong_type_is_named_in_russian(int index, string wrongType, string column, string wantedType)
    {
        using var db = new TempDatabase("wm-schema");

        var fields = Reference.ToArray();
        fields[index] = (fields[index].Name, wrongType);
        var id = IssueStand.Create(db.Constructor, IssueStand.DefaultName, fields);

        var check = JournalSchema.Check(db.Constructor, id, IssueStand.DefaultName);

        Assert.False(check.Ok);
        Assert.Equal($"столбец «{column}» должен быть типа «{wantedType}», а он " +
                     $"«{(wrongType == "text" ? "Текст" : "Число")}»", check.Problem);
    }

    // --- Тумблер календаря --------------------------------------------------------------------

    [Fact]
    public void Date_column_shown_in_calendar_is_rejected()
    {
        // Ключевая проверка: тумблер календаря у столбца даты мог быть включен осознанно —
        // пропусти расширение такую коллекцию, и каждая выдача станет событием календаря.
        using var db = new TempDatabase("wm-schema");
        var id = IssueStand.Create(db.Constructor, IssueStand.DefaultName, Reference, dateInCalendar: true);

        var check = JournalSchema.Check(db.Constructor, id, IssueStand.DefaultName);

        Assert.False(check.Ok);
        Assert.Equal("у столбца «Дата выдачи» включен показ в календаре — " +
                     "выключите тумблер в свойствах столбца", check.Problem);
    }

    // --- Откуда берется порядок -----------------------------------------------------------------

    [Fact]
    public void Order_comes_from_saved_column_order_not_from_field_positions()
    {
        using var db = new TempDatabase("wm-schema");
        var id = IssueStand.Create(db.Constructor);
        var fields = db.Constructor.GetFields(id);

        // Позиции полей не трогаем — двигаем только сохраненный порядок колонок, как это делает
        // человек, перетаскивая столбцы в таблице.
        db.Constructor.SaveColumnOrder(id,
        [
            JournalSchema.TitleKey,
            fields[1].Id!.Value.ToString(),
            fields[0].Id!.Value.ToString(),
            fields[2].Id!.Value.ToString(),
        ]);

        var check = JournalSchema.Check(db.Constructor, id, IssueStand.DefaultName);

        Assert.False(check.Ok);
        Assert.Equal("2-й столбец — «Кому выдан», а должен быть «Экземпляр»", check.Problem);
    }
}
