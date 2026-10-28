using System.Threading.Tasks;
using Avalonia.Threading;
using Serilog;
using SmartBase.Core.Models;
using SmartBase.Logging;
using SmartBase.Utils;

namespace SmartBase.Notifications;

/// <summary>
/// Оркестратор уведомлений: пока приложение открыто, раз в <see cref="TickSeconds"/> секунд
/// (и один раз на старте — догон пропущенного) собирает наступившие события календаря и новые
/// события задач, показывает нативные баннеры (<see cref="INativeNotifier"/>) и ведет
/// значок-счетчик на иконке (<see cref="IAppBadge"/>). Служба живет весь сеанс вместе с
/// процессом — в фоновом режиме и при скрытом окне тоже.
///
/// Источников два, оркестратор один: значок и счетчик непросмотренного — общий ресурс, и
/// разводить их по двум службам значило бы делить одно состояние на двоих. Каждый источник
/// гейтится своим тумблером «Настройки → Общее»: календарь, назначение задачи, смена стадии.
///
/// Логику отбора держат чистые <see cref="NotificationScheduler"/> (календарь) и
/// <see cref="TaskEventScheduler"/> (задачи), поэтому сам сервис покрывается интеграционным
/// тестом через прямой вызов <see cref="Tick"/> (без таймера/UI): зависимости — источники,
/// часы и запись метки — внедряются.
///
/// Поток БД: продакшн-тик (<see cref="TickBackground"/> из таймера/старта) уводит тяжелое
/// чтение источников на пул потоков, а показ плашек, значок, метку и курсоры возвращает в
/// UI-поток — чтобы тик не «морозил» интерфейс на больших БД. Источники при этом читают
/// СВЕЖИЕ подключения (new AppDbContext), а не общий контекст (он однопоточный). Синхронный
/// <see cref="Tick"/> остается для интеграционных тестов (прямой вызов без таймера/UI).
/// </summary>
public sealed class NotificationService : IDisposable
{
    private const int TickSeconds = 30;

    private readonly INativeNotifier _notifier;
    private readonly IAppBadge       _badge;
    // Источник наступивших событий за окно [from, to): по всем видимым БД (см. MainWindow).
    private readonly Func<DateTime, DateTime, IReadOnlyList<CalendarRecord>> _eventSource;
    private readonly Action<DateTime> _persistWatermark;
    private readonly Func<DateTime>   _clock;
    private readonly int _maxBanners;
    // Источник событий задач (курсоры прочитанного держит он сам); null — задачная ветка
    // выключена целиком (тесты календаря, старые вызовы конструктора).
    private readonly ITaskEventSource? _taskSource;

    private DispatcherTimer? _timer;
    // Стандарты те же, что в AppSettings: календарь включен, задачи — по желанию.
    private bool     _calendarOn     = true;
    private bool     _taskAssignedOn = false;
    private bool     _taskStatusOn   = false;
    private bool     _backupOn       = false;
    private DateTime _watermark;
    // Тумблер задачных видов включили — на ближайшем тике не читаем накопленное, а сдвигаем
    // курсоры на хвост: включение не должно вываливать историю (симметрия с водяной меткой).
    private bool _reseedTasks;
    // Идет фоновое чтение тика — второй поверх не запускаем (чтение обычно десятки мс, но на
    // холодном кеше может затянуться дольше интервала таймера). Читается/пишется на UI-потоке.
    private bool _ticking;
    // Показано, но еще «не увидено» (окно не активировали) — это и есть число на значке.
    private int _unseen;

    // Хоть один источник включен: тик целиком гейтится этим.
    private bool AnyOn => _calendarOn || _taskAssignedOn || _taskStatusOn || _backupOn;
    private bool TasksOn => _taskSource is not null && (_taskAssignedOn || _taskStatusOn);

    // Недавно показанные цели по метке (tag) — чтобы по клику отдать полные данные без
    // повторного запроса в БД. Ограничено, чтобы не расти в долгих сессиях.
    // Пишется с UI-потока (Remember), читается из потока нотифайера (клик по баннеру) —
    // поэтому под локом: одновременный доступ к Dictionary портит его внутреннее состояние.
    private readonly Dictionary<string, object> _recent = new();
    private readonly Lock _recentLock = new();
    private const int RecentLimit = 200;

