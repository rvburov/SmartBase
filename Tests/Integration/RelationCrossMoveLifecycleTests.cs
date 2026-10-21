using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>Generic-перенос переносит граф после полной карты ветки, а не по карте одной страницы.</summary>
public sealed class RelationCrossMoveLifecycleTests
{
    [Fact]
    public void Group_move_warning_counts_only_incoming_links_from_outside_the_whole_branch()
    {
        using var database = new TempDatabase();
        var repo = database.Constructor;
        var a = SampleData.AddCollection(repo, "А").Id!.Value;
        var b = SampleData.AddCollection(repo, "Б").Id!.Value;
        var outside = SampleData.AddCollection(repo, "Снаружи").Id!.Value;
        var ar = SampleData.AddRecord(repo, a, "А").Id!.Value;
        var br = SampleData.AddRecord(repo, b, "Б").Id!.Value;
        var other = SampleData.AddRecord(repo, outside, "Сосед").Id!.Value;
        var internalLink = Relation(repo, a, "Внутри", new RelationConfig(b, false));
        var outgoing = Relation(repo, a, "Наружу", new RelationConfig(outside, false));
        repo.SetLinks(internalLink, ar, [br]);
        repo.SetLinks(outgoing, ar, [other]);
        Assert.False(RelationDeleteImpact.ForCollectionsExternalCrossMove(repo, [a, b]));
        var incoming = Relation(repo, outside, "Входящая", new RelationConfig(a, false));
        repo.SetLinks(incoming, other, [ar]);
        Assert.True(RelationDeleteImpact.ForCollectionsExternalCrossMove(repo, [a, b]));
        Assert.False(RelationDeleteImpact.ForCollectionsExternalCrossMove(repo, []));
    }

    [SkippableTheory]
    [InlineData("sqlite", "postgres")]
    [InlineData("postgres", "sqlite")]
    [InlineData("postgres", "postgres")]
    public void Collection_move_keeps_self_links_external_targets_and_property_keys(string from, string to)
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        using var pair = new Pair(from, to);
        var source = pair.Source.Constructor;
        var target = pair.Target.Constructor;
        OffsetTargetIds(target);
        var outside = SampleData.AddCollection(source, "Остается в источнике").Id!.Value;
        var outsideCode = SampleData.AddField(source, outside, "Код").Id!.Value;
        var outsideRecord = SampleData.AddRecord(source, outside, "Внешняя цель").Id!.Value;
        var destination = SampleData.AddCollection(target, "Уже в назначении").Id!.Value;
        var destinationRecord = SampleData.AddRecord(target, destination, "Цель назначения").Id!.Value;
        var moving = SampleData.AddCollection(source, "Переносимая").Id!.Value;
        var code = SampleData.AddField(source, moving, "Код").Id!.Value;
        var search = SampleData.AddField(source, moving, "Для поиска").Id!.Value;
        var records = Enumerable.Range(0, 9).Select(i => SampleData.AddRecord(source, moving,
            $"Узел {i}", position: (9 - i) * 10).Id!.Value).ToArray();
        var self = Relation(source, moving, "Внутри", new RelationConfig(moving, false, "semicolon"));
        var origin = Relation(source, moving, "В источнике", new RelationConfig(outside, false));
        var alreadyThere = Relation(source, moving, "В назначении", new RelationConfig(destination, false,
            TargetDbId: Pair.TargetId));
        var third = Relation(source, moving, "Третья база", new RelationConfig(57, false, TargetDbId: 303));
        source.SetLinks(self, records[0], [records[^1]]);
        source.SetLinks(self, records[7], [records[1]]);
        source.SetLinks(origin, records[0], [outsideRecord]);
        source.SetLinks(alreadyThere, records[0], [destinationRecord]);
        source.SetLinks(third, records[0], [777]);
        var incoming = Relation(source, outside, "Входящая", new RelationConfig(moving, false));
        source.SetLinks(incoming, outsideRecord, [records[0]]);

        // Настройки ставятся после ребер: проверяем их перенос, без запуска автосопоставления.
        Configure(pair.Source, self, new RelationConfig(moving, false, "semicolon", DisplayField: code.ToString(),
            SearchField: search.ToString(), Auto: new RelationAutoRule(code.ToString(), code.ToString())));
        Configure(pair.Source, origin, new RelationConfig(outside, false, DisplayField: outsideCode.ToString(),
            Auto: new RelationAutoRule(code.ToString(), outsideCode.ToString())));
        Configure(pair.Source, third, new RelationConfig(57, false, TargetDbId: 303,
            DisplayField: code.ToString(), SearchField: search.ToString(),
            Auto: new RelationAutoRule(code.ToString(), search.ToString())));

