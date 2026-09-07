using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Views.Tasks;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Быстрые фильтры раздела «Задачи» (Views/Tasks/TaskFilters.cs) — выпадающий список в
/// навбаре со счетчиками («Просрочено 1»): стандартные пункты и стадии.
///
/// Определения обязаны совпадать с тем, что показывает сама карточка: у выполненной задачи
/// чипа просрочки нет, значит и в «Просрочено» она попадать не должна — иначе счетчик
/// спорит со списком, который сам же и открывает. Момент просрочки и показ выполненных
/// задают правила из настроек раздела (<see cref="TaskRules"/>).
/// </summary>
public sealed class TaskFiltersTests
{
    private static readonly DateTime Now = new(2026, 8, 16, 12, 0, 0);
    private const string Me    = "Буров Роман";
    private const string Other = "Иванов Иван";

    private static readonly TaskFilter Today   = TaskFilter.Of(TaskFilterKind.Today);
    private static readonly TaskFilter Overdue = TaskFilter.Of(TaskFilterKind.Overdue);
    private static readonly TaskFilter Mine    = TaskFilter.Of(TaskFilterKind.AssignedToMe);
    private static readonly TaskFilter NoDue   = TaskFilter.Of(TaskFilterKind.NoDue);
    private static readonly TaskFilter Done    = TaskFilter.ForStage(ConstructorRepository.TaskStatusDone);
    private static readonly TaskFilter Planned = TaskFilter.ForStage(ConstructorRepository.TaskStatusPlanned);
    private static readonly TaskFilter Active  = TaskFilter.ForStage(ConstructorRepository.TaskStatusActive);

    private static TaskRecord Task(DateTime? start = null, DateTime? end = null,
                                   string executor = Me, string status = "") =>
        new()
        {
            Id = 1, Title = "T", Executor = executor, Status = status,
            DueStart = start, DueEnd = end ?? start,
        };

    private static bool Matches(TaskRecord t, TaskFilter f, TaskRules? rules = null) =>
        TaskFilters.Matches(t, f, Now, Me, rules ?? TaskRules.Default);

    // ── Сегодня ───────────────────────────────────────────────────────────────

    [Fact]
    public void Today_takes_tasks_of_the_current_day_including_spans()
    {
        Assert.True(Matches(Task(Now.Date.AddHours(9)), Today));   // утро сегодня
        Assert.True(Matches(Task(Now.Date.AddHours(20)), Today));  // вечер сегодня
        // Многодневная задача идет через сегодня — она тоже «на сегодня».
        Assert.True(Matches(Task(Now.Date.AddDays(-2), Now.Date.AddDays(2)), Today));

        Assert.False(Matches(Task(Now.Date.AddDays(1)), Today));
        Assert.False(Matches(Task(Now.Date.AddDays(-1)), Today));
        Assert.False(Matches(Task(), Today));                      // без срока
    }

    // ── Просрочено ────────────────────────────────────────────────────────────

    [Fact]
    public void Overdue_takes_expired_but_not_finished_tasks()
    {
        Assert.True(Matches(Task(Now.AddHours(-2)), Overdue));
        Assert.True(Matches(Task(Now.Date.AddDays(-3)), Overdue));

        Assert.False(Matches(Task(Now.AddHours(2)), Overdue));     // срок впереди
        Assert.False(Matches(Task(), Overdue));                    // без срока
    }

    /// <summary>Выполненная задача не просрочена, сколько бы срок ни истек — как и в карточке.</summary>
    [Fact]
    public void Finished_task_is_never_overdue()
    {
        var done = Task(Now.Date.AddDays(-5), status: ConstructorRepository.TaskStatusDone);

        Assert.False(Matches(done, Overdue));
        Assert.True(Matches(done, Done));
    }

    /// <summary>Конец периода решает: начавшаяся вчера задача до конца срока не просрочена.</summary>
    [Fact]
    public void Overdue_is_decided_by_the_end_of_the_span()
    {
        Assert.False(Matches(Task(Now.Date.AddDays(-1), Now.Date.AddDays(1)), Overdue));
        Assert.True(Matches(Task(Now.Date.AddDays(-3), Now.Date.AddDays(-1)), Overdue));
    }

