using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

public sealed class RelationComputedConfigTests
{
    [Fact]
    public void Configuration_roundtrips_without_changing_selected_property_or_aggregate()
    {
        var cfg = new RelationComputedConfig(42, "63", "avg");
        Assert.Equal(cfg, RelationComputedConfig.FromConfig(cfg.ToConfigJson()));
        Assert.Equal(new RelationComputedConfig(42), RelationComputedConfig.FromConfig("{\"relationFieldId\":42}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"relationFieldId\":\"42\"}")]
    [InlineData("{\"relationFieldId\":true}")]
    [InlineData("{\"relationFieldId\":-1}")]
    [InlineData("{\"relationFieldId\":2147483648}")]
    [InlineData("{\"relationFieldId\":1,\"targetField\":\"no-property\"}")]
    [InlineData("{\"relationFieldId\":1,\"targetField\":false}")]
    [InlineData("{\"relationFieldId\":1,\"aggregate\":null}")]
    [InlineData("{\"relationFieldId\":1,\"aggregate\":\"median\"}")]
    public void Damaged_or_unknown_configuration_is_rejected_without_throwing(string? json) =>
        Assert.Null(RelationComputedConfig.FromConfig(json));

    [Theory]
    [InlineData("lookup")]
    [InlineData("rollup")]
    public void Related_computed_values_are_readonly_but_do_not_read_record_audit(string type)
    {
        Assert.True(FieldTypeInfo.IsComputed(type));
        Assert.True(FieldTypeInfo.IsRelationComputed(type));
        Assert.False(FieldTypeInfo.IsAuditComputed(type));
        Assert.Equal("", FieldTypeInfo.ComputedValue(new DbRecord { CreatedAt = "2026-01-01" }, type));
    }

    [Fact]
    public void A_lookup_that_looks_like_a_date_is_not_reformatted()
    {
        Assert.Equal("2026-01-01", FieldTypeInfo.FormatComputedDisplay("2026-01-01", "lookup"));
        Assert.Equal("1,5", FieldTypeInfo.FormatComputedDisplay("1.5", "rollup"));
    }

    [Fact]
    public void Rollup_filter_values_sort_numerically_in_both_directions()
    {
        var ascending = ColumnSort.BuildComparer("rollup", true);
        var descending = ColumnSort.BuildComparer("rollup", false);
        Assert.True(ascending("10", "2") > 0);
        Assert.True(descending("10", "2") < 0);
        Assert.True(ascending("", "2") > 0);
        Assert.True(descending("", "2") > 0);
    }

    [Theory]
    [InlineData("count", "3")]
    [InlineData("sum", "6.5")]
    [InlineData("avg", "3.25")]
    [InlineData("min", "2")]
    [InlineData("max", "4.5")]
    public void Aggregates_count_records_and_use_raw_numbers_in_both_decimal_formats(string aggregate, string expected) =>
        Assert.Equal(expected, RelationRollupValue.Calculate(aggregate, ["2", "4,5", ""]));

    [Fact]
    public void Numeric_aggregates_preserve_decimal_precision_and_report_bad_values()
    {
        Assert.Equal("0.3", RelationRollupValue.Calculate("sum", ["0.1", "0.2"]));
        Assert.Equal("(нечисловое значение)", RelationRollupValue.Calculate("sum", ["3", "ошибка"]));
        Assert.Equal("(слишком большое значение)", RelationRollupValue.Calculate("sum", [decimal.MaxValue.ToString(), "1"]));
        Assert.Equal("0", RelationRollupValue.Calculate("count", []));
        Assert.Equal("0", RelationRollupValue.Calculate("sum", []));
        Assert.Equal("", RelationRollupValue.Calculate("avg", []));
    }

    [Fact]
    public void Earliest_and_latest_dates_keep_the_whole_target_value()
    {
        string[] dates = ["2026-10-05/2026-10-20", "2026-10-03T14:00", "", "2026-10-18"];
        Assert.Equal("2026-10-03T14:00", RelationRollupValue.Calculate("earliest", dates));
        // Поздняя — по концу: диапазон до 20 октября позже даты 18 октября.
        Assert.Equal("2026-10-05/2026-10-20", RelationRollupValue.Calculate("latest", dates));
        Assert.Equal("", RelationRollupValue.Calculate("latest", ["", ""]));
        Assert.Equal("(не дата)", RelationRollupValue.Calculate("earliest", ["2026-10-01", "завтра"]));
    }

    [Fact]
    public void A_date_rollup_shows_and_sorts_as_a_date_and_a_number_stays_a_number()
    {
        Assert.Equal("03.10.2026 14:00", FieldTypeInfo.FormatComputedDisplay("2026-10-03T14:00", "rollup"));
        Assert.Equal("1,5", FieldTypeInfo.FormatComputedDisplay("1.5", "rollup"));
        var ascending = ColumnSort.BuildComparer("rollup", true);
        Assert.True(ascending("2026-10-20", "2026-09-30") > 0);
        Assert.True(ascending("2026-01-05", "") < 0);
        Assert.True(RelationComputedConfig.IsDateAggregate("latest") && !RelationComputedConfig.IsNumberAggregate("latest"));
        Assert.Equal(new RelationComputedConfig(5, "7", "earliest"),
            RelationComputedConfig.FromConfig(new RelationComputedConfig(5, "7", "earliest").ToConfigJson()));
    }
}
