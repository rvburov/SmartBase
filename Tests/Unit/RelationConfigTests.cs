using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Настройка поля-связи (тип «relation»): разбор/сериализация ConfigJson (целевая коллекция +
/// one/many). Без целевой коллекции правило неполно (null).
/// </summary>
public sealed class RelationConfigTests
{
    [Fact]
    public void Roundtrips_through_config_json()
    {
        var cfg  = new RelationConfig(42, Single: true, Separator: "semicolon");
        var back = RelationConfig.FromConfig(cfg.ToConfigJson());
        Assert.Equal(cfg, back);
        Assert.Equal("; ", back!.SeparatorString);   // ключ → строка склейки
    }

    [Fact]
    public void Defaults_separator_to_comma_when_absent()
    {
        var cfg = RelationConfig.FromConfig("{\"targetCollectionId\":7}");
        Assert.Equal("comma", cfg!.Separator);
        Assert.Equal(", ", cfg.SeparatorString);
    }

    [Fact]
    public void Defaults_single_to_false_when_absent()
    {
        var cfg = RelationConfig.FromConfig("{\"targetCollectionId\":7}");
        Assert.NotNull(cfg);
        Assert.Equal(7, cfg!.TargetCollectionId);
        Assert.False(cfg.Single);   // «Несколько» по умолчанию
    }

    [Theory]
    [InlineData("{}")]                              // нет цели
    [InlineData("{\"targetCollectionId\":0}")]      // цель 0 (не выбрана)
    [InlineData("{\"targetCollectionId\":-3}")]     // отрицательная
    [InlineData("не json")]
    [InlineData(null)]
    public void Incomplete_config_is_null(string? json) =>
        Assert.Null(RelationConfig.FromConfig(json));
}
