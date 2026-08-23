namespace SmartBase.Core.Models;

/// <summary>Уровень узла в дереве дат фильтра.</summary>
public enum DateNodeKind
{
    /// <summary>Год: «2026».</summary>
    Year,
    /// <summary>Месяц: «Август».</summary>
    Month,
    /// <summary>День: «23» — лист, отмечается галочкой.</summary>
    Day,
    /// <summary>Значения без даты: пустые и нераспознанные. Один узел верхнего уровня.</summary>
    Undated,
}

/// <summary>
/// Узел дерева дат в окне фильтра колонки. Дерево строится один раз
/// (<see cref="DateFilterTree.Build"/>) и дальше только читается — состояние галочки и
/// раскрытия живет в вид-модели.
/// </summary>
/// <param name="Raws">СЫРЫЕ значения колонки, попавшие в это поддерево. Фильтр работает
/// именно ими (в SQL уходит <c>IN (...)</c>), поэтому у дня их может быть несколько:
/// «2 августа 10:00» и «2 августа 14:00» — разные значения одного дня.</param>
public sealed record DateFilterNode(
    DateNodeKind Kind,
    string Label,
    int Count,
    int Level,
    IReadOnlyList<string> Raws,
    IReadOnlyList<DateFilterNode> Children);

/// <summary>
/// Дерево «Год → Месяц → День» для чеклиста фильтра по дате.
///
/// Зачем: значения-даты в плоском списке нечитаемы. В таблице конструктора там лежит
/// хранимый вид <see cref="DateValue"/> целиком («2026-08-02T10:00/2026-08-02T11:00»), в
/// журналах — «2026-08-23»; сотня записей давала сотню строк, по которым нельзя выбрать
/// «весь август». Дерево сворачивает их в годы и месяцы, а отмечается ДЕНЬ — все записи
/// этого дня с любым временем.
///
/// Тип чистый (без Avalonia и без знания формата хранения): как из сырого значения
/// получить дату, решает вызывающий (<c>parse</c>), поэтому одно дерево обслуживает и
/// свойства-даты конструктора, и колонки времени в «Диагностике» и «Истории изменений».
///
/// Запись с диапазоном на несколько дней попадает ТОЛЬКО в день начала: так сумма
/// счетчиков по дням равна числу записей, и так же ее видит сортировка колонки.
/// </summary>
public static class DateFilterTree
{
    private static readonly string[] MonthNames =
    [
        "Январь", "Февраль", "Март", "Апрель", "Май", "Июнь",
        "Июль", "Август", "Сентябрь", "Октябрь", "Ноябрь", "Декабрь",
    ];

    /// <summary>Подпись узла «без даты» (пустые и нераспознанные значения).</summary>
    public const string UndatedLabel = "(Пусто)";

    /// <summary>
    /// Строит дерево из пар «сырое значение → количество записей».
    /// </summary>
    /// <param name="parse">Сырое значение → дата (null, если значение пустое или не дата).</param>
    /// <param name="newestFirst">true — годы, месяцы и дни идут от новых к старым
    /// (порядок повторяет выбранную сортировку колонки).</param>
    public static IReadOnlyList<DateFilterNode> Build(
        IEnumerable<(string Raw, int Count)> values,
        Func<string, DateTime?> parse,
        bool newestFirst = false)
    {
        // Даты складываем в трехуровневый словарь, недатированное — в отдельный список:
        // год → месяц → день → (сырые значения, сумма).
        var years   = new Dictionary<int, Dictionary<int, Dictionary<int, (List<string> Raws, int Count)>>>();
        var undated = new List<string>();
        var undatedCount = 0;

        foreach (var (raw, count) in values)
        {
            var date = string.IsNullOrEmpty(raw) ? null : parse(raw);
            if (date is not { } d)
            {
                undated.Add(raw);
                undatedCount += count;
                continue;
            }

            var months = years.TryGetValue(d.Year, out var m) ? m : years[d.Year] = [];
            var days   = months.TryGetValue(d.Month, out var dd) ? dd : months[d.Month] = [];
            (List<string> Raws, int Count) slot = days.TryGetValue(d.Day, out var s) ? s : ([], 0);
            slot.Raws.Add(raw);
            days[d.Day] = (slot.Raws, slot.Count + count);
        }

        var roots = new List<DateFilterNode>();
        foreach (var year in Order(years.Keys, newestFirst))
        {
            var monthNodes = new List<DateFilterNode>();
            foreach (var month in Order(years[year].Keys, newestFirst))
            {
                var dayNodes = new List<DateFilterNode>();
                foreach (var day in Order(years[year][month].Keys, newestFirst))
                {
                    var (raws, count) = years[year][month][day];
                    dayNodes.Add(new DateFilterNode(DateNodeKind.Day, day.ToString(), count, 2, raws, []));
                }
                monthNodes.Add(new DateFilterNode(DateNodeKind.Month, MonthNames[month - 1],
                                                  dayNodes.Sum(n => n.Count), 1,
                                                  dayNodes.SelectMany(n => n.Raws).ToList(), dayNodes));
            }
            roots.Add(new DateFilterNode(DateNodeKind.Year, year.ToString(),
                                         monthNodes.Sum(n => n.Count), 0,
                                         monthNodes.SelectMany(n => n.Raws).ToList(), monthNodes));
        }

        // «(Пусто)» — всегда в конце: это не дата, и в порядке годов ему места нет.
        if (undated.Count > 0)
            roots.Add(new DateFilterNode(DateNodeKind.Undated, UndatedLabel, undatedCount, 0, undated, []));

        return roots;
    }

