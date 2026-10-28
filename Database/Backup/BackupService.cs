using Avalonia.Threading;
using Serilog;
using SmartBase.Database.Entities;
using SmartBase.Logging;
using SmartBase.Utils;
using SmartBase.Core;

namespace SmartBase.Database.Backup;

/// <summary>
/// Служба резервного копирования: раз в минуту сверяет расписания включенных баз
/// (см. BackupPlanner) и снимает копии — SQLite через <see cref="DatabaseManager.BackupTo"/>
/// (VACUUM INTO + проверка целостности), PostgreSQL через <see cref="PgDump"/>. Пропущенный
/// при выключенном приложении запуск догоняется первым же тиком (IsDue по lastRun).
///
/// Тик идет на UI-потоке (DispatcherTimer, как у службы уведомлений): реестр баз и
/// настройки живут на общем контексте app.db, который трогают только с UI-потока.
/// Сама копия — в Task.Run, копии строго по одной (<see cref="_gate"/>): параллельные
/// VACUUM INTO двух больших баз убили бы диск. Смонтированность не требуется:
/// SQLite-копия работает по пути файла, pg_dump — по параметрам подключения.
///
/// События <see cref="BackupStarted"/>/<see cref="BackupFinished"/> поднимаются на
/// UI-потоке; <see cref="BackupProgress"/> — ИЗ ФОНОВОГО ПОТОКА (колбэк хода копии),
/// подписчик маршалит сам.
/// </summary>
public sealed class BackupService : IDisposable
{
    public const int TickSeconds = 60;

    /// <summary>Повторная попытка после сбоя — не раньше чем через полчаса: без паузы
    /// упавший бэкап (файл занят, сервер недоступен) ретраился бы каждый тик.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(30);

    /// <summary>Копия базы начата (dbId). UI-поток.</summary>
    public event Action<int>? BackupStarted;
    /// <summary>Ход копии (dbId, фаза, доля 0..1 или null). Фоновый поток!</summary>
    public event Action<int, string, double?>? BackupProgress;
    /// <summary>Копия завершена (dbId, успех, текст ошибки — пустой при успехе). UI-поток.</summary>
    public event Action<int, bool, string>? BackupFinished;

    private readonly DatabaseRegistry _registry;
    private readonly SemaphoreSlim    _gate = new(1, 1);   // копии строго по одной
    private readonly Dictionary<int, DateTime> _lastAttempt = [];   // бэкоф ретраев, на процесс
    private readonly object _runsGate = new();
    private readonly Dictionary<long, BackupAttempt> _runs = [];
    private long _runSequence;
    private DispatcherTimer? _timer;
    private bool _ticking;   // тики не накладываются; все входы — с UI-потока
    private bool _recovered;
    private volatile bool _disposed;
    private readonly string _jobPrefix = "backup." + Guid.NewGuid().ToString("N") + ".";
    private const string JobTitle = "Резервное копирование";
    private const string JobIcon = "IconArchive";
    internal const string InterruptedMessage = "Копирование прервано. Результат не подтвержден";
    internal const string CanceledMessage = "Копирование отменено. Новая копия не создана.";
    internal string BackgroundJobId(int dbId) => _jobPrefix + dbId;

    public BackupService(DatabaseRegistry registry) => _registry = registry;

    /// <summary>Отменить все выполняемые и ожидающие своей очереди копии этой базы.
    /// В фоновой панели Stop отдельной строки отменяет только эту попытку.</summary>
    public bool Cancel(int dbId)
    {
        CancellationTokenSource[] cancellations;
        lock (_runsGate)
            cancellations = _runs.Values.Where(r => r.DatabaseId == dbId).Select(r => r.Cancellation).ToArray();
        foreach (var cancellation in cancellations) Cancel(cancellation);
        return cancellations.Length > 0;
    }

