using SmartBase.Extensions;
using SmartBase.Utils;

namespace SmartBase.Tests.Extensions.Registry;

/// <summary>
/// Контракт объявленных настроек по всем расширениям. Раздел «Настройки → Расширения» строится
/// целиком из <see cref="IExtension.Settings"/>, а значения хранит оболочка под ключом
/// «ext.&lt;Id&gt;.&lt;Key&gt;» — поэтому кривое объявление ломает не расширение, а общий экран.
///
/// Сам МЕХАНИЗМ хранения (раскладка ключей, уборка сирот) — забота ядра и проверяется в
/// Tests/Unit/ExtensionSettingsTests.cs. Здесь — только то, что зависит от состава реестра.
/// </summary>
public sealed class ExtensionSettingsContractTests
{
    // Настройка объявлена — значит у неё есть непустые Key и Label: по ним строится поле,
    // без них строка окажется безымянной.
    [Fact]
    public void Declared_settings_have_key_and_label()
    {
        foreach (var extension in ExtensionRegistry.All)
            foreach (var setting in extension.Settings)
            {
                Assert.False(string.IsNullOrWhiteSpace(setting.Key),   $"Пустой ключ настройки у {extension.Id}");
                Assert.False(string.IsNullOrWhiteSpace(setting.Label), $"Пустая подпись настройки у {extension.Id}");
            }
    }

    // Два одинаковых Key — два поля на одно значение: правка в одном молча меняет второе.
    [Fact]
    public void Setting_keys_are_unique_within_an_extension()
    {
        foreach (var extension in ExtensionRegistry.All)
        {
            var keys = extension.Settings.Select(s => s.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        }
    }

    // Точка в Key ломает разбор ключа «ext.<Id>.<Key>» так же, как точка в Id.
    [Fact]
    public void Setting_keys_contain_no_dot()
    {
        foreach (var extension in ExtensionRegistry.All)
            foreach (var setting in extension.Settings)
                Assert.DoesNotContain('.', setting.Key);
    }

    // Дефолт интерфейса (Settings => []) обязан отдавать пустой список, а не null: раздел
    // настроек обходит его без проверок.
    [Fact]
    public void Extension_without_settings_declares_an_empty_list()
    {
        foreach (var extension in ExtensionRegistry.All)
            Assert.NotNull(extension.Settings);
    }

    // Уборка сирот получает Id из того же реестра — значит ни одной живой настройки не сносит.
    [Fact]
    public void Cleanup_keeps_settings_of_registered_extensions()
    {
        var store = new FakeSettingsStore();
        AppSettings.AttachStore(store);
        foreach (var extension in ExtensionRegistry.All)
            foreach (var setting in extension.Settings)
                AppSettings.SaveExtensionSetting(extension.Id, setting.Key, "значение");

        var before  = store.Count;
        var removed = AppSettings.RemoveOrphanExtensionSettings(
            ExtensionRegistry.All.Select(e => e.Id));

        Assert.Equal(0, removed);
        Assert.Equal(before, store.Count);
    }
}
