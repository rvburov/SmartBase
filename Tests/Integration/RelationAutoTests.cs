using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

public sealed class RelationAutoTests
{
    private sealed class Fixture : IDisposable
    {
        public TempDatabase Database { get; } = new();
        public ConstructorRepository Repo => Database.Constructor;
        public int Source { get; }
        public int Target { get; }
        public int SourceKey { get; }
        public int TargetKey { get; }
        public int Relation { get; }
        public Fixture(string type = "text")
        {
            Source = SampleData.AddCollection(Repo, "Заказы").Id!.Value;
            Target = SampleData.AddCollection(Repo, "Клиенты").Id!.Value;
            SourceKey = SampleData.AddField(Repo, Source, "Ключ заказа", type).Id!.Value;
            TargetKey = SampleData.AddField(Repo, Target, "Ключ клиента", type).Id!.Value;
            var relation = SampleData.AddField(Repo, Source, "Клиент", "relation");
            relation.ConfigJson = new RelationConfig(Target, false).ToConfigJson();
            Repo.UpdateField(relation);
            Relation = relation.Id!.Value;
        }
        public int AddSource(string value, string title = "Заказ")
        {
            var id = SampleData.AddRecord(Repo, Source, title).Id!.Value;
            Repo.SetFieldValue(id, SourceKey, value);
            return id;
        }
        public int AddTarget(string value, string title = "Клиент")
        {
            var id = SampleData.AddRecord(Repo, Target, title).Id!.Value;
            Repo.SetFieldValue(id, TargetKey, value);
            return id;
        }
        public DbField Proposed(bool single = false) => new()
        {
            Id = Relation, CollectionId = Source, Name = "Клиент", FieldType = "relation", Position = 1000,
            ConfigJson = new RelationConfig(Target, single, Auto: new(SourceKey.ToString(), TargetKey.ToString())).ToConfigJson(),
        };
        public void Dispose() => Database.Dispose();
    }

    [Fact]
    public void Preview_is_read_only_and_save_materializes_every_matching_target()
    {
        using var f = new Fixture();
        var order = f.AddSource("7701");
        var first = f.AddTarget("7701", "Клиент, основной");
        var second = f.AddTarget("7701", "Другой клиент");
        f.AddTarget("7702");
        var field = f.Proposed();
        var preview = f.Repo.PreviewAutoRelation(field);
        Assert.True(preview.CanApply);
        Assert.Equal(1, preview.Added);
        Assert.Empty(f.Repo.GetLinkEdges(f.Relation, order));
        Assert.Null(RelationConfig.FromConfig(f.Repo.GetFields(f.Source).Single(x => x.Id == f.Relation).ConfigJson)!.Auto);
        f.Repo.SaveAutoRelationField(field, preview);
        Assert.Equal([first, second], f.Repo.GetLinkTargetIds(f.Relation, order));
        Assert.NotNull(RelationConfig.FromConfig(f.Repo.GetFields(f.Source).Single(x => x.Id == f.Relation).ConfigJson)!.Auto);
    }

