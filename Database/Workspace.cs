using Serilog;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// Рабочее пространство: набор смонтированных баз данных. Сплэш открывает только дефолтную
/// (база самой программы — после миграции открывается мгновенно), остальные БД реестра
/// монтирует MountQueue в фоне уже после показа главного окна: старт всегда быстрый,
/// сколько бы баз ни было и какого размера.
/// </summary>
public sealed class Workspace : IDisposable
{
    // Монтирует фоновый поток очереди, читает UI-поток — все обращения под замком,
    // Mounted отдаёт снимок.
    private readonly object _gate = new();
    private readonly List<DatabaseManager> _mounted = [];

    public DatabaseRegistry              Registry { get; }
    public DatabaseManager               Default  { get; }
    public IReadOnlyList<DatabaseManager> Mounted
    {
        get { lock (_gate) return _mounted.ToList(); }
    }

    private Workspace(DatabaseRegistry registry, DatabaseManager def)
    {
        Registry = registry;
        Default  = def;
        _mounted.Add(def);
    }

    /// <summary>Открыть рабочее пространство: смонтировать ТОЛЬКО дефолтную БД.
    /// <paramref name="progress"/> — прогресс для сплэша (доля 0..1 + текущий этап);
    /// разовая миграция/сжатие могут быть долгими, поэтому вызывается в фоне.
    /// Остальные БД реестра ставит в очередь MainWindow после показа окна.</summary>
    public static Workspace Open(DatabaseRegistry registry,
                                 IProgress<(double Fraction, string Message)>? progress = null)
    {
        var defEntry = registry.GetAll().First(d => d.IsDefault);

        // Дефолт всегда открываем по актуальному пути приложения (а не по сохранённому
        // в реестре — на случай переноса), но идентичность (DbId, Name) берём из реестра.
        // Долгие этапы репортят текст и живую долю — иначе на больших БД сплэш выглядит
        // зависшим (замерший процент неотличим от зависания).
        progress?.Report((0, $"Открываю базу «{defEntry.Name}»…"));
        var def = DatabaseManager.Open(defEntry.Id, defEntry.Name, AppSettings.DbPath,
            stage: (msg, inner) => progress?.Report((Math.Clamp(inner ?? 0, 0, 1), msg)));
        return new Workspace(registry, def);
    }

    /// <summary>Смонтировать БД из записи реестра. Возвращает null при ошибке (недоступна/повреждена).</summary>
    public DatabaseManager? Mount(DatabaseRefEntity entry, Action<string, double?>? stage = null)
    {
        lock (_gate)
        {
            var existing = _mounted.FirstOrDefault(m => m.DbId == entry.Id);
            if (existing is not null) return existing;
        }
        try
        {
            // PG-запись: монтируем по строке подключения, собранной из Pg*-полей реестра
            // (entry.Path хранит только отображаемый вид без пароля). У синтетической записи
            // ретраев DbHealthMonitor Pg*-полей нет — там Path уже строка подключения.
            string mountPath;
            if (entry.IsPostgres)
            {
                var info = entry.ToPgInfo();
                if (info is not null) mountPath = info.ToConnectionString();
                else if (PgConnectionInfo.IsPostgresConnectionString(entry.Path)) mountPath = entry.Path;
                else return null;   // повреждённая запись реестра
            }
            else mountPath = entry.Path;

            // Открытие вне замка: миграция большой базы может идти минуты, UI в это время
            // свободно читает Mounted (у каждого SQLite-файла свои блокировки).
            var manager = DatabaseManager.Open(entry.Id, entry.Name, mountPath, entry.Provider, stage);
            lock (_gate)
            {
                // Гонка двух монтирований одной БД: оставляем первый, лишний закрываем.
                var raced = _mounted.FirstOrDefault(m => m.DbId == entry.Id);
                if (raced is not null) { manager.Dispose(); return raced; }
                _mounted.Add(manager);
            }
            return manager;
        }
        catch (Exception ex)
        {
            // Недоступная/повреждённая БД — статус покажут сайдбар и список БД в настройках.
            // Единая точка сбоя открытия БД — здесь же диагностический лог (имя + путь без пароля).
            Log.Warning(ex, "Не удалось смонтировать БД «{Name:l}» ({Path:l})", entry.Name, entry.Path);
            return null;
        }
    }

    /// <summary>Размонтировать БД (закрыть контекст). Дефолтную БД размонтировать нельзя.</summary>
    public void Unmount(int dbId)
    {
        if (dbId == Default.DbId) return;
        DatabaseManager? manager;
        lock (_gate)
        {
            manager = _mounted.FirstOrDefault(m => m.DbId == dbId);
            if (manager is null) return;
            _mounted.Remove(manager);
        }
        manager.Dispose();
    }

    /// <summary>Найти смонтированную БД по её DbId.</summary>
    public DatabaseManager? Get(int dbId)
    {
        lock (_gate) return _mounted.FirstOrDefault(m => m.DbId == dbId);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var m in _mounted) m.Dispose();
            _mounted.Clear();
        }
    }
}
