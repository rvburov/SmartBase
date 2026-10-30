using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

public sealed class RelationComputedTests
{
    private sealed record Setup(int Source, int Target, int Relation, int Number, int Date, int Record, int A, int B);

    private static Setup Seed(ConstructorRepository repo)
    {
        var target = SampleData.AddCollection(repo, "Счета").Id!.Value;
        var source = SampleData.AddCollection(repo, "Клиенты").Id!.Value;
        var number = SampleData.AddField(repo, target, "Сумма", "number");
        var date = SampleData.AddField(repo, target, "Дата", "date");
        var relation = SampleData.AddField(repo, source, "Счета", "relation");
        relation.ConfigJson = new RelationConfig(target, false).ToConfigJson();
        repo.UpdateField(relation);
        var a = SampleData.AddRecord(repo, target, "Первый").Id!.Value;
        var b = SampleData.AddRecord(repo, target, "Второй").Id!.Value;
        repo.SetFieldValue(a, number.Id!.Value, "1,25");
        repo.SetFieldValue(b, number.Id.Value, "10,5");
        repo.SetFieldValue(a, date.Id!.Value, "2026-10-01");
        repo.SetFieldValue(b, date.Id.Value, "2026-10-02");
        var record = SampleData.AddRecord(repo, source, "Клиент").Id!.Value;
        repo.SetLinks(relation.Id!.Value, record, [b, a]);
        return new(source, target, relation.Id.Value, number.Id.Value, date.Id.Value, record, a, b);
    }

    private static DbField Computed(ConstructorRepository repo, Setup s, string type, string target, string aggregate = "lookup") =>
        repo.AddField(new DbField
        {
            CollectionId = s.Source, Name = type + " " + aggregate + " " + target,
            FieldType = type, ConfigJson = new RelationComputedConfig(s.Relation, target, aggregate).ToConfigJson(),
        });

