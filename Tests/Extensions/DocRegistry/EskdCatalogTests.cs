using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Встроенный справочник ЕСКД: состав файла и подбор по нему.
///
/// Главный инвариант здесь — ПОРЯДОК: подбор по цифрам сделан бинарным поиском по диапазону
/// кодов и на неотсортированном файле молча отдавал бы неполный список. Файл готовится
/// отсортированным, загрузчик это проверяет, а тест ниже проверяет сам файл — чтобы поломка
/// нашлась при прогоне, а не у пользователя.
/// </summary>
public sealed class EskdCatalogTests
{
    /// <summary>Состав файла прибит: справочник — часть поставки, и его подмена должна
    /// заметиться. 66 186 — все шестизначные коды исходных данных (неполные уровни
    /// классификатора в сборку не идут: в обозначение они не ставятся).</summary>
    [Fact]
    public void Catalog_has_all_six_digit_codes() =>
        Assert.Equal(66_186, EskdCatalog.Count);

    [Fact]
    public void Codes_are_strictly_ascending()
    {
        var previous = -1;
        foreach (var entry in EskdCatalog.All)
        {
            var code = int.Parse(entry.Code);
            Assert.True(code >= previous, $"Код {entry.Code} идет после {previous:D6} — порядок сломан");
            previous = code;
        }
    }

    [Fact]
    public void Every_code_is_six_digits() =>
        Assert.All(EskdCatalog.All, e => Assert.True(EskdCatalog.IsFullCode(e.Code)));

    /// <summary>Список окна связывается с <see cref="EskdCatalog.All"/> напрямую, поэтому он
    /// должен быть ГОТОВЫМ и одним и тем же — а не пересобираться на каждое обращение.</summary>
    [Fact]
    public void All_is_the_same_list_every_time() =>
        Assert.Same(EskdCatalog.All, EskdCatalog.All);

    [Theory]
    [InlineData("301121", true)]
    [InlineData("04000",  false)]   // пять цифр — неполный код
    [InlineData("3011211", false)]  // семь цифр
    [InlineData("30112a", false)]
    [InlineData("",       false)]
    [InlineData(null,     false)]
    public void Full_code_is_exactly_six_digits(string? code, bool expected) =>
        Assert.Equal(expected, EskdCatalog.IsFullCode(code));

    // ── Точный поиск ──────────────────────────────────────────────────────────

    [Fact]
    public void Known_code_is_found()
    {
        var entry = EskdCatalog.Find("040000");

        Assert.NotNull(entry);
        Assert.Equal("По двум и более видам норм", entry.Name);
    }

    [Fact]
    public void Missing_code_is_not_found() =>
        Assert.Null(EskdCatalog.Find("999999"));

    [Fact]
    public void Incomplete_code_is_not_found() =>
        Assert.Null(EskdCatalog.Find("0400"));

    // ── Подбор по цифрам ──────────────────────────────────────────────────────

    [Fact]
    public void Digits_pick_codes_by_prefix()
    {
        var found = EskdCatalog.Search("3011");

        Assert.Equal(56, found.Total);
        Assert.All(found.Shown, e => Assert.StartsWith("3011", e.Code, StringComparison.Ordinal));
    }

    [Fact]
    public void Full_code_picks_its_own_row()
    {
        var found = EskdCatalog.Search("301121");

        Assert.Equal(1, found.Total);
        Assert.Equal("301121", found.Shown[0].Code);
    }

    /// <summary>Пять кодов в исходных данных задвоены с разными наименованиями — обе строки
    /// остаются в подсказке, выбор между ними за человеком.</summary>
    [Fact]
    public void Duplicated_code_shows_both_rows()
    {
        var found = EskdCatalog.Search("337641");

        Assert.Equal(2, found.Total);
        Assert.Equal(["Браслеты", "Броши"], found.Shown.Select(e => e.Name));
    }

    [Fact]
    public void More_digits_than_a_code_finds_nothing() =>
        Assert.Equal(0, EskdCatalog.Search("3011211").Total);

    // ── Подбор по наименованию ────────────────────────────────────────────────

    [Fact]
    public void Word_picks_codes_by_name()
    {
        var found = EskdCatalog.Search("трехступенчатые");

        Assert.True(found.Total > 0);
        Assert.All(found.Shown, e => Assert.Contains("рехступенчатые", e.Name.ToLowerInvariant(),
                                                     StringComparison.Ordinal));
    }

    /// <summary>
    /// Данные справочника готовятся в одном написании буквы «е», а человек может набрать второе —
    /// свертка приводит запрос к тому же виду, и находится то же самое. Без нее такой запрос не
    /// нашел бы ничего.
    /// </summary>
    [Fact]
    public void Both_spellings_find_the_same_rows() =>
        Assert.Equal(EskdCatalog.Search("трехступенчатые").Total,
                     EskdCatalog.Search("трёхступенчатые").Total);

    /// <summary>В данных справочника второго написания буквы «е» нет — файл готовится без него.</summary>
    [Fact]
    public void Catalog_names_use_one_spelling() =>
        Assert.All(EskdCatalog.All,
                   e => Assert.DoesNotContain('ё', e.Name.ToLowerInvariant()));

    [Fact]
    public void Name_search_ignores_case() =>
        Assert.Equal(EskdCatalog.Search("трехступенчатые").Total,
                     EskdCatalog.Search("ТРЕХСТУПЕНЧАТЫЕ").Total);

    // ── Границы выдачи ────────────────────────────────────────────────────────

    /// <summary>Пустой фильтр — ВЕСЬ справочник: список в окне открыт до всякого ввода, и ввод
    /// его сужает, а не наполняет.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_query_returns_the_whole_catalog(string? query)
    {
        var found = EskdCatalog.Search(query);

        Assert.Equal(EskdCatalog.Count, found.Total);
        Assert.Same(EskdCatalog.All, found.Shown);   // та же готовая коллекция, без копии
    }

    /// <summary>Отобранные строки — ТЕ ЖЕ объекты, что в <see cref="EskdCatalog.All"/>: список
    /// не пересоздает строки на каждый фильтр, а выделение переживает смену отбора.</summary>
    [Fact]
    public void Search_returns_the_same_entry_objects()
    {
        var fromAll    = EskdCatalog.All.First(e => e.Code == "301121");
        var fromSearch = EskdCatalog.Search("301121").Shown.Single();

        Assert.Same(fromAll, fromSearch);
    }

    /// <summary>Показ ограничен, но общее число нашедшегося видно — окно пишет его подсказкой.</summary>
    [Fact]
    public void Long_result_is_truncated_but_counted()
    {
        var found = EskdCatalog.Search("3", limit: 10);

        Assert.Equal(10, found.Shown.Count);
        Assert.True(found.Total > 10);
        Assert.True(found.Truncated);
    }

    [Fact]
    public void Short_result_is_not_truncated()
    {
        var found = EskdCatalog.Search("301121");

        Assert.False(found.Truncated);
    }
}
