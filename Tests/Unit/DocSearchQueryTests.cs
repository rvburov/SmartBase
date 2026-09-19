using SmartBase.Database.DocIndex;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Сборка выражения FTS5 и разбор сниппета (Database/DocIndex/DocTextIndex.cs) — чистые функции
/// без базы.
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
        Assert.Equal("\"системы\" \"управл\"*", DocTextIndex.BuildMatch("системы управл"));
    }

    [Fact]
    public void Words_are_split_by_spaces_commas_and_semicolons()
    {
        Assert.Equal("\"а\" \"б\" \"в\"*", DocTextIndex.BuildMatch("а, б; в"));
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
}
