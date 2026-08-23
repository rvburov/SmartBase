namespace SmartBase.Views.Tasks;

/// <summary>
/// Что раздел «Задачи» обязан прочитать перед показом. Размеры коллекций читаются всегда — счет
/// по индексам дешев, — а вот сами карточки нужны не каждому виду, и на большой базе их чтение
/// самое дорогое, что раздел делает: десять тысяч строк с коррелированными подзапросами, и так
/// при каждом открытии и каждой правке задачи.
///
/// Отдельно от контроллера, потому что это чистое правило: ошибка в нем либо возвращает разделу
/// прежнюю медлительность, либо — что хуже — оставляет список пустым, когда карточки были нужны.
/// Проверяют его тесты (<c>TaskLoadPlanTests</c>).
/// </summary>
public static class TaskLoadPlan
{
    /// <summary>
    /// Нужен ли сплошной прочит карточек. Нужен ровно пока раздел помещается в память: дальше
    /// страницами читается ЛЮБОЙ вид — и поиск, и каждый из пяти фильтров уходят в SQL, — а
    /// прочитанные «на всякий случай» строки были бы чистой тратой.
    /// </summary>
    /// <param name="sectionTotal">Сколько задач в разделе по счету из базы.</param>
    /// <param name="cap">Крышка чтения (<c>TaskLoadCap</c>): до нее раздел живет в памяти.</param>
    public static bool NeedsTaskRead(int sectionTotal, int cap) => sectionTotal <= cap;

    /// <summary>
    /// Совпадают ли наборы отмеченных коллекций. Сайдбар присылает набор и когда ничего не
    /// менял (возврат в раздел, перемонтирование базы), а на большой базе каждый лишний заход —
    /// это лишний счет по всем коллекциям и волна «Загрузка…» на ровном месте.
    /// <paramref name="current"/> = null — раздел еще ничего не показывал, читать надо.
    /// </summary>
    public static bool SameActiveIds(IReadOnlyDictionary<int, HashSet<int>>? current,
                                     IReadOnlyDictionary<int, HashSet<int>> next)
    {
        if (current is null || current.Count != next.Count) return false;
        foreach (var (dbId, ids) in next)
            if (!current.TryGetValue(dbId, out var mine) || !mine.SetEquals(ids)) return false;
        return true;
    }
}
