using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;
using Serilog;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// Фоновая проверка чужих изменений в смонтированных БД (~каждые 3 с): несколько
/// пользователей могут работать с одним файлом (сетевая папка) или несколько копий
/// приложения — с одним локальным файлом. Дешевый детектор — <c>PRAGMA data_version</c>:
/// меняется, когда БД изменил ДРУГОЙ коннект (в т.ч. другой процесс), но только
/// относительно СВОЕГО соединения — поэтому на каждую БД держится одно постоянное
/// read-only подключение (свежее подключение на каждый тик ничего не увидит). Версия
/// не изменилась — тик стоит микросекунды. Изменилась — читаем хвост change_log после
/// последнего виденного Id, отбрасываем свои правки (UserKey) и отдаем остальным
/// подписчику: MainWindow обновляет сайдбары/таблицу/календарь.
///
/// События поднимаются ИЗ ФОНОВОГО ПОТОКА — подписчик маршалит в UI сам.
/// </summary>
public sealed class ChangePoller : IDisposable
{
    /// <summary>Чужая правка: действие + тип объекта (для выбора, что обновлять в UI).</summary>
    public sealed record RemoteChange(string Action, string TargetType, string? CollectionName);

    /// <summary>Чужие изменения в БД (dbId, строки журнала новее последнего виденного).</summary>
    public event Action<int, IReadOnlyList<RemoteChange>>? RemoteChanges;

    private readonly Workspace       _ws;
    private readonly DbHealthMonitor _health;
    private readonly Timer           _timer;
    private int _busy;                        // тики не накладываются

    // Период опроса задает настройка «Проверка чужих изменений» (стандарт — прежние 3 с при
    // активном окне и 15 с в фоне). Свойства, а не константы: настройку меняют на ходу, и
    // таймер пересаживается на новый период сразу (см. ApplyPeriod).
    private static int ForegroundMs => Utils.ConstructorSettings.PollSeconds * 1000;
    private static int BackgroundMs => Utils.ConstructorSettings.BackgroundPollSeconds * 1000;

    // Активно ли окно — запоминаем, чтобы при смене настройки пересчитать ИМЕННО текущий
    // период: иначе смена настройки на свернутом окне вернула бы его в активный режим.
    private bool _foreground = true;
    private bool _started;

    // Наблюдение за одной БД: постоянное соединение + последняя версия + курсор журнала.
    // Для PostgreSQL (Path — строка подключения) data_version не существует; дешевый
    // детектор — MAX(id) журнала обратным сканом PK (микросекунды на тик).
    private sealed class Watch
    {
        public DbConnection? Conn;
        public string Path        = "";
        public bool   IsPostgres;
        public long   DataVersion = -1;
        public long   LastLogId   = -1;
        public int    BusyStreak;   // подряд BUSY на этом соединении (см. Tick)
    }

    // BUSY подряд, после которых постоянное соединение переоткрывается: мертвый хендл сетевого
    // файла после перезагрузки сервера сам не оживет, а чужой коммит столько не держится.
    private const int BusyStreakToReopen = 3;
    private readonly Dictionary<int, Watch> _watch = [];
    // Базы, о сбое наблюдения которых уже написано в журнал: до возврата базы каждый тик
    // падал бы на том же месте, а одной записи на серию достаточно.
    private readonly HashSet<int> _failLogged = [];

    public ChangePoller(Workspace ws, DbHealthMonitor health)
    {
        _ws     = ws;
        _health = health;
        _timer  = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        Utils.ConstructorSettings.Changed += ApplyPeriod;
    }

    public void Start()
    {
        _started = true;
        ApplyPeriod();
    }

    /// <summary>Один тик вручную (стенды и тесты — без ожидания таймера).</summary>
    public void PollNow() => Tick();

    /// <summary>Окно активно/в фоне: в фоне частота проверки снижается.</summary>
    public void SetForeground(bool fg)
    {
        _foreground = fg;
        ApplyPeriod();
    }

    // Пересадить таймер на период текущего режима. До Start ничего не делаем: поллер еще не
    // работает, и заводить его раньше времени (например по смене настройки) нельзя.
    private void ApplyPeriod()
    {
        if (!_started) return;
        var period = _foreground ? ForegroundMs : BackgroundMs;
        try { _timer.Change(period, period); }
        catch (ObjectDisposedException) { /* окно уже закрылось — таймера нет */ }
    }

    public void Dispose()
    {
        Utils.ConstructorSettings.Changed -= ApplyPeriod;
        _timer.Dispose();
        // Соединения наблюдения закрываются в фоне: закрытие хендла сетевого файла на зависшей
        // папке не возвращается, а Dispose зовется с UI-потока при закрытии окна — выход
        // приложения не должен на этом стоять (см. Workspace.CloseAll).
        var conns = _watch.Values.Select(w => w.Conn).Where(c => c is not null).ToList();
        _watch.Clear();
        if (conns.Count == 0) return;
        _ = Task.Run(() =>
        {
            foreach (var c in conns)
            {
                try { c!.Dispose(); } catch { /* best-effort */ }
            }
        });
    }

    private void Tick()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            var mounted = _ws.Mounted;

            // Наблюдения размонтированных БД сворачиваем (закрыть соединение).
            foreach (var dbId in _watch.Keys.Where(id => mounted.All(m => m.DbId != id)).ToList())
            {
                _watch[dbId].Conn?.Dispose();
                _watch.Remove(dbId);
            }

