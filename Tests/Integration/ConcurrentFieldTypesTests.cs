using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Exporters.TableWriters;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Хранимые типы из базовой матрицы проходят настоящие команды независимых редакторов.
/// Relation хранит ребра, lookup/rollup и аудит вычисляются: SetFieldValue для них здесь
/// не вызывается. Их контракт проверяют отдельные наборы связей/вычислений/аудита.
/// </summary>
public sealed class ConcurrentFieldTypesTests
{
    private const int Clients = 8;
    private static readonly string[] StoredTypes =
        ["text", "number", "date", "select", "status", "multiselect", "checkbox", "url", "file", "user"];
    private static readonly string[] Profiles = ["sqlite", "sqlite-shared", "postgres"];

    public static IEnumerable<object[]> Cases() =>
        Profiles.SelectMany(profile => StoredTypes.Select(type => new object[] { profile, type }));

    [Fact]
    public void Concurrent_cases_cover_every_stored_type_of_the_functional_matrix()
    {
        Assert.Equal(FieldTypeMatrixTests.TypeValues().Select(row => (string)row[0]).Order(), StoredTypes.Order());
        Assert.Equal(StoredTypes.Length, StoredTypes.Distinct().Count());
        Assert.All(StoredTypes, type =>
        {
            Assert.False(FieldTypeInfo.IsComputed(type));
            Assert.False(FieldTypeInfo.IsRelation(type));
            Assert.False(string.IsNullOrWhiteSpace(FieldTypeInfo.Label(type)));
        });
        Assert.All(new[] { "created_at", "updated_at", "created_by", "updated_by", "lookup", "rollup" },
            type => Assert.True(FieldTypeInfo.IsComputed(type)));
        Assert.True(FieldTypeInfo.IsRelation("relation"));
    }

    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public void Parallel_editors_roundtrip_update_duplicate_and_move_every_stored_type(string profile, string type)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var schema = CreateSchema(fixture.Repo, type);
        RunAndVerify(fixture, Enumerable.Repeat(schema, Clients).ToArray());
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Different_stored_types_can_be_edited_at_the_same_time_without_crossing_values(string profile)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        // Типы разделены коллекциями: user-дефолт другого свойства не становится лишней
        // ячейкой чужой записи. База и общий старт всех десяти редакторов остаются общими.
        var schemas = StoredTypes.Select(type => CreateSchema(fixture.Repo, type)).ToArray();
        RunAndVerify(fixture, schemas);
    }

    private static Schema CreateSchema(ConstructorRepository repo, string type)
    {
        var source = SampleData.AddCollection(repo, $"Источник {type}").Id!.Value;
        var target = SampleData.AddCollection(repo, $"Назначение {type}").Id!.Value;
        var config = type switch
        {
            "number" => "{\"decimalSeparator\":\"dot\"}",
            "select" or "status" => JsonSerializer.Serialize(new
            {
                options = Enumerable.Range(0, StoredTypes.Length)
                    .SelectMany(client => new[] { Raw(type, client, false), Raw(type, client, true) }).ToArray(),
            }),
            "multiselect" => JsonSerializer.Serialize(new
            {
                options = Enumerable.Range(0, StoredTypes.Length)
                    .SelectMany(client => Options(client, false).Concat(Options(client, true))).ToArray(),
                separator = "semicolon",
            }),
            _ => "",
        };
        DbField Field(int collection, string name, string fieldType, int position) => repo.AddField(new DbField
        {
            CollectionId = collection, Name = name, FieldType = fieldType, Position = position,
            ConfigJson = name == "Значение" ? config : "", IsCalStart = fieldType == "date",
        });
        var sourceField = Field(source, "Значение", type, 1000).Id!.Value;
        var sourceMarker = Field(source, "Метка участника", "text", 2000).Id!.Value;
        var targetField = Field(target, "Значение", type, 1000).Id!.Value;
        var targetMarker = Field(target, "Метка участника", "text", 2000).Id!.Value;
        return new(type, source, target, sourceField, sourceMarker, targetField, targetMarker, config);
    }

    private static void RunAndVerify(MultiUserTestDatabase fixture, Schema[] schemas)
    {
        var completed = new Completed[schemas.Length];
        MultiUserTestDatabase.RunClients(fixture, schemas.Length, (repo, context, client) =>
        {
            var schema = schemas[client];
            var marker = $"{schema.Type}:участник-{client:D2}";
            using var actor = CurrentUser.ActAs($"field-type-{client}@concurrent-test", $"Редактор {client}");
            var record = SampleData.AddRecord(repo, schema.Source, marker, (client + 1) * 1000);
            var id = record.Id!.Value;
            repo.SetFieldValue(id, schema.SourceMarker, marker);
            repo.SetFieldValue(id, schema.SourceField, Raw(schema.Type, client, false));
            context.ChangeTracker.Clear();
            AssertRead(repo, schema, id, schema.SourceField, schema.SourceMarker, marker, client, false);

            repo.SetFieldValue(id, schema.SourceField, Raw(schema.Type, client, true));
            context.ChangeTracker.Clear();
            AssertRead(repo, schema, id, schema.SourceField, schema.SourceMarker, marker, client, true);
            var beforeMove = context.DbRecords.AsNoTracking().Single(row => row.Id == id);
            var audit = new Audit(beforeMove.CreatedAt, beforeMove.UpdatedAt, beforeMove.CreatedBy, beforeMove.UpdatedBy);

            var copy = repo.DuplicateRecord(id);
            Assert.NotEqual(id, copy.Id!.Value);
            context.ChangeTracker.Clear();
            AssertRead(repo, schema, copy.Id.Value, schema.SourceField, schema.SourceMarker, marker, client, true);

            var moved = Assert.Single(repo.MoveRecordsToCollectionIsolated(fixture.Path, schema.Source,
                [id], schema.Target, createMissingFields: false, sourceDbId: 1, targetDbId: 1));
            Assert.NotEqual(copy.Id.Value, moved);
            context.ChangeTracker.Clear();
            var oldId = repo.GetRecord(id);
            Assert.False(oldId is not null && oldId.CollectionId == schema.Source && oldId.Title == marker,
                "После переноса исходная запись осталась в исходной коллекции");
            AssertRead(repo, schema, moved, schema.TargetField, schema.TargetMarker, marker, client, true);
            completed[client] = new(copy.Id.Value, moved, marker, audit);
        });

        using var check = fixture.OpenContext();
        var reader = new ConstructorRepository(check);
        var rows = check.DbRecords.AsNoTracking().ToDictionary(row => row.Id);
        var expectedIds = completed.SelectMany(done => new[] { done.Copy, done.Moved }).ToHashSet();
        Assert.Equal(schemas.Length * 2, expectedIds.Count);
        // Отсутствие старого int-ID само по себе не является инвариантом: SQLite вправе
        // переиспользовать ID. Точный набор подтвержденных ID и метки/коллекции ниже
        // доказывают отсутствие лишних оригиналов без запрета законного переиспользования.
        Assert.True(expectedIds.SetEquals(rows.Keys), "Итоговые записи не совпадают с подтвержденными копиями и переносами");
        Assert.Equal(schemas.Length * 4, check.DbFieldValues.Count());
        Assert.Equal(schemas.Count(schema => schema.Type == "date") * 2, check.CalendarIndex.Count());
        for (var client = 0; client < schemas.Length; client++)
        {
            var schema = schemas[client];
            var done = completed[client];
            Assert.Equal(schema.Source, rows[done.Copy].CollectionId);
            Assert.Equal(schema.Target, rows[done.Moved].CollectionId);
            Assert.Equal(done.Marker, rows[done.Copy].Title);
            Assert.Equal(done.Marker, rows[done.Moved].Title);
            var moved = rows[done.Moved];
            Assert.Equal(done.Audit, new Audit(moved.CreatedAt, moved.UpdatedAt, moved.CreatedBy, moved.UpdatedBy));
            AssertRead(reader, schema, done.Copy, schema.SourceField, schema.SourceMarker, done.Marker, client, true);
            AssertRead(reader, schema, done.Moved, schema.TargetField, schema.TargetMarker, done.Marker, client, true);
            AssertProjection(check, schema, done.Copy, schema.Source, schema.SourceField, client);
            AssertProjection(check, schema, done.Moved, schema.Target, schema.TargetField, client);
        }
        foreach (var schema in schemas.Distinct())
        {
            foreach (var collection in new[] { schema.Source, schema.Target })
            {
                var fields = reader.GetFields(collection);
                Assert.Equal(2, fields.Count);
                var valueField = Assert.Single(fields, field => field.Name == "Значение");
                Assert.Equal(schema.Type, valueField.FieldType);
                Assert.Equal(schema.Config, valueField.ConfigJson);
                Assert.Equal(schema.Type == "date", valueField.IsCalStart);
            }
        }
        MultiUserTestDatabase.AssertHealthy(check);
    }

    private static void AssertRead(ConstructorRepository repo, Schema schema, int record,
        int field, int markerField, string marker, int client, bool updated)
    {
        var expected = Stored(schema.Type, client, updated);
        var values = repo.GetFieldValues(record);
        Assert.Equal(2, values.Count);
        Assert.Equal(marker, values[markerField]);
        Assert.Equal(expected, values[field]);
        var batch = Assert.Single(repo.GetFieldValuesBatch([record]));
        Assert.Equal(record, batch.Key);
        Assert.Equal(2, batch.Value.Count);
        Assert.Equal(marker, batch.Value[markerField]);
        Assert.Equal(expected, batch.Value[field]);
        switch (schema.Type)
        {
            case "date":
                Assert.True(DateValue.TryParseStorage(values[field], out var parsed));
                Assert.Equal(Date(client, updated), parsed);
                break;
            case "number":
                Assert.True(WriterHelpers.TryNumber(values[field], out var number));
                Assert.Equal(double.Parse(expected, CultureInfo.InvariantCulture), number);
                Assert.DoesNotContain(',', values[field]);
                break;
            case "multiselect":
                Assert.Equal(Options(client, updated), MultiSelectValue.Parse(values[field]));
                break;
            case "file":
                Assert.Equal(Paths(client, updated), FileValue.ReadPaths(values[field]));
                break;
            case "checkbox":
                Assert.Equal(expected == "1", WriterHelpers.IsChecked(values[field]));
                break;
        }
    }

    private static void AssertProjection(AppDbContext context, Schema schema, int record,
        int collection, int field, int client)
    {
        var projections = context.CalendarIndex.AsNoTracking().Where(row => row.RecordId == record).ToList();
        if (schema.Type != "date") { Assert.Empty(projections); return; }
        var projection = Assert.Single(projections);
        var expected = Date(client, true);
        var columns = expected.ToRecordColumns();
        Assert.Equal(collection, projection.CollectionId);
        Assert.Equal(field, projection.FieldId);
        Assert.Equal(columns.StartIso, projection.StartIso);
        Assert.Equal(columns.EndIso, projection.EndIso);
        Assert.Equal(expected.AllDay, projection.AllDay);
    }

    private static string Stored(string type, int client, bool updated) => type == "number"
        ? Raw(type, client, updated).Replace(',', '.') : Raw(type, client, updated);

    private static string Raw(string type, int client, bool updated) => type switch
    {
        "text" => $"Участник {client}: {(updated ? "последний" : "первый")} текст, \"кавычки\"\nвторая строка",
        "number" => updated ? $"-{client + 11},75" : $"{client + 1},25",
        "date" => Date(client, updated).ToStorage(),
        "select" => $"Вариант {client} {(updated ? "Б" : "А")}",
        "status" => $"Стадия {client} {(updated ? "готово" : "в работе")}",
        "multiselect" => MultiSelectValue.Serialize(Options(client, updated)),
        "checkbox" => (client % 2 == 0) != updated ? "1" : "0",
        "url" => $"https://example.invalid/client/{client}?phase={(updated ? "final" : "initial")}",
        "file" => FileValue.Serialize(Paths(client, updated)),
        "user" => $"Пользователь {client} {(updated ? "последний" : "исходный")}",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Нет случая хранимого типа"),
    };

    private static string[] Options(int client, bool updated) => updated
        ? [$"Пункт, {client}", $"Вариант \"Б\" {client}"] : [$"Пункт А {client}"];

    private static string[] Paths(int client, bool updated) => updated
        ? [$"/synthetic-field-types/client-{client}/Отчет, финал.txt", $"C:/synthetic-field-types/client-{client}/Приложение.pdf"]
        : [$"/synthetic-field-types/client-{client}/Исходник.txt"];

    private static DateValue Date(int client, bool updated)
    {
        var start = new DateTime(2026, 10, 1).AddDays(client);
        return updated
            ? new(start.AddMonths(1).AddHours(10), start.AddMonths(1).AddHours(11).AddMinutes(30), false)
            : new(start, start.Add(DateValue.EndOfDay), true);
    }

    private sealed record Schema(string Type, int Source, int Target, int SourceField, int SourceMarker,
        int TargetField, int TargetMarker, string Config);
    private sealed record Audit(string CreatedAt, string UpdatedAt, string CreatedBy, string UpdatedBy);
    private sealed record Completed(int Copy, int Moved, string Marker, Audit Audit);
}
