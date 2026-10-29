using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    // Оба компонента — int; этот ключ сохраняет точный порядок (Position, Id)
    // первого появления группы без дополнительных запросов или списка записей.
    private const long DatePeriodOrderStride = 2147483648L;
    private sealed record DatePeriodColumnInput(int Id, string? Start, string? End);

    // Обход всей колонки нужен только явным операциям над колонкой (отбор, сортировка,
    // счетчики). Один SQL отдает ровно нужные источники: ни списка всех id, ни повторных
    // запросов по 200 строк, ни матрицы всех свойств в памяти здесь нет.
    private IEnumerable<(int Id, string Value)> StreamDatePeriodColumn(DbField field,
        IQueryable<DbRecordEntity> scope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (field.Id is not int fieldId) yield break;
        var config = DatePeriodConfig.FromConfig(field.ConfigJson);
        var readable = Permissions?.Can(PermAction.ViewCollection, field.CollectionId) != false;
        var calculate = readable && config is { Automatic: true };
        if (calculate)
        {
            var wanted = config!.EndFieldId is int end
                ? new[] { config.StartFieldId, end }.Distinct().ToArray() : [config.StartFieldId];
            var available = db.DbFields.AsNoTracking().Where(f => f.CollectionId == field.CollectionId
                && f.FieldType == "date" && Enumerable.Contains(wanted, f.Id)).Select(f => f.Id).Count();
            calculate = available == wanted.Length;
        }

        var ordered = scope.AsNoTracking().Where(r => r.CollectionId == field.CollectionId)
            .OrderBy(r => r.Position).ThenBy(r => r.Id);
        IQueryable<DatePeriodColumnInput> query;
        if (!readable || config is null || config.Automatic && !calculate)
            query = ordered.Select(r => new DatePeriodColumnInput(r.Id, null, null));
        else if (!config.Automatic)
            query = ordered.Select(r => new DatePeriodColumnInput(r.Id,
                db.DbFieldValues.Where(v => v.RecordId == r.Id && v.FieldId == fieldId)
                    .Select(v => v.Value).FirstOrDefault(), null));
        else
        {
            var startId = config.StartFieldId;
            if (config.EndFieldId is int endId)
                query = ordered.Select(r => new DatePeriodColumnInput(r.Id,
                    db.DbFieldValues.Where(v => v.RecordId == r.Id && v.FieldId == startId)
                        .Select(v => v.Value).FirstOrDefault(),
                    db.DbFieldValues.Where(v => v.RecordId == r.Id && v.FieldId == endId)
                        .Select(v => v.Value).FirstOrDefault()));
            else
                query = ordered.Select(r => new DatePeriodColumnInput(r.Id,
                    db.DbFieldValues.Where(v => v.RecordId == r.Id && v.FieldId == startId)
                        .Select(v => v.Value).FirstOrDefault(), null));
        }

        var now = calculate && config!.EndFieldId is null ? DatePeriodClock() : default;
        if (calculate && config!.EndFieldId is null)
            now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, now.Kind);
        var calculator = config is not null ? DatePeriodValue.Prepare(config) : default;
        // Часто даты повторяются у тысяч записей. Кеш ограничен независимо от размера
        // коллекции; повтор одной пары не разбирает строки дат и не считает часы заново.
        var cache = new Dictionary<(string Start, string End), string>();

        foreach (var row in StreamDatePeriodQuery(query, ct))
        {
            var result = "";
            if (readable && config is { Automatic: false }) result = row.Start ?? "";
            else if (calculate)
            {
                var pair = (row.Start ?? "", row.End ?? "");
                if (!cache.TryGetValue(pair, out result!))
                {
                    result = "";
                    if (DateValue.TryParse(pair.Item1, out var start))
                    {
                        if (config!.EndFieldId is null) result = calculator.Calculate(start, null, now);
                        else if (DateValue.TryParse(pair.Item2, out var end))
                            result = calculator.Calculate(start, end, now);
                    }
                    if (cache.Count >= 1024) cache.Clear();
                    cache[pair] = result;
                }
            }
            yield return (row.Id, result);
        }
    }

    // Async-чтение передает отмену команде PostgreSQL еще до первой строки.
    // Эти синхронные API исполняются в рабочем потоке; завершенные ValueTask не
    // превращаются в отдельный Task на каждую запись большой коллекции.
    private IEnumerable<T> StreamDatePeriodQuery<T>(IQueryable<T> query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sqlite = db.Database.GetDbConnection() as SqliteConnection;
        var opened = sqlite is not null && sqlite.State != System.Data.ConnectionState.Open;
        // Держим дескриптор до снятия регистрации отмены: EF может сам закрыть
        // соединение при DisposeAsync читателя, а callback еще обращается к нему.
        if (opened) db.Database.OpenConnection();
        try
        {
            using var interrupt = sqlite is not null && ct.CanBeCanceled ? ct.Register(() =>
            {
                if (sqlite.Handle is { } handle) SQLitePCL.raw.sqlite3_interrupt(handle);
            }) : default;
            var rows = query.AsAsyncEnumerable().GetAsyncEnumerator(ct);
            try
            {
                while (MoveNext(rows))
                {
                    ct.ThrowIfCancellationRequested();
                    yield return rows.Current;
                }
            }
            finally
            {
                var disposing = rows.DisposeAsync();
                if (disposing.IsCompletedSuccessfully) disposing.GetAwaiter().GetResult();
                else disposing.AsTask().GetAwaiter().GetResult();
            }
        }
        finally { if (opened) db.Database.CloseConnection(); }

        bool MoveNext(IAsyncEnumerator<T> rows)
        {
            try
            {
                var next = rows.MoveNextAsync();
                return next.IsCompletedSuccessfully ? next.Result : next.AsTask().GetAwaiter().GetResult();
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
        }
    }

    private List<(string Value, int Count)> DatePeriodCountsStream(DbField field,
        IQueryable<DbRecordEntity> scope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var config = DatePeriodConfig.FromConfig(field.ConfigJson);
        var records = scope.AsNoTracking().Where(r => r.CollectionId == field.CollectionId);
        if (field.Id is not int fieldId || config is null
            || Permissions?.Can(PermAction.ViewCollection, field.CollectionId) == false)
        {
            var total = records.Count();
            return total > 0 ? [("", total)] : [];
        }
        if (!config.Automatic)
        {
            var grouped = from record in records
                          join value in db.DbFieldValues.AsNoTracking().Where(v => v.FieldId == fieldId)
                              on record.Id equals value.RecordId into cells
                          from value in cells.DefaultIfEmpty()
                          group record by value == null ? "" : value.Value ?? "" into g
                          select new
                          {
                              Value = g.Key, Count = g.Count(),
                              First = g.Min(r => (long)r.Position * DatePeriodOrderStride + r.Id),
                          };
            foreach (var group in StreamDatePeriodQuery(grouped.OrderBy(g => g.First), ct))
                counts[group.Value] = group.Count;
        }
        else
        {
            var startId = config.StartFieldId;
            var endId = config.EndFieldId ?? -1;
            var wanted = config.EndFieldId is null ? new[] { startId } : new[] { startId, endId }.Distinct().ToArray();
            var available = db.DbFields.AsNoTracking().Where(f => f.CollectionId == field.CollectionId
                && f.FieldType == "date" && Enumerable.Contains(wanted, f.Id)).Select(f => f.Id).Count();
            if (available != wanted.Length)
            {
                var total = records.Count();
                return total > 0 ? [("", total)] : [];
            }
            // Сначала группируем одинаковые исходные даты в SQL: тысячи строк с одними
            // датами передаются одним счетчиком и требуют единственного вычисления.
            var grouped = from record in records
                          join start in db.DbFieldValues.AsNoTracking().Where(v => v.FieldId == startId)
                              on record.Id equals start.RecordId into starts
                          from start in starts.DefaultIfEmpty()
                          join end in db.DbFieldValues.AsNoTracking().Where(v => v.FieldId == endId)
                              on record.Id equals end.RecordId into ends
                          from end in ends.DefaultIfEmpty()
                          group record by new
                          {
                              Start = start == null ? "" : start.Value ?? "",
                              End = end == null ? "" : end.Value ?? "",
                          } into g
                          select new
                          {
                              g.Key.Start, g.Key.End, Count = g.Count(),
                              First = g.Min(r => (long)r.Position * DatePeriodOrderStride + r.Id),
                          };
            var now = config.EndFieldId is null ? DatePeriodClock() : default;
            if (config.EndFieldId is null) now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, now.Kind);
            var calculator = DatePeriodValue.Prepare(config);
            foreach (var group in StreamDatePeriodQuery(grouped.OrderBy(g => g.First), ct))
            {
                var value = "";
                if (DateValue.TryParse(group.Start, out var start))
                {
                    if (config.EndFieldId is null) value = calculator.Calculate(start, null, now);
                    else if (DateValue.TryParse(group.End, out var end)) value = calculator.Calculate(start, end, now);
                }
                counts[value] = counts.GetValueOrDefault(value) + group.Count;
            }
        }
        return counts.Where(pair => pair.Key.Length > 0).Select(pair => (pair.Key, pair.Value))
            .Concat(counts.TryGetValue("", out var empty) ? [("", empty)] : []).ToList();
    }
}
