using Serilog;
using SmartBase.Database.Entities;
using SmartBase.Logging;

namespace SmartBase.Database;

/// <summary>
/// Очередь фонового монтирования БД. Старт открывает только дефолтную базу, остальные
/// встают сюда и монтируются СТРОГО ПО ОДНОЙ в фоновом потоке: параллельное монтирование
/// умножило бы память (кеш миграции) и заставило бы сортировки драться за диск, а работать
/// пользователь все равно может только в уже открытых базах. Стартовая партия сортируется
/// по размеру файла — мелкие базы оживают почти сразу, гиганты мигрируют последними.
/// Добавленные позже (подключение из настроек) встают в хвост.
///
/// События поднимаются ИЗ ФОНОВОГО ПОТОКА — подписчик (MainWindow) маршалит в UI сам.
/// </summary>
public sealed class MountQueue
{
    public enum ItemState { Queued, Mounting, Failed, Maintenance }

    /// <summary>Статус немонтированной БД для сайдбара/настроек: короткий текст обновляется
    /// живьем из тех же stage-колбэков, что сплэш и окно подключения. <c>Reason</c> у Failed —
    /// причина без обвязки («файл не найден», «связь потеряна») для ретраев монитора.</summary>
    public sealed record ItemStatus(int DbId, string Name, ItemState State, string Text, string? Reason = null);

    /// <summary>Изменился статус БД (dbId, сменилось ли состояние — не только процент).</summary>
    public event Action<int, bool>? Changed;
    /// <summary>Монтирование завершено: менеджер или null при ошибке (статус станет Failed).</summary>
    public event Action<int, DatabaseManager?>? Completed;

    private readonly Workspace _ws;
    private readonly object _gate = new();
    private readonly List<DatabaseRefEntity> _queue = [];
    private readonly Dictionary<int, ItemStatus> _status = [];
    private bool _running;

    public MountQueue(Workspace ws) => _ws = ws;

    /// <summary>Стартовая партия: мелкие файлы первыми (недоступные — нулевой размер, т.е.
    /// первыми: их сбой виден сразу и никого не задерживает).</summary>
    public void EnqueueStartupBatch(IEnumerable<DatabaseRefEntity> entries)
    {
        foreach (var e in entries.OrderBy(FileSize))
            Enqueue(e);
    }

    private static long FileSize(DatabaseRefEntity e)
    {
        // PG-записи (и недоступные файлы) — нулевой «размер»: монтируются первыми,
        // таймаут подключения к серверу 5 с (см. PgConnectionInfo) очередь не задержит.
        try { return e.IsPostgres ? 0 : new FileInfo(e.Path).Length; }
        catch { return 0; }
    }

    /// <summary>Поставить БД в хвост очереди. Повторная постановка «упавшей» базы
    /// («Подключить заново») сбрасывает ее статус на «в очереди…».</summary>
    public void Enqueue(DatabaseRefEntity entry)
    {
        lock (_gate)
        {
            if (_ws.Get(entry.Id) is not null) return;                 // уже смонтирована
            if (_queue.Any(e => e.Id == entry.Id)) return;             // уже ждет
            if (_status.TryGetValue(entry.Id, out var s) && s.State == ItemState.Mounting) return;
            _queue.Add(entry);
            _status[entry.Id] = new ItemStatus(entry.Id, entry.Name, ItemState.Queued, "в очереди…");
        }
        Changed?.Invoke(entry.Id, true);
        EnsurePump();
    }

    /// <summary>Пометить ранее смонтированную БД потерянной на ходу (сервер выключился, файл
    /// удалили — см. DbHealthMonitor): Failed-статус БЕЗ постановки в очередь, чтобы база
    /// переиспользовала всю инфраструктуру заглушек (узел «недоступна…» в сайдбаре, статус в
    /// настройках, «Подключить заново»). Переподключение ретраит монитор или пользователь.</summary>
    public void MarkFailed(int dbId, string name, string text, string? reason = null)
    {
        lock (_gate)
        {
            // База в этот момент не должна числиться ждущей (ее только что размонтировали).
            _queue.RemoveAll(e => e.Id == dbId);
            _status[dbId] = new ItemStatus(dbId, name, ItemState.Failed, text, reason);
        }
        Changed?.Invoke(dbId, true);
    }

