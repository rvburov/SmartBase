using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

// ── Отбор и сортировка по цвету (ColorQuery) ──────────────────────────────────
// Цвет ячейки — каскад трех разреженных уровней: своя ячейка (cell_styles), строка (record_display)
// и колонка (column_meta). У подавляющего большинства записей своих цветов нет, и видят они цвет
// колонки. Поэтому записи не перебираются: считаются только «особые» — у которых есть цвет ячейки
// этой колонки или цвет строки; все остальные видят один и тот же цвет колонки. Условие уходит в
// запрос списком особых id: «все, кроме особых, что не подошли» или «только особые, что подошли».
public partial class ConstructorRepository
{
    // Видимые цвета колонки у записей со своим оформлением: recordId → сведенный стиль. Плюс то, что
    // видят все остальные записи коллекции.
    private (Dictionary<int, RowStyle> Special, RowStyle Common) ColumnColorLevels(int collectionId, string columnKey)
    {
        var column = db.ColumnMeta.AsNoTracking()
            .Where(m => m.CollectionId == collectionId && m.Key == columnKey)
            .Select(m => new { m.Bg, m.Fg, m.BgAt, m.FgAt })
            .AsEnumerable()
            .Select(m => RowStyle.Clean(m.Bg, m.Fg, at: new StyleStamps(m.BgAt ?? 0, m.FgAt ?? 0, 0, 0)))
            .FirstOrDefault();
        var rows = db.RecordDisplay.AsNoTracking()
            .Where(d => d.CollectionId == collectionId
                        && (d.Bg != null || d.Fg != null || d.BgAt != null || d.FgAt != null))
            .Select(d => new { d.RecordId, d.Bg, d.Fg, d.BgAt, d.FgAt })
            .AsEnumerable()
            .ToDictionary(d => d.RecordId,
                          d => RowStyle.Clean(d.Bg, d.Fg, at: new StyleStamps(d.BgAt ?? 0, d.FgAt ?? 0, 0, 0)));
        var cells = db.CellStyles.AsNoTracking()
            .Where(c => c.CollectionId == collectionId && c.Key == columnKey && (c.Bg != null || c.Fg != null))
            .Select(c => new { c.RecordId, c.Bg, c.Fg })
            .AsEnumerable()
            .ToDictionary(c => c.RecordId, c => RowStyle.Clean(c.Bg, c.Fg));

        var special = new Dictionary<int, RowStyle>(rows.Count + cells.Count);
        foreach (var id in rows.Keys.Concat(cells.Keys))
            special[id] = RowStyle.Resolve(cells.GetValueOrDefault(id), rows.GetValueOrDefault(id), column);
        return (special, RowStyle.Resolve(RowStyle.None, RowStyle.None, column));
    }

    // Какие записи подходят под метки: список особых id и как его читать. Complement = true — подходят
    // все, КРОМЕ записей из списка (цвет колонки подходит, особые выпадают); false — только записи
    // из списка.
    private (HashSet<int> Ids, bool Complement) ColorMatches(int collectionId, string columnKey, IReadOnlyCollection<string> tokens)
    {
        var (special, common) = ColumnColorLevels(collectionId, columnKey);
        bool Match(RowStyle style) => ColorQuery.MatchesFilter(style, tokens);
        var commonMatches = Match(common);
        var ids = special.Where(kv => Match(kv.Value) != commonMatches).Select(kv => kv.Key).ToHashSet();
        return (ids, commonMatches);
    }

    // Отбор по цвету в общем запросе отбора (FilteredRecords).
    private IQueryable<DbRecordEntity> ColorFilter(IQueryable<DbRecordEntity> q, int collectionId, string columnKey,
                                                   IReadOnlyCollection<string> tokens)
    {
        if (tokens.Count == 0) return q;
        var (ids, complement) = ColorMatches(collectionId, columnKey, tokens);
        return complement ? q.Where(r => !ids.Contains(r.Id)) : q.Where(r => ids.Contains(r.Id));
    }

    // Сортировка по цвету: записи цвета метки — сверху (asc) или снизу, внутри групп — порядок таблицы.
    private async Task<List<int>> ColorSortedIdsAsync(IQueryable<DbRecordEntity> q, int collectionId, string columnKey,
                                                      string token, bool asc, CancellationToken ct)
    {
        var ordered = await q.OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => r.Id).ToListAsync(ct);
        var (ids, complement) = ColorMatches(collectionId, columnKey, [token]);
        bool Hit(int id) => ids.Contains(id) != complement;
        var first  = ordered.Where(id => Hit(id) == asc);
        var second = ordered.Where(id => Hit(id) != asc);
        return [.. first, .. second];
    }

    /// <summary>
    /// Цвета колонки с числом записей — для пункта «Фильтр по цвету» окна фильтра.
    /// Считаются, как и значения, по записям, прошедшим отбор ОСТАЛЬНЫХ колонок (свой отбор
    /// колонки — и по значениям, и по цвету — список не режет). Порядок — по убыванию числа записей.
    /// </summary>
    public Task<List<(string Token, int Count)>> GetColumnColorsAsync(int collectionId, string columnKey,
        IReadOnlyDictionary<string, HashSet<string>>? filters = null, bool excludeHidden = false,
        CancellationToken ct = default)
    {
        var others     = filters is null ? new Dictionary<string, HashSet<string>>()
                       : filters.Where(kv => ColorQuery.ColumnOf(kv.Key) != columnKey)
                                .ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));
        var crossPaths = CrossDbPathsForKeys(collectionId, others.Keys);
        return Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            var repo     = new ConstructorRepository(ctx);
            var resolver = ForeignResolver(crossPaths);
            using var foreignScope = (resolver?.Target as ForeignPropertyReader)?.OpenScope();
            var scope = repo.FilteredRecords(collectionId, others, excludeHidden, resolver);

            var (special, common) = repo.ColumnColorLevels(collectionId, columnKey);
            ct.ThrowIfCancellationRequested();
            var specialIds = special.Keys.ToList();
            var inScope    = specialIds.Count == 0 ? [] : scope.Where(r => specialIds.Contains(r.Id)).Select(r => r.Id).ToList();
            var total      = scope.Count();
            ct.ThrowIfCancellationRequested();

            var counts = new Dictionary<string, int>();
            void Add(RowStyle style, int n)
            {
                if (n <= 0) return;
                var (bg, fg) = ColorQuery.TokensOf(style);
                counts[bg] = counts.GetValueOrDefault(bg) + n;
                counts[fg] = counts.GetValueOrDefault(fg) + n;
            }
            Add(common, total - inScope.Count);
            foreach (var id in inScope) Add(special[id], 1);
            return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                         .Select(kv => (kv.Key, kv.Value)).ToList();
        }, ct);
    }
}
