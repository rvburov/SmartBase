using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    private bool IsFileField(int fieldId) =>
        db.DbFields.AsNoTracking().Any(f => f.Id == fieldId && f.FieldType == "file");

    private string FileQueryCollation => db.IsPostgres ? "C" : "BINARY";

    private object FileQueryParameter(string name, object value) => db.IsPostgres
        ? new NpgsqlParameter(name, value) : new SqliteParameter(name, value);

    // Одна строка на пару (запись, полный путь). Повтор пути в поврежденном/старом массиве
    // не увеличивает счетчик и не размножает записи при отборе сразу нескольких файлов.
    // В SQL подставляются только выражения из кода; идентификатор поля остается параметром.
    #pragma warning disable EF1002
    private IQueryable<DbFieldValueEntity> FileQueryPaths(int fieldId)
    {
        var sql = $@"SELECT DISTINCT v.RecordId, v.FieldId, {DocPathSql} AS Value
            FROM db_field_values v {DocPathsJoinSql} WHERE v.FieldId = @file_query_field";
        // FromSqlRaw обрабатывает форматные скобки даже с именованными параметрами.
        // Литерал '{' в SQLite JSON-ветке должен пережить этот слой форматирования.
        sql = sql.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);
        return db.DbFieldValues.FromSqlRaw(sql, FileQueryParameter("@file_query_field", fieldId)).AsNoTracking();
    }
    #pragma warning restore EF1002

    private IQueryable<DbRecordEntity> FileFilter(IQueryable<DbRecordEntity> scope, int fieldId, HashSet<string> allowed)
    {
        var excluded = FilterExclusion.IsExclude(allowed);
        var selected = excluded ? FilterExclusion.Values(allowed).ToList() : allowed.ToList();
        var empty = selected.Remove("");
        // Сохраненные до раскрытия файлов фильтры содержали всю JSON-ячейку одним ключом.
        // Он остается точным сравнением исходной строки, а не превращается в ИЛИ ее путей.
        var legacyGroups = selected.Where(v => FileValue.ReadPaths(v).Count > 1).ToList();
        selected = selected.Except(legacyGroups, StringComparer.Ordinal).ToList();
        var paths = FileQueryPaths(fieldId);
        var named = paths.Select(v => v.RecordId);
        var matching = paths.Where(v => selected.Contains(EF.Functions.Collate(v.Value, FileQueryCollation)))
            .Select(v => v.RecordId);
        if (legacyGroups.Count > 0)
            matching = matching.Union(db.DbFieldValues.AsNoTracking()
                .Where(v => v.FieldId == fieldId && legacyGroups.Contains(EF.Functions.Collate(v.Value, FileQueryCollation)))
                .Select(v => v.RecordId));
        return excluded
            ? scope.Where(r => !matching.Contains(r.Id) && (!empty || named.Contains(r.Id)))
            : scope.Where(r => matching.Contains(r.Id) || (empty && !named.Contains(r.Id)));
    }

    private IQueryable<DbFieldValueEntity> FilePathsInScope(int fieldId, IQueryable<DbRecordEntity> scope) =>
        FileQueryPaths(fieldId).Where(v => scope.Select(r => r.Id).Contains(v.RecordId));

    private List<(string Value, int Count)> FileColumnCounts(int fieldId, IQueryable<DbRecordEntity> scope)
    {
        var paths = FilePathsInScope(fieldId, scope);
        var result = paths.GroupBy(v => EF.Functions.Collate(v.Value, FileQueryCollation))
            .Select(g => new { Value = g.Key, Count = g.Count() }).ToList()
            .Select(v => (v.Value, v.Count)).OrderBy(v => FileValue.DisplayName(v.Value), StringComparer.Ordinal)
            .ThenBy(v => v.Value, StringComparer.Ordinal).ToList();
        var empty = scope.Count(r => !paths.Select(v => v.RecordId).Contains(r.Id));
        if (empty > 0) result.Add(("", empty));
        return result;
    }

    private ColumnValueList FileColumnValues(int fieldId, IQueryable<DbRecordEntity> scope,
        string? search, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var paths = FilePathsInScope(fieldId, scope);
        var many = FileQueryPaths(fieldId).Select(v => EF.Functions.Collate(v.Value, FileQueryCollation)).Distinct()
            .Take(FilterValueCap + 1).Count() > FilterValueCap;
        if (!many)
        {
            var all = FileColumnCounts(fieldId, scope);
            return new(all, false, all.Count(v => v.Value.Length > 0), false);
        }

        // Полный путь содержит и имя файла: поиск не видит JSON-ключи, кавычки и escapes.
        var query = FilterSelection.IsEmptyQuery(search) ? null : search!.Trim();
        if (query is not null)
        {
            if (db.IsPostgres)
            {
                var pattern = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                paths = paths.Where(v => EF.Functions.ILike(v.Value, pattern, "\\"));
            }
            else paths = paths.Where(v => SqlText.ContainsIgnoreCase(v.Value, query));
        }
        var page = paths.GroupBy(v => EF.Functions.Collate(v.Value, FileQueryCollation)).OrderBy(g => g.Key)
            .Take(FilterValueCap + 1).Select(g => new { Value = g.Key, Count = g.Count() }).ToList()
            .Select(v => (v.Value, v.Count)).ToList();
        var more = page.Count > FilterValueCap;
        if (more) page.RemoveAt(page.Count - 1);
        ct.ThrowIfCancellationRequested();
        return new(page, true, -1, more);
    }

    private (long Distinct, int Empty) FileColumnValueTotals(int fieldId, IQueryable<DbRecordEntity> scope,
        CancellationToken ct)
    {
        var paths = FilePathsInScope(fieldId, scope);
        var distinct = paths.Select(v => EF.Functions.Collate(v.Value, FileQueryCollation)).Distinct().LongCount();
        ct.ThrowIfCancellationRequested();
        var empty = scope.Count(r => !paths.Select(v => v.RecordId).Contains(r.Id));
        ct.ThrowIfCancellationRequested();
        return (distinct, empty);
    }

    // Ключ совпадает с FileValue.DisplayName: порядок paths сохранен, оба разделителя пути
    // узнаются на любой ОС, завершающий разделитель оставляет исходное значение целиком.
    // C/BINARY задает одинаковый байтовый порядок головы и ее курсора на двух провайдерах.
    private string FileDisplaySql(string raw) => db.IsPostgres
        ? $@"(COALESCE((SELECT string_agg(
                CASE WHEN right(replace(fp.path, chr(92), '/'), 1) = '/' THEN fp.path
                     ELSE reverse(split_part(reverse(replace(fp.path, chr(92), '/')), '/', 1)) END,
                ', ' ORDER BY fp.file_index) FROM smartbase_file_paths({raw}) fp), '') COLLATE ""C"")"
        : $"({SqlText.FileDisplayName}({raw}) COLLATE BINARY)";

    private string FileSortedValuesSql => $@"SELECT v.RecordId, {FileDisplaySql("v.Value")} AS Value
        FROM db_field_values v WHERE v.FieldId = @f";

    private RecordPage GetFileRecordsSortedHead(int collectionId, int fieldId, bool asc, bool excludeHidden, int take,
        string? afterValue, int? afterId, string? beforeValue, int? beforeId, int skip, int? headCount)
    {
        var args = new List<(string, object)> { ("@f", fieldId), ("@c", collectionId) };
        var hidden = HiddenFilter(excludeHidden);
        var cursor = "";
        var reverse = false;
        var offset = 0;
        var limit = take;
        if (afterValue is not null && afterId is int aid)
        {
            cursor = $" AND (fv.Value, fv.RecordId) {(asc ? ">" : "<")} (@v, @id)";
            args.Add(("@v", afterValue)); args.Add(("@id", aid));
        }
        else if (beforeValue is not null && beforeId is int bid)
        {
            cursor = $" AND (fv.Value, fv.RecordId) {(asc ? "<" : ">")} (@v, @id)";
            args.Add(("@v", beforeValue)); args.Add(("@id", bid));
            reverse = true;
        }
        else
        {
            reverse = headCount is int h && skip > h / 2;
            offset = reverse ? Math.Max(0, headCount!.Value - skip - take) : skip;
            limit = reverse ? Math.Min(take, Math.Max(0, headCount!.Value - skip)) : take;
        }
        if (limit <= 0) return RecordPage.Empty;
        args.Add(("@take", limit)); args.Add(("@skip", offset));
        var order = asc != reverse ? "fv.Value, fv.RecordId" : "fv.Value DESC, fv.RecordId DESC";
        return ReadSortedRows($@"SELECT {RecordCols}, fv.Value FROM ({FileSortedValuesSql}) fv
            JOIN db_records r ON r.Id = fv.RecordId
            WHERE r.CollectionId = @c AND fv.Value <> ''{cursor}{hidden}
            ORDER BY {order} LIMIT @take OFFSET @skip", reverse, args);
    }

    private int CountFileNonEmptyValues(int collectionId, int fieldId, bool excludeHidden) =>
        (int)ScalarSql($@"SELECT COUNT(*) FROM ({FileSortedValuesSql}) fv
            JOIN db_records r ON r.Id = fv.RecordId
            WHERE r.CollectionId = @c AND fv.Value <> ''{HiddenFilter(excludeHidden)}",
            ("@f", fieldId), ("@c", collectionId));

    private int? RankInFileSortedView(int collectionId, int fieldId, bool asc, bool excludeHidden, int recordId, int headCount)
    {
        if (!RecordShown(collectionId, recordId, excludeHidden)) return null;
        var value = ScalarText($"SELECT {FileDisplaySql("v.Value")} FROM db_field_values v WHERE v.RecordId = @id AND v.FieldId = @f",
            ("@id", recordId), ("@f", fieldId));
        if (value is { Length: > 0 })
            return (int)ScalarSql($@"SELECT COUNT(*) FROM ({FileSortedValuesSql}) fv
                JOIN db_records r ON r.Id = fv.RecordId
                WHERE r.CollectionId = @c AND fv.Value <> ''
                  AND (fv.Value, fv.RecordId) {(asc ? "<" : ">")} (@v, @id){HiddenFilter(excludeHidden)}",
                ("@c", collectionId), ("@f", fieldId), ("@id", recordId), ("@v", value));
        return headCount + (int)ScalarSql($@"SELECT COUNT(*) FROM db_records i
            WHERE i.CollectionId = @c{HiddenFilter(excludeHidden, "i")}
              AND NOT EXISTS (SELECT 1 FROM db_field_values v
                  WHERE v.RecordId = i.Id AND v.FieldId = @f AND {FileDisplaySql("v.Value")} <> '')
              AND (i.Position, i.Id) < (SELECT r.Position, r.Id FROM db_records r WHERE r.Id = @id)",
            ("@c", collectionId), ("@f", fieldId), ("@id", recordId));
    }

    private async Task<List<int>> FileSortedIdsAsync(IQueryable<DbRecordEntity> scope, int fieldId, bool asc, CancellationToken ct)
    {
        // Выражение показа из кода, идентификатор поля — отдельный параметр.
        #pragma warning disable EF1002
        var values = db.DbFieldValues.FromSqlRaw($@"SELECT v.RecordId, v.FieldId, {FileDisplaySql("v.Value")} AS Value
            FROM db_field_values v WHERE v.FieldId = @file_query_field",
            FileQueryParameter("@file_query_field", fieldId)).AsNoTracking();
        #pragma warning restore EF1002
        var keys = scope.Select(r => new
        {
            r.Id, r.Position,
            Value = values.Where(v => v.RecordId == r.Id).Select(v => v.Value).FirstOrDefault() ?? "",
        });
        var empty = keys.OrderBy(v => v.Value == "" ? 1 : 0);
        var ordered = asc ? empty.ThenBy(v => EF.Functions.Collate(v.Value, FileQueryCollation))
            : empty.ThenByDescending(v => EF.Functions.Collate(v.Value, FileQueryCollation));
        var byTailPosition = ordered.ThenBy(v => v.Value == "" ? v.Position : 0);
        return asc
            ? await byTailPosition.ThenBy(v => v.Id).Select(v => v.Id).ToListAsync(ct)
            : await byTailPosition.ThenByDescending(v => v.Value == "" ? 0 : v.Id).ThenBy(v => v.Id)
                .Select(v => v.Id).ToListAsync(ct);
    }
}
