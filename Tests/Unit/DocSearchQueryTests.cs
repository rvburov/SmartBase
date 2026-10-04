using SmartBase.Database.DocIndex;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Сборка выражения FTS5, разбор сниппета и написания пути
/// (Database/DocIndex/DocTextIndex.cs) — чистые функции без базы.
///
/// Запрос приходит от человека, а синтаксис FTS5 полон спецсимволов (<c>* : ^ - NEAR OR</c>).
/// Без экранирования «C++» и «-» роняли бы поиск синтаксической ошибкой, а «OR» молча менял бы
/// смысл запроса. Поэтому каждое слово берется в кавычки, и проверяется это здесь.
/// </summary>
public sealed class DocSearchQueryTests
{
    // ── Выражение MATCH ──────────────────────────────────────────────────────

    [Fact]
    public void Empty_query_builds_nothing()
    {
        Assert.Equal("", DocTextIndex.BuildMatch(null));
        Assert.Equal("", DocTextIndex.BuildMatch(""));
        Assert.Equal("", DocTextIndex.BuildMatch("   "));
    }

    /// <summary>Одно слово ищется по префиксу: набравший «автоматизир» ждет «автоматизированных».</summary>
    [Fact]
    public void A_single_word_becomes_a_prefix_term()
    {
        Assert.Equal("\"автоматизир\"*", DocTextIndex.BuildMatch("автоматизир"));
    }

    /// <summary>Префикс только у последнего слова — остальные человек уже дописал.</summary>
    [Fact]
    public void Only_the_last_word_gets_a_prefix_star()
    {
        Assert.Equal("\"сварка\" AND \"труб\"*", DocTextIndex.BuildMatch("сварка труб"));
        Assert.Equal("(\"системы\" OR \"систёмы\") AND \"управл\"*", DocTextIndex.BuildMatch("системы управл"));
    }

    /// <summary>
    /// «Е» ищется и как «ё»: токенизатор индекса «ё» не складывает, а набирают обычно без нее.
    /// Набранная «ё» складывается в «е» и дает те же написания — «учёт» и «учет» ищут одно.
    /// </summary>
    [Fact]
    public void Ye_is_also_searched_as_yo()
    {
        Assert.Equal("(\"учет\"* OR \"учёт\"*)", DocTextIndex.BuildMatch("учет"));
        Assert.Equal("(\"учет\"* OR \"учёт\"*)", DocTextIndex.BuildMatch("учёт"));
        Assert.Equal("(\"учет\"* OR \"учёт\"*)", DocTextIndex.BuildMatch("УЧЁТ"));
        Assert.Equal("(\"учет контроль\"* OR \"учёт контроль\"*)", DocTextIndex.BuildMatch("учёт-контроль"));
    }

