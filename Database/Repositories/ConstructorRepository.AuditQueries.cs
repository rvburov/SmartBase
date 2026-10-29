using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    // Аудит не является EAV-значением: даже оставшаяся от прежнего типа строка поля
    // не должна перекрыть метаданные записи. Проекция общая для отбора, счетчиков и сортировки.
    private sealed record AuditFieldInfo(string Type, int CollectionId);

    private AuditFieldInfo? AuditField(int fieldId) =>
        db.DbFields.AsNoTracking().Where(f => f.Id == fieldId &&
            (f.FieldType == "created_at" || f.FieldType == "updated_at"
             || f.FieldType == "created_by" || f.FieldType == "updated_by"))
            .Select(f => new AuditFieldInfo(f.FieldType, f.CollectionId)).FirstOrDefault();

    private static string AuditProperty(string fieldType) => fieldType switch
    {
        "created_at" => nameof(DbRecordEntity.CreatedAt),
        "updated_at" => nameof(DbRecordEntity.UpdatedAt),
        "created_by" => nameof(DbRecordEntity.CreatedBy),
        "updated_by" => nameof(DbRecordEntity.UpdatedBy),
        _ => throw new ArgumentOutOfRangeException(nameof(fieldType)),
    };

    private sealed class AuditValueRow
    {
        public int RecordId { get; init; }
        public int Position { get; init; }
        public string Value { get; init; } = "";
    }

    private static IQueryable<AuditValueRow> AuditValues(IQueryable<DbRecordEntity> scope, string fieldType)
    {
        var property = AuditProperty(fieldType);
        return scope.Select(r => new AuditValueRow
        {
            RecordId = r.Id, Position = r.Position, Value = EF.Property<string>(r, property) ?? "",
        });
    }

    private static IQueryable<DbRecordEntity> AuditFilter(IQueryable<DbRecordEntity> scope,
        string fieldType, HashSet<string> allowed)
    {
        var property = AuditProperty(fieldType);
        if (FilterExclusion.IsExclude(allowed))
        {
            var excluded = FilterExclusion.Values(allowed).ToList();
            return scope.Where(r => !excluded.Contains(EF.Property<string>(r, property) ?? ""));
        }
        var selected = allowed.ToList();
        return scope.Where(r => selected.Contains(EF.Property<string>(r, property) ?? ""));
    }

    private static List<(string Value, int Count)> AuditCounts(IQueryable<DbRecordEntity> scope, string fieldType)
    {
        var groups = AuditValues(scope, fieldType).GroupBy(v => v.Value)
            .Select(g => new { Value = g.Key, Count = g.Count(), FirstPosition = g.Min(v => v.Position) })
            .ToList();
        return OrderValueGroups(groups.Select(g => (g.Value, g.Count, g.FirstPosition)));
    }

    private static async Task<List<int>> AuditSortedIdsAsync(IQueryable<DbRecordEntity> scope,
        string fieldType, bool ascending, CancellationToken ct)
    {
        var values = AuditValues(scope, fieldType);
        if (fieldType is "created_by" or "updated_by")
        {
            var nonemptyFirst = values.OrderBy(v => v.Value == "" ? 1 : 0);
            var sorted = ascending ? nonemptyFirst.ThenBy(v => v.Value) : nonemptyFirst.ThenByDescending(v => v.Value);
            return await sorted.ThenBy(v => v.Position).ThenBy(v => v.RecordId).Select(v => v.RecordId).ToListAsync(ct);
        }

        // ISO "o" содержит смещение и доли секунды: порядок строк не равен хронологии.
        // Разбираем только одну колонку и один раз на запись, без остальных значений EAV.
        var keys = new List<(long Key, int Order, int Id)>();
        var order = 0;
        await foreach (var row in values.OrderBy(v => v.Position).ThenBy(v => v.RecordId)
                           .AsAsyncEnumerable().WithCancellation(ct))
        {
            if (order % 1000 == 0) ct.ThrowIfCancellationRequested();
            keys.Add((ColumnSort.AuditDateKey(row.Value), order++, row.RecordId));
        }
        keys.Sort((a, b) =>
        {
            var aEmpty = a.Key == long.MaxValue;
            var bEmpty = b.Key == long.MaxValue;
            if (aEmpty || bEmpty) return aEmpty == bEmpty ? a.Order.CompareTo(b.Order) : aEmpty ? 1 : -1;
            var compare = a.Key.CompareTo(b.Key);
            return compare == 0 ? a.Order.CompareTo(b.Order) : ascending ? compare : -compare;
        });
        ct.ThrowIfCancellationRequested();
        return keys.Select(k => k.Id).ToList();
    }

    // Ограниченный список аудита нельзя строить по db_field_values. Для имен весь запрос
    // остается в SQL; даты ищутся по тому же локальному показу, что в ячейке и списке фильтра.
    private ColumnValueList AuditColumnValues(IQueryable<DbRecordEntity> scope, string fieldType,
        string? search, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var values = AuditValues(scope, fieldType).Where(v => v.Value != "");
        var many = values.Select(v => v.Value).Distinct().Take(FilterValueCap + 1).Count() > FilterValueCap;
        if (!many)
        {
            var all = AuditCounts(scope, fieldType);
            ct.ThrowIfCancellationRequested();
            return new(all, false, all.Count, false);
        }

        var query = FilterSelection.IsEmptyQuery(search) ? null : search!.Trim();
        if (query is not null && fieldType is "created_at" or "updated_at")
            return AuditDateSearch(values, fieldType, query, ct);

        if (query is not null)
        {
            var pattern = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            if (!db.IsPostgres && db.Database.GetDbConnection() is SqliteConnection sqlite)
            {
                db.Database.OpenConnection();
                sqlite.CreateFunction(SqlText.ContainsName, (string? value, string? part) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return value is not null && part is not null && value.Contains(part, StringComparison.OrdinalIgnoreCase);
                }, isDeterministic: true);
            }
            values = db.IsPostgres
                ? values.Where(v => EF.Functions.ILike(v.Value, pattern, "\\"))
                : values.Where(v => SqlText.ContainsIgnoreCase(v.Value, query));
        }

        List<(string Value, int Count)> page;
        try
        {
            page = values.GroupBy(v => v.Value).OrderBy(g => g.Key).Take(FilterValueCap + 1)
                .Select(g => new { Value = g.Key, Count = g.Count() }).ToList()
                .Select(g => (g.Value, g.Count)).ToList();
        }
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        ct.ThrowIfCancellationRequested();
        var more = page.Count > FilterValueCap;
        if (more) page.RemoveAt(page.Count - 1);
        return new(page, true, -1, more);
    }

    private static ColumnValueList AuditDateSearch(IQueryable<AuditValueRow> values,
        string fieldType, string query, CancellationToken ct)
    {
        // Поиск по локальной дате не транслируется одинаково в SQLite и PG. Читаем одну
        // колонку потоком; держим только первые cap+1 разных совпадений, не весь аудит.
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var read = 0;
        foreach (var raw in values.Select(v => v.Value).AsEnumerable())
        {
            if (++read % 1000 == 0) ct.ThrowIfCancellationRequested();
            if (!FieldTypeInfo.FormatComputedDisplay(raw, fieldType).Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            if (counts.TryGetValue(raw, out var count)) { counts[raw] = count + 1; continue; }
            if (keys.Count == FilterValueCap + 1 && StringComparer.Ordinal.Compare(raw, keys.Max) >= 0) continue;
            keys.Add(raw);
            counts.Add(raw, 1);
            if (keys.Count > FilterValueCap + 1)
            {
                var last = keys.Max!;
                keys.Remove(last);
                counts.Remove(last);
            }
        }
        ct.ThrowIfCancellationRequested();
        var more = keys.Count > FilterValueCap;
        return new(keys.Take(FilterValueCap).Select(v => (v, counts[v])).ToList(), true, -1, more);
    }
}
