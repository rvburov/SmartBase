using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    // Общий конфиг связи не может одновременно ссылаться на новые числовые ключи копии
    // и на прежние ключи целей, оставшихся снаружи. Проверка идет до записи конфига и ребер.
    private void ValidateRelationCopyProjections(RelationCopyMap map, string sourcePrefix)
    {
        if (map.Fields.Count == 0) return;
        var fields = new List<DbField>();
        using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = $"SELECT Id,CollectionId,Name,FieldType,ConfigJson FROM {sourcePrefix}db_fields WHERE Id IN ({string.Join(",", map.Fields.Keys)})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                fields.Add(new DbField
                {
                    Id = Convert.ToInt32(reader.GetValue(0)), CollectionId = Convert.ToInt32(reader.GetValue(1)),
                    Name = reader.GetString(2), FieldType = reader.GetString(3),
                    ConfigJson = reader.IsDBNull(4) ? "{}" : reader.GetString(4),
                });
        }
        var affected = RelationProjectionRemappedFields(fields, map.Collections, map.Fields, map.SourceDbId);
        if (affected.Count == 0) return;
        var local = map.SourceDbId > 0 ? $"(l.TargetDbId = 0 OR l.TargetDbId = {map.SourceDbId})" : "l.TargetDbId = 0";
        using var check = db.Database.GetDbConnection().CreateCommand();
        check.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        check.CommandText = $@"
            SELECT 1 FROM {sourcePrefix}db_record_links l
            JOIN _relation_copy_rmap frm ON frm.old_id = l.FromRecordId
            LEFT JOIN _relation_copy_rmap target ON target.old_id = l.ToRecordId
            WHERE l.FieldId IN ({string.Join(",", affected)})
              AND (NOT ({local}) OR target.old_id IS NULL)
            LIMIT 1";
        if (check.ExecuteScalar() is not null) throw RelationCopyProjectionConflict();
    }

    private void ValidateRelationCopyProjections(RelationCopyMap map, ConstructorRepository source)
    {
        if (map.Fields.Count == 0) return;
        var ids = map.Fields.Keys.ToList();
        var fields = source.RelationLifecycleContext.DbFields.AsNoTracking().Where(f => ids.Contains(f.Id))
            .ToList().Select(ToDbField).ToList();
        var affected = RelationProjectionRemappedFields(fields, map.Collections, map.Fields, map.SourceDbId).ToList();
        if (affected.Count == 0) return;
        var after = 0;
        while (true)
        {
            var edges = source.RelationLifecycleContext.DbRecordLinks.AsNoTracking()
                .Where(l => affected.Contains(l.FieldId) && l.Id > after).OrderBy(l => l.Id).Take(10000).ToList();
            if (edges.Count == 0) return;
            var records = ReadRelationRecordMap(edges.Select(e => e.FromRecordId)
                .Concat(edges.Where(e => IsRelationSourceDatabase(e.TargetDbId, map.SourceDbId)).Select(e => e.ToRecordId)));
            foreach (var edge in edges)
                if (records.ContainsKey(edge.FromRecordId)
                    && (!IsRelationSourceDatabase(edge.TargetDbId, map.SourceDbId) || !records.ContainsKey(edge.ToRecordId)))
                    throw RelationCopyProjectionConflict();
            after = edges[^1].Id;
        }
    }

    private static InvalidOperationException RelationCopyProjectionConflict() =>
        new("При копировании смешанной связи нельзя сохранить выбранные свойства для всех ее целей. Выберите показ по названию и уберите зависимые вычисления перед копированием.");
}
