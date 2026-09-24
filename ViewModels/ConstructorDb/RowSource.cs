using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.ViewModels.ConstructorDb;

/// <summary>
/// Откуда виртуальный список берет строки таблицы — итог отбора и сортировки, посчитанный в фоне
/// (<see cref="RowSourcePlanner"/>). Три вида: порядок таблицы, сортировка по индексу значений
/// поля, готовый список id. Сам список строк режима не вычисляет — только листает.
/// </summary>
public abstract record RowSource
{
    /// <summary>Порядок таблицы (Position, Id): keyset и смещение по индексу коллекции.</summary>
    public static readonly RowSource Plain = new PlainRowSource();
}

/// <inheritdoc cref="RowSource.Plain"/>
public sealed record PlainRowSource : RowSource;

/// <summary>Сортировка по колонке прямо по индексу <c>(FieldId, Value)</c>: голова — непустые
/// значения в порядке индекса, хвост — записи без значения в порядке таблицы. Без фильтров и только
/// для типов, чей порядок показа совпадает с порядком индекса (<see cref="ColumnSort.IsIndexSortable"/>).</summary>
public sealed record SortedRowSource(int FieldId, bool Asc) : RowSource;

/// <summary>Готовый список id в порядке показа — фильтр и/или сортировка, которую по индексу не
/// выразить (число, связь, название, вычисляемые колонки).</summary>
public sealed record IdListRowSource(IReadOnlyList<int> Ids) : RowSource;

/// <summary>
/// Планировщик источника строк: по фильтрам и сортировке решает, каким из трех способов листать
/// таблицу, и считает список id в фоне с отменой, когда он нужен. UI-поток не ждет: вызывающий
/// держит один <see cref="CancellationTokenSource"/> на текущий запрос, новый запрос отменяет старый,
/// а устаревший результат отбрасывается по токену.
/// </summary>
public static class RowSourcePlanner
{
    /// <summary>Источник для показа: без фильтров и сортировки — порядок таблицы (мгновенно); без
    /// фильтров с сортировкой по индексируемому типу — сортировка по индексу (мгновенно); иначе —
    /// список id из фона.</summary>
    public static async Task<RowSource> BuildAsync(ConstructorRepository repo, int collectionId, IReadOnlyList<DbField> fields,
                                                   IReadOnlyDictionary<string, HashSet<string>> filters,
                                                   string? sortKey, bool sortAsc, bool excludeHidden, CancellationToken ct)
    {
        if (filters.Count == 0 && sortKey is null) return RowSource.Plain;

        var sortField = sortKey is not null && int.TryParse(sortKey, out var fid)
            ? fields.FirstOrDefault(f => f.Id == fid)
            : null;
        if (filters.Count == 0 && sortField is not null && ColumnSort.IsIndexSortable(sortField.FieldType))
            return new SortedRowSource(sortField.Id!.Value, sortAsc);

        var fieldType = sortKey == TableRowVm.TitleKey ? "text" : sortField?.FieldType;
        var ids = await repo.GetFilteredRecordIdsAsync(collectionId, filters, excludeHidden, sortKey, sortAsc, fieldType, ct);
        ct.ThrowIfCancellationRequested();
        return new IdListRowSource(ids);
    }
}
