using SmartBase.Core.Models;
using SmartBase.Views.Tasks;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Выделение карточек раздела «Задачи» (Views/Tasks/TaskSelection.cs) — то, на что действуют
/// контекстное меню и Delete.
///
/// Правила те же, что в календаре: обычный клик выделяет одну, Shift+клик добавляет/снимает,
/// Ctrl+Shift+клик берёт диапазон от последней выделенной. Список перечитывается на каждый
/// символ поиска и после любой правки, поэтому выделение хранится ключами задач, а не
/// индексами строк, и чистится от того, чего в наборе больше нет.
/// </summary>
public sealed class TaskSelectionTests
{
    private static TaskRecord Task(int id, int dbId = 1, int execFieldId = 7) =>
        new() { Id = id, DbId = dbId, ExecFieldId = execFieldId, Title = $"T{id}" };

    private static List<TaskRecord> Order(params TaskRecord[] tasks) => [.. tasks];

    // ── Одиночный клик и Shift ────────────────────────────────────────────────

    [Fact]
    public void Plain_click_selects_one_and_drops_the_rest()
    {
        var a = Task(1);
        var b = Task(2);
        var sel = new TaskSelection();

        sel.SelectOnly(a);
        sel.SelectOnly(b);

        Assert.False(sel.Contains(a));
        Assert.True(sel.Contains(b));
        Assert.Equal(1, sel.Count);
    }

    [Fact]
    public void Shift_click_adds_and_removes()
    {
        var a = Task(1);
        var b = Task(2);
        var sel = new TaskSelection();

        sel.SelectOnly(a);
        sel.Toggle(b);
        Assert.Equal(2, sel.Count);

        sel.Toggle(b);
        Assert.Equal(1, sel.Count);
        Assert.True(sel.Contains(a));
    }

    // ── Диапазон ──────────────────────────────────────────────────────────────

    [Fact]
    public void Range_covers_everything_between_the_anchor_and_the_click()
    {
        var t = new[] { Task(1), Task(2), Task(3), Task(4) };
        var order = Order(t);
        var sel = new TaskSelection();

        sel.SelectOnly(t[0]);
        Assert.True(sel.SelectRange(order, t[2]));

        Assert.Equal(3, sel.Count);
        Assert.False(sel.Contains(t[3]));
    }

    // Диапазон считается по ПОКАЗАННОМУ порядку, а не по группам: он свободно пересекает
    // границу коллекций — карточки идут подряд на экране, значит и выделяются подряд.
    [Fact]
    public void Range_crosses_collection_boundaries()
    {
        var t = new[] { Task(1, dbId: 1), Task(2, dbId: 1), Task(3, dbId: 2), Task(4, dbId: 2) };
        var sel = new TaskSelection();

        sel.SelectOnly(t[1]);
        sel.SelectRange(Order(t), t[3]);

        Assert.Equal(3, sel.Count);
        Assert.True(sel.Contains(t[2]));
    }

    [Fact]
    public void Range_works_backwards()
    {
        var t = new[] { Task(1), Task(2), Task(3) };
        var sel = new TaskSelection();

        sel.SelectOnly(t[2]);
        sel.SelectRange(Order(t), t[0]);

        Assert.Equal(3, sel.Count);
    }

    // Без якоря (первый клик после загрузки — Ctrl+Shift сразу) диапазон невозможен:
    // вызывающий должен сделать обычное выделение, а не выделить полсписка наугад.
    [Fact]
    public void Range_without_an_anchor_is_refused()
    {
        var t = new[] { Task(1), Task(2) };
        var sel = new TaskSelection();

        Assert.False(sel.SelectRange(Order(t), t[1]));
        Assert.Equal(0, sel.Count);
    }

    // Якорь ушёл из набора (сузили поиск) — диапазон не от чего считать.
    [Fact]
    public void Range_is_refused_when_the_anchor_left_the_list()
    {
        var gone = Task(1);
        var a = Task(2);
        var b = Task(3);
        var sel = new TaskSelection();

        sel.SelectOnly(gone);
        sel.Retain([a, b]);

        Assert.False(sel.SelectRange(Order(a, b), b));
    }

    // ── Перезагрузка списка ───────────────────────────────────────────────────

    [Fact]
    public void Reload_keeps_the_selection_of_surviving_tasks()
    {
        var a = Task(1);
        var b = Task(2);
        var sel = new TaskSelection();

        sel.SelectOnly(a);
        sel.Toggle(b);
        // Тот же набор, но другие экземпляры: список читается из базы заново.
        sel.Retain([Task(1), Task(2)]);

        Assert.Equal(2, sel.Count);
        Assert.True(sel.Contains(a));
    }

    [Fact]
    public void Reload_drops_tasks_that_left_the_set()
    {
        var a = Task(1);
        var b = Task(2);
        var sel = new TaskSelection();

        sel.SelectOnly(a);
        sel.Toggle(b);
        sel.Retain([a]);

        Assert.Equal(1, sel.Count);
        Assert.False(sel.Contains(b));
    }

    // ── Ключ задачи ───────────────────────────────────────────────────────────

    // Одинаковые id в РАЗНЫХ базах — разные задачи (id между базами пересекаются).
    [Fact]
    public void Same_id_in_another_database_is_another_task()
    {
        var first  = Task(1, dbId: 1);
        var second = Task(1, dbId: 2);
        var sel = new TaskSelection();

        sel.SelectOnly(first);

        Assert.False(sel.Contains(second));
    }

    // Запись с двумя полями-исполнителями даёт две карточки — выделяются они порознь.
    [Fact]
    public void Two_executor_fields_of_one_record_select_separately()
    {
        var byFirst  = Task(1, execFieldId: 7);
        var bySecond = Task(1, execFieldId: 9);
        var sel = new TaskSelection();

        sel.SelectOnly(byFirst);

        Assert.False(sel.Contains(bySecond));
    }

    // Несохранённая задача (без Id) ключа не имеет — выделять нечего.
    [Fact]
    public void Task_without_id_is_never_selected()
    {
        var draft = new TaskRecord { Title = "черновик" };
        var sel = new TaskSelection();

        sel.SelectOnly(draft);

        Assert.False(sel.Contains(draft));
        Assert.Equal(0, sel.Count);
    }

    // ── Состав выделения ──────────────────────────────────────────────────────

    // Меню и подтверждение показывают задачи в порядке показа, а не в порядке кликов.
    [Fact]
    public void Selected_tasks_come_back_in_display_order()
    {
        var t = new[] { Task(1), Task(2), Task(3) };
        var sel = new TaskSelection();

        sel.SelectOnly(t[2]);
        sel.Toggle(t[0]);

        Assert.Equal([1, 3], sel.In(Order(t)).Select(x => x.Id));
    }

    [Fact]
    public void Clear_reports_whether_anything_was_selected()
    {
        var sel = new TaskSelection();
        Assert.False(sel.Clear());

        sel.SelectOnly(Task(1));
        Assert.True(sel.Clear());
        Assert.Equal(0, sel.Count);
    }
}
