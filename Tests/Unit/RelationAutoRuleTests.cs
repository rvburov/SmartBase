using System.Text;
using System.Text.Json;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>Автоподбор по нескольким парам: свертка текста, составной ключ записи, чтение и запись
/// правила так, чтобы прежняя версия программы не пересчитывала его иначе.</summary>
public sealed class RelationAutoRuleTests
{
    private const string Yo = "\u0451";   // «е с точками»: в тексте исходников ее нет

    private static string? Key(string? raw, string type, bool fold)
    {
        Assert.True(RelationAutoMatch.TryKey(raw, type, out var key, 0, fold));
        return key;
    }

    [Fact]
    public void Fold_ignores_case_and_edge_whitespace_but_keeps_inner_spaces()
    {
        Assert.Equal(Key("ооо ромашка", "text", true), Key("  ООО Ромашка\u00A0", "text", true));
        Assert.Equal(Key("ромашка", "text", true), Key("\u200BРомашка\uFEFF", "text", true));
        Assert.NotEqual(Key("ООО  Ромашка", "text", true), Key("ООО Ромашка", "text", true));
        Assert.Null(Key(" \u200B ", "text", true));
    }

    [Fact]
    public void Fold_joins_composed_letters_and_the_dotted_e()
    {
        Assert.Equal(Key("Бер" + Yo + "за", "text", true), Key("БЕРЕЗА", "text", true));
        // «й» одной буквой и «и» с отдельным знаком краткости — одно и то же.
        Assert.Equal(Key("Чайка", "text", true), Key("Чаи\u0306ка", "text", true));
    }

    [Fact]
    public void Exact_mode_is_unchanged()
    {
        Assert.NotEqual(Key("A", "text", false), Key("a", "text", false));
        Assert.NotEqual(Key("A ", "text", false), Key("A", "text", false));
        Assert.Equal("s:A ", Key("A ", "text", false));
    }

    [Fact]
    public void Fold_skips_user_files_and_typed_values()
    {
        Assert.NotEqual(Key("Иванов", "user", true), Key("иванов", "user", true));
        Assert.NotEqual(Key("C:\\A.pdf", "file", true), Key("c:\\a.pdf", "file", true));
        Assert.Equal(Key("1,0", "number", true), Key("1", "number", false));
    }

    [Fact]
    public void Fold_applies_to_multiselect_items_and_lookup_text()
    {
        Assert.Equal(Key("[\"Б\",\"а \"]", "multiselect", true), Key("[\"А\",\"б\"]", "multiselect", true));
        Assert.NotEqual(Key("[\"Б\",\"а \"]", "multiselect", false), Key("[\"А\",\"б\"]", "multiselect", false));
        const string upper = "[{\"raw\":\"ООО\",\"fieldType\":\"text\"}]";
        const string lower = "[{\"raw\":\" ооо\",\"fieldType\":\"text\"}]";
        Assert.Equal(Key(upper, "lookup", true), Key(lower, "lookup", true));
        Assert.NotEqual(Key(upper, "lookup", false), Key(lower, "lookup", false));
    }

    private static (bool Ok, string? Key, bool FirstEmpty) Rule(bool fold, params (string? Raw, string Type)[] parts)
    {
        var ok = RelationAutoMatch.TryRuleKey(parts, fold, 0, new StringBuilder(), out var key, out var firstEmpty);
        return (ok, key, firstEmpty);
    }

    [Fact]
    public void Rule_key_is_injective()
    {
        Assert.NotEqual(Rule(false, ("a|b", "text"), ("c", "text")).Key, Rule(false, ("a", "text"), ("b|c", "text")).Key);
        Assert.NotEqual(Rule(false, ("a;", "text"), ("b", "text")).Key, Rule(false, ("a", "text"), (";b", "text")).Key);
        // Без длины части «a;s:b» + «c» и «a» + «b;s:c» склеились бы одинаково.
        Assert.NotEqual(Rule(false, ("a;s:b", "text"), ("c", "text")).Key, Rule(false, ("a", "text"), ("b;s:c", "text")).Key);
        // Одна пара — прежний ключ.
        Assert.Equal(Key("ИНН", "text", false), Rule(false, ("ИНН", "text")).Key);
    }

