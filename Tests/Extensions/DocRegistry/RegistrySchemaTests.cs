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
        RegistrySchema.ProjectColumn, RegistrySchema.AccountingColumn,
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
        Assert.Equal(RegistrySchema.ProjectColumn,     byId[shape.ProjectFieldId]);
        Assert.Equal(RegistrySchema.AccountingColumn,  byId[shape.AccountingFieldId]);
        Assert.Equal(RegistrySchema.FirstUseColumn,    byId[shape.FirstUseFieldId]);
        Assert.Equal(RegistrySchema.SummaryColumn,     byId[shape.SummaryFieldId]);
        Assert.Equal("date",                           shape.DateFieldType);
    }

    // ── Порядок и свои столбцы ────────────────────────────────────────────────

    /// <summary>Столбцы ищутся по именам: переставленные «Обозначение» и «ФИО регистрирующего»
    /// получают каждый свое значение, а не соседнее.</summary>
    [Fact]
    public void Swapped_columns_are_found_by_name()
    {
        using var db = new TempDatabase();

        string[] swapped = [Reference[1], Reference[0], .. Reference[2..]];
        var id = JournalStand.Create(db.Constructor, "Перепутанный", swapped);

        var shape = JournalStand.Shape(db.Constructor, id, "Перепутанный");
        var byId  = db.Constructor.GetFields(id).ToDictionary(f => f.Id!.Value, f => f.Name);

        Assert.Equal(RegistrySchema.DesignationColumn, byId[shape.DesignationFieldId]);
        Assert.Equal(RegistrySchema.FioColumn,         byId[shape.FioFieldId]);
    }

    /// <summary>
    /// Свои столбцы журнала — для письма и прочего — разрешены где угодно, в том числе ПЕРЕД
    /// «Наименованием» и между полями эталона, и раскладка их не задевает (решение пользователя
    /// 21.10.2026).
    /// </summary>
    [Fact]
    public void Extra_columns_are_allowed_anywhere()
    {
        using var db = new TempDatabase();

        string[] extra = ["Письмо", .. Reference[..3], "Дата письма", .. Reference[3..], "Ответ"];
        var id = JournalStand.Create(db.Constructor, "Длинный", extra);

        // Системную колонку записи уводим в середину ряда: порядок больше ничего не значит.
        var fields = db.Constructor.GetFields(id).ToDictionary(f => f.Name, f => f.Id!.Value.ToString());
        db.Constructor.SaveColumnOrder(id,
            [fields["Письмо"], fields[Reference[0]], RegistrySchema.TitleKey, .. Reference[1..].Select(n => fields[n]),
             fields["Дата письма"], fields["Ответ"]]);

        var shape = JournalStand.Shape(db.Constructor, id, "Длинный");
        var byId  = db.Constructor.GetFields(id).ToDictionary(f => f.Id!.Value, f => f.Name);

        Assert.Equal(RegistrySchema.DesignationColumn, byId[shape.DesignationFieldId]);
        Assert.Equal(RegistrySchema.FioColumn,         byId[shape.FioFieldId]);
        Assert.Equal(RegistrySchema.DateColumn,        byId[shape.DateFieldId]);
        Assert.Equal(RegistrySchema.ProjectColumn,     byId[shape.ProjectFieldId]);
        Assert.Equal(RegistrySchema.AccountingColumn,  byId[shape.AccountingFieldId]);
        Assert.Equal(RegistrySchema.FirstUseColumn,    byId[shape.FirstUseFieldId]);
        Assert.Equal(RegistrySchema.SummaryColumn,     byId[shape.SummaryFieldId]);
    }

    // ── Отказы ────────────────────────────────────────────────────────────────

    /// <summary>Переименованный столбец — это пропавший столбец эталона: отказ называет, какого
    /// не хватает, а «Шифр» становится просто своим столбцом.</summary>
    [Fact]
    public void Renamed_column_is_rejected()
    {
        using var db = new TempDatabase();

        string[] renamed = ["Шифр", .. Reference[1..]];
        var id = JournalStand.Create(db.Constructor, "Переименованный", renamed);

        var check = RegistrySchema.Check(db.Constructor, id, "Переименованный");

        Assert.False(check.Ok);
        Assert.Equal($"нет столбца «{RegistrySchema.DesignationColumn}»", check.Problem);
    }

    [Fact]
    public void Missing_column_is_rejected()
    {
        using var db = new TempDatabase();

        var id = JournalStand.Create(db.Constructor, "Короткий", Reference[..^1]);

        var check = RegistrySchema.Check(db.Constructor, id, "Короткий");

        Assert.False(check.Ok);
        Assert.Equal($"нет столбца «{RegistrySchema.SummaryColumn}»", check.Problem);
    }

    /// <summary>Два столбца с одним именем эталона — отказ: в какой из них писать, не понять.
    /// Одинаковых имен программа не дает, а вот «Проект» и «проект» — разные имена для нее и
    /// одно и то же для сравнения столбцов.</summary>
    [Fact]
    public void Duplicate_column_is_rejected()
    {
        using var db = new TempDatabase();

        string[] twice = [.. Reference, RegistrySchema.ProjectColumn.ToLowerInvariant()];
        var id = JournalStand.Create(db.Constructor, "Двойной", twice);

        var check = RegistrySchema.Check(db.Constructor, id, "Двойной");

        Assert.False(check.Ok);
        Assert.Contains($"«{RegistrySchema.ProjectColumn}» встречается несколько раз", check.Problem);
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
        Assert.StartsWith("нет столбцов:", check.Problem);
        Assert.All(RegistrySchema.Columns, name => Assert.Contains($"«{name}»", check.Problem));
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

    /// <summary>
    /// Эталон написан одним написанием буквы «е». Но в уже заведенных журналах столбец учета
    /// может быть назван вторым — такая коллекция обязана подходить, иначе людям пришлось бы
    /// переименовывать столбец из-за одной буквы.
    ///
    /// Проверка идет ИМЕННО в эту сторону (второе написание в коллекции, эталон без него).
    /// Обратная сторона теперь невозможна, и тест, написанный так, ничего бы не проверял.
    /// </summary>
    [Fact]
    public void Second_spelling_in_a_column_name_is_forgiven()
    {
        using var db = new TempDatabase();

        string[] renamed = [.. Reference.Select(n => n.Replace("учета", "учёта"))];
        Assert.NotEqual(Reference, renamed);   // стенд и правда отличается от эталона

        var id = JournalStand.Create(db.Constructor, "Второе написание", renamed);

        Assert.True(RegistrySchema.Check(db.Constructor, id, "Второе написание").Ok);
    }

    /// <summary>В эталонных именах столбцов второго написания буквы «е» быть не должно.</summary>
    [Fact]
    public void Reference_column_names_use_one_spelling() =>
        Assert.All(RegistrySchema.Columns,
                   name => Assert.DoesNotContain('ё', name.ToLowerInvariant()));

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
    [InlineData(" Проект ", "Проект", true)]
    [InlineData("Проект", "Проекты", false)]
    public void Name_comparison_rules(string actual, string expected, bool same) =>
        Assert.Equal(same, RegistrySchema.SameName(actual, expected));
}
