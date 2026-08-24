using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Навигация нижней панели таблицы стоит на internal-членах Avalonia DataGrid (версия
/// зафиксирована 11.2.x): видимые строки берутся из <c>DisplayData</c>, прокрутка идет
/// через <c>VerticalScrollBar</c> + <c>ProcessVerticalScroll</c>, а брошенные в дереве
/// строки отсеиваются по <c>IsRecycled</c>. Рефлексия отказывает МОЛЧА — кнопки перестают
/// нажиматься, индикатор начинает врать, — поэтому имена и типы проверяются тестом: после
/// апгрейда Avalonia он падает раньше, чем это увидит пользователь.
///
/// Разбор: docs/developer/features/constructor/table.md, «Навигация нижней панели».
/// </summary>
public sealed class DataGridInternalsTests
{
    // Public тоже нужен: у internal-класса DataGridDisplayData свойства объявлены public,
    // и по одному NonPublic рефлексия их НЕ находит (на этом тест и поймал первую версию).
    private const BindingFlags Hidden =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void Grid_exposes_display_data()
    {
        Assert.NotNull(typeof(DataGrid).GetProperty("DisplayData", Hidden));
    }

    [Theory]
    [InlineData("FirstScrollingSlot")]
    [InlineData("LastScrollingSlot")]
    public void Display_data_exposes_scrolling_slots(string name)
    {
        var displayData = typeof(DataGrid).GetProperty("DisplayData", Hidden)?.PropertyType;
        Assert.NotNull(displayData);

        var slot = displayData!.GetProperty(name, Hidden);

        Assert.NotNull(slot);
        Assert.Equal(typeof(int), slot!.PropertyType);
    }

    [Fact]
    public void Row_exposes_recycled_flag()
    {
        var recycled = typeof(DataGridRow).GetProperty("IsRecycled", Hidden);

        Assert.NotNull(recycled);
        Assert.Equal(typeof(bool), recycled!.PropertyType);
    }

    [Fact]
    public void Grid_exposes_vertical_scroll_api()
    {
        var bar = typeof(DataGrid).GetProperty("VerticalScrollBar", Hidden);

        Assert.NotNull(bar);
        Assert.Equal(typeof(ScrollBar), bar!.PropertyType);
        Assert.NotNull(typeof(DataGrid).GetMethod("ProcessVerticalScroll", Hidden));
    }
}
