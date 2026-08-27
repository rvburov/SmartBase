using SmartBase.Core.Models;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Проверка структуры выбранной коллекции — единственное, что стоит между расширением и чужой
/// таблицей: расширение пишет в ГОТОВУЮ коллекцию, и промах разложил бы значения не по тем
/// столбцам. Поэтому отказ проверяется подробнее, чем согласие.
/// </summary>
public sealed class RegistrySchemaTests
{
    private static readonly string[] Reference =
    [
        RegistrySchema.DesignationColumn, RegistrySchema.FioColumn, RegistrySchema.DateColumn,
        RegistrySchema.TopicColumn, RegistrySchema.AccountingColumn,
        RegistrySchema.FirstUseColumn, RegistrySchema.SummaryColumn,
    ];

    [Fact]
    public void Reference_layout_is_accepted()
    {
        using var db = new TempDatabase();
        var id = JournalStand.Create(db.Constructor);

        var check = RegistrySchema.Check(db.Constructor, id, "Журнал регистрации");

        Assert.True(check.Ok, check.Problem);
        Assert.Null(check.Problem);
    }

    /// <summary>Раскладка отдает id именно тех полей, что стоят в коллекции, — иначе значения
    /// легли бы в соседние столбцы, и проверка структуры была бы бесполезной.</summary>
    [Fact]
    public void Shape_points_at_the_right_fields()
    {
        using var db = new TempDatabase();
        var id    = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, id);

        var byId = db.Constructor.GetFields(id).ToDictionary(f => f.Id!.Value, f => f.Name);

        Assert.Equal(RegistrySchema.DesignationColumn, byId[shape.DesignationFieldId]);
        Assert.Equal(RegistrySchema.FioColumn,         byId[shape.FioFieldId]);
        Assert.Equal(RegistrySchema.DateColumn,        byId[shape.DateFieldId]);
        Assert.Equal(RegistrySchema.TopicColumn,       byId[shape.TopicFieldId]);
        Assert.Equal(RegistrySchema.AccountingColumn,  byId[shape.AccountingFieldId]);
        Assert.Equal(RegistrySchema.FirstUseColumn,    byId[shape.FirstUseFieldId]);
        Assert.Equal(RegistrySchema.SummaryColumn,     byId[shape.SummaryFieldId]);
        Assert.Equal("date",                           shape.DateFieldType);
    }

    // ── Отказы ────────────────────────────────────────────────────────────────

    [Fact]
    public void Swapped_columns_are_rejected()
    {
        using var db = new TempDatabase();

        // Меняем местами «Обозначение» и «Фамилия, имя, отчество»: имена все на месте,
        // а порядок другой — значения ушли бы не туда.
        string[] swapped = [Reference[1], Reference[0], .. Reference[2..]];
        var id = JournalStand.Create(db.Constructor, "Перепутанный", swapped);

        var check = RegistrySchema.Check(db.Constructor, id, "Перепутанный");

        Assert.False(check.Ok);
        Assert.Contains(RegistrySchema.DesignationColumn, check.Problem);
    }

    [Fact]
    public void Renamed_column_is_rejected()
    {
        using var db = new TempDatabase();

        string[] renamed = ["Шифр", .. Reference[1..]];
        var id = JournalStand.Create(db.Constructor, "Переименованный", renamed);

        var check = RegistrySchema.Check(db.Constructor, id, "Переименованный");

        Assert.False(check.Ok);
        Assert.Contains("Шифр", check.Problem);
    }

    [Fact]
    public void Missing_column_is_rejected()
    {
        using var db = new TempDatabase();

        var id = JournalStand.Create(db.Constructor, "Короткий", Reference[..^1]);

        var check = RegistrySchema.Check(db.Constructor, id, "Короткий");

        Assert.False(check.Ok);
        Assert.Contains("столбцов", check.Problem);
    }

    [Fact]
    public void Extra_column_is_rejected()
    {
        using var db = new TempDatabase();

        string[] extra = [.. Reference, "Лишний"];
        var id = JournalStand.Create(db.Constructor, "Длинный", extra);

        var check = RegistrySchema.Check(db.Constructor, id, "Длинный");

        Assert.False(check.Ok);
        Assert.Contains("столбцов", check.Problem);
    }

    /// <summary>Обычная коллекция программы — самый частый промах: у нее и колонок другое число,
    /// и имена свои.</summary>
    [Fact]
    public void Ordinary_collection_is_rejected()
    {
        using var db = new TempDatabase();
        var collection = db.Constructor.AddCollection(new DbCollection { Name = "Просто", Position = 1000 });

        var check = RegistrySchema.Check(db.Constructor, collection.Id!.Value, "Просто");

        Assert.False(check.Ok);
        Assert.NotNull(check.Problem);
    }

    /// <summary>«Наименование» обязано быть системной колонкой записи: одноименное обычное поле
    /// прошло бы проверку по именам, но название записи в него не пишется.</summary>
    [Fact]
    public void Title_must_be_the_system_column()
    {
        using var db = new TempDatabase();

        // Системную колонку НЕ переименовываем — вместо этого заводим обычное поле
        // «Наименование» первым в ряду.
        string[] withFakeTitle = [RegistrySchema.TitleColumn, .. Reference];
        var id = JournalStand.Create(db.Constructor, "Подмена", withFakeTitle);

        var check = RegistrySchema.Check(db.Constructor, id, "Подмена");

        Assert.False(check.Ok);
    }

    // ── Мягкость сравнения имен ───────────────────────────────────────────────

    /// <summary>«Отметка учета» и «Отметка учёта» — одно и то же: «ё» набирают не все, и ронять
    /// из-за нее целую коллекцию было бы придиркой.</summary>
    [Fact]
    public void Yo_in_a_column_name_is_forgiven()
    {
        using var db = new TempDatabase();

        string[] withoutYo = [.. Reference.Select(n => n.Replace('ё', 'е'))];
        var id = JournalStand.Create(db.Constructor, "Без ё", withoutYo);

        Assert.True(RegistrySchema.Check(db.Constructor, id, "Без ё").Ok);
    }

    [Fact]
    public void Case_and_spaces_in_a_column_name_are_forgiven()
    {
        using var db = new TempDatabase();

        string[] sloppy = [$"  {Reference[0].ToUpperInvariant()} ", .. Reference[1..]];
        var id = JournalStand.Create(db.Constructor, "Неряшливый", sloppy);

        Assert.True(RegistrySchema.Check(db.Constructor, id, "Неряшливый").Ok);
    }

    [Theory]
    [InlineData("Обозначение", "обозначение", true)]
    [InlineData("Отметка учёта", "Отметка учета", true)]
    [InlineData(" Тема ", "Тема", true)]
    [InlineData("Тема", "Темы", false)]
    public void Name_comparison_rules(string actual, string expected, bool same) =>
        Assert.Equal(same, RegistrySchema.SameName(actual, expected));
}
