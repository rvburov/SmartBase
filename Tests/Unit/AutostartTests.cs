using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Автозапуск и фоновый режим: чистое построение записи автозапуска (plist/desktop/reg —
/// без записи на диск), логика «скрывать окно при закрытии» и дельты настроек. Побочные
/// эффекты (реальная запись файла/реестра) и жизненный цикл окна здесь не проверяются.
/// </summary>
public sealed class AutostartTests
{
    private sealed class FakeStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _d = new();
        public bool Has(string key) => _d.ContainsKey(key);
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _d[key] = value;
        public void Remove(string key) => _d.Remove(key);
    }

    // ── Построение записи (чистое) ─────────────────────────────────────────────

    // Запись автозапуска не несёт аргументов: при входе в систему окно открывается всегда,
    // независимо от фонового режима. Раньше сюда дописывался --background, и приложение
    // стартовало скрытым — со стороны это выглядело как «автозапуск не сработал».

    [Fact]
    public void Windows_command_is_quoted_exe_without_arguments()
    {
        Assert.Equal("\"C:\\App\\SmartBase.exe\"",
            Autostart.BuildWindowsCommand("C:\\App\\SmartBase.exe"));
        Assert.DoesNotContain("--background", Autostart.BuildWindowsCommand("C:\\App\\SmartBase.exe"));
    }

    [Fact]
    public void Mac_plist_has_runatload_and_program_without_arguments()
    {
        var plist = Autostart.BuildMacPlist("/Apps/SmartBase.app/Contents/MacOS/SmartBase");
        Assert.Contains("<key>RunAtLoad</key>", plist);
        Assert.Contains("com.romanburov.smartbase", plist);
        Assert.Contains("/Apps/SmartBase.app/Contents/MacOS/SmartBase", plist);
        Assert.DoesNotContain("--background", plist);
    }

    [Fact]
    public void Linux_desktop_entry_is_autostart_enabled_with_exec()
    {
        var d = Autostart.BuildLinuxDesktop("/opt/smartbase/SmartBase");
        Assert.Contains("[Desktop Entry]", d);
        Assert.Contains("X-GNOME-Autostart-enabled=true", d);
        Assert.Contains("/opt/smartbase/SmartBase", d);
        Assert.DoesNotContain("--background", d);
    }

    // ── Разбор записи (чистое) ─────────────────────────────────────────────────

    [Theory]
    [InlineData("\"C:\\App\\SmartBase.exe\"",                "C:\\App\\SmartBase.exe")]
    [InlineData("\"C:\\App\\SmartBase.exe\" --background",   "C:\\App\\SmartBase.exe")]   // запись до 1.0.5
    [InlineData("\"C:\\Program Files\\Smart Base\\App.exe\"", "C:\\Program Files\\Smart Base\\App.exe")]
    [InlineData("C:\\App\\SmartBase.exe",                    "C:\\App\\SmartBase.exe")]   // без кавычек
    [InlineData("",     null)]
    [InlineData("   ",  null)]
    [InlineData("\"",   null)]
    public void Windows_target_is_parsed_back_from_command(string command, string? expected) =>
        Assert.Equal(expected, Autostart.ParseWindowsTarget(command));

    [Fact]
    public void Mac_and_linux_targets_are_parsed_back_from_record()
    {
        const string bin = "/Apps/SmartBase.app/Contents/MacOS/SmartBase";
        Assert.Equal(bin, Autostart.ParseMacTarget(Autostart.BuildMacPlist(bin)));
        Assert.Null(Autostart.ParseMacTarget("<plist><dict></dict></plist>"));

        const string exec = "/opt/smartbase/SmartBase";
        Assert.Equal(exec, Autostart.ParseLinuxDesktopTarget(Autostart.BuildLinuxDesktop(exec)));
        // Запись до 1.0.5 несла аргумент — путь всё равно должен вычитываться.
        Assert.Equal(exec, Autostart.ParseLinuxDesktopTarget($"[Desktop Entry]\nExec=\"{exec}\" --background\n"));
        Assert.Null(Autostart.ParseLinuxDesktopTarget("[Desktop Entry]\nName=SmartBase\n"));
    }

    [Fact]
    public void Windows_command_round_trips_through_parse()
    {
        const string exe = "C:\\Users\\Иван\\AppData\\Local\\SmartBase\\SmartBase.exe";
        Assert.Equal(exe, Autostart.ParseWindowsTarget(Autostart.BuildWindowsCommand(exe)));
    }

    // ── Решение о синхронизации записи с настройкой (чистое) ───────────────────

    private const string Cur = "C:\\App\\SmartBase.exe";
    private static string Expected => Autostart.BuildWindowsCommand(Cur);

    [Fact]
    public void Sync_restores_missing_record_when_enabled() =>
        Assert.Equal(Autostart.SyncAction.Write,
            Autostart.DecideSync(true, null, null, false, Cur, Expected));

    [Fact]
    public void Sync_rewrites_record_pointing_at_deleted_binary()
    {
        var old = "C:\\Old\\SmartBase.exe";
        Assert.Equal(Autostart.SyncAction.Write,
            Autostart.DecideSync(true, $"\"{old}\"", old, recordedTargetExists: false, Cur, Expected));
    }

    [Fact]
    public void Sync_rewrites_stale_arguments_of_our_own_record() =>
        Assert.Equal(Autostart.SyncAction.Write,
            Autostart.DecideSync(true, $"\"{Cur}\" --background", Cur, true, Cur, Expected));

    [Fact]
    public void Sync_leaves_matching_record_alone() =>
        Assert.Equal(Autostart.SyncAction.None,
            Autostart.DecideSync(true, Expected, Cur, true, Cur, Expected));

    [Fact]
    public void Sync_does_not_hijack_another_living_copy()
    {
        var other = "D:\\Portable\\SmartBase.exe";
        Assert.Equal(Autostart.SyncAction.None,
            Autostart.DecideSync(true, $"\"{other}\"", other, recordedTargetExists: true, Cur, Expected));
    }

    [Fact]
    public void Sync_removes_only_our_record_when_disabled()
    {
        Assert.Equal(Autostart.SyncAction.Remove,
            Autostart.DecideSync(false, Expected, Cur, true, Cur, Expected));

        var other = "D:\\Portable\\SmartBase.exe";
        Assert.Equal(Autostart.SyncAction.None,
            Autostart.DecideSync(false, $"\"{other}\"", other, true, Cur, Expected));
        Assert.Equal(Autostart.SyncAction.None,
            Autostart.DecideSync(false, null, null, false, Cur, Expected));
    }

    // ── Поведение окна в фоне (чистое) ─────────────────────────────────────────

    [Theory]
    [InlineData(true,  false, true)]   // фон вкл, не явный выход → скрыть
    [InlineData(true,  true,  false)]  // фон вкл, явный выход     → не скрывать (закрыть)
    [InlineData(false, false, false)]  // фон выкл                 → не скрывать
    public void ShouldHideOnClose(bool background, bool explicitQuit, bool expected) =>
        Assert.Equal(expected, BackgroundBehavior.ShouldHideOnClose(background, explicitQuit));

    // ── Дельты настроек ────────────────────────────────────────────────────────

    [Fact]
    public void Autostart_and_background_default_off_and_round_trip_as_deltas()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);

        Assert.False(AppSettings.LoadAutostartEnabled());
        Assert.False(AppSettings.LoadBackgroundEnabled());

        AppSettings.SaveAutostartEnabled(true);
        AppSettings.SaveBackgroundEnabled(true);
        Assert.True(store.Has("autostartEnabled"));
        Assert.True(store.Has("backgroundEnabled"));
        Assert.True(AppSettings.LoadAutostartEnabled());
        Assert.True(AppSettings.LoadBackgroundEnabled());

        // Назад к дефолту (false) — ключи удаляются (в БД лежат только отличия).
        AppSettings.SaveAutostartEnabled(false);
        AppSettings.SaveBackgroundEnabled(false);
        Assert.False(store.Has("autostartEnabled"));
        Assert.False(store.Has("backgroundEnabled"));
    }
}
