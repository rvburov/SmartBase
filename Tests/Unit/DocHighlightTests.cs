using SmartBase.Core.Documents;
using SmartBase.Database.DocIndex;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Подсветка найденного в панели чтения (<see cref="DocHighlight"/>).
///
/// Главное, что здесь проверяется, — НЕ сами совпадения, а то, что панель считает их по тем же
/// правилам, по которым ищет раздел. Разойдись они — и человек увидит «нашлось 12 документов», а
/// в открытом документе ни одного желтого слова; выглядит это как сломанная панель, а не как
/// разные правила, и найти такое по коду почти нечем.
/// </summary>
public sealed class DocHighlightTests
{
    private static List<string> Words(string text, string query) =>
        [.. DocHighlight.Find(text, DocHighlight.Tokens(query)).Select(h => text.Substring(h.Start, h.Length))];

    // ── Разбор запроса ────────────────────────────────────────────────────────

    /// <summary>
    /// Слова запроса режет ОДНА функция на всю программу: ею пользуется и выражение MATCH для
    /// FTS5, и подсветка. Второй разбор — это первый шаг к расхождению.
    /// </summary>
    [Theory]
    [InlineData("договор подряда", 2)]
    [InlineData("  договор   подряда  ", 2)]
    [InlineData("гост,сто;регламент", 3)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    public void Query_is_split_into_words(string query, int count)
    {
        Assert.Equal(count, DocHighlight.Tokens(query).Count);
    }

    [Fact]
    public void Match_expression_is_built_from_the_same_words()
    {
        var tokens = DocHighlight.Tokens("гост 21.501");
        var match  = DocTextIndex.BuildMatch("гост 21.501");

        foreach (var token in tokens) Assert.Contains(token, match, StringComparison.Ordinal);
        // Последнему слову приписан «*» — его же панель ищет по началу слова.
        Assert.EndsWith("*", match, StringComparison.Ordinal);
    }

    // ── Что считается совпадением ─────────────────────────────────────────────

    [Fact]
    public void Case_does_not_matter()
    {
        Assert.Equal(["ДОГОВОР", "Договор"], Words("ДОГОВОР и еще Договор", "договор"));
    }

    /// <summary>
    /// «ё» ищется как «е» — так документ с «учёт» находится по «учет». Это не вольность
    /// подсветки, а поведение токенизатора индекса (<c>unicode61 remove_diacritics 2</c>).
    /// </summary>
    [Fact]
    public void Yo_is_read_as_ye()
    {
        Assert.Equal(["учёт"], Words("бухгалтерский учёт ведется", "учет"));
        Assert.Equal(["учет"], Words("бухгалтерский учет ведется", "учёт"));
    }

    /// <summary>
    /// Последнее слово запроса ищется по НАЧАЛУ слова, и подсвечивается при этом слово
    /// целиком: набравший «автоматизир» ищет «автоматизированных», и желтым обязано быть
    /// слово, а не его обрубок.
    /// </summary>
    [Fact]
    public void Last_word_matches_by_prefix_and_highlights_the_whole_word()
    {
        Assert.Equal(["автоматизированных"], Words("перечень автоматизированных систем", "автоматизир"));
    }

    /// <summary>
    /// А вот слово НЕ последнее совпадает только целиком — ровно так его ищет MATCH, где «*»
    /// приписан одному последнему.
    /// </summary>
    [Fact]
    public void Earlier_words_match_whole_only()
    {
        // «систем» — не последнее слово, и «системами» ему не равно.
        Assert.DoesNotContain("системами", Words("работа с системами связи", "систем связ"));
        Assert.Contains("связи", Words("работа с системами связи", "систем связ"));
    }

    [Fact]
    public void Match_starts_at_a_word_boundary()
    {
        // «под» есть внутри «подряда», но слова «под» в тексте нет — подсвечивать нечего.
        Assert.DoesNotContain("под", Words("договор подряда", "под договор"));
    }

    [Fact]
    public void Hits_come_in_order_and_do_not_overlap()
    {
        const string text = "акт акт акт";
        var hits = DocHighlight.Find(text, DocHighlight.Tokens("акт"));

        Assert.Equal(3, hits.Count);
        for (var i = 1; i < hits.Count; i++) Assert.True(hits[i].Start >= hits[i - 1].End);
    }

    [Fact]
    public void Digits_are_part_of_a_word()
    {
        Assert.Equal(["21"], Words("ГОСТ 21.501 лист 3", "21"));
    }

    // ── Слово с пунктуацией внутри — фраза ───────────────────────────────────
    //
    // Индекс режет «21.501» на «21» и «501» и ищет их подряд (BuildMatch берет слово в
    // кавычки — это фраза). Пока подсветка сравнивала «21.501» с целыми словами текста, такой
    // документ находился поиском, а в панели не подсвечивалось ничего — ровно тот исход,
    // который заголовок DocHighlight объявляет недопустимым.

    [Fact]
    public void A_word_with_punctuation_inside_is_a_phrase_of_its_parts()
    {
        Assert.Equal(["21.501"], Words("ГОСТ 21.501 лист 3", "21.501"));
        // Части подряд — а чем они разделены в тексте, индексу все равно.
        Assert.Equal(["21 501"], Words("ГОСТ 21 501 лист", "21.501"));
        Assert.Equal(["кв.м"],   Words("площадь 40 кв.м", "кв.м"));
    }

    [Fact]
    public void The_parts_of_a_phrase_must_stand_together()
    {
        Assert.Empty(Words("21 лист 501", "21.501"));
        Assert.Empty(Words("501.21", "21.501"));
    }

    /// <summary>«*» последнего слова запроса достается последней части фразы — как в MATCH.</summary>
    [Fact]
    public void The_prefix_goes_to_the_last_part_of_the_last_word()
    {
        Assert.Equal(["21.501"], Words("ГОСТ 21.501", "21.5"));
        // То же слово не последнее — совпадает только целиком.
        Assert.Equal(["ГОСТ"], Words("ГОСТ 21.501", "21.5 гост"));
    }

    /// <summary>
    /// «C++» для индекса — одно слово «c» с «*»: находится и «C++», и «cat». Подсветка честно
    /// показывает то же самое, и желтой становится буква, а не плюсы, — их индекс не видит.
    /// </summary>
    [Fact]
    public void Punctuation_around_a_word_is_not_part_of_it()
    {
        Assert.Equal(["C", "cat"], Words("компилятор C++ и cat", "C++"));
    }

    /// <summary>
    /// Слово из одной пунктуации индекс не учитывает: «гост ++» находит все, где есть «гост».
    /// Подсветке — то же, иначе найденное осталось бы без единого желтого слова.
    /// </summary>
    [Fact]
    public void Punctuation_only_words_are_ignored()
    {
        Assert.Equal(["гост"], Words("просто гост без номера", "гост ++"));
        Assert.Empty(Words("просто гост", "++"));
        Assert.Empty(DocHighlight.Parts("++"));
    }

    /// <summary>На одном месте побеждает самая длинная фраза — «21.501» целиком, а не «21».</summary>
    [Fact]
    public void The_longest_phrase_wins_at_one_spot()
    {
        Assert.Equal(["21.501"], Words("ГОСТ 21.501", "21 21.501"));
    }

    [Fact]
    public void Nothing_to_search_gives_nothing()
    {
        Assert.Empty(DocHighlight.Find("любой текст", DocHighlight.Tokens("")));
        Assert.Empty(DocHighlight.Find("", DocHighlight.Tokens("договор")));
        Assert.Empty(DocHighlight.Find(null, DocHighlight.Tokens("договор")));
    }
}
