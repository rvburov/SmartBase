using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Serilog;
using SmartBase.Logging;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// Перенос журнала WAL в файл базы после массовой правки — в фоне, после того как правка уже
/// закончилась и видна. Правка к этому моменту надежно лежит в журнале (коммит), перенос только
/// переписывает ее страницы в тело файла. Замер на широкой коллекции (1000 свойств): дубль или
/// удаление столбца на 20 тыс. записей — 0,5 с работы и 0,3 с коммита, а перенос 80 МБ журнала —
/// 3 с; на 100 тыс. — два десятка секунд, которые окно прогресса ждало ни за что.
///
/// Автоперенос SQLite выключен на всех подключениях программы к локальным базам
/// (AppDbContext.ApplyConnectionPragmas): он шел внутри коммита той правки, которой выпало
/// перевалить порог, — после дубля столбца на 100 тыс. записей его сделала мелкая правка
/// интерфейса, 17 с на UI-потоке. Журнал переносится только здесь: после массовых правок —
/// закрытие их контекста зовет <see cref="Schedule"/>, — и по размеру журнала у открытых баз
/// (<see cref="Watch"/>, проверка раз в секунду, порог — <see cref="AppDbContext.WalSizeLimitBytes"/>).
/// Перенос идет PASSIVE — не ждет читателей и не мешает писателям — и повторяется, пока журнал не
/// перенесен целиком. Соединение переноса — вне пула: закрытие базы (<see cref="Unwatch"/>) не
/// оставляет ее файл открытым.
/// PostgreSQL и сетевые пути (журнал DELETE) — без переноса.
/// </summary>
public static class WalCheckpointer
{
    private static readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> _again   = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> _watched = new(StringComparer.OrdinalIgnoreCase);
    // Раньше какого времени перенос не начинать (см. Schedule с отсрочкой).
    private static readonly ConcurrentDictionary<string, DateTime> _notBefore = new(StringComparer.OrdinalIgnoreCase);
    private static Timer? _timer;

    /// <summary>Идет ли фоновый перенос журнала какой-нибудь базы (для стендов и ожиданий).</summary>
    public static bool Busy => !_running.IsEmpty;

    /// <summary>Следить за журналом открытой базы: вырос больше предела — перенести в фоне.</summary>
    public static void Watch(string dbPath)
    {
        if (PgConnectionInfo.IsPostgresConnectionString(dbPath) || NetworkPath.IsNetwork(dbPath)) return;
        if (!_watched.TryAdd(dbPath, 0)) return;
        lock (_watched) _timer ??= new Timer(_ => Tick(), null, 1000, 1000);
    }

    /// <summary>База закрывается: перестать следить и дождаться идущего переноса (не дольше
    /// 30 с), чтобы его соединение не держало файл.</summary>
    public static void Unwatch(string dbPath)
    {
        _watched.TryRemove(dbPath, out _);
        _again.TryRemove(dbPath, out _);
        _notBefore.TryRemove(dbPath, out _);
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (_running.ContainsKey(dbPath) && wait.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(20);
    }

    /// <summary>Дождаться, пока фоновых переносов не останется (тесты, стенды).</summary>
    public static bool WaitIdle(TimeSpan timeout)
    {
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (Busy && wait.Elapsed < timeout) Thread.Sleep(20);
        return !Busy;
    }

    private static void Tick()
    {
        foreach (var path in _watched.Keys)
        {
            long length;
            try { length = new FileInfo(path + "-wal").Length; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (length > AppDbContext.WalSizeLimitBytes) Schedule(path);
        }
    }

    /// <summary>Перенос после массовой правки — с отсрочкой: сразу за правкой интерфейс
    /// перечитывает колонки и строки, а перенос гигабайта журнала занимает диск целиком (замер на
    /// базе 31 ГБ: чтение колонок после удаления столбца ждало диск 7 с на UI-потоке). Проверка
    /// размера журнала раньше срока перенос тоже не начинает.</summary>
    public static void Schedule(string dbPath, TimeSpan delay)
    {
        _notBefore[dbPath] = DateTime.UtcNow + delay;
        Schedule(dbPath);
    }

    public static void Schedule(string dbPath)
    {
        if (PgConnectionInfo.IsPostgresConnectionString(dbPath) || NetworkPath.IsNetwork(dbPath)) return;
        // Уже переносится — пусть после этого пройдет еще раз: журнал мог вырасти за время переноса.
        if (!_running.TryAdd(dbPath, 0)) { _again[dbPath] = 0; return; }
        _ = Task.Run(() => Run(dbPath));
    }

    private static async Task Run(string dbPath)
    {
        try
        {
            await Hold(dbPath);
            using var slow = Diag.Measure("перенос журнала базы", Diag.Background, DbErrors.Display(dbPath));
            do
            {
                _again.TryRemove(dbPath, out _);
                await Hold(dbPath);   // отсрочку могли заказать, пока шел прошлый проход
                using var conn = SqliteGate.NewConnection(
                    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
                conn.Open();
                for (var attempt = 0; attempt < 120; attempt++)
                {
                    // (busy, кадров в журнале, перенесено) — перенесено все, что есть, — готово.
                    var (log, done) = Checkpoint(conn);
                    if (log <= 0 || done >= log) break;
                    await Task.Delay(500);   // читатель держит старый снимок — подождать его
                }
            }
            while (_again.ContainsKey(dbPath));
        }
        catch (Exception ex)
        {
            if (ex is not Microsoft.Data.Sqlite.SqliteException sq || !DbHealthMonitor.IsBusyError(sq))
                Log.Warning(ex, "Фоновый перенос журнала не выполнен: {Path:l}", DbErrors.Display(dbPath));
        }
        finally
        {
            _running.TryRemove(dbPath, out _);
        }
    }

    // Отсрочка, заказанная Schedule с задержкой, — перед очередным проходом переноса.
    private static Task Hold(string dbPath) =>
        _notBefore.TryRemove(dbPath, out var at) && at > DateTime.UtcNow ? Task.Delay(at - DateTime.UtcNow) : Task.CompletedTask;

    private static (long Log, long Done) Checkpoint(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        // synchronous=NORMAL — как у подключений программы: перенос синхронизирует файл базы.
        cmd.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA wal_checkpoint(PASSIVE);";
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? (rd.GetInt64(1), rd.GetInt64(2)) : (0, 0);
    }
}
