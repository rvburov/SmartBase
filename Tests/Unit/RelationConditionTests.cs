using System.Globalization;
using System.Text.Json;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>Условие выбора записей связи: сравнения по типам, разбор и запись настройки.</summary>
public sealed class RelationConditionTests
{
    private static RelationCondition C(string op, params string[] values) => new("5", op, values);

    [Theory]
    [InlineData("eq", "Активен", "  АКТИВЕН ", true)]
    [InlineData("eq", "Активен", "Активный", false)]
    [InlineData("ne", "Активен", "активен", false)]
    [InlineData("ne", "Активен", "", true)]
    [InlineData("contains", "ооо", "ООО «Ромашка»", true)]
    [InlineData("contains", "ип", "ООО «Ромашка»", false)]
    [InlineData("empty", null, "   ", true)]
    [InlineData("notEmpty", null, "x", true)]
    [InlineData("notEmpty", null, "", false)]
    public void Text_is_compared_without_case_and_outer_spaces(string op, string? value, string raw, bool expected) =>
        Assert.Equal(expected, C(op, value is null ? [] : [value]).Matches(raw, "text"));

    [Fact]
    public void Several_text_values_are_joined_with_or_and_blank_ones_are_ignored()
    {
        Assert.True(C("eq", "ООО", "ИП").Matches(" ип ", "text"));
        Assert.False(C("ne", "ООО", "ИП").Matches("ИП", "text"));
        Assert.True(C("ne", "ООО", "ИП").Matches("АО", "text"));
        Assert.True(C("contains", "ромаш", "лют").Matches("ООО «Лютик»", "text"));
        Assert.False(C("contains", "ромаш", "  ").Matches("ООО «Лютик»", "text"));
    }

    [Fact]
    public void Text_comparison_does_not_depend_on_the_current_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.True(C("eq", "TITLE").Matches("title", "text"));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Lists_are_compared_exactly_and_accept_several_items()
    {
        var active = C("eq", "Активен", "На продлении");
        Assert.True(active.Matches("На продлении", "status"));
        Assert.False(active.Matches("активен", "status"));
        Assert.False(active.Matches("", "status"));
        Assert.True(C("ne", "Закрыт").Matches("", "select"));
        Assert.False(C("ne", "Закрыт").Matches("Закрыт", "select"));
        Assert.True(C("empty").Matches("", "user"));
        // «Содержит» у списка не бывает: такое условие сломано, а не ложно.
        Assert.Null(C("contains", "Акт").Matches("Активен", "status"));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    public void A_checkbox_is_checked_or_not(string raw, bool isChecked)
    {
        Assert.Equal(isChecked, C("notEmpty").Matches(raw, "checkbox"));
        Assert.Equal(!isChecked, C("empty").Matches(raw, "checkbox"));
        Assert.Null(C("eq", "1").Matches(raw, "checkbox"));
    }

    [Fact]
    public void Unsupported_types_break_the_condition()
    {
        Assert.Null(C("eq", "5").Matches("5", "number"));
        Assert.Null(C("notEmpty").Matches("x", "date"));
        Assert.Empty(RelationCondition.OpsFor("relation"));
        Assert.Equal("text", RelationCondition.TypeOf(RelationConfig.TitleField, null));
    }

    [Fact]
    public void A_condition_is_described_for_the_user()
    {
        Assert.Equal("«Статус» равно «Активен» или «На продлении»",
            C("eq", "Активен", "На продлении").Describe("Статус", "status"));
        Assert.Equal("«Важный» отмечено", C("notEmpty").Describe("Важный", "checkbox"));
        Assert.Equal("«ИНН» заполнено", C("notEmpty").Describe("ИНН", "text"));
        var all = string.Concat(RelationCondition.OpsFor("text").Concat(RelationCondition.OpsFor("checkbox"))
            .Select(op => RelationCondition.Label(op, "checkbox") + RelationCondition.Label(op, "text")));
        Assert.DoesNotContain('\u0451', all);
    }

    [Fact]
    public void A_config_without_a_condition_stays_byte_for_byte_the_same()
    {
        const string json = "{\"targetCollectionId\":7,\"single\":true,\"separator\":\"comma\"}";
        Assert.Equal(json, RelationConfig.FromConfig(json)!.ToConfigJson());
    }

    [Fact]
    public void A_condition_roundtrips_and_survives_with()
    {
        var cfg = new RelationConfig(7, true, Where: [C("eq", "Активен", "На продлении"),
            new RelationCondition(RelationConfig.TitleField, "contains", ["ООО"]), new RelationCondition("6", "notEmpty", [])]);
        var json = cfg.ToConfigJson();
        var back = RelationConfig.FromConfig(json)!;
        Assert.Equal(cfg, back);
        Assert.Equal(cfg.Where, (back with { TargetDbId = 1_234_567 }).Where);
        Assert.Contains("5", cfg.TargetPropertyKeys);
        Assert.Contains("6", cfg.TargetPropertyKeys);
        Assert.EndsWith("]}", json);   // «where» — последним ключом
    }

    [Theory]
    [InlineData("{\"field\":\"5\",\"op\":\"between\",\"values\":[\"1\",\"2\"]}")]
    [InlineData("{\"field\":\"5\",\"op\":\"eq\",\"values\":[\"a\"],\"since\":\"2027\"}")]
    [InlineData("{\"field\":\"x\",\"op\":\"eq\",\"values\":[\"a\"]}")]
    [InlineData("{\"field\":\"5\",\"op\":\"eq\",\"values\":[]}")]
    [InlineData("{\"field\":\"5\",\"op\":\"empty\",\"values\":[\"a\"]}")]
    [InlineData("{\"field\":\"5\",\"op\":\"eq\",\"values\":[5]}")]
    public void An_unknown_or_damaged_condition_is_kept_and_closes_the_choice(string item)
    {
        var json = $"{{\"targetCollectionId\":7,\"single\":true,\"separator\":\"comma\",\"where\":[{item}]}}";
        var cfg = RelationConfig.FromConfig(json)!;
        Assert.Null(cfg.Where);
        Assert.NotNull(cfg.WhereUnreadable);
        Assert.True(cfg.HasCondition);
        // Записывается обратно по смыслу без изменений.
        using var original = JsonDocument.Parse(json);
        using var written = JsonDocument.Parse(cfg.ToConfigJson());
        Assert.Equal(original.RootElement.GetProperty("where").GetRawText(),
            written.RootElement.GetProperty("where").GetRawText());
    }

    [Fact]
    public void More_than_five_conditions_or_too_long_values_are_unreadable()
    {
        var six = string.Join(",", Enumerable.Range(1, 6).Select(i => $"{{\"field\":\"{i}\",\"op\":\"notEmpty\"}}"));
        Assert.NotNull(RelationConfig.FromConfig($"{{\"targetCollectionId\":7,\"where\":[{six}]}}")!.WhereUnreadable);
        var longValue = new string('я', RelationCondition.MaxValueLength + 1);
        Assert.NotNull(RelationConfig.FromConfig(
            $"{{\"targetCollectionId\":7,\"where\":[{{\"field\":\"5\",\"op\":\"eq\",\"values\":[\"{longValue}\"]}}]}}")!.WhereUnreadable);
    }
}
