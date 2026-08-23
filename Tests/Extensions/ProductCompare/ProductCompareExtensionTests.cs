using SmartBase.Extensions;
using SmartBase.Extensions.ProductCompare;

namespace SmartBase.Tests.Extensions.ProductCompare;

/// <summary>
/// Контракт ИМЕННО этого расширения: прибитые Id и ключ настройки плюс факт регистрации.
/// Общие правила живут в Tests/Extensions/Registry — здесь только то, что уходит вместе
/// с расширением при удалении.
///
/// Ключ настройки прибит потому, что он часть ХРАНИЛИЩА («ext.product-compare.template»
/// в app.db): переименование молча теряет сохраненный пользователем путь к шаблону.
///
/// CreateWindow() здесь не зовется и звать нельзя: Avalonia в тест-хосте не поднята.
/// </summary>
public sealed class ProductCompareExtensionTests
{
    [Fact]
    public void Extension_id_is_frozen()
    {
        Assert.Equal("product-compare", new ProductCompareExtension().Id);
        Assert.Equal("product-compare", ProductCompareExtension.ExtensionId);
    }

    [Fact]
    public void Setting_keys_are_frozen()
    {
        var keys = ((IExtension)new ProductCompareExtension()).Settings.Select(s => s.Key).ToArray();

        Assert.Equal(["template"], keys);
        Assert.Equal("template", ProductCompareExtension.TemplateKey);
    }

    [Fact]
    public void Extension_is_registered()
    {
        var found = ExtensionRegistry.FindById("product-compare");

        Assert.NotNull(found);
        Assert.IsType<ProductCompareExtension>(found);
    }
}
