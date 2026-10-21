using SmartBase.Notifications;

namespace SmartBase.Tests.Integration;

public sealed class BackupNotificationTests
{
    private sealed class Notifier : INativeNotifier
    {
        public List<(string Title, string Body, string Tag)> Shown { get; } = [];
        public event Action<string>? Activated;
        public void Show(string title, string body, string tag) => Shown.Add((title, body, tag));
        public void Click(string tag) => Activated?.Invoke(tag);
    }
    private sealed class Badge : IAppBadge
    {
        public int Count { get; private set; }
        public void SetCount(int count) => Count = count;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Backup_toggle_controls_both_results_independently_of_calendar_and_click_routes_to_database(bool ok)
    {
        var notifier = new Notifier();
        var badge = new Badge();
        using var service = new NotificationService(notifier, badge, (_, _) => [], _ => { });
        service.Configure(false, DateTime.Now);
        service.NotifyBackupResult(42, "Основная", ok, "файл не найден");
        Assert.Empty(notifier.Shown);
        Assert.Equal(0, badge.Count);

        service.SetBackupEnabled(true);
        service.NotifyBackupResult(42, "Основная", ok, "файл не найден");
        var banner = Assert.Single(notifier.Shown);
        Assert.Equal(ok ? "Резервная копия создана" : "Ошибка резервного копирования", banner.Title);
        Assert.Contains("Основная", banner.Body);
        if (!ok) Assert.Contains("файл не найден", banner.Body);
        Assert.Equal(1, badge.Count);
        int? activated = null;
        service.BackupActivated += id => activated = id;
        notifier.Click(banner.Tag);
        Assert.Equal(42, activated);

        service.SetBackupEnabled(false);
        Assert.Equal(0, badge.Count);
        service.NotifyBackupResult(42, "Основная", ok, "");
        Assert.Single(notifier.Shown);
    }
}
