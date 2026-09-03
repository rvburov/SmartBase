using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Механизм хранения настроек расширений: раскладка ключей «ext.&lt;Id&gt;.&lt;имя&gt;» и уборка
/// строк от расширений, которых в сборке больше нет. Это часть ОБОЛОЧКИ (AppSettings): она
/// работает при нуле расширений и обязана работать после удаления любого из них, поэтому
/// тесты живут в ядре и намеренно НЕ называют ни одного настоящего расширения — иначе
/// удаленное расширение осталось бы упомянутым здесь.
///
/// Инварианты, которые зависят от состава реестра (уникальность Id, объявленные настройки),
/// проверяются отдельно — Tests/Extensions/Registry/, см. docs/developer/testing/extension-tests.md.
/// Общая картина механизма — docs/developer/features/extensions/overview.md.
/// </summary>
public sealed class ExtensionSettingsTests
{
    private sealed class FakeStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _d = new();
        public bool Has(string key) => _d.ContainsKey(key);
        public int Count => _d.Count;
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _d[key] = value;
        public void Remove(string key) => _d.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            _d.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    // ── Хранение ──────────────────────────────────────────────────────────────

    [Fact]
    public void Setting_Is_Stored_Under_Prefixed_Key()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);

        AppSettings.SaveExtensionSetting("расш-1", "template", @"C:\шаблон.xlsm");

        Assert.True(store.Has("ext.расш-1.template"));
        Assert.Equal(@"C:\шаблон.xlsm", AppSettings.LoadExtensionSetting("расш-1", "template"));
    }

    [Fact]
    public void Missing_Setting_Reads_As_Empty()
    {
        AppSettings.AttachStore(new FakeStore());

        Assert.Equal("", AppSettings.LoadExtensionSetting("нет-такого", "template"));
    }

    [Fact]
    public void Empty_Value_Removes_The_Key()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        AppSettings.SaveExtensionSetting("расш-1", "template", @"C:\шаблон.xlsm");

        AppSettings.SaveExtensionSetting("расш-1", "template", "   ");

        Assert.False(store.Has("ext.расш-1.template"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Settings_Of_Different_Extensions_Do_Not_Collide()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);

        AppSettings.SaveExtensionSetting("расш-1", "template", "первый");
        AppSettings.SaveExtensionSetting("расш-2", "template", "второй");

        Assert.Equal("первый", AppSettings.LoadExtensionSetting("расш-1", "template"));
        Assert.Equal("второй", AppSettings.LoadExtensionSetting("расш-2", "template"));
    }

    // ── Уборка после удаления расширения ──────────────────────────────────────

    [Fact]
    public void Orphan_Settings_Are_Removed_And_Live_Ones_Kept()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        AppSettings.SaveExtensionSetting("живое",  "template", "остается");
        AppSettings.SaveExtensionSetting("удаленное", "template", "уходит");
        AppSettings.SaveExtensionSetting("удаленное", "иное",     "тоже уходит");

        var removed = AppSettings.RemoveOrphanExtensionSettings(["живое"]);

        Assert.Equal(2, removed);
        Assert.True(store.Has("ext.живое.template"));
        Assert.False(store.Has("ext.удаленное.template"));
        Assert.False(store.Has("ext.удаленное.иное"));
    }

    [Fact]
    public void Cleanup_Does_Not_Touch_Other_Settings()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        store.Set("scale", "1.2");
        store.Set("startupModule", "calendar");
        AppSettings.SaveExtensionSetting("удаленное", "template", "уходит");

        AppSettings.RemoveOrphanExtensionSettings([]);

        Assert.True(store.Has("scale"));
        Assert.True(store.Has("startupModule"));
        Assert.False(store.Has("ext.удаленное.template"));
    }

    // Id сверяется по полной приставке «ext.<id>.», поэтому расширение с похожим началом
    // имени чужие настройки не уносит.
    [Fact]
    public void Similar_Id_Prefix_Is_Not_Treated_As_Live()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        AppSettings.SaveExtensionSetting("живое",    "mode", "остается");
        AppSettings.SaveExtensionSetting("живое-v2", "mode", "уходит");

        var removed = AppSettings.RemoveOrphanExtensionSettings(["живое"]);

        Assert.Equal(1, removed);
        Assert.True(store.Has("ext.живое.mode"));
        Assert.False(store.Has("ext.живое-v2.mode"));
    }
}
