using SmartBase.Views.Library;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Потолок выдачи поиска — один на раздел (<c>Views/Library/DocSearchBudget.cs</c>).
///
/// Здесь закрепляются две вещи, которые раньше были неверны обе: потолок делится между базами,
/// а не выдается каждой целиком, и «обрезано» — это факт (пришла строка сверх остатка), а не
/// догадка по круглому числу показанного. Три базы по двести настоящих находок — не обрезка,
/// хотя показано 600; одна база, вернувшая на строку больше остатка, — обрезка, сколько бы ни
/// было показано.
/// </summary>
public sealed class DocSearchBudgetTests
{
    [Fact]
    public void The_first_source_is_asked_for_the_limit_plus_one()
    {
        var budget = new DocSearchBudget(500);

        Assert.Equal(501, budget.Ask);
        Assert.Equal(0, budget.Shown);
        Assert.False(budget.Capped);
    }

    [Fact]
    public void Sources_share_one_limit()
    {
        var budget = new DocSearchBudget(500);

        Assert.Equal(200, budget.Accept(200));
        Assert.Equal(301, budget.Ask);
        Assert.Equal(200, budget.Accept(200));
        Assert.Equal(101, budget.Ask);
        Assert.Equal(50, budget.Accept(50));

        Assert.Equal(450, budget.Shown);
        Assert.Equal(51, budget.Ask);
        Assert.False(budget.Capped);
    }

    /// <summary>Ровно потолок находок — не обрезка: спрашивали на одну больше, и ее не пришло.</summary>
    [Fact]
    public void Exactly_the_limit_is_not_a_cut()
    {
        var budget = new DocSearchBudget(500);

        Assert.Equal(500, budget.Accept(500));

        Assert.False(budget.Capped);
        Assert.Equal(500, budget.Shown);
        Assert.Equal(0, budget.Ask);
    }

    [Fact]
    public void One_row_past_the_room_is_a_cut_and_is_dropped()
    {
        var budget = new DocSearchBudget(500);

        Assert.Equal(500, budget.Accept(501));

        Assert.True(budget.Capped);
        Assert.Equal(500, budget.Shown);
    }

    /// <summary>Хвост нашелся у второй базы: показывается остаток, третью базу не спрашивают.</summary>
    [Fact]
    public void A_cut_in_the_middle_takes_the_rest_and_stops_asking()
    {
        var budget = new DocSearchBudget(500);
        budget.Accept(300);

        Assert.Equal(201, budget.Ask);
        Assert.Equal(200, budget.Accept(201));

        Assert.True(budget.Capped);
        Assert.Equal(500, budget.Shown);
        Assert.Equal(0, budget.Ask);
    }

    /// <summary>
    /// Кеш текста режет находки по содержимому сам, до сшивки с базами: его хвост — тоже хвост
    /// выдачи, хотя показанного он не меняет и остаток не тратит.
    /// </summary>
    [Fact]
    public void The_text_cache_can_declare_a_tail_by_itself()
    {
        var budget = new DocSearchBudget(500);

        budget.MarkCapped();

        Assert.True(budget.Capped);
        Assert.Equal(0, budget.Shown);
        Assert.Equal(501, budget.Ask);
    }

    [Fact]
    public void Nothing_found_is_not_a_cut()
    {
        var budget = new DocSearchBudget(500);

        Assert.Equal(0, budget.Accept(0));
        Assert.Equal(0, budget.Accept(0));

        Assert.False(budget.Capped);
        Assert.Equal(501, budget.Ask);
    }

    [Fact]
    public void A_limit_below_one_becomes_one()
    {
        var budget = new DocSearchBudget(0);

        Assert.Equal(1, budget.Limit);
        Assert.Equal(2, budget.Ask);
        Assert.Equal(1, budget.Accept(2));
        Assert.True(budget.Capped);
    }
}