    /// <summary>Запуск из UI-потока (MainWindow.Opened). Идемпотентен: Opened приходит
    /// снова при каждом возврате окна из трея (см. NotificationService.Start), повторный
    /// вызов не заводит второй таймер. Первый тик — через минуту: старт приложения и так
    /// занят монтированием баз, а догон пропущенного минуты не боится.</summary>
    public void Start()
    {
        if (_timer is not null) return;
        RecoverInterrupted();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TickSeconds) };
        _timer.Tick += (_, _) => _ = TickAsync();
        _timer.Start();

        // Служба без окна — запуск и число расписаний в журнал: разбор «копия не снялась»
        // начинается с вопроса, была ли служба вообще запущена и что у нее включено.
        var enabled = _registry.GetAll().Count(d => AppSettings.LoadBackupEnabled(d.Id));
        Log.Information("Резервное копирование: служба запущена, расписаний включено: {Count}", enabled);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Stop();
        _timer = null;
        CancellationTokenSource[] cancellations;
        lock (_runsGate) cancellations = _runs.Values.Select(r => r.Cancellation).ToArray();
        foreach (var cancellation in cancellations) Cancel(cancellation);
        foreach (var job in BackgroundJobs.All.Where(j => j.Id.StartsWith(_jobPrefix, StringComparison.Ordinal)))
            BackgroundJobs.Finish(job.Id);
        Log.Information("Резервное копирование: служба остановлена");
    }

    // Маркер хранится до проверки и публикации копии. После аварийного завершения
    // его нельзя превращать в успех по наличию .part или по старому якорю расписания.
    internal void RecoverInterrupted()
    {
        if (_recovered) return;
        _recovered = true;
        foreach (var entry in _registry.GetAll())
        {
            var state = AppSettings.LoadBackupRunState(entry.Id);
            if (state.Outcome != BackupRunOutcome.Running) continue;
            AppSettings.SaveBackupRunState(entry.Id, state with
            {
                Outcome = BackupRunOutcome.Interrupted, Error = InterruptedMessage,
            });
            BackgroundJobs.Fail(BackgroundJobId(entry.Id), JobTitle,
                $"{entry.Name}: {InterruptedMessage}", JobIcon);
            BackupFinished?.Invoke(entry.Id, false, InterruptedMessage);
        }
    }

    /// <summary>Снять копию немедленно (кнопка «Копировать сейчас»). Ждет своей очереди,
    /// если сейчас идет другая копия; бэкоф ретраев не действует — человек попросил явно.
    /// Успешная ручная копия двигает lastRun: плановая в тот же день не дублируется.</summary>
    public async Task RunNowAsync(int dbId, CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        RecoverInterrupted();
        var entry = _registry.GetAll().FirstOrDefault(d => d.Id == dbId);
        if (entry is null) return;
        await RunOneAsync(entry, manual: true, cancellationToken);
    }

    /// <summary>Один плановый проход (для тестов — без ожидания таймера).</summary>
    public Task TickNowAsync() => TickAsync();

    private async Task TickAsync()
    {
        if (_disposed) return;
        RecoverInterrupted();
        if (_ticking) return;
        _ticking = true;
        try
        {
            var now = DateTime.Now;
            // Снимок реестра до работы: копия долгая, а реестр могли поменять по ходу.
            foreach (var entry in _registry.GetAll().ToList())
            {
                if (!AppSettings.LoadBackupEnabled(entry.Id)) continue;

                var last = AppSettings.LoadBackupLastRun(entry.Id);
                if (last is null)
                {
                    // Якорь «сейчас»: тумблер только что включили — первый бэкап по
                    // ближайшему расписанию, а не немедленно (см. BackupPlanner).
                    AppSettings.SaveBackupLastRun(entry.Id, now);
                    continue;
                }

                var schedule = BackupSchedule.Parse(AppSettings.LoadBackupSchedule(entry.Id));
                if (!BackupPlanner.IsDue(schedule, last.Value, now)) continue;
                if (_lastAttempt.TryGetValue(entry.Id, out var attempt) && now - attempt < RetryDelay) continue;

                await RunOneAsync(entry, manual: false);
            }
        }
        finally
        {
            _ticking = false;
        }
    }

    // Одна копия от начала до конца: очередь, файл, ротация, отметка, журнал, события.
    private async Task RunOneAsync(DatabaseRefEntity entry, bool manual, CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long runId;
        string jobId;
        BackupAttempt attempt;
        lock (_runsGate)
        {
            if (_disposed) return;
            runId = ++_runSequence;
            // Повторный ручной запуск обязан снять новый снимок после своей очереди.
            // Его Stop не должен отменить идущую копию с тем же dbId.
            jobId = _runs.Values.Any(r => r.DatabaseId == entry.Id)
                ? BackgroundJobId(entry.Id) + ".run." + runId
                : BackgroundJobId(entry.Id);
            attempt = new BackupAttempt(entry.Id, cancellation);
            _runs.Add(runId, attempt);
        }
        var token = cancellation.Token;
        var acquired = false;
        var startedAt = DateTime.Now;
        var progressGate = new object();
        var acceptingProgress = true;
        void ReportProgress(string message, double? fraction)
        {
            lock (progressGate)
            {
                if (!acceptingProgress || _disposed) return;
                BackgroundJobs.Report(jobId, JobTitle,
                    $"{entry.Name}: {message.Replace('\n', ' ')}",
                    fraction is { } f ? (int)(Math.Clamp(f, 0, 1) * 1000) : 0,
                    fraction.HasValue ? 1000 : 0, stop: () => Cancel(cancellation), icon: JobIcon);
                if (_disposed)
                {
                    BackgroundJobs.Finish(jobId);
                    return;
                }
                BackupProgress?.Invoke(entry.Id, message, fraction);
            }
        }
        try
        {
            ReportProgress("Ожидаю очереди…", null);
            await _gate.WaitAsync(token);
            acquired = true;
            lock (_runsGate) attempt.Started = true;
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            startedAt = DateTime.Now;
            _lastAttempt[entry.Id] = startedAt;
            var state = AppSettings.LoadBackupRunState(entry.Id);
            AppSettings.SaveBackupRunState(entry.Id, state with
            {
                StartedAt = startedAt, Outcome = BackupRunOutcome.Running, Error = null,
            });
            ReportProgress("Копирую…", null);
            Log.Information("Резервное копирование базы «{Name:l}» начато ({Kind:l})",
                            entry.Name, manual ? "вручную" : "по расписанию");
            BackupStarted?.Invoke(entry.Id);

            var folder = AppSettings.ResolveBackupFolder();
            var keep   = AppSettings.LoadBackupKeep();
            var ext    = BackupRotation.Extension(entry.IsPostgres);
            var target = Path.Combine(folder, BackupRotation.CopyFileName(entry.Name, startedAt, ext));

            var error = await Task.Run(() => ExecuteAsync(entry, folder, target, keep, ReportProgress, token));
            lock (progressGate) acceptingProgress = false;
            if (_disposed) return;

            if (error is null)
            {
                AppSettings.SaveBackupLastRun(entry.Id, startedAt);
                AppSettings.SaveBackupRunState(entry.Id, AppSettings.LoadBackupRunState(entry.Id) with
                {
                    LastSuccess = DateTime.Now, Outcome = BackupRunOutcome.Succeeded, Error = null,
                });
                _lastAttempt.Remove(entry.Id);
                BackgroundJobs.Finish(jobId);
                BackupFinished?.Invoke(entry.Id, true, "");
            }
            else
            {
                ReportFailure(entry, manual, error, null, jobId);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (progressGate) acceptingProgress = false;
            if (!_disposed)
            {
                _lastAttempt[entry.Id] = DateTime.Now;
                bool anotherRunning;
                lock (_runsGate)
                    anotherRunning = _runs.Values.Any(r => r != attempt && r.DatabaseId == entry.Id && r.Started);
                // Отмена второго запроса в очереди не затирает Running первого.
                if (!anotherRunning) AppSettings.SaveBackupRunState(entry.Id, AppSettings.LoadBackupRunState(entry.Id) with
                {
                    Outcome = BackupRunOutcome.Canceled, Error = CanceledMessage,
                });
                BackgroundJobs.Finish(jobId);
                Log.Information("Резервное копирование базы «{Name:l}» отменено", entry.Name);
                BackupFinished?.Invoke(entry.Id, false, CanceledMessage);
            }
        }
        catch (Exception ex)
        {
            lock (progressGate) acceptingProgress = false;
            if (!_disposed) ReportFailure(entry, manual, ex.Message, ex, jobId);
        }
        finally
        {
            lock (progressGate) acceptingProgress = false;
            lock (_runsGate) _runs.Remove(runId);
            if (acquired) _gate.Release();
        }
    }

    // Фоновая часть: сама копия и ротация. Возвращает текст ошибки или null.
    private async Task<string?> ExecuteAsync(DatabaseRefEntity entry, string folder, string target, int keep,
                                            Action<string, double?> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(folder);

        if (entry.IsPostgres)
        {
            if (entry.ToPgInfo() is not { } info)
                return "Запись подключения повреждена — переподключите базу заново.";
            progress($"Сохраняю PostgreSQL «{entry.Name}»…", null);
            var error = await PgDump.RunAsync(info, entry.Name, target, cancellationToken);
            if (error is not null) return error;
        }
        else
        {
            if (!File.Exists(entry.Path))
                return $"Файл базы не найден: {entry.Path}";
            // BackupTo сам проверяет целостность копии и пишет строку успеха в журнал.
            DatabaseManager.BackupTo(entry.Path, entry.Name, target, progress, cancellationToken);
        }

        Rotate(entry, folder, keep);
        return null;
    }

    private static void Cancel(CancellationTokenSource cancellation)
    {
        // Ссылка Stop могла остаться у уже открытой строки интерфейса после завершения.
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private sealed class BackupAttempt(int databaseId, CancellationTokenSource cancellation)
    {
        public int DatabaseId { get; } = databaseId;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public bool Started { get; set; }
    }

    // Уборка старых копий ЭТОЙ базы сверх лимита. Сбой уборки не валит бэкап: копия
    // уже снята, а лишний файл — меньшее зло, чем «ошибка» после успешной копии.
    private static void Rotate(DatabaseRefEntity entry, string folder, int keep)
    {
        try
        {
            var ext = BackupRotation.Extension(entry.IsPostgres);
            var names = Directory.EnumerateFiles(folder).Select(Path.GetFileName).Select(n => n!).ToList();

            // Недописанные копии этой базы, брошенные оборванным запуском (выход из программы,
            // пропавший диск): копии идут строго по одной, и своя к этому моменту уже на месте.
            foreach (var part in names.Where(n => BackupRotation.IsPartOf(n, entry.Name, ext)))
                File.Delete(Path.Combine(folder, part));

            var copies = names.Where(n => BackupRotation.IsCopyOf(n, entry.Name, ext)).ToList();
            var doomed = BackupRotation.SelectForDeletion(copies, keep);
            foreach (var name in doomed)
                File.Delete(Path.Combine(folder, name));
            if (doomed.Count > 0)
                Log.Information("Резервное копирование: удалено старых копий базы «{Name:l}»: {Count}",
                                entry.Name, doomed.Count);
        }
        catch (Exception ex)
        {
            if (Diag.Throttle($"backup-rotate:{entry.Id}", TimeSpan.FromMinutes(10)))
                Log.Warning(ex, "Резервное копирование: старые копии базы «{Name:l}» не убраны", entry.Name);
        }
    }

    private void ReportFailure(DatabaseRefEntity entry, bool manual, string message, Exception? ex, string jobId)
    {
        AppSettings.SaveBackupRunState(entry.Id, AppSettings.LoadBackupRunState(entry.Id) with
        {
            Outcome = BackupRunOutcome.Failed, Error = message,
        });
        BackgroundJobs.Fail(jobId, JobTitle, $"{entry.Name}: {message}", JobIcon);
        // Ручной запуск — след всегда (разовое действие человека); плановый — не чаще
        // раза в период: до починки каждый ретрай падал бы той же записью.
        if (manual || Diag.Throttle($"backup:{entry.Id}", TimeSpan.FromMinutes(10)))
            Log.Error(ex, "Резервная копия базы «{Name:l}» не создана: {Reason:l}", entry.Name, message);
        BackupFinished?.Invoke(entry.Id, false, message);
    }
}
