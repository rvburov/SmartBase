using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>Постоянный номер базы для межбазовых связей и перевод старых связей, которые хранили
/// номер реестра того компьютера, где их создали.</summary>
public sealed class RelationBaseIdentityTests
{
    /// <summary>У базы без номера пустое чтение помнится недолго: списки подключенных баз на
    /// UI-потоке не спрашивают db_meta на каждой загрузке страницы, а номер, заведенный за это время
    /// другим компьютером, становится виден.</summary>
    [Fact]
    public void A_missing_link_number_is_remembered_for_a_while()
    {
        var path = TempDatabase.NewTempPath();
        var previous = ConstructorRepository.MissedBaseIdTtl;
        try
        {
            using (DatabaseManager.Open(1, "A", path)) { }
            using var context = new AppDbContext(path);
            context.Database.ExecuteSqlRaw($"DELETE FROM db_meta WHERE Key = '{ConstructorRepository.RelationBaseKey}'");
            ConstructorRepository.ForgetRelationBaseId(path);
            ConstructorRepository.MissedBaseIdTtl = 2000;
            var repo = new ConstructorRepository(context);
            Assert.Equal(0, repo.RelationBaseId);
            context.Database.ExecuteSqlRaw($"INSERT INTO db_meta (Key, Value) VALUES ('{ConstructorRepository.RelationBaseKey}', '1234567')");
            Assert.Equal(0, repo.RelationBaseId);
            Thread.Sleep(2500);
            Assert.Equal(1_234_567, repo.RelationBaseId);
        }
        finally
        {
            ConstructorRepository.MissedBaseIdTtl = previous;
            ConstructorRepository.ForgetRelationBaseId(path);
            TempDatabase.Cleanup(path);
        }
    }

