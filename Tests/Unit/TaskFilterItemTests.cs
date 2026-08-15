using System.ComponentModel;
using SmartBase.Controls.Components;
using SmartBase.Views.Tasks;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Пункт быстрого фильтра задач (<see cref="TaskFilterItem"/>) — модель строки выпадающего
/// списка в навбаре.
///
/// Счётчик обязан быть наблюдаемым: пункты создаются один раз и живут всё время работы
/// панели, а числа меняются на каждую загрузку списка. Пересоборка коллекции вместо
/// уведомлений уже приводила к дефекту — список показывал верные числа, а свёрнутый бокс
/// застревал на промежуточном состоянии («0» у «Всех задач» после возврата с пустого
/// фильтра), потому что коллекция менялась под руками ComboBox прямо в обработке клика.
///
/// Кисти (<c>CountBg</c>, <c>LabelBrush</c>, …) здесь не трогаем: они лезут в ресурсы
/// приложения, которых в чистом юнит-тесте нет.
/// </summary>
public sealed class TaskFilterItemTests
{
    private static List<string> Track(TaskFilterItem item)
    {
        var seen = new List<string>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");
        return seen;
    }

    [Fact]
    public void Changing_the_count_notifies_everything_shown_in_the_row()
    {
        var item = new TaskFilterItem(TaskFilterKind.All, 0);
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
        var item = new TaskFilterItem(TaskFilterKind.All, 14);

        item.Count = 0;
        Assert.False(item.HasCount);

        item.Count = 14;
        Assert.True(item.HasCount);
        Assert.Equal("14", item.CountText);
    }

    [Fact]
    public void Empty_item_has_no_pill()
    {
        var item = new TaskFilterItem(TaskFilterKind.Today, 0);

        Assert.False(item.HasCount);
        Assert.Equal("0", item.CountText);   // текст есть, но пилюля скрыта по HasCount
    }

    [Fact]
    public void Large_counts_get_a_thousands_separator()
    {
        var item = new TaskFilterItem(TaskFilterKind.Done, 1234);

        Assert.Contains("1", item.CountText);
        Assert.NotEqual("1234", item.CountText);
    }

    [Fact]
    public void Kind_and_label_come_from_the_shared_filter_table()
    {
        var item = new TaskFilterItem(TaskFilterKind.Overdue, 2);

        Assert.Equal(TaskFilterKind.Overdue, item.Kind);
        Assert.Equal(TaskFilters.Label(TaskFilterKind.Overdue), item.Label);
    }
}
