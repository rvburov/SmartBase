using DesktopNotifications;
using SmartBase.Notifications.Platforms;

namespace SmartBase.Notifications;

/// <summary>
/// Фабрика платформенных бэкендов. Баннер: macOS — свой код (<see cref="MacNotifier"/>,
/// UserNotifications), Windows/Linux — библиотека DesktopNotifications
/// (<see cref="LibraryNotifier"/>, менеджер приходит из Program.BuildAvaloniaApp).
/// Значок: платформенный per-OS. Неподдержанная среда — тихие no-op заглушки.
/// </summary>
public static class PlatformNotifications
{
    /// <param name="libManager">Менеджер библиотеки DesktopNotifications для Windows/Linux
    /// (на macOS не используется — там свой бэкенд; может быть null, если не создан).</param>
    public static INativeNotifier CreateNotifier(INotificationManager? libManager)
    {
        if (OperatingSystem.IsMacOS()) return new MacNotifier();
        if ((OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) && libManager is not null)
            return new LibraryNotifier(libManager);
        return new NullNotifier();
    }

    /// <param name="windowHandle">Нативный хэндл главного окна (нужен только Windows —
    /// ITaskbarList3.SetOverlayIcon); macOS/Linux его игнорируют.</param>
    public static IAppBadge CreateBadge(Func<nint>? windowHandle = null)
    {
        if (OperatingSystem.IsMacOS())   return new MacBadge();
        if (OperatingSystem.IsWindows()) return new WindowsBadge(windowHandle ?? (() => nint.Zero));
        if (OperatingSystem.IsLinux())   return new LinuxBadge();
        return new NullBadge();
    }

    private sealed class NullNotifier : INativeNotifier
    {
        public event Action<string>? Activated { add { } remove { } }
        public void Show(string title, string body, string tag) { }
    }

    private sealed class NullBadge : IAppBadge
    {
        public void SetCount(int count) { }
    }
}
