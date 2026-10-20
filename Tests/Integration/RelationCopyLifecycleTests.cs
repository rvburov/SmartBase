using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

public sealed class RelationCopyLifecycleTests
{
    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Field_copy_preserves_edge_only_cells_database_identity_and_positions(bool postgres, bool batches)
    {
        using var database = new SingleDatabase(postgres);
        var manager = database.Manager;
        var repo = manager.Constructor;
        var targets = SampleData.AddCollection(repo, "Цели").Id!.Value;
        var a = SampleData.AddRecord(repo, targets, "А").Id!.Value;
        var b = SampleData.AddRecord(repo, targets, "Б").Id!.Value;
        var source = SampleData.AddCollection(repo, "Источник").Id!.Value;
        var records = Enumerable.Range(0, 3).Select(i => SampleData.AddRecord(repo, source, $"Запись {i}").Id!.Value).ToArray();
        var config = new RelationConfig(targets, false, "newline");
        var relation = Relation(repo, source, "Связи", config);
        AddEdges(manager, relation, records[0], (a, 0, 420), (a, 303, 17));
        AddEdges(manager, relation, records[1], (b, 404, 73), (a, 0, -9));
        var before = Edges(manager, relation);
        Assert.False(manager.Context.DbFieldValues.Any(v => v.FieldId == relation));

        var copy = batches
            ? repo.DuplicateFieldData(relation, batchSize: 1, chunkValues: 1)
            : repo.DuplicateField(relation);

        Assert.NotEqual(relation, copy.Id!.Value);
        Assert.Equal(config, RelationConfig.FromConfig(copy.ConfigJson));
        Assert.Equal(before, Edges(manager, copy.Id.Value));
        Assert.Equal(before, Edges(manager, relation));
        Assert.Empty(repo.GetLinkEdges(copy.Id.Value, records[2]));
        Assert.False(manager.Context.DbFieldValues.Any(v => v.FieldId == copy.Id.Value));
        AssertNoPending(manager);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collection_copy_remaps_self_properties_and_computations_but_keeps_external_targets(bool postgres)
    {
        using var database = new SingleDatabase(postgres);
        var manager = database.Manager;
        var repo = manager.Constructor;
        var outside = SampleData.AddCollection(repo, "Снаружи").Id!.Value;
        var outsideCode = SampleData.AddField(repo, outside, "Код цели").Id!.Value;
        var outsideNumber = SampleData.AddField(repo, outside, "Сумма цели", "number").Id!.Value;
        var endpoint = SampleData.AddRecord(repo, outside, "Внешняя цель").Id!.Value;
        repo.SetFieldValue(endpoint, outsideCode, "Внешний код");
        repo.SetFieldValue(endpoint, outsideNumber, "19");
        var source = SampleData.AddCollection(repo, "Исходная").Id!.Value;
        var code = SampleData.AddField(repo, source, "Код").Id!.Value;
        var search = SampleData.AddField(repo, source, "Поиск").Id!.Value;
        var number = SampleData.AddField(repo, source, "Сумма", "number").Id!.Value;
        var first = SampleData.AddRecord(repo, source, "Первая", 20).Id!.Value;
        var last = SampleData.AddRecord(repo, source, "Последняя", 10).Id!.Value;
        repo.SetFieldValue(first, code, "А");
        repo.SetFieldValue(last, code, "Б");
        repo.SetFieldValue(first, number, "7");
        repo.SetFieldValue(last, number, "11");
        var self = Relation(repo, source, "Своя", new RelationConfig(source, false));
        var external = Relation(repo, source, "Внешняя", new RelationConfig(outside, false));
        var foreign = Relation(repo, source, "Другая база", new RelationConfig(source, false, TargetDbId: 303));
        AddEdges(manager, self, first, (last, 0, 47));
        AddEdges(manager, self, last, (first, 0, 61));
        AddEdges(manager, external, first, (endpoint, 0, 23));
        AddEdges(manager, foreign, first, (first, 303, 91));
        Computed(repo, source, "Код связанной", "lookup", self, code.ToString());
        Computed(repo, source, "Сумма связанных", "rollup", self, number.ToString(), "sum");
        Computed(repo, source, "Код внешней", "lookup", external, outsideCode.ToString());
        Computed(repo, source, "Сумма внешней", "rollup", external, outsideNumber.ToString(), "sum");
        var selfConfig = new RelationConfig(source, false, "semicolon", DisplayField: code.ToString(),
            SearchField: search.ToString(), Auto: new RelationAutoRule(code.ToString(), code.ToString()));
        var externalConfig = new RelationConfig(outside, false, DisplayField: outsideCode.ToString(),
            SearchField: outsideCode.ToString(), Auto: new RelationAutoRule(code.ToString(), outsideCode.ToString()));
        var foreignConfig = new RelationConfig(source, false, TargetDbId: 303, DisplayField: code.ToString(),
            SearchField: search.ToString(), Auto: new RelationAutoRule(code.ToString(), search.ToString()));
        Configure(manager, self, selfConfig);
        Configure(manager, external, externalConfig);
        Configure(manager, foreign, foreignConfig);

        var copy = repo.DuplicateCollection(source).Id!.Value;
        var fields = repo.GetFields(copy).ToDictionary(f => f.Name);
        var records = repo.GetRecords(copy).ToDictionary(r => r.Title, r => r.Id!.Value);
        var copiedFirst = records["Первая"];
        var copiedLast = records["Последняя"];
        var copiedCode = fields["Код"].Id!.Value;
        var copiedNumber = fields["Сумма"].Id!.Value;
        Assert.NotEqual(source, copy);
        Assert.NotEqual(first, copiedFirst);
        Assert.NotEqual(last, copiedLast);
        Assert.NotEqual(code, copiedCode);
        Assert.Equal([(copiedFirst, copiedLast, 0, 47), (copiedLast, copiedFirst, 0, 61)],
            Edges(manager, fields["Своя"].Id!.Value).OrderBy(e => e.Position).ToList());
        Assert.Equal([(copiedFirst, endpoint, 0, 23)], Edges(manager, fields["Внешняя"].Id!.Value));
        Assert.Equal([(copiedFirst, first, 303, 91)], Edges(manager, fields["Другая база"].Id!.Value));
        Assert.Equal(selfConfig with
        {
            TargetCollectionId = copy, DisplayField = copiedCode.ToString(),
            SearchField = fields["Поиск"].Id!.Value.ToString(),
            Auto = new RelationAutoRule(copiedCode.ToString(), copiedCode.ToString()),
        }, RelationConfig.FromConfig(fields["Своя"].ConfigJson));
        Assert.Equal(externalConfig with { Auto = new RelationAutoRule(copiedCode.ToString(), outsideCode.ToString()) },
            RelationConfig.FromConfig(fields["Внешняя"].ConfigJson));
        Assert.Equal(foreignConfig with { Auto = new RelationAutoRule(copiedCode.ToString(), search.ToString()) },
            RelationConfig.FromConfig(fields["Другая база"].ConfigJson));
        AssertComputed(fields["Код связанной"], fields["Своя"], copiedCode.ToString(), "lookup");
        AssertComputed(fields["Сумма связанных"], fields["Своя"], copiedNumber.ToString(), "sum");
        AssertComputed(fields["Код внешней"], fields["Внешняя"], outsideCode.ToString(), "lookup");
        AssertComputed(fields["Сумма внешней"], fields["Внешняя"], outsideNumber.ToString(), "sum");

        // Меняем копию без движка автосвязей: вычисления должны читать именно ее новые поля и записи.
        manager.Context.DbFieldValues.Single(v => v.RecordId == copiedLast && v.FieldId == copiedCode).Value = "Копия";
        manager.Context.DbFieldValues.Single(v => v.RecordId == copiedLast && v.FieldId == copiedNumber).Value = "23";
        manager.Context.SaveChanges();
        var values = repo.GetRelationComputedValues([copiedFirst])[copiedFirst];
        Assert.Equal("Копия", values[fields["Код связанной"].Id!.Value]);
        Assert.Equal("23", values[fields["Сумма связанных"].Id!.Value]);
        Assert.Equal("Внешний код", values[fields["Код внешней"].Id!.Value]);
        Assert.Equal("19", values[fields["Сумма внешней"].Id!.Value]);
        Assert.Equal([(last, 0)], repo.GetLinkEdges(self, first));
        Assert.Equal(selfConfig, RelationConfig.FromConfig(repo.GetFields(source).Single(f => f.Id == self).ConfigJson));
        Assert.Equal("Б", manager.Context.DbFieldValues.AsNoTracking().Single(v => v.RecordId == last && v.FieldId == code).Value);
        AssertNoPending(manager);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Group_copy_remaps_the_whole_nested_cycle_and_keeps_targets_outside_the_branch(bool postgres)
    {
        using var database = new SingleDatabase(postgres);
        var manager = database.Manager;
        var repo = manager.Constructor;
        var root = repo.AddGroup(new DbGroup { Name = "Ветка" }).Id!.Value;
        var child = repo.AddGroup(new DbGroup { Name = "Ниже", ParentId = root }).Id!.Value;
        var a = SampleData.AddCollection(repo, "А", root).Id!.Value;
        var b = SampleData.AddCollection(repo, "Б", child).Id!.Value;
        var outside = SampleData.AddCollection(repo, "Снаружи").Id!.Value;
        var endpoint = SampleData.AddRecord(repo, outside, "Остается").Id!.Value;
        var ac = SampleData.AddField(repo, a, "Код А").Id!.Value;
        var bc = SampleData.AddField(repo, b, "Код Б").Id!.Value;
        var bs = SampleData.AddField(repo, b, "Поиск Б").Id!.Value;
        var ar = SampleData.AddRecord(repo, a, "А0", 20).Id!.Value;
        var ar2 = SampleData.AddRecord(repo, a, "А1", 10).Id!.Value;
        var br = SampleData.AddRecord(repo, b, "Б0", 20).Id!.Value;
        var br2 = SampleData.AddRecord(repo, b, "Б1", 10).Id!.Value;
        var forward = Relation(repo, a, "Вперед", new RelationConfig(b, false));
        var backward = Relation(repo, b, "Назад", new RelationConfig(a, true));
        var self = Relation(repo, a, "Своя", new RelationConfig(a, false));
        var external = Relation(repo, b, "Снаружи", new RelationConfig(outside, false));
        var foreign = Relation(repo, a, "Другая база", new RelationConfig(a, false, TargetDbId: 303));
        AddEdges(manager, forward, ar, (br2, 0, 13), (br, 0, 71));
        AddEdges(manager, backward, br2, (ar, 0, 41));
        AddEdges(manager, self, ar2, (ar, 0, 17));
        AddEdges(manager, external, br, (endpoint, 0, 31));
        AddEdges(manager, foreign, ar, (ar, 303, 29));
        Configure(manager, forward, new RelationConfig(b, false, "newline", DisplayField: bc.ToString(),
            SearchField: bs.ToString(), Auto: new RelationAutoRule(ac.ToString(), bc.ToString())));
        Configure(manager, backward, new RelationConfig(a, true, DisplayField: ac.ToString(),
            Auto: new RelationAutoRule(RelationConfig.TitleField, ac.ToString())));

        var copiedRoot = repo.DuplicateGroupCore(root, batchSize: 1);
        var copiedChild = repo.GetAllGroups().Single(g => g.ParentId == copiedRoot).Id!.Value;
        var copiedA = repo.GetAllCollections().Single(c => c.GroupId == copiedRoot && c.Name == "А").Id!.Value;
        var copiedB = repo.GetAllCollections().Single(c => c.GroupId == copiedChild && c.Name == "Б").Id!.Value;
        var af = repo.GetFields(copiedA).ToDictionary(f => f.Name);
        var bf = repo.GetFields(copiedB).ToDictionary(f => f.Name);
        var ars = repo.GetRecords(copiedA).ToDictionary(r => r.Title, r => r.Id!.Value);
        var brs = repo.GetRecords(copiedB).ToDictionary(r => r.Title, r => r.Id!.Value);
        Assert.NotEqual(root, copiedRoot);
        Assert.NotEqual(child, copiedChild);
        Assert.NotEqual(a, copiedA);
        Assert.NotEqual(b, copiedB);
        Assert.Equal([(ars["А0"], brs["Б1"], 0, 13), (ars["А0"], brs["Б0"], 0, 71)], Edges(manager, af["Вперед"].Id!.Value));
        Assert.Equal([(brs["Б1"], ars["А0"], 0, 41)], Edges(manager, bf["Назад"].Id!.Value));
        Assert.Equal([(ars["А1"], ars["А0"], 0, 17)], Edges(manager, af["Своя"].Id!.Value));
        Assert.Equal([(brs["Б0"], endpoint, 0, 31)], Edges(manager, bf["Снаружи"].Id!.Value));
        Assert.Equal([(ars["А0"], ar, 303, 29)], Edges(manager, af["Другая база"].Id!.Value));
        Assert.Equal(new RelationConfig(copiedB, false, "newline", DisplayField: bf["Код Б"].Id!.Value.ToString(),
            SearchField: bf["Поиск Б"].Id!.Value.ToString(),
            Auto: new RelationAutoRule(af["Код А"].Id!.Value.ToString(), bf["Код Б"].Id!.Value.ToString())),
            RelationConfig.FromConfig(af["Вперед"].ConfigJson));
        Assert.Equal(new RelationConfig(copiedA, true, DisplayField: af["Код А"].Id!.Value.ToString(),
            Auto: new RelationAutoRule(RelationConfig.TitleField, af["Код А"].Id!.Value.ToString())),
            RelationConfig.FromConfig(bf["Назад"].ConfigJson));
        Assert.Equal(new RelationConfig(outside, false), RelationConfig.FromConfig(bf["Снаружи"].ConfigJson));
        Assert.Equal(new RelationConfig(a, false, TargetDbId: 303), RelationConfig.FromConfig(af["Другая база"].ConfigJson));
        Assert.Equal([(br2, 0), (br, 0)], repo.GetLinkEdges(forward, ar));
        Assert.Equal([(ar, 0)], repo.GetLinkEdges(backward, br2));
        Assert.Equal(2, repo.GetRecords(a).Count);
        Assert.Equal(2, repo.GetRecords(b).Count);
        AssertNoPending(manager);
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Synchronous_delete_cleans_local_incoming_but_preserves_foreign_equal_ids(bool postgres, bool group)
    {
        using var database = new SingleDatabase(postgres);
        var manager = database.Manager;
        var repo = manager.Constructor;
        var root = group ? repo.AddGroup(new DbGroup { Name = "Удаляемая ветка" }).Id : null;
        var child = group ? repo.AddGroup(new DbGroup { Name = "Ниже", ParentId = root }).Id : null;
        var a = SampleData.AddCollection(repo, "Удаляемая", root).Id!.Value;
        var b = group ? SampleData.AddCollection(repo, "Вложенная", child).Id!.Value : a;
        var victim = SampleData.AddRecord(repo, a, "Цель").Id!.Value;
        var victim2 = SampleData.AddRecord(repo, b, "Другая цель").Id!.Value;
        var outside = SampleData.AddCollection(repo, "Остается").Id!.Value;
        var keeper = SampleData.AddRecord(repo, outside, "Источник").Id!.Value;
        var survivingTarget = SampleData.AddRecord(repo, outside, "Другая цель").Id!.Value;
        var incoming = Relation(repo, outside, "Входящая", new RelationConfig(a, false));
        var outgoing = Relation(repo, a, "Исходящая", new RelationConfig(outside, false));
        var internalLink = Relation(repo, a, "Внутренняя", new RelationConfig(b, false));
        AddEdges(manager, incoming, keeper, (victim, 0, 11), (victim, 303, 22), (victim2, 0, 33), (survivingTarget, 0, 44));
        AddEdges(manager, outgoing, victim, (keeper, 0, 51));
        AddEdges(manager, internalLink, victim, (victim2, 0, 61));

        if (group) repo.DeleteGroup(root!.Value);
        else repo.DeleteCollection(a);

        Assert.Equal([(keeper, victim, 303, 22), (keeper, survivingTarget, 0, 44)], Edges(manager, incoming));
        Assert.False(manager.Context.DbRecords.AsNoTracking().Any(r => r.Id == victim || r.Id == victim2));
        Assert.False(manager.Context.DbCollections.AsNoTracking().Any(c => c.Id == a || c.Id == b));
        Assert.False(manager.Context.DbFields.AsNoTracking().Any(f => f.CollectionId == a || f.CollectionId == b));
        Assert.Empty(Edges(manager, outgoing));
        Assert.Empty(Edges(manager, internalLink));
        Assert.NotNull(repo.GetRecord(keeper, refresh: true));
        Assert.NotNull(repo.GetRecord(survivingTarget, refresh: true));
        if (group) Assert.False(manager.Context.DbGroups.AsNoTracking().Any(g => g.Id == root || g.Id == child));
    }

    private static int Relation(ConstructorRepository repo, int collection, string name, RelationConfig config) =>
        repo.AddField(new DbField
        {
            CollectionId = collection, Name = name, FieldType = "relation", ConfigJson = config.ToConfigJson(),
        }).Id!.Value;

    private static void Computed(ConstructorRepository repo, int collection, string name, string type,
        int relation, string target, string aggregate = "lookup") => repo.AddField(new DbField
        {
            CollectionId = collection, Name = name, FieldType = type,
            ConfigJson = new RelationComputedConfig(relation, target, aggregate).ToConfigJson(),
        });

    private static void AssertComputed(DbField field, DbField relation, string target, string aggregate) =>
        Assert.Equal(new RelationComputedConfig(relation.Id!.Value, target, aggregate), RelationComputedConfig.FromConfig(field.ConfigJson));

    private static void Configure(DatabaseManager manager, int field, RelationConfig config)
    {
        // Копирование настроек проверяется отдельно от запуска автоматического подбора.
        manager.Context.DbFields.Single(f => f.Id == field).ConfigJson = config.ToConfigJson();
        manager.Context.SaveChanges();
    }

    private static void AddEdges(DatabaseManager manager, int field, int source, params (int To, int Db, int Position)[] edges)
    {
        manager.Context.DbRecordLinks.AddRange(edges.Select(e => new DbRecordLinkEntity
        {
            FieldId = field, FromRecordId = source, ToRecordId = e.To, TargetDbId = e.Db, Position = e.Position,
        }));
        manager.Context.SaveChanges();
    }

    private static List<(int From, int To, int Db, int Position)> Edges(DatabaseManager manager, int field) =>
        manager.Context.DbRecordLinks.AsNoTracking().Where(l => l.FieldId == field)
            .OrderBy(l => l.FromRecordId).ThenBy(l => l.Position).ThenBy(l => l.Id).ToList()
            .Select(l => (l.FromRecordId, l.ToRecordId, l.TargetDbId, l.Position)).ToList();

    private static void AssertNoPending(DatabaseManager manager) =>
        Assert.False(manager.Context.DbMeta.AsNoTracking().Any(m => m.Key.StartsWith(ConstructorRepository.PendingPrefix)));

    private sealed class SingleDatabase : IDisposable
    {
        private readonly TempDatabase? _sqlite;
        public DatabaseManager Manager { get; }

        public SingleDatabase(bool postgres)
        {
            if (!postgres)
            {
                _sqlite = new TempDatabase(101, "relation-copy");
                Manager = _sqlite.Manager;
                return;
            }
            Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
            var connectionString = PgTestServer.ConnectionString(PgTestServer.Database1);
            using (var connection = new NpgsqlConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
                command.ExecuteNonQuery();
            }
            Manager = DatabaseManager.Open(101, "relation-copy", connectionString, provider: "postgres");
        }

        public void Dispose()
        {
            if (_sqlite is not null) _sqlite.Dispose();
            else Manager.Dispose();
        }
    }
}
