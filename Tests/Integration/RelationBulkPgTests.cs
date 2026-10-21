using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>Пакетные ячейки, связи и отмена проверяются на настоящем PostgreSQL, включая
/// совпадающие id чужой базы, конфликт конфигурации и откат транзакции.</summary>
public sealed class RelationBulkPgTests
{
    private sealed class Fixture : IDisposable
    {
        public DatabaseManager Manager { get; }
        public ConstructorRepository Repo => Manager.Constructor;
        public int Collection { get; }
        public int Targets { get; }
        public int Text { get; }
        public int Number { get; }
        public int Check { get; }
        public int Date { get; }
        public int Relation { get; }
        public int First { get; }
        public int Second { get; }
        public int Target1 { get; }
        public int Target2 { get; }

        public Fixture()
        {
            Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL (порт 5455) не поднят");
            var connectionString = PgTestServer.ConnectionString(PgTestServer.Database1);
            using (var connection = new NpgsqlConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
                command.ExecuteNonQuery();
            }
            Manager = DatabaseManager.Open(101, "Пакетные правки PG", connectionString, provider: "postgres");
            Collection = SampleData.AddCollection(Repo, "Записи").Id!.Value;
            Targets = SampleData.AddCollection(Repo, "Цели").Id!.Value;
            Text = SampleData.AddField(Repo, Collection, "Текст").Id!.Value;
            Number = SampleData.AddField(Repo, Collection, "Число", "number").Id!.Value;
            Check = SampleData.AddField(Repo, Collection, "Готово", "checkbox").Id!.Value;
            Date = SampleData.AddCalendarDateField(Repo, Collection).Id!.Value;
            var relation = Repo.AddField(new DbField { CollectionId = Collection, Name = "Связь", FieldType = "relation",
                ConfigJson = new RelationConfig(Targets, false).ToConfigJson() });
            Relation = relation.Id!.Value;
            First = SampleData.AddRecord(Repo, Collection, "Первая").Id!.Value;
            Second = SampleData.AddRecord(Repo, Collection, "Вторая").Id!.Value;
            Target1 = SampleData.AddRecord(Repo, Targets, "Цель А").Id!.Value;
            Target2 = SampleData.AddRecord(Repo, Targets, "Цель Б").Id!.Value;
            Repo.SetFieldValue(First, Text, "Было А");
            Repo.SetFieldValue(Second, Text, "Было Б");
        }

        public AppDbContext Read() => new(Manager.Path);
        public void Dispose() => Manager.Dispose();
    }

    [SkippableFact]
    public void Mixed_cell_types_and_title_undo_preserve_a_later_edit_and_calendar_projection()
    {
        using var f = new Fixture();
        DateValue.TryParse("21.10.2026", out var day);
        var changes = f.Repo.SetCellsBulk(f.Collection,
            [new(f.First, f.Text, "Стало А"), new(f.Second, f.Text, "Стало Б"), new(f.First, f.Number, "1,25"),
                new(f.First, f.Check, "1"), new(f.First, f.Date, day.ToStorage())], [new(f.First, "Новое название")], "Вставка");
        Assert.Equal(6, changes.Changed);
        Assert.Contains(f.Repo.GetCalendarRecordsByRange(new DateTime(2026, 10, 20), new DateTime(2026, 10, 23)), r => r.Id == f.First);
        f.Repo.SetFieldValue(f.Second, f.Text, "Поздняя правка");
        var undo = f.Repo.SetCellsBulk(f.Collection,
            changes.Changes.Where(c => c.FieldId.HasValue).Select(c => new ConstructorRepository.CellWrite(c.RecordId,
                c.FieldId!.Value, c.OldValue, c.NewValue)).ToList(),
            changes.Changes.Where(c => !c.FieldId.HasValue).Select(c => new ConstructorRepository.TitleWrite(c.RecordId,
                c.OldValue, c.NewValue)).ToList(), "Отмена: вставка");
        Assert.Equal(5, undo.Changed);
        Assert.Equal(1, undo.Conflicts);
        Assert.Equal("Было А", f.Repo.GetFieldValues(f.First)[f.Text]);
        Assert.Equal("Поздняя правка", f.Repo.GetFieldValues(f.Second)[f.Text]);
        Assert.Equal("Первая", f.Repo.GetRecord(f.First)!.Title);
        Assert.DoesNotContain(f.Repo.GetCalendarRecordsByRange(new DateTime(2026, 10, 20), new DateTime(2026, 10, 23)), r => r.Id == f.First);
        using var read = f.Read();
        Assert.False(read.DbFieldValues.Any(v => v.RecordId == f.First && (v.FieldId == f.Number || v.FieldId == f.Check || v.FieldId == f.Date)));
    }