    [Fact]
    public void First_empty_is_unmatched_and_an_additional_empty_matches_only_empty()
    {
        Assert.Equal((true, null, true), Rule(false, ("", "text"), ("01", "text")));
        var soleTrader = Rule(false, ("7700", "text"), ("", "text"));
        Assert.NotNull(soleTrader.Key);
        Assert.Equal(soleTrader.Key, Rule(false, ("7700", "text"), (" ", "text")).Key);
        Assert.NotEqual(soleTrader.Key, Rule(false, ("7700", "text"), ("01", "text")).Key);
    }

    [Fact]
    public void A_damaged_part_fails_even_after_an_empty_one()
    {
        Assert.False(Rule(false, ("", "text"), ("12 ₽", "number")).Ok);
        Assert.False(Rule(false, ("7700", "text"), ("", "text"), ("maybe", "checkbox")).Ok);
    }

    [Fact]
    public void A_date_rollup_is_a_date()
    {
        const string earliest = "{\"relationFieldId\":5,\"targetField\":\"7\",\"aggregate\":\"earliest\"}";
        const string sum = "{\"relationFieldId\":5,\"targetField\":\"7\",\"aggregate\":\"sum\"}";
        Assert.Equal("date", RelationAutoMatch.Family("rollup", earliest));
        Assert.True(RelationAutoMatch.Compatible("date", null, "rollup", earliest));
        Assert.False(RelationAutoMatch.Compatible("number", null, "rollup", earliest));
        Assert.True(RelationAutoMatch.Compatible("number", null, "rollup", sum));
        Assert.True(RelationAutoMatch.Compatible("lookup", null, "date", null));
    }

    private static RelationAutoRule? Read(string auto) =>
        RelationConfig.FromConfig($"{{\"targetCollectionId\":3,\"single\":true,\"auto\":{auto}}}")?.Auto;

    private static string Write(RelationAutoRule rule)
    {
        var json = new RelationConfig(3, true, Auto: rule).ToConfigJson();
        return JsonDocument.Parse(json).RootElement.GetProperty("auto").GetRawText();
    }

    [Fact]
    public void A_single_exact_pair_keeps_the_old_format_byte_for_byte()
    {
        const string legacy = "{\"sourceField\":\"12\",\"targetField\":\"34\"}";
        Assert.Equal(new RelationAutoRule("12", "34"), Read(legacy));
        Assert.Equal(legacy, Write(new RelationAutoRule("12", "34")));
    }

    [Fact]
    public void Several_pairs_or_folding_write_a_guard_and_never_the_first_pair()
    {
        foreach (var rule in new[]
                 {
                     new RelationAutoRule([new("12", "34"), new("13", "35")]),
                     new RelationAutoRule([new("12", "34")], fold: true),
                 })
        {
            var json = Write(rule);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(RelationAutoRule.GuardSource, doc.RootElement.GetProperty("sourceField").GetString());
            Assert.Equal(RelationAutoRule.GuardTarget, doc.RootElement.GetProperty("targetField").GetString());
            Assert.Equal(rule, Read(json));
        }
        Assert.Contains("\"text\":\"fold\"", Write(new RelationAutoRule([new("12", "34")], fold: true)));
    }

    [Fact]
    public void The_previous_version_reads_the_guard_as_a_rule_on_a_missing_property()
    {
        // Дословно разбор «auto» из v1.3.2: ключи строками, IsFieldKey. Заглушка читается как
        // правило, а не как ручная связь, — и ее свойства цели нет, поэтому подбор отказывает.
        var json = Write(new RelationAutoRule([new("12", "34"), new("13", "35")]));
        using var doc = JsonDocument.Parse(json);
        static string? Old(JsonElement root, string name) =>
            root.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            && field.GetString() is { } key && RelationConfig.IsFieldKey(key) ? key : null;
        Assert.Equal(RelationConfig.TitleField, Old(doc.RootElement, "sourceField"));
        Assert.Equal("2147483647", Old(doc.RootElement, "targetField"));
    }

