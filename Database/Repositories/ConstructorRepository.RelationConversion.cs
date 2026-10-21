using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    // UpdateField вызывает проверку до изменения метаданных, в своей транзакции.
    private void ValidateAndConvertRelationFieldChange(DbField oldField, DbField newField)
    {
        if (oldField.Id is not int fieldId) return;
        var wasRelation = oldField.FieldType == "relation";
        var isRelation = newField.FieldType == "relation";
        if (!wasRelation && !isRelation) return;
        var oldConfig = RelationConfig.FromConfig(oldField.ConfigJson);
        var config = RelationConfig.FromConfig(newField.ConfigJson);
        var autoSave = IsApplyingAutoRelation && config?.Auto is not null;
        var edges = db.DbRecordLinks.Where(l => l.FieldId == fieldId).OrderBy(l => l.Position).ThenBy(l => l.Id).ToList();

        if (wasRelation && isRelation)
        {
            if (edges.Count == 0 || autoSave) return;
            if (config is not null && oldConfig is not null && IsLegacyRelationDb(oldConfig.TargetDbId)
                && !IsLegacyRelationDb(config.TargetDbId) && config.TargetCollectionId == oldConfig.TargetCollectionId)
            {
                RebindLegacyRelationField(oldConfig.TargetDbId, config, edges);
                return;
            }
            if (config is null || oldConfig is null || config.TargetDbId != oldConfig.TargetDbId
                || config.TargetCollectionId != oldConfig.TargetCollectionId)
                throw new InvalidOperationException("Сначала очистите связи: изменение целевой коллекции не переносит существующие записи автоматически.");
            if (config.Single && !oldConfig.Single && edges.GroupBy(l => l.FromRecordId).Any(g => g.Count() > 1))
                throw new InvalidOperationException("В некоторых записях несколько связей. Оставьте по одной перед включением режима одной записи.");
            return;
        }

        using var ownedTx = db.Database.CurrentTransaction is null ? db.Database.BeginTransaction() : null;
        var scalars = db.DbFieldValues.Where(v => v.FieldId == fieldId).ToList();
        if (wasRelation)
        {
            if (edges.Count > 0 && newField.FieldType is not ("text" or "markdown"))
                throw new InvalidOperationException("Сначала преобразуйте связь в текст или очистите ее перед сменой типа.");
            var labels = ResolveRelationLabels(edges.Select(e => new RelationTarget(e.TargetDbId, e.ToRecordId)).Distinct().ToList(),
                oldConfig?.DisplayField ?? RelationConfig.TitleField, oldConfig?.TargetDbId);
            if (edges.Any(e => !labels.TryGetValue(new(e.TargetDbId, e.ToRecordId), out var label)
                || label == RelationProperty.NoAccess || label == RelationProperty.Unavailable || label == RelationProperty.UnknownBase
                || label == RelationProperty.MissingField
                || label == RelationProperty.Cycle))
                throw new InvalidOperationException("Не удалось прочитать все цели связи. Преобразование в текст отменено.");
            db.DbFieldValues.RemoveRange(scalars);
            db.SaveChanges();
            foreach (var group in edges.GroupBy(e => e.FromRecordId))
                db.DbFieldValues.Add(new DbFieldValueEntity
                {
                    RecordId = group.Key, FieldId = fieldId,
                    Value = string.Join(oldConfig?.SeparatorString ?? ", ", group.Select(e => labels[new(e.TargetDbId, e.ToRecordId)])),
                });
            db.DbRecordLinks.RemoveRange(edges);
        }
        else
        {
            if (scalars.Any(v => !string.IsNullOrWhiteSpace(v.Value)) && !autoSave)
            {
                if (oldField.FieldType is not ("text" or "markdown") || config is null)
                    throw new InvalidOperationException("Преобразование в связь требует текстового свойства и настроенной целевой коллекции.");
                var target = config.IsCrossDb ? RelationDbResolver.RepoFor(config.TargetDbId) : this;
                if (target is null) throw new InvalidOperationException("Целевая база связи недоступна.");
                var resolved = target.ResolveRelationTexts(config, scalars.Select(v => v.Value).Distinct().ToList());
                if (resolved.Values.Any(r => r.Match != TitleMatch.Found))
                    throw new InvalidOperationException("Часть значений не найдена или неоднозначна. Преобразование в связь отменено.");
                // Прежние ребра от старого типа не должны появиться снова.
                db.DbRecordLinks.RemoveRange(edges);
                db.SaveChanges();
                foreach (var value in scalars)
                {
                    var ids = resolved[value.Value].Ids;
                    var wanted = NormalizeRelationTargets(config, value.RecordId,
                        ids.Select(id => new RelationTarget(config.TargetDbId, id)).ToList());
                    if (wanted.Count != ids.Distinct().Count())
                        throw new InvalidOperationException("Нельзя сохранить все найденные связи. Преобразование отменено.");
                    for (var i = 0; i < wanted.Count; i++)
                        db.DbRecordLinks.Add(new DbRecordLinkEntity
                        {
                            FieldId = fieldId, FromRecordId = value.RecordId, TargetDbId = wanted[i].DbId,
                            ToRecordId = wanted[i].RecordId, Position = (i + 1) * 1000,
                        });
                }
            }
            else db.DbRecordLinks.RemoveRange(edges);
            db.DbFieldValues.RemoveRange(scalars);
        }
        db.SaveChanges();
        ownedTx?.Commit();
    }

    // Старая межбазовая связь получила базу из окна свойства: ребра переводятся на ее постоянный
    // номер, только если все связанные записи лежат в целевой коллекции выбранной базы.
    private void RebindLegacyRelationField(int legacy, RelationConfig config, List<DbRecordLinkEntity> edges)
    {
        var target = config.TargetDbId == 0 ? this : RelationDbResolver.RepoFor(config.TargetDbId)
            ?? throw new InvalidOperationException("Выбранная база недоступна. Подключите ее и сохраните свойство снова.");
        var legacyEdges = edges.Where(e => e.TargetDbId == legacy).ToList();
        var targets = legacyEdges.Select(e => e.ToRecordId).Distinct().ToList();
        if (!IsLegacyTarget(target.AutoContext, config.TargetCollectionId, targets, LegacyFieldKeys(config)))
            throw new InvalidOperationException("В выбранной базе нет всех связанных записей этой коллекции. Выберите другую базу.");
        var newDbId = config.TargetDbId;
        var existing = edges.Where(e => e.TargetDbId == newDbId).Select(e => (e.FromRecordId, e.ToRecordId)).ToHashSet();
        foreach (var edge in legacyEdges)
        {
            if (existing.Contains((edge.FromRecordId, edge.ToRecordId))) db.DbRecordLinks.Remove(edge);
            else edge.TargetDbId = newDbId;
        }
        db.SaveChanges();
    }
}