    [Fact]
    public void Every_database_keeps_one_link_number_for_all_connections_and_reopens()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            int first;
            using (var db = DatabaseManager.Open(1, "A", path))
            {
                first = db.Constructor.RelationBaseId;
                Assert.True(RelationDbResolver.IsLinkId(first));
                // Другое подключение той же базы читает номер из db_meta, а не из памяти окна.
                ConstructorRepository.ForgetRelationBaseId(path);
                using var other = new AppDbContext(path);
                Assert.Equal(first, new ConstructorRepository(other).RelationBaseId);
            }
            // Другой номер реестра (другой компьютер или повторное добавление) номер не меняет.
            using var reopened = DatabaseManager.Open(42, "A", path);
            Assert.Equal(first, reopened.Constructor.RelationBaseId);
            using var second = new TempDatabase(dbId: 1);
            Assert.NotEqual(first, second.LinkId);
        }
        finally { TempDatabase.Cleanup(path); }
    }

    [Fact]
    public void Legacy_links_move_to_the_link_number_when_this_computer_knows_the_target()
    {
        using var f = new Fixture();
        // На этом компьютере номер реестра 7 — база с клиентами: все признаки сходятся.
        var result = Migrate(f, (1, f.Source), (7, f.Target), (9, f.Other));
        Assert.Equal(new RelationBaseMigrationResult(1, 0), result);
        Assert.All(f.Edges(), e => Assert.Equal(f.Target.LinkId, e.TargetDbId));
        Assert.Equal(f.Target.LinkId, f.Config().TargetDbId);
        // Повторный проход ничего не просматривает: в базе отмечено, что старых связей нет.
        Assert.Equal(new RelationBaseMigrationResult(0, 0), Migrate(f, (1, f.Source), (7, f.Target)));
    }

    [Fact]
    public void A_registry_number_that_means_another_database_here_is_not_trusted()
    {
        using var f = new Fixture();
        // У коллеги под номером 7 — другая база, где нет этих записей: связь не переводится.
        var result = Migrate(f, (1, f.Source), (7, f.Other), (8, f.Target));
        Assert.Equal(new RelationBaseMigrationResult(0, 1), result);
        Assert.All(f.Edges(), e => Assert.Equal(7, e.TargetDbId));
        Assert.Equal(7, f.Config().TargetDbId);
        Assert.Equal(RelationProperty.UnknownBase,
            f.Source.Constructor.ResolveRelationLabels([new RelationTarget(7, f.Clients[0])], RelationConfig.TitleField)
                .Single().Value);

        // Позже подключилась нужная база под этим номером — связь переводится оставшимся списком.
        Assert.Equal(new RelationBaseMigrationResult(1, 0), Migrate(f, (1, f.Source), (7, f.Target)));
        Assert.All(f.Edges(), e => Assert.Equal(f.Target.LinkId, e.TargetDbId));
    }

    [Fact]
    public void Two_matching_databases_leave_the_base_undetermined()
    {
        using var f = new Fixture();
        using var copy = new TempDatabase(dbId: 3);
        // Вторая база с той же коллекцией и теми же записями: выбрать наугад нельзя.
        f.FillLike(copy);
        var result = Migrate(f, (1, f.Source), (7, f.Target), (8, copy));
        Assert.Equal(new RelationBaseMigrationResult(0, 1), result);
        Assert.All(f.Edges(), e => Assert.Equal(7, e.TargetDbId));
    }

    [Fact]
    public void A_legacy_edge_already_written_with_the_link_number_is_not_duplicated()
    {
        using var f = new Fixture();
        f.Source.Context.DbRecordLinks.Add(new DbRecordLinkEntity
        {
            FieldId = f.Field, FromRecordId = f.Order, ToRecordId = f.Clients[0], TargetDbId = f.Target.LinkId, Position = 9000,
        });
        f.Source.Context.SaveChanges();
        Assert.Equal(new RelationBaseMigrationResult(1, 0), Migrate(f, (1, f.Source), (7, f.Target)));
        var edges = f.Edges();
        Assert.Equal(f.Clients.Length, edges.Count);
        Assert.Equal(f.Clients.Order(), edges.Select(e => e.ToRecordId).Order());
    }

    [Fact]
    public void Choosing_the_base_in_the_property_rebinds_legacy_links_after_checking_records()
    {
        using var f = new Fixture();
        using var resolver = RelationDbResolver.UseScopedResolver(id =>
            id == f.Target.LinkId ? f.Target.Constructor : id == f.Other.LinkId ? f.Other.Constructor : null);
        var field = f.Source.Constructor.GetFields(f.Orders).Single(x => x.Id == f.Field);

        // Неподходящая база: записей нет — отказ без изменений.
        field.ConfigJson = (f.Config() with { TargetDbId = f.Other.LinkId }).ToConfigJson();
        Assert.Throws<InvalidOperationException>(() => f.Source.Constructor.UpdateField(field));
        Assert.All(f.Edges(), e => Assert.Equal(7, e.TargetDbId));

        field = f.Source.Constructor.GetFields(f.Orders).Single(x => x.Id == f.Field);
        field.ConfigJson = (f.Config() with { TargetDbId = f.Target.LinkId }).ToConfigJson();
        f.Source.Constructor.UpdateField(field);
        Assert.All(f.Edges(), e => Assert.Equal(f.Target.LinkId, e.TargetDbId));
        Assert.Equal(f.Target.LinkId, f.Config().TargetDbId);
    }

    private static RelationBaseMigrationResult Migrate(Fixture f, params (int Registry, TempDatabase Db)[] mounted) =>
        ConstructorRepository.MigrateLegacyRelationTargets(f.Source.Path,
            mounted.Select(m => new RelationBaseCandidate(m.Registry, m.Db.LinkId, m.Db.Path)).ToArray());

    // Заказы (база-источник) ссылаются на клиентов другой базы по старому номеру реестра 7 —
    // так хранила связи прежняя версия программы на компьютере, где их создали.
    private sealed class Fixture : IDisposable
    {
        public TempDatabase Source { get; } = new(dbId: 1, name: "Заказы");
        public TempDatabase Target { get; } = new(dbId: 7, name: "Клиенты");
        public TempDatabase Other { get; } = new(dbId: 9, name: "Другая");
        public int ClientsCollection { get; }
        public int[] Clients { get; }
        public int Orders { get; }
        public int Order { get; }
        public int Field { get; }

        public Fixture()
        {
            ClientsCollection = SampleData.AddCollection(Target.Constructor, "Клиенты").Id!.Value;
            Clients = [.. Enumerable.Range(1, 3).Select(i => SampleData.AddRecord(Target.Constructor, ClientsCollection, $"Клиент {i}").Id!.Value)];
            Orders = SampleData.AddCollection(Source.Constructor, "Заказы").Id!.Value;
            Order = SampleData.AddRecord(Source.Constructor, Orders, "Заказ").Id!.Value;
            var field = new DbFieldEntity
            {
                CollectionId = Orders, Name = "Клиент", FieldType = "relation", Position = 5000,
                ConfigJson = new RelationConfig(ClientsCollection, false, TargetDbId: 7).ToConfigJson(),
            };
            Source.Context.DbFields.Add(field);
            Source.Context.SaveChanges();
            Field = field.Id;
            for (var i = 0; i < Clients.Length; i++)
                Source.Context.DbRecordLinks.Add(new DbRecordLinkEntity
                {
                    FieldId = Field, FromRecordId = Order, ToRecordId = Clients[i], TargetDbId = 7, Position = (i + 1) * 1000,
                });
            Source.Context.SaveChanges();
        }

        // Та же форма данных в другой базе: коллекция и записи с теми же номерами.
        public void FillLike(TempDatabase db)
        {
            var collection = SampleData.AddCollection(db.Constructor, "Клиенты").Id!.Value;
            Assert.Equal(ClientsCollection, collection);
            foreach (var id in Clients) Assert.Equal(id, SampleData.AddRecord(db.Constructor, collection, "Копия").Id);
        }

        public List<DbRecordLinkEntity> Edges()
        {
            using var context = new AppDbContext(Source.Path);
            return context.DbRecordLinks.AsNoTracking().Where(l => l.FieldId == Field).ToList();
        }

        public RelationConfig Config()
        {
            using var context = new AppDbContext(Source.Path);
            return RelationConfig.FromConfig(context.DbFields.AsNoTracking().Single(x => x.Id == Field).ConfigJson)!;
        }

        public void Dispose()
        {
            Other.Dispose();
            Target.Dispose();
            Source.Dispose();
        }
    }
}
