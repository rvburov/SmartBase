using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Обратные ссылки страницы записи («Ссылаются на эту запись»): карточка на каждое свойство-связь,
/// нацеленное на коллекцию записи, число источников до 1 000 и первые названия — сначала новые;
/// список источников страницами с поиском; закрытые коллекции не видны. Чтение идет от индекса
/// ребер и сверяет коллекцию источника по первичному ключу.
/// </summary>
public sealed class RelationBacklinkTests
{
    [Fact]
    public void Every_relation_on_the_collection_gets_a_card_with_newest_sources_first()
    {
        using var f = new Fixture();
        var bills = Enumerable.Range(1, 5).Select(i => f.AddBill($"Счет №{i}")).ToList();
        foreach (var bill in bills) f.Repo.SetLinks(f.BillClient, bill, [f.Client]);
        var groups = f.Groups();
        // Сделки ссылаются на клиентов, но ссылок нет; связь заметок ведет в другую коллекцию.
        Assert.Equal(["Сделки · Клиент", "Счета · Клиент"], groups.Select(g => $"{g.Collection} · {g.Field}"));
        var deals = groups[0];
        Assert.Equal((0, false), (deals.Sources, deals.More));
        Assert.Equal("ссылок нет", RelationBacklinkText.Value(deals));
        var billGroup = groups[1];
        Assert.Equal(5, billGroup.Sources);
        Assert.Equal(["Счет №5", "Счет №4", "Счет №3"], billGroup.First.Select(s => s.Title));
        Assert.Equal("5 записей: Счет №5, Счет №4, Счет №3…", RelationBacklinkText.Value(billGroup));
        Assert.Equal(0, billGroup.SourceDbId);
        // Другая запись той же коллекции ссылок не получает.
        Assert.All(f.Groups(f.Other), g => Assert.Equal(0, g.Sources));
    }

    [Fact]
    public void Old_edges_with_the_own_base_number_count_once()
    {
        using var f = new Fixture();
        var own = f.Repo.RelationBaseId;
        Assert.True(RelationDbResolver.IsLinkId(own));
        var first = f.AddBill("Первый");
        var second = f.AddBill("Второй");
        var third = f.AddBill("Третий");
        // Прежняя версия писала ребро на свою запись и с номером своей базы; у одного источника
        // встречаются обе формы.
        f.InsertEdges(f.BillClient, f.Client, [(first, 0), (second, own), (third, 0), (third, own)]);
        var group = f.Groups().Single(g => g.FieldId == f.BillClient);
        Assert.Equal(3, group.Sources);
        Assert.Equal([third, second, first], group.First.Select(s => s.RecordId));
        Assert.Equal([0, own], group.Targets);
        Assert.Equal([third, second, first], f.Page(group, null, null, 10).Items.Select(s => s.RecordId));
        Assert.Equal(3, f.Repo.RelationBacklinkTotal(group, f.Client));
    }

    [Fact]
    public void A_record_is_not_listed_as_linking_to_itself_and_strays_are_not_counted()
    {
        using var f = new Fixture();
        var parent = SampleData.AddField(f.Repo, f.Clients, "Головная", "relation");
        parent.ConfigJson = new RelationConfig(f.Clients, true).ToConfigJson();
        f.Repo.UpdateField(parent);
        var deal = SampleData.AddRecord(f.Repo, f.Deals, "Сделка").Id!.Value;
        // Ссылка на себя (так ее мог оставить импорт прежней версии) и ребро от записи чужой
        // коллекции (запись перенесли) не считаются и не называются.
        f.InsertEdges(parent.Id!.Value, f.Client, [(f.Client, 0), (f.Other, 0)]);
        f.InsertEdges(f.BillClient, f.Client, [(deal, 0)]);
        var groups = f.Groups();
        var self = groups.Single(g => g.FieldId == parent.Id);
        Assert.Equal(["Другой клиент"], self.First.Select(s => s.Title));
        Assert.Equal(1, f.Repo.RelationBacklinkTotal(self, f.Client));
        var billGroup = groups.Single(g => g.FieldId == f.BillClient);
        Assert.Equal(0, billGroup.Sources);
        Assert.Empty(f.Page(billGroup, null, null, 10).Items);
        Assert.Equal(0, f.Repo.RelationBacklinkTotal(billGroup, f.Client));
    }

