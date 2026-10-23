using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    private AppDbContext RelationLifecycleContext => db;
    // Полные карты записей остаются во временной таблице: размер памяти не зависит от числа
    // записей коллекции. Карты немногочисленных коллекций/полей нужны также для ConfigJson.
    internal sealed class RelationCopyMap : IDisposable
    {
        private readonly ConstructorRepository _owner;
        internal readonly Dictionary<int, int> Collections = new();
        internal readonly Dictionary<int, int> Fields = new();
        internal readonly int SourceDbId;
        internal readonly int TargetDbId;
        internal readonly bool CrossDatabase;

        internal RelationCopyMap(ConstructorRepository owner, int sourceDbId, int targetDbId, bool crossDatabase)
        {
            _owner = owner;
            SourceDbId = sourceDbId;
            TargetDbId = targetDbId;
            CrossDatabase = crossDatabase;
            owner.RelationLifecycleContext.Database.OpenConnection();
            try
            {
                owner.RelationLifecycleContext.Database.ExecuteSqlRaw("CREATE TEMP TABLE _relation_copy_rmap(old_id INTEGER PRIMARY KEY, new_id INTEGER NOT NULL)");
                owner.RelationLifecycleContext.Database.ExecuteSqlRaw("CREATE TEMP TABLE _relation_copy_fmap(old_id INTEGER PRIMARY KEY, new_id INTEGER NOT NULL)");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void AddCollection(int oldId, int newId, IReadOnlyDictionary<int, int> fields)
        {
            Collections.Add(oldId, newId);
            foreach (var (oldField, newField) in fields)
            {
                Fields.Add(oldField, newField);
                _owner.RelationLifecycleContext.Database.ExecuteSqlInterpolated($"INSERT INTO _relation_copy_fmap(old_id,new_id) VALUES ({oldField},{newField})");
            }
        }

        internal void AddRecords(IReadOnlyDictionary<long, long> records) =>
            AddRecordPairs(records.Select(p => (checked((int)p.Key), checked((int)p.Value))));

        internal void AddRecords(IReadOnlyDictionary<int, int> records) =>
            AddRecordPairs(records.Select(p => (p.Key, p.Value)));

        private void AddRecordPairs(IEnumerable<(int Old, int New)> records)
        {
            if (_owner.RelationLifecycleContext.Database.GetDbConnection() is Npgsql.NpgsqlConnection pg)
            {
                CopyRows(pg, "_relation_copy_rmap", "old_id,new_id", records.Select(p => new object?[] { p.Old, p.New }));
                return;
            }
            using var command = _owner.RelationLifecycleContext.Database.GetDbConnection().CreateCommand();
            command.Transaction = _owner.RelationLifecycleContext.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = "INSERT INTO _relation_copy_rmap(old_id,new_id) VALUES (@old,@new)";
            var old = command.CreateParameter(); old.ParameterName = "@old"; command.Parameters.Add(old);
            var next = command.CreateParameter(); next.ParameterName = "@new"; command.Parameters.Add(next);
            foreach (var pair in records)
            {
                old.Value = pair.Old;
                next.Value = pair.New;
                command.ExecuteNonQuery();
            }
        }

        public void Dispose()
        {
            try { _owner.RelationLifecycleContext.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS _relation_copy_fmap"); }
            catch { /* временные карты уйдут с подключением */ }
            try { _owner.RelationLifecycleContext.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS _relation_copy_rmap"); }
            catch { /* временные карты уйдут с подключением */ }
            _owner.RelationLifecycleContext.Database.CloseConnection();
        }
    }

    private RelationCopyMap BeginRelationCopy(int sourceDbId = 0, int targetDbId = 0, bool crossDatabase = false) =>
        new(this, sourceDbId, targetDbId, crossDatabase);

    private static string RemapRelationFieldKey(string key, IReadOnlyDictionary<int, int> fields) =>
        int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && fields.TryGetValue(id, out var mapped)
            ? mapped.ToString(CultureInfo.InvariantCulture) : key;

    private static string RemapRelationFieldKeys(string composite, IReadOnlyDictionary<int, int> fields) =>
        RelationConfig.JoinKeys(RelationConfig.SplitKeys(composite).Select(key => RemapRelationFieldKey(key, fields)));

    private static bool IsRelationSourceDatabase(int edgeDbId, int sourceDbId) =>
        edgeDbId == 0 || sourceDbId > 0 && edgeDbId == sourceDbId;

    private static int RebaseRelationDatabase(int edgeDbId, int sourceDbId, int targetDbId, bool crossDatabase)
    {
        if (!crossDatabase) return edgeDbId;
        var absolute = edgeDbId == 0 ? sourceDbId : edgeDbId;
        if (absolute <= 0)
            throw new InvalidOperationException("Для переноса внешней связи требуется идентификатор исходной базы.");
        return targetDbId > 0 && absolute == targetDbId ? 0 : absolute;
    }

    private void RemapCopiedRelationConfigs(IReadOnlyDictionary<int, int> collectionMap,
        IReadOnlyDictionary<int, int> fieldMap, int sourceDbId, int targetDbId, bool crossDatabase = false,
        IReadOnlySet<int>? onlyNewFieldIds = null)
    {
        if (fieldMap.Count == 0) return;
        var newIds = fieldMap.Values.ToList();
        var fields = db.DbFields.Where(f => newIds.Contains(f.Id)).ToList();
        var oldByNew = fieldMap.ToDictionary(p => p.Value, p => p.Key);
        var internalRelations = new HashSet<int>();
        foreach (var field in fields.Where(f => f.FieldType == "relation"))
        {
            if (RelationConfig.FromConfig(field.ConfigJson) is not { } config) continue;
            var internalTarget = IsRelationSourceDatabase(config.TargetDbId, sourceDbId)
                                 && collectionMap.ContainsKey(config.TargetCollectionId);
            if (internalTarget) internalRelations.Add(oldByNew[field.Id]);
            if (onlyNewFieldIds is not null && !onlyNewFieldIds.Contains(field.Id)) continue;
            var updated = config with
            {
                TargetCollectionId = internalTarget ? collectionMap[config.TargetCollectionId] : config.TargetCollectionId,
                TargetDbId = internalTarget ? 0 : RebaseRelationDatabase(config.TargetDbId, sourceDbId, targetDbId, crossDatabase),
                DisplayField = internalTarget ? RemapRelationFieldKeys(config.DisplayField, fieldMap) : config.DisplayField,
                SearchField = internalTarget && config.SearchField is { } search ? RemapRelationFieldKeys(search, fieldMap) : config.SearchField,
                Auto = config.Auto?.MapKeys(key => RemapRelationFieldKey(key, fieldMap),
                    key => internalTarget ? RemapRelationFieldKey(key, fieldMap) : key),
                // Условие выбора ссылается на свойства цели: у копии своей коллекции — на свойства копии.
                Where = internalTarget && config.Where is { } where
                    ? where.Select(c => c with { Field = RemapRelationFieldKey(c.Field, fieldMap) }).ToList()
                    : config.Where,
            };
            field.ConfigJson = updated.ToConfigJson();
        }
        foreach (var field in fields.Where(f => f.FieldType is "lookup" or "rollup"))
        {
            if (onlyNewFieldIds is not null && !onlyNewFieldIds.Contains(field.Id)) continue;
            if (RelationComputedConfig.FromConfig(field.ConfigJson) is not { } config) continue;
            field.ConfigJson = (config with
            {
                RelationFieldId = fieldMap.GetValueOrDefault(config.RelationFieldId, config.RelationFieldId),
                TargetField = internalRelations.Contains(config.RelationFieldId)
                    ? RemapRelationFieldKey(config.TargetField, fieldMap) : config.TargetField,
            }).ToConfigJson();
        }
        db.SaveChanges();
    }

    // Один SQL-проход для дубля в той же БД и для SQLite ATTACH. Карта всей ветки уже полна,
    // поэтому ребро в коллекцию, скопированную последней, тоже получает правильную новую цель.
    private void CompleteRelationCopy(RelationCopyMap map, string sourcePrefix)
    {
        ValidateRelationCopyProjections(map, sourcePrefix);
        RemapCopiedRelationConfigs(map.Collections, map.Fields, map.SourceDbId, map.TargetDbId, map.CrossDatabase);
        var local = map.SourceDbId > 0 ? $"(l.TargetDbId = 0 OR l.TargetDbId = {map.SourceDbId})" : "l.TargetDbId = 0";
        if (map.CrossDatabase && map.SourceDbId <= 0)
        {
            // Без реестрового DbId можно перенести внутренний граф, но нельзя изобрести БД
            // для локальной цели, которая остается снаружи переносимой ветки.
            var hasUnknown = ScalarLong(db.Database.GetDbConnection(), db.Database.CurrentTransaction?.GetDbTransaction(), $@"
                SELECT EXISTS(SELECT 1 FROM {sourcePrefix}db_record_links l
                JOIN _relation_copy_fmap fm ON fm.old_id = l.FieldId
                LEFT JOIN _relation_copy_rmap tm ON tm.old_id = l.ToRecordId
                WHERE l.TargetDbId = 0 AND tm.old_id IS NULL)") > 0;
            if (hasUnknown) throw new InvalidOperationException("Для переноса внешней связи требуется идентификатор исходной базы.");
        }
        var rebased = map.CrossDatabase
            ? $"CASE WHEN (CASE WHEN l.TargetDbId = 0 THEN {map.SourceDbId} ELSE l.TargetDbId END) = {map.TargetDbId} THEN 0 ELSE (CASE WHEN l.TargetDbId = 0 THEN {map.SourceDbId} ELSE l.TargetDbId END) END"
            : "l.TargetDbId";
        #pragma warning disable EF1002
        db.Database.ExecuteSqlRaw($@"
            INSERT INTO db_record_links (FieldId,FromRecordId,ToRecordId,TargetDbId,Position)
            SELECT fm.new_id, frm.new_id,
                   CASE WHEN {local} AND tm.new_id IS NOT NULL THEN tm.new_id ELSE l.ToRecordId END,
                   CASE WHEN {local} AND tm.new_id IS NOT NULL THEN 0 ELSE {rebased} END,
                   l.Position
            FROM {sourcePrefix}db_record_links l
            JOIN _relation_copy_fmap fm ON fm.old_id = l.FieldId
            JOIN _relation_copy_rmap frm ON frm.old_id = l.FromRecordId
            LEFT JOIN _relation_copy_rmap tm ON tm.old_id = l.ToRecordId AND {local}");
        #pragma warning restore EF1002
    }

    // Generic-путь читает источник ограниченными страницами, а карту ищет в приемнике.
    private void CompleteRelationCopy(RelationCopyMap map, ConstructorRepository source)
    {
        if (ReferenceEquals(db, source.RelationLifecycleContext)) { CompleteRelationCopy(map, ""); return; }
        ValidateRelationCopyProjections(map, source);
        RemapCopiedRelationConfigs(map.Collections, map.Fields, map.SourceDbId, map.TargetDbId, map.CrossDatabase);
        var fieldIds = map.Fields.Keys.ToList();
        if (fieldIds.Count == 0) return;
        var after = 0;
        while (true)
        {
            var edges = source.RelationLifecycleContext.DbRecordLinks.AsNoTracking()
                .Where(l => fieldIds.Contains(l.FieldId) && l.Id > after).OrderBy(l => l.Id).Take(10000).ToList();
            if (edges.Count == 0) break;
            var needed = edges.Select(l => l.FromRecordId)
                .Concat(edges.Where(l => IsRelationSourceDatabase(l.TargetDbId, map.SourceDbId)).Select(l => l.ToRecordId));
            var records = ReadRelationRecordMap(needed);
            var rows = new List<object?[]>(edges.Count);
            foreach (var edge in edges)
            {
                if (!records.TryGetValue(edge.FromRecordId, out var from)) continue;
                var internalTarget = IsRelationSourceDatabase(edge.TargetDbId, map.SourceDbId) && records.ContainsKey(edge.ToRecordId);
                rows.Add([map.Fields[edge.FieldId], from,
                    internalTarget ? records[edge.ToRecordId] : edge.ToRecordId,
                    internalTarget ? 0 : RebaseRelationDatabase(edge.TargetDbId, map.SourceDbId, map.TargetDbId, map.CrossDatabase),
                    edge.Position]);
            }
            InsertRows(db, "db_record_links", "FieldId,FromRecordId,ToRecordId,TargetDbId,Position", rows);
            after = edges[^1].Id;
        }
    }

    private Dictionary<int, int> ReadRelationRecordMap(IEnumerable<int> ids)
    {
        var result = new Dictionary<int, int>();
        foreach (var page in ids.Distinct().Chunk(10000))
        {
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = $"SELECT old_id,new_id FROM _relation_copy_rmap WHERE old_id IN ({string.Join(",", page)})";
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(Convert.ToInt32(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(1)));
        }
        return result;
    }

    private void CopyFieldRelationEdges(int sourceFieldId, int targetFieldId, string? recordFilter = null)
    {
        #pragma warning disable EF1002
        db.Database.ExecuteSqlRaw($@"
            INSERT INTO db_record_links(FieldId,FromRecordId,ToRecordId,TargetDbId,Position)
            SELECT {targetFieldId},FromRecordId,ToRecordId,TargetDbId,Position FROM db_record_links
            WHERE FieldId = {sourceFieldId}{(recordFilter is null ? "" : $" AND FromRecordId IN ({recordFilter})")}");
        #pragma warning restore EF1002
    }

    private void EnsureSingleRelationMove(ConstructorRepository source, int sourceFieldId, IReadOnlyList<int> recordIds, string name)
    {
        foreach (var page in recordIds.Chunk(10000))
        {
            var selected = page.ToList();
            if (source.RelationLifecycleContext.DbRecordLinks.Where(l => l.FieldId == sourceFieldId && selected.Contains(l.FromRecordId))
                .GroupBy(l => l.FromRecordId).Any(g => g.Count() > 1))
                throw new InvalidOperationException($"Свойство «{name}» допускает одну связь, а переносимые записи содержат несколько. Измените настройку свойства перед переносом.");
        }
    }

    public bool HasExternalIncomingLinksToCollections(IReadOnlyCollection<int> collectionIds)
    {
        var ids = collectionIds.ToList();
        return (from link in db.DbRecordLinks
                join target in db.DbRecords on link.ToRecordId equals target.Id
                join sourceRecord in db.DbRecords on link.FromRecordId equals sourceRecord.Id
                where link.TargetDbId == 0 && ids.Contains(target.CollectionId) && !ids.Contains(sourceRecord.CollectionId)
                select link.Id).Any();
    }
}
