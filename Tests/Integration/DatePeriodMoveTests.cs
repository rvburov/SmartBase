using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>Перенос обычных записей, а не коллекции целиком: схема сопоставляется
/// по именам, вычисляемые значения не превращаются в редактируемые строки EAV.</summary>
public sealed class DatePeriodMoveTests
{
    private const string StartName = "Начало";
    private const string EndName = "Конец";
    private const string AutomaticName = "Автоматический период";
    private const string FixedName = "Фиксированный период";

    [SkippableTheory]
    [InlineData("sqlite", false)]
    [InlineData("sqlite", true)]
    [InlineData("postgres", false)]
    [InlineData("postgres", true)]
    public void Scalar_copy_by_name_remaps_dates_and_copies_only_the_compatible_fixed_snapshot(string profile, bool existingFields)
    {
        using var db = new MultiUserTestDatabase(profile);
        var (source, records) = Seed(db.Repo);
        var target = SampleData.AddCollection(db.Repo, "Получатель").Id!.Value;
        if (existingFields) AddSchema(db.Repo, target);
        var record = SampleData.AddRecord(db.Repo, target, "Существующая запись получателя").Id!.Value;
        var sourceBefore = SourceData(db, source.Collection);

        db.Repo.CopyFieldValuesByName(db.Repo, records[0], source.Collection, record, target, createMissingFields: true);

        AssertValues(db, target, new Dictionary<int, string> { [record] = "7200" });
        Assert.Equal("Существующая запись получателя", db.Repo.GetRecord(record)!.Title);
        // Копирование значений не удаляет оригинал и не меняет его исходные даты/снимок.
        Assert.Equal(sourceBefore, SourceData(db, source.Collection));
        Assert.Equal(2, db.Repo.CountRecords(source.Collection));
        Assert.Equal("7200", db.Repo.GetFieldValues(records[0])[source.Fixed]);
        Assert.Equal("14400", db.Repo.GetFieldValues(records[0])[source.Automatic]);
        Assert.Equal("2026-10-28T09:00/2026-10-28T13:00", db.Repo.GetFieldValues(records[0])[source.Start]);
        using var check = db.OpenContext();
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite", false)]
    [InlineData("sqlite", true)]
    [InlineData("postgres", false)]
    [InlineData("postgres", true)]
    public void Bulk_move_handles_periods_before_their_source_dates_and_preserves_fixed_snapshots(string profile, bool existingFields)
    {
        using var db = new MultiUserTestDatabase(profile);
        var (source, records) = Seed(db.Repo);
        var target = SampleData.AddCollection(db.Repo, "Назначение").Id!.Value;
        if (existingFields) AddSchema(db.Repo, target);
        Assert.Equal(["date_period", "date_period", "date", "date"], db.Repo.GetFields(source.Collection).Select(f => f.FieldType));

        var moved = db.Repo.MoveRecordsToCollectionIsolated(db.Path, source.Collection, records, target,
            createMissingFields: true, sourceDbId: 1, targetDbId: 1);

        Assert.Equal(2, moved.Count);
        Assert.Equal(0, db.Repo.CountRecords(source.Collection));
        AssertValues(db, target, new Dictionary<int, string> { [moved[0]] = "7200", [moved[1]] = "3600" });
        Assert.Equal(["Первая", "Вторая"], db.Repo.GetRecords(target).Select(record => record.Title));
        using var check = db.OpenContext();
        Assert.False(check.DbFieldValues.Any(value => records.Contains(value.RecordId)));
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite", false)]
    [InlineData("sqlite", true)]
    [InlineData("postgres", false)]
    [InlineData("postgres", true)]
    public void A_compatible_empty_fixed_snapshot_is_preserved_instead_of_recalculating_from_later_dates(string profile, bool bulk)
    {
        using var db = new MultiUserTestDatabase(profile);
        var source = AddSchema(db.Repo, SampleData.AddCollection(db.Repo, "Пустой снимок").Id!.Value);
        var original = SampleData.AddRecord(db.Repo, source.Collection, "Даты добавлены позже").Id!.Value;
        db.Repo.SetFieldValue(original, source.Start, "2026-10-28T09:00/2026-10-28T13:00");
        db.Repo.SetFieldValue(original, source.End, "2026-10-28T13:00");
        Assert.Equal("", db.Repo.GetFieldValues(original)[source.Fixed]);
        Assert.Equal("14400", db.Repo.GetFieldValues(original)[source.Automatic]);
        var target = AddSchema(db.Repo, SampleData.AddCollection(db.Repo, "Получатель пустого снимка").Id!.Value);
        int moved;
        if (bulk)
            moved = Assert.Single(db.Repo.MoveRecordsToCollectionIsolated(db.Path, source.Collection, [original], target.Collection,
                createMissingFields: true, sourceDbId: 1, targetDbId: 1));
        else
        {
            moved = SampleData.AddRecord(db.Repo, target.Collection, "Получатель").Id!.Value;
            db.Repo.CopyFieldValuesByName(db.Repo, original, source.Collection, moved, target.Collection, createMissingFields: true);
        }
        AssertValues(db, target.Collection, new Dictionary<int, string> { [moved] = "" });
    }

    [SkippableTheory]
    [InlineData("sqlite", false)]
    [InlineData("sqlite", true)]
    [InlineData("postgres", false)]
    [InlineData("postgres", true)]
    public void A_different_target_rule_uses_its_own_creation_snapshot_and_never_imports_the_source_cache(string profile, bool bulk)
    {
        using var db = new MultiUserTestDatabase(profile);
        var (source, records) = Seed(db.Repo);
        var target = AddSchema(db.Repo, SampleData.AddCollection(db.Repo, "Другое рабочее окно").Id!.Value);
        var ownRule = new DatePeriodConfig(target.Start, Automatic: false, DayStart: "09:00", DayEnd: "10:00");
        var field = db.Repo.GetFields(target.Collection).Single(value => value.Id == target.Fixed);
        field.ConfigJson = ownRule.ToConfigJson();
        db.Repo.UpdateField(field);
        Dictionary<int, string> snapshots;
        if (bulk)
        {
            var moved = db.Repo.MoveRecordsToCollectionIsolated(db.Path, source.Collection, records, target.Collection,
                createMissingFields: true, sourceDbId: 1, targetDbId: 1);
            snapshots = new() { [moved[0]] = "3600", [moved[1]] = "0" };
        }
        else
        {
            var existing = Assert.Single(db.Repo.BulkAddRecords(target.Collection, [target.Start, target.End],
                [new BulkRecordRow("Свой прежний снимок", ["2026-10-28T10:00/2026-10-28T11:00", "2030-01-01"])], 1, default));
            Assert.Equal("0", db.Repo.GetFieldValues(existing)[target.Fixed]);
            db.Repo.CopyFieldValuesByName(db.Repo, records[0], source.Collection, existing, target.Collection, createMissingFields: true);
            snapshots = new() { [existing] = "0" };
        }
        AssertValues(db, target.Collection, snapshots, ownRule);
    }

    [SkippableTheory]
    [InlineData("sqlite", false)]
    [InlineData("sqlite", true)]
    [InlineData("postgres", false)]
    [InlineData("postgres", true)]
    public void A_same_name_text_collision_is_rejected_without_partial_schema_values_or_source_deletion(string profile, bool bulk)
    {
        using var db = new MultiUserTestDatabase(profile);
        var (source, records) = Seed(db.Repo);
        var target = SampleData.AddCollection(db.Repo, "Конфликт типов").Id!.Value;
        var collision = SampleData.AddField(db.Repo, target, StartName, "text").Id!.Value;
        var existing = SampleData.AddRecord(db.Repo, target, "Оставить получателя").Id!.Value;
        db.Repo.SetFieldValue(existing, collision, "Этот текст нельзя превратить в исходную дату периода");
        var sourceBefore = Snapshot(db, source.Collection);
        var targetBefore = Snapshot(db, target);

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            if (bulk)
                db.Repo.MoveRecordsToCollectionIsolated(db.Path, source.Collection, records, target,
                    createMissingFields: true, sourceDbId: 1, targetDbId: 1);
            else
                db.Repo.CopyFieldValuesByName(db.Repo, records[0], source.Collection, existing, target, createMissingFields: true);
        });

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Equal(sourceBefore, Snapshot(db, source.Collection));
        Assert.Equal(targetBefore, Snapshot(db, target));
        using var check = db.OpenContext();
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite", "sqlite")]
    [InlineData("sqlite", "postgres")]
    [InlineData("postgres", "sqlite")]
    [InlineData("postgres", "postgres")]
    public void Cross_database_record_move_remaps_period_sources_and_preserves_only_fixed_cached_values(string sourceProfile, string targetProfile)
    {
        using var sourceDb = new MultiUserTestDatabase(sourceProfile);
        using var targetDb = new MultiUserTestDatabase(targetProfile);
        var (source, records) = Seed(sourceDb.Repo);
        var target = SampleData.AddCollection(targetDb.Repo, "Межбазовый получатель").Id!.Value;
        // Занятые номера в приемнике не позволяют случайному совпадению id скрыть отсутствие переписи.
        var decoy = SampleData.AddCollection(targetDb.Repo, "Посторонняя схема").Id!.Value;
        for (var i = 0; i < 5; i++) SampleData.AddField(targetDb.Repo, decoy, "Постороннее " + i);

        var moved = targetDb.Repo.MoveRecordsToCollectionIsolated(sourceDb.Path, source.Collection, records, target,
            createMissingFields: true, sourceDbId: 1, targetDbId: 2);

        Assert.Equal(2, moved.Count);
        Assert.Equal(0, sourceDb.Repo.CountRecords(source.Collection));
        AssertValues(targetDb, target, new Dictionary<int, string> { [moved[0]] = "7200", [moved[1]] = "3600" });
        var targetFields = targetDb.Repo.GetFields(target).ToDictionary(field => field.Name);
        var remapped = DatePeriodConfig.FromConfig(targetFields[AutomaticName].ConfigJson);
        Assert.NotNull(remapped);
        Assert.Equal(targetFields[StartName].Id, remapped!.DateFieldId);
        Assert.NotEqual(source.Start, remapped.DateFieldId);
        using var sourceCheck = sourceDb.OpenContext();
        using var targetCheck = targetDb.OpenContext();
        Assert.False(sourceCheck.DbFieldValues.Any(value => records.Contains(value.RecordId)));
        Assert.Equal(5, targetCheck.DbFields.Count(field => field.CollectionId == decoy));
        MultiUserTestDatabase.AssertHealthy(sourceCheck);
        MultiUserTestDatabase.AssertHealthy(targetCheck);
    }

    private sealed record Schema(int Collection, int Start, int End, int Automatic, int Fixed);

    private static Schema AddSchema(ConstructorRepository repo, int collection)
    {
        var start = SampleData.AddField(repo, collection, StartName, "date").Id!.Value;
        var end = SampleData.AddField(repo, collection, EndName, "date").Id!.Value;
        int Period(string name, bool automatic) => repo.AddField(new DbField
        {
            CollectionId = collection, Name = name, FieldType = "date_period", IsSystem = true,
            ConfigJson = new DatePeriodConfig(start, Automatic: automatic).ToConfigJson(),
        }).Id!.Value;
        var automatic = Period(AutomaticName, true);
        var fixedField = Period(FixedName, false);
        repo.ReorderFields([automatic, fixedField, start, end]);
        return new(collection, start, end, automatic, fixedField);
    }

    private static (Schema Schema, List<int> Records) Seed(ConstructorRepository repo)
    {
        var schema = AddSchema(repo, SampleData.AddCollection(repo, "Исходная коллекция").Id!.Value);
        var records = repo.BulkAddRecords(schema.Collection, [schema.Start, schema.End],
        [
            new BulkRecordRow("Первая", ["2026-10-28T09:00/2026-10-28T11:00", "2030-01-01"]),
            new BulkRecordRow("Вторая", ["2026-10-28T10:00/2026-10-28T11:00", "2030-01-01"]),
        ], 2, default);
        repo.SetFieldValue(records[0], schema.Start, "2026-10-28T09:00/2026-10-28T13:00");
        repo.SetFieldValue(records[1], schema.Start, "2026-10-28T10:00/2026-10-28T14:00");
        Assert.Equal("7200", repo.GetFieldValues(records[0])[schema.Fixed]);
        Assert.Equal("3600", repo.GetFieldValues(records[1])[schema.Fixed]);
        return (schema, records);
    }

    private static void AssertValues(MultiUserTestDatabase database, int collection, IReadOnlyDictionary<int, string> expectedFixed,
        DatePeriodConfig? ownFixedRule = null)
    {
        using var context = database.OpenContext();
        var repo = new ConstructorRepository(context);
        var fields = repo.GetFields(collection).ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(4, fields.Count);
        var start = fields[StartName].Id!.Value;
        Assert.Equal("date", fields[StartName].FieldType);
        Assert.Equal("date", fields[EndName].FieldType);
        Assert.Equal(new DatePeriodConfig(start), DatePeriodConfig.FromConfig(fields[AutomaticName].ConfigJson));
        Assert.Equal(ownFixedRule ?? new DatePeriodConfig(start, Automatic: false), DatePeriodConfig.FromConfig(fields[FixedName].ConfigJson));
        foreach (var (record, snapshot) in expectedFixed)
        {
            var values = repo.GetFieldValues(record);
            Assert.Equal("14400", values[fields[AutomaticName].Id!.Value]);
            Assert.Equal(snapshot, values[fields[FixedName].Id!.Value]);
        }
        var automaticId = fields[AutomaticName].Id!.Value;
        Assert.False(context.DbFieldValues.Any(value => value.FieldId == automaticId));
        var fixedId = fields[FixedName].Id!.Value;
        Assert.Equal(expectedFixed.Count, context.DbFieldValues.Count(value => value.FieldId == fixedId));
    }

    private static string SourceData(MultiUserTestDatabase fixture, int collection)
    {
        using var context = fixture.OpenContext();
        return JsonSerializer.Serialize(new
        {
            Records = context.DbRecords.AsNoTracking().Where(record => record.CollectionId == collection).OrderBy(record => record.Id)
                .Select(record => new { record.Id, record.Title, record.CreatedAt, record.UpdatedAt, record.CreatedBy, record.UpdatedBy }).ToList(),
            Values = context.DbFieldValues.AsNoTracking().Where(value => value.Record.CollectionId == collection)
                .OrderBy(value => value.RecordId).ThenBy(value => value.FieldId)
                .Select(value => new { value.RecordId, value.FieldId, value.Value }).ToList(),
        });
    }

    private static string Snapshot(MultiUserTestDatabase fixture, int collection)
    {
        using var context = fixture.OpenContext();
        return JsonSerializer.Serialize(new
        {
            Data = SourceData(fixture, collection),
            Fields = context.DbFields.AsNoTracking().Where(field => field.CollectionId == collection).OrderBy(field => field.Id)
                .Select(field => new { field.Id, field.Name, field.FieldType, field.ConfigJson, field.Position }).ToList(),
            Logs = context.ChangeLog.LongCount(),
        });
    }
}
