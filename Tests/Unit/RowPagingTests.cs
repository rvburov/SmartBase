using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Арифметика страниц виртуального списка строк (<see cref="RowPaging"/>): бюджет кеша от окна
/// колонок, недостающие поля страницы, упреждающая страница, разбиение сортированного вида на
/// голову и хвост. Чистые функции — здесь, поведение с базой — в интеграционных тестах.
/// </summary>
public sealed class RowPagingTests
{
    [Fact]
    public void MaxPages_depends_on_window_not_on_collection_width()
    {
        Assert.Equal(50, RowPaging.MaxPages(20));      // узкая таблица — прежний потолок
        Assert.Equal(50, RowPaging.MaxPages(1));
        Assert.Equal(11, RowPaging.MaxPages(1000));    // все 1000 полей — нижняя граница из SyncRangeRows
        Assert.Equal(20, RowPaging.MaxPages(100));     // 400 000 / (100 × 200)
        Assert.True(RowPaging.MaxPages(40) * RowPaging.PageSize >= RowPaging.SyncRangeRows);
    }

    [Fact]
    public void MissingFields_returns_only_absent_and_not_in_flight()
    {
        int[] loaded = [1, 2, 3];
        Assert.Null(RowPaging.MissingFields(loaded, [1, 2], null));
        Assert.Equal([4, 5], RowPaging.MissingFields(loaded, [1, 4, 5], null));
        Assert.Equal([5], RowPaging.MissingFields(loaded, [1, 4, 5], new HashSet<int> { 4 }));
        Assert.Null(RowPaging.MissingFields(null, [1, 4, 5], null));   // у страницы все поля
    }

    [Fact]
    public void Union_is_sorted_and_distinct()
    {
        Assert.Equal([1, 2, 5, 9], RowPaging.Union([5, 1], [9, 2, 1]));
        Assert.Equal([3], RowPaging.Union(null, [3, 3]));
    }

    [Fact]
    public void PrefetchPage_follows_scroll_direction_and_stops_at_edges()
    {
        Assert.Equal(3, RowPaging.PrefetchPage(1, 2, prevFirstRow: -1, firstRow: 250, pageCount: 10));   // первый показ — вниз
        Assert.Equal(3, RowPaging.PrefetchPage(1, 2, prevFirstRow: 200, firstRow: 250, pageCount: 10));  // вниз
        Assert.Equal(0, RowPaging.PrefetchPage(1, 2, prevFirstRow: 300, firstRow: 250, pageCount: 10));  // вверх
        Assert.Null(RowPaging.PrefetchPage(0, 1, prevFirstRow: 300, firstRow: 0, pageCount: 10));        // выше нечего
        Assert.Null(RowPaging.PrefetchPage(8, 9, prevFirstRow: 0, firstRow: 1700, pageCount: 10));       // ниже нечего
    }

    [Fact]
    public void SplitSorted_divides_page_between_head_and_tail()
    {
        Assert.Equal((200, 0, 0),   RowPaging.SplitSorted(skip: 0,   take: 200, headCount: 1000));   // целиком в голове
        Assert.Equal((0, 200, 200), RowPaging.SplitSorted(skip: 400, take: 200, headCount: 200));    // целиком в хвосте
        Assert.Equal((50, 0, 150),  RowPaging.SplitSorted(skip: 150, take: 200, headCount: 200));    // граница внутри
        Assert.Equal((0, 0, 200),   RowPaging.SplitSorted(skip: 0,   take: 200, headCount: 0));      // голова пуста
    }

    [Fact]
    public void PageCount_rounds_up()
    {
        Assert.Equal(0, RowPaging.PageCount(0));
        Assert.Equal(1, RowPaging.PageCount(1));
        Assert.Equal(1, RowPaging.PageCount(200));
        Assert.Equal(2, RowPaging.PageCount(201));
    }
}
