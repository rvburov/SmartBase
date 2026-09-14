using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Операции над большим выделением ячеек — на ОТДЕЛЬНОМ подключении, с фонового потока.
///
/// Общий контекст трогать с фонового потока нельзя: его параллельно использует интерфейс, и EF
/// отвечает на это «A second operation was started on this context instance». Поэтому чтение
/// строк выделения и пакетная запись идут через клон репозитория на своем подключении — тем же
/// путем, что фоновая подгрузка страниц таблицы (<see cref="LoadPageAsync"/>) и применение
/// привязки (<see cref="ApplyRowsIsolated"/>).
///
/// Права спрашиваются ЗДЕСЬ, на публичной точке входа: изолированный клон прав не несет.
///
/// После записи общий контекст не знает, что поменялось: отслеживаемые им значения и записи
/// затронутых ячеек снимаются с учета (<see cref="ForgetTracked"/>), и следующее чтение берет
/// свежие. Слияние побуквенных правок по этим ячейкам тоже забывается — по той же причине, что и
/// после записи в общем контексте.
/// </summary>
public sealed partial class ConstructorRepository
{
    /// <summary>Строка таблицы для операции над выделением: запись, ее название, аудит и
    /// значения ТОЛЬКО нужных столбцов — на ста тысячах строк все значения записи заняли бы память
    /// впустую.</summary>
    public sealed record CellRow(int RecordId, int Position, string Title,
                                 string CreatedAt, string UpdatedAt, string CreatedBy, string UpdatedBy,
                                 Dictionary<int, string> Values);

    /// <summary>
    /// Страница строк выделения в порядке таблицы. Режим — как у <see cref="LoadPageAsync"/>:
    /// <paramref name="idSlice"/> (фильтр или сортировка), иначе keyset после
    /// (<paramref name="afterPos"/>, <paramref name="afterId"/>), иначе смещение <paramref name="skip"/>.
    /// </summary>
    public Task<List<CellRow>> ReadCellRowsAsync(int collectionId, bool excludeHidden, IReadOnlyList<int>? idSlice,
                                                 int skip, int take, int? afterPos, int? afterId,
                                                 IReadOnlyList<int> fieldIds, CancellationToken ct)
    {
        Demand(PermAction.ViewCollection, collectionId);
        var path = db.DbPath;
        var fields = fieldIds.ToList();

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var ctx = new AppDbContext(path);
            ctx.ApplyPerformancePragmas();
            var repo = new ConstructorRepository(ctx);

            var records = idSlice is not null
                ? repo.GetRecordsByIdsOrdered(idSlice)
                : afterPos is int p && afterId is int a
                    ? repo.GetRecordsPageAfter(collectionId, p, a, take, excludeHidden)
                    : repo.GetRecordsPage(collectionId, skip, take, excludeHidden);

            var ids    = records.Select(r => r.Id!.Value).ToList();
            var values = new Dictionary<int, Dictionary<int, string>>();
            if (fields.Count > 0 && ids.Count > 0)
            {
                var rows = ctx.DbFieldValues.AsNoTracking()
                    .Where(v => ids.Contains(v.RecordId) && fields.Contains(v.FieldId))
                    .Select(v => new { v.RecordId, v.FieldId, v.Value })
                    .ToList();
                foreach (var v in rows)
                {
                    if (!values.TryGetValue(v.RecordId, out var byField)) values[v.RecordId] = byField = [];
                    byField[v.FieldId] = v.Value ?? "";
                }
            }

            return records.Select(r => new CellRow(
                r.Id!.Value, r.Position, r.Title ?? "",
                r.CreatedAt ?? "", r.UpdatedAt ?? "", r.CreatedBy ?? "", r.UpdatedBy ?? "",
                values.TryGetValue(r.Id!.Value, out var v) ? v : [])).ToList();
        }, ct);
    }

    /// <summary><see cref="SetCellsBulk"/> на отдельном подключении — для больших пакетов.</summary>
    public async Task<CellBulkResult> SetCellsBulkIsolatedAsync(int collectionId, IReadOnlyList<CellWrite> cells,
                                                                IReadOnlyList<TitleWrite>? titles, string operation)
    {
        Demand(PermAction.EditRecord, collectionId);
        var path = db.DbPath;

        var result = await Task.Run(() =>
        {
            using var ctx = new AppDbContext(path);
            return new ConstructorRepository(ctx).SetCellsBulk(collectionId, cells, titles, operation);
        });

        // Итог без списка изменений (больше предела отмены) не говорит, какие ячейки тронуты, —
        // тогда берем все, что просили записать.
        ForgetTracked(result.ChangesTruncated
            ? cells.Select(c => (c.RecordId, (int?)c.FieldId)).Concat((titles ?? []).Select(t => (t.RecordId, (int?)null)))
            : result.Changes.Select(c => (c.RecordId, c.FieldId)));
        return result;
    }

    /// <summary><see cref="SetLinksBulk"/> на отдельном подключении — для больших пакетов.</summary>
    public async Task<LinkBulkResult> SetLinksBulkIsolatedAsync(int collectionId, IReadOnlyList<LinkWrite> writes, string operation)
    {
        Demand(PermAction.EditRecord, collectionId);
        var path = db.DbPath;

        var result = await Task.Run(() =>
        {
            using var ctx = new AppDbContext(path);
            return new ConstructorRepository(ctx).SetLinksBulk(collectionId, writes, operation);
        });

        var touched = result.Changes.Select(c => (c.RecordId, c.FieldId)).ToHashSet();
        foreach (var link in db.DbRecordLinks.Local.Where(l => touched.Contains((l.FromRecordId, l.FieldId))).ToList())
            db.Entry(link).State = EntityState.Detached;
        ForgetTracked(result.Changes.Select(c => (c.RecordId, (int?)null)));
        return result;
    }

    /// <summary><see cref="ResolveRelationTitles"/> на отдельном подключении.</summary>
    public Task<Dictionary<string, TitleResolution>> ResolveRelationTitlesIsolatedAsync(int fieldId, IReadOnlyCollection<string> texts)
    {
        Demand(PermAction.ViewCollection, CollectionOfField(fieldId));
        var path = db.DbPath;
        var list = texts.ToList();

        return Task.Run(() =>
        {
            using var ctx = new AppDbContext(path);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).ResolveRelationTitles(fieldId, list);
        });
    }

    // Снять с учета общего контекста значения и записи, которые изменила изолированная запись, и
    // забыть слияние побуквенных правок по ним. FieldId null — название записи.
    private void ForgetTracked(IEnumerable<(int RecordId, int? FieldId)> cells)
    {
        var values  = db.DbFieldValues.Local.ToDictionary(v => (v.RecordId, v.FieldId));
        var records = db.DbRecords.Local.ToDictionary(r => r.Id);

        var seenRecords = new HashSet<int>();
        foreach (var (recordId, fieldId) in cells)
        {
            if (fieldId is int fid)
            {
                if (values.Remove((recordId, fid), out var tracked)) db.Entry(tracked).State = EntityState.Detached;
                _editMerges.Remove(("field", recordId, fid));
            }
            else
            {
                _editMerges.Remove(("title", recordId, 0));
            }

            if (seenRecords.Add(recordId) && records.TryGetValue(recordId, out var record))
                db.Entry(record).State = EntityState.Detached;
        }
    }
}
