using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

public sealed class RelationAutoMatchTests
{
    private static string? Key(string? raw, string type, int db = 0)
    {
        Assert.True(RelationAutoMatch.TryKey(raw, type, out var key, db));
        return key;
    }

    [Theory]
    [InlineData("1,00", "1.0")]
    [InlineData("-0", "0")]
    [InlineData("12.500", "1.25e1")]
    [InlineData("12345678901234567890.01", "12345678901234567890,010")]
    public void Numbers_match_the_value_without_rounding_or_display_format(string left, string right) =>
        Assert.Equal(Key(left, "number"), Key(right, "number"));

    [Theory]
    [InlineData("12 ₽", "number")]
    [InlineData("2026-99-42", "date")]
    [InlineData("tomorrow", "date")]
    [InlineData("maybe", "checkbox")]
    [InlineData("{}", "relation")]
    [InlineData("[{\"DbId\":1,\"RecordId\":\"2\"}]", "relation")]
    public void Invalid_typed_values_do_not_become_empty_matches(string value, string type) =>
        Assert.False(RelationAutoMatch.TryKey(value, type, out _));

    [Fact]
    public void Dates_use_the_full_range_and_distinguish_all_day_from_midnight()
    {
        Assert.Equal(Key("01.01.2026", "date"), Key("2026-01-01", "date"));
        Assert.Equal(Key("01.01.2026 - 03.01.2026", "date"), Key("2026-01-01/2026-01-03", "date"));
        Assert.NotEqual(Key("2026-01-01", "date"), Key("2026-01-01T00:00", "date"));
    }

    [Fact]
    public void Empty_does_not_equal_false_or_zero()
    {
        Assert.Null(Key(" ", "checkbox"));
        Assert.Null(Key("", "number"));
        Assert.NotNull(Key("0", "checkbox"));
        Assert.Equal(Key("0", "checkbox"), Key("false", "checkbox"));
        Assert.Equal(Key("1", "checkbox"), Key("TRUE", "checkbox"));
        Assert.NotEqual(Key("0", "number"), Key("0", "checkbox"));
    }

    [Fact]
    public void Text_is_literal_case_sensitive_and_preserves_significant_spaces()
    {
        Assert.Equal(Key("A", "text"), Key("A", "select"));
        Assert.NotEqual(Key("A", "text"), Key("a", "text"));
        Assert.NotEqual(Key("A ", "text"), Key("A", "text"));
        Assert.NotEqual(Key("A", "user"), Key("A", "text"));
    }

    [Fact]
    public void Multi_select_is_a_complete_set_instead_of_a_display_join_or_intersection()
    {
        Assert.Equal(Key("[\"B\",\"A\",\"A\"]", "multiselect"), Key("[\"A\",\"B\"]", "multiselect"));
        Assert.NotEqual(Key("[\"A, B\"]", "multiselect"), Key("[\"A\",\"B\"]", "multiselect"));
        Assert.NotEqual(Key("[\"A\"]", "multiselect"), Key("[\"A\",\"B\"]", "multiselect"));
        Assert.Null(Key("[]", "multiselect"));
    }

    [Fact]
    public void File_keys_preserve_commas_and_use_full_paths()
    {
        Assert.NotEqual(Key("/one/report, final.pdf", "file"), Key("/two/report, final.pdf", "file"));
        Assert.Equal(Key(FileValue.Serialize(["/b.pdf", "/a.pdf"]), "file"),
                     Key(FileValue.Serialize(["/a.pdf", "/b.pdf"]), "file"));
    }

    [Fact]
    public void Relation_keys_use_the_database_and_record_pair()
    {
        const string local = "[{\"DbId\":0,\"RecordId\":7}]";
        Assert.Equal(Key(local, "relation", 3), Key("[{\"DbId\":3,\"RecordId\":7}]", "relation"));
        Assert.NotEqual(Key(local, "relation", 3), Key("[{\"DbId\":4,\"RecordId\":7}]", "relation"));
        Assert.False(RelationAutoMatch.TryKey(local, "relation", out _));
        Assert.Null(Key("[]", "relation"));
    }

    [Fact]
    public void Typed_lookup_sets_compare_values_without_display_punctuation()
    {
        const string first = "[{\"raw\":\"1.0\",\"fieldType\":\"number\"},{\"raw\":\"A, B\",\"fieldType\":\"text\"}]";
        const string second = "[{\"raw\":\"A, B\",\"fieldType\":\"text\"},{\"raw\":\"1,00\",\"fieldType\":\"number\"}]";
        Assert.Equal(Key(first, "lookup"), Key(second, "lookup"));
        Assert.False(RelationAutoMatch.TryKey("[{\"raw\":\"broken\",\"fieldType\":\"number\"}]", "lookup", out _));
    }
}
