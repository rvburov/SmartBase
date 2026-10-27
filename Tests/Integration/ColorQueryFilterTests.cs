using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Отбор и сортировка таблицы по цвету (<see cref="ColorQuery"/>) на базе: цвет — видимый в ячейке
/// колонки (своя ячейка, строка или колонка — что выставлено позже), список цветов колонки — с
/// числом записей, свой отбор колонки этот список не режет.
/// </summary>
public sealed class ColorQueryFilterTests
{
    private const string Red   = "#FF3B30";
    private const string Green = "#34C759";

    // Пять записей: R1 — красная ячейка «A», R2 — зеленая строка, R3 — без цвета, R4 — красный текст
    // ячейки «A», R5 — красная ячейка в другой колонке «B».
    private static (ConstructorRepository Repo, int ColId, string A, string B, int[] R) Seed(TempDatabase db)
    {
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo).Id!.Value;
        var a    = SampleData.AddField(repo, col, "A", "text").Id!.Value.ToString();
        var b    = SampleData.AddField(repo, col, "B", "text").Id!.Value.ToString();
        var r    = Enumerable.Range(1, 5).Select(i => SampleData.AddRecord(repo, col, $"R{i}", i * 1000).Id!.Value).ToArray();
        repo.SetCellStyle(col, [(r[0], a)], StyleAspect.Background, Red);
        repo.SetRowStyle(col, [r[1]], StyleAspect.Background, Green);
        repo.SetCellStyle(col, [(r[3], a)], StyleAspect.Text, Red);
        repo.SetCellStyle(col, [(r[4], b)], StyleAspect.Background, Red);
        return (repo, col, a, b, r);
    }

    private static Task<List<int>> Ids(ConstructorRepository repo, int col, Dictionary<string, HashSet<string>> filters,
                                      string? sortKey = null, bool asc = true) =>
        repo.GetFilteredRecordIdsAsync(col, filters, excludeHidden: false, sortKey, asc, null, CancellationToken.None);

    [Fact]
    public async Task Filter_by_cell_fill_takes_only_that_color()
    {
        using var db = new TempDatabase();
        var (repo, col, a, _, r) = Seed(db);

        var ids = await Ids(repo, col, new() { [ColorQuery.FilterKey(a)] = ["bg:" + Red] });

        Assert.Equal([r[0]], ids);
    }

    [Fact]
    public async Task Row_color_is_the_color_of_its_cells()
    {
        using var db = new TempDatabase();
        var (repo, col, a, _, r) = Seed(db);

        var ids = await Ids(repo, col, new() { [ColorQuery.FilterKey(a)] = ["bg:" + Green] });

        Assert.Equal([r[1]], ids);
    }

    /// <summary>«Без заливки» — все, у кого в этой колонке нет видимого фона, в том числе запись с
    /// красной ячейкой в ДРУГОЙ колонке и запись с цветом только у текста.</summary>
    [Fact]
    public async Task No_fill_takes_everything_without_a_visible_background()
    {
        using var db = new TempDatabase();
        var (repo, col, a, _, r) = Seed(db);

        var ids = await Ids(repo, col, new() { [ColorQuery.FilterKey(a)] = ["bg:"] });

        Assert.Equal([r[2], r[3], r[4]], ids);
    }

    [Fact]
    public async Task Filter_by_text_color()
    {
        using var db = new TempDatabase();
        var (repo, col, a, _, r) = Seed(db);

        Assert.Equal([r[3]], await Ids(repo, col, new() { [ColorQuery.FilterKey(a)] = ["fg:" + Red] }));
    }

    [Fact]
    public async Task Multiple_backgrounds_take_any_selected_color_without_reordering()
    {
        using var db = new TempDatabase();
        var (repo, col, a, _, r) = Seed(db);

        var ids = await Ids(repo, col, new()
        {
            [ColorQuery.FilterKey(a)] = ["bg:" + Green, "bg:" + Red],
        });

        Assert.Equal([r[0], r[1]], ids);
    }

    [Fact]
    public async Task No_fill_is_an_alternative_to_a_selected_background()
    {
        using var db = new TempDatabase();
        var (repo, col, a, _, r) = Seed(db);

        var ids = await Ids(repo, col, new()
        {
            [ColorQuery.FilterKey(a)] = ["bg:", "bg:" + Red],
        });

        Assert.Equal([r[0], r[2], r[3], r[4]], ids);
    }

    [Fact]
    public async Task Background_choices_and_text_choices_must_both_match()
    {
        using var db = new TempDatabase();
        var (repo, col, a, _, r) = Seed(db);
        repo.SetCellStyle(col, [(r[0], a)], StyleAspect.Text, Red);
        repo.SetCellStyle(col, [(r[3], a)], StyleAspect.Background, Green);

        var ids = await Ids(repo, col, new()
        {
            [ColorQuery.FilterKey(a)] = ["bg:" + Red, "bg:" + Green, "fg:" + Red],
        });

        // Зеленая R2 с обычным текстом не подходит, хотя ее фон отмечен.
        Assert.Equal([r[0], r[3]], ids);
    }

    [Fact]
    public async Task Default_text_excludes_colored_text_when_common_background_matches()
    {
        using var db = new TempDatabase();
        var (repo, col, a, _, r) = Seed(db);

        var ids = await Ids(repo, col, new()
        {
            [ColorQuery.FilterKey(a)] = ["bg:", "bg:" + Red, "fg:"],
        });

        // Большинство ячеек без своего оформления подходит; R4 с красным текстом выпадает.
        Assert.Equal([r[0], r[2], r[4]], ids);
    }

    [Fact]
    public async Task Common_column_background_does_not_bypass_the_text_condition()
    {
        using var db = new TempDatabase();
        var (repo, col, a, _, r) = Seed(db);
        repo.SetColumnStyle(col, [a], StyleAspect.Background, Green);

        var ids = await Ids(repo, col, new()
        {
            [ColorQuery.FilterKey(a)] = ["bg:" + Green, "fg:" + Red],
        });

        Assert.Equal([r[3]], ids);
    }

    [Fact]
    public async Task Empty_color_selection_keeps_other_filters_and_sorting()
    {
        using var db = new TempDatabase();
        var (repo, col, a, b, r) = Seed(db);
        repo.SetCellsBulk(col, [new(r[2], int.Parse(b), "x"), new(r[4], int.Parse(b), "x")]);

        var ids = await Ids(repo, col, new()
        {
            [ColorQuery.FilterKey(a)] = [],
            [b] = ["x"],
        }, "__title__", asc: false);

        Assert.Equal([r[4], r[2]], ids);
    }

    [Fact]
    public async Task Multiple_color_choices_keep_value_filters_and_existing_sort()
    {
        using var db = new TempDatabase();
        var (repo, col, a, b, r) = Seed(db);
        repo.SetCellsBulk(col, [new(r[0], int.Parse(b), "x"), new(r[1], int.Parse(b), "x"),
                                new(r[3], int.Parse(b), "x")]);

        var ids = await Ids(repo, col, new()
        {
            [ColorQuery.FilterKey(a)] = ["bg:" + Red, "bg:" + Green],
            [b] = ["x"],
        }, "__title__", asc: false);

        Assert.Equal([r[1], r[0]], ids);
    }

    /// <summary>Цвет колонки — у всех записей без своего: отбор по нему берет их, а не только
    /// окрашенные ячейки.</summary>
    [Fact]
    public async Task Column_color_is_seen_by_every_record_without_its_own()
    {
        using var db = new TempDatabase();
        var (repo, col, _, b, r) = Seed(db);
        repo.SetColumnStyle(col, [b], StyleAspect.Background, Green);

        var ids = await Ids(repo, col, new() { [ColorQuery.FilterKey(b)] = ["bg:" + Green] });

        // Строка R2 тоже зеленая; красная ячейка R5 выставлена раньше цвета колонки и им снята.
        Assert.Equal(r, ids.OrderBy(x => x));
    }

    [Fact]
    public async Task Color_filter_combines_with_value_filter()
    {
        using var db = new TempDatabase();
        var (repo, col, a, _, r) = Seed(db);
        repo.SetCellsBulk(col, [new(r[2], int.Parse(a), "x"), new(r[4], int.Parse(a), "x")]);

        var ids = await Ids(repo, col, new()
        {
            [ColorQuery.FilterKey(a)] = ["bg:"],
            [a]                       = ["x"],
        });

        Assert.Equal([r[2], r[4]], ids);
    }

    [Fact]
    public async Task Sort_by_color_puts_it_on_top_and_keeps_table_order()
    {
        using var db = new TempDatabase();
        var (repo, col, a, _, r) = Seed(db);
        repo.SetCellStyle(col, [(r[2], a)], StyleAspect.Background, Red);

        var top    = await Ids(repo, col, [], ColorQuery.SortKey(a, "bg:" + Red), asc: true);
        var bottom = await Ids(repo, col, [], ColorQuery.SortKey(a, "bg:" + Red), asc: false);

        Assert.Equal([r[0], r[2], r[1], r[3], r[4]], top);
        Assert.Equal([r[1], r[3], r[4], r[0], r[2]], bottom);
    }

    [Fact]
    public async Task Column_colors_are_counted_by_visible_color()
    {
        using var db = new TempDatabase();
        var (repo, col, a, _, _) = Seed(db);

        var colors = (await repo.GetColumnColorsAsync(col, a)).ToDictionary(c => c.Token, c => c.Count);

        Assert.Equal(1, colors["bg:" + Red]);
        Assert.Equal(1, colors["bg:" + Green]);
        Assert.Equal(3, colors["bg:"]);
        Assert.Equal(1, colors["fg:" + Red]);
        Assert.Equal(4, colors["fg:"]);
    }

    /// <summary>Как список значений: отбор других колонок счет режет, свой — нет.</summary>
    [Fact]
    public async Task Column_colors_follow_other_filters_but_not_their_own()
    {
        using var db = new TempDatabase();
        var (repo, col, a, b, r) = Seed(db);
        repo.SetCellsBulk(col, [new(r[0], int.Parse(b), "y"), new(r[1], int.Parse(b), "y")]);

        var colors = (await repo.GetColumnColorsAsync(col, a, new Dictionary<string, HashSet<string>>
        {
            [b]                       = ["y"],
            [ColorQuery.FilterKey(a)] = ["bg:" + Red],
        })).ToDictionary(c => c.Token, c => c.Count);

        Assert.Equal(1, colors["bg:" + Red]);
        Assert.Equal(1, colors["bg:" + Green]);
        Assert.False(colors.ContainsKey("bg:"));
    }

    /// <summary>Список значений колонки свой отбор по цвету тоже не режет — иначе снятое цветом
    /// значение нельзя было бы вернуть в отбор.</summary>
    [Fact]
    public void Value_list_ignores_own_color_filter()
    {
        using var db = new TempDatabase();
        var (repo, col, a, _, r) = Seed(db);
        repo.SetCellsBulk(col, [new(r[0], int.Parse(a), "красная"), new(r[2], int.Parse(a), "простая")]);

        var values = repo.GetColumnCounts(col, a, new Dictionary<string, HashSet<string>> { [ColorQuery.FilterKey(a)] = ["bg:" + Red] });

        Assert.Contains(values, v => v.Value == "простая");
    }
}
