using SmartBase.Extensions;
using SmartBase.Extensions.DocsCollection;

namespace SmartBase.Tests.Extensions.DocsCollection;

/// <summary>
/// Контракт ИМЕННО этого расширения: прибитый Id и факт регистрации. Общие правила живут в
/// Tests/Extensions/Registry — здесь только то, что уходит вместе с расширением при удалении.
///
/// CreateWindow() здесь не зовется и звать нельзя: Avalonia в тест-хосте не поднята.
/// </summary>
public sealed class DocsCollectionExtensionTests
{
    [Fact]
    public void Extension_id_is_frozen() =>
        Assert.Equal("docs-collection", new DocsCollectionExtension().Id);

    // Настроек расширение не объявляет: папку выбирают в самом окне, хранить нечего.
    [Fact]
    public void Extension_declares_no_settings() =>
        Assert.Empty(((IExtension)new DocsCollectionExtension()).Settings);

    [Fact]
    public void Extension_is_registered()
    {
        var found = ExtensionRegistry.FindById("docs-collection");

        Assert.NotNull(found);
        Assert.IsType<DocsCollectionExtension>(found);
    }
}