    [Fact]
    public void Rules_of_other_versions_keep_their_state()
    {
        Assert.Equal(RelationAutoState.Lost, Read("{\"sourceField\":\"__title__\",\"targetField\":\"2147483647\"}")!.State);
        const string newer = "{\"sourceField\":\"__title__\",\"targetField\":\"2147483647\",\"format\":3,\"pairs\":[],\"x\":1}";
        var rule = Read(newer)!;
        Assert.Equal(RelationAutoState.Newer, rule.State);
        Assert.Equal(newer, Write(rule));
        Assert.Empty(rule.SourceKeys);
        Assert.False(rule.IsReady);
    }

    [Theory]
    [InlineData("\"pairs\":[{\"sourceField\":\"1\",\"targetField\":\"2\"},{\"sourceField\":\"3\",\"targetField\":\"4\"},{\"sourceField\":\"5\",\"targetField\":\"6\"},{\"sourceField\":\"7\",\"targetField\":\"8\"}]")]
    [InlineData("\"pairs\":[{\"sourceField\":\"1\",\"targetField\":\"2\"}],\"text\":\"loose\"")]
    [InlineData("\"pairs\":[{\"sourceField\":\"1\",\"targetField\":\"x\"}]")]
    [InlineData("\"pairs\":[]")]
    [InlineData("\"pairs\":[{\"sourceField\":\"1\",\"targetField\":\"2\",\"extra\":true}]")]
    [InlineData("\"pairs\":[{\"sourceField\":\"1\",\"targetField\":\"2\"}],\"unknown\":1")]
    public void A_broken_second_format_is_damaged_not_truncated(string body)
    {
        var json = $"{{\"sourceField\":\"__title__\",\"targetField\":\"2147483647\",\"format\":2,{body}}}";
        var rule = Read(json)!;
        Assert.Equal(RelationAutoState.Damaged, rule.State);
        Assert.Equal(json, Write(rule));
    }

    [Theory]
    [InlineData("{\"sourceField\":\"12\",\"targetField\":\"34\",\"format\":2,\"pairs\":[{\"sourceField\":\"1\",\"targetField\":\"2\"}]}")]
    [InlineData("{\"sourceField\":\"__title__\",\"targetField\":\"2147483647\",\"format\":\"2\",\"pairs\":[]}")]
    [InlineData("{\"sourceField\":\"__title__\",\"targetField\":\"2147483647\",\"format\":1}")]
    public void A_bad_format_is_damaged_not_manual(string auto) =>
        Assert.Equal(RelationAutoState.Damaged, Read(auto)!.State);

    [Fact]
    public void Keys_are_remapped_for_every_pair_and_an_unread_rule_becomes_lost()
    {
        var rule = new RelationAutoRule([new("1", "2"), new("3", "4")], fold: true);
        var mapped = rule.MapKeys(k => k + "0", k => k);
        Assert.Equal([new RelationAutoPair("10", "2"), new RelationAutoPair("30", "4")], mapped.Pairs);
        Assert.True(mapped.Fold);
        var newer = Read("{\"sourceField\":\"__title__\",\"targetField\":\"2147483647\",\"format\":3}")!;
        Assert.Equal(RelationAutoState.Lost, newer.MapKeys(k => k, k => k).State);
    }

    [Fact]
    public void Rules_are_equal_by_pairs_and_folding()
    {
        Assert.Equal(new RelationAutoRule([new("1", "2"), new("3", "4")]), new RelationAutoRule([new("1", "2"), new("3", "4")]));
        Assert.NotEqual(new RelationAutoRule([new("1", "2"), new("3", "4")]), new RelationAutoRule([new("3", "4"), new("1", "2")]));
        Assert.NotEqual(new RelationAutoRule("1", "2"), new RelationAutoRule([new("1", "2")], fold: true));
        Assert.Throws<ArgumentException>(() => new RelationAutoRule(
            [new("1", "2"), new("3", "4"), new("5", "6"), new("7", "8")]));
    }
}