    [Fact]
    public void Ambiguous_single_does_not_partially_write_other_unambiguous_rows_or_the_rule()
    {
        using var f = new Fixture();
        var ambiguousOrder = f.AddSource("same");
        var clearOrder = f.AddSource("unique");
        var old = f.AddTarget("old");
        f.AddTarget("same");
        f.AddTarget("same");
        f.AddTarget("unique");
        f.Repo.SetLinks(f.Relation, ambiguousOrder, [old]);
        var field = f.Proposed(single: true);
        var preview = f.Repo.PreviewAutoRelation(field);
        Assert.Equal(RelationAutoStatus.Ambiguous, preview.Status);
        Assert.Equal(1, preview.Ambiguous);
        Assert.False(preview.CanApply);
        Assert.Throws<RelationAutoApplyException>(() => f.Repo.SaveAutoRelationField(field, preview));
        Assert.Equal([old], f.Repo.GetLinkTargetIds(f.Relation, ambiguousOrder));
        Assert.Empty(f.Repo.GetLinkEdges(f.Relation, clearOrder));
        Assert.Null(RelationConfig.FromConfig(f.Repo.GetFields(f.Source).Single(x => x.Id == f.Relation).ConfigJson)!.Auto);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("links")]
    public void Snapshot_rejects_source_target_or_link_changes_after_preview(string changed)
    {
        using var f = new Fixture();
        var order = f.AddSource("same");
        var target = f.AddTarget("same");
        var other = f.AddTarget("other");
        var field = f.Proposed();
        var preview = f.Repo.PreviewAutoRelation(field);
        if (changed == "source") f.Repo.SetFieldValue(order, f.SourceKey, "other");
        else if (changed == "target") f.Repo.SetFieldValue(target, f.TargetKey, "other");
        else f.Repo.SetLinks(f.Relation, order, [other]);
        var error = Assert.Throws<RelationAutoApplyException>(() => f.Repo.SaveAutoRelationField(field, preview));
        Assert.Equal(RelationAutoStatus.Conflict, error.Status);
        Assert.Equal(changed == "links" ? new[] { other } : [], f.Repo.GetLinkTargetIds(f.Relation, order));
        Assert.Null(RelationConfig.FromConfig(f.Repo.GetFields(f.Source).Single(x => x.Id == f.Relation).ConfigJson)!.Auto);
    }

    [Fact]
    public void Missing_or_incompatible_property_is_not_treated_as_a_successful_empty_result()
    {
        using var f = new Fixture();
        var order = f.AddSource("same");
        var target = f.AddTarget("same");
        f.Repo.SetLinks(f.Relation, order, [target]);
        var field = f.Proposed();
        field.ConfigJson = new RelationConfig(f.Target, false, Auto: new(f.SourceKey.ToString(), "2147483647")).ToConfigJson();
        Assert.Equal(RelationAutoStatus.InvalidRule, f.Repo.PreviewAutoRelation(field).Status);
        var numeric = SampleData.AddField(f.Repo, f.Target, "Числовой ключ", "number").Id!.Value;
        field.ConfigJson = new RelationConfig(f.Target, false, Auto: new(f.SourceKey.ToString(), numeric.ToString())).ToConfigJson();
        Assert.Equal(RelationAutoStatus.InvalidRule, f.Repo.PreviewAutoRelation(field).Status);
        Assert.Equal([target], f.Repo.GetLinkTargetIds(f.Relation, order));
    }

    [Fact]
    public void Concurrent_field_flags_are_not_overwritten_by_an_older_auto_preview()
    {
        using var f = new Fixture();
        var order = f.AddSource("next");
        var old = f.AddTarget("old");
        f.AddTarget("next");
        f.Repo.SetLinks(f.Relation, order, [old]);
        var proposed = f.Proposed();
        var preview = f.Repo.PreviewAutoRelation(proposed);
        var current = f.Repo.GetFields(f.Source).Single(x => x.Id == f.Relation);
        current.IsRequired = true;
        f.Repo.UpdateField(current);
        var error = Assert.Throws<RelationAutoApplyException>(() => f.Repo.SaveAutoRelationField(proposed, preview));
        Assert.Equal(RelationAutoStatus.Conflict, error.Status);
        var saved = f.Repo.GetFields(f.Source).Single(x => x.Id == f.Relation);
        Assert.True(saved.IsRequired);
        Assert.Null(RelationConfig.FromConfig(saved.ConfigJson)!.Auto);
        Assert.Equal([old], f.Repo.GetLinkTargetIds(f.Relation, order));
    }

    [Fact]
    public void Empty_keys_do_not_match_each_other_but_false_is_a_real_value()
    {
        using var f = new Fixture("checkbox");
        var blank = f.AddSource("");
        var no = f.AddSource("0");
        f.AddTarget("");
        var noTarget = f.AddTarget("0");
        var field = f.Proposed();
        f.Repo.SaveAutoRelationField(field, f.Repo.PreviewAutoRelation(field));
        Assert.Empty(f.Repo.GetLinkEdges(f.Relation, blank));
        Assert.Equal([noTarget], f.Repo.GetLinkTargetIds(f.Relation, no));
    }

