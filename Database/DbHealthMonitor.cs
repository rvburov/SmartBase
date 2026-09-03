using Microsoft.Data.Sqlite;
using Npgsql;
using Serilog;
using SmartBase.Database.Entities;

namespace SmartBase.Database;

/// <summary>
/// Монитор доступности смонтированных БД. Общая база в сетевой папке может пропасть на
/// ходу (сервер выключили, файл удалили/переместили) — приложение не должно ни падать,
/// ни молча работать с «мертвым» файлом. Монитор периодически пробует каждую
/// недефолтную БД; при потере поднимает <see cref="DatabaseLost"/> (MainWindow
/// размонтирует, покажет заглушку «недоступна» и уведомление), затем сам ретраит путь
/// с бэкофом и, когда файл вернулся, ставит базу в очередь монтирования — она оживает
/// без действий пользователя.
///
/// Две вещи, которых не видно по одной пробе:
///  • сервер перезагружают целиком, и базы на одном пути (<see cref="NetworkPath.ServerRoot"/>)
///    пропадают вместе — потеря одной сразу проверяет соседей и объявляется одной пачкой,
///    чтобы UI-поток не упирался в каждую мертвую базу по очереди;
///  • после возврата сервера рабочие подключения держат мертвые хендлы, и SQLite отвечает
///    на них BUSY, а не IOERR (см. AppDbContext.ResetPool). Свежая проба при этом проходит —
///    поэтому рабочий код сообщает о BUSY сетевой базы (<see cref="ReportBusy"/>), и если
///    проба прошла, монитор сбрасывает пул подключений к файлу.
///
/// События поднимаются ИЗ ФОНОВОГО ПОТОКА — подписчик маршалит в UI сам.
/// </summary>
public sealed class DbHealthMonitor : IDisposable
{
    /// <summary>БД потеряна: (dbId, причина — «файл не найден» / «связь потеряна»).</summary>
    public event Action<int, string>? DatabaseLost;

    private readonly Workspace  _ws;
    private readonly MountQueue _queue;
    private readonly Timer      _timer;
    private readonly object     _gate = new();

    // Счетчик подряд неудачных проб по dbId (потеря объявляется после 2 подряд —
    // одиночный таймаут сети не должен ронять базу).
    private readonly Dictionary<int, int> _failStreak = [];
    // Пробы «в полете»: на зависшей SMB проба может висеть в неотменяемом IO — не плодим
    // новые потоки поверх, следующая попытка только после завершения текущей.
    private readonly HashSet<int> _probing = [];
    // Потерянные БД, которые монитор ретраит сам. Бэкоф 5 → 15 → 60 с (потолок). Причина и
    // время потери — для живой надписи под именем базы в сайдбаре и настройках.
    private sealed record LostDb(DatabaseRefEntity Entry, string Reason, DateTime LostAt,
                                 int Attempt, DateTime NextTry);
    private readonly Dictionary<int, LostDb> _lost = [];
    // Внеплановая проверка, запрошенная из рабочего кода (ReportFailure/ReportBusy).
    private readonly HashSet<int> _urgent = [];
    // Срочные проверки, вызванные BUSY сетевой базы: пройдет свежая проба — сбросить пул.
    private readonly HashSet<int> _busyReported = [];

    private const int ProbePeriodMs   = 10_000;  // штатный тик
    private const int ProbeWaitMs     = 5_000;   // ожидание пробы (дальше поток бросаем)
    private const int FailsToDeclare  = 2;       // подряд неудач до объявления потери