    /// <summary>У сложных слов «ё» бывает две — перебираются и такие написания.</summary>
    [Fact]
    public void Two_yo_in_a_word_are_spelled_too()
    {
        Assert.Contains("\"трёхмёрный\"", DocTextIndex.BuildMatch("трехмерный чертеж"), StringComparison.Ordinal);
        Assert.Contains("\"трёхмёрный\"", DocTextIndex.BuildMatch("трёхмёрный чертеж"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Слово из одних «е» не раздувает выражение: написаний не больше потолка, и первым идет
    /// написание без «ё».
    /// </summary>
    [Fact]
    public void Yo_spellings_are_capped()
    {
        var word      = new string('е', 40);
        var spellings = DocTextIndex.YoSpellings([word]);

        Assert.Equal(DocTextIndex.MaxYoSpellings, spellings.Count);
        Assert.Equal(word, spellings[0][0]);
        Assert.Equal(DocTextIndex.MaxYoSpellings, spellings.Select(s => s[0]).Distinct().Count());
        Assert.Equal(DocTextIndex.MaxYoSpellings, DocTextIndex.BuildMatch(word).Split(" OR ").Length);
    }

    [Fact]
    public void Words_are_split_by_spaces_commas_and_semicolons()
    {
        Assert.Equal("\"а\" AND \"б\" AND \"в\"*", DocTextIndex.BuildMatch("а, б; в"));
    }

    /// <summary>
    /// Слово из одной пунктуации индекс не видит, и в выражение оно не идет: с явным AND пустая
    /// фраза не находила бы ничего, и «гост ++» перестал бы находить «гост». Звездочка остается
    /// за последним словом запроса — так же считает подсветка.
    /// </summary>
    [Fact]
    public void Punctuation_only_words_stay_out_of_the_expression()
    {
        Assert.Equal("\"гост\"", DocTextIndex.BuildMatch("гост ++"));
        Assert.Equal("\"гост\" AND \"лист\"*", DocTextIndex.BuildMatch("гост ++ лист"));
        Assert.Equal("", DocTextIndex.BuildMatch("++ --"));
    }

    /// <summary>
    /// Число из четырех и больше цифр ищется и с разрядами — скобкой через OR, со звездочкой у
    /// каждого варианта последнего слова. Слова соединяет ЯВНЫЙ AND: неявное «и» у FTS5 скобку
    /// с вариантами не связывает.
    /// </summary>
    [Fact]
    public void A_long_number_is_searched_with_its_groups_too()
    {
        Assert.Equal("(\"30772\"* OR \"30 772\"*)", DocTextIndex.BuildMatch("30772"));
        Assert.Equal("\"итого\" AND (\"30772 48\"* OR \"30 772 48\"*)", DocTextIndex.BuildMatch("итого 30772.48"));
        Assert.Equal("(\"30772\" OR \"30 772\") AND \"руб\"*", DocTextIndex.BuildMatch("30772 руб"));
    }

    /// <summary>
    /// Спецсимволы синтаксиса FTS5 до выражения не доходят: слово режется на части так же, как
    /// его режет токенизатор, и фраза собирается из частей в кавычках. Без этого «-» открывал бы
    /// отрицание, «*» посреди слова — ошибку разбора, а «OR» менял бы смысл запроса.
    /// </summary>
    [Theory]
    [InlineData("ГОСТ-34.11", "\"гост 34 11\"*")]
    [InlineData("C++", "\"c\"*")]
    [InlineData("^начало", "\"начало\"*")]
    [InlineData("NEAR", "\"near\"*")]
    [InlineData("OR", "\"or\"*")]
    public void Special_syntax_is_quoted_away(string query, string expected)
    {
        Assert.Equal(expected, DocTextIndex.BuildMatch(query));
    }

    /// <summary>Двойная кавычка вокруг слова — разделитель и в выражение не попадает: терм она не закроет.</summary>
    [Fact]
    public void Quotes_around_a_word_stay_out_of_the_expression()
    {
        Assert.Equal("\"гост\"*", DocTextIndex.BuildMatch("\"ГОСТ\""));
        Assert.Equal("\"гост 34\"*", DocTextIndex.BuildMatch("ГОСТ\"34"));
    }

    // ── Разбор сниппета ──────────────────────────────────────────────────────

    [Fact]
    public void An_empty_snippet_has_no_parts()
    {
        Assert.Empty(DocTextIndex.SplitSnippet(null));
        Assert.Empty(DocTextIndex.SplitSnippet(""));
    }

    [Fact]
    public void Text_without_markers_is_one_plain_part()
    {
        var parts = DocTextIndex.SplitSnippet("обычный текст");

        Assert.Equal([("обычный текст", false)], parts);
    }

    [Fact]
    public void Markers_split_the_snippet_into_plain_and_hit_parts()
    {
        var snippet = $"…требования к {DocTextIndex.HitOpen}автоматизированных" +
                      $"{DocTextIndex.HitClose} систем…";

        Assert.Equal(
        [
            ("…требования к ",      false),
            ("автоматизированных",  true),
            (" систем…",            false),
        ], DocTextIndex.SplitSnippet(snippet));
    }

    [Fact]
    public void Several_hits_in_one_snippet_are_all_marked()
    {
        var snippet = $"{DocTextIndex.HitOpen}систем{DocTextIndex.HitClose} и " +
                      $"{DocTextIndex.HitOpen}управления{DocTextIndex.HitClose}";

        Assert.Equal(
        [
            ("систем",     true),
            (" и ",        false),
            ("управления", true),
        ], DocTextIndex.SplitSnippet(snippet));
    }

    /// <summary>Совпадение в самом конце строки не теряется вместе с закрывающим маркером.</summary>
    [Fact]
    public void A_hit_at_the_very_end_survives()
    {
        var snippet = $"текст {DocTextIndex.HitOpen}конец{DocTextIndex.HitClose}";

        Assert.Equal([("текст ", false), ("конец", true)], DocTextIndex.SplitSnippet(snippet));
    }

    // ── Написания пути ───────────────────────────────────────────────────────

    /// <summary>
    /// Ключ кеша нормализован, а в поле записи путь лежит как его записали: на Windows тот же
    /// файл может стоять с прямыми слешами. В запрос обязаны уйти оба написания — иначе поиск
    /// по содержимому молча терял бы находку.
    /// </summary>
    [Fact]
    public void On_windows_a_path_is_searched_with_both_separators()
    {
        var spellings = DocTextIndex.Spellings(@"P:\Договоры\смета.xlsx");

        if (OperatingSystem.IsWindows())
            Assert.Equal([@"P:\Договоры\смета.xlsx", "P:/Договоры/смета.xlsx"], spellings);
        else
            // На Unix обратный слеш — обычный знак имени файла: подмена дала бы другой файл.
            Assert.Equal([@"P:\Договоры\смета.xlsx"], spellings);
    }

    /// <summary>Путь без обратных слешей второго написания не порождает: варианта нет.</summary>
    [Fact]
    public void A_path_without_backslashes_stays_alone()
    {
        Assert.Equal(["/home/док/смета.xlsx"], DocTextIndex.Spellings("/home/док/смета.xlsx"));
    }

    /// <summary>Пустой ключ не дает написаний вовсе — в <c>IN</c> уходить нечему.</summary>
    [Fact]
    public void An_empty_key_gives_no_spellings()
    {
        Assert.Empty(DocTextIndex.Spellings(""));
        Assert.Empty(DocTextIndex.Spellings(null!));
    }
}
