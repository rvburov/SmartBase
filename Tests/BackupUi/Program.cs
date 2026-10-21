// Настоящая разметка настроек и общая панель фоновых работ, без дисплея и пользовательских БД.
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Components;
using SmartBase.Core;
using SmartBase.Utils;
using SmartBase.Views.Settings;

AppSettings.AttachStore(new MemoryStore());
AppBuilder.Configure<SmartBase.App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
    .SetupWithoutStarting();
FontScale.Init();
var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
    Console.WriteLine("PASS: " + message);
}

var settings = new SettingsView();
var toggle = settings.FindControl<ToggleSwitch>("BackupNotificationsToggle")!;
Check(toggle is not null, "Тумблер уведомлений о резервных копиях загружается из разметки");
Check(toggle!.IsChecked == false, "Тумблер выключен по умолчанию");
bool? changed = null;
settings.BackupNotificationsChanged += (_, value) => changed = value;
toggle.IsChecked = true;
Check(changed == true && AppSettings.LoadNotifyBackup(), "Включение сохраняется и сообщает главному окну");
toggle.IsChecked = false;
Check(changed == false && !AppSettings.LoadNotifyBackup(), "Выключение применяется без перезапуска");

var anchor = new Button { Content = "Фоновые работы" };
var window = new Window { Width = 1000, Height = 700, Content = anchor };
window.Show();
const string jobId = "backup-ui-test";
try
{
    BackgroundJobs.Report(jobId, "Резервное копирование", "Основная: Проверяю копию", 3, 10, icon: "IconArchive");
    BackgroundJobsPanel.Show(window, anchor);
    Dispatcher.UIThread.RunJobs();
    var host = (StackPanel)typeof(BackgroundJobsPanel).GetField("_host", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    var text = host.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
    Check(text.Contains("Резервное копирование") && text.Contains("Основная: Проверяю копию"), "Панель показывает базу и этап");
    Application.Current!.TryFindResource("IconArchive", out var archive);
    Check(archive is not null && host.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
        .Any(p => ReferenceEquals(p.Data, archive)), "В панели нарисована собственная иконка архива");

    BackgroundJobs.Fail(jobId, "Резервное копирование", "Основная: временный файл не найден", "IconArchive");
    Dispatcher.UIThread.RunJobs();
    text = host.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
    Check(text.Contains("Основная: временный файл не найден") && text.Contains("Убрать"), "Ошибка остается видимой и доступна команда Убрать");
    BackgroundJobs.Finish(jobId);
    Dispatcher.UIThread.RunJobs();
    Check(typeof(BackgroundJobsPanel).GetField("_host", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) is null,
        "Панель закрывается после удаления последней работы");
}
finally
{
    BackgroundJobs.Finish(jobId);
    BackgroundJobsPanel.Close();
    window.Close();
}
Console.WriteLine($"Проверок: {checks}; расхождений нет.");

sealed class MemoryStore : ISettingsDatabase
{
    private readonly Dictionary<string, string> _values = [];
    public string? Get(string key) => _values.GetValueOrDefault(key);
    public void Set(string key, string value) => _values[key] = value;
    public void Remove(string key) => _values.Remove(key);
    public IReadOnlyList<string> KeysWithPrefix(string prefix) => _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
}