    [Fact]
    public void Lookup_formats_target_values_preserves_link_order_and_observes_later_edits()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var s = Seed(repo);
        var lookup = Computed(repo, s, "lookup", s.Date.ToString());
        var number = Computed(repo, s, "lookup", s.Number.ToString());
        var before = repo.GetRelationComputedValues([s.Record]);
        Assert.Equal("02.10.2026, 01.10.2026", before[s.Record][lookup.Id!.Value]);
        Assert.Equal("10,5, 1,25", before[s.Record][number.Id!.Value]);
        repo.SetFieldValue(s.A, s.Date, "2026-11-03");
        Assert.Equal("02.10.2026, 03.11.2026", repo.GetRelationComputedValues([s.Record])[s.Record][lookup.Id.Value]);
        Assert.False(db.Context.DbFieldValues.Any(v => v.FieldId == lookup.Id || v.FieldId == number.Id));
    }

    [Fact]
    public void Lookup_raw_values_keep_the_target_type_separate_from_formatted_display()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var s = Seed(repo);
        var lookup = Computed(repo, s, "lookup", s.Number.ToString());
        repo.SetLinks(s.Relation, s.Record, [s.A]);
        var raw = repo.GetRelationComputedRawValues([s.Record], lookup)[s.Record];
        Assert.Equal("number", raw.FieldType);
        Assert.Equal("1,25", raw.Raw);
        Assert.Equal("1,25", raw.Display);
        var number = repo.GetFields(s.Target).Single(f => f.Id == s.Number);
        // Смена показа не меняет сырое число, по которому автосвязь строит свой ключ.
        number.ConfigJson = System.Text.Json.JsonSerializer.Serialize(NumberFormatValue.ToConfigObject(
            new NumberFormat(NumberFormatKind.Currency, 2, true, "₽", true)));
        repo.UpdateField(number);
        var formatted = repo.GetRelationComputedRawValues([s.Record], lookup)[s.Record];
        Assert.Equal(raw.Raw, formatted.Raw);
        Assert.Contains("₽", formatted.Display);
    }

    [Fact]
    public void Rollup_computes_all_numeric_aggregates_and_counts_targets_with_empty_values()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var s = Seed(repo);
        var count = Computed(repo, s, "rollup", "__title__", "count");
        var sum = Computed(repo, s, "rollup", s.Number.ToString(), "sum");
        var avg = Computed(repo, s, "rollup", s.Number.ToString(), "avg");
        var min = Computed(repo, s, "rollup", s.Number.ToString(), "min");
        var max = Computed(repo, s, "rollup", s.Number.ToString(), "max");
        var values = repo.GetRelationComputedValues([s.Record])[s.Record];
        Assert.Equal("2", values[count.Id!.Value]);
        Assert.Equal("11.75", values[sum.Id!.Value]);
        Assert.Equal("5.875", values[avg.Id!.Value]);
        Assert.Equal("1.25", values[min.Id!.Value]);
        Assert.Equal("10.5", values[max.Id!.Value]);
        repo.SetFieldValue(s.B, s.Number, "");
        var changed = repo.GetRelationComputedValues([s.Record])[s.Record];
        Assert.Equal("2", changed[count.Id.Value]);
        Assert.Equal("1.25", changed[sum.Id.Value]);
        repo.SetLinks(s.Relation, s.Record, []);
        Assert.Equal("0", repo.GetRelationComputedValues([s.Record])[s.Record][count.Id.Value]);
    }

    [Fact]
    public void Computed_values_obey_the_field_window_and_never_come_from_stale_scalar_storage()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var s = Seed(repo);
        var count = Computed(repo, s, "rollup", "__title__", "count");
        var lookup = Computed(repo, s, "lookup", "__title__");
        var window = repo.GetFieldValuesBatch([s.Record], [count.Id!.Value]);
        Assert.Equal("2", window[s.Record][count.Id.Value]);
        Assert.False(window[s.Record].ContainsKey(lookup.Id!.Value));
        Assert.Empty(repo.GetFieldValuesBatch([s.Record], []));
        repo.SetLinks(s.Relation, s.Record, [s.B]);
        Assert.Equal("1", repo.GetFieldValuesBatch([s.Record], [count.Id.Value])[s.Record][count.Id.Value]);
    }

    [Fact]
    public void Validation_rejects_a_cycle_and_runtime_guard_handles_existing_damaged_configuration()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var collection = SampleData.AddCollection(repo, "Цикл").Id!.Value;
        var relation = SampleData.AddField(repo, collection, "Связь", "relation");
        relation.ConfigJson = new RelationConfig(collection, false).ToConfigJson();
        repo.UpdateField(relation);
        var lookup = repo.AddField(new DbField { CollectionId = collection, Name = "Цикл", FieldType = "text" });
        lookup.FieldType = "lookup";
        lookup.ConfigJson = new RelationComputedConfig(relation.Id!.Value, lookup.Id!.Value.ToString()).ToConfigJson();
        Assert.Throws<InvalidOperationException>(() => repo.ValidateRelationComputedField(lookup));

        var a = SampleData.AddRecord(repo, collection, "А").Id!.Value;
        var b = SampleData.AddRecord(repo, collection, "Б").Id!.Value;
        repo.SetLinks(relation.Id.Value, a, [b]);
        repo.SetLinks(relation.Id.Value, b, [a]);
        // Имитация уже поврежденной базы: в штатном сохранении конфиг отбит валидатором.
        db.Context.Database.ExecuteSqlInterpolated($"UPDATE db_fields SET FieldType = 'lookup', ConfigJson = {lookup.ConfigJson} WHERE Id = {lookup.Id.Value}");
        Assert.Equal(RelationProperty.Cycle, repo.GetRelationComputedValues([a])[a][lookup.Id.Value]);
    }

    [Fact]
    public void Legacy_foreign_title_and_count_use_the_actual_target_collection()
    {
        using var local = new TempDatabase(1, "local");
        using var remote = new TempDatabase(2, "remote");
        var repo = local.Constructor;
        using var resolver = RelationDbResolver.UseScopedResolver(id => id == 1 ? repo : id == 2 ? remote.Constructor : null,
            current => current.DbPath == local.Path ? 1 : 2);
        var configured = SampleData.AddCollection(repo, "Настроенная цель").Id!.Value;
        SampleData.AddCollection(remote.Constructor, "Другая коллекция");
        var actual = SampleData.AddCollection(remote.Constructor, "Реальная цель").Id!.Value;
        Assert.NotEqual(configured, actual);
        var target = SampleData.AddRecord(remote.Constructor, actual, "Старая внешняя цель").Id!.Value;
        var source = SampleData.AddCollection(repo, "Источники").Id!.Value;
        var record = SampleData.AddRecord(repo, source, "Источник").Id!.Value;
        var relation = repo.AddField(new DbField { CollectionId = source, Name = "Связь", FieldType = "relation",
            ConfigJson = new RelationConfig(configured, false).ToConfigJson() });
        local.Context.DbRecordLinks.Add(new SmartBase.Database.Entities.DbRecordLinkEntity
        { FieldId = relation.Id!.Value, FromRecordId = record, ToRecordId = target, TargetDbId = 2, Position = 1000 });
        local.Context.SaveChanges();
        var lookup = repo.AddField(new DbField { CollectionId = source, Name = "Названия", FieldType = "lookup",
            ConfigJson = new RelationComputedConfig(relation.Id.Value).ToConfigJson() });
        var count = repo.AddField(new DbField { CollectionId = source, Name = "Количество", FieldType = "rollup",
            ConfigJson = new RelationComputedConfig(relation.Id.Value, "__title__", "count").ToConfigJson() });
        var values = repo.GetRelationComputedValues([record])[record];
        Assert.Equal("Старая внешняя цель", values[lookup.Id!.Value]);
        Assert.Equal("1", values[count.Id!.Value]);

    }

    [Fact]
    public void Numeric_target_property_never_reads_a_colliding_field_from_a_legacy_edge_database()
    {
        using var local = new TempDatabase(1, "local");
        using var remote = new TempDatabase(2, "remote");
        var repo = local.Constructor;
        using var resolver = RelationDbResolver.UseScopedResolver(id => id == 1 ? repo : id == 2 ? remote.Constructor : null,
            current => current.DbPath == local.Path ? 1 : 2);
        var localTarget = SampleData.AddCollection(repo, "Старые цели").Id!.Value;
        var remoteTarget = SampleData.AddCollection(remote.Constructor, "Новые цели").Id!.Value;
        var localProperty = SampleData.AddField(repo, localTarget, "Местный код");
        var remoteProperty = SampleData.AddField(remote.Constructor, remoteTarget, "Удаленный код");
        Assert.Equal(localTarget, remoteTarget);
        Assert.Equal(localProperty.Id, remoteProperty.Id);
        var oldTarget = SampleData.AddRecord(repo, localTarget, "Старая цель").Id!.Value;
        var newTarget = SampleData.AddRecord(remote.Constructor, remoteTarget, "Новая цель").Id!.Value;
        repo.SetFieldValue(oldTarget, localProperty.Id!.Value, "Чужое свойство с тем же номером");
        remote.Constructor.SetFieldValue(newTarget, remoteProperty.Id!.Value, "Правильный код");
        var source = SampleData.AddCollection(repo, "Источники").Id!.Value;
        var record = SampleData.AddRecord(repo, source, "Источник").Id!.Value;
        var relation = repo.AddField(new DbField { CollectionId = source, Name = "Связь", FieldType = "relation",
            ConfigJson = new RelationConfig(remoteTarget, false, TargetDbId: 2).ToConfigJson() });
        local.Context.DbRecordLinks.Add(new SmartBase.Database.Entities.DbRecordLinkEntity
        { FieldId = relation.Id!.Value, FromRecordId = record, ToRecordId = oldTarget, TargetDbId = 0, Position = 1000 });
        local.Context.SaveChanges();
        var lookup = repo.AddField(new DbField { CollectionId = source, Name = "Код", FieldType = "lookup",
            ConfigJson = new RelationComputedConfig(relation.Id.Value, remoteProperty.Id.Value.ToString()).ToConfigJson() });

        var blocked = repo.GetRelationComputedRawValues([record], lookup)[record];
        Assert.Equal(RelationProperty.MissingField, blocked.Display);
        Assert.Empty(blocked.Raw);
        Assert.Empty(blocked.FieldType);
        repo.SetLinkEdges(relation.Id.Value, record, [new(2, newTarget)]);
        Assert.Equal("Правильный код", repo.GetRelationComputedValues([record])[record][lookup.Id!.Value]);
    }

    [Fact]
    public void An_unavailable_database_marks_only_the_records_that_link_into_it()
    {
        // Значение записи не зависит от соседей по пакету: страница, карточка и столбец целиком
        // показывают одно и то же. Ребро в базу, которой нет, дает подпись только своей записи.
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var s = Seed(repo);
        var lookup = Computed(repo, s, "lookup", "__title__");
        var count = Computed(repo, s, "rollup", "__title__", "count");
        var linked = SampleData.AddRecord(repo, s.Source, "Со ссылкой в пропавшую базу").Id!.Value;
        var plain = SampleData.AddRecord(repo, s.Source, "Без ребер").Id!.Value;
        repo.SetLinks(s.Relation, linked, [s.A]);
        db.Context.DbRecordLinks.Add(new SmartBase.Database.Entities.DbRecordLinkEntity
            { FieldId = s.Relation, FromRecordId = linked, ToRecordId = s.B, TargetDbId = 1_234_567, Position = 2000 });
        db.Context.SaveChanges();
        var batch = repo.GetRelationComputedValues([s.Record, linked, plain]);
        Assert.StartsWith("(база", batch[linked][lookup.Id!.Value], StringComparison.Ordinal);
        Assert.Equal(batch[linked][lookup.Id.Value], batch[linked][count.Id!.Value]);
        Assert.Equal("Второй, Первый", batch[s.Record][lookup.Id.Value]);
        Assert.Equal("2", batch[s.Record][count.Id.Value]);
        Assert.Equal("", batch[plain][lookup.Id.Value]);
        Assert.Equal("0", batch[plain][count.Id.Value]);
        foreach (var id in new[] { s.Record, linked, plain })
            Assert.Equal(batch[id], repo.GetRelationComputedValues([id])[id]);
        var column = repo.GetRelationComputedColumnValues(s.Source, lookup);
        Assert.Equal(batch[s.Record][lookup.Id.Value], column[s.Record]);
        Assert.Equal(batch[linked][lookup.Id.Value], column[linked]);
        Assert.Equal("", column[plain]);
    }

    [SkippableFact]
    public void Related_values_are_equal_on_SQLite_and_PostgreSQL()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        List<string> Scenario(ConstructorRepository repo)
        {
            var s = Seed(repo);
            var lookup = Computed(repo, s, "lookup", s.Date.ToString());
            var sum = Computed(repo, s, "rollup", s.Number.ToString(), "sum");
            var count = Computed(repo, s, "rollup", "__title__", "count");
            var values = repo.GetRelationComputedValues([s.Record])[s.Record];
            return [values[lookup.Id!.Value], values[sum.Id!.Value], values[count.Id!.Value]];
        }
        List<string> lite;
        using (var db = new TempDatabase()) lite = Scenario(db.Constructor);
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        using (var connection = new NpgsqlConnection(cs))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
            cmd.ExecuteNonQuery();
        }
        using var manager = DatabaseManager.Open(101, "pg-relation-computed", cs, provider: "postgres");
        Assert.Equal(lite, Scenario(manager.Constructor));
    }
}
