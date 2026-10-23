using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    private AppDbContext RelationComputedContext => db;
    /// <summary>Значения lookup/rollup для страницы. Ребра читаются одним пакетом, свойства целей —
    /// пакетами по базе и свойству. Результат не материализуется в db_field_values.</summary>
    public Dictionary<int, Dictionary<int, string>> GetRelationComputedValues(
        IReadOnlyCollection<int> recordIds, IReadOnlyList<DbField>? fields = null) =>
        CalculateRelationComputedValues(recordIds, fields, null);

    /// <summary>Сырое типизированное значение вычисляемого свойства, отдельно от его подписи.
    /// Смена формата денег или даты не меняет ключ автосвязи.</summary>
    public Dictionary<int, RelationComputedValue> GetRelationComputedRawValues(
        IReadOnlyCollection<int> recordIds, DbField field)
    {
        var raw = new Dictionary<(int Record, int Field), RelationComputedValue>();
        CalculateRelationComputedValues(recordIds, [field], raw);
        return raw.Where(kv => kv.Key.Field == field.Id).ToDictionary(kv => kv.Key.Record, kv => kv.Value);
    }

    private Dictionary<int, Dictionary<int, string>> CalculateRelationComputedValues(
        IReadOnlyCollection<int> recordIds, IReadOnlyList<DbField>? fields,
        Dictionary<(int Record, int Field), RelationComputedValue>? raw)
    {
        var result = new Dictionary<int, Dictionary<int, string>>();
        if (recordIds.Count == 0) return result;
        fields ??= db.DbFields.AsNoTracking().Where(f => f.FieldType == "lookup" || f.FieldType == "rollup")
            .ToList().Select(ToDbField).ToList();
        if (fields.Count == 0) return result;
        var ids = recordIds.Distinct().ToList();
        var sourceCollections = db.DbRecords.AsNoTracking().Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.CollectionId }).ToList().ToDictionary(r => r.Id, r => r.CollectionId);
        if (sourceCollections.Count == 0) return result;
        var collectionIds = sourceCollections.Values.Distinct().ToArray();
        var computed = fields
            .Where(f => f.Id.HasValue && FieldTypeInfo.IsRelationComputed(f.FieldType)
                     && collectionIds.Contains(f.CollectionId)).ToList();
        if (computed.Count == 0) return result;

        var configs = computed.ToDictionary(f => f.Id!.Value, f => RelationComputedConfig.FromConfig(f.ConfigJson));
        var relationIds = configs.Values.Where(c => c is not null).Select(c => c!.RelationFieldId).Distinct().ToList();
        var relations = db.DbFields.AsNoTracking().Where(f => relationIds.Contains(f.Id))
            .ToList().ToDictionary(f => f.Id, ToDbField);
        var edges = db.DbRecordLinks.AsNoTracking()
            .Where(l => relationIds.Contains(l.FieldId) && ids.Contains(l.FromRecordId))
            .OrderBy(l => l.Position).ThenBy(l => l.Id)
            .Select(l => new { l.FieldId, l.FromRecordId, l.TargetDbId, l.ToRecordId }).ToList();
        var cache = new Dictionary<(int Db, int Collection, string Key), Dictionary<int, RelationPropertyValue>>();

        void Store(int recordId, int fieldId, string value, RelationComputedValue? typed = null)
        {
            if (!result.TryGetValue(recordId, out var values)) result[recordId] = values = [];
            values[fieldId] = value;
            if (raw is not null) raw[(recordId, fieldId)] = typed ?? new("", value, "", null);
        }

        foreach (var field in computed)
        {
            var fid = field.Id!.Value;
            var ownIds = ids.Where(id => sourceCollections.GetValueOrDefault(id) == field.CollectionId).ToArray();
            if (ownIds.Length == 0) continue;
            if (Permissions?.Can(PermAction.ViewCollection, field.CollectionId) == false)
            {
                foreach (var id in ownIds) Store(id, fid, "(нет доступа)");
                continue;
            }
            using var scope = RelationPropertyResolution.Enter(DbPath, fid);
            if (scope is null)
            {
                foreach (var id in ownIds) Store(id, fid, "(циклическая связь)");
                continue;
            }
            var cfg = configs[fid];
            if (cfg is null || !relations.TryGetValue(cfg.RelationFieldId, out var relation)
                || relation.CollectionId != field.CollectionId || relation.FieldType != "relation"
                || RelationConfig.FromConfig(relation.ConfigJson) is not { } relationConfig
                || field.FieldType == "lookup" && cfg.Aggregate != "lookup"
                || field.FieldType == "rollup" && !RelationComputedConfig.Aggregates.Contains(cfg.Aggregate, StringComparer.Ordinal))
            {
                foreach (var id in ownIds) Store(id, fid, "(вычисление не настроено)");
                continue;
            }

            var fieldEdges = edges.Where(e => e.FieldId == cfg.RelationFieldId).ToList();
            var bySource = fieldEdges.GroupBy(e => e.FromRecordId).ToDictionary(g => g.Key,
                g => g.Select(e => (e.TargetDbId, e.ToRecordId)).Distinct().ToArray());
            var propertyKey = cfg.Aggregate == "count" ? TitleKey : cfg.TargetField;
            var targets = new Dictionary<(int Db, int Record), RelationPropertyValue>();
            // Отказ — у группы целей одной базы, а не у пакета: запись без ребер в эту базу считается
            // как обычно, и значение записи не зависит от соседей по странице.
            var groupError = new Dictionary<int, string>();
            foreach (var group in fieldEdges.GroupBy(e => e.TargetDbId))
            {
                var wanted = group.Select(e => e.ToRecordId).Distinct().ToArray();
                if (int.TryParse(propertyKey, out _)
                    && !IsExpectedRelationTargetDatabase(group.Key, relationConfig.TargetDbId))
                {
                    // Идентификатор свойства принадлежит настроенной базе, а не старому ребру.
                    foreach (var id in wanted)
                        targets[(group.Key, id)] = new(id, "", "", RelationProperty.MissingField, "", null);
                    continue;
                }
                var key = (group.Key, relationConfig.TargetCollectionId, propertyKey);
                if (!cache.TryGetValue(key, out var values)) cache[key] = values = [];
                var missing = wanted.Where(id => !values.ContainsKey(id)).ToArray();
                if (missing.Length > 0)
                {
                    try
                    {
                        var targetRepo = ComputedTargetRepo(group.Key, relationConfig.TargetDbId);
                        if (targetRepo is null) { groupError[group.Key] = UnavailableLabel(group.Key); continue; }
                        if (propertyKey != TitleKey
                            && targetRepo.Permissions?.Can(PermAction.ViewCollection, relationConfig.TargetCollectionId) == false)
                        { groupError[group.Key] = RelationProperty.NoAccess; continue; }
                        // Чужой общий контекст не уходит в фоновый поток чтения страницы.
                        var reader = ComputedTargetReader(group.Key, targetRepo, out var owned);
                        using (owned)
                            foreach (var value in ReadComputedTargetProperties(reader, relationConfig.TargetCollectionId, propertyKey, missing))
                                values[value.RecordId] = value;
                    }
                    catch (PermissionDeniedException) { groupError[group.Key] = RelationProperty.NoAccess; continue; }
                    catch (Exception ex) when (ex is System.Data.Common.DbException or System.IO.IOException
                                                        or InvalidOperationException or ArgumentException)
                    { groupError[group.Key] = "(вычисление недоступно)"; continue; }
                }
                foreach (var id in wanted)
                    if (values.TryGetValue(id, out var value)) targets[(group.Key, id)] = value;
            }

            foreach (var id in ownIds)
            {
                var edgesOf = bySource.GetValueOrDefault(id, []);
                // Первая отказавшая база по порядку ребер записи.
                var failed = edgesOf.Select(e => groupError.GetValueOrDefault(e.TargetDbId)).FirstOrDefault(e => e is not null);
                var folded = failed is not null ? RelationComputedFold.Error(failed)
                    : RelationComputedFold.Fold(field.FieldType, cfg.Aggregate, relationConfig.SeparatorString, field.ConfigJson,
                        edgesOf.Where(targets.ContainsKey).Select(key => targets[key]).ToList());
                Store(id, fid, folded.Value, folded.Typed);
            }
        }
        return result;
    }

    private static List<RelationPropertyValue> ReadComputedTargetProperties(ConstructorRepository repo,
        int configuredCollection, string propertyKey, IReadOnlyCollection<int> requested)
    {
        if (propertyKey != TitleKey)
            return repo.ReadRelationPropertyValues(configuredCollection, propertyKey, requested);
        var ids = requested.ToList();
        var records = repo.RelationComputedContext.DbRecords.AsNoTracking().Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.CollectionId, r.Title }).ToList();
        var result = new List<RelationPropertyValue>();
        foreach (var group in records.GroupBy(r => r.CollectionId))
        {
            if (!repo.CanReadRelationCollection(group.Key))
                result.AddRange(group.Select(r => new RelationPropertyValue(r.Id, "", "", RelationProperty.NoAccess, "", null)));
            else
                result.AddRange(group.Select(r => new RelationPropertyValue(r.Id, r.Title ?? "", r.Title ?? "", r.Title ?? "", "text", null)));
        }
        return result;
    }

    /// <summary>Дописать вычисляемые значения к обычным значениям страницы. Если fieldIds заданы,
    /// вычисляются только столбцы окна, а не все свойства широкой коллекции.</summary>
    public void FillRelationComputedValues(IReadOnlyCollection<int> recordIds,
        Dictionary<int, Dictionary<int, string>> values, IReadOnlyCollection<int>? fieldIds = null)
    {
        if (recordIds.Count == 0 || fieldIds is { Count: 0 }) return;
        IReadOnlyList<DbField>? fields = null;
        if (fieldIds is not null)
        {
            var selected = fieldIds.ToList();
            fields = db.DbFields.AsNoTracking().Where(f => selected.Contains(f.Id)
                && (f.FieldType == "lookup" || f.FieldType == "rollup"))
                .ToList().Select(ToDbField).ToList();
            if (fields.Count == 0) return;
        }
        foreach (var (recordId, derived) in GetRelationComputedValues(recordIds, fields))
        {
            if (!values.TryGetValue(recordId, out var byField)) values[recordId] = byField = [];
            foreach (var (fieldId, value) in derived) byField[fieldId] = value;
        }
    }

    /// <summary>Вычисляемая колонка по всем записям коллекции в порядке таблицы (Position, Id): при
    /// повторах значения первой берется та же запись, что раньше. Считается столбцом целиком
    /// (<see cref="ReadRelationComputedColumn"/>).</summary>
    public Dictionary<int, string> GetRelationComputedColumnValues(int collectionId, DbField field)
    {
        var column = ReadRelationComputedColumn(collectionId, field);
        return db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId)
            .OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => r.Id).ToList()
            .ToDictionary(id => id, column.ValueOf);
    }

    /// <summary>Проверка настройки до сохранения: цель существует, числовой итог считает числа,
    /// граф зависимых свойств не замыкается на себя. Никаких данных не меняет.</summary>
    public void ValidateRelationComputedField(DbField field)
    {
        if (!FieldTypeInfo.IsRelationComputed(field.FieldType)) return;
        Demand(PermAction.ViewCollection, field.CollectionId);
        var stack = new HashSet<(string Db, int Field)>();
        var completed = new HashSet<(string Db, int Field)>();
        Visit(this, field, 0);

        void Visit(ConstructorRepository repo, DbField current, int depth)
        {
            if (depth >= 16) throw new InvalidOperationException("Цепочка свойств по связи слишком глубокая.");
            var key = (repo.DbPath, current.Id ?? -1);
            if (completed.Contains(key)) return;
            if (!stack.Add(key)) throw new InvalidOperationException("Свойства по связи образуют цикл. Выберите другое свойство.");
            try
            {
                if (FieldTypeInfo.IsRelationComputed(current.FieldType))
                {
                    var cfg = RelationComputedConfig.FromConfig(current.ConfigJson)
                        ?? throw new InvalidOperationException("Настройте связь и свойство для вычисления.");
                    if (current.FieldType == "lookup" && cfg.Aggregate != "lookup"
                        || current.FieldType == "rollup" && !RelationComputedConfig.Aggregates.Contains(cfg.Aggregate, StringComparer.Ordinal))
                        throw new InvalidOperationException("Выберите допустимый вид вычисления по связи.");
                    var relation = repo.RelationComputedContext.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == cfg.RelationFieldId);
                    if (relation is null || relation.CollectionId != current.CollectionId || relation.FieldType != "relation")
                        throw new InvalidOperationException("Выберите поле-связь этой коллекции.");
                    var relationCfg = RelationConfig.FromConfig(relation.ConfigJson)
                        ?? throw new InvalidOperationException("Сначала настройте целевую коллекцию поля-связи.");
                    var target = relationCfg.TargetDbId == 0 ? repo : RelationDbResolver.RepoFor(relationCfg.TargetDbId)
                        ?? throw new InvalidOperationException("База связанной записи недоступна.");
                    target.Demand(PermAction.ViewCollection, relationCfg.TargetCollectionId);
                    if (!target.RelationComputedContext.DbCollections.AsNoTracking().Any(c => c.Id == relationCfg.TargetCollectionId))
                        throw new InvalidOperationException("Целевая коллекция связи удалена.");
                    if (cfg.Aggregate == "count") return;
                    if (cfg.TargetField == TitleKey)
                    {
                        if (current.FieldType == "rollup")
                            throw new InvalidOperationException(RelationComputedConfig.IsDateAggregate(cfg.Aggregate)
                                ? "Для самой ранней или поздней даты выберите свойство-дату."
                                : "Для числового итога выберите числовое свойство.");
                        return;
                    }
                    var targetId = int.Parse(cfg.TargetField, CultureInfo.InvariantCulture);
                    var targetField = target.DbPath == DbPath && field.Id == targetId ? field
                        : target.RelationComputedContext.DbFields.AsNoTracking().Where(f => f.Id == targetId)
                            .ToList().Select(ToDbField).FirstOrDefault();
                    if (targetField is null || targetField.CollectionId != relationCfg.TargetCollectionId)
                        throw new InvalidOperationException("Выбранное свойство отсутствует в целевой коллекции.");
                    if (current.FieldType == "rollup" && RelationComputedConfig.IsNumberAggregate(cfg.Aggregate)
                        && targetField.FieldType is not ("number" or "rollup"))
                        throw new InvalidOperationException("Для числового итога выберите числовое свойство.");
                    if (RelationComputedConfig.IsDateAggregate(cfg.Aggregate) && !FieldTypeInfo.IsDateLike(targetField.FieldType))
                        throw new InvalidOperationException("Для самой ранней или поздней даты выберите свойство-дату.");
                    Visit(target, targetField, depth + 1);
                }
                else if (current.FieldType == "relation")
                {
                    var cfg = RelationConfig.FromConfig(current.ConfigJson);
                    if (cfg is null || cfg.DisplayKeys.All(k => k == TitleKey)) return;
                    var target = cfg.TargetDbId == 0 ? repo : RelationDbResolver.RepoFor(cfg.TargetDbId)
                        ?? throw new InvalidOperationException("База связанной записи недоступна.");
                    target.Demand(PermAction.ViewCollection, cfg.TargetCollectionId);
                    foreach (var displayKey in cfg.DisplayKeys)
                    {
                        if (!int.TryParse(displayKey, out var targetId)) continue;
                        var targetField = target.DbPath == DbPath && field.Id == targetId ? field
                            : target.RelationComputedContext.DbFields.AsNoTracking().Where(f => f.Id == targetId).ToList().Select(ToDbField).FirstOrDefault();
                        if (targetField is not null) Visit(target, targetField, depth + 1);
                    }
                }
            }
            finally { stack.Remove(key); }
            completed.Add(key);
        }
    }
}
