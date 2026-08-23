namespace SmartBase.Notifications;

/// <summary>
/// Значок-счетчик на иконке приложения (Dock macOS, overlay-иконка таскбара Windows,
/// Unity LauncherEntry Linux). Реализация выбирается фабрикой <see cref="PlatformNotifications"/>.
/// Контракт: вызывается из UI-потока, НИКОГДА не бросает; <c>count &lt;= 0</c> прячет значок.
/// </summary>
public interface IAppBadge
{
    /// <summary>Показать число <paramref name="count"/> на иконке; 0 (или меньше) — убрать значок.</summary>
    void SetCount(int count);
}
