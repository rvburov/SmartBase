using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

public sealed class AtomicWriteBoundaryTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void PostgreSQL_pins_field_type_and_format_until_the_value_edit_commits(bool changeType)
    {
        using var fixture = new MultiUserTestDatabase("postgres");
        var collection = SampleData.AddCollection(fixture.Repo, "Метаданные во время записи").Id!.Value;
        var field = SampleData.AddField(fixture.Repo, collection, "Число", "number");
        field.ConfigJson = "{\"decimalSeparator\":\"comma\"}";
        fixture.Repo.UpdateField(field);
        var record = SampleData.AddRecord(fixture.Repo, collection, "Стабильное свойство").Id!.Value;
        fixture.Repo.SetFieldValue(record, field.Id!.Value, "1,25");
        using var staged = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var metadataChanged = false;

        MultiUserTestDatabase.RunClients(fixture, 2, (repo, context, client) =>
        {
            if (client == 0)
            {
                context.ChangeTracker.Tracked += (_, args) =>
                {
                    if (args.Entry is not { State: EntityState.Added, Entity: ChangeLogEntity row }
                        || row.TargetName != "Стабильное свойство" || row.Action != "update") return;
                    // Родители/метаданные уже прочитаны, новое значение еще не записано.
                    staged.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)), "Проверка метаданных не освободила редактор");
                };
                repo.SetFieldValue(record, field.Id.Value, "42,5");
            }
            else
            {
                try
                {
                    Assert.True(staged.Wait(TimeSpan.FromSeconds(10)), "Редактор не дошел до границы записи");
                    context.Database.OpenConnection();
                    context.Database.SetCommandTimeout(1);
                    context.Database.ExecuteSqlRaw("SET lock_timeout = '300ms'");
                    // Отдельное реальное подключение идет прямо в SQL, мимо локальной
                    // очереди приложения: проверяем защиту PostgreSQL между процессами.
                    var error = Record.Exception(() =>
                    {
                        if (changeType)
                            context.Database.ExecuteSqlRaw("UPDATE db_fields SET fieldtype = 'date' WHERE id = {0}", field.Id.Value);
                        else
                            context.Database.ExecuteSqlRaw("UPDATE db_fields SET configjson = {0} WHERE id = {1}",
                                "{\"decimalSeparator\":\"dot\"}", field.Id.Value);
                    });
                    if (error is null) metadataChanged = true;
                    else Assert.Equal("55P03", Assert.IsType<PostgresException>(error.GetBaseException()).SqlState);
                }
                finally { release.Set(); }
            }
        });

        Assert.False(metadataChanged, "Метаданные изменились после проверки типа, но до записи ячейки");
        using var check = fixture.OpenContext();
        var actualField = check.DbFields.AsNoTracking().Single(row => row.Id == field.Id);
        Assert.Equal("number", actualField.FieldType);
        Assert.Equal(field.ConfigJson, actualField.ConfigJson);
        Assert.Equal("42,5", Assert.Single(check.DbFieldValues.Where(value => value.RecordId == record)).Value);
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void A_rolled_back_child_cannot_return_through_a_loaded_navigation_on_the_next_save(string profile)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var context = fixture.Manager.Context;
        var repo = fixture.Repo;
        var collection = SampleData.AddCollection(repo, "Загруженный граф").Id!.Value;
        var field = SampleData.AddField(repo, collection, "Значение").Id!.Value;
        var healthy = SampleData.AddRecord(repo, collection, "Осталась").Id!.Value;
        context.ChangeTracker.Clear();
        var graph = context.DbCollections.Include(row => row.Records).Include(row => row.Fields)
            .Single(row => row.Id == collection);
        var originalRecords = graph.Records.ToArray();
        var originalFields = graph.Fields.ToArray();
        var recordsNavigation = graph.Records;

        using (var outer = context.Database.BeginTransaction())
        {
            var error = Record.Exception(() => repo.InWriteTransaction<int>(() =>
            {
                SampleData.AddRecord(repo, collection, "Не должна воскреснуть");
                Assert.Equal(2, graph.Records.Count);
                throw new InvalidOperationException("rollback-child");
            }));
            Assert.NotNull(error);
            Assert.Contains("rollback-child", error.ToString());
            Assert.Same(outer, context.Database.CurrentTransaction);
            Assert.Same(recordsNavigation, graph.Records);
            Assert.Equal(originalRecords, graph.Records);
            Assert.Equal(originalFields, graph.Fields);

            repo.SetFieldValue(healthy, field, "После отката");
            outer.Commit();
        }

        using var check = fixture.OpenContext();
        Assert.Equal(healthy, Assert.Single(check.DbRecords.Where(row => row.CollectionId == collection)).Id);
        Assert.Equal("После отката", Assert.Single(check.DbFieldValues.Where(value => value.RecordId == healthy)).Value);
        Assert.DoesNotContain(check.ChangeLog, row => row.TargetName == "Не должна воскреснуть");
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void A_failed_nested_edit_preserves_outer_work_and_only_the_original_pending_properties(string profile)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var context = fixture.Manager.Context;
        var repo = fixture.Repo;
        var collection = SampleData.AddCollection(repo, "Граница команды").Id!.Value;
        var text = SampleData.AddField(repo, collection, "Текст").Id!.Value;
        var date = SampleData.AddCalendarDateField(repo, collection, "Дата").Id!.Value;
        var healthy = SampleData.AddRecord(repo, collection, "Действующая запись").Id!.Value;
        var failing = SampleData.AddRecord(repo, collection, "Отказавшая запись").Id!.Value;
        repo.SetFieldValue(healthy, text, "До транзакции");
        repo.SetFieldValue(failing, date, "2026-11-01");
        context.ChangeTracker.Clear();
        var failedLogsBefore = JsonSerializer.Serialize(context.ChangeLog.AsNoTracking()
            .Where(row => row.TargetName == "Отказавшая запись").OrderBy(row => row.Id).ToList());
        var indexBefore = JsonSerializer.Serialize(context.CalendarIndex.AsNoTracking()
            .Where(row => row.RecordId == failing).ToList());
        context.Database.ExecuteSqlRaw(context.IsPostgres ? $$"""
            CREATE FUNCTION fail_nested_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.id = {{failing}} THEN RAISE EXCEPTION 'nested-audit-failure'; END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER fail_nested_audit BEFORE UPDATE ON db_records
            FOR EACH ROW EXECUTE FUNCTION fail_nested_audit();
            """ : $$"""
            CREATE TRIGGER fail_nested_audit BEFORE UPDATE ON db_records
            WHEN NEW.id = {{failing}}
            BEGIN SELECT RAISE(ABORT, 'nested-audit-failure'); END;
            """);

        using (var outer = context.Database.BeginTransaction())
        {
            repo.SetFieldValue(healthy, text, "Внешняя правка до отказа");
            var pending = context.DbRecords.Find(healthy)!;
            pending.Title = "Несохраненный заголовок";
            context.ChangeTracker.DetectChanges();
            Assert.True(context.Entry(pending).Property(row => row.Title).IsModified);
            Assert.False(context.Entry(pending).Property(row => row.Position).IsModified);

            var error = Record.Exception(() => repo.SetFieldValue(failing, date, "2026-12-02"));
            Assert.NotNull(error);
            Assert.Contains("nested-audit-failure", error.ToString());
            Assert.Same(outer, context.Database.CurrentTransaction);
            Assert.Equal("2026-11-01", context.DbFieldValues.AsNoTracking()
                .Single(value => value.RecordId == failing && value.FieldId == date).Value);
            Assert.Equal("Действующая запись", context.DbRecords.AsNoTracking()
                .Single(row => row.Id == healthy).Title);
            Assert.Equal("Несохраненный заголовок", pending.Title);
            Assert.True(context.Entry(pending).Property(row => row.Title).IsModified);
            Assert.False(context.Entry(pending).Property(row => row.Position).IsModified);
            Assert.False(context.Entry(pending).Property(row => row.CreatedAt).IsModified);

            // После перехваченной ошибки тот же контекст и та же внешняя транзакция
            // сохраняют прежнюю pending-правку, а не хвост отказавшей команды.
            repo.SetFieldValue(healthy, text, "Внешняя правка после отказа");
            Assert.Same(outer, context.Database.CurrentTransaction);
            outer.Commit();
        }

        using var check = fixture.OpenContext();
        Assert.Equal("Несохраненный заголовок", check.DbRecords.AsNoTracking()
            .Single(row => row.Id == healthy).Title);
        Assert.Equal("Внешняя правка после отказа", check.DbFieldValues.AsNoTracking()
            .Single(value => value.RecordId == healthy && value.FieldId == text).Value);
        Assert.Equal("2026-11-01", check.DbFieldValues.AsNoTracking()
            .Single(value => value.RecordId == failing && value.FieldId == date).Value);
        Assert.Equal(failedLogsBefore, JsonSerializer.Serialize(check.ChangeLog.AsNoTracking()
            .Where(row => row.TargetName == "Отказавшая запись").OrderBy(row => row.Id).ToList()));
        Assert.Equal(indexBefore, JsonSerializer.Serialize(check.CalendarIndex.AsNoTracking()
            .Where(row => row.RecordId == failing).ToList()));
        MultiUserTestDatabase.AssertHealthy(check);
    }
}