    /// <summary>
    /// Ветки, подходящие под запрос поиска. Совпадение с годом или месяцем оставляет ветку
    /// целиком (искали «Август» — нужен весь август), совпадение с днем оставляет только его,
    /// но вместе с родителями — иначе непонятно, к какому месяцу относится «23».
    ///
    /// День ищется и по номеру («23»), и по полной дате («23.08.2026», «23.08»): в плоском
    /// списке пользователь искал именно так.
    /// </summary>
    public static IReadOnlyList<DateFilterNode> Search(IReadOnlyList<DateFilterNode> roots, string? query)
    {
        var q = query?.Trim() ?? "";
        if (q.Length == 0) return roots;

        var result = new List<DateFilterNode>();
        foreach (var year in roots)
        {
            if (year.Kind == DateNodeKind.Undated)
            {
                if (Has(year.Label, q)) result.Add(year);
                continue;
            }

            if (Has(year.Label, q)) { result.Add(year); continue; }   // «2026» — весь год

            var months = new List<DateFilterNode>();
            foreach (var month in year.Children)
            {
                if (Has(month.Label, q)) { months.Add(month); continue; }   // «Август» — весь месяц

                var days = month.Children
                    .Where(day => Has(day.Label, q) || Has(FullDate(day, month, year), q))
                    .ToList();
                if (days.Count > 0)
                    months.Add(month with { Children = days, Raws = days.SelectMany(d => d.Raws).ToList(),
                                            Count = days.Sum(d => d.Count) });
            }

            if (months.Count > 0)
                result.Add(year with { Children = months, Raws = months.SelectMany(m => m.Raws).ToList(),
                                       Count = months.Sum(m => m.Count) });
        }
        return result;
    }

    /// <summary>
    /// Галочка узла: true — только если отмечено ВСЕ поддерево. Снят хоть один день —
    /// месяц и год показываются снятыми, промежуточного состояния нет: галочка ветки
    /// читается как «(Выделить все)» для нее, а не как индикатор частичного выбора.
    /// </summary>
    public static bool IsFullyChecked(DateFilterNode node, ICollection<string> checkedRaws) =>
        node.Raws.Count > 0 && node.Raws.All(checkedRaws.Contains);

    /// <summary>Все узлы поддерева в порядке показа (сам узел, потом потомки).</summary>
    public static IEnumerable<DateFilterNode> Flatten(IEnumerable<DateFilterNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    // «23.08.2026» — по такой строке день тоже должен находиться поиском.
    private static string FullDate(DateFilterNode day, DateFilterNode month, DateFilterNode year)
    {
        var m = Array.IndexOf(MonthNames, month.Label) + 1;
        return $"{day.Label.PadLeft(2, '0')}.{m:00}.{year.Label}";
    }

    private static bool Has(string text, string query) =>
        text.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static IEnumerable<int> Order(IEnumerable<int> keys, bool descending) =>
        descending ? keys.OrderByDescending(k => k) : keys.OrderBy(k => k);
}
