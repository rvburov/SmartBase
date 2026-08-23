using System.Threading.Tasks;
using Avalonia.Threading;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Notifications;

/// <summary>
/// Оркестратор уведомлений: пока приложение открыто, раз в <see cref="TickSeconds"/> секунд
/// (и один раз на старте — догон пропущенного) собирает наступившие события календаря,
/// показывает нативные баннеры (<see cref="INativeNotifier"/>) и ведет значок-счетчик на
/// иконке (<see cref="IAppBadge"/>). Служба живет весь сеанс вместе с процессом — в фоновом
/// режиме и при скрытом окне тоже.
///
/// Логику отбора держит чистый <see cref="NotificationScheduler"/>, поэтому сам сервис
/// покрывается интеграционным тестом через прямой вызов <see cref="Tick"/> (без таймера/UI):
/// зависимости — источник событий, часы и запись метки — внедряются.
///
/// Поток БД: продакшн-тик (<see cref="TickBackground"/> из таймера/старта) уводит тяжелое
/// чтение источника на пул потоков, а показ плашек, значок и метку возвращает в UI-поток —
/// чтобы тик не «морозил» интерфейс на больших БД. Источник при этом читает СВЕЖЕЕ подключение
/// (new AppDbContext), а не общий контекст (он однопоточный). Синхронный <see cref="Tick"/>
/// остается для интеграционных тестов (прямой вызов без таймера/UI).
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

    private DispatcherTimer? _timer;
    private bool     _enabled  = true;
    private DateTime _watermark;
    // Идет фоновое чтение тика — второй поверх не запускаем (чтение обычно десятки мс, но на
    // холодном кеше может затянуться дольше интервала таймера). Читается/пишется на UI-потоке.
    private bool _ticking;
    // Показано, но еще «не увидено» (окно не активировали) — это и есть число на значке.
    private int _unseen;

    // Недавно показанные записи по метке (tag) — чтобы по клику отдать полную запись в окно
    // без повторного запроса в БД. Ограничено, чтобы не расти в долгих сессиях.
    // Пишется с UI-потока (Remember), читается из потока нотифайера (клик по баннеру) —
    // поэтому под локом: одновременный доступ к Dictionary портит его внутреннее состояние.
    private readonly Dictionary<string, CalendarRecord> _recent = new();
    private readonly Lock _recentLock = new();
    private const int RecentLimit = 200;

    /// <summary>Пользователь кликнул по баннеру записи — MainWindow активирует окно и открывает
    /// окно с полной информацией и кнопкой «Принято». Поднимается из потока нотифайера —
    /// подписчик обязан маршалить в UI-поток.</summary>
    public event Action<CalendarRecord>? RecordActivated;

    public NotificationService(
        INativeNotifier notifier,
        IAppBadge badge,
        Func<DateTime, DateTime, IReadOnlyList<CalendarRecord>> eventSource,
        Action<DateTime> persistWatermark,
        Func<DateTime>? clock = null,
        int maxBanners = 5)
    {
        _notifier         = notifier;
        _badge            = badge;
        _eventSource      = eventSource;
        _persistWatermark = persistWatermark;
        _clock            = clock ?? (() => DateTime.Now);
        _maxBanners       = maxBanners;
        _watermark        = _clock();
        _notifier.Activated += OnNotifierActivated;
    }

    // Клик по баннеру: находим запись по метке и просим MainWindow открыть окно записи.
    // Сводка (пустая метка / запись не в кэше) — только активирует приложение, окна нет.
    private void OnNotifierActivated(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return;
        CalendarRecord? rec;
        lock (_recentLock) rec = _recent.TryGetValue(tag, out var r) ? r : null;
        if (rec is not null) RecordActivated?.Invoke(rec);
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

        _enabled = AppSettings.LoadNotificationsEnabled();

        var saved = AppSettings.LoadNotifyWatermark();
        _watermark = saved ?? _clock();
        if (saved is null) Persist(_watermark);   // первый старт — зафиксировать точку отсчета

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TickSeconds) };
        _timer.Tick += (_, _) => TickBackground();
        _timer.Start();

        TickBackground();   // догон пропущенного (если включено) — тоже вне UI-потока
    }

    /// <summary>Синхронный проход: собрать наступившие, показать плашки, обновить значок и метку.
    /// ЧИТАЕТ источник на вызывающем потоке — используется интеграционными тестами напрямую;
    /// в приложении тик идет через <see cref="TickBackground"/> (чтение вне UI-потока).
    /// Возвращает решение планировщика.</summary>
    public NotificationScheduler.Decision Tick()
    {
        var now = _clock();
        if (!_enabled)
            return new NotificationScheduler.Decision([], 0, _watermark);

        // Источник намеренно отдает лишнее (буфер многодневных) — точную отсечку по StartDt
        // делает планировщик. Небольшой заглляд вперед, чтобы событие ровно на границе now
        // не проскочило мимо (SQL-верхняя граница строгая).
        var candidates = _eventSource(_watermark, now.AddMinutes(1));
        return Apply(candidates, _watermark, now);
    }

    /// <summary>Продакшн-тик: тяжелое чтение источника (скан calendar_index) уводим с UI-потока
    /// на пул, а показ плашек, значок и запись метки возвращаем в UI-поток — так интерфейс не
    /// «морозит» даже на больших БД. Пересекающиеся запуски отсекаем (<see cref="_ticking"/>):
    /// чтение обычно десятки мс, но на холодном кеше может затянуться дольше интервала таймера.</summary>
    private void TickBackground()
    {
        if (!_enabled || _ticking) return;
        _ticking = true;
        var now       = _clock();
        var watermark = _watermark;   // снимок на UI-потоке; окно чтения — (watermark, now+1мин]
        Task.Run(() =>
        {
            IReadOnlyList<CalendarRecord>? candidates = null;
            try { candidates = _eventSource(watermark, now.AddMinutes(1)); }
            catch { /* БД могла отвалиться — пропускаем тик, метку не двигаем */ }
            Dispatcher.UIThread.Post(() =>
            {
                // За время чтения тумблер мог выключиться — уважаем актуальное состояние.
                try { if (candidates is not null && _enabled) Apply(candidates, watermark, now); }
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

    /// <summary>Живое переключение тумблера «Общее → Уведомления». Включение стартует «с
    /// чистого листа» (метка = сейчас), чтобы не показать разом все, что прошло, пока было
    /// выключено; выключение гасит значок.</summary>
    public void SetEnabled(bool on)
    {
        if (on == _enabled) return;
        _enabled = on;
        if (on)
        {
            _watermark = _clock();
            Persist(_watermark);
        }
        else
        {
            _unseen = 0;
            SafeBadge(0);
        }
    }

    /// <summary>Окно активировали — пользователь «увидел» уведомления: гасим значок.</summary>
    public void NotifyWindowActivated()
    {
        if (_unseen == 0) return;
        _unseen = 0;
        SafeBadge(0);
    }

    /// <summary>Тестовый/стартовый посев состояния без таймера и app.db.</summary>
    public void Configure(bool enabled, DateTime watermark)
    {
        _enabled   = enabled;
        _watermark = watermark;
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;   // симметрично Start(): служба остановлена — ее можно запустить заново
        _notifier.Activated -= OnNotifierActivated;
    }

    // Запомнить запись под меткой для маршрутизации клика; при переполнении чистим кэш
    // (старые баннеры пользователь уже вряд ли откроет).
    private void Remember(string tag, CalendarRecord rec)
    {
        if (string.IsNullOrEmpty(tag)) return;
        lock (_recentLock)
        {
            if (_recent.Count >= RecentLimit) _recent.Clear();
            _recent[tag] = rec;
        }
    }

    private void Persist(DateTime w)
    {
        try { _persistWatermark(w); } catch { /* запись метки не критична */ }
    }

    private void SafeShow(string title, string body, string tag)
    {
        try { _notifier.Show(title, body, tag); } catch { /* сбой ОС-баннера не ломает цикл */ }
    }

    private void SafeBadge(int count)
    {
        try { _badge.SetCount(count); } catch { /* сбой значка не ломает цикл */ }
    }
}
