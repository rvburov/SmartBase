using System.Text.Json;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Канон хранения десятичного разделителя поля «Число»: значение приводится к выбранному символу
/// на записи (<c>SetFieldValue</c>) и при смене настройки разделителя уже сохранённые значения
/// мигрируют (<c>UpdateField</c>). Формат показа (валюта/проценты/разряды) — чистый показ, здесь
/// не проверяется (см. NumberFormatValueTests). SQLite.
/// </summary>
public sealed class NumberFieldFormatTests
{
    private static string Config(DecimalMark mark) =>
        JsonSerializer.Serialize(NumberFormatValue.ToConfigObject(
            NumberFormat.Default with { DecimalSeparator = mark }));

    private static DbField AddNumberField(Database.Repositories.ConstructorRepository repo, int collectionId, DecimalMark mark) =>
        repo.AddField(new DbField
        {
            CollectionId = collectionId,
            Name         = "Число",
            FieldType    = "number",
            Position     = 1000,
            ConfigJson   = Config(mark),
        });

    [Fact]
    public void SetFieldValue_normalizes_dot_to_field_comma()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);
        var f    = AddNumberField(repo, col.Id!.Value, DecimalMark.Comma);
        var rec  = SampleData.AddRecord(repo, col.Id!.Value, "R");

        repo.SetFieldValue(rec.Id!.Value, f.Id!.Value, "42.5");   // ввод с точкой

        var stored = repo.GetFieldValuesBatch([rec.Id!.Value])[rec.Id!.Value][f.Id!.Value];
        Assert.Equal("42,5", stored);   // в БД — с запятой (канон поля)
    }

    [Fact]
    public void SetFieldValue_normalizes_comma_to_field_dot()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);
        var f    = AddNumberField(repo, col.Id!.Value, DecimalMark.Dot);
        var rec  = SampleData.AddRecord(repo, col.Id!.Value, "R");

        repo.SetFieldValue(rec.Id!.Value, f.Id!.Value, "42,5");   // ввод с запятой

        var stored = repo.GetFieldValuesBatch([rec.Id!.Value])[rec.Id!.Value][f.Id!.Value];
        Assert.Equal("42.5", stored);
    }

    [Fact]
    public void ChangingSeparator_migrates_existing_values()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);
        var f    = AddNumberField(repo, col.Id!.Value, DecimalMark.Dot);
        var r1   = SampleData.AddRecord(repo, col.Id!.Value, "R1", 1000);
        var r2   = SampleData.AddRecord(repo, col.Id!.Value, "R2", 2000);
        repo.SetFieldValue(r1.Id!.Value, f.Id!.Value, "1,5");    // → хранится «1.5» (поле — точка)
        repo.SetFieldValue(r2.Id!.Value, f.Id!.Value, "-3,14");  // → «-3.14»

        // Меняем разделитель поля на запятую — существующие значения должны мигрировать.
        var field = repo.GetFields(col.Id!.Value).First(x => x.Id == f.Id);
        field.ConfigJson = Config(DecimalMark.Comma);
        repo.UpdateField(field);

        var vals = repo.GetFieldValuesBatch([r1.Id!.Value, r2.Id!.Value]);
        Assert.Equal("1,5",   vals[r1.Id!.Value][f.Id!.Value]);
        Assert.Equal("-3,14", vals[r2.Id!.Value][f.Id!.Value]);
    }

    [Fact]
    public void Normalization_leaves_non_numeric_untouched()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);
        // Текстовое поле → значение-«дата» с точками; смена типа на число не должна ломать точки.
        var f    = SampleData.AddField(repo, col.Id!.Value, "Поле", "text");
        var rec  = SampleData.AddRecord(repo, col.Id!.Value, "R");
        repo.SetFieldValue(rec.Id!.Value, f.Id!.Value, "01.02.2026");

        var field = repo.GetFields(col.Id!.Value).First(x => x.Id == f.Id);
        field.FieldType  = "number";
        field.ConfigJson = Config(DecimalMark.Comma);
        repo.UpdateField(field);

        var stored = repo.GetFieldValuesBatch([rec.Id!.Value])[rec.Id!.Value][f.Id!.Value];
        Assert.Equal("01.02.2026", stored);   // не число — миграция не тронула
    }
}