    [SkippableFact]
    public void Link_bulk_and_undo_keep_full_database_identity_when_target_ids_match()
    {
        using var f = new Fixture();
        using var external = new TempDatabase(303);
        var externalCollection = SampleData.AddCollection(external.Constructor).Id!.Value;
        for (var i = 0; i < f.Target1; i++) SampleData.AddRecord(external.Constructor, externalCollection, $"Внешняя {i}");
        using var resolver = RelationDbResolver.UseScopedResolver(id => id == 303 ? external.Constructor : id == 101 ? f.Repo : null);
        var original = new[] { new RelationTarget(303, f.Target1), new RelationTarget(0, f.Target1), new RelationTarget(0, f.Target2) };
        f.Manager.Context.DbRecordLinks.AddRange(original.Select((target, index) => new DbRecordLinkEntity
        { FieldId = f.Relation, FromRecordId = f.First, TargetDbId = target.DbId, ToRecordId = target.RecordId, Position = (index + 1) * 1000 }));
        f.Manager.Context.SaveChanges();
        var changes = f.Repo.SetLinksBulk(f.Collection, [ConstructorRepository.LinkWrite.FromTargets(f.First, f.Relation, [new(0, f.Target1)])]);
        Assert.Equal(1, changes.Changed);
        Assert.Equal(original, Assert.Single(changes.Changes).OldTargets);
        var undo = f.Repo.SetLinksBulk(f.Collection, Undo(changes));
        Assert.Equal(1, undo.Changed);
        Assert.Equal(original.Select(t => (t.RecordId, t.DbId)), f.Repo.GetLinkEdges(f.Relation, f.First));
        var another = f.Repo.SetLinksBulk(f.Collection, [ConstructorRepository.LinkWrite.FromTargets(f.First, f.Relation, [new(0, f.Target2)])]);
        f.Repo.SetLinks(f.Relation, f.First, [f.Target1]);
        var conflicting = f.Repo.SetLinksBulk(f.Collection, Undo(another));
        Assert.Equal(0, conflicting.Changed); Assert.Equal(1, conflicting.Conflicts);
        Assert.Equal([(f.Target1, 0)], f.Repo.GetLinkEdges(f.Relation, f.First));
    }

    [SkippableFact]
    public void Link_undo_refuses_schema_changes_and_structured_many_to_single_does_not_truncate()
    {
        using var f = new Fixture();
        f.Repo.SetLinks(f.Relation, f.First, [f.Target1]);
        var changed = f.Repo.SetLinksBulk(f.Collection, [ConstructorRepository.LinkWrite.FromTargets(f.First, f.Relation, [new(0, f.Target2)])]);
        var field = f.Repo.GetFields(f.Collection).Single(c => c.Id == f.Relation);
        field.ConfigJson = new RelationConfig(f.Targets, false, "semicolon").ToConfigJson();
        f.Repo.UpdateField(field);
        var undo = f.Repo.SetLinksBulk(f.Collection, Undo(changed));
        Assert.Equal(0, undo.Changed); Assert.Equal(1, undo.Conflicts);
        Assert.Equal([f.Target2], f.Repo.GetLinkTargetIds(f.Relation, f.First));
        f.Repo.SetLinks(f.Relation, f.First, []);
        field.ConfigJson = new RelationConfig(f.Targets, true).ToConfigJson();
        f.Repo.UpdateField(field);
        var rejected = f.Repo.SetLinksBulk(f.Collection,
            [ConstructorRepository.LinkWrite.FromTargets(f.First, f.Relation, [new(0, f.Target1), new(0, f.Target2)])]);
        Assert.Equal(0, rejected.Changed); Assert.Equal(1, rejected.Conflicts);
        Assert.Empty(f.Repo.GetLinkEdges(f.Relation, f.First));
    }