    /// <summary>Пользователь кликнул по баннеру записи — MainWindow активирует окно и открывает
    /// окно с полной информацией и кнопкой «Принято». Поднимается из потока нотифайера —
    /// подписчик обязан маршалить в UI-поток.</summary>
    public event Action<CalendarRecord>? RecordActivated;

    /// <summary>Пользователь кликнул по баннеру задачи — MainWindow открывает карточку задачи.
    /// Поднимается из потока нотифайера: подписчик обязан маршалить в UI-поток.</summary>
    public event Action<TaskEvent>? TaskActivated;
    public event Action<int>? BackupActivated;
    private sealed record BackupTarget(int DbId);

    public NotificationService(
        INativeNotifier notifier,
        IAppBadge badge,
        Func<DateTime, DateTime, IReadOnlyList<CalendarRecord>> eventSource,
        Action<DateTime> persistWatermark,
        Func<DateTime>? clock = null,
        int maxBanners = 5,
        ITaskEventSource? taskSource = null)
    {
        _notifier         = notifier;
        _badge            = badge;
        _eventSource      = eventSource;
        _persistWatermark = persistWatermark;
        _clock            = clock ?? (() => DateTime.Now);
        _maxBanners       = maxBanners;
        _taskSource       = taskSource;
        _watermark        = _clock();
        _notifier.Activated += OnNotifierActivated;
    }

    // Клик по баннеру: находим цель по метке и просим MainWindow ее открыть — окно записи для
    // события календаря, карточку задачи для события задачи (метки различаются приставкой).
    // Сводка (пустая метка / цели нет в кэше) — только активирует приложение.
    private void OnNotifierActivated(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return;
        object? target;
        lock (_recentLock) target = _recent.TryGetValue(tag, out var t) ? t : null;
        switch (target)
        {
            case CalendarRecord rec: RecordActivated?.Invoke(rec); break;
            case TaskEvent e:        TaskActivated?.Invoke(e);     break;
            case BackupTarget b:    BackupActivated?.Invoke(b.DbId); break;
        }
    }