    /// <summary>
    /// Настройка «просрочка со следующего дня»: задача, чье время сегодня уже прошло, до конца
    /// дня не просрочена; вчерашняя — просрочена. Со стандартным правилом обе просрочены.
    /// </summary>
    [Fact]
    public void Next_day_overdue_waits_for_the_end_of_the_due_day()
    {
        var nextDay = TaskRules.Default with { OverdueNextDay = true };
        var earlier = Task(Now.Date.AddHours(9));
        var yesterday = Task(Now.Date.AddDays(-1).AddHours(23));

        Assert.True(Matches(earlier, Overdue));
        Assert.False(Matches(earlier, Overdue, nextDay));
        Assert.True(Matches(yesterday, Overdue, nextDay));

        Assert.False(TaskFilters.IsOverdue(Task(), Now, nextDay));
        Assert.False(TaskFilters.IsOverdue(Task(Now.Date.AddDays(-9), status: ConstructorRepository.TaskStatusDone),
                                           Now, nextDay));
    }

    // ── Стадии ────────────────────────────────────────────────────────────────

    /// <summary>Стадия — точное значение поля «Статус»; пустой статус карточка показывает как
    /// «Запланировано», значит и стадия «Запланировано» берет его.</summary>
    [Fact]
    public void Stage_matches_the_exact_status_and_planned_also_takes_the_empty_one()
    {
        Assert.True(Matches(Task(status: ConstructorRepository.TaskStatusActive), Active));
        Assert.False(Matches(Task(status: ConstructorRepository.TaskStatusActive), Planned));

        Assert.True(Matches(Task(status: ""), Planned));
        Assert.True(Matches(Task(status: ConstructorRepository.TaskStatusPlanned), Planned));
        Assert.False(Matches(Task(status: ""), Active));

        var custom = TaskFilter.ForStage("Проверка");
        Assert.True(Matches(Task(status: "Проверка"), custom));
        Assert.False(Matches(Task(status: "проверка"), custom));   // регистр — часть значения
    }

    /// <summary>Скрытые выполненные исчезают из всех пунктов, кроме стадии «Выполнено».</summary>
    [Fact]
    public void Hidden_done_tasks_disappear_from_every_filter_but_the_done_stage()
    {
        var hide = TaskRules.Default with { ShowDone = false };
        var done = Task(Now.Date.AddHours(9), status: ConstructorRepository.TaskStatusDone);

        Assert.True(Matches(done, TaskFilter.All));
        Assert.False(Matches(done, TaskFilter.All, hide));
        Assert.False(Matches(done, Today, hide));
        Assert.False(Matches(done, Mine, hide));
        Assert.True(Matches(done, Done, hide));

        // Незакрытые задачи скрытие не трогает.
        Assert.True(Matches(Task(Now.Date.AddHours(9)), Today, hide));
    }

    // ── Остальные пункты ──────────────────────────────────────────────────────

    [Fact]
    public void Assigned_to_me_compares_the_executor_ignoring_case()
    {
        Assert.True(Matches(Task(executor: Me), Mine));
        Assert.True(Matches(Task(executor: Me.ToUpperInvariant()), Mine));
        Assert.False(Matches(Task(executor: Other), Mine));
    }

    [Fact]
    public void No_due_takes_only_tasks_without_a_date()
    {
        Assert.True(Matches(Task(), NoDue));
        Assert.False(Matches(Task(Now), NoDue));
    }

    [Fact]
    public void All_takes_everything()
    {
        Assert.True(Matches(Task(), TaskFilter.All));
        Assert.True(Matches(Task(Now.AddDays(-9), status: ConstructorRepository.TaskStatusDone), TaskFilter.All));
    }

    // ── Ключи ─────────────────────────────────────────────────────────────────

