using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Views.Tasks;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Раскладка раздела «Задачи» по коллекциям (Views/Tasks/TaskGrouping.cs).
///
/// Раздел показывает задачи ВСЕХ отмеченных галочками коллекций, разложенные по группам с
/// заголовком коллекции. Порядок групп обязан повторять сайдбар: пользователь отмечает
/// коллекции сверху вниз и ищет их в списке в том же порядке — иначе «где моя коллекция?»
/// на каждой загрузке. Внутри группы — срок ↑, «без даты» в конец.
/// </summary>
public sealed class TaskGroupingTests
{
    private static TaskRecord Task(int id, int dbId, int colId, string colName,
                                   DateTime? due = null, string status = "") =>
        new()
        {
            Id = id, DbId = dbId, CollectionId = colId, CollectionName = colName,
            Title = $"T{id}", DueStart = due, DueEnd = due, Status = status,
        };

    // Коллекции «как в сайдбаре»: (DbId, ColId) → место в списке, цвет и название. Название
    // берёт виртуальный список: там карточек в памяти нет и спросить имя не у кого.
    private static Dictionary<(int DbId, int ColId), (int Order, string Color, string Name)> Cols(
        params (int DbId, int ColId, int Place)[] rows) =>
        rows.ToDictionary(r => (r.DbId, r.ColId), r => (r.Place, "", ""));

    // ── Порядок групп ─────────────────────────────────────────────────────────

    [Fact]
    public void Groups_follow_the_sidebar_order_not_the_load_order()
    {
        var tasks = new[]
        {
            Task(1, 1, 20, "Вторая"),
            Task(2, 1, 10, "Первая"),
            Task(3, 1, 30, "Третья"),
        };
        var cols = Cols((1, 10, 0), (1, 20, 1), (1, 30, 2));

        var groups = TaskGrouping.Build(tasks, cols);

        Assert.Equal(["Первая", "Вторая", "Третья"], groups.Select(g => g.Title));
    }

    /// <summary>Коллекция пропала между загрузкой и показом — группа уходит в конец, а не теряется.</summary>
    [Fact]
    public void Unknown_collection_goes_last_and_keeps_its_tasks()
    {
        var tasks = new[] { Task(1, 1, 99, "Удалённая"), Task(2, 1, 10, "Живая") };
        var groups = TaskGrouping.Build(tasks, Cols((1, 10, 0)));

        Assert.Equal(["Живая", "Удалённая"], groups.Select(g => g.Title));
        Assert.Single(groups[^1].Tasks);
    }

    [Fact]
    public void Task_without_a_collection_is_titled_explicitly()
    {
        var groups = TaskGrouping.Build([Task(1, 1, 0, "")], Cols());
        Assert.Equal("Без коллекции", Assert.Single(groups).Title);
    }

    /// <summary>Многострочное имя коллекции показывается первой строкой — как везде ([[TitleText]]).</summary>
    [Fact]
    public void Multiline_collection_name_is_shown_as_one_line()
    {
        var groups = TaskGrouping.Build([Task(1, 1, 10, "Проекты\nотдела")], Cols((1, 10, 0)));
        Assert.Equal("Проекты", Assert.Single(groups).Title);
    }

    // ── Порядок задач внутри группы ───────────────────────────────────────────

    [Fact]
    public void Inside_a_group_tasks_go_by_due_date_and_undated_last()
    {
        var day = new DateTime(2026, 8, 15);
        var tasks = new[]
        {
            Task(1, 1, 10, "К", due: null),
            Task(2, 1, 10, "К", due: day.AddDays(2)),
            Task(3, 1, 10, "К", due: day),
            Task(4, 1, 10, "К", due: null),
        };

        var order = Assert.Single(TaskGrouping.Build(tasks, Cols((1, 10, 0)))).Tasks
                          .Select(t => t.Id).ToArray();

        // Сначала по сроку, затем «без даты» — между собой по Id (стабильный порядок).
        Assert.Equal([3, 2, 1, 4], order);
    }

    // ── Ключ группы и подзаголовок ────────────────────────────────────────────

    /// <summary>
    /// Ключ группы — пара (DbId, ColId): по нему список помнит, какие коллекции свёрнуты,
    /// и переживает перезагрузку (поиск, правка задачи). Индекс для этого не годится —
    /// он съезжает, стоит группе появиться или исчезнуть.
    /// </summary>
    [Fact]
    public void Group_carries_its_collection_key()
    {
        var group = Assert.Single(TaskGrouping.Build([Task(1, 7, 10, "К")], Cols((7, 10, 0))));

        Assert.Equal(7, group.DbId);
        Assert.Equal(10, group.ColId);
    }

