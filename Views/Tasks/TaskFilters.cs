using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Views.Tasks;

/// <summary>Вид быстрого фильтра раздела «Задачи» — выпадающий список в навбаре.</summary>
public enum TaskFilterKind
{
    All,
    Today,
    AssignedToMe,
    NoDue,
    Overdue,
    /// <summary>Одна стадия задачи — значение поля «Статус»; какая именно, говорит
    /// <see cref="TaskFilter.Stage"/>. Стадий у коллекций сколько угодно, поэтому это не
    /// набор пунктов enum, а один вид с параметром.</summary>
    Stage,
}

/// <summary>
/// Правила показа раздела, которые задает человек в «Настройки → Задачи». Передаются явно, а
/// не читаются из статики: фильтр, чип карточки и SQL-условие постраничного пути получают ОДИН
/// набор правил, а тесты задают его без хранилища настроек.
/// </summary>
/// <param name="OverdueNextDay">Просрочка наступает со следующего дня после срока
/// (false — с момента срока: задача на 10:00 просрочена в 10:01).</param>
/// <param name="ShowDone">Показывать выполненные во всех фильтрах (false — только в стадии
/// «Выполнено»).</param>
public readonly record struct TaskRules(bool OverdueNextDay, bool ShowDone)
{
    public static readonly TaskRules Default = new(OverdueNextDay: false, ShowDone: true);
}

/// <summary>Пункт быстрого фильтра: стандартное правило либо стадия.</summary>
/// <param name="Stage">Имя стадии — только для <see cref="TaskFilterKind.Stage"/>.</param>
public readonly record struct TaskFilter(TaskFilterKind Kind, string Stage = "")
{
    public static readonly TaskFilter All = new(TaskFilterKind.All);

    public static TaskFilter Of(TaskFilterKind kind)  => new(kind);
    public static TaskFilter ForStage(string stage)   => new(TaskFilterKind.Stage, stage);

    /// <summary>Имя стадии строкой всегда: <c>default(TaskFilter)</c> оставляет поле null.</summary>
    public string StageName => Stage ?? "";

    public bool IsStage     => Kind == TaskFilterKind.Stage;
    public bool IsDoneStage => IsStage && StageName == ConstructorRepository.TaskStatusDone;

    /// <summary>Строковый ключ — для настроек и словарей репозитория: «All», «Today», …,
    /// «Stage:В работе».</summary>
    public string Key => IsStage ? "Stage:" + StageName : Kind.ToString();

    public string Label   => TaskFilters.Label(this);
    public string IconKey => TaskFilters.IconKey(this);

    private const string StagePrefix = "Stage:";

    /// <summary>Обратный разбор <see cref="Key"/>; null — ключ не распознан.</summary>
    public static TaskFilter? ParseKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (key.StartsWith(StagePrefix, StringComparison.Ordinal))
            return key.Length > StagePrefix.Length ? ForStage(key[StagePrefix.Length..]) : null;
        return Enum.TryParse<TaskFilterKind>(key, out var kind) && kind != TaskFilterKind.Stage
            ? Of(kind)
            : null;
    }
}

/// <summary>
/// Правила быстрых фильтров и их счетчики. Отдельно от навбара и контроллера: это чистая
/// логика «какая задача куда попадает», ее и проверяют тесты (<c>TaskFiltersTests</c>).
///
/// Определения совпадают с тем, что показывает сама карточка задачи (см. <c>TaskCardVm</c>),
/// иначе счетчик и список спорили бы с чипами: просроченной считается незакрытая задача,
/// чей срок уже истек, — у выполненной чипа просрочки тоже нет. Момент «истек» задает
/// <see cref="TaskRules.OverdueNextDay"/>, и чип, и фильтр берут его из одного
/// <see cref="IsOverdue"/>.
///
/// Список в навбаре собран из двух групп через черту: стандартные пункты
/// (<see cref="Standard"/>) и стадии — все пункты поля «Статус» отмеченных коллекций
/// (<c>ConstructorRepository.GetTaskStages</c>). «Выполнено» живет только в группе стадий.
/// </summary>
public static class TaskFilters
{
    /// <summary>Стандартные пункты в порядке показа; дальше идут стадии.</summary>
    public static readonly TaskFilter[] Standard =
        [TaskFilter.Of(TaskFilterKind.All), TaskFilter.Of(TaskFilterKind.Today),
         TaskFilter.Of(TaskFilterKind.AssignedToMe), TaskFilter.Of(TaskFilterKind.NoDue),
         TaskFilter.Of(TaskFilterKind.Overdue)];

