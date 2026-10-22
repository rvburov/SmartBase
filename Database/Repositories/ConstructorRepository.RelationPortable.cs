using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartBase.Core.Exporters;
using SmartBase.Core.Importers;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    public List<ExportColumn> PreparePortableExportColumnsIsolated(int collectionId, IReadOnlyList<ExportColumn> columns,
        bool excludeHidden = false)
    {
        Demand(PermAction.ViewCollection, collectionId);
        using var ctx = new AppDbContext(DbPath);
        var repo = new ConstructorRepository(ctx) { Permissions = Permissions };
        var fields = columns.Where(c => c.FieldId.HasValue)
            .Select(c => new RelationPortableField(c.FieldId!.Value, c.Header, c.FieldType, c.ConfigJson)).ToList();
        var includedFields = fields.Select(f => f.Id).ToHashSet();
        var sourceDbId = RelationDbResolver.DbIdFor(this);
        var selfRelations = fields.Where(f => f.FieldType == "relation")
            .Select(f => (f.Id, Config: RelationConfig.FromConfig(f.ConfigJson)))
            .Where(f => f.Config is { } config && IsRelationSourceDatabase(config.TargetDbId, sourceDbId)
                && config.TargetCollectionId == collectionId).Select(f => f.Id).ToList();
        foreach (var field in fields)
        {
            if (RelationComputedConfig.FromConfig(field.ConfigJson) is { } computed
                && (field.FieldType is "lookup" or "rollup"))
            {
                if (!includedFields.Contains(computed.RelationFieldId))
                    throw new ExportException($"Для экспорта свойства «{field.Name}» включите его исходное свойство связи.");
                if (computed.Aggregate != "count" && selfRelations.Contains(computed.RelationFieldId)
                    && int.TryParse(computed.TargetField, out var targetField) && !includedFields.Contains(targetField))
                    throw new ExportException($"Для экспорта свойства «{field.Name}» включите используемое им свойство целевой записи.");
            }
            if (field.FieldType == "relation" && RelationConfig.FromConfig(field.ConfigJson) is { } relation)
            {
                var keys = new List<string>();
                if (relation.Auto is { } auto) keys.Add(auto.SourceField);
                if (selfRelations.Contains(field.Id))
                {
                    keys.AddRange(relation.DisplayKeys);
                    if (relation.SearchField is not null) keys.AddRange(relation.SearchKeys);
                    if (relation.Auto is { } rule) keys.Add(rule.TargetField);
                    keys.AddRange(relation.Where?.Select(c => c.Field) ?? []);
                }
                if (keys.Any(k => int.TryParse(k, out var id) && !includedFields.Contains(id)))
                    throw new ExportException($"Для экспорта связи «{field.Name}» включите используемые ей свойства этой коллекции.");
            }
        }
        if (excludeHidden && selfRelations.Count > 0)
        {
            var omittedTarget = (from link in ctx.DbRecordLinks.AsNoTracking()
                join source in ctx.DbRecords on link.FromRecordId equals source.Id
                join target in ctx.DbRecords on link.ToRecordId equals target.Id
                where selfRelations.Contains(link.FieldId)
                    && (link.TargetDbId == 0 || sourceDbId > 0 && link.TargetDbId == sourceDbId)
                    && source.CollectionId == collectionId && target.CollectionId == collectionId
                    && !ctx.RecordDisplay.Any(d => d.CollectionId == collectionId && d.RecordId == source.Id && !d.Visible)
                    && ctx.RecordDisplay.Any(d => d.CollectionId == collectionId && d.RecordId == target.Id && !d.Visible)
                select link.Id).Any();
            if (omittedTarget)
                throw new ExportException("Связь видимой записи ведет к скрытой записи этой коллекции. Включите скрытые записи в экспорт JSON.");
        }
        // Новые id при экспорте еще неизвестны: числовому ключу может потребоваться замена.
        // Прежняя внешняя цель не может безопасно использовать тот же замененный ключ.
        var possibleRemaps = fields.ToDictionary(f => f.Id, f => -f.Id);
        var projectedMixedFields = RelationProjectionRemappedFields(fields.Select(f => new DbField
            { Id = f.Id, Name = f.Name, FieldType = f.FieldType, ConfigJson = f.ConfigJson ?? "{}" }).ToList(),
            new Dictionary<int, int> { [collectionId] = -collectionId }, possibleRemaps, sourceDbId).ToList();
        if (projectedMixedFields.Count > 0 && (from edge in ctx.DbRecordLinks.AsNoTracking()
                join source in ctx.DbRecords on edge.FromRecordId equals source.Id
                where projectedMixedFields.Contains(edge.FieldId) && source.CollectionId == collectionId
                    && (!excludeHidden || !ctx.RecordDisplay.Any(d => d.CollectionId == collectionId && d.RecordId == source.Id && !d.Visible))
                    && (!(edge.TargetDbId == 0 || sourceDbId > 0 && edge.TargetDbId == sourceDbId)
                        || !ctx.DbRecords.Any(target => target.Id == edge.ToRecordId && target.CollectionId == collectionId))
                select edge.Id).Any())
            throw new ExportException("При экспорте смешанной связи нельзя сохранить выбранные свойства для всех ее целей. Выберите показ по названию и уберите зависимые вычисления перед экспортом JSON.");
        var relationIds = fields.Where(f => f.FieldType == "relation").Select(f => f.Id).ToList();
        var targets = ctx.DbRecordLinks.AsNoTracking().Where(l => relationIds.Contains(l.FieldId))
            .Select(l => l.TargetDbId).Distinct().ToList().Select(id => new RelationTarget(id, 0))
            .Concat(fields.Where(f => f.FieldType == "relation").Select(f => RelationConfig.FromConfig(f.ConfigJson))
                .Where(c => c is not null).Select(c => new RelationTarget(c!.TargetDbId, 0))).ToList();
        var identities = repo.GetRelationDatabaseIdentities(targets);
        if (targets.Any(t => t.DbId > 0 && !identities.ContainsKey(t.DbId)))
            throw new ExportException("Не удалось проверить внешнюю базу связи. Подключите ее перед переносимым экспортом JSON.");
        var schema = new RelationPortableSchema(1, collectionId, sourceDbId, RelationDatabaseIdentity,
            identities, fields, columns.FirstOrDefault(c => c.Key == TitleKey)?.Header ?? "Название");
        var result = columns.ToList();
        if (result.Count > 0) result[0] = result[0] with { PortableSchema = schema };
        return result;
    }

    private int ImportPortableStreamedCollection(string name, int? groupId, ImportSchema schema,
        IEnumerable<ImportRow> rows, CancellationToken ct, IProgress<long>? progress, int batchSize)
    {
        var portable = schema.PortableSchema!;
        if (portable.Version != 1 || portable.SourceCollectionId <= 0 || portable.Fields.Count != schema.FieldNames.Count
            || portable.Fields.Any(f => f.Id <= 0) || portable.Fields.Select(f => f.Id).Distinct().Count() != portable.Fields.Count)
            throw new ImportException("Неверная схема переносимого JSON.");
        // Проверяем внешние базы до создания коллекции.
        var shell = new CollectionSnapshot(new DbCollection { Id = portable.SourceCollectionId },
            portable.Fields.Select(f => new DbField { Id = f.Id, Name = f.Name, FieldType = f.FieldType, ConfigJson = f.ConfigJson ?? "{}" }).ToList(),
            [], new UiSnapshot([], [], null, [], [], []))
        {
            SourceDatabaseIdentity = portable.SourceIdentity, SourceDbId = portable.SourceDbId,
            DatabaseIdentities = portable.DatabaseIdentities,
        };
        foreach (var id in portable.DatabaseIdentities.Keys) SnapshotExternalDatabase(shell, id);
        foreach (var field in portable.Fields)
            if (RelationConfig.FromConfig(field.ConfigJson) is { } cfg
                && !(IsRelationSourceDatabase(cfg.TargetDbId, portable.SourceDbId) && cfg.TargetCollectionId == portable.SourceCollectionId))
            {
                var target = SnapshotExternalDatabase(shell, cfg.TargetDbId);
                target.Demand(PermAction.ViewCollection, cfg.TargetCollectionId);
                if (!target.RelationLifecycleContext.DbCollections.AsNoTracking().Any(c => c.Id == cfg.TargetCollectionId))
                    throw new ImportException("Целевая коллекция переносимой связи не найдена.");
            }
        using var tx = db.Database.BeginTransaction();
        using var map = BeginRelationCopy(portable.SourceDbId, RelationDbResolver.DbIdFor(this), portable.SourceIdentity != RelationDatabaseIdentity);
        db.Database.ExecuteSqlRaw("CREATE TEMP TABLE _portable_relation_edges(seq BIGINT PRIMARY KEY, field_id INTEGER NOT NULL, from_id INTEGER NOT NULL, target_db INTEGER NOT NULL, target_id INTEGER NOT NULL, position INTEGER NOT NULL)");
        try
        {
            var collection = new DbCollectionEntity { Name = name, GroupId = groupId,
                Position = (db.DbCollections.Select(c => (int?)c.Position).Max() ?? 0) + 1000, Icon = "table", Color = "#007AFF" };
            db.DbCollections.Add(collection);
            db.SaveChanges();
            var fields = portable.Fields.Select((f, i) => new DbFieldEntity
            { CollectionId = collection.Id, Name = f.Name, FieldType = f.FieldType, ConfigJson = f.ConfigJson ?? "{}", Position = (i + 1) * 1000 }).ToList();
            db.DbFields.AddRange(fields);
            db.SaveChanges();
            var fieldMap = portable.Fields.Select((f, i) => (f.Id, New: fields[i].Id)).ToDictionary(f => f.Id, f => f.New);
            map.AddCollection(portable.SourceCollectionId, collection.Id, fieldMap);
            SetSystemFieldName(collection.Id, TitleKey, portable.TitleHeader, "Название");
            SaveColumnOrder(collection.Id, [TitleKey, .. fields.Select(f => f.Id.ToString())]);
            long sequence = 0;
            WriteRowsBatched(collection.Id, fields.Select(f => f.Id).ToList(), schema, rows, ct, progress, batchSize,
                (batch, ids) =>
                {
                    var recordMap = new Dictionary<int, int>();
                    var staged = new List<object?[]>();
                    for (var i = 0; i < batch.Count; i++)
                    {
                        var row = batch[i].PortableRow ?? throw new ImportException("В переносимом JSON нет идентификатора записи.");
                        if (row.Id <= 0 || !recordMap.TryAdd(row.Id, checked((int)ids[i])))
                            throw new ImportException("Повторяющийся идентификатор записи в переносимом JSON.");
                        foreach (var (oldField, targets) in row.Links)
                        {
                            var field = portable.Fields.FirstOrDefault(f => f.Id == oldField);
                            if (field?.FieldType != "relation") throw new ImportException("Связь ссылается на отсутствующее свойство.");
                            var cfg = RelationConfig.FromConfig(field.ConfigJson) ?? throw new ImportException("У связи отсутствует цель.");
                            if (cfg.Single && targets.Distinct().Count() > 1) throw new ImportException("У свойства с одной связью несколько целей.");
                            for (var p = 0; p < targets.Count; p++)
                                staged.Add([++sequence, oldField, row.Id, targets[p].DbId, targets[p].RecordId, (p + 1) * 1000]);
                        }
                    }
                    map.AddRecords(recordMap);
                    InsertRows(db, "_portable_relation_edges", "seq,field_id,from_id,target_db,target_id,position", staged);
                });
            ValidatePortableRelationProjections(portable, map, ct);
            RemapCopiedRelationConfigs(map.Collections, map.Fields, map.SourceDbId, map.TargetDbId, map.CrossDatabase);
            // Условие выбора держит и импорт: файл можно поправить руками. Неподходящие связи не
            // создаются, остальное — как в файле; число пропущенных — в уведомлении.
            var newFieldIds = fieldMap.Values.ToList();
            var conditioned = db.DbFields.AsNoTracking().Where(f => newFieldIds.Contains(f.Id) && f.FieldType == "relation").ToList()
                .Select(f => (f.Id, Config: RelationConfig.FromConfig(f.ConfigJson)))
                .Where(p => p.Config is { HasCondition: true }).ToDictionary(p => p.Id, p => p.Config!);
            var skipped = new Dictionary<int, int>();
            long after = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var staged = ReadPortableEdges(after);
                if (staged.Count == 0) break;
                var records = ReadRelationRecordMap(staged.SelectMany(e => new[] { e.From, e.Target.RecordId }));
                var edges = new List<object?[]>();
                var checks = new List<(int Field, int TargetDbId, int TargetId, int SourceDbId, object?[] Row)>();
                foreach (var edge in staged)
                {
                    var internalTarget = IsRelationSourceDatabase(edge.Target.DbId, portable.SourceDbId) && records.ContainsKey(edge.Target.RecordId);
                    var targetId = internalTarget ? records[edge.Target.RecordId] : edge.Target.RecordId;
                    var targetDbId = internalTarget ? 0 : portable.SourceIdentity == RelationDatabaseIdentity ? edge.Target.DbId
                        : RebaseRelationDatabase(edge.Target.DbId, portable.SourceDbId, map.TargetDbId, true);
                    if (!internalTarget)
                    {
                        var target = SnapshotExternalDatabase(shell, edge.Target.DbId);
                        if (target.CollectionOfRecord(targetId) is not int targetCollection)
                            throw new ImportException("Целевая запись переносимой связи не найдена.");
                        target.Demand(PermAction.ViewCollection, targetCollection);
                    }
                    if (targetDbId == 0 && targetId == records[edge.From]) throw new ImportException("Переносимая связь ссылается на саму запись.");
                    object?[] row = [fieldMap[edge.Field], records[edge.From], targetId, targetDbId, edge.Position];
                    if (conditioned.TryGetValue(fieldMap[edge.Field], out var cfg) && targetDbId == cfg.TargetDbId)
                        checks.Add((fieldMap[edge.Field], targetDbId, targetId, edge.Target.DbId, row));
                    else edges.Add(row);
                }
                foreach (var group in checks.GroupBy(c => (c.Field, c.TargetDbId, c.SourceDbId)))
                {
                    var cfg = conditioned[group.Key.Field];
                    var owner = group.Key.TargetDbId == 0 ? this : SnapshotExternalDatabase(shell, group.Key.SourceDbId);
                    var verdict = owner.EvaluateRelationCondition(cfg.TargetCollectionId, cfg, group.Select(c => c.TargetId).ToList());
                    foreach (var check in group)
                        if (verdict.Broken is null && verdict.Allowed.Contains(check.TargetId)) edges.Add(check.Row);
                        else skipped[group.Key.Field] = skipped.GetValueOrDefault(group.Key.Field) + 1;
                }
                InsertRows(db, "db_record_links", "FieldId,FromRecordId,ToRecordId,TargetDbId,Position", edges);
                after = staged[^1].Seq;
            }
            Log("import", "collection", name, count: schema.RowCount);
            db.SaveChanges();
            db.Database.ExecuteSqlRaw("DROP TABLE _portable_relation_edges");
            NotifyRelationDataChanged(collection.Id);
            tx.Commit();
            ReportSkippedLinks(skipped, "Коллекция загружена. Не создано связей");
            return collection.Id;
        }
        catch
        {
            tx.Rollback();
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private List<(long Seq, int Field, int From, RelationTarget Target, int Position)> ReadPortableEdges(long after)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT seq,field_id,from_id,target_db,target_id,position FROM _portable_relation_edges WHERE seq > @after ORDER BY seq LIMIT 2000";
        var parameter = command.CreateParameter(); parameter.ParameterName = "@after"; parameter.Value = after; command.Parameters.Add(parameter);
        var result = new List<(long, int, int, RelationTarget, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add((Convert.ToInt64(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(1)),
            Convert.ToInt32(reader.GetValue(2)), new(Convert.ToInt32(reader.GetValue(3)), Convert.ToInt32(reader.GetValue(4))), Convert.ToInt32(reader.GetValue(5))));
        return result;
    }
}