    /// <summary>Ключ пункта уходит в настройки (стартовый фильтр) и в словари репозитория —
    /// обратный разбор обязан вернуть тот же пункт, а мусор — ничего.</summary>
    [Fact]
    public void Key_round_trips_for_standard_and_stage_filters()
    {
        foreach (var f in TaskFilters.WithStages(["В работе", "Проверка"]))
            Assert.Equal(f, TaskFilter.ParseKey(f.Key));

        Assert.Equal("Stage:В работе", Active.Key);
        Assert.Equal("Overdue", Overdue.Key);

        Assert.Null(TaskFilter.ParseKey(null));
        Assert.Null(TaskFilter.ParseKey(""));
        Assert.Null(TaskFilter.ParseKey("Stage:"));
        Assert.Null(TaskFilter.ParseKey("Stage"));     // вид без имени стадии — не пункт
        Assert.Null(TaskFilter.ParseKey("Done"));      // прежний пункт стал стадией
        Assert.Null(TaskFilter.ParseKey("мусор"));
    }

    // ── Счетчики ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Одна задача попадает сразу в несколько пунктов (например, моя просроченная — это и
    /// «Мне назначено», и «Просрочено»): счетчики независимы, а не разбивают набор на части.
    /// </summary>
    [Fact]
    public void Counts_cover_every_filter_independently()
    {
        var tasks = new[]
        {
            Task(Now.Date.AddHours(20)),                                            // сегодня вечером, моя
            Task(Now.Date.AddDays(-2), executor: Other),                            // просрочено, чужая
            Task(),                                                                 // без срока, моя
            Task(Now.Date.AddDays(-4), status: ConstructorRepository.TaskStatusDone),  // выполнена, моя
            Task(status: ConstructorRepository.TaskStatusActive, executor: Other),  // в работе, чужая
        };
        var filters = TaskFilters.WithStages(ConstructorRepository.TaskStatusOptions);

        var counts = TaskFilters.Counts(tasks, filters, Now, Me, TaskRules.Default);

        Assert.Equal(5, counts[TaskFilter.All]);
        Assert.Equal(1, counts[Today]);
        Assert.Equal(3, counts[Mine]);
        Assert.Equal(2, counts[NoDue]);
        Assert.Equal(1, counts[Overdue]);
        Assert.Equal(3, counts[Planned]);   // три без статуса — «Запланировано»
        Assert.Equal(1, counts[Active]);
        Assert.Equal(1, counts[Done]);
    }

    /// <summary>
    /// Сегодняшняя задача, чье время уже прошло, считается и «Сегодня», и «Просрочено» —
    /// ровно как ее собственный чип в карточке («Просрочено» появляется по истечении срока,
    /// а не с началом следующего дня).
    /// </summary>
    [Fact]
    public void Task_due_earlier_today_is_both_today_and_overdue()
    {
        var task = Task(Now.Date.AddHours(9));

        Assert.True(Matches(task, Today));
        Assert.True(Matches(task, Overdue));
    }

    /// <summary>Пустой набор дает нули по всем пунктам, а не отсутствующие ключи.</summary>
    [Fact]
    public void Counts_of_an_empty_set_are_zeroes()
    {
        var filters = TaskFilters.WithStages(["Проверка"]);
        var counts  = TaskFilters.Counts([], filters, Now, Me, TaskRules.Default);

        Assert.Equal(filters.Count, counts.Count);
        Assert.All(counts.Values, n => Assert.Equal(0, n));
    }

    /// <summary>У каждого пункта есть подпись и иконка из общего реестра — пустых не бывает;
    /// у трех стандартных стадий свои иконки, у остальных часы.</summary>
    [Fact]
    public void Every_filter_has_a_label_and_an_icon()
    {
        Assert.All(TaskFilters.WithStages([.. ConstructorRepository.TaskStatusOptions, "Проверка"]), f =>
        {
            Assert.NotEqual("", f.Label);
            Assert.StartsWith("Icon", f.IconKey);
        });

        Assert.Equal("IconCircleDashed", Planned.IconKey);
        Assert.Equal("IconPlay",         Active.IconKey);
        Assert.Equal("IconCheckCheck",   Done.IconKey);
        Assert.Equal("IconClock",        TaskFilter.ForStage("Проверка").IconKey);
        Assert.Equal("Проверка",         TaskFilter.ForStage("Проверка").Label);
    }
}
