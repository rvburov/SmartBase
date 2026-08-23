using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace SmartBase.Notifications.Platforms;

/// <summary>
/// Linux: значок-счетчик через протокол Unity LauncherEntry — сигнал D-Bus
/// <c>com.canonical.Unity.LauncherEntry.Update</c> со счетчиком. Его слушают док-панели
/// GNOME (Dash-to-Dock), KDE Plasma, Unity и др. Эмитим через <c>gdbus emit</c> (без
/// сторонних библиотек). Best-effort: где окружение не поддерживает LauncherEntry —
/// сигнал просто игнорируется. Привязка к <c>.desktop</c> приложения — <see cref="AppUri"/>.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxBadge : IAppBadge
{
    // Идентификатор приложения для LauncherEntry: application://<файл>.desktop.
    private const string AppUri     = "application://smartbase.desktop";
    private const string ObjectPath = "/com/canonical/Unity/LauncherEntry/smartbase";

    public void SetCount(int count)
    {
        var n = Math.Max(0, count);
        var visible = n > 0 ? "true" : "false";
        // a{sv}: {'count': <int64 N>, 'count-visible': <bool>}
        var payload = string.Format(CultureInfo.InvariantCulture,
            "{{'count': <int64 {0}>, 'count-visible': <{1}>}}", n, visible);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "gdbus",
                ArgumentList    =
                {
                    "emit", "--session",
                    "--object-path", ObjectPath,
                    "--signal", "com.canonical.Unity.LauncherEntry.Update",
                    AppUri, payload,
                },
                UseShellExecute = false,
                CreateNoWindow  = true,
            })?.Dispose();
        }
        catch { /* нет gdbus / окружение без LauncherEntry — тихо пропускаем */ }
    }
}
