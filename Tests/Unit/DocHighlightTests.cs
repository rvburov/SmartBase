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
        var tokens = DocHighlight.Tokens("ГОСТ 21.501");
        var match  = DocTextIndex.BuildMatch("ГОСТ 21.501");

        // Фраза выражения — те же части, на которые слово режет подсветка, и так же сложенные.
        foreach (var token in tokens)
            Assert.Contains("\"" + string.Join(' ', DocHighlight.Parts(token)) + "\"", match, StringComparison.Ordinal);
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
    /// «ё» ищется как «е» — так документ с «учёт» находится по «учет», и наоборот. У
    /// токенизатора индекса такого правила нет, поиск уравнивает «е» и «ё» запросом
    /// (<c>DocTextIndex.YoSpellings</c>), а подсветка — сложением.
    /// </summary>
    [Fact]
    public void Yo_is_read_as_ye()
    {
        Assert.Equal(["учёт"], Words("бухгалтерский учёт ведется", "учет"));
        Assert.Equal(["учет"], Words("бухгалтерский учет ведется", "учёт"));
        Assert.Equal(["УЧЁТ"], Words("БУХГАЛТЕРСКИЙ УЧЁТ", "учет"));
    }

    /// <summary>
    /// А «й» — своя буква, и для токенизатора тоже: «мой» и «мои» — разные слова. Пока
    /// подсветка складывала «й» в «и», панель подсвечивала то, чего поиск не находил.
    /// </summary>
    [Fact]
    public void Short_i_is_a_letter_of_its_own()
    {
        Assert.Equal(["мои"], Words("мой дом и мои книги", "мои"));
        Assert.Empty(Words("йод", "иод"));
        Assert.Equal(["йод"], Words("йод", "йод"));
    }

    /// <summary>
    /// С латиницы надстрочные знаки снимаются, как в индексе: «cafe» находит «café», и панель
    /// обязана его подсветить.
    /// </summary>
    [Fact]
    public void Latin_letters_lose_their_marks()
    {
        Assert.Equal(["café"], Words("Le café du coin", "cafe"));
        Assert.Equal(["Zürich"], Words("Werk Zürich", "zurich"));
        Assert.Equal(["Zurich"], Words("Werk Zurich", "zürich"));
    }

    /// <summary>
    /// Знак, стоящий отдельно от буквы, — ударение или две точки разложенной «ё» — слова не
    /// рвет: индекс его снимает, а слово читает целым.
    /// </summary>
    [Fact]
    public void A_mark_apart_from_its_letter_does_not_split_the_word()
    {
        Assert.Equal(["за\u0301мок"], Words("старый за\u0301мок", "замок"));
        Assert.Equal(["за\u0301мок"], Words("старый за\u0301мок", "за\u0301мок"));
        Assert.Equal(["е\u0308лка"],   Words("зеленая е\u0308лка", "ёлка"));
    }

    /// <summary>
    /// «²», «½» и римская «Ⅳ» — часть слова: «м²» для индекса одно слово, и по «м²» не
    /// подсвечивается каждое слово на «м».
    /// </summary>
    [Fact]
    public void Superscripts_and_fractions_belong_to_the_word()
    {
        Assert.Equal(["м²"], Words("площадь 40 м² и масса", "м²"));
        Assert.Equal(["½"],  Words("доля ½ ставки", "½"));
        Assert.Empty(Words("объем 5 м³", "м3"));
    }

    /// <summary>Греческие начертания одной буквы — одна буква: «φ» находит и «ϕ», как в индексе.</summary>
    [Fact]
    public void Greek_letter_shapes_are_one_letter()
    {
        Assert.Equal(["ϕ"], Words("угол ϕ = 30°", "φ"));
        Assert.Equal(["ς"], Words("ς", "σ"));
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

    // ── Числа с разрядами ─────────────────────────────────────────────────────

    // Варианты слова строками: части через пробел.
    private static string[] Variants(string token) =>
        [.. DocHighlight.Variants(token).Select(v => string.Join(' ', v))];

    // Сумма так, как ее показывает книга по записи «#,##0.00», — с разрядами языка системы.
    private static string Money(double value) =>
        value.ToString("#,##0.00", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>
    /// Число из четырех и больше цифр ищется и с разрядами: книга показывает «30 772,48», а
    /// набирают «30772». Дробная часть остается при числе.
    /// </summary>
    [Fact]
    public void A_long_number_is_also_searched_with_its_groups()
    {
        Assert.Equal(["30772", "30 772"],          Variants("30772"));
        Assert.Equal(["30772 48", "30 772 48"],    Variants("30772.48"));
        Assert.Equal(["1234567", "1 234 567"],     Variants("-1234567"));
    }

    /// <summary>
    /// Вариант — только у слова, которое ЦЕЛИКОМ число. Код, номер с приставкой, число с ведущим
    /// нулем и короткое число разрядами не делятся.
    /// </summary>
    [Theory]
    [InlineData("12345-2019")]
    [InlineData("№12345")]
    [InlineData("01234")]
    [InlineData("123")]
    [InlineData("05.03.2024")]
    [InlineData("2024г")]
    [InlineData("1234.")]
    public void Codes_and_short_numbers_have_one_variant(string token)
    {
        Assert.Single(DocHighlight.Variants(token));
    }

    [Fact]
    public void Punctuation_only_words_have_no_variants()
    {
        Assert.Empty(DocHighlight.Variants("++"));
        Assert.Empty(DocHighlight.Variants(""));
    }

    /// <summary>
    /// Сумма, найденная поиском по «30772», подсвечивается в панели целиком — одним куском от
    /// первой группы цифр до последней.
    /// </summary>
    [Fact]
    public void A_number_typed_without_groups_highlights_the_grouped_one()
    {
        var text = $"Итого {Money(30772.48)} руб.";
        var head = Money(30772.48)[..^3];                    // «30 772» без копеек

        Assert.Equal([head], Words(text, "30772"));
        Assert.Equal([Money(30772.48)], Words(text, "30772.48"));
        Assert.Equal(["30772"], Words("сырое 30772.48", "30772"));
    }

    // ── Слова страницы ────────────────────────────────────────────────────────

    /// <summary>
    /// На странице PDF текст — это слова с местами. Фраза запроса переходит через границу слов,
    /// как ее находит индекс: «30772» обязан подсветить оба слова суммы «30 772,48», иначе поиск
    /// нашел документ, а страница сказала «нет совпадений».
    /// </summary>
    [Fact]
    public void A_phrase_spans_the_words_of_a_page()
    {
        string[] words = ["Итого", "30", "772,48", "руб.", "ГОСТ", "21.", "501"];
        var tokens = DocHighlight.Tokens("30772");

        Assert.Equal([(1, 2)], DocHighlight.FindInWords(words, tokens));
        Assert.Equal([(5, 6)], DocHighlight.FindInWords(words, DocHighlight.Tokens("21.501")));
        Assert.Equal([(0, 0), (3, 3)], DocHighlight.FindInWords(words, DocHighlight.Tokens("итого руб")));
    }

    /// <summary>Два совпадения в одном слове — одно совпадение: подсвечивается слово, и считать его дважды незачем.</summary>
    [Fact]
    public void Two_hits_inside_one_word_count_once()
    {
        Assert.Equal([(0, 0)], DocHighlight.FindInWords(["21.501-21"], DocHighlight.Tokens("21")));
        Assert.Empty(DocHighlight.FindInWords([], DocHighlight.Tokens("21")));
        Assert.Empty(DocHighlight.FindInWords(["21"], DocHighlight.Tokens("")));
    }
}
