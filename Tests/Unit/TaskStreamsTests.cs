using SmartBase.Views.Tasks;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Разбиение куска группы на потоки чтения (Views/Tasks/TaskStreams.cs).
///
/// Порядок «срок ↑, без даты в конец» собирается двумя потоками встык, а страница
/// виртуального списка их границу не уважает: она легко начинается в первом и заканчивается во
/// втором. Ошибка в этой арифметике не падает — она тихо повторяет или теряет задачи ровно на
/// стыке, то есть в том единственном месте, где сбой почти невидим.
///
/// Смещение второго потока отсчитывается от начала СВОЕГО потока, а не группы: это и есть
/// главная ловушка, поэтому проверяется в каждом пересекающем случае.
/// </summary>
public sealed class TaskStreamsTests
{
    private static List<TaskStreams.Slice> Split(int dated, int from, int take) =>
        TaskStreams.Split(dated, from, take);

    // ── Внутри одного потока ─────────────────────────────────────────────────

    [Fact]
    public void A_slice_entirely_inside_the_dated_stream_is_one_query()
    {
        Assert.Equal([new TaskStreams.Slice(true, 0, 5)], Split(dated: 10, from: 0, take: 5));
        Assert.Equal([new TaskStreams.Slice(true, 3, 4)], Split(dated: 10, from: 3, take: 4));
    }

    /// <summary>Кусок ровно до границы — всё ещё один запрос: следующая строка уже чужая.</summary>
    [Fact]
    public void A_slice_ending_exactly_at_the_boundary_stays_one_query()
    {
        Assert.Equal([new TaskStreams.Slice(true, 6, 4)], Split(dated: 10, from: 6, take: 4));
    }

    /// <summary>Кусок, начинающийся ровно на границе, целиком во втором потоке и смещение
    /// у него нулевое, а не 10.</summary>
    [Fact]
    public void A_slice_starting_exactly_at_the_boundary_is_the_undated_stream_from_zero()
    {
        Assert.Equal([new TaskStreams.Slice(false, 0, 4)], Split(dated: 10, from: 10, take: 4));
    }

    [Fact]
    public void A_slice_deep_in_the_undated_stream_shifts_by_the_first_stream_length()
    {
        Assert.Equal([new TaskStreams.Slice(false, 5, 3)], Split(dated: 10, from: 15, take: 3));
    }

    // ── Через границу ────────────────────────────────────────────────────────

    [Fact]
    public void A_slice_crossing_the_boundary_splits_in_two()
    {
        Assert.Equal(
            [new TaskStreams.Slice(true, 8, 2), new TaskStreams.Slice(false, 0, 3)],
            Split(dated: 10, from: 8, take: 5));
    }

    [Fact]
    public void A_slice_covering_the_whole_group_splits_at_the_boundary()
    {
        Assert.Equal(
            [new TaskStreams.Slice(true, 0, 4), new TaskStreams.Slice(false, 0, 6)],
            Split(dated: 4, from: 0, take: 10));
    }

    // ── Вырожденные потоки ───────────────────────────────────────────────────

    /// <summary>Ни одной задачи со сроком — всё берётся из второго потока без смещения.</summary>
    [Fact]
    public void With_no_dated_tasks_everything_comes_from_the_undated_stream()
    {
        Assert.Equal([new TaskStreams.Slice(false, 0, 7)], Split(dated: 0, from: 0, take: 7));
        Assert.Equal([new TaskStreams.Slice(false, 4, 3)], Split(dated: 0, from: 4, take: 3));
    }

    /// <summary>Сроки есть у всех — второго потока не возникает вовсе.</summary>
    [Fact]
    public void With_every_task_dated_the_undated_stream_is_never_asked()
    {
        Assert.Equal([new TaskStreams.Slice(true, 0, 20)], Split(dated: 100, from: 0, take: 20));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nothing_is_asked_for_an_empty_take(int take)
    {
        Assert.Empty(Split(dated: 10, from: 0, take: take));
    }

    // ── Свойство, ради которого всё и написано ───────────────────────────────

    /// <summary>
    /// Как ни режь группу на куски, потоки в сумме дают каждую строку РОВНО ОДИН раз и в том же
    /// порядке. Перебираем все длины куска и все точки старта — именно так страницы и ложатся
    /// на группу при разных размерах коллекции.
    /// </summary>
    [Theory]
    [InlineData(0, 6)]
    [InlineData(1, 6)]
    [InlineData(3, 6)]
    [InlineData(6, 6)]
    [InlineData(4, 9)]
    public void Any_chunking_covers_every_row_exactly_once(int dated, int total)
    {
        for (var chunk = 1; chunk <= total + 2; chunk++)
        {
            var seen = new List<(bool Dated, int Index)>();
            for (var from = 0; from < total; from += chunk)
            {
                var take = Math.Min(chunk, total - from);
                foreach (var s in Split(dated, from, take))
                    for (var i = 0; i < s.Take; i++) seen.Add((s.Dated, s.Skip + i));
            }

            var expected = new List<(bool, int)>();
            for (var i = 0; i < dated; i++)         expected.Add((true, i));
            for (var i = 0; i < total - dated; i++) expected.Add((false, i));

            Assert.Equal(expected, seen);
        }
    }
}