    /// <summary>Стандартные пункты плюс стадии — полный список навбара.</summary>
    public static List<TaskFilter> WithStages(IEnumerable<string> stages)
    {
        var list = new List<TaskFilter>(Standard);
        foreach (var s in stages) list.Add(TaskFilter.ForStage(s));
        return list;
    }

    public static string Label(TaskFilter f) => f.Kind switch
    {
        TaskFilterKind.Today        => "Сегодня",
        TaskFilterKind.AssignedToMe => "Мне назначено",
        TaskFilterKind.NoDue        => "Без срока",
        TaskFilterKind.Overdue      => "Просрочено",
        TaskFilterKind.Stage        => f.StageName,
        _                           => "Все задачи",
    };

    /// <summary>Ключ иконки из общего реестра (Assets/Styles/Icons.axaml). У трех стандартных
    /// стадий свои иконки, у всех прочих — часы: стадии заводит человек, и рисовать под каждую
    /// иконку неоткуда.</summary>
    public static string IconKey(TaskFilter f) => f.Kind switch
    {
        TaskFilterKind.Today        => "IconCalendarDate",
        TaskFilterKind.AssignedToMe => "IconUser",
        // Тот же календарь, что у «Сегодня», только пустой — срока нет, показывать нечего.
        TaskFilterKind.NoDue        => "IconCalendarBlank",
        TaskFilterKind.Overdue      => "IconClock",
        TaskFilterKind.Stage        => f.StageName switch
        {
            ConstructorRepository.TaskStatusPlanned => "IconCircleDashed",
            ConstructorRepository.TaskStatusActive  => "IconPlay",
            ConstructorRepository.TaskStatusDone    => "IconCheckCheck",
            _                                       => "IconClock",
        },
        _                           => "IconSquareCheck",
    };

    public static bool IsDone(TaskRecord t) => t.Status == ConstructorRepository.TaskStatusDone;

    /// <summary>Просрочена ли задача — единое правило для чипа карточки, фильтра и счетчиков.
    /// Выполненная не просрочена никогда, сколько бы срок ни истек.</summary>
    public static bool IsOverdue(TaskRecord t, DateTime now, TaskRules rules)
    {
        if (IsDone(t) || t.DueStart is not DateTime start) return false;
        var end = t.DueEnd ?? start;
        return rules.OverdueNextDay ? end.Date < now.Date : end <= now;
    }

    /// <summary>Стадия — точное значение поля «Статус». Пустой статус карточка показывает как
    /// «Запланировано», поэтому и в стадию «Запланировано» он попадает.</summary>
    public static bool MatchesStage(TaskRecord t, string stage) =>
        t.Status == stage ||
        (stage == ConstructorRepository.TaskStatusPlanned && t.Status.Length == 0);

    /// <param name="now">«Сейчас» — от него считаются «Сегодня» и просрочка.</param>
    /// <param name="me">Отображаемое имя текущего пользователя (значение поля-исполнителя).</param>
    /// <param name="rules">Правила показа из настроек раздела.</param>
    public static bool Matches(TaskRecord t, TaskFilter f, DateTime now, string me, TaskRules rules)
    {
        // Скрытые выполненные не попадают никуда, кроме собственной стадии.
        if (!rules.ShowDone && !f.IsDoneStage && IsDone(t)) return false;

        return f.Kind switch
        {
            // Задача «на сегодня», если сегодняшний день попадает в ее период: однодневная
            // с сегодняшним сроком и многодневная, которая идет через сегодня, — обе на виду.
            TaskFilterKind.Today => t.DueStart is DateTime s &&
                                    s.Date <= now.Date && (t.DueEnd ?? s).Date >= now.Date,

            TaskFilterKind.AssignedToMe => t.Executor.Equals(me, StringComparison.CurrentCultureIgnoreCase),

            TaskFilterKind.NoDue => t.DueStart is null,

            TaskFilterKind.Overdue => IsOverdue(t, now, rules),

            TaskFilterKind.Stage => MatchesStage(t, f.StageName),

            _ => true,
        };
    }

    /// <summary>Счетчики всех пунктов одним проходом набора.</summary>
    public static Dictionary<TaskFilter, int> Counts(
        IReadOnlyCollection<TaskRecord> tasks, IReadOnlyList<TaskFilter> filters,
        DateTime now, string me, TaskRules rules)
    {
        var counts = new Dictionary<TaskFilter, int>(filters.Count);
        foreach (var f in filters) counts[f] = 0;
        foreach (var t in tasks)
            foreach (var f in filters)
                if (Matches(t, f, now, me, rules)) counts[f]++;
        return counts;
    }
}
