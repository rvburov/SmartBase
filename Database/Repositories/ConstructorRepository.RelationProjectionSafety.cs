using SmartBase.Core.Importers;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    // Один числовой ключ не может описывать одновременно перенумерованное внутреннее
    // свойство и прежнее внешнее. Такой перенос явно отклоняется.
    private static HashSet<int> RelationProjectionRemappedFields(IReadOnlyList<DbField> fields,
        IReadOnlyDictionary<int, int> collectionMap, IReadOnlyDictionary<int, int> fieldMap, int sourceDbId)
    {
        bool Changes(string? key) => RelationConfig.SplitKeys(key).Any(k =>
            int.TryParse(k, out var id) && fieldMap.TryGetValue(id, out var mapped) && mapped != id);
        var internalRelations = fields.Where(f => f.FieldType == "relation" && f.Id.HasValue)
            .Select(f => (Id: f.Id!.Value, Config: RelationConfig.FromConfig(f.ConfigJson)))
            .Where(f => f.Config is { } cfg && IsRelationSourceDatabase(cfg.TargetDbId, sourceDbId)
                && collectionMap.ContainsKey(cfg.TargetCollectionId)).ToDictionary(f => f.Id, f => f.Config!);
        var changed = internalRelations.Where(f => Changes(f.Value.DisplayField) || Changes(f.Value.SearchField)
            || Changes(f.Value.Auto?.TargetField)).Select(f => f.Key).ToHashSet();
        foreach (var field in fields.Where(f => f.FieldType is "lookup" or "rollup"))
            if (RelationComputedConfig.FromConfig(field.ConfigJson) is { } cfg && cfg.Aggregate != "count"
                && internalRelations.ContainsKey(cfg.RelationFieldId) && Changes(cfg.TargetField))
                changed.Add(cfg.RelationFieldId);
        return changed;
    }

    private static void ValidateRelationSnapshotProjections(RelationSnapshotImport state)
    {
        if (state.Snapshots.FirstOrDefault() is not { } first) return;
        var changed = RelationProjectionRemappedFields(state.Snapshots.SelectMany(s => s.Fields).ToList(),
            state.Collections, state.Fields, first.SourceDbId);
        if (state.Snapshots.SelectMany(s => s.Links).Any(edge => changed.Contains(edge.FieldId)
            && !(IsRelationSourceDatabase(edge.Target.DbId, first.SourceDbId) && state.Records.ContainsKey(edge.Target.RecordId))))
            throw new InvalidOperationException("При переносе смешанной связи нельзя сохранить выбранные свойства для всех ее целей. Выберите показ по названию и уберите зависимые вычисления перед переносом.");
    }

    private void ValidatePortableRelationProjections(RelationPortableSchema portable, RelationCopyMap map, CancellationToken ct)
    {
        var fields = portable.Fields.Select(f => new DbField { Id = f.Id, Name = f.Name,
            FieldType = f.FieldType, ConfigJson = f.ConfigJson ?? "{}" }).ToList();
        var changed = RelationProjectionRemappedFields(fields, map.Collections, map.Fields, portable.SourceDbId);
        if (changed.Count == 0) return;
        long after = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var staged = ReadPortableEdges(after);
            if (staged.Count == 0) return;
            var relevant = staged.Where(e => changed.Contains(e.Field)).ToList();
            var mappedTargets = ReadRelationRecordMap(relevant.Where(e => IsRelationSourceDatabase(e.Target.DbId, portable.SourceDbId))
                .Select(e => e.Target.RecordId));
            if (relevant.Any(e => !IsRelationSourceDatabase(e.Target.DbId, portable.SourceDbId) || !mappedTargets.ContainsKey(e.Target.RecordId)))
                throw new ImportException("При импорте смешанной связи нельзя сохранить выбранные свойства для всех ее целей. Используйте показ по названию и уберите зависимые вычисления перед экспортом.");
            after = staged[^1].Seq;
        }
    }
}
