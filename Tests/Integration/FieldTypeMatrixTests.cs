using SmartBase.Core.Exporters.TableWriters;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Сводная матрица типов свойств: для КАЖДОГО базового типа — round-trip значения
/// (запись → чтение), человекочитаемая подпись; для отдельных типов — приведение при смене
/// типа (текст↔дата), дефолт user-поля, правила отображения checkbox/числа. Дополняет точечные
/// тесты (CRUD, календарь, экспорт) сводным покрытием реестра типов. Вычисляемые типы
/// (created_*/updated_*) — в ConstructorRepositoryCrudTests (метаданные).
/// </summary>
public sealed class FieldTypeMatrixTests
{
    // Пример значения каждого типа в том виде, как оно ложится в db_field_values
    // (всё — строки: числа сырым вводом, даты — ISO-канон, checkbox — «1»/«0»).
    public static IEnumerable<object[]> TypeValues() =>
    [
        ["text",     "произвольный текст"],
        ["number",   "42,5"],
        ["date",     "2026-02-01"],
        ["select",   "Вариант Б"],
        ["status",   "В работе"],
        ["multiselect", "[\"Дизайн\",\"Backend\"]"],
        ["checkbox", "1"],
        ["url",      "https://example.com"],
        ["user",     "Иван Иванов"],
    ];

    [Theory]
    [MemberData(nameof(TypeValues))]
    public void Value_round_trips_for_every_type(string fieldType, string value)
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var field = SampleData.AddField(repo, col.Id!.Value, "F", fieldType);
        var rec = SampleData.AddRecord(repo, col.Id!.Value, "R");

        repo.SetFieldValue(rec.Id!.Value, field.Id!.Value, value);

        var stored = repo.GetFieldValuesBatch([rec.Id!.Value])[rec.Id!.Value][field.Id!.Value];
        Assert.Equal(value, stored);   // значения всех типов хранятся строкой как есть
    }

    [Theory]
    [InlineData("text",     "Текст")]
    [InlineData("number",   "Число")]
    [InlineData("date",     "Дата")]
    [InlineData("select",   "Список")]
    [InlineData("status",   "Статус")]
    [InlineData("multiselect", "Мультисписок")]
    [InlineData("checkbox", "Чекбокс")]
    [InlineData("url",      "Ссылка")]
    [InlineData("user",     "Пользователь")]
    public void Label_is_defined_for_every_type(string fieldType, string label)
    {
        Assert.Equal(label, FieldTypeInfo.Label(fieldType));
    }

    [Theory]
    [InlineData("select",      true)]
    [InlineData("status",      true)]
    [InlineData("multiselect", true)]
    [InlineData("text",        false)]
    [InlineData("user",        false)]
    public void IsOptionList_covers_select_status_multiselect(string fieldType, bool expected) =>
        Assert.Equal(expected, FieldTypeInfo.IsOptionList(fieldType));

    [Fact]
    public void MultiSelectValue_roundtrips_array_and_migrates_legacy_single()
    {
        // Пусто → пустой список; массив → тот же набор в том же порядке.
        Assert.Empty(MultiSelectValue.Parse(""));
        Assert.Equal(["Дизайн", "Backend"], MultiSelectValue.Parse("[\"Дизайн\",\"Backend\"]"));

        // Сериализация отбрасывает пустые; показ — через запятую.
        Assert.Equal("[\"A\",\"B\"]", MultiSelectValue.Serialize(["A", "", "B"]));
        Assert.Equal("Дизайн, Backend", MultiSelectValue.Display("[\"Дизайн\",\"Backend\"]"));

        // Мягкая миграция из select/status: не-JSON строка = одно значение.
        Assert.Equal(["В работе"], MultiSelectValue.Parse("В работе"));
    }

    [Fact]
    public void Enabling_tasks_creates_status_typed_field_without_duplicates()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var exec = SampleData.AddField(repo, col.Id!.Value, "Исполнитель", "user");

        // Включение задач авто-создаёт «Назначил» (user) и «Статус» (новый тип status).
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);

        var status = repo.GetFields(col.Id!.Value).Single(f => f.IsTaskStatus);
        Assert.Equal("status", status.FieldType);
        // Опции хранятся в ConfigJson (кириллица JSON-эскейпится — сверяем после разбора).
        var opts = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(status.ConfigJson);
        Assert.Contains(ConstructorRepository.TaskStatusPlanned, opts!["options"]);

        // Повторное «обеспечение» коллекции задачами не плодит второй «Статус».
        repo.EnsureTaskCollection(col.Id!.Value);
        Assert.Single(repo.GetFields(col.Id!.Value).Where(f => f.IsTaskStatus));
    }

    [Fact]
    public void Changing_text_to_date_and_back_preserves_the_date()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var field = SampleData.AddField(repo, col.Id!.Value, "F", "text");
        var rec = SampleData.AddRecord(repo, col.Id!.Value, "R");
        repo.SetFieldValue(rec.Id!.Value, field.Id!.Value, "01.02.2026");

        // text → date: значение приводится к ISO-канону и читается как дата.
        field.FieldType = "date";
        repo.UpdateField(field);
        var asDate = repo.GetFieldValuesBatch([rec.Id!.Value])[rec.Id!.Value][field.Id!.Value];
        Assert.True(DateValue.TryParse(asDate, out var dv));
        Assert.Equal(new DateTime(2026, 2, 1), dv.Start.Date);

        // date → text: обратно в человекочитаемое дд.MM.гггг без потери данных.
        field.FieldType = "text";
        repo.UpdateField(field);
        var asText = repo.GetFieldValuesBatch([rec.Id!.Value])[rec.Id!.Value][field.Id!.Value];
        Assert.Equal("01.02.2026", asText);
    }

    [Fact]
    public void User_field_defaults_to_current_user_on_record_create()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var userField = SampleData.AddField(repo, col.Id!.Value, "Исполнитель", "user");

        // user-поле уже есть → новая запись получает текущего пользователя по умолчанию.
        var rec = SampleData.AddRecord(repo, col.Id!.Value, "R");

        var stored = repo.GetFieldValuesBatch([rec.Id!.Value])[rec.Id!.Value][userField.Id!.Value];
        Assert.Equal(CurrentUser.DisplayName, stored);
        Assert.Contains(CurrentUser.DisplayName, repo.GetUserOptions());   // допущенные пользователи базы
    }

    [Theory]
    [InlineData("1",    true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("0",    false)]
    [InlineData("",     false)]
    public void Checkbox_truthy_matches_table_display_rule(string raw, bool expected) =>
        Assert.Equal(expected, WriterHelpers.IsChecked(raw));

    [Theory]
    [InlineData("42,5", true)]    // запятая → точка (как ParseNum таблицы)
    [InlineData("42.5", true)]
    [InlineData("abc",  false)]   // неразобранное — остаётся строкой
    public void Number_parses_raw_input(string raw, bool ok) =>
        Assert.Equal(ok, WriterHelpers.TryNumber(raw, out _));
}
