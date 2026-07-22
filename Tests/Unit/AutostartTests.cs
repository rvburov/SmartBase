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

    [Fact]
    public void Windows_command_quotes_exe_and_adds_background_flag()
    {
        Assert.Equal("\"C:\\App\\SmartBase.exe\"",
            Autostart.BuildWindowsCommand("C:\\App\\SmartBase.exe", background: false));

        var bg = Autostart.BuildWindowsCommand("C:\\App\\SmartBase.exe", background: true);
        Assert.Contains(Autostart.BackgroundArg, bg);
    }

    [Fact]
    public void Mac_plist_has_runatload_program_and_optional_background()
    {
        var plist = Autostart.BuildMacPlist("/Apps/SmartBase.app/Contents/MacOS/SmartBase", background: true);
        Assert.Contains("<key>RunAtLoad</key>", plist);
        Assert.Contains("com.romanburov.smartbase", plist);
        Assert.Contains("/Apps/SmartBase.app/Contents/MacOS/SmartBase", plist);
        Assert.Contains(Autostart.BackgroundArg, plist);

        var noBg = Autostart.BuildMacPlist("/Apps/SmartBase.app/Contents/MacOS/SmartBase", background: false);
        Assert.DoesNotContain(Autostart.BackgroundArg, noBg);
    }

    [Fact]
    public void Linux_desktop_entry_is_autostart_enabled_with_exec()
    {
        var d = Autostart.BuildLinuxDesktop("/opt/smartbase/SmartBase", background: true);
        Assert.Contains("[Desktop Entry]", d);
        Assert.Contains("X-GNOME-Autostart-enabled=true", d);
        Assert.Contains("/opt/smartbase/SmartBase", d);
        Assert.Contains(Autostart.BackgroundArg, d);
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
