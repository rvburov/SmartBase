using SmartBase.Extensions;
using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Настройки расширений: раскладка ключей «ext.&lt;Id&gt;.&lt;имя&gt;», уборка строк от
/// расширений, которых в сборке больше нет, и уникальность Id в реестре. Всё это держит
/// отделимость расширения: удаление сводится к папке и строке реестра, лишнего не остаётся
/// ни в коде, ни в app.db (см. docs/features/extensions.md).
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

        AppSettings.SaveExtensionSetting("product-compare", "template", @"C:\шаблон.xlsm");

        Assert.True(store.Has("ext.product-compare.template"));
        Assert.Equal(@"C:\шаблон.xlsm", AppSettings.LoadExtensionSetting("product-compare", "template"));
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
        AppSettings.SaveExtensionSetting("product-compare", "template", @"C:\шаблон.xlsm");

        AppSettings.SaveExtensionSetting("product-compare", "template", "   ");

        Assert.False(store.Has("ext.product-compare.template"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Settings_Of_Different_Extensions_Do_Not_Collide()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);

        AppSettings.SaveExtensionSetting("product-compare", "template", "первый");
        AppSettings.SaveExtensionSetting("assembly-calc",   "template", "второй");

        Assert.Equal("первый", AppSettings.LoadExtensionSetting("product-compare", "template"));
        Assert.Equal("второй", AppSettings.LoadExtensionSetting("assembly-calc",   "template"));
    }

    // ── Уборка после удаления расширения ──────────────────────────────────────

    [Fact]
    public void Orphan_Settings_Are_Removed_And_Live_Ones_Kept()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        AppSettings.SaveExtensionSetting("живое",  "template", "остаётся");
        AppSettings.SaveExtensionSetting("удалённое", "template", "уходит");
        AppSettings.SaveExtensionSetting("удалённое", "иное",     "тоже уходит");

        var removed = AppSettings.RemoveOrphanExtensionSettings(["живое"]);

        Assert.Equal(2, removed);
        Assert.True(store.Has("ext.живое.template"));
        Assert.False(store.Has("ext.удалённое.template"));
        Assert.False(store.Has("ext.удалённое.иное"));
    }

    [Fact]
    public void Cleanup_Does_Not_Touch_Other_Settings()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        store.Set("scale", "1.2");
        store.Set("startupModule", "calendar");
        AppSettings.SaveExtensionSetting("удалённое", "template", "уходит");

        AppSettings.RemoveOrphanExtensionSettings([]);

        Assert.True(store.Has("scale"));
        Assert.True(store.Has("startupModule"));
        Assert.False(store.Has("ext.удалённое.template"));
    }

    // Id сверяется по полной приставке «ext.<id>.», поэтому расширение с похожим началом
    // имени чужие настройки не уносит.
    [Fact]
    public void Similar_Id_Prefix_Is_Not_Treated_As_Live()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        AppSettings.SaveExtensionSetting("pdf-rename",    "mode", "остаётся");
        AppSettings.SaveExtensionSetting("pdf-rename-v2", "mode", "уходит");

        var removed = AppSettings.RemoveOrphanExtensionSettings(["pdf-rename"]);

        Assert.Equal(1, removed);
        Assert.True(store.Has("ext.pdf-rename.mode"));
        Assert.False(store.Has("ext.pdf-rename-v2.mode"));
    }

    // ── Реестр ────────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_Ids_Are_Unique()
    {
        ExtensionRegistry.EnsureUniqueIds();   // не бросает — Id в реестре не повторяются

        var ids = ExtensionRegistry.All.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // Настройки живых расширений уборка не трогает: их Id приходят из того же реестра.
    [Fact]
    public void Cleanup_Keeps_Settings_Of_Registered_Extensions()
    {
        var store = new FakeStore();
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

    // Настройка объявлена — значит у неё есть непустые Key и Label: по ним строится поле
    // в разделе «Расширения», без них строка окажется безымянной.
    [Fact]
    public void Declared_Settings_Have_Key_And_Label()
    {
        foreach (var extension in ExtensionRegistry.All)
        {
            var keys = extension.Settings.Select(s => s.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());

            foreach (var setting in extension.Settings)
            {
                Assert.False(string.IsNullOrWhiteSpace(setting.Key));
                Assert.False(string.IsNullOrWhiteSpace(setting.Label));
            }
        }
    }
}
