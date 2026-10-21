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
}