    /// <summary>Живая надпись недоступной базы («повтор через ≈ 1 мин», «проверяю…» — ставит
    /// монитор на каждом переходе ретрая): меняется только текст, состояние и причина прежние,
    /// дерево/список не пересобираются (Changed со stateChanged=false — тем же путем, что
    /// проценты миграции). База не в состоянии Failed (уже в очереди/смонтирована) — тишина.</summary>
    public void UpdateFailedText(int dbId, string text)
    {
        lock (_gate)
        {
            if (!_status.TryGetValue(dbId, out var s) || s.State != ItemState.Failed) return;
            if (s.Text == text) return;
            _status[dbId] = s with { Text = text };
        }
        Changed?.Invoke(dbId, false);
    }

    /// <summary>Забыть БД (отключили в настройках): из очереди и статусов. Уже монтирующуюся
    /// не прерываем — по завершении MainWindow увидит, что записи в реестре нет, и размонтирует.</summary>
    public void Forget(int dbId)
    {
        lock (_gate)
        {
            _queue.RemoveAll(e => e.Id == dbId);
            if (_status.TryGetValue(dbId, out var s) && s.State != ItemState.Mounting)
                _status.Remove(dbId);
        }
        Changed?.Invoke(dbId, true);
    }

    /// <summary>
    /// Обслуживание файла УЖЕ ОТЦЕПЛЕННОЙ базы («Сжать файл»): состояние Maintenance с живой
    /// надписью («сжимаю файл · 45 % · ≈ 2 мин» — тем же путем, что проценты миграции), работа
    /// в фоновом потоке, по завершении база встает в очередь монтирования обратно.
    /// <paramref name="work"/> получает stage-колбэк формата DatabaseManager.Open («Фаза\nинфо»)
    /// и возвращает текст ошибки (null — успех); исключения тоже становятся ошибкой. Ошибка
    /// уходит в <see cref="MaintenanceFinished"/> — MainWindow покажет карточку. Базу, которая
    /// сейчас в очереди или монтируется, не трогаем (false).
    /// </summary>
    public bool RunMaintenance(DatabaseRefEntity entry, Func<Action<string, double?>, string?> work)
    {
        lock (_gate)
        {
            if (_ws.Get(entry.Id) is not null) return false;              // не отцеплена
            if (_queue.Any(e => e.Id == entry.Id)) return false;          // ждет монтирования
            if (_status.TryGetValue(entry.Id, out var s) && s.State is ItemState.Mounting or ItemState.Maintenance)
                return false;
            _status[entry.Id] = new ItemStatus(entry.Id, entry.Name, ItemState.Maintenance, "сжимаю файл…");
        }
        Changed?.Invoke(entry.Id, true);

        Task.Run(() =>
        {
            string? error;
            try { error = work((msg, frac) => OnMaintenanceStage(entry, msg, frac)); }
            catch (Exception ex)
            {
                Log.Error(ex, "Обслуживание файла БД «{Name:l}» не удалось", entry.Name);
                error = DbErrorText(ex);
            }
            // База возвращается в работу тем же путем, что при подключении. Enqueue переводит
            // Maintenance → Queued под тем же замком — без промежуточного «не подключена» в
            // списке настроек. Если базу за это время отключили (Forget снял статус), очередь
            // смонтирует и тихо размонтирует ее — как при отключении во время монтирования.
            Enqueue(entry);
            MaintenanceFinished?.Invoke(entry.Id, error);
        });
        return true;
    }

    /// <summary>Обслуживание завершено: (dbId, текст ошибки или null при успехе). Из фонового потока.</summary>
    public event Action<int, string?>? MaintenanceFinished;

    private void OnMaintenanceStage(DatabaseRefEntity entry, string msg, double? frac)
    {
        var nl    = msg.IndexOf('\n');
        var info  = nl < 0 ? "" : msg[(nl + 1)..];
        var parts = new List<string> { "сжимаю файл" };
        if (frac is { } f) parts.Add($"{(int)(Math.Clamp(f, 0, 1) * 100)}%");
        const string marker = "осталось ";
        var i = info.IndexOf(marker, StringComparison.Ordinal);
        if (i >= 0) parts.Add(info[(i + marker.Length)..].TrimEnd());
        var text = string.Join(" · ", parts);
        lock (_gate)
        {
            if (!_status.TryGetValue(entry.Id, out var s) || s.State != ItemState.Maintenance) return;
            if (s.Text == text) return;
            _status[entry.Id] = s with { Text = text };
        }
        Changed?.Invoke(entry.Id, false);
    }

