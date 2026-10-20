using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    private sealed class RelationSnapshotImport(List<CollectionSnapshot> snapshots)
    {
        public readonly List<CollectionSnapshot> Snapshots = snapshots;
        public readonly Dictionary<int, int> Collections = [];
        public readonly Dictionary<int, int> Fields = [];
        public readonly Dictionary<int, int> Records = [];
    }

    private static IEnumerable<CollectionSnapshot> RelationGroupCollections(GroupSnapshot group) =>
        group.Collections.Concat(group.Subgroups.SelectMany(RelationGroupCollections));

    private RelationSnapshotImport PrepareRelationSnapshotImport(IEnumerable<CollectionSnapshot> snapshots)
    {
        var list = snapshots.ToList();
        var collections = list.Select(s => s.Collection.Id ?? throw new InvalidOperationException("Нет идентификатора коллекции в снимке.")).ToHashSet();
        var records = list.SelectMany(s => s.Records).Select(r => r.Record.Id ?? throw new InvalidOperationException("Нет идентификатора записи в снимке.")).ToHashSet();
        if (collections.Count != list.Count || records.Count != list.Sum(s => s.Records.Count))
            throw new InvalidOperationException("Повторяющиеся идентификаторы в снимке.");
        if (list.Select(s => (s.SourceDatabaseIdentity, s.SourceDbId)).Distinct().Count() > 1)
            throw new InvalidOperationException("Снимок группы должен принадлежать одной исходной базе.");
        var allFields = list.SelectMany(s => s.Fields).Select(f => f.Id).ToList();
        if (allFields.Any(id => id is null or <= 0) || allFields.Distinct().Count() != allFields.Count)
            throw new InvalidOperationException("Неверные идентификаторы свойств в снимке.");
        foreach (var snap in list)
        {
            var fieldIds = snap.Fields.Where(f => f.Id.HasValue).Select(f => f.Id!.Value).ToHashSet();
            var sourceRecords = snap.Records.Select(r => r.Record.Id!.Value).ToHashSet();
            foreach (var field in snap.Fields)
                if (RelationConfig.FromConfig(field.ConfigJson) is { } config
                    && !(IsRelationSourceDatabase(config.TargetDbId, snap.SourceDbId) && collections.Contains(config.TargetCollectionId)))
                {
                    var target = SnapshotExternalDatabase(snap, config.TargetDbId);
                    target.Demand(PermAction.ViewCollection, config.TargetCollectionId);
                    if (!target.RelationLifecycleContext.DbCollections.AsNoTracking().Any(c => c.Id == config.TargetCollectionId))
                        throw new InvalidOperationException("Целевая коллекция связи из снимка не найдена.");
                }
            foreach (var edge in snap.Links)
            {
                if (!fieldIds.Contains(edge.FieldId) || !sourceRecords.Contains(edge.FromRecordId)
                    || snap.Fields.First(f => f.Id == edge.FieldId).FieldType != "relation")
                    throw new InvalidOperationException("Снимок содержит связь без исходного свойства или записи.");
                if (IsRelationSourceDatabase(edge.Target.DbId, snap.SourceDbId) && records.Contains(edge.Target.RecordId)) continue;
                var target = SnapshotExternalDatabase(snap, edge.Target.DbId);
                if (target.CollectionOfRecord(edge.Target.RecordId) is not int targetCollection)
                    throw new InvalidOperationException("Целевая запись связи из снимка не найдена.");
                target.Demand(PermAction.ViewCollection, targetCollection);
            }
            foreach (var config in snap.Fields.Where(f => f.FieldType == "relation")
                         .Select(f => (f.Id, Config: RelationConfig.FromConfig(f.ConfigJson))).Where(f => f.Config is { Single: true }))
                if (snap.Links.Where(l => l.FieldId == config.Id).GroupBy(l => l.FromRecordId).Any(g => g.Select(l => l.Target).Distinct().Count() > 1))
                    throw new InvalidOperationException("Снимок содержит несколько целей у свойства с одной связью.");
        }
        return new(list);
    }

    private ConstructorRepository SnapshotExternalDatabase(CollectionSnapshot snap, int dbId)
    {
        if (IsRelationSourceDatabase(dbId, snap.SourceDbId))
        {
            if (snap.SourceDatabaseIdentity == RelationDatabaseIdentity) return this;
            if (snap.SourceDbId > 0 && RelationDbResolver.RepoFor(snap.SourceDbId) is { } source
                && source.RelationDatabaseIdentity == snap.SourceDatabaseIdentity) return source;
        }
        else if (snap.DatabaseIdentities.TryGetValue(dbId, out var identity)
                 && RelationDbResolver.RepoFor(dbId) is { } target && target.RelationDatabaseIdentity == identity) return target;
        throw new InvalidOperationException("Не удалось проверить внешнюю базу связи. Импорт отменен до записи данных.");
    }

    private void CompleteRelationSnapshotImport(RelationSnapshotImport state)
    {
        ValidateRelationSnapshotProjections(state);
        var targetDbId = RelationDbResolver.DbIdFor(this);
        if (state.Snapshots.FirstOrDefault() is { } first)
            RemapCopiedRelationConfigs(state.Collections, state.Fields, first.SourceDbId, targetDbId,
                first.SourceDatabaseIdentity != RelationDatabaseIdentity);
        foreach (var snap in state.Snapshots)
        {
            var sameDatabase = snap.SourceDatabaseIdentity == RelationDatabaseIdentity;
            foreach (var edge in snap.Links)
            {
                var internalTarget = IsRelationSourceDatabase(edge.Target.DbId, snap.SourceDbId) && state.Records.ContainsKey(edge.Target.RecordId);
                var targetId = internalTarget ? state.Records[edge.Target.RecordId] : edge.Target.RecordId;
                var targetDb = internalTarget ? 0 : sameDatabase ? edge.Target.DbId
                    : RebaseRelationDatabase(edge.Target.DbId, snap.SourceDbId, targetDbId, true);
                db.DbRecordLinks.Add(new DbRecordLinkEntity
                {
                    FieldId = state.Fields[edge.FieldId], FromRecordId = state.Records[edge.FromRecordId],
                    ToRecordId = targetId, TargetDbId = targetDb, Position = edge.Position,
                });
            }
        }
        db.SaveChanges();
    }
}
