using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    public List<(RelationTarget Target, string Display)> GetRelationLinks(int fieldId, int fromRecordId) =>
        GetRelationLinksBatch(fieldId, [fromRecordId]).GetValueOrDefault(fromRecordId, []);

    public Dictionary<int, List<(RelationTarget Target, string Display)>> GetRelationLinksBatch(int fieldId,
        IReadOnlyList<int> fromRecordIds)
    {
        var result = new Dictionary<int, List<(RelationTarget Target, string Display)>>();
        if (fromRecordIds.Count == 0) return result;
        var field = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fieldId);
        if (field is null || !CanReadRelationCollection(field.CollectionId)) return result;
        var cfg = RelationConfig.FromConfig(field.ConfigJson);
        if (cfg is null) return result;
        var edges = (from l in db.DbRecordLinks.AsNoTracking()
                     join source in db.DbRecords.AsNoTracking() on l.FromRecordId equals source.Id
                     where l.FieldId == fieldId && source.CollectionId == field.CollectionId && fromRecordIds.Contains(l.FromRecordId)
                     orderby l.Position, l.Id
                     select new { l.FromRecordId, l.TargetDbId, l.ToRecordId }).ToList();
        using var scope = RelationPropertyResolution.Enter(DbPath, fieldId);
        var labels = scope is null
            ? edges.Select(e => new RelationTarget(e.TargetDbId, e.ToRecordId)).Distinct().ToDictionary(t => t, _ => RelationProperty.Cycle)
            : ResolveRelationLabels(edges.Select(e => new RelationTarget(e.TargetDbId, e.ToRecordId)).ToList(), cfg.DisplayField, cfg.TargetDbId);
        foreach (var edge in edges)
        {
            var target = new RelationTarget(edge.TargetDbId, edge.ToRecordId);
            if (!labels.TryGetValue(target, out var display)) continue;
            if (!result.TryGetValue(edge.FromRecordId, out var links)) result[edge.FromRecordId] = links = [];
            links.Add((target, display));
        }
        return result;
    }

    public List<RelationPropertyOption> GetRelationPropertyOptions(int collectionId)
    {
        if (!CanReadRelationCollection(collectionId)) return [];
        var result = new List<RelationPropertyOption>
        {
            new(RelationConfig.TitleField, GetSystemFieldNames(collectionId).GetValueOrDefault(TitleKey, "Название"), "text"),
        };
        result.AddRange(GetFields(collectionId).Where(f => f.Id.HasValue)
            .Select(f => new RelationPropertyOption(f.Id!.Value.ToString(), f.Name, f.FieldType)));
        return result;
    }

    public bool CanReadRelationCollection(int collectionId) =>
        Permissions?.Can(PermAction.ViewCollection, collectionId) ?? true;

    /// <summary>Значения выбранного свойства в порядке записей. Запрет чтения отличается от пустой коллекции.</summary>
    public List<RelationPropertyValue> ReadRelationPropertyValues(int collectionId, string fieldKey,
        IReadOnlyCollection<int>? ids = null)
    {
        Demand(PermAction.ViewCollection, collectionId);
        if (ids is { Count: 0 }) return [];
        var query = db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId);
        if (ids is not null) query = query.Where(r => ids.Contains(r.Id));
        var records = query.OrderBy(r => r.Position).ThenBy(r => r.Id).ToList();
        if (records.Count == 0) return [];
        if (fieldKey == RelationConfig.TitleField)
            return records.Select(r => new RelationPropertyValue(r.Id, r.Title ?? "", r.Title ?? "", r.Title ?? "", "text", null)).ToList();
        var field = int.TryParse(fieldKey, out var fid)
            ? db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fid && f.CollectionId == collectionId)
            : null;
        if (field is null)
            return records.Select(r => new RelationPropertyValue(r.Id, r.Title ?? "", "", RelationProperty.MissingField, "", null)).ToList();
        var recordIds = records.Select(r => r.Id).ToList();
        var rawValues = db.DbFieldValues.AsNoTracking()
            .Where(v => v.FieldId == field.Id && recordIds.Contains(v.RecordId))
            .ToDictionary(v => v.RecordId, v => v.Value);
        Dictionary<int, string>? relationDisplay = null;
        Dictionary<int, string>? relationRaw = null;
        if (field.FieldType is "lookup" or "rollup")
        {
            var model = GetFields(collectionId).Single(f => f.Id == field.Id);
            var values = GetRelationComputedRawValues(recordIds, model);
            return records.Select(r =>
            {
                var value = values.GetValueOrDefault(r.Id);
                return new RelationPropertyValue(r.Id, r.Title ?? "", value?.Raw ?? "", value?.Display ?? "",
                    value?.FieldType ?? "", value?.ConfigJson);
            }).ToList();
        }
        if (field.FieldType == "relation")
        {
            using var scope = RelationPropertyResolution.Enter(DbPath, field.Id);
            if (scope is null)
                relationDisplay = records.ToDictionary(r => r.Id, _ => RelationProperty.Cycle);
            else
            {
                var cfg = RelationConfig.FromConfig(field.ConfigJson);
                relationDisplay = cfg is null ? [] : ReadRelationLabels(field.Id, recordIds, cfg);
            }
            var ownDbId = RelationDbResolver.DbIdFor(this);
            var edges = db.DbRecordLinks.AsNoTracking().Where(l => l.FieldId == field.Id && recordIds.Contains(l.FromRecordId))
                .OrderBy(l => l.Position).ThenBy(l => l.Id).ToList();
            relationRaw = edges.GroupBy(l => l.FromRecordId).ToDictionary(g => g.Key,
                g => JsonSerializer.Serialize(g.Select(l => new RelationTarget(l.TargetDbId == 0 ? ownDbId : l.TargetDbId, l.ToRecordId))));
        }
        return records.Select(r =>
        {
            var raw = field.FieldType switch
            {
                "created_at" => r.CreatedAt ?? "", "updated_at" => r.UpdatedAt ?? "",
                "created_by" => r.CreatedBy ?? "", "updated_by" => r.UpdatedBy ?? "",
                "relation" => relationRaw?.GetValueOrDefault(r.Id, "[]") ?? "[]",
                _ => rawValues.GetValueOrDefault(r.Id, ""),
            };
            var display = relationDisplay is not null ? relationDisplay.GetValueOrDefault(r.Id, "")
                : RelationProperty.Format(raw, field.FieldType, field.ConfigJson);
            return new RelationPropertyValue(r.Id, r.Title ?? "", raw, display, field.FieldType, field.ConfigJson);
        }).ToList();
    }

    /// <summary>Пакетный показ целей из их собственных коллекций; запрет не раскрывает заголовок.</summary>
    public Dictionary<int, string> GetRecordPropertyValues(IReadOnlyCollection<int> ids, string fieldKey)
    {
        if (ids.Count == 0) return [];
        var records = db.DbRecords.AsNoTracking().Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.CollectionId }).ToList();
        var result = new Dictionary<int, string>();
        foreach (var group in records.GroupBy(r => r.CollectionId))
        {
            if (!CanReadRelationCollection(group.Key))
            {
                foreach (var r in group) result[r.Id] = RelationProperty.NoAccess;
                continue;
            }
            foreach (var value in ReadRelationPropertyValues(group.Key, fieldKey, group.Select(r => r.Id).ToList()))
                result[value.RecordId] = value.Display;
        }
        return result;
    }

    public List<RelationPropertyValue> SearchRelationRecords(int collectionId, string fieldKey, string? query,
        int excludeId = 0, int take = 200) => SearchRelationRecordsCore(collectionId, fieldKey, query, excludeId, take, default);

    public Task<List<RelationPropertyValue>> SearchRelationRecordsAsync(int collectionId, string fieldKey, string? query,
        int excludeId = 0, int take = 200, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connections = CaptureAutoConnections(new DbField { CollectionId = collectionId });
        return Task.Run(() => WithAutoConnections(connections, repo =>
        {
            var result = repo.SearchRelationRecordsCore(collectionId, fieldKey, query, excludeId, take, ct);
            ct.ThrowIfCancellationRequested();
            return result;
        }), ct);
    }

    public Task<RelationPickerData> GetRelationPickerDataAsync(int recordId, RelationConfig cfg,
        string? query, IReadOnlyList<RelationTarget> selected, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sourceCollection = CollectionOfRecord(recordId);
        Demand(PermAction.ViewCollection, sourceCollection);
        var connections = CaptureAutoConnections(new DbField { CollectionId = sourceCollection ?? 0, FieldType = "relation", ConfigJson = cfg.ToConfigJson() });
        var targets = selected.ToArray();
        return Task.Run(() => WithAutoConnections(connections, repo =>
        {
            ct.ThrowIfCancellationRequested();
            var target = cfg.TargetDbId == 0 ? repo : RelationDbResolver.RepoFor(cfg.TargetDbId);
            var found = target?.SearchRelationRecordsCore(cfg.TargetCollectionId, cfg.EffectiveSearchField,
                query, cfg.TargetDbId == 0 ? recordId : 0, 201, ct) ?? [];
            ct.ThrowIfCancellationRequested();
            var more = found.Count > 200;
            var candidateIds = found.Take(200).Select(v => v.RecordId).ToList();
            var display = target?.ReadRelationPropertyValues(cfg.TargetCollectionId, cfg.DisplayField, candidateIds) ?? [];
            var byId = display.ToDictionary(v => v.RecordId);
            var ordered = candidateIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            var labels = repo.ResolveRelationLabels(targets, cfg.DisplayField, cfg.TargetDbId);
            ct.ThrowIfCancellationRequested();
            var search = string.IsNullOrWhiteSpace(query) ? new Dictionary<RelationTarget, string>()
                : cfg.EffectiveSearchField == cfg.DisplayField ? labels : repo.ResolveRelationLabels(targets, cfg.EffectiveSearchField, cfg.TargetDbId);
            var titles = cfg.DisplayField == RelationConfig.TitleField ? new Dictionary<RelationTarget, string>()
                : repo.ResolveRelationLabels(targets, RelationConfig.TitleField);
            ct.ThrowIfCancellationRequested();
            return new RelationPickerData(ordered, labels, search, titles, more);
        }), ct);
    }

    private List<RelationPropertyValue> SearchRelationRecordsCore(int collectionId, string fieldKey, string? query,
        int excludeId, int take, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanReadRelationCollection(collectionId)) return [];
        take = Math.Clamp(take, 1, 1000);
        var term = query?.Trim() ?? "";
        if (fieldKey == RelationConfig.TitleField)
            return SearchRecordTitles(collectionId, query, take, excludeId)
                .Select(r => new RelationPropertyValue(r.Id, r.Title, r.Title, r.Title, "text", null)).ToList();
        if (term.Length == 0)
        {
            var first = db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId && r.Id != excludeId)
                .OrderBy(r => r.Position).ThenBy(r => r.Id).Take(Math.Max(1, take)).Select(r => r.Id).ToList();
            return ReadRelationPropertyValues(collectionId, fieldKey, first)
                .Where(v => v.Display != RelationProperty.MissingField).ToList();
        }
        var field = int.TryParse(fieldKey, out var fieldId)
            ? db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fieldId && f.CollectionId == collectionId) : null;
        if (field is null) return [];
        if (field.FieldType is "text" or "markdown" or "url" or "select" or "status" or "user")
        {
            var rows = from r in db.DbRecords.AsNoTracking()
                       join v in db.DbFieldValues.AsNoTracking().Where(v => v.FieldId == field.Id) on r.Id equals v.RecordId
                       where r.CollectionId == collectionId && r.Id != excludeId
                       select new { r.Id, r.Position, v.Value };
            var pattern = "%" + EscapeLike(term) + "%";
            rows = db.IsPostgres ? rows.Where(r => EF.Functions.ILike(r.Value, pattern, "\\"))
                : rows.Where(r => SqlText.ContainsIgnoreCase(r.Value, term));
            var matching = rows.OrderBy(r => r.Position).ThenBy(r => r.Id).Take(Math.Max(1, take)).Select(r => r.Id).ToList();
            return ReadRelationPropertyValues(collectionId, fieldKey, matching);
        }
        var result = new List<RelationPropertyValue>();
        var firstPage = true;
        var lastPosition = 0;
        var lastId = 0;
        while (result.Count < take)
        {
            ct.ThrowIfCancellationRequested();
            var pageQuery = db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId && r.Id != excludeId);
            if (!firstPage) pageQuery = pageQuery.Where(r => r.Position > lastPosition || r.Position == lastPosition && r.Id > lastId);
            var page = pageQuery.OrderBy(r => r.Position).ThenBy(r => r.Id).Take(256)
                .Select(r => new { r.Id, r.Position }).ToList();
            if (page.Count == 0) break;
            var values = ReadRelationPropertyValues(collectionId, fieldKey, page.Select(r => r.Id).ToList());
            ct.ThrowIfCancellationRequested();
            foreach (var value in values)
                if (value.Display != RelationProperty.MissingField
                    && value.Display.Contains(term, StringComparison.CurrentCultureIgnoreCase))
                {
                    result.Add(value);
                    if (result.Count == take) break;
                }
            firstPage = false;
            lastPosition = page[^1].Position;
            lastId = page[^1].Id;
        }
        return result;
    }

    /// <summary>Пакетный показ ребер с полной идентичностью. Отсутствующая цель пропускается.</summary>
    internal bool IsExpectedRelationTargetDatabase(int actual, int expected)
    {
        var own = RelationDbResolver.DbIdFor(this);
        int Absolute(int id) => id == 0 && own > 0 ? own : id;
        return Absolute(actual) == Absolute(expected);
    }

    public Dictionary<RelationTarget, string> ResolveRelationLabels(IReadOnlyCollection<RelationTarget> targets,
        string fieldKey, int? expectedTargetDbId = null)
    {
        var labels = new Dictionary<RelationTarget, string>();
        foreach (var group in targets.Distinct().GroupBy(t => t.DbId))
        {
            var target = group.Key == 0 ? this : RelationDbResolver.RepoFor(group.Key);
            var differentPropertyDatabase = int.TryParse(fieldKey, out _) && expectedTargetDbId is int expected
                && !IsExpectedRelationTargetDatabase(group.Key, expected);
            // Численный id свойства имеет смысл только в базе, для которой выбран этот selector.
            // Чтение Title сохраняет проверку существования и прав, не читая одноименную цифру чужого поля.
            var values = target?.GetRecordPropertyValues(group.Select(t => t.RecordId).ToList(),
                differentPropertyDatabase ? RelationConfig.TitleField : fieldKey);
            foreach (var edge in group)
            {
                if (values is null) labels[edge] = UnavailableLabel(group.Key);
                else if (values.TryGetValue(edge.RecordId, out var text))
                    labels[edge] = differentPropertyDatabase && text != RelationProperty.NoAccess ? RelationProperty.MissingField : text;
            }
        }
        return labels;
    }

    private Dictionary<int, string> ReadRelationLabels(int fieldId, IReadOnlyCollection<int> records, RelationConfig cfg)
    {
        var edges = db.DbRecordLinks.AsNoTracking().Where(l => l.FieldId == fieldId && records.Contains(l.FromRecordId))
            .OrderBy(l => l.Position).ThenBy(l => l.Id).ToList();
        var labels = ResolveRelationLabels(edges.Select(l => new RelationTarget(l.TargetDbId, l.ToRecordId)).ToList(), cfg.DisplayField, cfg.TargetDbId);
        return edges.GroupBy(l => l.FromRecordId).ToDictionary(g => g.Key,
            g => string.Join(cfg.SeparatorString, g.Select(l => new RelationTarget(l.TargetDbId, l.ToRecordId))
                .Where(labels.ContainsKey).Select(t => labels[t])));
    }
}
