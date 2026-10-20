using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

public sealed class RelationCopyProjectionTests
{
    [Theory]
    [InlineData("display", false)]
    [InlineData("display", true)]
    [InlineData("search", false)]
    [InlineData("search", true)]
    [InlineData("auto", false)]
    [InlineData("auto", true)]
    [InlineData("lookup", false)]
    [InlineData("lookup", true)]
    [InlineData("rollup", false)]
    [InlineData("rollup", true)]
    public void Collection_copy_refuses_to_apply_new_property_keys_to_targets_outside_the_copy(string projection, bool foreign)
    {
        using var database = new TempDatabase(101);
        var manager = database.Manager;
        var graph = Seed(manager, projection, foreign);
        var before = manager.Constructor.GetAllCollections().Select(c => c.Id).OrderBy(id => id).ToArray();

        Assert.Throws<InvalidOperationException>(() => manager.Constructor.DuplicateCollection(graph.Collection));

        Assert.Equal(before, manager.Constructor.GetAllCollections().Select(c => c.Id).OrderBy(id => id));
        AssertSourceIntact(manager, graph);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Title_and_count_projections_keep_legacy_mixed_database_edges(bool count)
    {
        using var database = new TempDatabase(101);
        var manager = database.Manager;
        var graph = Seed(manager, count ? "count" : "title", foreign: true);

        var copy = manager.Constructor.DuplicateCollection(graph.Collection).Id!.Value;
        var fields = manager.Constructor.GetFields(copy);
        var relation = fields.Single(f => f.Name == "Связь").Id!.Value;
        var records = manager.Constructor.GetRecords(copy).ToDictionary(r => r.Title, r => r.Id!.Value);
        Assert.Equal([(records["Цель"], 0), (graph.Target, 303)], manager.Constructor.GetLinkEdges(relation, records["Источник"]));
        AssertSourceIntact(manager, graph);
    }

    [Fact]
    public void A_group_copy_refuses_a_mixed_projection_after_all_internal_collections_are_mapped()
    {
        using var database = new TempDatabase(101);
        var manager = database.Manager;
        var repo = manager.Constructor;
        var root = repo.AddGroup(new DbGroup { Name = "Ветка" }).Id!.Value;
        var a = SampleData.AddCollection(repo, "А", root).Id!.Value;
        var b = SampleData.AddCollection(repo, "Б", root).Id!.Value;
        var key = SampleData.AddField(repo, b, "Код").Id!.Value;
        var source = SampleData.AddRecord(repo, a, "Источник").Id!.Value;
        var target = SampleData.AddRecord(repo, b, "Цель").Id!.Value;
        var relation = repo.AddField(new DbField
        {
            CollectionId = a, Name = "Связь", FieldType = "relation",
            ConfigJson = new RelationConfig(b, false, DisplayField: key.ToString()).ToConfigJson(),
        }).Id!.Value;
        Edges(manager, relation, source, target, target, 303);

        Assert.Throws<InvalidOperationException>(() => repo.DuplicateGroupCore(root, batchSize: 1));

        Assert.Equal([root], repo.GetAllGroups().Select(g => g.Id!.Value));
        Assert.Equal([a, b], repo.GetAllCollections().Select(c => c.Id!.Value).OrderBy(id => id));
        Assert.Equal([(target, 0), (target, 303)], repo.GetLinkEdges(relation, source));
        AssertNoPending(manager);
    }

    [SkippableTheory]
    [InlineData("sqlite", "sqlite")]
    [InlineData("sqlite", "postgres")]
    [InlineData("postgres", "sqlite")]
    [InlineData("postgres", "postgres")]
    public void Cross_database_move_refuses_projection_retargeting_before_deleting_the_source(string from, string to)
    {
        if (from == "postgres" || to == "postgres")
            Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL (порт 5455) не поднят");
        using var pair = new Pair(from, to);
        var occupied = SampleData.AddCollection(pair.Target.Constructor, "Существующая").Id!.Value;
        SampleData.AddField(pair.Target.Constructor, occupied, "Первое поле");
        SampleData.AddField(pair.Target.Constructor, occupied, "Второе поле");
        var graph = Seed(pair.Source, "display", foreign: true);

        Assert.Throws<InvalidOperationException>(() => pair.Target.Constructor.MoveCollectionIsolated(pair.Source.Path,
            graph.Collection, null, batchSize: 1, sourceDbId: 101, targetDbId: 202));

        Assert.Equal([occupied], pair.Target.Constructor.GetAllCollections().Select(c => c.Id!.Value));
        AssertSourceIntact(pair.Source, graph);
        AssertNoPending(pair.Target);
    }

    [Fact]
    public void Numeric_projection_is_safe_when_the_target_property_id_does_not_change()
    {
        using var pair = new Pair("sqlite", "sqlite");
        var graph = Seed(pair.Source, "display", foreign: true);

        var copy = pair.Target.Constructor.MoveCollectionIsolated(pair.Source.Path, graph.Collection, null,
            batchSize: 1, sourceDbId: 101, targetDbId: 202);

        var fields = pair.Target.Constructor.GetFields(copy);
        var key = fields.Single(f => f.Name == "Код").Id!.Value;
        Assert.Equal(graph.Key, key);
        var relation = fields.Single(f => f.Name == "Связь");
        Assert.Equal(key.ToString(), RelationConfig.FromConfig(relation.ConfigJson)!.DisplayField);
        var records = pair.Target.Constructor.GetRecords(copy).ToDictionary(r => r.Title, r => r.Id!.Value);
        Assert.Equal([(records["Цель"], 0), (graph.Target, 303)], pair.Target.Constructor.GetLinkEdges(relation.Id!.Value, records["Источник"]));
        Assert.Empty(pair.Source.Constructor.GetRecords(graph.Collection));
        AssertNoPending(pair.Source);
        AssertNoPending(pair.Target);
    }

    private sealed record Graph(int Collection, int Key, int Relation, int Source, int Target, int External, int ExternalDb, string Config);

    private static Graph Seed(DatabaseManager manager, string projection, bool foreign)
    {
        var repo = manager.Constructor;
        var collection = SampleData.AddCollection(repo, "Граф").Id!.Value;
        var key = SampleData.AddField(repo, collection, "Код", "number").Id!.Value;
        var source = SampleData.AddRecord(repo, collection, "Источник", 10).Id!.Value;
        var target = SampleData.AddRecord(repo, collection, "Цель", 20).Id!.Value;
        var relation = repo.AddField(new DbField
        {
            CollectionId = collection, Name = "Связь", FieldType = "relation",
            ConfigJson = new RelationConfig(collection, false).ToConfigJson(),
        }).Id!.Value;
        var external = target;
        var externalDb = 303;
        if (!foreign)
        {
            var outside = SampleData.AddCollection(repo, "Снаружи").Id!.Value;
            external = SampleData.AddRecord(repo, outside, "Внешняя цель").Id!.Value;
            externalDb = 0;
        }
        Edges(manager, relation, source, target, external, externalDb);
        if (projection is "lookup" or "rollup" or "count")
            repo.AddField(new DbField
            {
                CollectionId = collection, Name = "Вычисление", FieldType = projection == "lookup" ? "lookup" : "rollup",
                ConfigJson = new RelationComputedConfig(relation, key.ToString(),
                    projection == "lookup" ? "lookup" : projection == "count" ? "count" : "sum").ToConfigJson(),
            });
        var config = new RelationConfig(collection, false,
            DisplayField: projection == "display" ? key.ToString() : RelationConfig.TitleField,
            SearchField: projection == "search" ? key.ToString() : null,
            Auto: projection == "auto" ? new RelationAutoRule(RelationConfig.TitleField, key.ToString()) : null).ToConfigJson();
        // Legacy mixed-цели уже существуют; не запускаем движок автоматического подбора.
        manager.Context.DbFields.Single(f => f.Id == relation).ConfigJson = config;
        manager.Context.SaveChanges();
        return new(collection, key, relation, source, target, external, externalDb, config);
    }

    private static void Edges(DatabaseManager manager, int field, int source, int target, int external, int externalDb)
    {
        manager.Context.DbRecordLinks.AddRange(
            new DbRecordLinkEntity { FieldId = field, FromRecordId = source, ToRecordId = target, Position = 10 },
            new DbRecordLinkEntity { FieldId = field, FromRecordId = source, ToRecordId = external, TargetDbId = externalDb, Position = 20 });
        manager.Context.SaveChanges();
    }

    private static void AssertSourceIntact(DatabaseManager manager, Graph graph)
    {
        Assert.Equal([(graph.Target, 0), (graph.External, graph.ExternalDb)], manager.Constructor.GetLinkEdges(graph.Relation, graph.Source));
        Assert.Equal(graph.Config, manager.Context.DbFields.AsNoTracking().Single(f => f.Id == graph.Relation).ConfigJson);
        Assert.Equal(2, manager.Constructor.GetRecords(graph.Collection).Count);
        AssertNoPending(manager);
    }

    private static void AssertNoPending(DatabaseManager manager) =>
        Assert.False(manager.Context.DbMeta.AsNoTracking().Any(m => m.Key.StartsWith(ConstructorRepository.PendingPrefix)));

    private sealed class Pair : IDisposable
    {
        private readonly List<IDisposable> _owners = [];
        public DatabaseManager Source { get; }
        public DatabaseManager Target { get; }

        public Pair(string from, string to)
        {
            Source = Open(from, 101, PgTestServer.Database1);
            Target = Open(to, 202, PgTestServer.Database2);
        }

        private DatabaseManager Open(string provider, int id, string database)
        {
            if (provider == "sqlite")
            {
                var temp = new TempDatabase(id, "projection-copy");
                _owners.Add(temp);
                return temp.Manager;
            }
            var connectionString = PgTestServer.ConnectionString(database);
            using (var connection = new NpgsqlConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
                command.ExecuteNonQuery();
            }
            var manager = DatabaseManager.Open(id, "projection-copy", connectionString, provider: "postgres");
            _owners.Add(manager);
            return manager;
        }

        public void Dispose()
        {
            foreach (var owner in _owners) owner.Dispose();
        }
    }
}
