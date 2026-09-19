using SmartBase.Controls.Components;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Форма боковой панели по режиму (<see cref="SidebarModes.HasChecks"/>).
///
/// Панель бывает ровно двух видов. В конструкторе коллекцию ОТКРЫВАЮТ: клик по строке
/// показывает таблицу, галочек нет. В разделах-агрегатах (календарь, задачи, библиотека)
/// коллекцию ОТМЕЧАЮТ: галочка решает, отдает ли коллекция записи в общий список, клик по
/// строке ее переключает, снятые помнятся между запусками.
///
/// Тест закрывает не «правильность списка режимов», а разъезд: от этого правила зависят
/// видимость галочки, отступ под нее и ветка клика, и пока каждое место перечисляло режимы
/// само, библиотека приехала с галочками в логике и без них на экране.
/// </summary>
public sealed class SidebarModeTests
{
    [Theory]
    [InlineData(SidebarMode.Calendar)]
    [InlineData(SidebarMode.Tasks)]
    [InlineData(SidebarMode.Library)]
    public void Aggregate_sections_have_collection_checkboxes(SidebarMode mode) =>
        Assert.True(mode.HasChecks());

    [Fact]
    public void The_constructor_has_none() =>
        Assert.False(SidebarMode.Constructor.HasChecks());

    // Третьей формы панели нет: новый режим — либо конструктор, либо раздел-агрегат с
    // галочками. Тест перебирает перечисление целиком, поэтому добавленный режим, забытый в
    // HasChecks, падает здесь, а не всплывает потом пустым местом на экране.
    [Fact]
    public void Every_mode_is_either_the_constructor_or_an_aggregate_with_checks()
    {
        foreach (var mode in Enum.GetValues<SidebarMode>())
            Assert.True(mode == SidebarMode.Constructor || mode.HasChecks(),
                        $"Режим {mode} не попал ни в конструктор, ни в разделы с галочками");
    }
}
