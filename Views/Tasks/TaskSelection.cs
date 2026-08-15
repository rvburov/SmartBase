using SmartBase.Core.Models;

namespace SmartBase.Views.Tasks;

/// <summary>
/// Ключ задачи в списке. В ключ входит поле-исполнитель: одна запись с двумя такими полями
/// даёт две задачи (см. <see cref="TaskRecord.ExecFieldId"/>), и различать их надо так же,
/// как календарь различает события одной записи парой (запись, поле-дата).
/// </summary>
public readonly record struct TaskKey(int DbId, int RecId, int ExecFieldId);

/// <summary>
/// Выделение карточек списка задач: что выбрано и от чего считается диапазон. Живёт в самом
/// списке (база о выделении ничего не знает) и переживает перезагрузку — список перечитывается
/// на каждый символ поиска и после любой правки, а выделение при этом теряться не должно.
/// </summary>
public sealed class TaskSelection
{
    private readonly HashSet<TaskKey> _keys = [];

    // Якорь диапазона — последняя выделенная кликом задача: Ctrl+Shift+клик выделяет всё
    // между ней и кликнутой (как в «Расписании» и «Журнале» календаря).
    private TaskKey? _anchor;

    public int Count => _keys.Count;

    /// <summary>Ключ задачи; null — задача ещё не сохранена (нет Id).</summary>
    public static TaskKey? KeyOf(TaskRecord t) =>
        t.Id is int id ? new TaskKey(t.DbId, id, t.ExecFieldId) : null;

    public bool Contains(TaskRecord t) => KeyOf(t) is { } k && _keys.Contains(k);

    /// <summary>Снять выделение. true — что-то действительно было выделено (нужно перерисовать).</summary>
    public bool Clear()
    {
        _anchor = null;
        if (_keys.Count == 0) return false;
        _keys.Clear();
        return true;
    }

    /// <summary>Выделить одну задачу, сняв остальные (обычный клик).</summary>
    public void SelectOnly(TaskRecord t)
    {
        _keys.Clear();
        _anchor = KeyOf(t);
        if (_anchor is { } k) _keys.Add(k);
    }

    /// <summary>Добавить/убрать задачу из выделения (Shift+клик).</summary>
    public void Toggle(TaskRecord t)
    {
        if (KeyOf(t) is not { } k) return;
        if (_keys.Remove(k)) return;
        _keys.Add(k);
        _anchor = k;
    }

    /// <summary>
    /// Ctrl+Shift+клик: выделить всё от якоря до <paramref name="clicked"/> по порядку показа
    /// <paramref name="order"/> (плоский список карточек — заголовки групп в него не входят,
    /// поэтому диапазон свободно пересекает границы коллекций). false — якоря нет или он ушёл
    /// из списка: вызывающий делает обычное выделение.
    /// </summary>
    public bool SelectRange(IReadOnlyList<TaskRecord> order, TaskRecord clicked)
    {
        if (_anchor is not { } a || KeyOf(clicked) is not { } c) return false;
        var ai = IndexOf(order, a);
        var ci = IndexOf(order, c);
        if (ai < 0 || ci < 0) return false;

        var (from, to) = ai <= ci ? (ai, ci) : (ci, ai);
        for (var i = from; i <= to; i++)
            if (KeyOf(order[i]) is { } k) _keys.Add(k);
        return true;
    }

    /// <summary>
    /// Оставить только задачи, которые есть в новом наборе. Без этого выделение копило бы ключи
    /// удалённых и отфильтрованных задач всё время работы, а «Удалить» действовало бы на то,
    /// чего на экране нет.
    /// </summary>
    public void Retain(IEnumerable<TaskRecord> alive)
    {
        var live = new HashSet<TaskKey>();
        foreach (var t in alive)
            if (KeyOf(t) is { } k) live.Add(k);

        _keys.RemoveWhere(k => !live.Contains(k));
        if (_anchor is { } a && !live.Contains(a)) _anchor = null;
    }

    /// <summary>Выделенные задачи в порядке показа (порядок важен: он же порядок в меню и в счёте).</summary>
    public List<TaskRecord> In(IReadOnlyList<TaskRecord> order) => order.Where(Contains).ToList();

    private static int IndexOf(IReadOnlyList<TaskRecord> order, TaskKey key)
    {
        for (var i = 0; i < order.Count; i++)
            if (KeyOf(order[i]) is { } k && k == key) return i;
        return -1;
    }
}
