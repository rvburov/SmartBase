// Один настоящий UI-наблюдатель при фоновой нагрузке, без пользовательских данных.
// 1000 сообщений или работ не означают 1000 окон/машин. Нативные баннеры заменены fake.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Components;
using SmartBase.Core;
using SmartBase.Notifications;
using SmartBase.Utils;

namespace SmartBase.Tests.ConcurrentUi;

internal static class Program
{
    private const int Events = 1000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly ConcurrentQueue<Exception> UiErrors = new();
    private static readonly List<ScenarioResult> Results = [];
    private static readonly List<Task> Producers = [];
    private static string _stage = "setup";

    private static int Main(string[] args)
    {
        Options options;
        try { options = Options.Parse(args); }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine("ConcurrentUi [--mode all|jobs-live|jobs-snapshot|notifications] [--out НОВАЯ_ПАПКА]");
            return 2;
        }
        var directory = options.Output ?? Path.Combine(Path.GetTempPath(), $"sb_concurrent_ui_{Guid.NewGuid():N}");
        if (Directory.Exists(directory) || File.Exists(directory))
        {
            Console.Error.WriteLine("Папка уже существует; укажите новую. Существующие данные не изменены.");
            return 2;
        }
        Directory.CreateDirectory(directory);
        var stopped = new ManualResetEventSlim(false);
        var watchdog = new Thread(() =>
        {
            if (stopped.Wait(TimeSpan.FromSeconds(60))) return;
            try
            {
                File.WriteAllText(Path.Combine(directory, "watchdog-failure.json"), JsonSerializer.Serialize(new
                {
                    Passed = false, Mode = Volatile.Read(ref _stage), ProcessId = Environment.ProcessId,
                    Reason = "Стенд не завершился за 60 секунд; UI или вызываемая операция заблокированы",
                    NativeBannersVerified = false,
                }, Json));
                Console.Error.WriteLine("WATCHDOG: стенд не завершился за 60 секунд; проверка не пройдена.");
            }
            finally { Environment.Exit(3); } // Завершается только текущий процесс стенда.
        }) { IsBackground = true, Name = "concurrent-ui-watchdog" };
        watchdog.Start();
        var overall = Stopwatch.StartNew();
        Exception? setupError = null;
        try
        {
            AppBuilder.Configure<SmartBase.App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            FontScale.Init();
            AppSettings.AttachStore(new MemoryStore()); // Один раз, на UI-потоке.
            Dispatcher.UIThread.UnhandledException += (_, error) =>
            {
                UiErrors.Enqueue(error.Exception);
                error.Handled = true;
            };
            Application.Current!.TryFindResource("FontFamilyBase", out var font);
            using (var probe = new TextLayout("Аа", new Typeface(font as FontFamily ?? FontFamily.Default),
                       FontScale.BaseFontSize, Brushes.Black))
                if (!double.IsFinite(probe.Height) || probe.Height <= 0)
                    throw new InvalidOperationException("Системный шрифт не имеет метрик; проверьте TrueType/OpenType и fontconfig");

            foreach (var mode in options.Mode == "all"
                         ? new[] { "jobs-live", "jobs-snapshot", "notifications" }
                         : new[] { options.Mode })
            {
                Volatile.Write(ref _stage, mode);
                var checks = new List<CheckResult>();
                var watch = Stopwatch.StartNew();
                using var pulses = new UiPulses();
                Exception? failure = null;
                try
                {
                    Until(() => pulses.Heartbeats > 0, null, pulses);
                    switch (mode)
                    {
                        case "jobs-live": JobsLive(checks, pulses); break;
                        case "jobs-snapshot": JobsSnapshot(checks, pulses); break;
                        case "notifications": Notifications(checks, pulses); break;
                    }
                    Check(checks, "Нет необработанных исключений UI", UiErrors.IsEmpty);
                }
                catch (Exception error)
                {
                    failure = error;
                    Console.Error.WriteLine($"FAIL {mode}: {error}");
                }
                finally
                {
                    BackgroundJobsPanel.Close();
                    // Продюсеры только отчитываются в реестр или Post, UI они не ждут.
                    // Заканчиваем их до очистки, чтобы не загрязнить следующий режим.
                    foreach (var producer in Producers)
                    {
                        try { producer.GetAwaiter().GetResult(); }
                        catch (Exception error) { failure ??= error; }
                    }
                    Producers.Clear();
                    foreach (var job in BackgroundJobs.All) BackgroundJobs.Finish(job.Id);
                    pulses.Stop();
                    Pump(null, pulses);
                }
                watch.Stop();
                var result = new ScenarioResult(mode, failure is null && checks.All(check => check.Passed),
                    Events, mode == "jobs-live" ? Events : 0, 16,
                    watch.Elapsed.TotalMilliseconds, pulses.Pumps, pulses.Heartbeats,
                    pulses.MaxGapMilliseconds, pulses.MaxQueueDelayMilliseconds,
                    checks, failure?.ToString());
                Results.Add(result);
                File.WriteAllText(Path.Combine(directory, mode + ".json"), JsonSerializer.Serialize(result, Json));
                Console.WriteLine($"{mode}: {(result.Passed ? "PASS" : "FAIL")}; " +
                    $"{result.ElapsedMilliseconds:F1} мс, UI pumps={result.UiPumps}, heartbeats={result.Heartbeats}, " +
                    $"max gap={result.MaxHeartbeatGapMilliseconds:F1} мс");
            }
        }
        catch (Exception error) { setupError = error; Console.Error.WriteLine(error); }
        finally
        {
            overall.Stop();
            File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new
            {
                Passed = setupError is null && Results.Count > 0 && Results.All(result => result.Passed),
                RequestedMode = options.Mode,
                ElapsedMilliseconds = overall.Elapsed.TotalMilliseconds,
                UiObservers = 1,
                LogicalProducersOrJobs = Events,
                NativeBannersVerified = false,
                Results,
                SetupError = setupError?.ToString(),
                UiErrors = UiErrors.Select(error => error.ToString()).ToArray(),
                Limitations = new[]
                {
                    "Один UI-наблюдатель в одном процессе; не 1000 окон/машин.",
                    "jobs-live: 1000 фоновых обновлений десяти работ и отдельный пакет 1000 отчетов для проверки объединения, не 1000 окон.",
                    "jobs-snapshot: 1000 строк сначала подготовлены, затем панель построена одним показом.",
                    "Для 1000 строк проверяется наличие в UI-дереве; прокрутка и доступность каждой строки отдельно не проверяются.",
                    "Уведомления проходят настоящий сервис и Dispatcher, но native notifier и badge заменены fake.",
                    "Задержки измерены без продуктового SLA; 60 секунд — предохранитель зависшего процесса.",
                },
            }, Json));
            stopped.Set();
            if (watchdog.Join(TimeSpan.FromSeconds(1))) stopped.Dispose();
        }
        Console.WriteLine("Отчеты: " + directory);
        return setupError is null && Results.Count > 0 && Results.All(result => result.Passed) ? 0 : 1;
    }

    private static void JobsLive(List<CheckResult> checks, UiPulses pulses)
    {
        var anchor = new Button { Content = "Фоновые работы" };
        var activity = new ActivityBar();
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        content.Children.Add(activity);
        Grid.SetColumn(anchor, 1);
        content.Children.Add(anchor);
        var window = new Window { Width = 1000, Height = 700, Content = content };
        window.Show();
        try
        {
            for (var job = 0; job < 10; job++)
                BackgroundJobs.Report($"live-{job}", $"Копия {job}", $"База {job}: подготовка", 0, 100, icon: "IconArchive");
            BackgroundJobsPanel.Show(window, anchor);
            Pump(window, pulses);
            var initialRows = Host()!.Children.Skip(1).Take(10).ToArray();
            var worker = Task.Run(() => Parallel.For(0, Events, new ParallelOptions { MaxDegreeOfParallelism = 16 }, producer =>
            {
                var job = producer % 10;
                var done = producer / 10 + 1;
                BackgroundJobs.Report($"live-{job}", $"Копия {job}", $"База {job}: этап {done}", done, 100, icon: "IconArchive");
            }));
            Producers.Add(worker);
            Until(() => worker.IsCompleted, window, pulses);
            worker.GetAwaiter().GetResult();
            Pump(window, pulses);

            var jobs = BackgroundJobs.All;
            Check(checks, "1000 фоновых обновлений сохранили ровно 10 работ", jobs.Count == 10);
            Check(checks, "Ключи работ не потеряны", jobs.Select(job => job.Id).ToHashSet()
                .SetEquals(Enumerable.Range(0, 10).Select(job => $"live-{job}")));
            Check(checks, "Последнее значение каждой работы целое и согласовано с подробностями",
                jobs.All(job => job.Done is >= 1 and <= 100 && job.Total == 100 && !job.Failed
                    && job.Icon == "IconArchive" && job.Detail == $"База {int.Parse(job.Id.AsSpan(5))}: этап {job.Done}"));
            Check(checks, "Общий прогресс совпадает со снимком работ",
                Math.Abs((BackgroundJobs.Progress() ?? -1) - jobs.Sum(job => job.Done) / 1000d) < 1e-9);
            var host = Host() ?? throw new InvalidOperationException("Панель исчезла при работающих заданиях");
            Check(checks, "Поток прогресса обновил прежние строки без создания новых контролов",
                initialRows.Zip(host.Children.Skip(1).Take(10)).All(pair => ReferenceEquals(pair.First, pair.Second)));
            var text = Text(host);
            Check(checks, "Открытая панель показывает все последние заголовки и подробности",
                jobs.All(job => text.Contains(job.Title) && text.Contains(job.Detail)));
            Check(checks, "У каждой работы есть иконка архива", ArchiveIcons(host) == 10);
            var bars = host.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Height == 4 && grid.ColumnDefinitions.Count == 2).ToList();
            Check(checks, "Десять полос показывают прогресс соответствующих работ", bars.Count == 10
                && bars.Select((bar, index) => bar.ColumnDefinitions[0].Width.Value == jobs[index].Done
                    && bar.ColumnDefinitions[1].Width.Value == jobs[index].Total - jobs[index].Done).All(value => value));

            // Детерминированная проверка объединения: все отчеты пришли до обработки UI.
            // На каждую строку достаточно последнего текста, а не ста промежуточных.
            var details = host.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => jobs.Any(job => block.Text == job.Detail)).ToArray();
            var arc = activity.FindControl<Avalonia.Controls.Shapes.Ellipse>("WorkArc")
                ?? throw new InvalidOperationException("Кольцо фоновых работ не найдено");
            var changes = 0;
            var arcChanges = 0;
            void Changed(object? sender, AvaloniaPropertyChangedEventArgs args)
            {
                if (args.Property == TextBlock.TextProperty) changes++;
            }
            void ArcChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
            {
                if (args.Property == Avalonia.Controls.Shapes.Shape.StrokeDashArrayProperty) arcChanges++;
            }
            foreach (var detail in details) detail.PropertyChanged += Changed;
            arc.PropertyChanged += ArcChanged;
            try
            {
                for (var i = 0; i < Events; i++)
                {
                    var job = jobs[i % jobs.Count];
                    BackgroundJobs.Report(job.Id, job.Title, $"Пакет {i}", job.Done, job.Total, job.Stop, job.Icon);
                }
                Pump(window, pulses);
                Check(checks, "1000 отчетов до обработки очереди дали одно обновление текста каждой строки",
                    details.Length == 10 && changes == 10
                    && Enumerable.Range(990, 10).All(i => Text(host).Contains($"Пакет {i}")));
                Check(checks, "Значок фона также объединил пакет в одно обновление кольца", arcChanges == 1);
            }
            finally
            {
                foreach (var detail in details) detail.PropertyChanged -= Changed;
                arc.PropertyChanged -= ArcChanged;
            }

            BackgroundJobs.Fail("live-0", "Копия 0", "Контролируемый отказ временного файла", "IconArchive");
            Pump(window, pulses);
            host = Host()!;
            Check(checks, "Ошибка остается в реестре и панели", BackgroundJobs.AnyFailed
                && Text(host).Contains("Контролируемый отказ временного файла"));
            var remove = host.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault(block => block.Text == "Убрать");
            Check(checks, "Для ошибки доступна команда Убрать", remove is not null);
            var link = remove!;
            var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var released = new PointerEventArgs(InputElement.PointerReleasedEvent, link, pointer, link,
                new Point(1, 1), 0, PointerPointProperties.None, KeyModifiers.None);
            link.RaiseEvent(new TappedEventArgs(InputElement.TappedEvent, released));
            Pump(window, pulses);
            Check(checks, "Команда Убрать удалила только ошибочную работу",
                !BackgroundJobs.AnyFailed && BackgroundJobs.All.Count == 9 && BackgroundJobs.All.All(job => job.Id != "live-0"));
            foreach (var job in BackgroundJobs.All) BackgroundJobs.Finish(job.Id);
            Pump(window, pulses);
            Check(checks, "После завершения работ панель закрылась", !BackgroundJobs.Any && Host() is null);

            BackgroundJobs.Report("old-panel", "Прежняя панель", "Первый снимок", icon: "IconArchive");
            BackgroundJobsPanel.Show(window, anchor);
            BackgroundJobs.Report("old-panel", "Прежняя панель", "Отложенный отчет", icon: "IconArchive");
            BackgroundJobsPanel.Close();
            BackgroundJobs.Finish("old-panel");
            BackgroundJobs.Report("new-panel", "Новое открытие", "Актуальная работа", icon: "IconArchive");
            BackgroundJobsPanel.Show(window, anchor);
            var reopened = Host();
            Pump(window, pulses);
            Check(checks, "Отложенные обновления закрытой панели не затрагивают новое открытие",
                reopened is not null && ReferenceEquals(Host(), reopened)
                && Text(reopened).Contains("Актуальная работа") && !Text(reopened).Contains("Отложенный отчет"));
            BackgroundJobs.Finish("new-panel");
            Pump(window, pulses);
            Check(checks, "UI исполнял независимые heartbeat-сообщения", pulses.Heartbeats > 0);
        }
        finally { BackgroundJobsPanel.Close(); window.Close(); }
    }

    private static void JobsSnapshot(List<CheckResult> checks, UiPulses pulses)
    {
        BackgroundJobsPanel.Close();
        var worker = Task.Run(() => Parallel.For(0, Events, new ParallelOptions { MaxDegreeOfParallelism = 16 }, producer =>
            BackgroundJobs.Report($"snapshot-{producer}", $"Работа {producer:D4}", $"База {producer:D4}", 1, 2, icon: "IconArchive")));
        Producers.Add(worker);
        Until(() => worker.IsCompleted, null, pulses);
        worker.GetAwaiter().GetResult();
        Check(checks, "До открытия панели подготовлены все 1000 разных работ", BackgroundJobs.All.Count == Events && Host() is null);
        var anchor = new Button { Content = "Фоновые работы" };
        var window = new Window { Width = 1000, Height = 700, Content = anchor };
        window.Show();
        try
        {
            BackgroundJobsPanel.Show(window, anchor);
            Pump(window, pulses);
            var host = Host() ?? throw new InvalidOperationException("Панель не открылась");
            var text = Text(host);
            Check(checks, "Панель построила ровно 1000 строк и служебные шапку/подвал", host.Children.Count == Events + 2);
            Check(checks, "В дереве панели присутствуют все 1000 уникальных заголовков",
                Enumerable.Range(0, Events).All(producer => text.Contains($"Работа {producer:D4}")));
            Check(checks, "Все 1000 строк имеют иконку", ArchiveIcons(host) == Events);
            Check(checks, "Общий прогресс тысячи работ равен половине", BackgroundJobs.Progress() == .5);
            // Не прокачиваем Dispatcher между Finish: первая очередь увидит пустой реестр
            // и закроет панель, остальные callbacks безопасно завершатся без Rebuild.
            foreach (var job in BackgroundJobs.All) BackgroundJobs.Finish(job.Id);
            Pump(window, pulses);
            Check(checks, "Пакетное завершение тысячи работ очищает реестр и закрывает панель", !BackgroundJobs.Any && Host() is null);
            Check(checks, "UI принимал heartbeat-сообщения", pulses.Heartbeats > 0);
        }
        finally { BackgroundJobsPanel.Close(); window.Close(); }
    }

    private static void Notifications(List<CheckResult> checks, UiPulses pulses)
    {
        var notifier = new NativeNotifier();
        var badge = new Badge();
        using var service = new NotificationService(notifier, badge, (_, _) => [], _ => { });
        service.Configure(false, DateTime.Now);
        service.ConfigureTasks(false, false);
        service.SetBackupEnabled(true);
        var processed = 0;
        var worker = Task.Run(() => Parallel.For(0, Events, new ParallelOptions { MaxDegreeOfParallelism = 16 }, producer =>
            Dispatcher.UIThread.Post(() =>
            {
                service.NotifyBackupResult(producer + 1, $"База {producer + 1}", producer % 2 == 0, "Контролируемая ошибка");
                processed++;
            })));
        Producers.Add(worker);
        Until(() => worker.IsCompleted && processed == Events, null, pulses);
        worker.GetAwaiter().GetResult();
        Check(checks, "1000 фоновых событий обработаны на UI-потоке", processed == Events && notifier.WrongThreadCalls == 0 && badge.WrongThreadCalls == 0);
        Check(checks, "Показаны 1000 результатов с уникальными метками",
            notifier.Shown.Count == Events && notifier.Shown.Select(item => item.Tag).Distinct().Count() == Events);
        Check(checks, "Отдельно представлены успехи и ошибки копирования",
            notifier.Shown.Count(item => item.Title == "Резервная копия создана") == Events / 2
            && notifier.Shown.Count(item => item.Title == "Ошибка резервного копирования") == Events / 2);
        Check(checks, "Счетчик не потерял ни одного уведомления", badge.Count == Events && Unseen(service) == Events
            && badge.Values.SequenceEqual(Enumerable.Range(1, Events)));

        var last = notifier.Shown[^1];
        var expectedDatabase = int.Parse(last.Tag.Split(':')[1]);
        var activated = 0;
        service.BackupActivated += database => Dispatcher.UIThread.Post(() => activated = database);
        var click = Task.Run(() => notifier.Click(last.Tag));
        Producers.Add(click);
        Until(() => click.IsCompleted && activated != 0, null, pulses);
        click.GetAwaiter().GetResult();
        Check(checks, "Клик по последнему баннеру из фонового потока маршрутизирует нужную базу", activated == expectedDatabase);
        service.NotifyWindowActivated();
        Check(checks, "Активация окна очищает счетчик непрочитанных", badge.Count == 0 && Unseen(service) == 0);

        service.SetBackupEnabled(false);
        var shownBefore = notifier.Shown.Count;
        var badgeBefore = badge.Values.Count;
        var ignored = 0;
        worker = Task.Run(() => Parallel.For(0, Events, new ParallelOptions { MaxDegreeOfParallelism = 16 }, producer =>
            Dispatcher.UIThread.Post(() =>
            {
                service.NotifyBackupResult(producer + 1, "Отключенные уведомления", true, "");
                ignored++;
            })));
        Producers.Add(worker);
        Until(() => worker.IsCompleted && ignored == Events, null, pulses);
        worker.GetAwaiter().GetResult();
        Check(checks, "Выключенный тумблер подавил все 1000 следующих событий",
            notifier.Shown.Count == shownBefore && badge.Values.Count == badgeBefore && badge.Count == 0 && Unseen(service) == 0);
        Check(checks, "Нотифайер и значок всегда вызывались на UI-потоке", notifier.WrongThreadCalls == 0 && badge.WrongThreadCalls == 0);
    }

    private static int Unseen(NotificationService service) => (int)typeof(NotificationService)
        .GetField("_unseen", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
    private static StackPanel? Host() => (StackPanel?)typeof(BackgroundJobsPanel)
        .GetField("_host", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
    private static HashSet<string?> Text(Control host) => host.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToHashSet();
    private static int ArchiveIcons(Control host)
    {
        Application.Current!.TryFindResource("IconArchive", out var icon);
        if (icon is null) throw new InvalidOperationException("Ресурс иконки архива не найден");
        return host.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Count(path => ReferenceEquals(path.Data, icon));
    }
    private static void Until(Func<bool> ready, Window? window, UiPulses pulses)
    {
        do { Pump(window, pulses); if (!ready()) Thread.Yield(); }
        while (!ready()); // Включая зависший RunJobs защищается независимым watchdog.
    }
    private static void Pump(Window? window, UiPulses pulses)
    {
        Dispatcher.UIThread.RunJobs();
        if (window is { IsVisible: true })
        {
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame();
        }
        pulses.Pumps++;
        if (!UiErrors.IsEmpty) throw new AggregateException("Исключения UI", UiErrors);
    }
    private static void Check(List<CheckResult> checks, string name, bool condition)
    {
        checks.Add(new(name, condition));
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
        if (!condition) throw new InvalidOperationException(name);
    }

    private sealed record CheckResult(string Name, bool Passed);
    private sealed record ScenarioResult(string Mode, bool Passed, int LogicalEventsOrJobs, int AdditionalReportEvents, int MaxWorkers,
        double ElapsedMilliseconds, int UiPumps, int Heartbeats, double MaxHeartbeatGapMilliseconds,
        double MaxHeartbeatQueueDelayMilliseconds, IReadOnlyList<CheckResult> Checks, string? Error);

    private sealed class UiPulses : IDisposable
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly Thread _thread;
        private double _last;
        private bool _stopped;
        internal int Pumps;
        internal int Heartbeats;
        internal double MaxGapMilliseconds;
        internal double MaxQueueDelayMilliseconds;
        internal UiPulses()
        {
            _thread = new Thread(() =>
            {
                while (!_stop.IsSet)
                {
                    var posted = _clock.Elapsed.TotalMilliseconds;
                    Dispatcher.UIThread.Post(() =>
                    {
                        var now = _clock.Elapsed.TotalMilliseconds;
                        Heartbeats++;
                        MaxGapMilliseconds = Math.Max(MaxGapMilliseconds, now - _last);
                        MaxQueueDelayMilliseconds = Math.Max(MaxQueueDelayMilliseconds, now - posted);
                        _last = now;
                    });
                    if (_stop.Wait(TimeSpan.FromMilliseconds(5))) return;
                }
            }) { IsBackground = true, Name = "concurrent-ui-heartbeat" };
            _thread.Start();
        }
        internal void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            _stop.Set();
            if (_thread.Join(TimeSpan.FromSeconds(1))) _stop.Dispose();
        }
        public void Dispose() => Stop();
    }

    private sealed class NativeNotifier : INativeNotifier
    {
        internal List<(string Title, string Body, string Tag)> Shown { get; } = [];
        internal int WrongThreadCalls;
        public event Action<string>? Activated;
        public void Show(string title, string body, string tag)
        {
            if (!Dispatcher.UIThread.CheckAccess()) Interlocked.Increment(ref WrongThreadCalls);
            Shown.Add((title, body, tag));
        }
        internal void Click(string tag) => Activated?.Invoke(tag);
    }

    private sealed class Badge : IAppBadge
    {
        internal int Count;
        internal int WrongThreadCalls;
        internal List<int> Values { get; } = [];
        public void SetCount(int count)
        {
            if (!Dispatcher.UIThread.CheckAccess()) Interlocked.Increment(ref WrongThreadCalls);
            Count = count;
            Values.Add(count);
        }
    }

    private sealed class MemoryStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Get(string key) { RequireUi(); return _values.GetValueOrDefault(key); }
        public void Set(string key, string value) { RequireUi(); _values[key] = value; }
        public void Remove(string key) { RequireUi(); _values.Remove(key); }
        public IReadOnlyList<string> KeysWithPrefix(string prefix)
        {
            RequireUi();
            return _values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }
        private static void RequireUi()
        {
            if (!Dispatcher.UIThread.CheckAccess()) throw new InvalidOperationException("Хранилище настроек вызвано вне UI-потока");
        }
    }

    private sealed record Options(string Mode, string? Output)
    {
        internal static Options Parse(string[] args)
        {
            var mode = "all";
            string? output = null;
            var seen = new HashSet<string>();
            for (var i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || args[i] is not ("--mode" or "--out") || !seen.Add(args[i]))
                    throw new ArgumentException("Неизвестный, повторенный или незаполненный параметр: " + args[i]);
                if (args[i] == "--mode") mode = args[i + 1];
                else output = Path.GetFullPath(args[i + 1]);
            }
            if (mode is not ("all" or "jobs-live" or "jobs-snapshot" or "notifications"))
                throw new ArgumentException("Неизвестный режим: " + mode);
            return new(mode, output);
        }
    }
}