        var copied = target.MoveCollectionIsolated(pair.Source.Path, moving, null, batchSize: 2,
            sourceDbId: Pair.SourceId, targetDbId: Pair.TargetId);
        var fields = target.GetFields(copied).ToDictionary(f => f.Name);
        var copiedRecords = target.GetRecords(copied).ToDictionary(r => r.Title, r => r.Id!.Value);
        var first = copiedRecords["Узел 0"];
        Assert.NotEqual(records[0], first);
        Assert.Equal([(copiedRecords["Узел 8"], 0)], target.GetLinkEdges(fields["Внутри"].Id!.Value, first));
        Assert.Equal([(copiedRecords["Узел 1"], 0)], target.GetLinkEdges(fields["Внутри"].Id!.Value, copiedRecords["Узел 7"]));
        Assert.Equal([(outsideRecord, Pair.SourceId)], target.GetLinkEdges(fields["В источнике"].Id!.Value, first));
        Assert.Equal([(destinationRecord, 0)], target.GetLinkEdges(fields["В назначении"].Id!.Value, first));
        Assert.Equal([(777, 303)], target.GetLinkEdges(fields["Третья база"].Id!.Value, first));
        Assert.Null(source.GetRecord(records[0], refresh: true));
        Assert.NotNull(source.GetRecord(outsideRecord));
        Assert.Empty(source.GetLinkEdges(incoming, outsideRecord));