    /// <summary>Имя базы в подзаголовке — только когда его передали (баз несколько).</summary>
    [Fact]
    public void Database_name_appears_only_when_asked()
    {
        var tasks = new[] { Task(1, 1, 10, "К"), Task(2, 2, 10, "К") };
        var cols = Cols((1, 10, 0), (2, 10, 1));

        Assert.All(TaskGrouping.Build(tasks, cols), g => Assert.Equal("", g.Subtitle));

        var named = TaskGrouping.Build(tasks, cols, dbId => dbId == 1 ? "Основная" : "Вторая");
        Assert.Equal(["Основная", "Вторая"], named.Select(g => g.Subtitle));
    }

    /// <summary>Одна и та же коллекция в разных базах — разные группы (id не уникален между БД).</summary>
    [Fact]
    public void Same_collection_id_in_two_databases_gives_two_groups()
    {
        var tasks = new[] { Task(1, 1, 10, "К"), Task(2, 2, 10, "К") };
        var groups = TaskGrouping.Build(tasks, Cols((1, 10, 0), (2, 10, 1)));

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Tasks));
    }

    // ── Цвет группы ───────────────────────────────────────────────────────────

    /// <summary>
    /// Цветом коллекции заливается индикатор выполненного, поэтому действует общее правило
    /// <see cref="EventColors"/>: дефолтный синий = «без цвета» → серый (как чекбокс в сайдбаре).
    /// </summary>
    [Theory]
    [InlineData("#FF9500", "#FF9500")]
    [InlineData(EventColors.Default, EventColors.Grey)]
    [InlineData("", EventColors.Grey)]
    public void Group_color_follows_the_default_color_rule(string collectionColor, string expected)
    {
        var cols = new Dictionary<(int DbId, int ColId), (int Order, string Color, string Name)>
        {
            [(1, 10)] = (0, collectionColor, "К"),
        };

        var group = Assert.Single(TaskGrouping.Build([Task(1, 1, 10, "К")], cols));
        Assert.Equal(expected, group.Color);
    }

    // ── Индикатор выполненного в заголовке ────────────────────────────────────

    private static TaskGroupHeaderVm Header(params TaskRecord[] tasks) =>
        new(Assert.Single(TaskGrouping.Build(tasks, Cols((1, 10, 0)))), collapsed: false, first: true);

    /// <summary>«2 из 7» — сколько задач коллекции закрыто; выполненные считаются по статусу.</summary>
    [Fact]
    public void Header_counts_done_tasks()
    {
        var done = ConstructorRepository.TaskStatusDone;
        var vm = Header(
            Task(1, 1, 10, "К", status: done),
            Task(2, 1, 10, "К", status: ConstructorRepository.TaskStatusActive),
            Task(3, 1, 10, "К", status: done),
            Task(4, 1, 10, "К"));

        Assert.Equal(2, vm.DoneCount);
        Assert.Equal(4, vm.TotalCount);
        Assert.Equal("2 из 4", vm.ProgressText);
        Assert.Equal(0.5, vm.DoneFraction);
    }

    [Fact]
    public void Header_without_done_tasks_shows_empty_progress()
    {
        var vm = Header(Task(1, 1, 10, "К"), Task(2, 1, 10, "К"));

        Assert.Equal("0 из 2", vm.ProgressText);
        Assert.Equal(0, vm.DoneFraction);
    }

    [Fact]
    public void Header_with_everything_done_is_full()
    {
        var done = ConstructorRepository.TaskStatusDone;
        var vm = Header(Task(1, 1, 10, "К", status: done), Task(2, 1, 10, "К", status: done));

        Assert.Equal("2 из 2", vm.ProgressText);
        Assert.Equal(1, vm.DoneFraction);
    }

    /// <summary>
    /// В виртуальном режиме карточек в памяти нет вовсе: группа приходит с ПУСТЫМ набором задач
    /// и готовыми числами из базы. Заголовок обязан называть их, а не пересчитывать пустоту.
    /// </summary>
    [Fact]
    public void Header_takes_the_numbers_from_the_database_when_it_has_no_tasks()
    {
        var group = new TaskGroup(1, 10, "К", "", "#FF9500", [], Exact: (4218, 900));
        var vm    = new TaskGroupHeaderVm(group, collapsed: false, first: true);

        Assert.Equal(4218, vm.TotalCount);
        Assert.Equal(900, vm.DoneCount);
        Assert.Equal($"{900:N0} из {4218:N0}", vm.ProgressText);
    }
}
