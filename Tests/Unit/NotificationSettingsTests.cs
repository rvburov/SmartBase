using System.Globalization;
using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Настройки уведомлений в app.db через <see cref="AppSettings"/>: тумблер хранится дельтой
/// (в БД лежит только отличие от дефолта «включено»), водяная метка круго переживает
/// сохранение/чтение. Инвариант дельт — как у остальных настроек ([[settings-architecture]]).
/// </summary>
public sealed class NotificationSettingsTests
{
    private sealed class FakeStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _d = new();
        public bool Has(string key) => _d.ContainsKey(key);
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _d[key] = value;
        public void Remove(string key) => _d.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            _d.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    [Fact]
    public void Notifications_enabled_by_default_without_stored_value()
    {
        AppSettings.AttachStore(new FakeStore());
        Assert.True(AppSettings.LoadNotificationsEnabled());
    }

    [Fact]
    public void Disabling_persists_a_delta_and_reads_back()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);

        AppSettings.SaveNotificationsEnabled(false);
        Assert.True(store.Has("notificationsEnabled"));   // отличие от дефолта — хранится
        Assert.False(AppSettings.LoadNotificationsEnabled());
    }

    [Fact]
    public void Saving_default_value_removes_the_key()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);

        AppSettings.SaveNotificationsEnabled(false);
        AppSettings.SaveNotificationsEnabled(true);       // назад к дефолту
        Assert.False(store.Has("notificationsEnabled"));  // ключа быть не должно
        Assert.True(AppSettings.LoadNotificationsEnabled());
    }

    [Fact]
    public void Watermark_round_trips_exactly()
    {
        AppSettings.AttachStore(new FakeStore());

        var instant = new DateTime(2026, 7, 1, 12, 30, 45);
        AppSettings.SaveNotifyWatermark(instant);

        Assert.Equal(instant, AppSettings.LoadNotifyWatermark());
    }

    [Fact]
    public void Watermark_is_null_when_never_saved()
    {
        AppSettings.AttachStore(new FakeStore());
        Assert.Null(AppSettings.LoadNotifyWatermark());
    }

    [Fact]
    public void Watermark_stored_in_invariant_roundtrip_format()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);

        var instant = new DateTime(2026, 12, 31, 23, 59, 0);
        AppSettings.SaveNotifyWatermark(instant);

        // Формат "o" — стабилен независимо от локали машины.
        Assert.Equal(instant.ToString("o", CultureInfo.InvariantCulture), store.Get("notifyWatermark"));
    }
}
