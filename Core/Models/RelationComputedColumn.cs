namespace SmartBase.Core.Models;

/// <summary>
/// Столбец данных из связанных записей целиком — для отбора, сортировки и окна фильтра. Хранится
/// компактно: у записей с ребрами — номер значения среди различных (<see cref="Ids"/> по
/// возрастанию, поиск делением пополам), у остальных одно значение <see cref="Default"/> (итог
/// пустого набора целей). <see cref="ColumnError"/> — одно значение у всех записей (нет доступа к
/// коллекции столбца, вычисление не настроено, цикл).
/// </summary>
public sealed class RelationComputedColumn
{
    public string? ColumnError { get; init; }
    public string Default { get; init; } = "";
    public int[] Ids { get; init; } = [];
    public int[] ValueIndex { get; init; } = [];
    public string[] Values { get; init; } = [];

    /// <summary>Столбец, где у всех записей одно значение.</summary>
    public static RelationComputedColumn Constant(string value) => new() { ColumnError = value, Default = value };

    public string ValueOf(int id) => ColumnError
        ?? (Array.BinarySearch(Ids, id) is var i and >= 0 ? Values[ValueIndex[i]] : Default);

    /// <summary>Записи с ребрами, значение которых подходит под <paramref name="match"/>.</summary>
    public List<int> IdsWhere(Func<string, bool> match)
    {
        var fits = Values.Select(match).ToArray();
        var result = new List<int>();
        for (var i = 0; i < Ids.Length; i++)
            if (fits[ValueIndex[i]]) result.Add(Ids[i]);
        return result;
    }
}

/// <summary>
/// Порядок записей по значению данных из связанных записей — тем же компаратором, что раньше
/// (<see cref="ColumnSort.BuildComparer"/>: lookup — текст, rollup — итог), но ключ разбирается
/// один раз на различное значение: различные значения сортируются, записи раскладываются по рангу
/// устойчиво, равные для компаратора («abc» и «ABC») остаются в порядке таблицы. Пустые — в конце.
/// Подписи ошибок итога («(нет доступа)», «(база недоступна)», «(нечисловое значение)») — тоже в
/// конце, вместе с пустыми и в порядке таблицы, при любом направлении (решение пользователя
/// 23.10.2026), как нечисловые значения колонки «Число».
/// </summary>
public static class RelationComputedSort
{
    public static List<int> Order(IReadOnlyList<int> ordered, Func<int, string> valueOf, string fieldType, bool asc)
    {
        var rollup = fieldType == "rollup";
        var comparerType = rollup ? "rollup" : "text";
        var compare = ColumnSort.BuildComparer(comparerType, asc);
        var values = new string[ordered.Count];
        var distinct = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            values[i] = valueOf(ordered[i]);
            distinct.TryAdd(values[i], 0);
        }
        bool Tail(string v) => v.Length == 0 || rollup && double.IsPositiveInfinity(ColumnSort.RollupKey(v));
        var sorted = distinct.Keys.Where(v => !Tail(v)).ToList();
        sorted.Sort((a, b) => compare(a, b));
        // Ранг растет только на значениях, различных для компаратора.
        var rank = 0;
        for (var i = 0; i < sorted.Count; i++)
        {
            if (i > 0 && compare(sorted[i - 1], sorted[i]) != 0) rank++;
            distinct[sorted[i]] = rank;
        }
        var tail = sorted.Count == 0 ? 0 : rank + 1;
        foreach (var v in distinct.Keys.Where(Tail).ToList()) distinct[v] = tail;
        // Устойчивая раскладка подсчетом: внутри ранга — порядок таблицы.
        var counts = new int[tail + 2];
        foreach (var v in values) counts[distinct[v] + 1]++;
        for (var r = 1; r < counts.Length; r++) counts[r] += counts[r - 1];
        var result = new int[ordered.Count];
        for (var i = 0; i < ordered.Count; i++) result[counts[distinct[values[i]]]++] = ordered[i];
        return [.. result];
    }
}
