using DesktopNotifications;

namespace SmartBase.Notifications.Platforms;

/// <summary>
/// Windows/Linux: баннер через библиотеку DesktopNotifications (её <c>INotificationManager</c>
/// создаётся в Program.BuildAvaloniaApp). Она посылает уведомление от имени приложения (иконка
/// из ярлыка/AUMID на Windows, из .desktop на Linux) и отдаёт колбэк клика. Метку записи
/// связываем с объектом <see cref="Notification"/> и достаём в событии активации.
/// </summary>
internal sealed class LibraryNotifier : INativeNotifier
{
    private readonly INotificationManager _manager;
    // Метка (tag) по показанному уведомлению; ограничено, чтобы не расти в долгих сессиях.
    // Show идёт с UI-потока, событие активации библиотека поднимает со своего — под локом.
    private readonly Dictionary<Notification, string> _tags = new();
    private readonly Lock _lock = new();
    private const int Limit = 200;

    public event Action<string>? Activated;

    public LibraryNotifier(INotificationManager manager)
    {
        _manager = manager;
        _manager.NotificationActivated += OnActivated;
    }

    public void Show(string title, string body, string tag)
    {
        try
        {
            var n = new Notification { Title = title, Body = body };
            lock (_lock)
            {
                if (_tags.Count >= Limit) _tags.Clear();
                _tags[n] = tag;
            }
            _ = _manager.ShowNotification(n);
        }
        catch { /* сбой библиотеки/ОС не ломает цикл */ }
    }

    // Любая активация (клик по телу = ActionId "default") открывает запись.
    private void OnActivated(object? sender, NotificationActivatedEventArgs e)
    {
        string? tag;
        lock (_lock) tag = _tags.TryGetValue(e.Notification, out var t) ? t : null;
        if (!string.IsNullOrEmpty(tag)) Activated?.Invoke(tag);
    }
}
