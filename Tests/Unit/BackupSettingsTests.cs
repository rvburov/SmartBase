using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Настройки резервного копирования в AppSettings: инвариант «в БД хранятся только
/// отличия от стандарта» (дефолт = отсутствие ключа), валидация мусора, ключи на базу
/// с приставкой «backup.&lt;dbId&gt;.» и уборка осиротевших ключей отключенных баз.
/// Отметка lastRun — состояние, а не настройка: пишется всегда.
/// </summary>
public sealed class BackupSettingsTests
{
    private sealed class FakeStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _d = new();
        public bool Has(string key) => _d.ContainsKey(key);
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _d[key] = value;
        public void Remove(string key) => _d.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            [.. _d.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))];
    }

    // ── Папка копий ───────────────────────────────────────────────────────────

    [Fact]
    public void Folder_survives_save_and_load()
    {
        AppSettings.AttachStore(new FakeStore());
        AppSettings.SaveBackupFolder("/tmp/backups");
        Assert.Equal("/tmp/backups", AppSettings.LoadBackupFolder());
    }

    [Fact]
    public void Folder_default_is_stored_as_absence_of_key()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        AppSettings.SaveBackupFolder("/tmp/backups");
        AppSettings.SaveBackupFolder("   ");
        Assert.False(store.Has("backupFolder"));
        Assert.Equal(AppSettings.BackupFolderDefault, AppSettings.LoadBackupFolder());
    }

    [Fact]
    public void Resolved_folder_falls_back_to_backups_under_app_data()
    {
        AppSettings.AttachStore(new FakeStore());
        Assert.Equal(Path.Combine(AppSettings.AppDataDir, "Backups"), AppSettings.ResolveBackupFolder());
        AppSettings.SaveBackupFolder("/mnt/nas/copies");
        Assert.Equal("/mnt/nas/copies", AppSettings.ResolveBackupFolder());
    }

    // ── Глубина ротации ───────────────────────────────────────────────────────

    [Fact]
    public void Keep_survives_save_and_load()
    {
        AppSettings.AttachStore(new FakeStore());
        AppSettings.SaveBackupKeep(25);
        Assert.Equal(25, AppSettings.LoadBackupKeep());
    }

    [Fact]
    public void Keep_default_is_stored_as_absence_of_key()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        AppSettings.SaveBackupKeep(25);
        AppSettings.SaveBackupKeep(AppSettings.BackupKeepDefault);
        Assert.False(store.Has("backupKeep"));
    }

    [Fact]
    public void Keep_out_of_range_or_garbage_falls_back_to_default()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        store.Set("backupKeep", "0");
        Assert.Equal(AppSettings.BackupKeepDefault, AppSettings.LoadBackupKeep());
        store.Set("backupKeep", "101");
        Assert.Equal(AppSettings.BackupKeepDefault, AppSettings.LoadBackupKeep());
        store.Set("backupKeep", "десять");
        Assert.Equal(AppSettings.BackupKeepDefault, AppSettings.LoadBackupKeep());
    }

    [Fact]
    public void Nothing_saved_reads_as_defaults()
    {
        AppSettings.AttachStore(new FakeStore());
        Assert.Equal(AppSettings.BackupFolderDefault,   AppSettings.LoadBackupFolder());
        Assert.Equal(AppSettings.BackupKeepDefault,     AppSettings.LoadBackupKeep());
        Assert.Equal(AppSettings.BackupEnabledDefault,  AppSettings.LoadBackupEnabled(1));
        Assert.Equal(AppSettings.BackupScheduleDefault, AppSettings.LoadBackupSchedule(1));
        Assert.Null(AppSettings.LoadBackupLastRun(1));
    }

    // ── Ключи на базу ─────────────────────────────────────────────────────────

    [Fact]
    public void Per_database_settings_do_not_leak_between_databases()
    {
        AppSettings.AttachStore(new FakeStore());
        AppSettings.SaveBackupEnabled(1, true);
        AppSettings.SaveBackupSchedule(2, "weekly mon 04:00");

        Assert.True(AppSettings.LoadBackupEnabled(1));
        Assert.False(AppSettings.LoadBackupEnabled(2));
        Assert.Equal(AppSettings.BackupScheduleDefault, AppSettings.LoadBackupSchedule(1));
        Assert.Equal("weekly mon 04:00", AppSettings.LoadBackupSchedule(2));
    }

    [Fact]
    public void Enabled_default_is_stored_as_absence_of_key()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        AppSettings.SaveBackupEnabled(7, true);
        Assert.True(store.Has("backup.7.enabled"));
        AppSettings.SaveBackupEnabled(7, false);
        Assert.False(store.Has("backup.7.enabled"));
    }

    [Fact]
    public void Schedule_default_is_stored_as_absence_of_key()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        AppSettings.SaveBackupSchedule(7, "monthly 15 06:00");
        Assert.True(store.Has("backup.7.schedule"));
        AppSettings.SaveBackupSchedule(7, AppSettings.BackupScheduleDefault);
        Assert.False(store.Has("backup.7.schedule"));
    }

    [Fact]
    public void Last_run_round_trips_as_state()
    {
        AppSettings.AttachStore(new FakeStore());
        var instant = new DateTime(2026, 9, 5, 3, 0, 12);
        AppSettings.SaveBackupLastRun(3, instant);
        Assert.Equal(instant, AppSettings.LoadBackupLastRun(3));
    }

    // ── Уборка осиротевших ключей ─────────────────────────────────────────────

    [Fact]
    public void Orphan_cleanup_removes_keys_of_missing_databases_only()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        AppSettings.SaveBackupEnabled(1, true);
        AppSettings.SaveBackupSchedule(1, "monthly 15 06:00");
        AppSettings.SaveBackupEnabled(12, true);
        AppSettings.SaveBackupLastRun(3, new DateTime(2026, 9, 5, 3, 0, 0));
        AppSettings.SaveBackupFolder("/tmp/backups");
        AppSettings.SaveBackupKeep(25);

        // Жива только база 12: ключи баз 1 и 3 — сироты. «backup.1.» не должен задеть
        // «backup.12.…» — сверка идет по полной приставке с точкой.
        var removed = AppSettings.RemoveOrphanBackupSettings([12]);

        Assert.Equal(3, removed);
        Assert.False(store.Has("backup.1.enabled"));
        Assert.False(store.Has("backup.1.schedule"));
        Assert.False(store.Has("backup.3.lastRun"));
        Assert.True(store.Has("backup.12.enabled"));
        // Общие настройки раздела уборка не трогает.
        Assert.Equal("/tmp/backups", AppSettings.LoadBackupFolder());
        Assert.Equal(25, AppSettings.LoadBackupKeep());
    }
}
