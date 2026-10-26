using Microsoft.EntityFrameworkCore;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

public sealed class ConcurrentMoveAtomicityTests
{
    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void A_late_move_failure_rolls_back_schema_copy_projection_and_source_deletion(string profile)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var source = SampleData.AddCollection(fixture.Repo, "Источник").Id!.Value;
        var target = SampleData.AddCollection(fixture.Repo, "Пустое назначение").Id!.Value;
        var text = SampleData.AddField(fixture.Repo, source, "Текст").Id!.Value;
        var date = SampleData.AddCalendarDateField(fixture.Repo, source, "Дата").Id!.Value;
        var record = SampleData.AddRecord(fixture.Repo, source, "Сохраняемый источник").Id!.Value;
        fixture.Repo.SetFieldValue(record, text, "Все данные остаются в источнике");
        fixture.Repo.SetFieldValue(record, date, "2026-10-26");
        using var before = fixture.OpenContext();
        var original = before.DbRecords.AsNoTracking().Single(row => row.Id == record);
        var logCount = before.ChangeLog.LongCount();
        if (before.IsPostgres)
            before.Database.ExecuteSqlRaw("""
                CREATE FUNCTION reject_move_log() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.action = 'move' THEN RAISE EXCEPTION 'blocked move'; END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER reject_move_log BEFORE INSERT ON change_log
                FOR EACH ROW EXECUTE FUNCTION reject_move_log();
                """);
        else
            before.Database.ExecuteSqlRaw("""
                CREATE TRIGGER reject_move_log BEFORE INSERT ON change_log
                WHEN NEW.Action = 'move'
                BEGIN SELECT RAISE(ABORT, 'blocked move'); END;
                """);

        var error = Record.Exception(() => fixture.Repo.MoveRecordsToCollectionIsolated(
            fixture.Path, source, [record], target, createMissingFields: true, sourceDbId: 1, targetDbId: 1));
        Assert.NotNull(error);
        Assert.Contains("blocked move", error.GetBaseException().Message);

        using var check = fixture.OpenContext();
        var remaining = Assert.Single(check.DbRecords.AsNoTracking());
        Assert.Equal(record, remaining.Id);
        Assert.Equal(source, remaining.CollectionId);
        Assert.Equal(original.Title, remaining.Title);
        Assert.Equal(original.CreatedAt, remaining.CreatedAt);
        Assert.Equal(original.UpdatedAt, remaining.UpdatedAt);
        Assert.Equal(original.CreatedBy, remaining.CreatedBy);
        Assert.Equal(original.UpdatedBy, remaining.UpdatedBy);
        Assert.Empty(check.DbFields.Where(field => field.CollectionId == target));
        var values = new ConstructorRepository(check).GetFieldValues(record);
        Assert.Equal(2, values.Count);
        Assert.Equal("Все данные остаются в источнике", values[text]);
        Assert.Equal("2026-10-26", values[date]);
        var projection = Assert.Single(check.CalendarIndex.AsNoTracking());
        Assert.Equal(record, projection.RecordId);
        Assert.Equal(date, projection.FieldId);
        Assert.Equal(source, projection.CollectionId);
        Assert.Equal(logCount, check.ChangeLog.LongCount());
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Two_cross_database_moves_claim_one_source_before_publishing_a_copy(string profile)
    {
        using var source = new MultiUserTestDatabase(profile);
        using var target = new MultiUserTestDatabase(profile);
        var sourceCollection = SampleData.AddCollection(source.Repo, "Источник").Id!.Value;
        var targetCollection = SampleData.AddCollection(target.Repo, "Назначение").Id!.Value;
        var sourceField = SampleData.AddField(source.Repo, sourceCollection, "Текст").Id!.Value;
        var targetField = SampleData.AddField(target.Repo, targetCollection, "Текст").Id!.Value;
        var id = SampleData.AddRecord(source.Repo, sourceCollection, "Один источник").Id!.Value;
        source.Repo.SetFieldValue(id, sourceField, "Единственный полный экземпляр");
        var results = new List<int>[2];

        MultiUserTestDatabase.RunClients(target, 2, (repository, _, client) =>
            results[client] = repository.MoveRecordsToCollectionIsolated(source.Path, sourceCollection,
                [id], targetCollection, createMissingFields: false, sourceDbId: 1, targetDbId: 2));

        using var sourceCheck = source.OpenContext();
        using var targetCheck = target.OpenContext();
        Assert.Empty(sourceCheck.DbRecords);
        Assert.Empty(sourceCheck.DbFieldValues);
        var published = Assert.Single(targetCheck.DbRecords.AsNoTracking());
        Assert.Equal("Один источник", published.Title);
        Assert.Equal(targetCollection, published.CollectionId);
        Assert.Equal(published.Id, Assert.Single(results.SelectMany(ids => ids)));
        var value = Assert.Single(targetCheck.DbFieldValues.AsNoTracking());
        Assert.Equal(published.Id, value.RecordId);
        Assert.Equal(targetField, value.FieldId);
        Assert.Equal("Единственный полный экземпляр", value.Value);
        MultiUserTestDatabase.AssertHealthy(sourceCheck);
        MultiUserTestDatabase.AssertHealthy(targetCheck);
    }
}
