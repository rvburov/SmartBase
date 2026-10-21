using SmartBase.Core.Models;
using SmartBase.Database;

namespace SmartBase.Tests.Integration;

// Резолвер связей — общий на процесс: тест с рабочим пространством идет без параллельных соседей.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RelationResolverCollection
{
    public const string Name = "relation-resolver";
}

[Collection(RelationResolverCollection.Name)]
public sealed class RelationResolverWorkspaceTests
{
    [Fact]
    public void Link_numbers_find_databases_and_survive_reopening_the_window()
    {
        var appDb = TempDatabase.NewTempPath();
        var def   = TempDatabase.NewTempPath();
        var other = TempDatabase.NewTempPath();
        try
        {
            using var registry = DatabaseRegistry.Open(def, appDb);
            var defDb = DatabaseManager.Open(registry.GetAll().First(d => d.IsDefault).Id, "def", def);
            var entry = registry.Add("Клиенты", other);
            var ws = Workspace.Attach(registry, defDb);
            var mounted = ws.Mount(entry)!;
            RelationDbResolver.Configure(ws);
            try
            {
                var linkId = mounted.Constructor.RelationBaseId;
                Assert.Same(mounted.Constructor, RelationDbResolver.RepoFor(linkId));
                // Номер реестра этого компьютера — не номер связи: по нему базу не найти.
                Assert.Null(RelationDbResolver.RepoFor(entry.Id));
                Assert.Equal(entry.Id, RelationDbResolver.RegistryIdFor(linkId));
                Assert.Equal(entry.Id, RelationDbResolver.RegistryIdFor(mounted.Constructor));
                Assert.Contains(RelationDbResolver.Databases(), d => d.DbId == linkId && d.Name == "Клиенты");
                Assert.Contains(RelationDbResolver.RegistryDatabases(), d => d.DbId == entry.Id);
                Assert.Same(mounted.Constructor, RelationDbResolver.RegistryRepoFor(entry.Id));
                Assert.Contains(RelationDbResolver.MountedReposExcept(defDb.Constructor.RelationBaseId),
                    p => p.DbId == linkId && p.Repo == mounted.Constructor);
                RelationDbResolver.SaveKnownBases();

                // Окно открыто заново, база еще не подключена: номер из app.db подключает ее по запросу.
                ws.Unmount(entry.Id);
                RelationDbResolver.Configure(ws);
                var remounted = RelationDbResolver.RepoFor(linkId);
                Assert.NotNull(remounted);
                Assert.Equal(linkId, remounted!.RelationBaseId);
                Assert.NotNull(ws.Get(entry.Id));
            }
            finally
            {
                RelationDbResolver.Configure(null);
                ws.CloseAll(TimeSpan.FromSeconds(30));
            }
        }
        finally { foreach (var p in new[] { other, def, appDb }) TempDatabase.Cleanup(p); }
    }

    // Ссылки из другой открытой базы окно удаления видит по номеру базы и называет ее;
    // запрет удаления в такой связи держит удаление так же, как своя связь.
    [Fact]
    public void The_delete_window_sees_links_from_another_open_database()
    {
        var appDb = TempDatabase.NewTempPath();
        var def   = TempDatabase.NewTempPath();
        var other = TempDatabase.NewTempPath();
        try
        {
            using var registry = DatabaseRegistry.Open(def, appDb);
            var defDb = DatabaseManager.Open(registry.GetAll().First(d => d.IsDefault).Id, "def", def);
            var ws = Workspace.Attach(registry, defDb);
            var invoices = ws.Mount(registry.Add("Учет", other))!.Constructor;
            RelationDbResolver.Configure(ws);
            try
            {
                var clients = SampleData.AddCollection(defDb.Constructor, "Клиенты").Id!.Value;
                var client  = SampleData.AddRecord(defDb.Constructor, clients, "ООО Ромашка").Id!.Value;
                var bills   = SampleData.AddCollection(invoices, "Счета").Id!.Value;
                var bill    = SampleData.AddRecord(invoices, bills, "Счет 1").Id!.Value;
                var field   = SampleData.AddField(invoices, bills, "Клиент", "relation");
                void Restrict(bool on)
                {
                    field.ConfigJson = new RelationConfig(clients, true, TargetDbId: defDb.Constructor.RelationBaseId,
                        RestrictDelete: on).ToConfigJson();
                    invoices.UpdateField(field);
                }
                Restrict(false);
                invoices.SetLinks(field.Id!.Value, bill, [client]);

                var check = RelationDeleteImpact.CheckRecords(defDb.Constructor, [client]);
                Assert.Null(check.Blocked);
                Assert.Contains("«Счета · Клиент» (база «Учет») — 1 запись", check.Warning, StringComparison.Ordinal);
                Restrict(true);
                Assert.Contains("(база «Учет»)", RelationDeleteImpact.CheckRecords(defDb.Constructor, [client]).Blocked);
                Assert.NotNull(RelationDeleteImpact.CheckCollections(defDb.Constructor, [clients]).Blocked);
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
