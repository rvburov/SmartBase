using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Views.Tasks;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Быстрые фильтры раздела «Задачи» (Views/Tasks/TaskFilters.cs) — выпадающий список в
/// навбаре со счетчиками («Просрочено 1»).
///
/// Определения обязаны совпадать с тем, что показывает сама карточка: у выполненной задачи
/// чипа просрочки нет, значит и в «Просрочено» она попадать не должна — иначе счетчик
/// спорит со списком, который сам же и открывает.
/// </summary>
public sealed class TaskFiltersTests
{
    private static readonly DateTime Now = new(2026, 8, 16, 12, 0, 0);
    private const string Me    = "Буров Роман";
    private const string Other = "Иванов Иван";

    private static TaskRecord Task(DateTime? start = null, DateTime? end = null,
                                   string executor = Me, string status = "") =>
        new()
        {
            Id = 1, Title = "T", Executor = executor, Status = status,
            DueStart = start, DueEnd = end ?? start,
        };

    private static bool Matches(TaskRecord t, TaskFilterKind kind) =>
        TaskFilters.Matches(t, kind, Now, Me);

    // ── Сегодня ───────────────────────────────────────────────────────────────

    [Fact]
    public void Today_takes_tasks_of_the_current_day_including_spans()
    {
        Assert.True(Matches(Task(Now.Date.AddHours(9)), TaskFilterKind.Today));   // утро сегодня
        Assert.True(Matches(Task(Now.Date.AddHours(20)), TaskFilterKind.Today));  // вечер сегодня
        // Многодневная задача идет через сегодня — она тоже «на сегодня».
        Assert.True(Matches(Task(Now.Date.AddDays(-2), Now.Date.AddDays(2)), TaskFilterKind.Today));

        Assert.False(Matches(Task(Now.Date.AddDays(1)), TaskFilterKind.Today));
        Assert.False(Matches(Task(Now.Date.AddDays(-1)), TaskFilterKind.Today));
        Assert.False(Matches(Task(), TaskFilterKind.Today));                      // без срока
    }

    // ── Просрочено ────────────────────────────────────────────────────────────

    [Fact]
    public void Overdue_takes_expired_but_not_finished_tasks()
    {
        Assert.True(Matches(Task(Now.AddHours(-2)), TaskFilterKind.Overdue));
        Assert.True(Matches(Task(Now.Date.AddDays(-3)), TaskFilterKind.Overdue));

        Assert.False(Matches(Task(Now.AddHours(2)), TaskFilterKind.Overdue));     // срок впереди
        Assert.False(Matches(Task(), TaskFilterKind.Overdue));                    // без срока
    }

    /// <summary>Выполненная задача не просрочена, сколько бы срок ни истек — как и в карточке.</summary>
    [Fact]
    public void Finished_task_is_never_overdue()
    {
        var done = Task(Now.Date.AddDays(-5), status: ConstructorRepository.TaskStatusDone);

        Assert.False(Matches(done, TaskFilterKind.Overdue));
        Assert.True(Matches(done, TaskFilterKind.Done));
    }

    /// <summary>Конец периода решает: начавшаяся вчера задача до конца срока не просрочена.</summary>
    [Fact]
    public void Overdue_is_decided_by_the_end_of_the_span()
    {
        Assert.False(Matches(Task(Now.Date.AddDays(-1), Now.Date.AddDays(1)), TaskFilterKind.Overdue));
        Assert.True(Matches(Task(Now.Date.AddDays(-3), Now.Date.AddDays(-1)), TaskFilterKind.Overdue));
    }

    // ── Остальные пункты ──────────────────────────────────────────────────────

    [Fact]
    public void Assigned_to_me_compares_the_executor_ignoring_case()
    {
        Assert.True(Matches(Task(executor: Me), TaskFilterKind.AssignedToMe));
        Assert.True(Matches(Task(executor: Me.ToUpperInvariant()), TaskFilterKind.AssignedToMe));
        Assert.False(Matches(Task(executor: Other), TaskFilterKind.AssignedToMe));
    }

    [Fact]
    public void No_due_takes_only_tasks_without_a_date()
    {
        Assert.True(Matches(Task(), TaskFilterKind.NoDue));
        Assert.False(Matches(Task(Now), TaskFilterKind.NoDue));
    }

    [Fact]
    public void All_takes_everything()
    {
        Assert.True(Matches(Task(), TaskFilterKind.All));
        Assert.True(Matches(Task(Now.AddDays(-9), status: ConstructorRepository.TaskStatusDone), TaskFilterKind.All));
    }

    // ── Счетчики ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Одна задача попадает сразу в несколько пунктов (например, моя просроченная — это и
    /// «Мне назначено», и «Просрочено»): счетчики независимы, а не разбивают набор на части.
    /// </summary>
    [Fact]
    public void Counts_cover_every_kind_independently()
    {
        var tasks = new[]
        {
            Task(Now.Date.AddHours(20)),                                            // сегодня вечером, моя
            Task(Now.Date.AddDays(-2), executor: Other),                            // просрочено, чужая
            Task(),                                                                 // без срока, моя
            Task(Now.Date.AddDays(-4), status: ConstructorRepository.TaskStatusDone),  // выполнена, моя
        };

        var counts = TaskFilters.Counts(tasks, Now, Me);

        Assert.Equal(4, counts[TaskFilterKind.All]);
        Assert.Equal(1, counts[TaskFilterKind.Today]);
        Assert.Equal(3, counts[TaskFilterKind.AssignedToMe]);
        Assert.Equal(1, counts[TaskFilterKind.NoDue]);
        Assert.Equal(1, counts[TaskFilterKind.Overdue]);
        Assert.Equal(1, counts[TaskFilterKind.Done]);
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

        Assert.True(Matches(task, TaskFilterKind.Today));
        Assert.True(Matches(task, TaskFilterKind.Overdue));
    }

    /// <summary>Пустой набор дает нули по всем пунктам, а не отсутствующие ключи.</summary>
    [Fact]
    public void Counts_of_an_empty_set_are_zeroes()
    {
        var counts = TaskFilters.Counts([], Now, Me);

        Assert.Equal(TaskFilters.All.Length, counts.Count);
        Assert.All(counts.Values, n => Assert.Equal(0, n));
    }

    /// <summary>У каждого пункта есть подпись и иконка из общего реестра — пустых не бывает.</summary>
    [Fact]
    public void Every_kind_has_a_label_and_an_icon()
    {
        Assert.All(TaskFilters.All, k =>
        {
            Assert.NotEqual("", TaskFilters.Label(k));
            Assert.StartsWith("Icon", TaskFilters.IconKey(k));
        });
    }
}
