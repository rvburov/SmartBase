using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Views.Library;
using Origin = SmartBase.Database.Repositories.ConstructorRepository.DocSeekOrigin;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Выбор пути к куску ленты (<c>Views/Library/DocSeekPlan.cs</c>) и счет по гистограмме типов.
///
/// Цена пути — сколько строк база пропустит смещением. Прокрутка подряд обязана идти от соседа
/// за ноль пропущенных строк: иначе каждая страница в глубине коллекции-миллионника стоила бы
/// прохода по всему, что выше нее (замер на 125 000 документов: 455 мс против 1,3 мс).
/// </summary>
public sealed class DocSeekPlanTests
{
    private static readonly DocCursor A = new(10, 1, 1000, 5);
    private static readonly DocCursor B = new(20, 2, 1000, 5);

    private static Dictionary<int, DocCursor> Known(params (int At, DocCursor Cursor)[] items) =>
        items.ToDictionary(i => i.At, i => i.Cursor);

    [Fact]
    public void The_first_page_is_read_from_the_start()
    {
        var seek = DocSeekPlan.Choose(0, 200, 125_000, Known());

        Assert.Equal(Origin.Start, seek.From);
        Assert.Equal(0, seek.Skip);
    }

    [Fact]
    public void The_next_page_goes_right_after_the_previous_one()
    {
        var seek = DocSeekPlan.Choose(60_200, 200, 125_000, Known((60_199, A)));

        Assert.Equal(Origin.After, seek.From);
        Assert.Equal(0, seek.Skip);
        Assert.Equal(A, seek.Anchor);
    }

    /// <summary>Прокрутка вверх после прыжка вглубь: страница над приехавшей идет от ее первого документа.</summary>
    [Fact]
    public void The_page_above_goes_right_before_the_one_below()
    {
        var seek = DocSeekPlan.Choose(60_000, 200, 125_000, Known((60_200, B)));

        Assert.Equal(Origin.Before, seek.From);
        Assert.Equal(0, seek.Skip);
        Assert.Equal(B, seek.Anchor);
    }

    /// <summary>Прыжок ползунком в конец: соседа нет, но от конца пропускать нечего.</summary>
    [Fact]
    public void A_jump_to_the_end_counts_from_the_end()
    {
        var seek = DocSeekPlan.Choose(124_800, 200, 125_000, Known());

        Assert.Equal(Origin.End, seek.From);
        Assert.Equal(0, seek.Skip);
    }

    [Fact]
    public void A_jump_past_the_middle_takes_the_nearer_edge()
    {
        var seek = DocSeekPlan.Choose(70_000, 200, 125_000, Known());

        Assert.Equal(Origin.End, seek.From);
        Assert.Equal(125_000 - 70_200, seek.Skip);
    }

    [Fact]
    public void A_known_document_nearby_beats_both_edges()
    {
        var seek = DocSeekPlan.Choose(60_100, 200, 125_000, Known((60_000, A), (10, B)));

        Assert.Equal(Origin.After, seek.From);
        Assert.Equal(99, seek.Skip);
        Assert.Equal(A, seek.Anchor);
    }

    /// <summary>
    /// Кусок, вылезающий за конец потока (размер потока устарел), от конца не считается: такой
    /// путь дал бы чужие строки.
    /// </summary>
    [Fact]
    public void A_chunk_past_the_end_is_not_counted_from_the_end()
    {
        var seek = DocSeekPlan.Choose(100, 200, 150, Known());

        Assert.Equal(Origin.Start, seek.From);
        Assert.Equal(100, seek.Skip);
    }

    /// <summary>Курсор внутри самого куска ни выше, ни ниже его — опереться на него нельзя.</summary>
    [Fact]
    public void A_cursor_inside_the_chunk_is_not_an_anchor()
    {
        var seek = DocSeekPlan.Choose(200, 200, 125_000, Known((250, A)));

        Assert.Equal(Origin.Start, seek.From);
        Assert.Equal(200, seek.Skip);
    }

    // ── Счет по гистограмме ──────────────────────────────────────────────────

    private static readonly ConstructorRepository.DocTypeHistogram Histogram = new(
        7, new Dictionary<string, int> { ["ГОСТ"] = 5, ["СТО"] = 3, [""] = 2 });

    private static ConstructorRepository.DocView Types(params string[] types) => new(new HashSet<string>(types));

    [Fact]
    public void Without_a_filter_the_histogram_gives_everything()
    {
        Assert.Equal(10, Histogram.CountFor(ConstructorRepository.DocView.All));
    }

    [Fact]
    public void A_filter_sums_its_types_including_the_empty_one()
    {
        Assert.Equal(5, Histogram.CountFor(Types("ГОСТ")));
        Assert.Equal(7, Histogram.CountFor(Types("ГОСТ", "")));
    }

    /// <summary>
    /// Пустой набор типов — «не подходит ничего», а не «все»: та же семантика, что у SQL-условия
    /// вида. Тип, которого в коллекции нет, просто ничего не добавляет.
    /// </summary>
    [Fact]
    public void An_empty_filter_and_an_absent_type_give_nothing()
    {
        Assert.Equal(0, Histogram.CountFor(Types()));
        Assert.Equal(0, Histogram.CountFor(Types("нет такого")));
    }
}