    [SkippableFact]
    public void Link_bulk_joins_the_outer_transaction_and_rollback_keeps_edges_and_journal()
    {
        using var f = new Fixture();
        f.Repo.SetLinks(f.Relation, f.First, [f.Target1]);
        using var before = f.Read();
        var logCount = before.ChangeLog.Count();
        using (var transaction = f.Manager.Context.Database.BeginTransaction())
        {
            var changed = f.Repo.SetLinksBulk(f.Collection,
                [ConstructorRepository.LinkWrite.FromTargets(f.First, f.Relation, [new(0, f.Target2)])]);
            Assert.Equal(1, changed.Changed);
            Assert.Equal([f.Target2], f.Repo.GetLinkTargetIds(f.Relation, f.First));
            transaction.Rollback();
        }
        f.Manager.Context.ChangeTracker.Clear();
        using var read = f.Read();
        Assert.Equal(logCount, read.ChangeLog.Count());
        Assert.Equal([f.Target1], f.Repo.GetLinkTargetIds(f.Relation, f.First));
    }

    [SkippableFact]
    public void Cell_bulk_database_failure_rolls_back_all_values_titles_and_journal()
    {
        using var f = new Fixture();
        using var before = f.Read();
        var logCount = before.ChangeLog.Count();
        f.Manager.Context.Database.ExecuteSqlRaw("""
            CREATE FUNCTION reject_cell_bulk_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'forced test rollback'; END; $$;
            CREATE TRIGGER reject_cell_bulk_audit BEFORE UPDATE ON db_records
            FOR EACH ROW WHEN (NEW.updatedat IS DISTINCT FROM OLD.updatedat)
            EXECUTE FUNCTION reject_cell_bulk_audit();
            """);
        try
        {
            Assert.ThrowsAny<Exception>(() => f.Repo.SetCellsBulk(f.Collection,
                [new(f.First, f.Text, "Нельзя сохранить"), new(f.Second, f.Text, "Тоже нельзя")], [new(f.First, "Нельзя переименовать")]));
        }
        finally
        {
            f.Manager.Context.Database.ExecuteSqlRaw("DROP TRIGGER reject_cell_bulk_audit ON db_records; DROP FUNCTION reject_cell_bulk_audit();");
        }
        using var read = f.Read();
        Assert.Equal("Было А", read.DbFieldValues.Single(v => v.RecordId == f.First && v.FieldId == f.Text).Value);
        Assert.Equal("Было Б", read.DbFieldValues.Single(v => v.RecordId == f.Second && v.FieldId == f.Text).Value);
        Assert.Equal("Первая", read.DbRecords.Single(r => r.Id == f.First).Title);
        Assert.Equal(logCount, read.ChangeLog.Count());
    }

    [SkippableFact]
    public async Task Isolated_postgres_bulk_paths_return_complete_undo_snapshots()
    {
        using var f = new Fixture();
        var records = SampleData.AddRecords(f.Repo, f.Collection, 24);
        var values = await f.Repo.SetCellsBulkIsolatedAsync(f.Collection,
            records.Select(r => new ConstructorRepository.CellWrite(r.Id!.Value, f.Text, "Новое")).ToList(), null, "Вставка");
        Assert.Equal(records.Count, values.Changed);
        var links = await f.Repo.SetLinksBulkIsolatedAsync(f.Collection,
            records.Select(r => ConstructorRepository.LinkWrite.FromTargets(r.Id!.Value, f.Relation, [new(0, f.Target1), new(0, f.Target2)])).ToList(), "Вставка");
        Assert.Equal(records.Count, links.Changed);
        Assert.All(links.Changes, c => Assert.Equal([new RelationTarget(0, f.Target1), new RelationTarget(0, f.Target2)], c.NewTargets));
        var undone = await f.Repo.SetLinksBulkIsolatedAsync(f.Collection, Undo(links), "Отмена");
        Assert.Equal(records.Count, undone.Changed);
        using var read = f.Read();
        var ids = records.Select(r => r.Id!.Value).ToList();
        Assert.False(read.DbRecordLinks.Any(l => l.FieldId == f.Relation && ids.Contains(l.FromRecordId)));
    }

    private static List<ConstructorRepository.LinkWrite> Undo(ConstructorRepository.LinkBulkResult result) =>
        result.Changes.Select(c => ConstructorRepository.LinkWrite.FromTargets(c.RecordId, c.FieldId, c.OldTargets,
            ConstructorRepository.LinkTargetsKey(c.NewTargets), c.Config)).ToList();
}