    [Fact]
    public void Over_a_thousand_sources_are_shown_as_more()
    {
        using var f = new Fixture();
        var bills = f.AddBills(RelationBacklinkText.Cap + 1);
        // Самое новое ребро — от записи другой коллекции: первая порция дает тысячу настоящих
        // источников, и до тысяча первого нужно дочитать вторую.
        var stray = SampleData.AddRecord(f.Repo, f.Deals, "Сделка").Id!.Value;
        f.InsertEdges(f.BillClient, f.Client, [.. bills.Select(b => (b, 0)), (stray, 0)]);
        var group = f.Groups().Single(g => g.FieldId == f.BillClient);
        Assert.True(group.More);
        Assert.Equal(RelationBacklinkText.Cap, group.Sources);
        Assert.Equal(bills.OrderByDescending(b => b).Take(3), group.First.Select(s => s.RecordId));
        Assert.StartsWith($"более {RelationBacklinkText.Cap:N0} записей: ", RelationBacklinkText.Value(group), StringComparison.Ordinal);
        Assert.Equal(RelationBacklinkText.Cap + 1, f.Repo.RelationBacklinkTotal(group, f.Client));
        // Ровно тысяча — точное число.
        f.Repo.SetLinks(f.BillClient, bills[0], []);
        Assert.False(f.Groups().Single(g => g.FieldId == f.BillClient).More);
    }

    [Fact]
    public void Pages_walk_all_sources_without_gaps_or_repeats()
    {
        using var f = new Fixture();
        var own = f.Repo.RelationBaseId;
        var bills = f.AddBills(2500);
        // Формы цели вперемешку, у части источников — обе.
        f.InsertEdges(f.BillClient, f.Client, bills.SelectMany((b, i) => i % 7 == 0 ? [(b, 0), (b, own)]
            : i % 3 == 0 ? new[] { (b, own) } : [(b, 0)]).ToList());
        var group = f.Groups().Single(g => g.FieldId == f.BillClient);
        var seen = new List<int>();
        int? before = null;
        var pages = 0;
        do
        {
            var page = f.Page(group, null, before, 200);
            seen.AddRange(page.Items.Select(s => s.RecordId));
            before = page.Next;
            // Новая ссылка во время обхода в уже пройденные страницы не попадает и не сбивает следующие.
            if (pages++ == 2) f.Repo.SetLinks(f.BillClient, f.AddBill("Новый счет"), [f.Client]);
        } while (before is not null);
        Assert.Equal(bills.OrderByDescending(b => b), seen);
        Assert.Equal(13, pages);
    }

    [Fact]
    public void Search_finds_titles_regardless_of_case_across_portions()
    {
        using var f = new Fixture();
        var own = f.Repo.RelationBaseId;
        // Больше порции поиска, совпадения в разных порциях. Почти все ребра — со своим номером базы:
        // этот ответ упирается в порцию, а ответ формы 0 — нет, и вторая порция нужна только первому.
        var bills = f.AddBills(5000, i => i % 1000 == 107 || i is 4998 or 4999 ? $"Акт сверки {i}" : $"Счет {i}");
        f.InsertEdges(f.BillClient, f.Client, bills.Select((b, i) => (b, i % 10 == 9 ? 0 : own)).ToList());
        var group = f.Groups().Single(g => g.FieldId == f.BillClient);
        Assert.Equal(["Акт сверки 4999", "Акт сверки 4998", "Акт сверки 4107", "Акт сверки 3107", "Акт сверки 2107",
            "Акт сверки 1107", "Акт сверки 107"], f.Page(group, "акт сверки", null, 200).Items.Select(s => s.Title));
        var found = new List<string>();
        int? before = null;
        do
        {
            var page = f.Page(group, "АКТ СВЕРКИ", before, 2);
            Assert.True(page.Items.Count <= 2);
            found.AddRange(page.Items.Select(s => s.Title));
            before = page.Next;
        } while (before is not null);
        Assert.Equal(["Акт сверки 4999", "Акт сверки 4998", "Акт сверки 4107", "Акт сверки 3107", "Акт сверки 2107",
            "Акт сверки 1107", "Акт сверки 107"], found);
        Assert.Empty(f.Page(group, "нет такого", null, 200).Items);
        // Знаки подстановки ищутся как текст.
        Assert.Empty(f.Page(group, "%", null, 200).Items);
    }

