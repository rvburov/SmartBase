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

    // ── Уведомления по задачам ────────────────────────────────────────────────

    [Fact]
    public void Task_notifications_are_off_by_default()
    {
        // Уведомления по задачам осмысленны в общей базе с несколькими людьми — включает их
        // тот, кому они нужны. Календарные по той же логике включены (полезны и одному).
        AppSettings.AttachStore(new FakeStore());
        Assert.False(AppSettings.LoadNotifyTaskAssigned());
        Assert.False(AppSettings.LoadNotifyTaskStatus());
        Assert.True(AppSettings.LoadNotificationsEnabled());
    }

    [Fact]
    public void Task_toggles_are_stored_as_deltas_and_are_independent()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);

        AppSettings.SaveNotifyTaskAssigned(true);
        Assert.True(store.Has("notifyTaskAssigned"));       // отличие от дефолта — хранится
        Assert.True(AppSettings.LoadNotifyTaskAssigned());
        Assert.False(AppSettings.LoadNotifyTaskStatus());   // сосед не задет
        Assert.False(store.Has("notifyTaskStatus"));

        AppSettings.SaveNotifyTaskAssigned(false);          // назад к дефолту
        Assert.False(store.Has("notifyTaskAssigned"));
    }

    [Fact]
    public void Task_event_cursors_round_trip_per_database()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);

        AppSettings.SaveTaskEventCursors(new Dictionary<int, long> { [2] = 17, [5] = 4 });
        var back = AppSettings.LoadTaskEventCursors();

        Assert.Equal(17, back[2]);
        Assert.Equal(4, back[5]);
        // Порядок фиксированный: иначе один и тот же набор писался бы каждый раз новой строкой.
        Assert.Equal("2:17;5:4", store.Get("notifyTaskCursors"));
    }

    [Fact]
    public void Task_event_cursors_survive_a_broken_value()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        store.Set("notifyTaskCursors", "3:9;мусор;;7:x;4:12");

        var back = AppSettings.LoadTaskEventCursors();

        Assert.Equal(9, back[3]);
        Assert.Equal(12, back[4]);
        Assert.Equal(2, back.Count);
    }

    [Fact]
    public void Empty_cursor_set_removes_the_key()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);

        AppSettings.SaveTaskEventCursors(new Dictionary<int, long> { [1] = 5 });
        AppSettings.SaveTaskEventCursors(new Dictionary<int, long>());

        Assert.False(store.Has("notifyTaskCursors"));
        Assert.Empty(AppSettings.LoadTaskEventCursors());
    }
}
