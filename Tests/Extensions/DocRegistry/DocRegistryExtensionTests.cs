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
    /// Пять настроек, и порядок значим — так они и лягут полями на экране: сперва то, что
    /// вводят руками (коды организации, ФИО), затем то, что ведет само окно (база, коллекция),
    /// последней — список тех, кто регистрирует по сообщениям: окно ее не читает.
    /// </summary>
    [Fact]
    public void Settings_are_codes_fio_and_the_target()
    {
        var keys = ((IExtension)new DocRegistryExtension()).Settings.Select(s => s.Key).ToList();

        Assert.Equal([
            DocRegistryExtension.OrgCodesKey,
            DocRegistryExtension.FioKey,
            DocRegistryExtension.DatabaseKey,
            DocRegistryExtension.CollectionKey,
            DocRegistryExtension.SendersKey,
        ], keys);
    }

    /// <summary>Ключи уходят в app.db приставкой «ext.doc-registry.&lt;Key&gt;» и должны быть
    /// разными: тезки читали бы чужое значение.</summary>
    [Fact]
    public void Setting_keys_are_unique()
    {
        var keys = ((IExtension)new DocRegistryExtension()).Settings.Select(s => s.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>У каждой настройки есть подпись — иначе поле на экране осталось бы безымянным.</summary>
    [Fact]
    public void Setting_labels_are_not_empty() =>
        Assert.All(((IExtension)new DocRegistryExtension()).Settings,
                   s => Assert.False(string.IsNullOrWhiteSpace(s.Label)));

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
