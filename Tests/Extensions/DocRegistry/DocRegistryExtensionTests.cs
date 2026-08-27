using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Контракт ИМЕННО этого расширения: прибитый Id, объявленная настройка и разбор кодов
/// организации. Общие правила живут в Tests/Extensions/Registry — здесь только то, что уходит
/// вместе с расширением при удалении.
///
/// CreateWindow() здесь не зовется и звать нельзя: Avalonia в тест-хосте не поднята.
/// </summary>
public sealed class DocRegistryExtensionTests
{
    [Fact]
    public void Extension_id_is_frozen() =>
        Assert.Equal("doc-registry", new DocRegistryExtension().Id);

    [Fact]
    public void Extension_is_registered()
    {
        var found = ExtensionRegistry.FindById("doc-registry");

        Assert.NotNull(found);
        Assert.IsType<DocRegistryExtension>(found);
    }

    /// <summary>
    /// Настроек ровно две, и обе — то, что расширению негде взять самому: коды организации
    /// (у каждого предприятия свои) и ФИО регистратора (у машины одно и меняется редко).
    /// Выбранная коллекция настройкой НЕ объявлена намеренно — ее выбирает само окно.
    /// </summary>
    [Fact]
    public void Settings_are_organisation_codes_and_fio()
    {
        var keys = ((IExtension)new DocRegistryExtension()).Settings.Select(s => s.Key).ToList();

        Assert.Equal([DocRegistryExtension.OrgCodesKey, DocRegistryExtension.FioKey], keys);
        Assert.DoesNotContain(DocRegistryExtension.LastCollectionKey, keys);
    }

    // ── Разбор строки кодов организации ───────────────────────────────────────

    [Fact]
    public void Codes_are_split_by_comma() =>
        Assert.Equal(["АБВГ", "ГМБВ"], DocRegistryExtension.ParseOrgCodes("АБВГ, ГМБВ"));

    [Fact]
    public void Spaces_and_empty_parts_are_dropped() =>
        Assert.Equal(["АБВГ", "ГМБВ"], DocRegistryExtension.ParseOrgCodes("  АБВГ ,, ГМБВ ,  "));

    [Fact]
    public void Semicolon_and_newline_split_too() =>
        Assert.Equal(["АБВГ", "ГМБВ", "ЖЗИК"], DocRegistryExtension.ParseOrgCodes("АБВГ;ГМБВ\nЖЗИК"));

    /// <summary>Повтор в настройке — опечатка: два одинаковых пункта в списке не нужны.</summary>
    [Fact]
    public void Repeats_are_dropped() =>
        Assert.Equal(["АБВГ"], DocRegistryExtension.ParseOrgCodes("АБВГ, абвг"));

    /// <summary>Порядок — как ввел человек: первый код станет выбранным по умолчанию.</summary>
    [Fact]
    public void Order_is_kept() =>
        Assert.Equal(["ГМБВ", "АБВГ"], DocRegistryExtension.ParseOrgCodes("ГМБВ, АБВГ"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",;")]
    [InlineData(null)]
    public void Nothing_configured_gives_no_codes(string? raw) =>
        Assert.Empty(DocRegistryExtension.ParseOrgCodes(raw));
}
