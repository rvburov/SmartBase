using SmartBase.Views.Library;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Раскладка ленты документов по строкам (Views/Library/DocRowMap.cs).
///
/// Виртуальный список отвечает на вопрос «что стоит в строке N», не материализуя соседей, —
/// вся его правильность держится на этой арифметике. Ошибка здесь не падает: она молча
/// показывает документ не той коллекции или теряет последнюю карточку. Отличие от задач —
/// лента ПЛОСКАЯ (заголовков групп нет), и пустой поток занимает ноль строк, то есть двоичный
/// поиск обязан его перескакивать. Это и есть главный риск, который здесь проверяется.
/// </summary>
public sealed class DocRowMapTests
{
    private static DocRowMap Map(params int[] streams) => new(streams);

    // ── Длина ленты ──────────────────────────────────────────────────────────

    [Fact]
    public void Empty_map_has_no_rows()
    {
        var map = Map();

        Assert.Equal(0, map.Count);
        Assert.Equal((-1, -1), map.Resolve(0));
    }

    /// <summary>Заголовков у ленты нет: поток стоит ровно столько строк, сколько в нем документов.</summary>
    [Fact]
    public void A_stream_costs_exactly_its_documents()
    {
        Assert.Equal(3, Map(3).Count);
        Assert.Equal(7, Map(3, 4).Count);
    }

    [Fact]
    public void Streams_follow_each_other_without_gaps()
    {
        var map = Map(2, 3);

        Assert.Equal(new DocRowSpan(0, 2), map[0]);
        Assert.Equal(new DocRowSpan(2, 3), map[1]);
    }

    // ── Что стоит в строке ───────────────────────────────────────────────────

    [Fact]
    public void Resolve_returns_the_stream_and_the_local_number()
    {
        var map = Map(2, 3);

        Assert.Equal((0, 0), map.Resolve(0));
        Assert.Equal((0, 1), map.Resolve(1));
        Assert.Equal((1, 0), map.Resolve(2));
        Assert.Equal((1, 2), map.Resolve(4));
    }

    [Fact]
    public void Rows_outside_the_feed_resolve_to_nothing()
    {
        var map = Map(2);

        Assert.Equal((-1, -1), map.Resolve(-1));
        Assert.Equal((-1, -1), map.Resolve(2));
    }

    // ── Пустые потоки ────────────────────────────────────────────────────────
    //
    // Коллекция, у которой под фильтр типов не подошло ни одного документа, дает пустой поток.
    // Он занимает ноль строк, и его начало совпадает с началом следующего — двоичный поиск
    // «последний, чье начало не позже строки» упрется именно в него. Отдавать его наружу
    // нельзя: страница уехала бы читать не ту коллекцию.

    [Fact]
    public void An_empty_stream_in_the_middle_is_skipped()
    {
        var map = Map(2, 0, 3);

        Assert.Equal(5, map.Count);
        Assert.Equal((0, 1), map.Resolve(1));
        // Строка 2 принадлежит ТРЕТЬЕМУ потоку, хотя второй начинается там же.
        Assert.Equal((2, 0), map.Resolve(2));
        Assert.Equal((2, 2), map.Resolve(4));
    }

    [Fact]
    public void Several_empty_streams_in_a_row_are_skipped()
    {
        var map = Map(1, 0, 0, 0, 2);

        Assert.Equal(3, map.Count);
        Assert.Equal((0, 0), map.Resolve(0));
        Assert.Equal((4, 0), map.Resolve(1));
        Assert.Equal((4, 1), map.Resolve(2));
    }

    [Fact]
    public void Leading_empty_streams_do_not_shift_the_feed()
    {
        var map = Map(0, 0, 2);

        Assert.Equal(2, map.Count);
        Assert.Equal((2, 0), map.Resolve(0));
        Assert.Equal((2, 1), map.Resolve(1));
    }

    [Fact]
    public void Trailing_empty_streams_add_no_rows()
    {
        var map = Map(2, 0, 0);

        Assert.Equal(2, map.Count);
        Assert.Equal((0, 1), map.Resolve(1));
        Assert.Equal((-1, -1), map.Resolve(2));
    }

    [Fact]
    public void A_feed_of_only_empty_streams_is_empty()
    {
        var map = Map(0, 0);

        Assert.Equal(0, map.Count);
        Assert.Equal((-1, -1), map.Resolve(0));
    }

    /// <summary>Отрицательный размер (испорченный счет) не должен уводить нумерацию назад.</summary>
    [Fact]
    public void Negative_sizes_count_as_empty()
    {
        var map = Map(-5, 2);

        Assert.Equal(2, map.Count);
        Assert.Equal((1, 0), map.Resolve(0));
    }

    // ── Каждая строка достижима ровно один раз ───────────────────────────────

    [Fact]
    public void Every_row_maps_to_exactly_one_document()
    {
        var map  = Map(3, 0, 1, 4, 0, 2);
        var seen = new HashSet<(int, int)>();

        for (var row = 0; row < map.Count; row++)
        {
            var (stream, local) = map.Resolve(row);
            Assert.True(stream >= 0, $"строка {row} никому не принадлежит");
            Assert.True(seen.Add((stream, local)), $"строка {row} повторяет уже занятое место");
            // Локальный номер обязан лежать внутри своего потока.
            Assert.InRange(local, 0, map[stream].RowCount - 1);
        }

        Assert.Equal(map.Count, seen.Count);
    }
}
