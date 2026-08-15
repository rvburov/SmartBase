using SmartBase.Core.Models;

namespace SmartBase.Views.Tasks;

/// <summary>
/// Раскладка задач раздела по коллекциям: над каждой пачкой карточек идёт заголовок своей
/// коллекции. Отдельно от контроллера, потому что это чистое правило порядка — его и
/// проверяют тесты (<c>TaskGroupingTests</c>), а не отрисовку.
///
/// Порядок групп повторяет сайдбар (позиция коллекции в своей БД, базы — в порядке
/// монтирования): пользователь отмечает коллекции галочками сверху вниз и ожидает увидеть
/// их в том же порядке. Группа без известной коллекции («Без коллекции») уходит в конец.
/// </summary>
public static class TaskGrouping
{
    /// <param name="order">
    /// Место коллекции в сайдбаре: (DbId, CollectionId) → порядковый номер. Коллекции,
    /// которой в словаре нет (удалена между загрузкой и показом), достаётся конец списка.
    /// </param>
    /// <param name="dbName">
    /// Имя базы для подзаголовка группы; null — базу не показываем (она одна, это был бы шум).
    /// </param>
    public static List<TaskGroup> Build(
        IEnumerable<TaskRecord> tasks,
        IReadOnlyDictionary<(int DbId, int ColId), int> order,
        Func<int, string>? dbName = null)
    {
        return tasks
            .GroupBy(t => (t.DbId, ColId: t.CollectionId ?? 0))
            .Select(g =>
            {
                var items = g.ToList();
                items.Sort(ByDueThenId);

                var title = TitleText.FirstLine(items[0].CollectionName);

                return (
                    Order: order.TryGetValue(g.Key, out var place) ? place : int.MaxValue,
                    Group: new TaskGroup(
                        DbId:     g.Key.DbId,
                        ColId:    g.Key.ColId,
                        Title:    title.Length > 0 ? title : "Без коллекции",
                        Subtitle: dbName?.Invoke(g.Key.DbId) ?? "",
                        Tasks:    items));
            })
            .OrderBy(x => x.Order)
            .Select(x => x.Group)
            .ToList();
    }

    /// <summary>Порядок задач внутри группы: срок ↑, «без даты» в конец, тай-брейк по Id.</summary>
    public static readonly Comparison<TaskRecord> ByDueThenId = (a, b) =>
    {
        var an = a.DueStart is null;
        var bn = b.DueStart is null;
        if (an != bn) return an ? 1 : -1;
        if (!an)
        {
            var byDue = a.DueStart!.Value.CompareTo(b.DueStart!.Value);
            if (byDue != 0) return byDue;
        }
        return (a.Id ?? 0).CompareTo(b.Id ?? 0);
    };
}
