using Serilog;
using SmartBase.Database.Entities;

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
    public enum ItemState { Queued, Mounting, Failed }

    /// <summary>Статус немонтированной БД для сайдбара/настроек: короткий текст обновляется
    /// живьем из тех же stage-колбэков, что сплэш и окно подключения.</summary>
    public sealed record ItemStatus(int DbId, string Name, ItemState State, string Text);

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
    public void MarkFailed(int dbId, string name, string text)
    {
        lock (_gate)
        {
            // База в этот момент не должна числиться ждущей (ее только что размонтировали).
            _queue.RemoveAll(e => e.Id == dbId);
            _status[dbId] = new ItemStatus(dbId, name, ItemState.Failed, text);
        }
        Changed?.Invoke(dbId, true);
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

            // Mount() гасит ошибки сам (вернет null); страхуемся на случай неожиданных.
            DatabaseManager? m = null;
            try { m = _ws.Mount(entry, (msg, frac) => OnStage(entry, msg, frac)); }
            catch (Exception ex) { Log.Error(ex, "Неожиданный сбой монтирования БД «{Name:l}»", entry.Name); }

            lock (_gate)
            {
                if (m is not null) _status.Remove(entry.Id);
                else _status[entry.Id] = new ItemStatus(entry.Id, entry.Name, ItemState.Failed,
                    entry.IsPostgres          ? "недоступна (сервер не отвечает)"
                    : File.Exists(entry.Path) ? "недоступна (не удалось открыть)"
                                              : "недоступна (файл не найден)");
            }
            Changed?.Invoke(entry.Id, true);
            Completed?.Invoke(entry.Id, m);
        }
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