    [Fact]
    public void Failed_new_field_write_rolls_back_the_created_field_and_all_edges()
    {
        using var f = new Fixture();
        var order = f.AddSource("same");
        f.AddTarget("same");
        var field = f.Proposed();
        field.Id = null;
        field.Name = "Автоматический клиент";
        var before = f.Repo.GetFields(f.Source).Count;
        var preview = f.Repo.PreviewAutoRelation(field);
        f.Database.Context.Database.ExecuteSqlRaw($"CREATE TRIGGER test_auto_failure BEFORE INSERT ON db_record_links WHEN NEW.FieldId != {f.Relation} BEGIN SELECT RAISE(ABORT, 'test auto failure'); END;");
        Assert.ThrowsAny<DbException>(() => f.Repo.SaveAutoRelationField(field, preview));
        Assert.Null(field.Id);
        Assert.Equal(before, f.Repo.GetFields(f.Source).Count);
        Assert.Empty(f.Repo.GetLinkEdges(f.Relation, order));
        Assert.Empty(f.Database.Context.DbRecordLinks.AsNoTracking().ToList());
    }

    [Fact]
    public void Cancellation_leaves_new_field_and_previous_links_untouched()
    {
        using var f = new Fixture();
        var order = f.AddSource("same");
        var target = f.AddTarget("same");
        f.Repo.SetLinks(f.Relation, order, [target]);
        var field = f.Proposed();
        field.Id = null;
        field.Name = "Отмененный клиент";
        var preview = f.Repo.PreviewAutoRelation(field);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Equal(RelationAutoStatus.Cancelled, f.Repo.PreviewAutoRelation(field, cancel.Token).Status);
        Assert.ThrowsAny<OperationCanceledException>(() => f.Repo.SaveAutoRelationField(field, preview, cancel.Token));
        Assert.DoesNotContain(f.Repo.GetFields(f.Source), x => x.Name == field.Name);
        Assert.Equal([target], f.Repo.GetLinkTargetIds(f.Relation, order));
    }

    [Fact]
    public void Outer_transaction_rollback_restores_the_rule_and_edges_and_reads_its_own_changes()
    {
        using var f = new Fixture();
        var order = f.AddSource("old");
        var old = f.AddTarget("old");
        var next = f.AddTarget("next");
        f.Repo.SetLinks(f.Relation, order, [old]);
        var field = f.Proposed();
        Assert.Throws<InvalidOperationException>(() => f.Repo.InWriteTransaction<int>(() =>
        {
            f.Repo.SetFieldValue(order, f.SourceKey, "next");
            var preview = f.Repo.PreviewAutoRelation(field);
            Assert.True(preview.CanApply);
            f.Repo.SaveAutoRelationField(field, preview);
            Assert.Equal([next], f.Repo.GetLinkTargetIds(f.Relation, order));
            throw new InvalidOperationException("rollback test");
        }));
        Assert.Equal([old], f.Repo.GetLinkTargetIds(f.Relation, order));
        Assert.Null(RelationConfig.FromConfig(f.Repo.GetFields(f.Source).Single(x => x.Id == f.Relation).ConfigJson)!.Auto);
    }

    [Fact]
    public void Failed_nested_save_rolls_back_to_savepoint_while_outer_work_can_commit()
    {
        using var f = new Fixture();
        var order = f.AddSource("next");
        var old = f.AddTarget("old");
        f.AddTarget("next");
        f.Repo.SetLinks(f.Relation, order, [old]);
        var preserved = f.Repo.InWriteTransaction(() =>
        {
            var record = SampleData.AddRecord(f.Repo, f.Source, "Сохраненная внешняя запись").Id!.Value;
            var field = f.Proposed();
            var preview = f.Repo.PreviewAutoRelation(field);
            f.Database.Context.Database.ExecuteSqlRaw($"CREATE TRIGGER test_nested_auto_failure BEFORE INSERT ON db_record_links WHEN NEW.FieldId = {f.Relation} BEGIN SELECT RAISE(ABORT, 'nested auto failure'); END;");
            Assert.ThrowsAny<DbException>(() => f.Repo.SaveAutoRelationField(field, preview));
            Assert.Equal([old], f.Repo.GetLinkTargetIds(f.Relation, order));
            Assert.Null(RelationConfig.FromConfig(f.Repo.GetFields(f.Source).Single(x => x.Id == f.Relation).ConfigJson)!.Auto);
            return record;
        });
        Assert.True(f.Database.Context.DbRecords.AsNoTracking().Any(r => r.Id == preserved));
        Assert.Equal([old], f.Repo.GetLinkTargetIds(f.Relation, order));
        Assert.Null(RelationConfig.FromConfig(f.Repo.GetFields(f.Source).Single(x => x.Id == f.Relation).ConfigJson)!.Auto);
    }

