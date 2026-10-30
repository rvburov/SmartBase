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
    private sealed record DatePeriodColumnInput(int Id, string? Value);

    // Обход всей колонки нужен только явным операциям над колонкой (отбор, сортировка,
    // счетчики). Один SQL отдает ровно нужные источники: ни списка всех id, ни повторных
    // запросов по 200 строк, ни матрицы всех свойств в памяти здесь нет.
    private IEnumerable<(int Id, string Value)> StreamDatePeriodColumn(DbField field,
        IQueryable<DbRecordEntity> scope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (field.Id is not int fieldId) yield break;
        var config = DatePeriodConfig.FromConfig(field.ConfigJson);
        var calculate = config is { Automatic: true };
        if (calculate)
        {
            calculate = db.DbFields.AsNoTracking().Any(f => f.CollectionId == field.CollectionId
                && f.FieldType == "date" && f.Id == config!.DateFieldId);
        }

        var ordered = scope.AsNoTracking().Where(r => r.CollectionId == field.CollectionId)
            .OrderBy(r => r.Position).ThenBy(r => r.Id);
        IQueryable<DatePeriodColumnInput> query;
        if (config is null || config.Automatic && !calculate)
            query = ordered.Select(r => new DatePeriodColumnInput(r.Id, null));
        else if (!config.Automatic)
            query = ordered.Select(r => new DatePeriodColumnInput(r.Id,
                db.DbFieldValues.Where(v => v.RecordId == r.Id && v.FieldId == fieldId)
                    .Select(v => v.Value).FirstOrDefault()));
        else
        {
            var dateId = config.DateFieldId;
            query = ordered.Select(r => new DatePeriodColumnInput(r.Id,
                db.DbFieldValues.Where(v => v.RecordId == r.Id && v.FieldId == dateId)
                    .Select(v => v.Value).FirstOrDefault()));
        }

        var calculator = config is not null ? DatePeriodValue.Prepare(config) : default;
        // Часто даты повторяются у тысяч записей. Кеш ограничен независимо от размера
        // коллекции; повтор диапазона не разбирает строку дат и не считает часы заново.
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in StreamDatePeriodQuery(query, ct))
        {
            var result = "";
            if (config is { Automatic: false }) result = row.Value ?? "";
            else if (calculate)
            {
                var source = row.Value ?? "";
                if (!cache.TryGetValue(source, out result!))
                {
                    result = "";
                    if (DateValue.TryParse(source, out var range)) result = calculator.Calculate(range);
                    if (cache.Count >= 1024) cache.Clear();
                    cache[source] = result;
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
        if (field.Id is not int fieldId || config is null)
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
            var dateId = config.DateFieldId;
            if (!db.DbFields.AsNoTracking().Any(f => f.CollectionId == field.CollectionId
                    && f.FieldType == "date" && f.Id == dateId))
            {
                var total = records.Count();
                return total > 0 ? [("", total)] : [];
            }
            // Сначала группируем одинаковые исходные даты в SQL: тысячи строк с одними
            // датами передаются одним счетчиком и требуют единственного вычисления.
            var grouped = from record in records
                          join date in db.DbFieldValues.AsNoTracking().Where(v => v.FieldId == dateId)
                              on record.Id equals date.RecordId into dates
                          from date in dates.DefaultIfEmpty()
                          group record by date == null ? "" : date.Value ?? "" into g
                          select new
                          {
                              Value = g.Key, Count = g.Count(),
                              First = g.Min(r => (long)r.Position * DatePeriodOrderStride + r.Id),
                          };
            var calculator = DatePeriodValue.Prepare(config);
            foreach (var group in StreamDatePeriodQuery(grouped.OrderBy(g => g.First), ct))
            {
                var value = "";
                if (DateValue.TryParse(group.Value, out var range)) value = calculator.Calculate(range);
                counts[value] = counts.GetValueOrDefault(value) + group.Count;
            }
        }
        return counts.Where(pair => pair.Key.Length > 0).Select(pair => (pair.Key, pair.Value))
            .Concat(counts.TryGetValue("", out var empty) ? [("", empty)] : []).ToList();
    }
}
