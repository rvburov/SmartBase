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
        Assert.Equal("\"системы\" AND \"управл\"*", DocTextIndex.BuildMatch("системы управл"));
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
        Assert.Equal("\"итого\" AND (\"30772.48\"* OR \"30 772 48\"*)", DocTextIndex.BuildMatch("итого 30772.48"));
        Assert.Equal("(\"30772\" OR \"30 772\") AND \"руб\"*", DocTextIndex.BuildMatch("30772 руб"));
    }

    /// <summary>
    /// Спецсимволы синтаксиса FTS5 внутри кавычек перестают быть синтаксисом. Без этого
    /// «-» открывал бы отрицание, а «*» посреди слова — ошибку разбора.
    /// </summary>
    [Theory]
    [InlineData("ГОСТ-34.11", "\"ГОСТ-34.11\"*")]
    [InlineData("C++", "\"C++\"*")]
    [InlineData("^начало", "\"^начало\"*")]
    [InlineData("NEAR", "\"NEAR\"*")]
    [InlineData("OR", "\"OR\"*")]
    public void Special_syntax_is_quoted_away(string query, string expected)
    {
        Assert.Equal(expected, DocTextIndex.BuildMatch(query));
    }

    /// <summary>Двойная кавычка внутри слова удваивается — иначе она закрыла бы терм.</summary>
    [Fact]
    public void Inner_quotes_are_doubled()
    {
        Assert.Equal("\"\"\"Регламент\"\"\"*", DocTextIndex.BuildMatch("\"Регламент\""));
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
