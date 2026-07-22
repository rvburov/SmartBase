using Avalonia.Threading;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Notifications;

/// <summary>
/// Оркестратор уведомлений: пока приложение открыто, раз в <see cref="TickSeconds"/> секунд
/// (и один раз на старте — догон пропущенного) собирает наступившие события календаря,
/// показывает нативные баннеры (<see cref="INativeNotifier"/>) и ведёт значок-счётчик на
/// иконке (<see cref="IAppBadge"/>). Фонового режима нет: служба живёт вместе с окном.
///
/// Логику отбора держит чистый <see cref="NotificationScheduler"/>, поэтому сам сервис
/// покрывается интеграционным тестом через прямой вызов <see cref="Tick"/> (без таймера/UI):
/// зависимости — источник событий, часы и запись метки — внедряются.
///
/// Поток БД: <see cref="Tick"/> зовётся из UI-потока (DispatcherTimer / Opened), а
/// источник событий читает общий контекст — так соблюдается правило «общий AppDbContext
/// только из UI-потока».
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
    // Показано, но ещё «не увидено» (окно не активировали) — это и есть число на значке.
    private int _unseen;

    // Недавно показанные записи по метке (tag) — чтобы по клику отдать полную запись в окно
    // без повторного запроса в БД. Ограничено, чтобы не расти в долгих сессиях.
    private readonly Dictionary<string, CalendarRecord> _recent = new();
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
        if (!string.IsNullOrEmpty(tag) && _recent.TryGetValue(tag, out var rec))
            RecordActivated?.Invoke(rec);
    }

    /// <summary>Запуск из UI-потока (MainWindow.Opened): читает тумблер и метку из app.db,
    /// заводит таймер и делает первый проход — догон событий, наступивших при закрытом
    /// приложении. Первый запуск (метки нет) фиксирует «сейчас», чтобы не показать историю.</summary>
    public void Start()
    {
        _enabled = AppSettings.LoadNotificationsEnabled();

        var saved = AppSettings.LoadNotifyWatermark();
        _watermark = saved ?? _clock();
        if (saved is null) Persist(_watermark);   // первый старт — зафиксировать точку отсчёта

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TickSeconds) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Tick();   // догон пропущенного (если включено)
    }

    /// <summary>Один проход: собрать наступившие, показать плашки, обновить значок и метку.
    /// Возвращает решение планировщика — для интеграционных тестов.</summary>
    public NotificationScheduler.Decision Tick()
    {
        var now = _clock();
        if (!_enabled)
            return new NotificationScheduler.Decision([], 0, _watermark);

        // Источник намеренно отдаёт лишнее (буфер многодневных) — точную отсечку по StartDt
        // делает планировщик. Небольшой заглляд вперёд, чтобы событие ровно на границе now
        // не проскочило мимо (SQL-верхняя граница строгая).
        var candidates = _eventSource(_watermark, now.AddMinutes(1));
        var decision   = NotificationScheduler.Decide(candidates, _watermark, now, _maxBanners);

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
        // Пишем метку только когда что-то показали: между показами она в памяти уходит вперёд,
        // а на диск попадает лишь после показа — при падении не будет повторного показа.
        if (decision.FiredCount > 0) Persist(_watermark);

        return decision;
    }

    /// <summary>Живое переключение тумблера «Общее → Уведомления». Включение стартует «с
    /// чистого листа» (метка = сейчас), чтобы не показать разом всё, что прошло, пока было
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
        _notifier.Activated -= OnNotifierActivated;
    }

    // Запомнить запись под меткой для маршрутизации клика; при переполнении чистим кэш
    // (старые баннеры пользователь уже вряд ли откроет).
    private void Remember(string tag, CalendarRecord rec)
    {
        if (string.IsNullOrEmpty(tag)) return;
        if (_recent.Count >= RecentLimit) _recent.Clear();
        _recent[tag] = rec;
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
