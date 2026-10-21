using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

public sealed class RelationPropertyConfigTests
{
    [Fact]
    public void Old_config_keeps_title_and_new_selectors_roundtrip()
    {
        var old = RelationConfig.FromConfig("{\"targetCollectionId\":7,\"single\":false}")!;
        Assert.Equal(RelationConfig.TitleField, old.DisplayField);
        Assert.Equal(RelationConfig.TitleField, old.EffectiveSearchField);
        Assert.Null(old.Auto);
        var configured = old with { DisplayField = "21", SearchField = "22", Auto = new("3", "4"), TargetDbId = 9 };
        Assert.Equal(configured, RelationConfig.FromConfig(configured.ToConfigJson()));
        Assert.Equal("21", (configured with { SearchField = null }).EffectiveSearchField);
    }

    [Theory]
    [InlineData("{\"targetCollectionId\":\"7\"}")]
    [InlineData("{\"targetCollectionId\":true}")]
    [InlineData("{\"targetCollectionId\":[]}")]
    [InlineData("{\"targetCollectionId\":null}")]
    [InlineData("{\"targetCollectionId\":{}}")]
    [InlineData("{\"targetCollectionId\":7,\"targetDbId\":\"2\"}")]
    [InlineData("{\"targetCollectionId\":7,\"targetDbId\":[]}")]
    public void Wrong_target_json_types_are_incomplete_not_exceptions(string json) =>
        Assert.Null(RelationConfig.FromConfig(json));

    [Fact]
    public void Optional_malformed_settings_do_not_create_an_auto_rule()
    {
        var cfg = RelationConfig.FromConfig("{\"targetCollectionId\":7,\"displayField\":{},\"searchField\":false,\"auto\":{\"sourceField\":2,\"targetField\":\"8\"}}")!;
        Assert.Equal(RelationConfig.TitleField, cfg.DisplayField);
        Assert.Null(cfg.SearchField);
        Assert.Null(cfg.Auto);
        Assert.Equal(0, cfg.TargetDbId);
    }

    [Theory]
    [InlineData("2026-10-20", "date", "20.10.2026")]
    [InlineData("[\"A\",\"B\"]", "multiselect", "A, B")]
    [InlineData("/tmp/a,b.pdf", "file", "a,b.pdf")]
    [InlineData("0", "checkbox", "(Не отмечено)")]
    [InlineData("1", "checkbox", "✓")]
    [InlineData("Имя", "user", "Имя")]
    [InlineData("Статус", "status", "Статус")]
    public void Properties_use_user_visible_format_not_storage_syntax(string raw, string type, string display) =>
        Assert.Equal(display, RelationProperty.Format(raw, type));

    [Fact]
    public void Number_format_and_resolution_guard_are_shared()
    {
        Assert.Equal("42,50\u00a0%", RelationProperty.Format("42,5", "number",
            "{\"numberFormat\":\"percent\",\"decimals\":2}"));
        using (RelationPropertyResolution.Enter("first.db", 12))
        {
            Assert.Null(RelationPropertyResolution.Enter("first.db", 12));
            using var other = RelationPropertyResolution.Enter("second.db", 12);
            Assert.NotNull(other);
        }
        using var restored = RelationPropertyResolution.Enter("first.db", 12);
        Assert.NotNull(restored);
    }

    [Fact]
    public void Several_display_and_search_properties_roundtrip_and_old_versions_see_the_first()
    {
        var cfg = new RelationConfig(7, true, DisplayField: "21|__title__", SearchField: "22|23|21");
        var json = cfg.ToConfigJson();
        Assert.Equal(cfg, RelationConfig.FromConfig(json));
        Assert.Equal(["21", "__title__"], cfg.DisplayKeys);
        Assert.Equal(["22", "23", "21"], cfg.SearchKeys);
        // Прежняя версия читает только одиночные ключи и показывает первое свойство.
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("21", doc.RootElement.GetProperty("displayField").GetString());
        Assert.Equal("22", doc.RootElement.GetProperty("searchField").GetString());
        Assert.Equal(["21", "__title__", "22", "23"], cfg.TargetPropertyKeys);
    }

    [Fact]
    public void Property_lists_drop_bad_keys_duplicates_and_extra_items()
    {
        var cfg = RelationConfig.FromConfig(
            "{\"targetCollectionId\":7,\"displayField\":\"5\",\"displayFields\":[\"5\",\"x\",\"5\",6,\"7\",\"8\",\"9\"],\"searchFields\":[]}")!;
        Assert.Equal("5|7|8", cfg.DisplayField);
        Assert.Null(cfg.SearchField);
        Assert.Equal(["5", "7", "8"], cfg.SearchKeys);
    }

    [Fact]
    public void Delete_policy_is_written_only_when_deletion_is_restricted()
    {
        var plain = new RelationConfig(7, true);
        Assert.DoesNotContain("onDelete", plain.ToConfigJson(), StringComparison.Ordinal);
        Assert.False(RelationConfig.FromConfig(plain.ToConfigJson())!.RestrictDelete);
        var restricted = plain with { RestrictDelete = true };
        Assert.Contains("\"onDelete\":\"restrict\"", restricted.ToConfigJson(), StringComparison.Ordinal);
        Assert.Equal(restricted, RelationConfig.FromConfig(restricted.ToConfigJson()));
        // Незнакомое значение — обычное поведение «снять связь».
        Assert.False(RelationConfig.FromConfig("{\"targetCollectionId\":7,\"onDelete\":\"cascade\"}")!.RestrictDelete);
    }
}