    /// <summary>Запуск из UI-потока (MainWindow.Opened): читает тумблер и метку из app.db,
    /// заводит таймер и делает первый проход — догон событий, наступивших при закрытом
    /// приложении. Первый запуск (метки нет) фиксирует «сейчас», чтобы не показать историю.
    ///
    /// Идемпотентен: <c>Opened</c> приходит СНОВА при каждом возврате окна из трея (после
    /// <c>Hide()</c> следующий <c>Window.Show()</c> заново поднимает это событие), а служба
    /// живет весь сеанс. Без проверки каждое открытие заводило бы ЕЩЕ один таймер поверх
    /// работающего (старый никто не останавливал) — тики множились бы, а метка откатывалась
    /// к последней сохраненной на диск.</summary>
    public void Start()
    {
        if (_timer is not null) return;   // уже запущены — повторный Opened ничего не меняет

        _calendarOn     = AppSettings.LoadNotificationsEnabled();
        _taskAssignedOn = AppSettings.LoadNotifyTaskAssigned();
        _taskStatusOn   = AppSettings.LoadNotifyTaskStatus();
        _backupOn       = AppSettings.LoadNotifyBackup();

        var saved = AppSettings.LoadNotifyWatermark();
        _watermark = saved ?? _clock();
        if (saved is null) Persist(_watermark);   // первый старт — зафиксировать точку отсчета

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TickSeconds) };
        _timer.Tick += (_, _) => TickBackground();
        _timer.Start();

        // Служба без окна — ее запуск и состояние тумблеров в журнал: «напоминание не пришло»
        // начинают разбирать с вопроса, была ли служба вообще включена.
        Log.Information("Служба уведомлений запущена: календарь {Calendar:l}, назначение задач {Assigned:l}, стадии задач {Status:l}, метка {Watermark:l}",
                        OnOff(_calendarOn), OnOff(_taskAssignedOn), OnOff(_taskStatusOn), _watermark.ToString("yyyy-MM-dd HH:mm"));

        TickBackground();   // догон пропущенного (если включено) — тоже вне UI-потока
    }

    private static string OnOff(bool on) => on ? "вкл" : "выкл";
    // Повторяющиеся сбои тика (база пропала) — не чаще раза в период на причину.
    private static readonly TimeSpan FailLogPeriod = TimeSpan.FromMinutes(10);

    /// <summary>Синхронный проход: собрать наступившие, показать плашки, обновить значок и метку.
    /// ЧИТАЕТ источник на вызывающем потоке — используется интеграционными тестами напрямую;
    /// в приложении тик идет через <see cref="TickBackground"/> (чтение вне UI-потока).
    /// Возвращает решение планировщика.</summary>
    public NotificationScheduler.Decision Tick()
    {
        var now = _clock();

        if (TasksOn) TickTasksSync();

        if (!_calendarOn)
            return new NotificationScheduler.Decision([], 0, _watermark);

        // Источник намеренно отдает лишнее (буфер многодневных) — точную отсечку по StartDt
        // делает планировщик. Небольшой заглляд вперед, чтобы событие ровно на границе now
        // не проскочило мимо (SQL-верхняя граница строгая).
        var candidates = _eventSource(_watermark, now.AddMinutes(1));
        return Apply(candidates, _watermark, now);
    }

    // Задачная ветка синхронного тика (тесты): читает и сразу применяет на этом же потоке.
    private void TickTasksSync()
    {
        if (_reseedTasks)
        {
            _taskSource!.Reseed();
            _reseedTasks = false;
            _taskSource.Commit();
            return;
        }

        ApplyTasks(_taskSource!.Read());
    }

    /// <summary>Продакшн-тик: тяжелое чтение источника (скан calendar_index) уводим с UI-потока
    /// на пул, а показ плашек, значок и запись метки возвращаем в UI-поток — так интерфейс не
    /// «морозит» даже на больших БД. Пересекающиеся запуски отсекаем (<see cref="_ticking"/>):
    /// чтение обычно десятки мс, но на холодном кеше может затянуться дольше интервала таймера.</summary>
    private void TickBackground()
    {
        if (!AnyOn || _ticking) return;
        _ticking = true;
        var now       = _clock();
        var watermark = _watermark;   // снимок на UI-потоке; окно чтения — (watermark, now+1мин]
        var calendarOn = _calendarOn;
        var tasksOn    = TasksOn;
        var reseed     = _reseedTasks;
        Task.Run(() =>
        {
            IReadOnlyList<CalendarRecord>? candidates = null;
            if (calendarOn)
            {
                try { candidates = _eventSource(watermark, now.AddMinutes(1)); }
                catch (Exception ex)
                {
                    // БД могла отвалиться — пропускаем тик, метку не двигаем. След — один на период.
                    if (Diag.Throttle("notify:calendar-read", FailLogPeriod))
                        Log.Warning(ex, "Уведомления: события календаря не прочитаны — тик пропущен, метка не сдвинута");
                }
            }

            // Задачная ветка читается своими подключениями и своим try/catch: упавшая база
            // одного источника не должна гасить второй.
            IReadOnlyList<TaskEvent>? events = null;
            if (tasksOn)
            {
                try
                {
                    if (reseed)
                    {
                        _taskSource!.Reseed();
                        Log.Information("Уведомления по задачам: курсоры очередей сброшены на хвост — накопленное за время выключения не показывается");
                    }
                    else events = _taskSource!.Read();
                }
                catch (Exception ex)
                {
                    // Очередь недоступна — попробуем следующим тиком.
                    if (Diag.Throttle("notify:tasks-read", FailLogPeriod))
                        Log.Warning(ex, "Уведомления: очередь событий задач недоступна — тик пропущен");
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                // За время чтения тумблеры могли выключиться — уважаем актуальное состояние.
                try
                {
                    if (candidates is not null && _calendarOn) Apply(candidates, watermark, now);
                    if (tasksOn && reseed)
                    {
                        _reseedTasks = false;
                        _taskSource!.Commit();
                    }
                    else if (events is not null && TasksOn)
                    {
                        ApplyTasks(events);
                    }
                }
                finally { _ticking = false; }
            });
        });
    }

    // Применить решение планировщика: показать плашки, накопить значок, сдвинуть и (при показе)
    // сохранить метку. Меняет состояние службы — обязан идти на UI-потоке (Tick напрямую или
    // UI-пост из TickBackground).
    private NotificationScheduler.Decision Apply(
        IReadOnlyList<CalendarRecord> candidates, DateTime watermark, DateTime now)
    {
        var decision = NotificationScheduler.Decide(candidates, watermark, now, _maxBanners);

        foreach (var b in decision.Banners)
        {
            if (b.Record is { } rec) Remember(b.Tag, rec);
            SafeShow(b.Title, b.Body, b.Tag);
        }

        if (decision.FiredCount > 0)
        {
            _unseen += decision.FiredCount;
            SafeBadge(_unseen);
        }

        _watermark = decision.NewWatermark;
        // Пишем метку только когда что-то показали: между показами она в памяти уходит вперед,
        // а на диск попадает лишь после показа — при падении не будет повторного показа.
        if (decision.FiredCount > 0) Persist(_watermark);

        return decision;
    }

    // Задачная половина прохода: показать плашки, накопить значок и зафиксировать курсоры.
    // Курсоры фиксируем ВСЕГДА, а не только после показа: за прочитанным диапазоном стоят и
    // чужие события, которые иначе перечитывались бы каждым тиком по кругу.
    // Меняет состояние службы — обязан идти на UI-потоке.
    private TaskEventScheduler.Decision ApplyTasks(IReadOnlyList<TaskEvent> events)
    {
        var decision = TaskEventScheduler.Decide(events, _taskAssignedOn, _taskStatusOn, _maxBanners);

        foreach (var b in decision.Banners)
        {
            if (b.Event is { } e) Remember(b.Tag, e);
            SafeShow(b.Title, b.Body, b.Tag);
        }

        if (decision.FiredCount > 0)
        {
            _unseen += decision.FiredCount;
            SafeBadge(_unseen);
        }

        try { _taskSource?.Commit(); }
        catch (Exception ex)
        {
            // Курсор не записался — увидим событие дважды; повтор баннера объясняется только этой записью.
            Log.Warning(ex, "Уведомления: курсор очереди задач не сохранен в app.db — события могут показаться повторно");
        }
        return decision;
    }

    /// <summary>Живое переключение тумблера «Общее → Уведомления календаря». Включение стартует
    /// «с чистого листа» (метка = сейчас), чтобы не показать разом все, что прошло, пока было
    /// выключено; выключение гасит значок, если это был последний включенный источник.</summary>
    public void SetEnabled(bool on)
    {
        if (on == _calendarOn) return;
        _calendarOn = on;
        Log.Information("Уведомления календаря {State:l} в настройках", on ? "включены" : "выключены");
        if (on)
        {
            _watermark = _clock();
            Persist(_watermark);
        }
        else ClearBadgeIfAllOff();
    }

    /// <summary>Живое переключение тумблера «Уведомления о назначении задачи».</summary>
    public void SetTaskAssignedEnabled(bool on) => SetTaskFlag(ref _taskAssignedOn, on, "о назначении задачи");

    /// <summary>Живое переключение тумблера «Уведомления о смене стадии задачи».</summary>
    public void SetTaskStatusEnabled(bool on) => SetTaskFlag(ref _taskStatusOn, on, "о смене стадии задачи");

    public void SetBackupEnabled(bool on)
    {
        _backupOn = on;
        if (!on) ClearBadgeIfAllOff();
    }

    /// <summary>Результат ручной или плановой копии. UI-поток; выключенный тумблер
    /// подавляет баннер и счетчик, но не скрывает ошибку в фоновых работах.</summary>
    public void NotifyBackupResult(int dbId, string name, bool ok, string message, bool canceled = false)
    {
        if (!_backupOn) return;
        var tag = $"backup:{dbId}:{Guid.NewGuid():N}";
        Remember(tag, new BackupTarget(dbId));
        SafeShow(canceled ? "Резервное копирование отменено"
                : ok ? "Резервная копия создана" : "Ошибка резервного копирования",
            ok ? $"База «{name}»: копирование завершено" : $"База «{name}»: {message}", tag);
        _unseen++;
        SafeBadge(_unseen);
    }

    // Включение задачного вида — не показывать накопленное: на ближайшем тике курсоры уедут
    // на хвост очередей (аналог «метка = сейчас» у календаря).
    private void SetTaskFlag(ref bool flag, bool on, string what)
    {
        if (on == flag) return;
        flag = on;
        Log.Information("Уведомления {What:l} {State:l} в настройках", what, on ? "включены" : "выключены");
        if (on) _reseedTasks = true;
        else    ClearBadgeIfAllOff();
    }

    // Значок общий на оба источника: гасим его, только когда молчат все. Иначе выключение
    // календаря стирало бы счет, накопленный задачами.
    private void ClearBadgeIfAllOff()
    {
        if (AnyOn) return;
        _unseen = 0;
        SafeBadge(0);
    }

    /// <summary>Окно активировали — пользователь «увидел» уведомления: гасим значок.</summary>
    public void NotifyWindowActivated()
    {
        if (_unseen == 0) return;
        _unseen = 0;
        SafeBadge(0);
    }

    /// <summary>Тестовый/стартовый посев состояния без таймера и app.db.
    /// <paramref name="enabled"/> — календарный источник; задачные виды посеваются
    /// <see cref="ConfigureTasks"/> (по умолчанию включены оба).</summary>
    public void Configure(bool enabled, DateTime watermark)
    {
        _calendarOn = enabled;
        _watermark  = watermark;
    }

    /// <summary>Тестовый посев задачных тумблеров (без app.db и живого переключения).</summary>
    public void ConfigureTasks(bool assignedOn, bool statusOn)
    {
        _taskAssignedOn = assignedOn;
        _taskStatusOn   = statusOn;
        _reseedTasks    = false;
    }

    public void Dispose()
    {
        if (_timer is not null) Log.Information("Служба уведомлений остановлена");
        _timer?.Stop();
        _timer = null;   // симметрично Start(): служба остановлена — ее можно запустить заново
        _notifier.Activated -= OnNotifierActivated;
    }

    // Запомнить цель под меткой для маршрутизации клика; при переполнении чистим кэш
    // (старые баннеры пользователь уже вряд ли откроет).
    private void Remember(string tag, object target)
    {
        if (string.IsNullOrEmpty(tag)) return;
        lock (_recentLock)
        {
            if (_recent.Count >= RecentLimit) _recent.Clear();
            _recent[tag] = target;
        }
    }

    private void Persist(DateTime w)
    {
        try { _persistWatermark(w); }
        catch (Exception ex)
        {
            // Запись метки не критична для тика, но без нее после перезапуска придет повтор.
            if (Diag.Throttle("notify:persist", FailLogPeriod))
                Log.Warning(ex, "Уведомления: метка времени не сохранена в app.db");
        }
    }

    private void SafeShow(string title, string body, string tag)
    {
        try { _notifier.Show(title, body, tag); }
        catch (Exception ex)
        {
            // Сбой ОС-баннера не ломает цикл, но «напоминание не пришло» объясняется только здесь.
            if (Diag.Throttle("notify:show", FailLogPeriod))
                Log.Warning(ex, "Баннер ОС не показан: {Title:l}", title);
        }
    }

    private void SafeBadge(int count)
    {
        try { _badge.SetCount(count); }
        catch (Exception ex)
        {
            if (Diag.Throttle("notify:badge", FailLogPeriod))
                Log.Warning(ex, "Значок-счетчик уведомлений не обновлен");
        }
    }
}
