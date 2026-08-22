using SmartBase.Extensions;
using SmartBase.Extensions.AssemblyCalc;

namespace SmartBase.Tests.Extensions.AssemblyCalc;

/// <summary>
/// Контракт ИМЕННО этого расширения: прибитые Id и ключ настройки плюс факт регистрации.
/// Общие правила живут в Tests/Extensions/Registry — здесь только то, что уходит вместе
/// с расширением при удалении.
///
/// Ключ настройки прибит потому, что он часть ХРАНИЛИЩА («ext.assembly-calc.template»
/// в app.db): переименование молча теряет сохранённый пользователем путь к шаблону.
///
/// CreateWindow() здесь не зовётся и звать нельзя: Avalonia в тест-хосте не поднята.
/// </summary>
public sealed class AssemblyCalcExtensionTests
{
    [Fact]
    public void Extension_id_is_frozen()
    {
        Assert.Equal("assembly-calc", new AssemblyCalcExtension().Id);
        Assert.Equal("assembly-calc", AssemblyCalcExtension.ExtensionId);
    }

    [Fact]
    public void Setting_keys_are_frozen()
    {
        var keys = ((IExtension)new AssemblyCalcExtension()).Settings.Select(s => s.Key).ToArray();

        Assert.Equal(["template"], keys);
        Assert.Equal("template", AssemblyCalcExtension.TemplateKey);
    }

    [Fact]
    public void Extension_is_registered()
    {
        var found = ExtensionRegistry.FindById("assembly-calc");

        Assert.NotNull(found);
        Assert.IsType<AssemblyCalcExtension>(found);
    }
}
