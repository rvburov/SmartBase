using System.ComponentModel;
using SmartBase.Controls.Components;
using SmartBase.Database.Repositories;
using SmartBase.Views.Tasks;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Пункт быстрого фильтра задач (<see cref="TaskFilterItem"/>) — модель строки выпадающего
/// списка в навбаре.
///
/// Счетчик обязан быть наблюдаемым: пункты создаются один раз и живут все время работы
/// панели, а числа меняются на каждую загрузку списка. Пересоборка коллекции вместо
/// уведомлений уже приводила к дефекту — список показывал верные числа, а свернутый бокс
/// застревал на промежуточном состоянии («0» у «Всех задач» после возврата с пустого
/// фильтра), потому что коллекция менялась под руками ComboBox прямо в обработке клика.
///
/// Кисти (<c>CountBg</c>, <c>LabelBrush</c>, …) здесь не трогаем: они лезут в ресурсы
/// приложения, которых в чистом юнит-тесте нет.
/// </summary>
public sealed class TaskFilterItemTests
{
    private static readonly TaskFilter Today = TaskFilter.Of(TaskFilterKind.Today);
    private static readonly TaskFilter Done  = TaskFilter.ForStage(ConstructorRepository.TaskStatusDone);

    private static List<string> Track(TaskFilterItem item)
    {
        var seen = new List<string>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");
        return seen;
    }

    [Fact]
    public void Changing_the_count_notifies_everything_shown_in_the_row()
    {
        var item = new TaskFilterItem(TaskFilter.All, 0);
        var seen = Track(item);

        item.Count = 14;

        Assert.Contains(nameof(TaskFilterItem.CountText), seen);
        Assert.Contains(nameof(TaskFilterItem.HasCount), seen);
        Assert.Equal("14", item.CountText);
        Assert.True(item.HasCount);
    }

    // Тот самый сценарий: вернулись с пустого фильтра — число обязано ожить.
    [Fact]
    public void Count_survives_a_round_trip_through_zero()
    {
        var item = new TaskFilterItem(TaskFilter.All, 14);

        item.Count = 0;
        Assert.False(item.HasCount);

        item.Count = 14;
        Assert.True(item.HasCount);
        Assert.Equal("14", item.CountText);
    }

    [Fact]
    public void Empty_item_has_no_pill()
    {
        var item = new TaskFilterItem(Today, 0);

        Assert.False(item.HasCount);
        Assert.Equal("0", item.CountText);   // текст есть, но пилюля скрыта по HasCount
    }

    // Пилюля живет в свернутом боксе навбара: точное четырехзначное число там только распирает
    // строку. Ровно на потолке счетчик еще точный — «+» появляется только когда есть что скрывать.
    [Fact]
    public void Counts_up_to_the_cap_stay_exact()
    {
        Assert.Equal("998", new TaskFilterItem(Done, TaskFilterItem.CountCap - 1).CountText);
        Assert.Equal("999", new TaskFilterItem(Done, TaskFilterItem.CountCap).CountText);
    }

    [Fact]
    public void Counts_above_the_cap_collapse_to_999_plus()
    {
        Assert.Equal("999+", new TaskFilterItem(Done, TaskFilterItem.CountCap + 1).CountText);
        Assert.Equal("999+", new TaskFilterItem(TaskFilter.All, 1_234_567).CountText);
    }

    // Потолок обязан доезжать до привязки: пункты живут все время работы панели, и переход
    // через границу меняет только текст — уведомление о нем то же, что у обычной смены числа.
    [Fact]
    public void Crossing_the_cap_notifies_the_binding()
    {
        var item = new TaskFilterItem(TaskFilter.All, TaskFilterItem.CountCap);
        var seen = Track(item);

        item.Count = TaskFilterItem.CountCap + 1;

        Assert.Contains(nameof(TaskFilterItem.CountText), seen);
        Assert.Equal("999+", item.CountText);
    }

    [Fact]
    public void Kind_and_label_come_from_the_shared_filter_table()
    {
        var overdue = TaskFilter.Of(TaskFilterKind.Overdue);
        var item    = new TaskFilterItem(overdue, 2);

        Assert.Equal(TaskFilterKind.Overdue, item.Kind);
        Assert.Equal(overdue, item.Filter);
        Assert.Equal(TaskFilters.Label(overdue), item.Label);
    }

    /// <summary>Пункт стадии называется ее именем; черта над группой стадий — только у первого.</summary>
    [Fact]
    public void Stage_item_is_named_after_the_stage_and_only_the_first_starts_the_group()
    {
        var first  = new TaskFilterItem(TaskFilter.ForStage("Запланировано"), 3, startsGroup: true);
        var second = new TaskFilterItem(TaskFilter.ForStage("Проверка"), 0);

        Assert.Equal(TaskFilterKind.Stage, first.Kind);
        Assert.Equal("Запланировано", first.Label);
        Assert.True(first.StartsGroup);

        Assert.Equal("Проверка", second.Label);
        Assert.False(second.StartsGroup);
        Assert.False(new TaskFilterItem(TaskFilter.All, 0).StartsGroup);
    }
}