    public DbHealthMonitor(Workspace ws, MountQueue queue)
    {
        _ws    = ws;
        _queue = queue;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(ProbePeriodMs, ProbePeriodMs);

    public void Dispose() => _timer.Dispose();

    /// <summary>Ошибка БД из рабочего кода (правка ячейки, подгрузка страницы и т.п.):
    /// коды «файл пропал/поврежден» → немедленная внеплановая проверка этой БД.
    /// BUSY/LOCKED потерей НЕ считаются — это штатная конкуренция писателей
    /// (для сетевой базы см. <see cref="ReportBusy"/>).</summary>
    public void ReportFailure(int dbId, Exception ex)
    {
        if (!IsUnavailableError(ex)) return;
        lock (_gate) _urgent.Add(dbId);
        // Не ждем штатного тика — проверяем сразу (сам тик разберется с флагами).
        Task.Run(Tick);
    }

    /// <summary>То же по пути файла — для мест, где известен только DbPath.</summary>
    public void ReportFailureByPath(string dbPath, Exception ex)
    {
        if (FindMounted(dbPath) is { } db) ReportFailure(db.DbId, ex);
    }

    /// <summary>
    /// Сетевая SQLite-база ответила BUSY (запрос ждал busy_timeout и упал «database is locked»).
    /// Штатно так бывает, когда чужой писатель держит файл дольше busy_timeout, — редкость.
    /// Но ровно так же SQLite на Windows отвечает на мертвые хендлы после перезагрузки сервера,
    /// и без свежей пробы одно от другого не отличить. Монитор пробует файл немедленно: проба
    /// упала → база объявляется потерянной (без ожидания второй неудачи); прошла → пул
    /// подключений к файлу сбрасывается — при штатной конкуренции это лишь переоткрытие
    /// соединения, при мертвых хендлах это и есть лечение. Локальные базы и PostgreSQL
    /// сигнал не касается.
    /// </summary>
    public void ReportBusy(int dbId)
    {
        var db = _ws.Get(dbId);
        if (db is null || !db.IsShared || db.Provider != "sqlite") return;
        lock (_gate)
        {
            _urgent.Add(dbId);
            _busyReported.Add(dbId);
        }
        Task.Run(Tick);
    }

    /// <summary>То же по пути файла.</summary>
    public void ReportBusyByPath(string dbPath)
    {
        if (FindMounted(dbPath) is { } db) ReportBusy(db.DbId);
    }

    private DatabaseManager? FindMounted(string dbPath) =>
        _ws.Mounted.FirstOrDefault(m => string.Equals(m.Path, dbPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Взять под ретраи БД, которую не удалось смонтировать (стартовая очередь или
    /// повторная попытка после потери): монитор периодически пробует путь и, когда файл
    /// вернется, снова ставит базу в очередь. Вызывается из MainWindow (UI-поток — там
    /// безопасен доступ к реестру за записью). <paramref name="reason"/> — для надписи
    /// под именем базы («не удалось открыть», «файл не найден»).</summary>
    public void TrackFailed(DatabaseRefEntity entry, string reason = "не удалось подключиться")
    {
        var now = DateTime.Now;
        // Стартуем сразу с потолка бэкофа: монтирование только что честно упало,
        // частить по мертвому пути смысла нет.
        var wait = BackoffSeconds(2);
        lock (_gate)
        {
            if (_lost.ContainsKey(entry.Id)) return;
            _lost[entry.Id] = new LostDb(entry, reason, now, 2, DateTime.UtcNow.AddSeconds(wait));
        }
        _queue.UpdateFailedText(entry.Id, LostStatus(reason, now, RetryPhrase(wait)));
    }

    /// <summary>БД отключили в настройках или она смонтировалась — забыть ее (ретраи и счетчики).</summary>
    public void Forget(int dbId)
    {
        lock (_gate)
        {
            _lost.Remove(dbId);
            _urgent.Remove(dbId);
            _busyReported.Remove(dbId);
            _failStreak.Remove(dbId);
        }
    }

    /// <summary>Надпись под именем недоступной базы (сайдбар и «Управление базой данных»):
    /// «недоступна (связь потеряна) с 14:05 · повтор через ≈ 1 мин». Одно место сборки —
    /// чтобы сайдбары и настройки никогда не расходились.</summary>
    public static string LostStatus(string reason, DateTime lostAt, string? phase = null) =>
        $"недоступна ({reason}) с {lostAt:HH:mm}" + (phase is null ? "" : $" · {phase}");

    private static string RetryPhrase(int seconds) =>
        seconds < 50 ? $"повтор через {seconds} с"
                     : $"повтор через ≈ {Math.Ceiling(seconds / 60.0):0} мин";

    /// <summary>«База недоступна/повреждена». SQLite: IOERR(10), CORRUPT(11), CANTOPEN(14),
    /// NOTADB(26) + файловые исключения ОС. PostgreSQL: обрыв соединения (класс 08*),
    /// сервер гасится/недоступен (57P01–57P03), базы нет (3D000), отказ аутентификации (28*),
    /// переполнение max_connections (53300 — сервер насыщен, лечится тем же backoff-ремонтом) —
    /// плюс сетевые SocketException/TimeoutException и транзиентные ошибки Npgsql.
    /// Через этот фильтр DbErrors и монитор работают для обоих провайдеров.</summary>
    public static bool IsUnavailableError(Exception ex) =>
        ex is IOException or FileNotFoundException or DirectoryNotFoundException
          or System.Net.Sockets.SocketException or TimeoutException
        || (ex is SqliteException se && se.SqliteErrorCode is 10 or 11 or 14 or 26)
        || (ex is PostgresException pe &&
            (pe.SqlState.StartsWith("08", StringComparison.Ordinal)
             || pe.SqlState.StartsWith("28", StringComparison.Ordinal)
             || pe.SqlState is "57P01" or "57P02" or "57P03" or "3D000" or "53300"))
        || (ex is NpgsqlException { InnerException: null } ne && ne.IsTransient)
        || (ex.InnerException is not null && IsUnavailableError(ex.InnerException));

    /// <summary>«Занято, не потеря»: SQLite BUSY(5)/LOCKED(6) — файл держит другой писатель;
    /// PostgreSQL 40001 (serialization), 40P01 (deadlock), 55P03 (lock timeout) — штатная
    /// конкуренция транзакций, повтор действия обычно проходит.</summary>
    public static bool IsBusyError(Exception ex) =>
        (ex is SqliteException se && se.SqliteErrorCode is 5 or 6)
        || (ex is PostgresException pe && pe.SqlState is "40001" or "40P01" or "55P03")
        || (ex.InnerException is not null && IsBusyError(ex.InnerException));

    // ── Основной цикл ───────────────────────────────────────────────────────────

    private void Tick()
    {
        // 1. Пробы смонтированных недефолтных БД (дефолтная — локальная база программы,
        //    ее отказ — фатальный сценарий сплэша, не наш).
        foreach (var db in _ws.Mounted.Where(m => m.DbId != _ws.Default.DbId))
        {
            bool urgent, fromBusy;
            lock (_gate)
            {
                if (_lost.ContainsKey(db.DbId)) continue;       // уже потеряна — ретраит блок 2
                if (!_probing.Add(db.DbId)) continue;           // проба еще в полете
                urgent   = _urgent.Remove(db.DbId);
                fromBusy = _busyReported.Remove(db.DbId);
            }
            var dbId = db.DbId;
            var path = db.Path;
            var name = db.Name;
            _ = Task.Run(() => ProbeMounted(dbId, name, path, urgent, fromBusy));
        }

        // 2. Ретраи потерянных: файл вернулся → в очередь монтирования.
        List<(int DbId, LostDb Lost)> retry = [];
        lock (_gate)
        {
            foreach (var (dbId, s) in _lost)
                if (DateTime.UtcNow >= s.NextTry)
                    retry.Add((dbId, s));
        }
        foreach (var (dbId, s) in retry)
        {
            _queue.UpdateFailedText(dbId, LostStatus(s.Reason, s.LostAt, "проверяю…"));
            if (ProbePath(ProbeTarget(s.Entry)))
            {
                lock (_gate) _lost.Remove(dbId);
                // Дальше — существующий конвейер: монтирование, Completed → SyncMountedToUi,
                // узел в сайдбаре оживает. Если монтирование все же упадет, MainWindow вернет
                // базу под ретраи через TrackFailed (обработчик Completed).
                _queue.Enqueue(s.Entry);
            }
            else
            {
                int wait;
                lock (_gate)
                {
                    if (!_lost.TryGetValue(dbId, out var cur)) continue;
                    var attempt = cur.Attempt + 1;
                    wait = BackoffSeconds(attempt);
                    _lost[dbId] = cur with { Attempt = attempt, NextTry = DateTime.UtcNow.AddSeconds(wait) };
                }
                _queue.UpdateFailedText(dbId, LostStatus(s.Reason, s.LostAt, RetryPhrase(wait)));
            }
        }
    }

    // Проба смонтированной БД в фоне; 2 неудачи подряд (или файл исчез, или срочная проверка
    // по ошибке из рабочего кода) → потеря. Проба прошла после сигнала BUSY → сброс пула.
    private void ProbeMounted(int dbId, string name, string path, bool urgent, bool fromBusy)
    {
        try
        {
            var probe = Task.Run(() => ProbePathDetailed(path));
            var ok    = probe.Wait(ProbeWaitMs) && probe.Result.Ok;
            var gone  = probe.IsCompleted && !probe.Result.FileExists;

            lock (_gate)
            {
                if (ok) _failStreak.Remove(dbId);
                else
                {
                    var streak = _failStreak.GetValueOrDefault(dbId) + 1;
                    _failStreak[dbId] = streak;
                    // Файл исчез — теряем сразу; иначе (таймаут/ошибка чтения) ждем подтверждения,
                    // кроме случая urgent: рабочий код уже словил CANTOPEN/IOERR/BUSY — не тянем.
                    if (!gone && !urgent && streak < FailsToDeclare) return;
                }
            }

            if (ok)
            {
                if (fromBusy)
                {
                    // Файл читается свежим соединением, а рабочие подключения отвечали BUSY:
                    // либо чужой писатель держал файл дольше busy_timeout (тогда сброс пула —
                    // дешевое переоткрытие соединений), либо хендлы мертвы после перезагрузки
                    // сервера — тогда это и есть лечение.
                    AppDbContext.ResetPool(path);
                    Log.Information("База «{Name:l}» отвечала BUSY, свежая проба прошла — пул подключений сброшен", name);
                }
                return;
            }

            DeclareLost(dbId, name, path,
                PgConnectionInfo.IsPostgresConnectionString(path) ? "сервер недоступен"
                : gone                                            ? "файл не найден"
                                                                  : "связь потеряна");
        }
        finally
        {
            lock (_gate) _probing.Remove(dbId);
        }
    }

    // Потеря базы — и сразу проверка ее соседей по серверу: их пробуем параллельно, упавшие
    // объявляем той же пачкой (MainWindow размонтирует все и один раз пересоберет сайдбары),
    // уцелевшим сбрасываем пул — сервер, скорее всего, перезагружался, и их хендлы мертвы.
    private void DeclareLost(int dbId, string name, string path, string reason)
    {
        lock (_gate) if (_lost.ContainsKey(dbId)) return;   // уже объявлена (пачкой соседа)

        var lost = new List<(int DbId, string Name, string Path, string Reason)> { (dbId, name, path, reason) };

        var root = NetworkPath.ServerRoot(path);
        if (root is not null)
        {
            var siblings = _ws.Mounted
                .Where(m => m.DbId != dbId && m.DbId != _ws.Default.DbId && m.Provider == "sqlite"
                            && string.Equals(NetworkPath.ServerRoot(m.Path), root, StringComparison.OrdinalIgnoreCase))
                .ToList();
            lock (_gate) siblings.RemoveAll(m => _lost.ContainsKey(m.DbId));

            if (siblings.Count > 0)
            {
                var probes = siblings.Select(m => (Db: m, Probe: Task.Run(() => ProbePathDetailed(m.Path)))).ToList();
                Task.WaitAll(probes.Select(p => (Task)p.Probe).ToArray(), ProbeWaitMs);
                foreach (var (m, probe) in probes)
                {
                    if (probe.IsCompletedSuccessfully && probe.Result.Ok)
                    {
                        AppDbContext.ResetPool(m.Path);
                        continue;
                    }
                    var goneS = probe.IsCompletedSuccessfully && !probe.Result.FileExists;
                    lost.Add((m.DbId, m.Name, m.Path, goneS ? "файл не найден" : "связь потеряна"));
                }
                Log.Warning("База «{Name:l}» потеряна ({Reason:l}); соседей по «{Root:l}»: {Siblings}, из них потеряно: {Lost}",
                            name, reason, root, siblings.Count, lost.Count - 1);
            }
        }

        var now = DateTime.Now;
        lock (_gate)
        {
            foreach (var l in lost)
            {
                if (_lost.ContainsKey(l.DbId)) continue;
                _failStreak.Remove(l.DbId);
                _busyReported.Remove(l.DbId);
                // Идентичность для ретраев собираем из уже известного (реестр из фонового потока
                // не трогаем — его контекст живет на UI-потоке); Enqueue нужны только Id/Name/Path.
                // Для PG path — строка подключения (Workspace.Mount монтирует по ней напрямую,
                // когда Pg*-полей у синтетической записи нет).
                var entry = new DatabaseRefEntity
                {
                    Id       = l.DbId,
                    Name     = l.Name,
                    Path     = l.Path,
                    Provider = PgConnectionInfo.IsPostgresConnectionString(l.Path) ? "postgres" : "sqlite",
                };
                _lost[l.DbId] = new LostDb(entry, l.Reason, now, 0, DateTime.UtcNow.AddSeconds(BackoffSeconds(0)));
            }
        }
        foreach (var l in lost) DatabaseLost?.Invoke(l.DbId, l.Reason);
    }

    private static int BackoffSeconds(int attempt) => attempt switch
    {
        0 => 5,
        1 => 15,
        _ => 60,
    };

    // Цель пробы для записи реестра/синтетической записи ретраев: у PG-записи реестра
    // Path — отображаемая строка без пароля, подключаться надо по собранной из Pg*-полей;
    // у синтетической записи монитора Path — уже готовая строка подключения.
    private static string ProbeTarget(DatabaseRefEntity entry) =>
        entry.IsPostgres ? entry.ToPgInfo()?.ToConnectionString() ?? entry.Path : entry.Path;

    // Быстрая проба пути: файл существует и открывается на чтение.
    private static bool ProbePath(string path)
    {
        var probe = Task.Run(() => ProbePathDetailed(path));
        return probe.Wait(ProbeWaitMs) && probe.Result.Ok;
    }

    /// <summary>Подмена пробы для стендов и тестов: вернула значение — оно и есть результат
    /// пробы пути, null — обычная проба. Настоящую пропажу сетевой папки в тесте не
    /// устроить (открытый SQLite-файл на Windows не удалить и не переименовать).</summary>
    public static Func<string, (bool Ok, bool FileExists)?>? ProbeOverride;

    // Проба: File.Exists ОБЯЗАТЕЛЕН отдельно от открытия — на POSIX удаленный файл
    // продолжает «работать» через уже открытый хендл долгоживущего контекста, записи
    // молча уходят в отвязанный inode. Затем одноразовое read-only подключение + SELECT 1.
    // Для PostgreSQL (path — строка подключения) проба сервера: короткое подключение
    // (Timeout=5 уже в строке) + SELECT 1; понятия «файл исчез» у сервера нет.
    private static (bool Ok, bool FileExists) ProbePathDetailed(string path)
    {
        if (ProbeOverride?.Invoke(path) is { } forced) return forced;

        if (PgConnectionInfo.IsPostgresConnectionString(path))
        {
            try
            {
                using var pg = new NpgsqlConnection(path);
                pg.Open();
                using var ping = pg.CreateCommand();
                ping.CommandText = "SELECT 1;";
                ping.ExecuteScalar();
                return (true, true);
            }
            catch
            {
                return (false, true);
            }
        }

        try
        {
            if (!File.Exists(path)) return (false, false);
            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            cmd.ExecuteScalar();
            return (true, true);
        }
        catch
        {
            // Файл есть, но не читается (сеть отвалилась на открытом хендле, повреждение).
            return (false, File.Exists(path));
        }
    }
}
