using SmartBase.Settings;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Реестр вкладок раздела «Интеграции» (<see cref="IntegrationTabs"/>).
///
/// Проверять тут почти нечего, кроме одного: незнакомый ключ не должен оставлять раздел
/// пустым. Ключ приходит из <c>app.db</c> — из памяти о последнем выборе, — и переживает
/// откат программы на выпуск, где такой вкладки еще нет.
/// </summary>
public sealed class IntegrationTabsTests
{
    [Fact]
    public void Registry_is_not_empty_and_keys_are_unique()
    {
        Assert.NotEmpty(IntegrationTabs.All);
        Assert.Equal(IntegrationTabs.All.Count, IntegrationTabs.All.Select(t => t.Key).Distinct().Count());
        Assert.All(IntegrationTabs.All, tab =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tab.Key));
            Assert.False(string.IsNullOrWhiteSpace(tab.Label));
        });
    }

    [Fact]
    public void Known_key_resolves_to_itself()
    {
        var first = IntegrationTabs.All[0];

        Assert.Equal(first, IntegrationTabs.Resolve(first.Key));
    }

    /// <summary>Незнакомый и пустой ключ дают первую вкладку, а не пустой раздел.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("вкладка-из-будущего")]
    public void Unknown_key_falls_back_to_the_first_tab(string? key)
    {
        Assert.Equal(IntegrationTabs.All[0], IntegrationTabs.Resolve(key));
    }
}
