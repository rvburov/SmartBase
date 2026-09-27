using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Окно фильтра на колонке с тысячами разных значений (<c>ConstructorRepository.GetColumnValueListAsync</c>):
/// список — первые <c>FilterValueCap</c> по алфавиту, число разных значений и «(Пусто)» последним;
/// поиск — по всей колонке в базе, без учета регистра и у кириллицы; снятое из «всех» значение —
/// отбор «кроме» (<see cref="FilterExclusion"/>), его понимает и сама таблица (<c>GetFilteredRecordIds</c>).
/// Колонка, у которой значений не больше предела, — полным списком, как раньше.
/// </summary>
public sealed class ColumnValueListTests
{
    private const string TitleKey = "__title__";
    private static readonly int Cap = ConstructorRepository.FilterValueCap;

    // Коллекция: у «Код» Cap + 5 разных значений («К0000» …), у трех записей — «Москва», «москва»,
    // «МОСКВА-2», у одной пусто; «Город» — два значения.
    private static (int Col, int Code, int City, List<int> Ids) Seed(ConstructorRepository repo)
    {
        var col  = SampleData.AddCollection(repo, "Коды").Id!.Value;
        var code = SampleData.AddField(repo, col, "Код",   position: 10).Id!.Value;
        var city = SampleData.AddField(repo, col, "Город", position: 20).Id!.Value;
        var ids  = new List<int>();
        var codes = Enumerable.Range(0, Cap + 5).Select(i => $"К{i:D4}")
            .Concat(["Москва", "москва", "МОСКВА-2", ""]).ToList();
        for (var i = 0; i < codes.Count; i++)
        {
            var id = SampleData.AddRecord(repo, col, $"Запись {i:D4}", (i + 1) * 10).Id!.Value;
            ids.Add(id);
            if (codes[i] != "") repo.SetFieldValue(id, code, codes[i]);
            repo.SetFieldValue(id, city, i % 2 == 0 ? "Казань" : "Тула");
        }
        return (col, code, city, ids);
    }

    [Fact]
    public async Task Many_values_are_capped_alphabetically_with_total_and_empty_last()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var (col, code, _, _) = Seed(repo);

        var list = await repo.GetColumnValueListAsync(col, code.ToString());

        Assert.True(list.Capped);
        Assert.True(list.More);
        Assert.Equal(Cap, list.Values.Count);                     // первые Cap по алфавиту
        Assert.Equal(("К0000", 1), list.Values[0]);
        var shown = list.Values.Select(v => v.Value).ToList();
        Assert.Equal(shown.Order(StringComparer.Ordinal), shown);

        // Число значений и «(Пусто)» окно досчитывает отдельно, после показа списка.
        Assert.Equal((Cap + 5 + 3L, 1), await repo.GetColumnValueTotalsAsync(col, code.ToString()));
    }

    [Fact]
    public async Task Search_finds_across_whole_column_ignoring_cyrillic_case()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var (col, code, _, _) = Seed(repo);

        // «москва» лежит за первыми Cap значениями — ее находит только поиск по базе.
        var found = await repo.GetColumnValueListAsync(col, code.ToString(), search: "МоСкВа");

        Assert.True(found.Capped);
        Assert.False(found.More);
        Assert.Equal(new[] { "Москва", "МОСКВА-2", "москва" }.Order(StringComparer.Ordinal),
                     found.Values.Select(v => v.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Capped_list_follows_filters_of_other_columns()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var (col, code, city, ids) = Seed(repo);
        var filters = new Dictionary<string, HashSet<string>> { [city.ToString()] = ["Казань"] };

        var list = await repo.GetColumnValueListAsync(col, code.ToString(), filters);

        // Казань — у каждой второй записи: «К0000», «К0002» … «К1004», «москва» и пустое (последняя
        // запись). Предел решает вся колонка, а считается только отобранное.
        Assert.True(list.Capped);
        Assert.False(list.More);
        Assert.Equal(("К0000", 1), list.Values[0]);
        Assert.Equal(("К0002", 1), list.Values[1]);
        var (distinct, empty) = await repo.GetColumnValueTotalsAsync(col, code.ToString(), filters);
        Assert.Equal(504, distinct);
        Assert.Equal(1, empty);
        Assert.Equal(repo.GetFilteredRecordIds(col, filters).Count, list.Values.Sum(v => v.Count) + empty);
        Assert.Equal(ids.Count / 2 + 1, list.Values.Sum(v => v.Count) + empty);
    }

    [Fact]
    public void Exclude_filter_drops_rows_with_excluded_values_and_empty()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var (col, code, _, ids) = Seed(repo);
        var all = repo.GetFilteredRecordIds(col, new Dictionary<string, HashSet<string>>());

        var noMoscow = repo.GetFilteredRecordIds(col,
            new Dictionary<string, HashSet<string>> { [code.ToString()] = FilterExclusion.Exclude(["Москва", "москва"]) });
        Assert.Equal(all.Count - 2, noMoscow.Count);
        Assert.Contains(ids[^2], noMoscow);                    // «МОСКВА-2» — другое значение, остается

        var noEmpty = repo.GetFilteredRecordIds(col,
            new Dictionary<string, HashSet<string>> { [code.ToString()] = FilterExclusion.Exclude([""]) });
        Assert.Equal(all.Count - 1, noEmpty.Count);
        Assert.DoesNotContain(ids[^1], noEmpty);

        var noTitle = repo.GetFilteredRecordIds(col,
            new Dictionary<string, HashSet<string>> { [TitleKey] = FilterExclusion.Exclude(["Запись 0000"]) });
        Assert.Equal(all.Count - 1, noTitle.Count);
        Assert.DoesNotContain(ids[0], noTitle);
    }

    [Fact]
    public async Task Few_values_keep_full_list_in_table_order()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var (col, _, city, _) = Seed(repo);

        var list = await repo.GetColumnValueListAsync(col, city.ToString());

        Assert.False(list.Capped);
        Assert.Equal(repo.GetColumnCounts(col, city.ToString()), list.Values);
    }

    [Fact]
    public async Task Title_column_is_capped_too()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var (col, _, _, _) = Seed(repo);

        var list = await repo.GetColumnValueListAsync(col, TitleKey);
        Assert.True(list.Capped);
        Assert.Equal(Cap, list.Values.Count);
        Assert.Equal("Запись 0000", list.Values[0].Value);

        var found = await repo.GetColumnValueListAsync(col, TitleKey, search: "ЗАПИСЬ 100");
        Assert.Equal(Enumerable.Range(1000, 9).Select(i => $"Запись {i:D4}"),
                     found.Values.Select(v => v.Value));
    }
}
