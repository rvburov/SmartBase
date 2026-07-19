using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// Фоновая проверка чужих изменений в смонтированных БД (~каждые 3 с): несколько
/// пользователей могут работать с одним файлом (сетевая папка) или несколько копий
/// приложения — с одним локальным файлом. Дешёвый детектор — <c>PRAGMA data_version</c>:
/// меняется, когда БД изменил ДРУГОЙ коннект (в т.ч. другой процесс), но только
/// относительно СВОЕГО соединения — поэтому на каждую БД держится одно постоянное
/// read-only подключение (свежее подключение на каждый тик ничего не увидит). Версия
/// не изменилась — тик стоит микросекунды. Изменилась — читаем хвост change_log после
/// последнего виденного Id, отбрасываем свои правки (UserKey) и отдаём остальным
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

    private const int ForegroundMs = 3_000;   // окно активно — живая совместная работа
    private const int BackgroundMs = 15_000;  // окно в фоне — экономный режим

    // Наблюдение за одной БД: постоянное соединение + последняя версия + курсор журнала.
    // Для PostgreSQL (Path — строка подключения) data_version не существует; дешёвый
    // детектор — MAX(id) журнала обратным сканом PK (микросекунды на тик).
    private sealed class Watch
    {
        public DbConnection? Conn;
        public string Path        = "";
        public bool   IsPostgres;
        public long   DataVersion = -1;
        public long   LastLogId   = -1;
    }
    private readonly Dictionary<int, Watch> _watch = [];

    public ChangePoller(Workspace ws, DbHealthMonitor health)
    {
        _ws     = ws;
        _health = health;
        _timer  = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(ForegroundMs, ForegroundMs);

    /// <summary>Окно активно/в фоне: в фоне частота проверки снижается.</summary>
    public void SetForeground(bool fg)
    {
        var period = fg ? ForegroundMs : BackgroundMs;
        _timer.Change(period, period);
    }

    public void Dispose()
    {
        _timer.Dispose();
        foreach (var w in _watch.Values) w.Conn?.Dispose();
        _watch.Clear();
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
                try { PollOne(db.DbId, db.Path); }
                catch (Exception ex)
                {
                    // Соединение могло умереть вместе с файлом/сетью: наблюдение сбрасываем
                    // (после возврата базы построится заново), монитору — сигнал о потере
                    // (BUSY он игнорирует сам). Поллер тем самым — второй детектор пропажи.
                    if (_watch.TryGetValue(db.DbId, out var w)) { w.Conn?.Dispose(); _watch.Remove(db.DbId); }
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
            w.Conn = w.IsPostgres
                ? new NpgsqlConnection(path)
                : new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            w.Conn.Open();
            // Первое знакомство: историю не проигрываем — курсор на текущий хвост журнала.
            if (!w.IsPostgres) w.DataVersion = ReadLong(w.Conn, "PRAGMA data_version;");
            w.LastLogId  = ReadLong(w.Conn, MaxLogIdSql(w.IsPostgres));
            _watch[dbId] = w;
            return;
        }

        if (w.IsPostgres)
        {
            // PG: дешёвый детектор — вырос ли журнал (обратный скан PK). Правки без записи
            // в change_log (только UI-таблицы) сосед и не должен подхватывать.
            if (ReadLong(w.Conn!, MaxLogIdSql(true)) <= w.LastLogId) return;
        }
        else
        {
            var version = ReadLong(w.Conn!, "PRAGMA data_version;");
            if (version == w.DataVersion) return;   // с прошлого тика БД никто не менял
            w.DataVersion = version;
        }

        // Кто-то коммитил: читаем хвост журнала. Курсор двигаем ВСЕГДА (и на своих правках),
        // чтобы не перечитывать один хвост по кругу. Хвост ограничен: при огромном отставании
        // (массовый импорт соседа) детали не важны — union типов и так вызовет полный refresh.
        var changes = new List<RemoteChange>();
        using (var cmd = w.Conn!.CreateCommand())
        {
            // Идентификаторы без кавычек: SQLite матчит PascalCase-колонки без учёта регистра,
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
                    continue;   // своя правка — UI уже обновлён по месту действия
                changes.Add(new RemoteChange(r.GetString(2), r.GetString(3),
                                             r.IsDBNull(4) ? null : r.GetString(4)));
            }
        }

        if (changes.Count > 0)
            RemoteChanges?.Invoke(dbId, changes);
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
