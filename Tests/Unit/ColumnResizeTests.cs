using SmartBase.Views.ConstructorDb.DrawnGrid;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Арифметика жеста ручной ширины колонки (<see cref="ColumnResize"/>) — правила нынешней таблицы
/// без контролов: рост уводит соседей вправо, сужение раздает место колонкам справа, колонки
/// слева не трогаются, у последней движимой пол — «таблица ровно заполнена».
/// </summary>
public sealed class ColumnResizeTests
{
    private static readonly string[] Keys = ["a", "b", "c", "d"];

    private static Dictionary<string, double> Base(double w = 200) =>
        Keys.ToDictionary(k => k, _ => w);

    [Fact]
    public void Growing_keeps_neighbours_and_left_columns_untouched()
    {
        var next = ColumnResize.Apply(Keys, Base(), "b", raw: 350, fill: 800);

        Assert.Equal(200, next["a"]);
        Assert.Equal(350, next["b"]);
        Assert.Equal(200, next["c"]);
        Assert.Equal(200, next["d"]);
        Assert.True(next.Values.Sum() > 800);   // таблица шире окна — горизонтальная прокрутка
    }

    [Fact]
    public void Shrinking_gives_surplus_to_right_columns_proportionally()
    {
        var b = Base();
        b["c"] = 100; b["d"] = 300;
        var next = ColumnResize.Apply(Keys, b, "b", raw: 120, fill: 800);

        Assert.Equal(200, next["a"]);
        Assert.Equal(120, next["b"]);
        // Излишек 80 делится 100 : 300.
        Assert.Equal(120, next["c"], 6);
        Assert.Equal(360, next["d"], 6);
        Assert.Equal(800, next.Values.Sum(), 6);   // пустоты справа нет
    }

    [Fact]
    public void Column_never_goes_below_minimum_manual_width()
    {
        var next = ColumnResize.Apply(Keys, Base(), "a", raw: 5, fill: 800);
        Assert.Equal(ColumnLayout.MinManualWidth, next["a"]);
    }

    [Fact]
    public void Last_movable_column_floor_is_the_fill_point()
    {
        var next = ColumnResize.Apply(Keys, Base(), "d", raw: 50, fill: 800);
        Assert.Equal(200, next["d"]);            // 800 − 600 = ровно заполнить окно
        Assert.Equal(200, next["c"]);

        var wider = ColumnResize.Apply(Keys, Base(), "d", raw: 260, fill: 800);
        Assert.Equal(260, wider["d"]);           // расти последней можно
    }

    [Fact]
    public void Last_column_floor_respects_minimum_when_fill_is_small()
    {
        var next = ColumnResize.Apply(Keys, Base(), "d", raw: 10, fill: 500);   // 500 − 600 < 0
        Assert.Equal(ColumnLayout.MinManualWidth, next["d"]);
    }

    [Fact]
    public void Unknown_key_returns_base_unchanged()
    {
        var b = Base();
        var next = ColumnResize.Apply(Keys, b, "zzz", raw: 10, fill: 800);
        Assert.Equal(b, next);
    }

    [Fact]
    public void Returning_to_start_within_one_gesture_restores_base()
    {
        var b = Base();
        b["c"] = 100; b["d"] = 300;
        var moved = ColumnResize.Apply(Keys, b, "b", raw: 120, fill: 800);
        Assert.NotEqual(b["c"], moved["c"]);
        var back = ColumnResize.Apply(Keys, b, "b", raw: 200, fill: 800);
        foreach (var k in Keys) Assert.Equal(b[k], back[k], 6);
    }
}