    [Fact]
    public void Background_preview_and_save_reject_an_uncommitted_outer_transaction()
    {
        using var f = new Fixture();
        f.AddSource("same");
        f.AddTarget("same");
        var field = f.Proposed();
        var preview = f.Repo.PreviewAutoRelation(field);
        f.Repo.InWriteTransaction(() =>
        {
            Assert.Equal(RelationAutoStatus.Conflict, f.Repo.PreviewAutoRelationAsync(field).GetAwaiter().GetResult().Status);
            var error = Assert.Throws<RelationAutoApplyException>(() =>
                f.Repo.SaveAutoRelationFieldAsync(field, preview).GetAwaiter().GetResult());
            Assert.Equal(RelationAutoStatus.Conflict, error.Status);
            return 1;
        });
        Assert.Null(RelationConfig.FromConfig(f.Repo.GetFields(f.Source).Single(x => x.Id == f.Relation).ConfigJson)!.Auto);
    }

    [Fact]
    public async Task Async_save_and_rescan_do_not_leave_stale_metadata_or_links_in_the_callers_tracker()
    {
        using var f = new Fixture();
        var order = f.AddSource("same");
        var first = f.AddTarget("same");
        var second = f.AddTarget("other");
        f.Repo.SetLinks(f.Relation, order, [second]);
        var field = f.Proposed();
        var preview = await f.Repo.PreviewAutoRelationAsync(field);
        await f.Repo.SaveAutoRelationFieldAsync(field, preview);
        Assert.Equal([first], f.Repo.GetLinkTargetIds(f.Relation, order));
        Assert.DoesNotContain(f.Database.Context.ChangeTracker.Entries<SmartBase.Database.Entities.DbRecordLinkEntity>(),
            e => e.Entity.FieldId == f.Relation);
        f.Repo.SetFieldValue(order, f.SourceKey, "other");
        f.Repo.RefreshAutoRelationsForChange(f.Source, f.SourceKey, order);
        Assert.Equal([second], f.Repo.GetLinkTargetIds(f.Relation, order));
        f.Repo.SetFieldValue(second, f.TargetKey, "gone");
        f.Repo.RefreshAutoRelationsForChange(f.Target, f.TargetKey, second);
        Assert.Empty(f.Repo.GetLinkTargetIds(f.Relation, order));
    }

    [Fact]
    public void Direct_self_dependency_is_rejected_even_in_an_empty_collection()
    {
        using var f = new Fixture();
        var field = f.Proposed();
        field.ConfigJson = new RelationConfig(f.Target, false, Auto: new(f.Relation.ToString(), f.TargetKey.ToString())).ToConfigJson();
        Assert.Equal(RelationAutoStatus.InvalidRule, f.Repo.PreviewAutoRelation(field).Status);
    }

    [Fact]
    public void Lookup_back_to_the_same_auto_relation_is_an_indirect_cycle()
    {
        using var f = new Fixture();
        var lookup = f.Repo.AddField(new DbField
        {
            CollectionId = f.Source, Name = "Ключ выбранного клиента", FieldType = "lookup",
            ConfigJson = new RelationComputedConfig(f.Relation, f.TargetKey.ToString()).ToConfigJson(),
        });
        var field = f.Proposed();
        field.ConfigJson = new RelationConfig(f.Target, false,
            Auto: new(lookup.Id!.Value.ToString(), f.TargetKey.ToString())).ToConfigJson();
        Assert.Equal(RelationAutoStatus.InvalidRule, f.Repo.PreviewAutoRelation(field).Status);
    }

