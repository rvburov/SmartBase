using SmartBase.Core.Models;
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
    private static TaskRecord Task(int id, int dbId, int colId, string colName, DateTime? due = null) =>
        new()
        {
            Id = id, DbId = dbId, CollectionId = colId, CollectionName = colName,
            Title = $"T{id}", DueStart = due, DueEnd = due,
        };

    // Порядок коллекций «как в сайдбаре»: (DbId, ColId) → место.
    private static Dictionary<(int DbId, int ColId), int> Order(
        params (int DbId, int ColId, int Place)[] rows) =>
        rows.ToDictionary(r => (r.DbId, r.ColId), r => r.Place);

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
        var order = Order((1, 10, 0), (1, 20, 1), (1, 30, 2));

        var groups = TaskGrouping.Build(tasks, order);

        Assert.Equal(["Первая", "Вторая", "Третья"], groups.Select(g => g.Title));
    }

    /// <summary>Коллекция пропала между загрузкой и показом — группа уходит в конец, а не теряется.</summary>
    [Fact]
    public void Unknown_collection_goes_last_and_keeps_its_tasks()
    {
        var tasks = new[] { Task(1, 1, 99, "Удалённая"), Task(2, 1, 10, "Живая") };
        var groups = TaskGrouping.Build(tasks, Order((1, 10, 0)));

        Assert.Equal(["Живая", "Удалённая"], groups.Select(g => g.Title));
        Assert.Single(groups[^1].Tasks);
    }

    [Fact]
    public void Task_without_a_collection_is_titled_explicitly()
    {
        var groups = TaskGrouping.Build([Task(1, 1, 0, "")], Order());
        Assert.Equal("Без коллекции", Assert.Single(groups).Title);
    }

    /// <summary>Многострочное имя коллекции показывается первой строкой — как везде ([[TitleText]]).</summary>
    [Fact]
    public void Multiline_collection_name_is_shown_as_one_line()
    {
        var groups = TaskGrouping.Build([Task(1, 1, 10, "Проекты\nотдела")], Order((1, 10, 0)));
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

        var order = Assert.Single(TaskGrouping.Build(tasks, Order((1, 10, 0)))).Tasks
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
        var group = Assert.Single(TaskGrouping.Build([Task(1, 7, 10, "К")], Order((7, 10, 0))));

        Assert.Equal(7, group.DbId);
        Assert.Equal(10, group.ColId);
    }

    /// <summary>Имя базы в подзаголовке — только когда его передали (баз несколько).</summary>
    [Fact]
    public void Database_name_appears_only_when_asked()
    {
        var tasks = new[] { Task(1, 1, 10, "К"), Task(2, 2, 10, "К") };
        var order = Order((1, 10, 0), (2, 10, 1));

        Assert.All(TaskGrouping.Build(tasks, order), g => Assert.Equal("", g.Subtitle));

        var named = TaskGrouping.Build(tasks, order, dbId => dbId == 1 ? "Основная" : "Вторая");
        Assert.Equal(["Основная", "Вторая"], named.Select(g => g.Subtitle));
    }

    /// <summary>Одна и та же коллекция в разных базах — разные группы (id не уникален между БД).</summary>
    [Fact]
    public void Same_collection_id_in_two_databases_gives_two_groups()
    {
        var tasks = new[] { Task(1, 1, 10, "К"), Task(2, 2, 10, "К") };
        var groups = TaskGrouping.Build(tasks, Order((1, 10, 0), (2, 10, 1)));

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Tasks));
    }
}