    [Fact]
    public void Reading_stops_when_cancelled()
    {
        using var f = new Fixture();
        var bills = f.AddBills(50);
        f.InsertEdges(f.BillClient, f.Client, bills.Select(b => (b, 0)).ToList());
        var group = f.Groups().Single(g => g.FieldId == f.BillClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => f.Repo.RelationBacklinkGroups(0, f.Client, f.Clients, 0, cts.Token));
        Assert.Throws<OperationCanceledException>(() => f.Repo.RelationBacklinkSources(group, f.Client, "Счет", null, 200, cts.Token));
        Assert.Throws<OperationCanceledException>(() => f.Repo.RelationBacklinkTotal(group, f.Client, cts.Token));
    }

    [Fact]
    public void The_edge_query_reads_only_the_covering_index()
    {
        // Без ANALYZE план от объема не зависит: хватает малой базы. Именно этот запрос идет на
        // каждую карточку и страницу.
        using var f = new Fixture();
        f.Repo.SetLinks(f.BillClient, f.AddBill("Счет"), [f.Client]);
        foreach (var (before, excluded) in new (int?, int)[] { (null, 0), (500, 0), (500, f.Client) })
        {
            var plan = Explain(f.Database.Context, f.Repo.BacklinkEdgeSources(f.BillClient, f.Client, 0, before, excluded).Take(1001));
            Assert.Contains("COVERING INDEX IX_db_record_links_ToRecordId_TargetDbId_FieldId_FromRecordId", plan, StringComparison.Ordinal);
            Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.Ordinal);
        }
        // Сверка источников — по первичному ключу, а не проходом по коллекции-источнику.
        foreach (var term in new[] { null, "Счет" })
        {
            var plan = Explain(f.Database.Context, f.Repo.BacklinkVerifyQuery([f.Client, f.Other, 999], term));
            Assert.Contains("INTEGER PRIMARY KEY", plan, StringComparison.Ordinal);
            Assert.DoesNotContain("CollectionId", plan, StringComparison.Ordinal);
        }
        // Прежнего индекса нет: новый покрывает его запросы своим началом.
        var indexes = f.Database.Context.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND tbl_name = 'db_record_links'").ToList();
        Assert.Contains("IX_db_record_links_ToRecordId_TargetDbId_FieldId_FromRecordId", indexes);
        Assert.DoesNotContain("IX_db_record_links_ToRecordId", indexes);
    }

    [SkippableFact]
    public void Backlinks_work_on_PostgreSQL()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        using var f = new Fixture(postgres: true);
        var own = f.Repo.RelationBaseId;
        var bills = f.AddBills(450, i => i % 100 == 1 ? $"Акт {i}" : $"Счет {i}");
        f.InsertEdges(f.BillClient, f.Client, bills.Select((b, i) => (b, i % 5 == 0 ? own : 0)).ToList());
        var group = f.Groups().Single(g => g.FieldId == f.BillClient);
        Assert.Equal(450, group.Sources);
        Assert.Equal(bills.OrderByDescending(b => b).Take(3), group.First.Select(s => s.RecordId));
        var seen = new List<int>();
        int? before = null;
        do
        {
            var page = f.Page(group, null, before, 200);
            seen.AddRange(page.Items.Select(s => s.RecordId));
            before = page.Next;
        } while (before is not null);
        Assert.Equal(bills.OrderByDescending(b => b), seen);
        Assert.Equal(["Акт 401", "Акт 301", "Акт 201", "Акт 101", "Акт 1"], f.Page(group, "акт", null, 200).Items.Select(s => s.Title));
        Assert.Equal(450, f.Repo.RelationBacklinkTotal(group, f.Client));
        var indexes = f.Pg!.Context.Database.SqlQueryRaw<string>(
            "SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename = 'db_record_links'").ToList();
        Assert.Contains("IX_db_record_links_torecordid_targetdbid_fieldid_fromrecordid", indexes);
        Assert.DoesNotContain("IX_db_record_links_torecordid", indexes);
    }

    private static string Explain(DbContext context, IQueryable query)
    {
        using var command = query.CreateDbCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
        context.Database.OpenConnection();
        try
        {
            using var reader = command.ExecuteReader();
            var lines = new List<string>();
            while (reader.Read()) lines.Add(reader.GetString(3));
            return string.Join("\n", lines);
        }
        finally { context.Database.CloseConnection(); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDatabase? _db;
        public TempDatabase Database => _db!;
        public DatabaseManager? Pg { get; }
        public ConstructorRepository Repo => _db?.Constructor ?? Pg!.Constructor;
        private AppDbContext Context => _db?.Context ?? Pg!.Context;
        public int Clients { get; }
        public int Client { get; }
        public int Other { get; }
        public int Bills { get; }
        public int BillClient { get; }
        public int Deals { get; }

        public Fixture(bool postgres = false)
        {
            if (postgres)
            {
                var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
                using (var connection = new NpgsqlConnection(cs))
                {
                    connection.Open();
                    using var reset = connection.CreateCommand();
                    reset.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
                    reset.ExecuteNonQuery();
                }
                Pg = DatabaseManager.Open(101, "pg-backlinks", cs, provider: "postgres");
            }
            else _db = new();
            Clients = SampleData.AddCollection(Repo, "Клиенты").Id!.Value;
            Client = SampleData.AddRecord(Repo, Clients, "ООО Ромашка").Id!.Value;
            Other = SampleData.AddRecord(Repo, Clients, "Другой клиент").Id!.Value;
            Bills = SampleData.AddCollection(Repo, "Счета").Id!.Value;
            BillClient = Relation(Bills, "Клиент", Clients);
            Deals = SampleData.AddCollection(Repo, "Сделки").Id!.Value;
            Relation(Deals, "Клиент", Clients);
            var notes = SampleData.AddCollection(Repo, "Заметки").Id!.Value;
            Relation(notes, "Счет", Bills);
        }

        private int Relation(int collection, string name, int target)
        {
            var field = SampleData.AddField(Repo, collection, name, "relation");
            field.ConfigJson = new RelationConfig(target, false).ToConfigJson();
            Repo.UpdateField(field);
            return field.Id!.Value;
        }

        public int AddBill(string title) => SampleData.AddRecord(Repo, Bills, title).Id!.Value;

        public List<int> AddBills(int count, Func<int, string>? title = null) =>
            Repo.BulkAddRecords(Bills, [], Enumerable.Range(1, count).Select(i => new BulkRecordRow(title?.Invoke(i) ?? $"Счет {i}", [])),
                count, default);

        // Ребра сырым SQL — так их пишет и прежняя версия программы.
        public void InsertEdges(int field, int to, IReadOnlyList<(int From, int TargetDbId)> edges)
        {
            using var tx = Context.Database.BeginTransaction();
            foreach (var chunk in edges.Chunk(500))
                Context.Database.ExecuteSqlRaw(
                    "INSERT INTO db_record_links (FieldId, FromRecordId, ToRecordId, TargetDbId, Position) VALUES " +
                    string.Join(", ", chunk.Select(e => $"({field}, {e.From}, {to}, {e.TargetDbId}, 1000)")));
            tx.Commit();
        }

        public List<RelationBacklinkGroup> Groups(int? record = null) =>
            Repo.RelationBacklinkGroups(0, record ?? Client, Clients, Repo.RelationBaseId);

        public RelationBacklinkPage Page(RelationBacklinkGroup group, string? query, int? before, int take) =>
            Repo.RelationBacklinkSources(group, Client, query, before, take);

        public void Dispose()
        {
            _db?.Dispose();
            Pg?.Dispose();
        }
    }
}

