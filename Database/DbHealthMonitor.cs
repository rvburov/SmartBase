using Microsoft.Data.Sqlite;
using Npgsql;
using SmartBase.Database.Entities;

namespace SmartBase.Database;

/// <summary>
/// Монитор доступности смонтированных БД. Общая база в сетевой папке может пропасть на
/// ходу (сервер выключили, файл удалили/переместили) — приложение не должно ни падать,
/// ни молча работать с «мёртвым» файлом. Монитор периодически пробует каждую
/// недефолтную БД; при потере поднимает <see cref="DatabaseLost"/> (MainWindow
/// размонтирует, покажет заглушку «недоступна» и уведомление), затем сам ретраит путь
/// с бэкофом и, когда файл вернулся, ставит базу в очередь монтирования — она оживает
/// без действий пользователя.
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

    // Счётчик подряд неудачных проб по dbId (потеря объявляется после 2 подряд —
    // одиночный таймаут сети не должен ронять базу).
    private readonly Dictionary<int, int> _failStreak = [];
    // Пробы «в полёте»: на зависшей SMB проба может висеть в неотменяемом IO — не плодим
    // новые потоки поверх, следующая попытка только после завершения текущей.
    private readonly HashSet<int> _probing = [];
    // Потерянные БД, которые монитор ретраит сам: dbId → (запись реестра, номер попытки,
    // время следующей попытки). Бэкоф 5 → 15 → 60 с (потолок).
    private readonly Dictionary<int, (DatabaseRefEntity Entry, int Attempt, DateTime NextTry)> _lost = [];
    // Внеплановая проверка, запрошенная из рабочего кода (ReportFailure).
    private readonly HashSet<int> _urgent = [];

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
    /// коды «файл пропал/повреждён» → немедленная внеплановая проверка этой БД.
    /// BUSY/LOCKED потерей НЕ считаются — это штатная конкуренция писателей.</summary>
    public void ReportFailure(int dbId, Exception ex)
    {
        if (!IsUnavailableError(ex)) return;
        lock (_gate) _urgent.Add(dbId);
        // Не ждём штатного тика — проверяем сразу (сам тик разберётся с флагами).
        Task.Run(Tick);
    }

    /// <summary>То же по пути файла — для мест, где известен только DbPath.</summary>
    public void ReportFailureByPath(string dbPath, Exception ex)
    {
        var db = _ws.Mounted.FirstOrDefault(m =>
            string.Equals(m.Path, dbPath, StringComparison.OrdinalIgnoreCase));
        if (db is not null) ReportFailure(db.DbId, ex);
    }

    /// <summary>Взять под ретраи БД, которую не удалось смонтировать (стартовая очередь или
    /// повторная попытка после потери): монитор периодически пробует путь и, когда файл
    /// вернётся, снова ставит базу в очередь. Вызывается из MainWindow (UI-поток — там
    /// безопасен доступ к реестру за записью).</summary>
    public void TrackFailed(DatabaseRefEntity entry)
    {
        lock (_gate)
        {
            if (_lost.ContainsKey(entry.Id)) return;
            // Стартуем сразу с потолка бэкофа: монтирование только что честно упало,
            // частить по мёртвому пути смысла нет.
            _lost[entry.Id] = (entry, 2, DateTime.UtcNow.AddSeconds(BackoffSeconds(2)));
        }
    }

    /// <summary>БД отключили в настройках — забыть её (ретраи и счётчики).</summary>
    public void Forget(int dbId)
    {
        lock (_gate)
        {
            _lost.Remove(dbId);
            _urgent.Remove(dbId);
            _failStreak.Remove(dbId);
        }
    }

    /// <summary>«База недоступна/повреждена». SQLite: IOERR(10), CORRUPT(11), CANTOPEN(14),
    /// NOTADB(26) + файловые исключения ОС. PostgreSQL: обрыв соединения (класс 08*),
    /// сервер гасится/недоступен (57P01–57P03), базы нет (3D000), отказ аутентификации (28*) —
    /// плюс сетевые SocketException/TimeoutException и транзиентные ошибки Npgsql.
    /// Через этот фильтр DbErrors и монитор работают для обоих провайдеров.</summary>
    public static bool IsUnavailableError(Exception ex) =>
        ex is IOException or FileNotFoundException or DirectoryNotFoundException
          or System.Net.Sockets.SocketException or TimeoutException
        || (ex is SqliteException se && se.SqliteErrorCode is 10 or 11 or 14 or 26)
        || (ex is PostgresException pe &&
            (pe.SqlState.StartsWith("08", StringComparison.Ordinal)
             || pe.SqlState.StartsWith("28", StringComparison.Ordinal)
             || pe.SqlState is "57P01" or "57P02" or "57P03" or "3D000"))
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
        //    её отказ — фатальный сценарий сплэша, не наш).
        foreach (var db in _ws.Mounted.Where(m => m.DbId != _ws.Default.DbId))
        {
            bool urgent;
            lock (_gate)
            {
                if (_lost.ContainsKey(db.DbId)) continue;       // уже потеряна — ретраит блок 2
                if (!_probing.Add(db.DbId)) continue;           // проба ещё в полёте
                urgent = _urgent.Remove(db.DbId);
            }
            var dbId = db.DbId;
            var path = db.Path;
            var name = db.Name;
            _ = Task.Run(() => ProbeMounted(dbId, name, path, urgent));
        }

        // 2. Ретраи потерянных: файл вернулся → в очередь монтирования.
        List<(int DbId, DatabaseRefEntity Entry)> retry = [];
        lock (_gate)
        {
            foreach (var (dbId, s) in _lost)
                if (DateTime.UtcNow >= s.NextTry)
                    retry.Add((dbId, s.Entry));
        }
        foreach (var (dbId, entry) in retry)
        {
            if (ProbePath(ProbeTarget(entry)))
            {
                lock (_gate) _lost.Remove(dbId);
                // Дальше — существующий конвейер: монтирование, Completed → SyncMountedToUi,
                // узел в сайдбаре оживает. Если монтирование всё же упадёт, MainWindow вернёт
                // базу под ретраи через TrackFailed (обработчик Completed).
                _queue.Enqueue(entry);
            }
            else
            {
                lock (_gate)
                {
                    if (!_lost.TryGetValue(dbId, out var s)) continue;
                    var attempt = s.Attempt + 1;
                    _lost[dbId] = (s.Entry, attempt, DateTime.UtcNow.AddSeconds(BackoffSeconds(attempt)));
                }
            }
        }
    }

    // Проба смонтированной БД в фоне; 2 неудачи подряд (или файл исчез) → потеря.
    private void ProbeMounted(int dbId, string name, string path, bool urgent)
    {
        try
        {
            var probe = Task.Run(() => ProbePathDetailed(path));
            var ok    = probe.Wait(ProbeWaitMs) && probe.Result.Ok;
            var gone  = probe.IsCompleted && !probe.Result.FileExists;

            lock (_gate)
            {
                if (ok) { _failStreak.Remove(dbId); return; }
                var streak = _failStreak.GetValueOrDefault(dbId) + 1;
                _failStreak[dbId] = streak;
                // Файл исчез — теряем сразу; иначе (таймаут/ошибка чтения) ждём подтверждения,
                // кроме случая urgent: рабочий код уже словил CANTOPEN/IOERR — не тянем.
                if (!gone && !urgent && streak < FailsToDeclare) return;
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

    private void DeclareLost(int dbId, string name, string path, string reason)
    {
        // Идентичность для ретраев собираем из уже известного (реестр из фонового потока
        // не трогаем — его контекст живёт на UI-потоке); Enqueue нужны только Id/Name/Path.
        // Для PG path — строка подключения (Workspace.Mount монтирует по ней напрямую,
        // когда Pg*-полей у синтетической записи нет).
        var entry = new DatabaseRefEntity
        {
            Id       = dbId,
            Name     = name,
            Path     = path,
            Provider = PgConnectionInfo.IsPostgresConnectionString(path) ? "postgres" : "sqlite",
        };
        lock (_gate)
        {
            _failStreak.Remove(dbId);
            _lost[dbId] = (entry, 0, DateTime.UtcNow.AddSeconds(BackoffSeconds(0)));
        }
        DatabaseLost?.Invoke(dbId, reason);
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

    // Проба: File.Exists ОБЯЗАТЕЛЕН отдельно от открытия — на POSIX удалённый файл
    // продолжает «работать» через уже открытый хендл долгоживущего контекста, записи
    // молча уходят в отвязанный inode. Затем одноразовое read-only подключение + SELECT 1.
    // Для PostgreSQL (path — строка подключения) проба сервера: короткое подключение
    // (Timeout=5 уже в строке) + SELECT 1; понятия «файл исчез» у сервера нет.
    private static (bool Ok, bool FileExists) ProbePathDetailed(string path)
    {
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
