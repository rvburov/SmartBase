using SmartBase.Views.Tasks;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Что раздел «Задачи» читает перед показом (Views/Tasks/TaskLoadPlan.cs).
///
/// Правило решает, будет раздел быстрым или медленным, — и цена ошибки несимметрична.
/// Прочитать лишнее значит вернуть прежнюю медлительность: десять тысяч строк с
/// коррелированными подзапросами при каждом открытии и каждой правке задачи. НЕ прочитать
/// нужное значит показать пустой список — а это уже не медленно, а сломано. Поэтому граница
/// проверяется поименно, с обеих сторон.
/// </summary>
public sealed class TaskLoadPlanTests
{
    private const int Cap = 10_000;

    // ── Когда карточки нужны ─────────────────────────────────────────────────

    /// <summary>Раздел помещается в память — читаем все, поведение маленьких баз прежнее.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9_999)]
    [InlineData(10_000)]
    public void A_section_that_fits_in_memory_is_always_read(int total)
    {
        Assert.True(TaskLoadPlan.NeedsTaskRead(total, Cap));
    }

    // ── Когда не нужны ───────────────────────────────────────────────────────

    /// <summary>
    /// Раздел не помещается в память — не читаем ВООБЩЕ, каким бы ни был вид: страницами
    /// читается и «Все задачи», и поиск, и каждый из пяти фильтров. Ради этого все и затевалось.
    /// </summary>
    [Fact]
    public void A_section_that_does_not_fit_is_never_read_in_bulk()
    {
        Assert.False(TaskLoadPlan.NeedsTaskRead(10_001, Cap));
        Assert.False(TaskLoadPlan.NeedsTaskRead(520_000, Cap));
    }

    /// <summary>Граница ровно на крышке: до нее включительно читаем, за ней — нет.</summary>
    [Fact]
    public void The_cap_itself_still_counts_as_fitting()
    {
        Assert.True(TaskLoadPlan.NeedsTaskRead(Cap, Cap));
        Assert.False(TaskLoadPlan.NeedsTaskRead(Cap + 1, Cap));
    }

    // ── Набор отмеченных коллекций ───────────────────────────────────────────

    private static Dictionary<int, HashSet<int>> Active(params (int DbId, int[] Ids)[] rows) =>
        rows.ToDictionary(r => r.DbId, r => new HashSet<int>(r.Ids));

    /// <summary>Раздел еще ничего не показывал — читать надо в любом случае.</summary>
    [Fact]
    public void A_missing_current_set_never_counts_as_the_same()
    {
        Assert.False(TaskLoadPlan.SameActiveIds(null, Active((1, [10, 20]))));
    }

    [Fact]
    public void The_same_checkboxes_are_recognised_whatever_the_order()
    {
        Assert.True(TaskLoadPlan.SameActiveIds(
            Active((1, [20, 10]), (2, [5])),
            Active((2, [5]), (1, [10, 20]))));
    }

    [Fact]
    public void A_changed_collection_is_noticed()
    {
        Assert.False(TaskLoadPlan.SameActiveIds(Active((1, [10, 20])), Active((1, [10, 30]))));
        Assert.False(TaskLoadPlan.SameActiveIds(Active((1, [10])),     Active((1, [10, 20]))));
        Assert.False(TaskLoadPlan.SameActiveIds(Active((1, [10, 20])), Active((1, [10]))));
    }

    [Fact]
    public void An_added_or_removed_database_is_noticed()
    {
        Assert.False(TaskLoadPlan.SameActiveIds(Active((1, [10])), Active((1, [10]), (2, [10]))));
        Assert.False(TaskLoadPlan.SameActiveIds(Active((1, [10]), (2, [10])), Active((1, [10]))));
    }

    /// <summary>Пустой набор — «ни одной коллекции», и он тоже сравнивается как значение:
    /// повторная присылка того же не должна перечитывать раздел.</summary>
    [Fact]
    public void Empty_sets_compare_by_value_too()
    {
        Assert.True(TaskLoadPlan.SameActiveIds(Active((1, [])), Active((1, []))));
        Assert.False(TaskLoadPlan.SameActiveIds(Active((1, [])), Active((1, [10]))));
    }

    /// <summary>Другая база при том же числе баз — не тот же набор.</summary>
    [Fact]
    public void A_different_database_id_is_not_the_same_set()
    {
        Assert.False(TaskLoadPlan.SameActiveIds(Active((1, [10])), Active((2, [10]))));
    }
}
