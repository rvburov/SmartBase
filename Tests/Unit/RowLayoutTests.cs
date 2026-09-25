using SmartBase.Views.ConstructorDb.DrawnGrid;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Раскладка строк рисованной сетки (<see cref="RowLayout"/>): положение и шаг строк при
/// стандартной высоте и с разреженными ручными высотами, попадание по координате, видимый
/// диапазон. Чистая арифметика — индексы, а не записи.
/// </summary>
public sealed class RowLayoutTests
{
    [Fact]
    public void Uniform_layout_is_plain_multiplication()
    {
        var l = RowLayout.Uniform(37);

        Assert.False(l.HasManual);
        Assert.Equal(0,      l.Top(0));
        Assert.Equal(37 * 5, l.Top(5));
        Assert.Equal(37,     l.Height(123));
        Assert.Equal(37 * 1_000_000, l.TotalHeight(1_000_000));
        Assert.Equal(3, l.IndexAt(37 * 3 + 36.9, 10));
        Assert.Equal(-1, l.IndexAt(-1, 10));
        Assert.Equal(-1, l.IndexAt(370, 10));
        Assert.Equal(9, l.IndexAt(369.99, 10));
    }

    [Fact]
    public void Manual_heights_shift_rows_below_and_only_them()
    {
        // Строки 2 (высота 100) и 5 (высота 60) при стандартных 36 + линия 1.
        var l = RowLayout.Build(37, 1, [new(2, 100), new(5, 60)]);

        Assert.True(l.HasManual);
        Assert.Equal(37,  l.Height(0));
        Assert.Equal(101, l.Height(2));
        Assert.Equal(61,  l.Height(5));
        Assert.Equal(37 * 2,             l.Top(2));           // выше ручных — как в стандарте
        Assert.Equal(37 * 3 + (101 - 37), l.Top(3));          // ниже второй — сдвиг на ее отклонение
        Assert.Equal(37 * 6 + 64 + 24,    l.Top(6));          // ниже обеих
        Assert.Equal(l.Top(6) + 37 * 4,   l.TotalHeight(10));
        Assert.Equal(l.Top(5) + 61,       l.Bottom(5));
    }

    [Fact]
    public void IndexAt_with_manual_heights_uses_binary_search_over_positions()
    {
        var l = RowLayout.Build(37, 1, [new(2, 100), new(5, 60)]);

        Assert.Equal(2, l.IndexAt(l.Top(2), 10));
        Assert.Equal(2, l.IndexAt(l.Top(2) + 100.9, 10));
        Assert.Equal(3, l.IndexAt(l.Top(3), 10));
        Assert.Equal(5, l.IndexAt(l.Top(5) + 30, 10));
        Assert.Equal(9, l.IndexAt(l.TotalHeight(10) - 0.01, 10));
        Assert.Equal(-1, l.IndexAt(l.TotalHeight(10), 10));
        for (var i = 0; i < 10; i++)
            Assert.Equal(i, l.IndexAt(l.Top(i) + l.Height(i) / 2, 10));
    }

    [Fact]
    public void VisibleRange_covers_partially_visible_rows_and_clamps_at_edges()
    {
        var l = RowLayout.Build(37, 1, [new(2, 100)]);

        Assert.Equal((0, 2), l.VisibleRange(0, 100, 50));           // 37 + 37 + начало третьей
        Assert.Equal((2, 3), l.VisibleRange(l.Top(2) + 10, 100, 50)); // хвост высокой и следующая
        Assert.Equal((0, -1), l.VisibleRange(0, 0, 50));
        Assert.Equal((0, -1), l.VisibleRange(0, 100, 0));
        var total = l.TotalHeight(50);
        Assert.Equal((49, 49), l.VisibleRange(total - 1, 100, 50));  // окно за концом — последняя строка
        Assert.Equal(49, l.VisibleRange(total - 500, 10_000, 50).Last);
    }

    [Fact]
    public void Build_ignores_heights_not_above_standard_and_negative_indexes()
    {
        var l = RowLayout.Build(37, 1, [new(1, 36), new(-3, 200), new(4, 20)]);
        Assert.False(l.HasManual);
        Assert.Equal(37 * 7, l.Top(7));

        var last = RowLayout.Build(37, 1, [new(1, 50), new(1, 80)]);   // повтор — последнее значение
        Assert.Equal(81, last.Height(1));
    }
}
