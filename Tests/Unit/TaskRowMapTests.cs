using SmartBase.Views.Tasks;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Раскладка списка задач по строкам (Views/Tasks/TaskRowMap.cs).
///
/// Виртуальный список отвечает на вопрос «что стоит в строке N», не материализуя соседей, —
/// вся его правильность держится на этой арифметике. Ошибка здесь не падает: она молча
/// показывает заголовок вместо карточки или теряет последнюю задачу коллекции. Поэтому границы
/// групп и свернутость проверяются поименно.
/// </summary>
public sealed class TaskRowMapTests
{
    private static TaskRowMap Map(params (int Tasks, bool Collapsed)[] groups) =>
        new([.. groups.Select(g => new TaskRowMap.Group(g.Tasks, g.Collapsed))]);

    // ── Длина списка ─────────────────────────────────────────────────────────

    [Fact]
    public void Empty_map_has_no_rows()
    {
        var map = Map();

        Assert.Equal(0, map.Count);
        Assert.Equal(-1, map.GroupOf(0));
    }

    /// <summary>Заголовок считается строкой: группа из трех задач занимает четыре.</summary>
    [Fact]
    public void A_group_costs_a_header_plus_its_tasks()
    {
        Assert.Equal(4, Map((3, false)).Count);
    }

    /// <summary>Свернутая группа — это ровно заголовок: ее карточек не видно.</summary>
    [Fact]
    public void A_collapsed_group_shrinks_to_its_header()
    {
        Assert.Equal(1, Map((3, true)).Count);
    }

    /// <summary>Группа без единой задачи — один заголовок. Так выглядит коллекция, в которой
    /// текущему виду ничего не подошло.</summary>
    [Fact]
    public void A_group_with_no_tasks_is_just_its_header()
    {
        var map = Map((0, false));

        Assert.Equal(1, map.Count);
        Assert.Equal(TaskRowKind.Header, map.Resolve(0).Kind);
    }

    [Fact]
    public void Groups_are_laid_out_one_after_another()
    {
        var map = Map((2, false), (1, false), (5, true));

        Assert.Equal(new TaskRowSpan(0, 3), map[0]);   // заголовок + 2
        Assert.Equal(new TaskRowSpan(3, 2), map[1]);   // заголовок + 1
        Assert.Equal(new TaskRowSpan(5, 1), map[2]);   // свернута
        Assert.Equal(6, map.Count);
    }

    // ── Что стоит в строке ───────────────────────────────────────────────────

    [Fact]
    public void Rows_resolve_to_a_header_then_its_tasks()
    {
        var map = Map((2, false));

        Assert.Equal((0, TaskRowKind.Header, -1), map.Resolve(0));
        Assert.Equal((0, TaskRowKind.Task,    0), map.Resolve(1));
        Assert.Equal((0, TaskRowKind.Task,    1), map.Resolve(2));
    }

    /// <summary>Последняя строка группы — задача, а не что-то еще. На этой границе и живет
    /// классическая ошибка на единицу.</summary>
    [Fact]
    public void The_last_row_of_a_group_is_a_task()
    {
        var map = Map((2, false));

        Assert.Equal((0, TaskRowKind.Task, 1), map.Resolve(2));
        Assert.Equal(3, map.Count);
    }

    [Fact]
    public void Rows_of_the_second_group_are_numbered_from_its_own_start()
    {
        var map = Map((2, false), (3, false));

        Assert.Equal((1, TaskRowKind.Header, -1), map.Resolve(3));
        Assert.Equal((1, TaskRowKind.Task,    0), map.Resolve(4));
        Assert.Equal((1, TaskRowKind.Task,    2), map.Resolve(6));
    }

    [Fact]
    public void A_collapsed_group_hides_its_tasks_but_keeps_the_header()
    {
        var map = Map((5, true), (1, false));

        Assert.Equal((0, TaskRowKind.Header, -1), map.Resolve(0));
        Assert.Equal((1, TaskRowKind.Header, -1), map.Resolve(1));
        Assert.Equal((1, TaskRowKind.Task,    0), map.Resolve(2));
    }

    // ── Границы ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(1000)]
    public void Rows_outside_the_list_resolve_to_nothing(int row)
    {
        var map = Map((2, false), (2, false));

        Assert.Equal(6, map.Count);
        Assert.Equal(-1, map.GroupOf(row));
        Assert.Equal(-1, map.Resolve(row).Group);
    }

    /// <summary>Двоичный поиск обязан отвечать то же, что и обход подряд, — на каждой строке.</summary>
    [Fact]
    public void Binary_search_agrees_with_a_straight_walk()
    {
        var groups = new (int, bool)[]
        {
            (0, false), (7, false), (3, false), (11, true), (1, false), (250, false),
        };
        var map = Map(groups);

        var expected = new List<int>();
        for (var gi = 0; gi < groups.Length; gi++)
            for (var k = 0; k < map[gi].RowCount; k++) expected.Add(gi);

        Assert.Equal(expected.Count, map.Count);
        for (var row = 0; row < map.Count; row++)
            Assert.Equal(expected[row], map.GroupOf(row));
    }

    /// <summary>Номера карточек внутри группы идут подряд с нуля и покрывают ровно все задачи —
    /// ни одна не пропущена и ни одна не показана дважды.</summary>
    [Fact]
    public void Task_numbers_cover_every_task_exactly_once()
    {
        var map = Map((4, false), (2, false));

        var first  = new List<int>();
        var second = new List<int>();
        for (var row = 0; row < map.Count; row++)
        {
            var (gi, kind, local) = map.Resolve(row);
            if (kind != TaskRowKind.Task) continue;
            (gi == 0 ? first : second).Add(local);
        }

        Assert.Equal([0, 1, 2, 3], first);
        Assert.Equal([0, 1], second);
    }
}
