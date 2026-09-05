using Avalonia.Threading;
using Serilog;
using SmartBase.Database.Entities;
using SmartBase.Logging;
using SmartBase.Utils;

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
    private DispatcherTimer? _timer;
    private bool _ticking;   // тики не накладываются; все входы — с UI-потока

    public BackupService(DatabaseRegistry registry) => _registry = registry;

    /// <summary>Запуск из UI-потока (MainWindow.Opened). Идемпотентен: Opened приходит
    /// снова при каждом возврате окна из трея (см. NotificationService.Start), повторный
    /// вызов не заводит второй таймер. Первый тик — через минуту: старт приложения и так
    /// занят монтированием баз, а догон пропущенного минуты не боится.</summary>
    public void Start()
    {
        if (_timer is not null) return;

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
        if (_timer is null) return;
        _timer.Stop();
        _timer = null;
        Log.Information("Резервное копирование: служба остановлена");
    }

    /// <summary>Снять копию немедленно (кнопка «Копировать сейчас»). Ждет своей очереди,
    /// если сейчас идет другая копия; бэкоф ретраев не действует — человек попросил явно.
    /// Успешная ручная копия двигает lastRun: плановая в тот же день не дублируется.</summary>
    public async Task RunNowAsync(int dbId)
    {
        var entry = _registry.GetAll().FirstOrDefault(d => d.Id == dbId);
        if (entry is null) return;
        await RunOneAsync(entry, manual: true);
    }

    /// <summary>Один плановый проход (для тестов — без ожидания таймера).</summary>
    public Task TickNowAsync() => TickAsync();

    private async Task TickAsync()
    {
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
    private async Task RunOneAsync(DatabaseRefEntity entry, bool manual)
    {
        await _gate.WaitAsync();
        var startedAt = DateTime.Now;
        _lastAttempt[entry.Id] = startedAt;
        try
        {
            Log.Information("Резервное копирование базы «{Name:l}» начато ({Kind:l})",
                            entry.Name, manual ? "вручную" : "по расписанию");
            BackupStarted?.Invoke(entry.Id);

            var folder = AppSettings.ResolveBackupFolder();
            var keep   = AppSettings.LoadBackupKeep();
            var ext    = BackupRotation.Extension(entry.IsPostgres);
            var target = Path.Combine(folder, BackupRotation.CopyFileName(entry.Name, startedAt, ext));

            var error = await Task.Run(() => ExecuteAsync(entry, folder, target, keep));

            if (error is null)
            {
                AppSettings.SaveBackupLastRun(entry.Id, startedAt);
                _lastAttempt.Remove(entry.Id);
                BackupFinished?.Invoke(entry.Id, true, "");
            }
            else
            {
                ReportFailure(entry, manual, error, null);
            }
        }
        catch (Exception ex)
        {
            ReportFailure(entry, manual, ex.Message, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Фоновая часть: сама копия и ротация. Возвращает текст ошибки или null.
    private async Task<string?> ExecuteAsync(DatabaseRefEntity entry, string folder, string target, int keep)
    {
        Directory.CreateDirectory(folder);

        if (entry.IsPostgres)
        {
            if (entry.ToPgInfo() is not { } info)
                return "Запись подключения повреждена — переподключите базу заново.";
            var error = await PgDump.RunAsync(info, entry.Name, target);
            if (error is not null) return error;
        }
        else
        {
            if (!File.Exists(entry.Path))
                return $"Файл базы не найден: {entry.Path}";
            // BackupTo сам проверяет целостность копии и пишет строку успеха в журнал.
            DatabaseManager.BackupTo(entry.Path, entry.Name, target,
                (msg, frac) => BackupProgress?.Invoke(entry.Id, msg, frac));
        }

        Rotate(entry, folder, keep);
        return null;
    }

    // Уборка старых копий ЭТОЙ базы сверх лимита. Сбой уборки не валит бэкап: копия
    // уже снята, а лишний файл — меньшее зло, чем «ошибка» после успешной копии.
    private static void Rotate(DatabaseRefEntity entry, string folder, int keep)
    {
        try
        {
            var ext = BackupRotation.Extension(entry.IsPostgres);
            var copies = Directory.EnumerateFiles(folder)
                                  .Select(Path.GetFileName)
                                  .Where(n => n is not null && BackupRotation.IsCopyOf(n, entry.Name, ext))
                                  .Select(n => n!)
                                  .ToList();
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

    private void ReportFailure(DatabaseRefEntity entry, bool manual, string message, Exception? ex)
    {
        // Ручной запуск — след всегда (разовое действие человека); плановый — не чаще
        // раза в период: до починки каждый ретрай падал бы той же записью.
        if (manual || Diag.Throttle($"backup:{entry.Id}", TimeSpan.FromMinutes(10)))
            Log.Error(ex, "Резервная копия базы «{Name:l}» не создана: {Reason:l}", entry.Name, message);
        BackupFinished?.Invoke(entry.Id, false, message);
    }
}
