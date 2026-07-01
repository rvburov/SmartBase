using DataBase.Database.Entities;
using DataBase.Utils;

namespace DataBase.Database;

/// <summary>
/// Рабочее пространство: набор смонтированных баз данных. Сейчас монтирует только
/// дефолтную; на Этапе 3 здесь же будут монтироваться остальные БД из реестра, и
/// сайдбар/модули начнут адресовать сущности парой (DbId, Id).
/// </summary>
public sealed class Workspace : IDisposable
{
    private readonly List<DatabaseManager> _mounted = [];

    public DatabaseRegistry              Registry { get; }
    public DatabaseManager               Default  { get; }
    public IReadOnlyList<DatabaseManager> Mounted  => _mounted;

    private Workspace(DatabaseRegistry registry, DatabaseManager def)
    {
        Registry = registry;
        Default  = def;
        _mounted.Add(def);
    }

    /// <summary>Открыть рабочее пространство: смонтировать дефолтную БД и все остальные из реестра.
    /// <paramref name="progress"/> — опциональный прогресс (доля 0..1 + текущий этап: какая БД
    /// открывается) для сплэш-экрана; монтирование может быть долгим (миграции/бэкафилл на больших
    /// БД), поэтому вызывается в фоне. Доля считается по числу баз (+1 слот под запуск интерфейса).</summary>
    public static Workspace Open(DatabaseRegistry registry,
                                 IProgress<(double Fraction, string Message)>? progress = null)
    {
        var all      = registry.GetAll();
        var defEntry = all.First(d => d.IsDefault);
        var total    = all.Count;
        var step     = 0;
        double Frac() => total > 0 ? (double)step / (total + 1) : 0;   // +1 — слот под «Запуск интерфейса…»

        // Дефолт всегда открываем по актуальному пути приложения (а не по сохранённому
        // в реестре — на случай переноса), но идентичность (DbId, Name) берём из реестра.
        progress?.Report((Frac(), $"Открываю базу «{defEntry.Name}»…"));
        var def = DatabaseManager.Open(defEntry.Id, defEntry.Name, AppSettings.DbPath);
        var ws  = new Workspace(registry, def);
        step++;

        // Остальные зарегистрированные БД монтируем по сохранённому пути (файл создаётся
        // и мигрируется при первом монтировании). Сбой одной БД — например, недоступна
        // сетевая папка — не должен ронять старт; полноценная обработка недоступности
        // будет на Этапе 5.
        foreach (var e in all.Where(d => !d.IsDefault))
        {
            progress?.Report((Frac(), $"Открываю базу «{e.Name}»…"));
            ws.Mount(e);
            step++;
        }

        return ws;
    }

    /// <summary>Смонтировать БД из записи реестра. Возвращает null при ошибке (недоступна/повреждена).</summary>
    public DatabaseManager? Mount(DatabaseRefEntity entry)
    {
        var existing = _mounted.FirstOrDefault(m => m.DbId == entry.Id);
        if (existing is not null) return existing;
        try
        {
            var manager = DatabaseManager.Open(entry.Id, entry.Name, entry.Path);
            _mounted.Add(manager);
            return manager;
        }
        catch
        {
            // Недоступная/повреждённая БД — пропускаем (обработка статусов — Этап 5).
            return null;
        }
    }

    /// <summary>Размонтировать БД (закрыть контекст). Дефолтную БД размонтировать нельзя.</summary>
    public void Unmount(int dbId)
    {
        if (dbId == Default.DbId) return;
        var manager = _mounted.FirstOrDefault(m => m.DbId == dbId);
        if (manager is null) return;
        _mounted.Remove(manager);
        manager.Dispose();
    }

    /// <summary>Найти смонтированную БД по её DbId.</summary>
    public DatabaseManager? Get(int dbId) => _mounted.FirstOrDefault(m => m.DbId == dbId);

    public void Dispose()
    {
        foreach (var m in _mounted) m.Dispose();
        _mounted.Clear();
    }
}
