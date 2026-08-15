using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Views.Tasks;

/// <summary>Быстрый фильтр раздела «Задачи» — выпадающий список в навбаре.</summary>
public enum TaskFilterKind
{
    All,
    Today,
    AssignedToMe,
    NoDue,
    Overdue,
    Done,
}

/// <summary>
/// Правила быстрых фильтров и их счётчики. Отдельно от навбара и контроллера: это чистая
/// логика «какая задача куда попадает», её и проверяют тесты (<c>TaskFiltersTests</c>).
///
/// Определения совпадают с тем, что показывает сама карточка задачи (см. <c>TaskCardVm</c>),
/// иначе счётчик и список спорили бы с чипами: просроченной считается незакрытая задача,
/// чей срок уже истёк, — у выполненной чипа просрочки тоже нет.
/// </summary>
public static class TaskFilters
{
    /// <summary>Пункты списка в порядке показа.</summary>
    public static readonly TaskFilterKind[] All =
        [TaskFilterKind.All, TaskFilterKind.Today, TaskFilterKind.AssignedToMe,
         TaskFilterKind.NoDue, TaskFilterKind.Overdue, TaskFilterKind.Done];

    public static string Label(TaskFilterKind kind) => kind switch
    {
        TaskFilterKind.Today        => "Сегодня",
        TaskFilterKind.AssignedToMe => "Мне назначено",
        TaskFilterKind.NoDue        => "Без срока",
        TaskFilterKind.Overdue      => "Просрочено",
        TaskFilterKind.Done         => "Выполнены",
        _                           => "Все задачи",
    };

    /// <summary>Ключ иконки из общего реестра (Assets/Styles/Icons.axaml).</summary>
    public static string IconKey(TaskFilterKind kind) => kind switch
    {
        TaskFilterKind.Today        => "IconCalendarDate",
        TaskFilterKind.AssignedToMe => "IconUser",
        // Тот же календарь, что у «Сегодня», только пустой — срока нет, показывать нечего.
        TaskFilterKind.NoDue        => "IconCalendarBlank",
        TaskFilterKind.Overdue      => "IconClock",
        TaskFilterKind.Done         => "IconCheckCheck",
        _                           => "IconSquareCheck",
    };

    /// <param name="now">«Сейчас» — от него считаются «Сегодня» и просрочка.</param>
    /// <param name="me">Отображаемое имя текущего пользователя (значение поля-исполнителя).</param>
    public static bool Matches(TaskRecord t, TaskFilterKind kind, DateTime now, string me) => kind switch
    {
        // Задача «на сегодня», если сегодняшний день попадает в её период: однодневная
        // с сегодняшним сроком и многодневная, которая идёт через сегодня, — обе на виду.
        TaskFilterKind.Today => t.DueStart is DateTime s &&
                                s.Date <= now.Date && (t.DueEnd ?? s).Date >= now.Date,

        TaskFilterKind.AssignedToMe => t.Executor.Equals(me, StringComparison.CurrentCultureIgnoreCase),

        TaskFilterKind.NoDue => t.DueStart is null,

        // Как чип карточки: у выполненной просрочки нет, сколько бы срок ни истёк.
        TaskFilterKind.Overdue => t.Status != ConstructorRepository.TaskStatusDone &&
                                  t.DueStart is DateTime start && (t.DueEnd ?? start) <= now,

        TaskFilterKind.Done => t.Status == ConstructorRepository.TaskStatusDone,

        _ => true,
    };

    /// <summary>Счётчики всех пунктов одним проходом набора.</summary>
    public static Dictionary<TaskFilterKind, int> Counts(
        IReadOnlyCollection<TaskRecord> tasks, DateTime now, string me)
    {
        var counts = All.ToDictionary(k => k, _ => 0);
        foreach (var t in tasks)
            foreach (var kind in All)
                if (Matches(t, kind, now, me)) counts[kind]++;
        return counts;
    }
}
