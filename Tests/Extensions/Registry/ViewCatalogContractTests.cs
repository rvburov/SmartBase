using SmartBase.Extensions;
using SmartBase.Settings;

namespace SmartBase.Tests.Extensions.Registry;

/// <summary>
/// Каталог видов (<see cref="ViewCatalog"/>, «Оформление → Отображение раздела Расширения»)
/// отражает реестр: пункты раздела берутся из <see cref="ExtensionRegistry.All"/>, а не
/// перечислены в каталоге. Проверка живет здесь, а не в тестах ядра: те не обращаются ни к
/// конкретным расширениям, ни к реестру, который их создает (сторож «Ядро не знает про
/// расширения» в CI), а правила самого
/// каталога — скрытие пунктов, уборка сирот — проверяются в Tests/Unit/ViewCatalogTests.cs на
/// вымышленных ключах и от состава реестра не зависят.
/// </summary>
public sealed class ViewCatalogContractTests
{
    [Fact]
    public void Extensions_scope_mirrors_the_registry()
    {
        var items = ViewCatalog.All(ViewCatalog.ScopeExtensions);

        Assert.Equal(ExtensionRegistry.All.Select(e => e.Id),   items.Select(v => v.Key));
        Assert.Equal(ExtensionRegistry.All.Select(e => e.Name), items.Select(v => v.Label));
    }
}
