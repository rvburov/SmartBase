using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

public sealed class RelationGlobalRefreshTests
{
    [Fact]
    public void Cross_database_auto_lookup_chain_refreshes_in_dependency_order_from_reverse_input_order()
    {
        using var f = new Fixture();
        f.A.Constructor.SetRecordTitle(f.RootRecord, "new");
        var work = ConstructorRepository.CaptureAutoRefreshGraph(
            [f.C.Constructor, f.B.Constructor, f.A.Constructor], [f.RootChange]);
        var results = work(CancellationToken.None);
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Applied, result.Message));
        Assert.Equal([f.NewChoice], f.B.Constructor.GetLinkTargetIds(f.MiddleAuto, f.MiddleRecord));
        Assert.Empty(f.C.Constructor.GetLinkEdges(f.LastAuto, f.OldRecord));
        Assert.Equal([(f.MiddleRecord, f.B.LinkId)], f.C.Constructor.GetLinkEdges(f.LastAuto, f.NewRecord));
    }

    [Fact]
    public void An_unavailable_upstream_database_preserves_the_whole_dependent_chain()
    {
        using var f = new Fixture();
        f.A.Constructor.SetRecordTitle(f.RootRecord, "new");
        var work = ConstructorRepository.CaptureAutoRefreshGraph(
            [f.C.Constructor, f.B.Constructor], [f.RootChange], refreshAll: true);
        var results = work(CancellationToken.None);
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.False(result.Applied));
        Assert.Contains(results, result => result.Status == RelationAutoStatus.Unavailable);
        f.AssertOldEdges();
    }

    [Fact]
    public void A_damaged_cross_database_cycle_is_rejected_before_any_rule_writes()
    {
        using var f = new Fixture();
        var toLast = f.B.Constructor.AddField(new DbField
        {
            CollectionId = f.MiddleCollection, Name = "Запись последней базы", FieldType = "relation",
            ConfigJson = new RelationConfig(f.LastCollection, true, TargetDbId: f.C.LinkId).ToConfigJson(),
        });
        f.B.Constructor.SetLinkEdges(toLast.Id!.Value, f.MiddleRecord, [new(f.C.LinkId, f.OldRecord)]);
        var lastValue = f.B.Constructor.AddField(new DbField
        {
            CollectionId = f.MiddleCollection, Name = "Связь последней базы", FieldType = "lookup",
            ConfigJson = new RelationComputedConfig(toLast.Id.Value, f.LastAuto.ToString()).ToConfigJson(),
        });
        // Имитируем существующую поврежденную настройку в файле базы, минуя валидатор UI.
        var entity = f.B.Context.DbFields.Single(x => x.Id == f.MiddleAuto);
        entity.ConfigJson = new RelationConfig(f.ChoicesCollection, true,
            Auto: new(lastValue.Id!.Value.ToString(), RelationConfig.TitleField)).ToConfigJson();
        f.B.Context.SaveChanges();
        f.A.Constructor.SetRecordTitle(f.RootRecord, "new");
        var work = ConstructorRepository.CaptureAutoRefreshGraph(
            [f.C.Constructor, f.B.Constructor, f.A.Constructor], [f.RootChange], refreshAll: true);
        var result = Assert.Single(work(CancellationToken.None));
        Assert.Equal(RelationAutoStatus.InvalidRule, result.Status);
        f.AssertOldEdges();
    }

    private sealed class Fixture : IDisposable
    {
        public TempDatabase A { get; } = new(dbId: 1);
        public TempDatabase B { get; } = new(dbId: 2);
        public TempDatabase C { get; } = new(dbId: 3);
        private readonly Action<RelationDataChange> _ignore = _ => { };
        private readonly IDisposable _resolver;
        public int RootCollection { get; }
        public int RootRecord { get; }
        public int MiddleCollection { get; }
        public int MiddleRecord { get; }
        public int ChoicesCollection { get; }
        public int OldChoice { get; }
        public int NewChoice { get; }
        public int MiddleAuto { get; }
        public int LastCollection { get; }
        public int OldRecord { get; }
        public int NewRecord { get; }
        public int LastAuto { get; }
        public RelationDataChange RootChange => new(A.Path, A.LinkId, RootCollection, 0, RootRecord);

        public Fixture()
        {
            // Имитируем приложение: изменения выпускают событие, один подготовленный запуск
            // ниже должен обновить весь граф. Промежуточных headless-пересчетов здесь нет.
            ConstructorRepository.RelationDataChanged += _ignore;
            _resolver = RelationDbResolver.UseScopedResolver(id =>
                id == A.LinkId ? A.Constructor : id == B.LinkId ? B.Constructor : id == C.LinkId ? C.Constructor : null);
            try
            {
                RootCollection = SampleData.AddCollection(A.Constructor, "Корневые значения").Id!.Value;
                RootRecord = SampleData.AddRecord(A.Constructor, RootCollection, "old").Id!.Value;
                MiddleCollection = SampleData.AddCollection(B.Constructor, "Промежуточные записи").Id!.Value;
                MiddleRecord = SampleData.AddRecord(B.Constructor, MiddleCollection, "Промежуточная запись").Id!.Value;
                ChoicesCollection = SampleData.AddCollection(B.Constructor, "Варианты").Id!.Value;
                OldChoice = SampleData.AddRecord(B.Constructor, ChoicesCollection, "old").Id!.Value;
                NewChoice = SampleData.AddRecord(B.Constructor, ChoicesCollection, "new").Id!.Value;
                var rootRelation = B.Constructor.AddField(new DbField
                {
                    CollectionId = MiddleCollection, Name = "Корневая запись", FieldType = "relation",
                    ConfigJson = new RelationConfig(RootCollection, true, TargetDbId: A.LinkId).ToConfigJson(),
                });
                B.Constructor.SetLinkEdges(rootRelation.Id!.Value, MiddleRecord, [new(A.LinkId, RootRecord)]);
                var rootLookup = B.Constructor.AddField(new DbField
                {
                    CollectionId = MiddleCollection, Name = "Ключ корня", FieldType = "lookup",
                    ConfigJson = new RelationComputedConfig(rootRelation.Id.Value).ToConfigJson(),
                });
                MiddleAuto = B.Constructor.AddField(new DbField
                {
                    CollectionId = MiddleCollection, Name = "Выбранный вариант", FieldType = "relation",
                    ConfigJson = new RelationConfig(ChoicesCollection, true,
                        Auto: new(rootLookup.Id!.Value.ToString(), RelationConfig.TitleField)).ToConfigJson(),
                }).Id!.Value;
                var selectedLookup = B.Constructor.AddField(new DbField
                {
                    CollectionId = MiddleCollection, Name = "Название варианта", FieldType = "lookup",
                    ConfigJson = new RelationComputedConfig(MiddleAuto).ToConfigJson(),
                });
                LastCollection = SampleData.AddCollection(C.Constructor, "Конечные записи").Id!.Value;
                OldRecord = SampleData.AddRecord(C.Constructor, LastCollection, "old").Id!.Value;
                NewRecord = SampleData.AddRecord(C.Constructor, LastCollection, "new").Id!.Value;
                LastAuto = C.Constructor.AddField(new DbField
                {
                    CollectionId = LastCollection, Name = "Промежуточная запись", FieldType = "relation",
                    ConfigJson = new RelationConfig(MiddleCollection, true, TargetDbId: B.LinkId,
                        Auto: new(RelationConfig.TitleField, selectedLookup.Id!.Value.ToString())).ToConfigJson(),
                }).Id!.Value;
                AssertOldEdges();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void AssertOldEdges()
        {
            Assert.Equal([OldChoice], B.Constructor.GetLinkTargetIds(MiddleAuto, MiddleRecord));
            Assert.Equal([(MiddleRecord, B.LinkId)], C.Constructor.GetLinkEdges(LastAuto, OldRecord));
            Assert.Empty(C.Constructor.GetLinkEdges(LastAuto, NewRecord));
        }

        public void Dispose()
        {
            _resolver.Dispose();
            ConstructorRepository.RelationDataChanged -= _ignore;
            C.Dispose();
            B.Dispose();
            A.Dispose();
        }
    }
}