    [Fact]
    public void Two_auto_relations_cannot_use_each_other_as_the_source_key()
    {
        using var f = new Fixture();
        var targetKey = f.Repo.AddField(new DbField
        {
            CollectionId = f.Target, Name = "Обратная ссылка", FieldType = "relation",
            ConfigJson = new RelationConfig(f.Source, false).ToConfigJson(),
        });
        var other = f.Repo.AddField(new DbField
        {
            CollectionId = f.Source, Name = "Другой клиент", FieldType = "relation",
            ConfigJson = new RelationConfig(f.Target, false).ToConfigJson(),
        });
        other.ConfigJson = new RelationConfig(f.Target, false,
            Auto: new(f.Relation.ToString(), targetKey.Id!.Value.ToString())).ToConfigJson();
        f.Repo.SaveAutoRelationField(other, f.Repo.PreviewAutoRelation(other));
        var field = f.Proposed();
        field.ConfigJson = new RelationConfig(f.Target, false,
            Auto: new(other.Id!.Value.ToString(), targetKey.Id.Value.ToString())).ToConfigJson();
        Assert.Equal(RelationAutoStatus.InvalidRule, f.Repo.PreviewAutoRelation(field).Status);
    }

    [Fact]
    public void Existing_rule_refresh_preserves_required_metadata()
    {
        using var f = new Fixture();
        var order = f.AddSource("same");
        f.AddTarget("same");
        var next = f.AddTarget("next");
        var field = f.Proposed();
        field.IsRequired = true;
        f.Repo.SaveAutoRelationField(field, f.Repo.PreviewAutoRelation(field));
        f.Repo.SetFieldValue(order, f.SourceKey, "next");
        var results = f.Repo.RefreshAutoRelationsForChange(f.Source, f.SourceKey, order);
        Assert.All(results, result => Assert.True(result.Applied, result.Message));
        Assert.Equal([next], f.Repo.GetLinkTargetIds(f.Relation, order));
        Assert.True(f.Repo.GetFields(f.Source).Single(x => x.Id == f.Relation).IsRequired);
        Assert.Throws<InvalidOperationException>(() => f.Repo.SetLinks(f.Relation, order, []));
    }

