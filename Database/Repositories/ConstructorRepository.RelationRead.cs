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

    private List<(string Value, int Count)> RelatedComputedCounts(int fieldId, IQueryable<DbRecordEntity> scope)
    {
        var records = scope.OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => r.Id).ToList();
        var values = GetRelationComputedValues(records);
        var groups = records.Select((id, index) => (Index: index,
                Value: values.GetValueOrDefault(id)?.GetValueOrDefault(fieldId, "") ?? ""))
            .GroupBy(x => x.Value)
            .Select(g => (Value: g.Key, Count: g.Count(), Position: g.Min(x => x.Index)))
            .ToList();
        return groups.OrderBy(g => g.Value.Length == 0).ThenBy(g => g.Position)
            .Select(g => (g.Value, g.Count)).ToList();
    }
}