/// <summary>Ссылки из другой подключенной базы: свой раздел с именем базы, отбор по номеру базы
/// записи; база без допуска не читается.</summary>
[Collection(RelationResolverCollection.Name)]
public sealed class RelationBacklinkWorkspaceTests
{
    [Fact]
    public void Links_from_another_open_database_get_their_own_section()
    {
        var appDb = TempDatabase.NewTempPath();
        var def   = TempDatabase.NewTempPath();
        var other = TempDatabase.NewTempPath();
        try
        {
            using var registry = DatabaseRegistry.Open(def, appDb);
            var defDb = DatabaseManager.Open(registry.GetAll().First(d => d.IsDefault).Id, "def", def);
            var ws = Workspace.Attach(registry, defDb);
            var accounting = ws.Mount(registry.Add("Учет", other))!;
            var invoices = accounting.Constructor;
            RelationDbResolver.Configure(ws);
            try
            {
                var own = defDb.Constructor.RelationBaseId;
                var clients = SampleData.AddCollection(defDb.Constructor, "Клиенты").Id!.Value;
                var client  = SampleData.AddRecord(defDb.Constructor, clients, "ООО Ромашка").Id!.Value;
                var bills   = SampleData.AddCollection(invoices, "Счета").Id!.Value;
                var bill    = SampleData.AddRecord(invoices, bills, "Счет 1").Id!.Value;
                var stray   = SampleData.AddRecord(invoices, bills, "Счет 2").Id!.Value;
                var field   = SampleData.AddField(invoices, bills, "Клиент", "relation");
                field.ConfigJson = new RelationConfig(clients, false, TargetDbId: own).ToConfigJson();
                invoices.UpdateField(field);
                invoices.SetLinks(field.Id!.Value, bill, [client]);
                // Ребро с базой цели 0 и тем же номером записи ведет в запись самой базы «Учет»,
                // а не в клиента: оно не считается.
                accounting.Context.Database.ExecuteSqlRaw(
                    $"INSERT INTO db_record_links (FieldId, FromRecordId, ToRecordId, TargetDbId, Position) VALUES ({field.Id}, {stray}, {client}, 0, 1000)");

                var reader = RelationBacklinkReader.Capture(defDb.Constructor, client, clients);
                var sections = reader.Load(default);
                Assert.Equal(2, sections.Count);
                Assert.Equal((0, null), (sections[0].SourceDbId, sections[0].BaseName));
                Assert.Empty(sections[0].Groups);
                var foreign = sections[1];
                Assert.Equal((invoices.RelationBaseId, "Учет", false), (foreign.SourceDbId, foreign.BaseName, foreign.Failed));
                Assert.Equal("Ссылаются на эту запись из базы «Учет»", RelationBacklinkText.Header(foreign.BaseName));
                var group = Assert.Single(foreign.Groups);
                Assert.Equal(("Счета", "Клиент", 1), (group.Collection, group.Field, group.Sources));
                Assert.Equal([own], group.Targets);
                Assert.Equal(["Счет 1"], reader.Sources(group, null, null, 200, default).Items.Select(s => s.Title));
                Assert.Equal(1, reader.Total(group, default));

                // Своя база не прочиталась — ее раздел «Ссылки не прочитаны», другие базы видны.
                var broken = RelationBacklinkReader.Capture(defDb.Constructor, client, clients);
                defDb.Context.Database.ExecuteSqlRaw("ALTER TABLE db_fields RENAME TO db_fields_hidden");
                try
                {
                    var partial = broken.Load(default);
                    Assert.Equal(2, partial.Count);
                    Assert.True(partial[0].Failed);
                    Assert.Equal((0, null), (partial[0].SourceDbId, partial[0].BaseName));
                    Assert.Single(partial[1].Groups);
                }
                finally { defDb.Context.Database.ExecuteSqlRaw("ALTER TABLE db_fields_hidden RENAME TO db_fields"); }

            }
            finally
            {
                RelationDbResolver.Configure(null);
                ws.CloseAll(TimeSpan.FromSeconds(30));
            }
        }
        finally { foreach (var p in new[] { other, def, appDb }) TempDatabase.Cleanup(p); }
    }
}
