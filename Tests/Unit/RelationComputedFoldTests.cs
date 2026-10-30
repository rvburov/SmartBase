using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>Свертка значений целей одной записи (<see cref="RelationComputedFold"/>): старшинство
/// ошибок, склейка подписей, итоги и типизированное значение.</summary>
public sealed class RelationComputedFoldTests
{
    private static RelationPropertyValue Value(int id, string raw, string display, string type = "text", string? config = null) =>
        new(id, "", raw, display, type, config);

    private static RelationPropertyValue Fault(int id, string text) => new(id, "", "", text, "", null);

    [Fact]
    public void A_cycle_wins_over_a_bad_target_and_a_bad_target_over_the_value()
    {
        var cycle = new RelationPropertyValue(3, "", RelationProperty.Cycle, RelationProperty.Cycle, "text", null);
        Assert.Equal(RelationProperty.Cycle,
            RelationComputedFold.Fold("lookup", "lookup", ", ", null, [Value(1, "а", "а"), Fault(2, RelationProperty.MissingField), cycle]).Value);
        var bad = RelationComputedFold.Fold("rollup", "sum", ", ", null,
            [Value(1, "1", "1", "number"), Fault(2, RelationProperty.Unavailable), Fault(3, RelationProperty.MissingField)]);
        Assert.Equal(RelationProperty.Unavailable, bad.Value);
        Assert.Equal(new RelationComputedValue("", RelationProperty.Unavailable, "", null), bad.Typed);
    }

    [Fact]
    public void Lookup_joins_non_empty_labels_in_link_order()
    {
        var result = RelationComputedFold.Fold("lookup", "lookup", "; ", "{\"x\":1}",
            [Value(1, "Б", "Б"), Value(2, "", ""), Value(3, "А", "А")]);
        Assert.Equal("Б; А", result.Value);
        Assert.Equal("lookup", result.Typed.FieldType);
        Assert.Equal("{\"x\":1}", result.Typed.ConfigJson);
        var single = RelationComputedFold.Fold("lookup", "lookup", ", ", null, [Value(1, "1.5", "1,5", "number", "{}")]);
        Assert.Equal(new RelationComputedValue("1.5", "1,5", "number", "{}"), single.Typed);
        Assert.Equal("", RelationComputedFold.Fold("lookup", "lookup", ", ", null, []).Value);
    }

    [Theory]
    [InlineData("count", "0")]
    [InlineData("sum", "0")]
    [InlineData("avg", "")]
    [InlineData("min", "")]
    [InlineData("max", "")]
    [InlineData("earliest", "")]
    [InlineData("latest", "")]
    public void A_record_without_targets_gets_the_aggregate_default(string aggregate, string expected) =>
        Assert.Equal(expected, RelationComputedFold.Fold("rollup", aggregate, ", ", null, []).Value);

    [Fact]
    public void Rollup_keeps_an_invariant_number_and_the_date_type()
    {
        var sum = RelationComputedFold.Fold("rollup", "sum", ", ", null, [Value(1, "1,25", "1,25", "number"), Value(2, "10.5", "10,5", "number")]);
        Assert.Equal("11.75", sum.Value);
        Assert.Equal("number", sum.Typed.FieldType);
        var latest = RelationComputedFold.Fold("rollup", "latest", ", ", null, [Value(1, "2026-10-01", "", "date"), Value(2, "2026-10-02", "", "date")]);
        Assert.Equal("date", latest.Typed.FieldType);
        Assert.StartsWith("2026-10-02", latest.Value, StringComparison.Ordinal);
        Assert.Equal("(нечисловое значение)", RelationComputedFold.Fold("rollup", "sum", ", ", null, [Value(1, "abc", "abc")]).Value);
    }
}
