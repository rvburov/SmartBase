using SmartBase.Extensions;
using SmartBase.Extensions.ProductCompare;

namespace SmartBase.Tests.Extensions.ProductCompare;

/// <summary>
/// Контракт ИМЕННО этого расширения: прибитые Id и ключ настройки плюс факт регистрации.
/// Общие правила живут в Tests/Extensions/Registry — здесь только то, что уходит вместе
/// с расширением при удалении.
///
/// Ключи настроек прибиты потому, что они часть ХРАНИЛИЩА («ext.product-compare.template»
/// в app.db): переименование молча теряет сохраненный пользователем путь. Шаблон заключения
/// остался под ключом «template» времен одного режима — так сохраненный путь не потерялся.
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

        Assert.Equal(["template", "breakdown-template", "breakdown-folder"], keys);
        Assert.Equal("template",           ProductCompareExtension.TemplateKey);
        Assert.Equal("breakdown-template", ProductCompareExtension.BreakdownTemplateKey);
        Assert.Equal("breakdown-folder",   ProductCompareExtension.BreakdownFolderKey);
        Assert.Equal("mode",               ProductCompareExtension.ModeKey);
    }

    [Fact]
    public void Extension_is_registered()
    {
        var found = ExtensionRegistry.FindById("product-compare");

        Assert.NotNull(found);
        Assert.IsType<ProductCompareExtension>(found);
    }
}