        var copiedCode = fields["Код"].Id!.Value.ToString();
        var copiedSearch = fields["Для поиска"].Id!.Value.ToString();
        var cfg = RelationConfig.FromConfig(fields["Внутри"].ConfigJson)!;
        Assert.Equal(copied, cfg.TargetCollectionId);
        Assert.Equal(0, cfg.TargetDbId);
        Assert.Equal("semicolon", cfg.Separator);
        Assert.Equal(copiedCode, cfg.DisplayField);
        Assert.Equal(copiedSearch, cfg.SearchField);
        Assert.Equal(new RelationAutoRule(copiedCode, copiedCode), cfg.Auto);
        var originCfg = RelationConfig.FromConfig(fields["В источнике"].ConfigJson)!;
        Assert.Equal(outside, originCfg.TargetCollectionId);
        Assert.Equal(Pair.SourceId, originCfg.TargetDbId);
        Assert.Equal(outsideCode.ToString(), originCfg.DisplayField);
        Assert.Equal(new RelationAutoRule(copiedCode, outsideCode.ToString()), originCfg.Auto);
        var destinationCfg = RelationConfig.FromConfig(fields["В назначении"].ConfigJson)!;
        Assert.Equal(destination, destinationCfg.TargetCollectionId);
        Assert.Equal(0, destinationCfg.TargetDbId);
        Assert.Equal(RelationConfig.TitleField, destinationCfg.DisplayField);
        Assert.Null(destinationCfg.SearchField);
        var thirdCfg = RelationConfig.FromConfig(fields["Третья база"].ConfigJson)!;
        Assert.Equal(303, thirdCfg.TargetDbId);
        Assert.Equal(57, thirdCfg.TargetCollectionId);
        Assert.Equal(code.ToString(), thirdCfg.DisplayField); // такой же номер поля в другой БД не ремапится
        Assert.Equal(search.ToString(), thirdCfg.SearchField);
        Assert.Equal(new RelationAutoRule(copiedCode, search.ToString()), thirdCfg.Auto);
        AssertNoPending(pair);
    }

    [SkippableTheory]
    [InlineData("sqlite", "postgres")]
    [InlineData("postgres", "sqlite")]
    [InlineData("postgres", "postgres")]
    public void Group_move_remaps_links_and_property_keys_across_all_collections_and_pages(string from, string to)
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        using var pair = new Pair(from, to);
        var source = pair.Source.Constructor;
        var target = pair.Target.Constructor;
        OffsetTargetIds(target);
        var root = source.AddGroup(new DbGroup { Name = "Ветка" }).Id!.Value;
        var child = source.AddGroup(new DbGroup { Name = "Ниже", ParentId = root }).Id!.Value;
        var a = SampleData.AddCollection(source, "Ранняя", root).Id!.Value;
        var b = SampleData.AddCollection(source, "Поздняя", child).Id!.Value;
        var ac = SampleData.AddField(source, a, "Код А").Id!.Value;
        var bc = SampleData.AddField(source, b, "Код Б").Id!.Value;
        var bs = SampleData.AddField(source, b, "Поиск Б").Id!.Value;
        var ar = Enumerable.Range(0, 6).Select(i => SampleData.AddRecord(source, a, $"А{i}", (6 - i) * 10).Id!.Value).ToArray();
        var br = Enumerable.Range(0, 6).Select(i => SampleData.AddRecord(source, b, $"Б{i}", (6 - i) * 10).Id!.Value).ToArray();
        var forward = Relation(source, a, "Вперед", new RelationConfig(b, false, "newline"));
        var backward = Relation(source, b, "Назад", new RelationConfig(a, true));
        var self = Relation(source, a, "Своя", new RelationConfig(a, false));
        source.SetLinks(forward, ar[0], [br[0], br[^1]]);
        source.SetLinks(backward, br[0], [ar[0]]);
        source.SetLinks(self, ar[0], [ar[^1]]);
        Configure(pair.Source, forward, new RelationConfig(b, false, "newline", DisplayField: bc.ToString(),
            SearchField: bs.ToString(), Auto: new RelationAutoRule(ac.ToString(), bc.ToString())));
        Configure(pair.Source, backward, new RelationConfig(a, true, DisplayField: ac.ToString(),
            Auto: new RelationAutoRule(RelationConfig.TitleField, ac.ToString())));

        var newRoot = target.MoveGroupIsolated(pair.Source.Path, root, null, batchSize: 2,
            sourceDbId: Pair.SourceId, targetDbId: Pair.TargetId);
        var newChild = target.GetAllGroups().Single(g => g.ParentId == newRoot).Id!.Value;
        var newA = target.GetAllCollections().Single(c => c.GroupId == newRoot && c.Name == "Ранняя").Id!.Value;
        var newB = target.GetAllCollections().Single(c => c.GroupId == newChild && c.Name == "Поздняя").Id!.Value;
        var af = target.GetFields(newA).ToDictionary(f => f.Name);
        var bf = target.GetFields(newB).ToDictionary(f => f.Name);
        var copiedA = target.GetRecords(newA).ToDictionary(r => r.Title, r => r.Id!.Value);
        var copiedB = target.GetRecords(newB).ToDictionary(r => r.Title, r => r.Id!.Value);
        Assert.Equal([(copiedB["Б0"], 0), (copiedB["Б5"], 0)], target.GetLinkEdges(af["Вперед"].Id!.Value, copiedA["А0"]));
        Assert.Equal([(copiedA["А0"], 0)], target.GetLinkEdges(bf["Назад"].Id!.Value, copiedB["Б0"]));
        Assert.Equal([(copiedA["А5"], 0)], target.GetLinkEdges(af["Своя"].Id!.Value, copiedA["А0"]));
        var forwardCfg = RelationConfig.FromConfig(af["Вперед"].ConfigJson)!;
        Assert.Equal(newB, forwardCfg.TargetCollectionId);
        Assert.Equal(0, forwardCfg.TargetDbId);
        Assert.Equal("newline", forwardCfg.Separator);
        Assert.Equal(bf["Код Б"].Id!.Value.ToString(), forwardCfg.DisplayField);
        Assert.Equal(bf["Поиск Б"].Id!.Value.ToString(), forwardCfg.SearchField);
        Assert.Equal(new RelationAutoRule(af["Код А"].Id!.Value.ToString(), bf["Код Б"].Id!.Value.ToString()), forwardCfg.Auto);
        var backwardCfg = RelationConfig.FromConfig(bf["Назад"].ConfigJson)!;
        Assert.Equal(newA, backwardCfg.TargetCollectionId);
        Assert.Equal(0, backwardCfg.TargetDbId);
        Assert.True(backwardCfg.Single);
        Assert.Null(backwardCfg.SearchField);
        Assert.Equal(af["Код А"].Id!.Value.ToString(), backwardCfg.DisplayField);
        Assert.Equal(new RelationAutoRule(RelationConfig.TitleField, af["Код А"].Id!.Value.ToString()), backwardCfg.Auto);
        Assert.DoesNotContain(source.GetAllGroups(), g => g.Id == root || g.Id == child);
        Assert.Empty(source.GetRecords(a));
        Assert.Empty(source.GetRecords(b));
        AssertNoPending(pair);
    }

    [SkippableFact]
    public void Unknown_origin_database_does_not_delete_a_source_with_external_local_links()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        using var pair = new Pair("sqlite", "postgres");
        var source = pair.Source.Constructor;
        var target = pair.Target.Constructor;
        var external = SampleData.AddCollection(source, "Цели").Id!.Value;
        var endpoint = SampleData.AddRecord(source, external, "Цель").Id!.Value;
        var moving = SampleData.AddCollection(source, "Источник").Id!.Value;
        var record = SampleData.AddRecord(source, moving, "Запись").Id!.Value;
        var relation = Relation(source, moving, "Ссылка", new RelationConfig(external, false));
        source.SetLinks(relation, record, [endpoint]);
        var before = target.GetAllCollections().Count;

        Assert.Throws<InvalidOperationException>(() => target.MoveCollectionIsolated(pair.Source.Path, moving,
            null, batchSize: 1, sourceDbId: 0, targetDbId: Pair.TargetId));
        Assert.NotNull(source.GetRecord(record));
        Assert.Equal([(endpoint, 0)], source.GetLinkEdges(relation, record));
        Assert.Equal(before, target.GetAllCollections().Count);
        AssertNoPending(pair);
    }

    private static int Relation(ConstructorRepository repo, int collection, string name, RelationConfig config) =>
        repo.AddField(new DbField
        {
            CollectionId = collection, Name = name, FieldType = "relation", ConfigJson = config.ToConfigJson(),
        }).Id!.Value;

    private static void Configure(DatabaseManager manager, int field, RelationConfig config)
    {
        manager.Context.DbFields.Single(f => f.Id == field).ConfigJson = config.ToConfigJson();
        manager.Context.SaveChanges();
    }

    private static void OffsetTargetIds(ConstructorRepository target)
    {
        var col = SampleData.AddCollection(target, "Занятые номера").Id!.Value;
        for (var i = 0; i < 15; i++)
        {
            SampleData.AddField(target, col, $"Поле {i}");
            SampleData.AddRecord(target, col, $"До переноса {i}");
        }
        for (var i = 0; i < 5; i++) SampleData.AddCollection(target, $"Прежняя {i}");
    }

    private static void AssertNoPending(Pair pair)
    {
        Assert.False(pair.Source.Context.DbMeta.AsNoTracking().Any(m => m.Key.StartsWith(ConstructorRepository.PendingPrefix)));
        Assert.False(pair.Target.Context.DbMeta.AsNoTracking().Any(m => m.Key.StartsWith(ConstructorRepository.PendingPrefix)));
    }

    private sealed class Pair : IDisposable
    {
        public const int SourceId = 101;
        public const int TargetId = 202;
        private readonly List<TempDatabase> _sqlite = [];
        private readonly List<DatabaseManager> _postgres = [];
        public DatabaseManager Source { get; }
        public DatabaseManager Target { get; }

        public Pair(string sourceProvider, string targetProvider)
        {
            if (sourceProvider == "postgres") Reset(PgTestServer.ConnectionString(PgTestServer.Database1));
            if (targetProvider == "postgres") Reset(PgTestServer.ConnectionString(PgTestServer.Database2));
            Source = Open(sourceProvider, SourceId, PgTestServer.Database1);
            Target = Open(targetProvider, TargetId, PgTestServer.Database2);
        }

        private DatabaseManager Open(string provider, int id, string database)
        {
            if (provider == "sqlite")
            {
                var temp = new TempDatabase(id, $"relation-{id}");
                _sqlite.Add(temp);
                return temp.Manager;
            }
            var manager = DatabaseManager.Open(id, $"relation-{id}", PgTestServer.ConnectionString(database), provider: "postgres");
            _postgres.Add(manager);
            return manager;
        }

        private static void Reset(string connectionString)
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            foreach (var manager in _postgres) manager.Dispose();
            foreach (var database in _sqlite) database.Dispose();
        }
    }
}
