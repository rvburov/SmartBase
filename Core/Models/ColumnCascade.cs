namespace SmartBase.Core.Models;

/// <summary>
/// Каскад фильтров служебных таблиц — то же правило, что у таблицы конструктора: список значений
/// колонки в окне фильтра считается по строкам, прошедшим фильтры ОСТАЛЬНЫХ колонок. Отобрали в
/// «Пользователе» одного человека — в «Действии» остаются только его действия, со своими
/// счетчиками. Собственный фильтр колонки список не режет: снятое значение должно оставаться в
/// списке, иначе его не вернуть в отбор.
///
/// Таблицы с журналом в базе считают каскад в SQL и берут отсюда только <see cref="Others"/>;
/// таблица, которая держит строки в памяти («Пользователи» панели администратора), — все целиком.
/// </summary>
public static class ColumnCascade
{
    /// <summary>
    /// Строки, прошедшие фильтры всех колонок, кроме <paramref name="except"/>. Фильтр — набор
    /// разрешенных СЫРЫХ значений колонки; <paramref name="valueOf"/> отдает сырое значение
    /// колонки у строки.
    /// </summary>
    public static IEnumerable<T> Pass<T>(IEnumerable<T> rows,
                                         IReadOnlyDictionary<string, HashSet<string>> filters,
                                         Func<string, T, string> valueOf,
                                         string? except = null)
    {
        foreach (var (key, allowed) in filters)
        {
            if (key == except) continue;
            rows = rows.Where(row => allowed.Contains(valueOf(key, row)));
        }
        return rows;
    }

    /// <summary>Значения колонки <paramref name="key"/> со счетчиками — по строкам, прошедшим
    /// фильтры остальных колонок.</summary>
    public static List<(string Value, int Count)> Counts<T>(IEnumerable<T> rows, string key,
                                                            IReadOnlyDictionary<string, HashSet<string>> filters,
                                                            Func<string, T, string> valueOf) =>
        Pass(rows, filters, valueOf, except: key)
            .GroupBy(row => valueOf(key, row), StringComparer.Ordinal)
            .Select(group => (group.Key, group.Count()))
            .ToList();

    /// <summary>
    /// Снимок фильтров остальных колонок для фонового запроса. Копия, а не ссылки на живые наборы:
    /// запрос идет на другом потоке, а таблица за это время может поменять свои фильтры.
    /// </summary>
    public static Dictionary<string, IReadOnlyCollection<string>> Others(
        IReadOnlyDictionary<string, HashSet<string>> filters, string key) =>
        filters.Where(kv => kv.Key != key)
               .ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)new HashSet<string>(kv.Value));
}