            foreach (var db in mounted)
            {
                try
                {
                    PollOne(db.DbId, db.Path);
                    _failLogged.Remove(db.DbId);
                }
                catch (Exception ex) when (DbHealthMonitor.IsBusyError(ex))
                {
                    // Файл занят дольше busy_timeout соединения (2 с): чужой долгий коммит — или
                    // хендл мертв после перезагрузки сервера (SQLite отвечает BUSY и на это).
                    // Наблюдение и курсор НЕ сбрасываем: сброс курсора терял бы чужие правки
                    // при каждом BUSY. Монитор проверит базу свежим соединением; три BUSY подряд
                    // — переоткрываем свое соединение (курсор остается, хвост дочитается).
                    if (_watch.TryGetValue(db.DbId, out var w) && ++w.BusyStreak >= BusyStreakToReopen)
                    {
                        w.Conn?.Dispose();
                        w.Conn = null;
                        w.BusyStreak = 0;
                        Log.Warning("Поллер чужих правок: база «{Name:l}» ответила BUSY {Count} раза подряд — соединение наблюдения переоткрыто",
                                    db.Name, BusyStreakToReopen);
                    }
                    _health.ReportBusy(db.DbId);
                }
                catch (Exception ex)
                {
                    // Соединение могло умереть вместе с файлом/сетью: наблюдение сбрасываем
                    // (после возврата базы построится заново), монитору — сигнал о потере.
                    // Поллер тем самым — второй детектор пропажи. Одна запись на серию: до
                    // возврата базы каждый тик падал бы здесь же.
                    if (_watch.TryGetValue(db.DbId, out var w)) { w.Conn?.Dispose(); _watch.Remove(db.DbId); }
                    if (_failLogged.Add(db.DbId))
                        Log.Warning(ex, "Поллер чужих правок: наблюдение за базой «{Name:l}» сброшено — чужие правки не подхватываются до возврата базы",
                                    db.Name);
                    _health.ReportFailure(db.DbId, ex);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void PollOne(int dbId, string path)
    {
        if (!_watch.TryGetValue(dbId, out var w) || w.Path != path)
        {
            _watch.GetValueOrDefault(dbId)?.Conn?.Dispose();
            w = new Watch { Path = path, IsPostgres = PgConnectionInfo.IsPostgresConnectionString(path) };
            OpenWatch(w);
            // Первое знакомство: историю не проигрываем — курсор на текущий хвост журнала.
            if (!w.IsPostgres) w.DataVersion = ReadLong(w.Conn!, "PRAGMA data_version;");
            w.LastLogId  = ReadLong(w.Conn!, MaxLogIdSql(w.IsPostgres));
            _watch[dbId] = w;
            return;
        }

        if (w.Conn is null)
        {
            // Переоткрытие после серии BUSY (см. Tick): курсор журнала сохранен, версия
            // сброшена — этот же тик перечитает хвост после курсора, правки за время паузы
            // не теряются.
            OpenWatch(w);
            w.DataVersion = -1;
        }

        if (w.IsPostgres)
        {
            // PG: дешевый детектор — вырос ли журнал (обратный скан PK). Правки без записи
            // в change_log (только UI-таблицы) сосед и не должен подхватывать.
            if (ReadLong(w.Conn!, MaxLogIdSql(true)) <= w.LastLogId) return;
        }
        else
        {
            var version = ReadLong(w.Conn!, "PRAGMA data_version;");
            w.BusyStreak = 0;
            if (version == w.DataVersion) return;   // с прошлого тика БД никто не менял
            w.DataVersion = version;
        }

        // Кто-то коммитил: читаем хвост журнала. Курсор двигаем ВСЕГДА (и на своих правках),
        // чтобы не перечитывать один хвост по кругу. Хвост ограничен: при огромном отставании
        // (массовый импорт соседа) детали не важны — union типов и так вызовет полный refresh.
        var changes = new List<RemoteChange>();
        using (var cmd = w.Conn!.CreateCommand())
        {
            // Идентификаторы без кавычек: SQLite матчит PascalCase-колонки без учета регистра,
            // PG сворачивает в lowercase — совпадает с PG-моделью (см. ApplyLowercaseNames).
            cmd.CommandText = "SELECT Id, UserKey, Action, TargetType, CollectionName " +
                              "FROM change_log WHERE Id > @last ORDER BY Id LIMIT 500;";
            var p = cmd.CreateParameter();
            p.ParameterName = "@last";
            p.Value = w.LastLogId;
            cmd.Parameters.Add(p);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                w.LastLogId = r.GetInt64(0);
                var userKey = r.GetString(1);
                if (string.Equals(userKey, CurrentUser.Key, StringComparison.OrdinalIgnoreCase))
                    continue;   // своя правка — UI уже обновлен по месту действия
                changes.Add(new RemoteChange(r.GetString(2), r.GetString(3),
                                             r.IsDBNull(4) ? null : r.GetString(4)));
            }
        }

        if (changes.Count > 0)
            RemoteChanges?.Invoke(dbId, changes);
    }

    // Постоянное соединение наблюдения. SQLite: read-only, мимо пула (пул общий с рабочими
    // контекстами, а этому соединению важно жить долго — data_version считается относительно
    // него) и с коротким busy_timeout: без него PRAGMA data_version падала BUSY на каждом
    // чужом коммите, и наблюдение сбрасывалось вместе с курсором.
    private static void OpenWatch(Watch w)
    {
        w.Conn = w.IsPostgres
            ? new NpgsqlConnection(w.Path)
            : SqliteGate.NewConnection($"Data Source={w.Path};Mode=ReadOnly;Pooling=False");
        w.Conn.Open();
        if (w.IsPostgres) return;
        using var cmd = w.Conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=2000;";
        cmd.ExecuteNonQuery();
    }

    private static string MaxLogIdSql(bool postgres) =>
        postgres ? "SELECT COALESCE(MAX(id), 0) FROM change_log;"
                 : "SELECT COALESCE(MAX(\"Id\"), 0) FROM \"change_log\";";

    private static long ReadLong(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }
}
