using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    private DbField? RelatedComputedField(int fieldId)
    {
        var entity = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fieldId &&
            (f.FieldType == "lookup" || f.FieldType == "rollup"));
        return entity is null ? null : ToDbField(entity);
    }

    // Значения одной колонки по срезу в порядке таблицы; «(Пусто)» последним. Считается только эта
    // колонка: малый срез — сам, больший — столбцом целиком.
    private List<(string Value, int Count)> RelatedComputedCounts(int fieldId, IQueryable<DbRecordEntity> scope,
        CancellationToken ct = default)
    {
        var field = RelatedComputedField(fieldId);
        using var phase = field is null ? null : ComputedRun?.Phase($"Столбец «{field.Name}»: чтение записей");
        var ordered = scope.OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => new { r.Id, r.CollectionId }).ToList();
        if (field is null || ordered.Count == 0) return ordered.Count == 0 ? [] : [("", ordered.Count)];
        var records = ordered.Select(r => r.Id).ToList();
        var valueOf = RelationComputedValuesFor(ordered[0].CollectionId, field, records, ComputedToken(ct));
        var groups = records.Select((id, index) => (Index: index, Value: valueOf(id)))
            .GroupBy(x => x.Value)
            .Select(g => (Value: g.Key, Count: g.Count(), Position: g.Min(x => x.Index)))
            .ToList();
        return groups.OrderBy(g => g.Value.Length == 0).ThenBy(g => g.Position)
            .Select(g => (g.Value, g.Count)).ToList();
    }
}