    [Fact]
    public async Task A_property_type_changed_after_async_preview_requires_a_new_preview()
    {
        using var f = new Fixture();
        var order = f.AddSource("same");
        f.AddTarget("same");
        var field = f.Proposed();
        var preview = await f.Repo.PreviewAutoRelationAsync(field);
        var key = f.Repo.GetFields(f.Target).Single(x => x.Id == f.TargetKey);
        key.FieldType = "select";
        key.ConfigJson = "{\"options\":[\"same\"]}";
        f.Repo.UpdateField(key);
        var error = await Assert.ThrowsAsync<RelationAutoApplyException>(() => f.Repo.SaveAutoRelationFieldAsync(field, preview));
        Assert.Equal(RelationAutoStatus.Conflict, error.Status);
        Assert.Empty(f.Repo.GetLinkEdges(f.Relation, order));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Empty_collections_still_check_the_selected_property_schema_snapshot(bool source)
    {
        using var f = new Fixture();
        var field = f.Proposed();
        var preview = f.Repo.PreviewAutoRelation(field);
        var key = f.Repo.GetFields(source ? f.Source : f.Target).Single(x => x.Id == (source ? f.SourceKey : f.TargetKey));
        key.FieldType = "select";
        key.ConfigJson = "{\"options\":[\"same\"]}";
        f.Repo.UpdateField(key);
        var error = Assert.Throws<RelationAutoApplyException>(() => f.Repo.SaveAutoRelationField(field, preview));
        Assert.Equal(RelationAutoStatus.Conflict, error.Status);
        Assert.Null(RelationConfig.FromConfig(f.Repo.GetFields(f.Source).Single(x => x.Id == f.Relation).ConfigJson)!.Auto);
    }

    [Fact]
    public async Task Cross_database_apply_uses_isolated_contexts_and_unavailability_preserves_edges()
    {
        using var f = new Fixture();
        using var other = new TempDatabase(dbId: 2);
        var targetCollection = SampleData.AddCollection(other.Constructor, "Внешние клиенты").Id!.Value;
        var key = SampleData.AddField(other.Constructor, targetCollection, "ИНН").Id!.Value;
        var target = SampleData.AddRecord(other.Constructor, targetCollection, "Внешний клиент").Id!.Value;
        other.Constructor.SetFieldValue(target, key, "7701");
        var order = f.AddSource("7701");
        var field = f.Proposed();
        field.ConfigJson = new RelationConfig(targetCollection, true, TargetDbId: 2,
            Auto: new(f.SourceKey.ToString(), key.ToString())).ToConfigJson();
        Task<RelationAutoPreview> previewTask;
        using (RelationDbResolver.UseScopedResolver(id => id == 2 ? other.Constructor : null))
            previewTask = f.Repo.PreviewAutoRelationAsync(field);
        var preview = await previewTask;
        Assert.True(preview.CanApply);
        Task<int> saveTask;
        using (RelationDbResolver.UseScopedResolver(id => id == 2 ? other.Constructor : null))
            saveTask = f.Repo.SaveAutoRelationFieldAsync(field, preview);
        await saveTask;
        Assert.Equal([(target, 2)], f.Repo.GetLinkEdges(f.Relation, order));
        using (RelationDbResolver.UseScopedResolver(_ => null))
        {
            Assert.Equal(RelationAutoStatus.Unavailable, f.Repo.ApplyAutoRelation(f.Relation).Status);
            Assert.Equal([(target, 2)], f.Repo.GetLinkEdges(f.Relation, order));
        }
    }

    [SkippableFact]
    public void Numeric_auto_matching_and_atomic_conflict_checks_work_on_PostgreSQL()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL (порт 5455) не поднят");
        var connection = PgTestServer.ConnectionString(PgTestServer.Database1);
        using (var conn = new NpgsqlConnection(connection))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
            cmd.ExecuteNonQuery();
        }
        using var manager = DatabaseManager.Open(101, "pg-auto-relation", connection, provider: "postgres");
        var repo = manager.Constructor;
        var source = SampleData.AddCollection(repo, "Заказы").Id!.Value;
        var target = SampleData.AddCollection(repo, "Клиенты").Id!.Value;
        var left = SampleData.AddField(repo, source, "Ключ", "number").Id!.Value;
        var right = SampleData.AddField(repo, target, "Ключ", "number").Id!.Value;
        var relation = SampleData.AddField(repo, source, "Клиент", "relation");
        relation.ConfigJson = new RelationConfig(target, false).ToConfigJson();
        repo.UpdateField(relation);
        var order = SampleData.AddRecord(repo, source, "Заказ").Id!.Value;
        var client = SampleData.AddRecord(repo, target, "Клиент").Id!.Value;
        repo.SetFieldValue(order, left, "1,00");
        repo.SetFieldValue(client, right, "1.0");
        relation.ConfigJson = new RelationConfig(target, true, Auto: new(left.ToString(), right.ToString())).ToConfigJson();
        Assert.Throws<InvalidOperationException>(() => repo.InWriteTransaction<int>(() =>
        {
            repo.SaveAutoRelationField(relation, repo.PreviewAutoRelation(relation));
            Assert.Equal([client], repo.GetLinkTargetIds(relation.Id!.Value, order));
            throw new InvalidOperationException("rollback test");
        }));
        Assert.Empty(repo.GetLinkEdges(relation.Id!.Value, order));
        Assert.Null(RelationConfig.FromConfig(repo.GetFields(source).Single(f => f.Id == relation.Id).ConfigJson)!.Auto);
        repo.SaveAutoRelationField(relation, repo.PreviewAutoRelation(relation));
        Assert.Equal([client], repo.GetLinkTargetIds(relation.Id!.Value, order));
        var preview = repo.PreviewAutoRelation(relation);
        repo.SetFieldValue(order, left, "2");
        Assert.Equal(RelationAutoStatus.Conflict, repo.ApplyAutoRelation(preview).Status);
    }
}