    // Текст ошибки обслуживания для карточки: занятость файла — по-человечески, остальное —
    // сообщение исключения.
    private static string DbErrorText(Exception ex) =>
        DbHealthMonitor.IsBusyError(ex)
            ? "файл занят другими пользователями — попробуйте позже, когда никто не сохраняет изменения"
            : ex.Message;

    /// <summary>Статусы всех ждущих/монтирующихся/упавших БД (для узлов-заглушек сайдбара).</summary>
    public IReadOnlyList<ItemStatus> Pending()
    {
        lock (_gate) return _status.Values.ToList();
    }

    /// <summary>Короткий статус конкретной БД или null, если ее нет в очереди.</summary>
    public ItemStatus? StatusOf(int dbId)
    {
        lock (_gate) return _status.GetValueOrDefault(dbId);
    }

    private void EnsurePump()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
        }
        Task.Run(Pump);
    }

    /// <summary>Ожидание пробы пути перед монтированием сетевой базы: папка не ответила — монтирование
    /// не начинается, база помечается «недоступна (папка не отвечает)» и уходит под ретраи монитора.
    /// Раньше первое же обращение к файлу на зависшей папке вставало в ядре без предела времени: база
    /// висела «подключается…», очередь стояла, а процесс после закрытия окна не мог завершиться.</summary>
    public static TimeSpan PreMountProbeWait { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Монтирование без единого сигнала прогресса дольше этого считается зависшим: база
    /// помечается недоступной, очередь идет дальше. Само монтирование не прерывается (операцию в
    /// ядре не отменить); завершится позже — база подключится (<see cref="OnAbandonedMountFinished"/>).</summary>
    public static TimeSpan StallLimit { get; set; } = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan StallPoll = TimeSpan.FromSeconds(2);

    // Последний сигнал прогресса монтирования и его этап — для сторожа зависания.
    private sealed class MountProgress
    {
        private long _lastTicks = DateTime.UtcNow.Ticks;
        private volatile string _stage = "подключение";

        public DateTime LastActivity => new(Interlocked.Read(ref _lastTicks), DateTimeKind.Utc);
        public string   Stage        => _stage;

        public void Touch(string msg)
        {
            Interlocked.Exchange(ref _lastTicks, DateTime.UtcNow.Ticks);
            var nl = msg.IndexOf('\n');
            _stage = nl < 0 ? msg : msg[..nl];
        }
    }

    private void Pump()
    {
        while (true)
        {
            DatabaseRefEntity entry;
            lock (_gate)
            {
                if (_queue.Count == 0) { _running = false; return; }
                entry = _queue[0];
                _queue.RemoveAt(0);
                _status[entry.Id] = new ItemStatus(entry.Id, entry.Name, ItemState.Mounting, "подключается…");
            }
            Changed?.Invoke(entry.Id, true);

            DatabaseManager? m = null;
            string? blocked = null;   // причина, по которой монтирование не начато или брошено

            if (!entry.IsPostgres && NetworkPath.IsNetwork(entry.Path)
                && DbHealthMonitor.Probe(entry.Path, PreMountProbeWait) == DbHealthMonitor.ProbeOutcome.Timeout)
            {
                blocked = MountBlockedException.ShareNotResponding;
                Log.Warning("Монтирование «{Name:l}» не начато: папка не ответила за {Wait:l} ({Path:l}) — база помечена недоступной, повтор позже",
                            entry.Name, Diag.Duration(PreMountProbeWait), entry.Path);
            }
            else
            {
                // Mount() гасит ошибки сам (вернет null); страхуемся на случай неожиданных. Работа —
                // в отдельной задаче, чтобы сторож мог бросить зависшее монтирование и идти дальше.
                var progress = new MountProgress();
                var task = Task.Run(() =>
                {
                    try { return _ws.Mount(entry, (msg, frac) => { progress.Touch(msg); OnStage(entry, msg, frac); }); }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Неожиданный сбой монтирования БД «{Name:l}»", entry.Name);
                        return null;
                    }
                });
                while (!task.Wait(StallPoll))
                {
                    if (DateTime.UtcNow - progress.LastActivity < StallLimit) continue;
                    blocked = MountBlockedException.ShareNotResponding;
                    Log.Warning("Монтирование «{Name:l}» не продвигается {Stall:l} (этап «{Stage:l}», {Path:l}) — база помечена недоступной, очередь идет дальше; завершится позже — подключится",
                                entry.Name, Diag.Duration(StallLimit), progress.Stage, entry.Path);
                    _ = task.ContinueWith(t => OnAbandonedMountFinished(entry, t.Result),
                                          TaskContinuationOptions.OnlyOnRanToCompletion);
                    break;
                }
                if (blocked is null) m = task.Result;
            }

            string? reason = null;
            if (m is null)
                reason = blocked ?? _ws.TakeMountFailReason(entry.Id)
                       ?? (entry.IsPostgres          ? "сервер не отвечает"
                         : File.Exists(entry.Path) ? "не удалось открыть"
                                                   : "файл не найден");
            lock (_gate)
            {
                if (m is not null) _status.Remove(entry.Id);
                else
                    _status[entry.Id] = new ItemStatus(entry.Id, entry.Name, ItemState.Failed,
                                                       $"недоступна ({reason})", reason);
            }
            Changed?.Invoke(entry.Id, true);
            Completed?.Invoke(entry.Id, m);
        }
    }

    // Брошенное сторожем монтирование все же завершилось. Удачно — база уже в Workspace
    // (гонку с повторным монтированием разруливает Workspace.Mount), снимаем статус «недоступна»
    // и сообщаем подписчику как об обычном завершении. Неудачно — статус уже стоит, тишина.
    private void OnAbandonedMountFinished(DatabaseRefEntity entry, DatabaseManager? m)
    {
        if (m is null) return;
        lock (_gate)
        {
            if (_status.TryGetValue(entry.Id, out var s) && s.State == ItemState.Failed)
                _status.Remove(entry.Id);
        }
        Log.Information("Монтирование «{Name:l}», брошенное как зависшее, завершилось — база подключена", entry.Name);
        Changed?.Invoke(entry.Id, true);
        Completed?.Invoke(entry.Id, m);
    }

    private void OnStage(DatabaseRefEntity entry, string msg, double? frac)
    {
        var nl    = msg.IndexOf('\n');
        var phase = nl < 0 ? msg : msg[..nl];
        var info  = nl < 0 ? ""  : msg[(nl + 1)..];
        var text  = ShortText(phase, info, frac);
        lock (_gate)
        {
            // Гонка с Forget: статус уже сняли — не воскрешаем.
            if (!_status.ContainsKey(entry.Id)) return;
            _status[entry.Id] = new ItemStatus(entry.Id, entry.Name, ItemState.Mounting, text);
        }
        Changed?.Invoke(entry.Id, false);
    }

    // Компактная строка для сайдбара: «Обновление · 45% · ≈ 2 мин». Полные фразы стадий
    // (см. DatabaseManager) в узкой колонке не помещаются, поэтому фаза укорачивается,
    // а из инфо-строки берется только оценка времени (слово «осталось» опускаем — рядом
    // с процентами «≈ 2 мин» читается однозначно).
    private static string ShortText(string phase, string info, double? frac)
    {
        var p = phase.StartsWith("Обновление")           ? "Обновление"
              : phase.StartsWith("Сжимаю файл")          ? "Сжатие"
              : phase.StartsWith("Индексирую календарь") ? "Индексация"
              : "подключается…";

        var parts = new List<string> { p };
        if (frac is { } f) parts.Add($"{(int)(Math.Clamp(f, 0, 1) * 100)}%");

        const string marker = "осталось ";
        var i = info.IndexOf(marker, StringComparison.Ordinal);
        if (i >= 0) parts.Add(info[(i + marker.Length)..].TrimEnd());

        return string.Join(" · ", parts);
    }
}
