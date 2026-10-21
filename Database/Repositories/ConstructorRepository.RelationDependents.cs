using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

/// <summary>Столбец данных из связанных записей в окне связи: существующий (FieldId) или новый.
/// Aggregate «lookup» — значения свойства цели, остальные — итог (rollup).</summary>
public sealed record RelationDataColumn(int? FieldId, string Name, string TargetField, string Aggregate);

/// <summary>
/// Данные из связанных записей (lookup/rollup) живут только вместе со своей связью: их значение
/// вычисляется по ее ребрам. Удаление связи или смена ее типа уносит и их — иначе столбцы
/// оставались бы с «(вычисление не настроено)», и найти их могла бы только проверка связей.
/// </summary>
public partial class ConstructorRepository
{
    /// <summary>Столбцы данных из связанных записей, которые считаются по полю-связи
    /// <paramref name="relationFieldId"/> (той же коллекции), в порядке создания.</summary>
    public List<DbField> GetRelationDependentFields(int relationFieldId)
    {
        var collection = db.DbFields.AsNoTracking().Where(f => f.Id == relationFieldId)
            .Select(f => (int?)f.CollectionId).FirstOrDefault();
        return collection is int col ? RelationDependentFields(relationFieldId, col) : [];
    }

    private List<DbField> RelationDependentFields(int relationFieldId, int collectionId) =>
        db.DbFields.AsNoTracking()
            .Where(f => f.CollectionId == collectionId && (f.FieldType == "lookup" || f.FieldType == "rollup"))
            .OrderBy(f => f.Id).ToList().Select(ToDbField)
            .Where(f => RelationComputedConfig.FromConfig(f.ConfigJson)?.RelationFieldId == relationFieldId)
            .ToList();

    // Значений в db_field_values у вычисляемых столбцов нет: уходят строка поля, метаданные
    // колонки и оформление ее ячеек (как в DeleteField). Запись в журнал — по каждому столбцу.
    private void RemoveRelationDependentFields(int relationFieldId, int collectionId)
    {
        foreach (var dependent in RelationDependentFields(relationFieldId, collectionId))
        {
            var id = dependent.Id!.Value;
            var key = id.ToString();
            db.DbFields.Where(f => f.Id == id).ExecuteDelete();
            db.ColumnMeta.Where(m => m.CollectionId == collectionId && m.Key == key).ExecuteDelete();
            db.CellStyles.Where(c => c.CollectionId == collectionId && c.Key == key).ExecuteDelete();
            LogNow("delete", "field", dependent.Name, CollectionNameOf(collectionId));
        }
    }

    /// <summary>
    /// Сохранить столбцы данных из связанных записей поля-связи одним целым: изменить, добавить
    /// и удалить, как задано окном связи. Новые столбцы встают сразу за связью и ее прежними
    /// столбцами. Ошибка любого столбца (имя занято, свойство цели другого типа, цикл) отменяет
    /// все изменения столбцов; сама связь к этому моменту уже сохранена окном.
    /// </summary>
    public IReadOnlyList<int> SaveRelationDataColumns(int relationFieldId, IReadOnlyList<RelationDataColumn> columns)
    {
        var relation = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == relationFieldId && f.FieldType == "relation")
            ?? throw new InvalidOperationException("Связь не найдена.");
        var collection = relation.CollectionId;
        var existing = RelationDependentFields(relationFieldId, collection);
        var names = columns.Select(c => c.Name.Trim()).ToList();
        if (names.Any(n => n.Length == 0)) throw new InvalidOperationException("Укажите название каждого столбца данных из связи.");
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new InvalidOperationException("У столбцов данных из связи одинаковые названия.");

        using var tx = db.Database.CurrentTransaction is null ? db.Database.BeginTransaction() : null;
        try
        {
            var ids = SaveRelationDataColumnsCore(relation, existing, columns);
            tx?.Commit();
            return ids;
        }
        catch when (tx is not null)
        {
            // Откат транзакции не возвращает состояние отслеживания: иначе следующая запись
            // окна сохранила бы «удаленные» и «добавленные» здесь строки.
            tx.Rollback();
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private List<int> SaveRelationDataColumnsCore(Entities.DbFieldEntity relation, List<DbField> existing,
        IReadOnlyList<RelationDataColumn> columns)
    {
        var relationFieldId = relation.Id;
        var collection = relation.CollectionId;
        var keep = columns.Where(c => c.FieldId is not null).Select(c => c.FieldId!.Value).ToHashSet();
        foreach (var removed in existing.Where(f => !keep.Contains(f.Id!.Value)))
            DeleteField(removed.Id!.Value);

        var ids = new List<int>();
        var added = new List<int>();
        var position = relation.Position;
        foreach (var column in columns)
        {
            var type = column.Aggregate == "lookup" ? "lookup" : "rollup";
            var config = new RelationComputedConfig(relationFieldId,
                column.Aggregate == "count" ? RelationConfig.TitleField : column.TargetField, column.Aggregate).ToConfigJson();
            if (column.FieldId is int id && existing.FirstOrDefault(f => f.Id == id) is { } field)
            {
                field.Name = column.Name.Trim();
                field.FieldType = type;
                field.ConfigJson = config;
                ValidateRelationComputedField(field);
                UpdateField(field);
                ids.Add(id);
            }
            else
            {
                var created = AddField(new DbField
                {
                    CollectionId = collection, Name = column.Name.Trim(), FieldType = type, ConfigJson = config,
                    Position = ++position,
                });
                ids.Add(created.Id!.Value);
                added.Add(created.Id!.Value);
            }
        }
        if (added.Count > 0) PlaceColumnsAfter(collection, relationFieldId, ids);
        return ids;
    }

    // Текущий порядок колонок — как его показывает таблица (сохраненный, иначе «Название»
    // первым, поля по Position); столбцы связи ставятся сразу за ней в заданном порядке.
    private void PlaceColumnsAfter(int collectionId, int relationFieldId, IReadOnlyList<int> fieldIds)
    {
        var saved = GetColumnOrder(collectionId);
        var keys = new List<(string Key, int Position)> { (TitleKey, saved.GetValueOrDefault(TitleKey, 1000)) };
        foreach (var field in db.DbFields.AsNoTracking().Where(f => f.CollectionId == collectionId).ToList())
        {
            var key = field.Id.ToString();
            keys.Add((key, saved.TryGetValue(key, out var p) ? p : 3000 + field.Position));
        }
        var moving = fieldIds.Select(id => id.ToString()).ToList();
        var order = keys.OrderBy(k => k.Position).Select(k => k.Key).Where(k => !moving.Contains(k)).ToList();
        var at = order.IndexOf(relationFieldId.ToString());
        order.InsertRange(at < 0 ? order.Count : at + 1, moving);
        WriteColumnOrder(collectionId, order);
    }
}
