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
/// Порядок записей по значению данных из связанных записей — тем же порядком, что раньше
/// (<see cref="ColumnSort.BuildComparer"/>: lookup — текст по культуре без учета регистра, rollup —
/// итог как число или начало даты), но сортируются различные значения, а не записи, и ключ итога
/// разбирается один раз на различное значение. Записи раскладываются по рангу устойчиво: равные для
/// порядка («abc» и «ABC») остаются в порядке таблицы. Пустые — в конце. Подписи ошибок итога
/// («(нет доступа)», «(база недоступна)», «(нечисловое значение)») — тоже в конце, вместе с пустыми и
/// в порядке таблицы, при любом направлении (решение пользователя 23.10.2026), как нечисловые
/// значения колонки «Число». У данных lookup подпись ошибки — текст и сортируется как текст: значение
/// цели само может быть текстом в скобках.
/// </summary>
public static class RelationComputedSort
{
    public static List<int> Order(IReadOnlyList<int> ordered, Func<int, string> valueOf, string fieldType, bool asc)
    {
        var values = new string[ordered.Count];
        var distinct = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            values[i] = valueOf(ordered[i]);
            distinct.TryAdd(values[i], 0);
        }
        var sorted = Ranked(distinct.Keys, fieldType, asc, out var tail);
        foreach (var (value, rank) in sorted) distinct[value] = rank;
        foreach (var v in distinct.Keys.Where(v => !sorted.ContainsKey(v)).ToList()) distinct[v] = tail;
        // Устойчивая раскладка подсчетом: внутри ранга — порядок таблицы.
        var counts = new int[tail + 2];
        foreach (var v in values) counts[distinct[v] + 1]++;
        for (var r = 1; r < counts.Length; r++) counts[r] += counts[r - 1];
        var result = new int[ordered.Count];
        for (var i = 0; i < ordered.Count; i++) result[counts[distinct[values[i]]]++] = ordered[i];
        return [.. result];
    }

    /// <summary>Непустые значения столбца по возрастанию — порядком <see cref="Order"/> (окно фильтра
    /// показывает первые). Равные остаются в исходном порядке.</summary>
    public static IEnumerable<T> Ascending<T>(IEnumerable<T> items, Func<T, string> value, string fieldType) =>
        fieldType == "rollup"
            ? items.OrderBy(i => ColumnSort.RollupKey(value(i)))
            : items.OrderBy(value, StringComparer.CurrentCultureIgnoreCase);

    // Ранги значений, которые стоят по порядку; <paramref name="tail"/> — ранг пустых и подписей ошибок
    // итога (их в словаре нет). Ранг растет только на значениях, различных для порядка.
    private static Dictionary<string, int> Ranked(IEnumerable<string> distinct, string fieldType, bool asc, out int tail)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        var rank = -1;
        if (fieldType == "rollup")
        {
            var keyed = distinct.Where(v => v.Length > 0).Select(v => (Value: v, Key: ColumnSort.RollupKey(v)))
                .Where(k => !double.IsPositiveInfinity(k.Key));
            var sorted = (asc ? keyed.OrderBy(k => k.Key) : keyed.OrderByDescending(k => k.Key)).ToList();
            for (var i = 0; i < sorted.Count; i++)
            {
                if (i == 0 || sorted[i - 1].Key.CompareTo(sorted[i].Key) != 0) rank++;
                ranks[sorted[i].Value] = rank;
            }
        }
        else
        {
            var compare = ColumnSort.BuildComparer("text", asc);
            var sorted = distinct.Where(v => v.Length > 0).ToList();
            sorted.Sort((a, b) => compare(a, b));
            for (var i = 0; i < sorted.Count; i++)
            {
                if (i == 0 || compare(sorted[i - 1], sorted[i]) != 0) rank++;
                ranks[sorted[i]] = rank;
            }
        }
        tail = rank + 1;
        return ranks;
    }
}
