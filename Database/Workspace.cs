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
    // Mounted отдает снимок.
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

        // Дефолт всегда открываем по актуальному пути приложения (а не по сохраненному
        // в реестре — на случай переноса), но идентичность (DbId, Name) берем из реестра.
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
                else return null;   // поврежденная запись реестра
            }
            else mountPath = entry.Path;

            // Открытие вне замка: миграция большой базы может идти минуты, UI в это время
            // свободно читает Mounted (у каждого SQLite-файла свои блокировки).
            DatabaseManager manager;
            try
            {
                manager = DatabaseManager.Open(entry.Id, entry.Name, mountPath, entry.Provider, stage);
            }
            catch (Exception ex) when (ex is not MountBlockedException && !entry.IsPostgres
                                       && NetworkPath.IsNetwork(mountPath) && DbHealthMonitor.IsBusyError(ex))
            {
                // Сетевой файл ответил BUSY на первом же запросе монтирования. Чаще всего это не
                // чужой писатель, а мертвые хендлы в пуле подключений после перезагрузки сервера
                // (см. AppDbContext.ResetPool): раньше каждая попытка брала из пула тот же хендл,
                // падала «database is locked» и уходила на новый круг ретраев по минуте.
                Log.Information(ex, "Монтирование «{Name:l}»: BUSY на сетевом пути — сбрасываю пул подключений и повторяю", entry.Name);
                AppDbContext.ResetPool(mountPath);
                manager = DatabaseManager.Open(entry.Id, entry.Name, mountPath, entry.Provider, stage);
            }
            lock (_gate)
            {
                // Гонка двух монтирований одной БД: оставляем первый, лишний закрываем.
                var raced = _mounted.FirstOrDefault(m => m.DbId == entry.Id);
                if (raced is not null) { manager.Dispose(); return raced; }
                _mounted.Add(manager);
            }
            return manager;
        }
        catch (MountBlockedException ex)
        {
            // Понятная внешняя причина (файл в WAL у другого компьютера, чужая миграция): причина
            // словами уходит в статус базы через MountQueue (TakeMountFailReason) и в журнал.
            _failReasons[entry.Id] = ex.Reason;
            Log.Warning(ex.InnerException, "Не удалось смонтировать БД «{Name:l}» ({Path:l}): {Reason:l}", entry.Name, entry.Path, ex.Reason);
            return null;
        }
        catch (Exception ex)
        {
            // Недоступная/поврежденная БД — статус покажут сайдбар и список БД в настройках.
            // Единая точка сбоя открытия БД — здесь же диагностический лог (имя + путь без пароля).
            Log.Warning(ex, "Не удалось смонтировать БД «{Name:l}» ({Path:l})", entry.Name, entry.Path);
            return null;
        }
    }

    // Причины последнего неудачного монтирования словами (см. MountBlockedException) — очередь
    // забирает их для статуса «недоступна (…)» и ретраев монитора.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _failReasons = new();

    /// <summary>Забрать причину последнего отказа монтирования (null — обычный отказ:
    /// файл не найден / не удалось открыть).</summary>
    public string? TakeMountFailReason(int dbId) =>
        _failReasons.TryRemove(dbId, out var reason) ? reason : null;

    /// <summary>Размонтировать БД (закрыть контекст). Дефолтную БД размонтировать нельзя.</summary>
    public void Unmount(int dbId)
    {
        var manager = Detach(dbId);
        if (manager is not null) Release(manager);
    }

    /// <summary>Убрать БД из рабочего пространства, НЕ закрывая ее: модули перестают видеть базу
    /// мгновенно, а закрытие (<see cref="Release"/>) можно сделать в фоне. Нужно при потере базы
    /// на ходу — Dispose ждет фоновые чистки до 5 с, и на мертвой сетевой папке это ожидание не
    /// должно лечь на UI-поток. Дефолтную отцепить нельзя (null).</summary>
    public DatabaseManager? Detach(int dbId)
    {
        if (dbId == Default.DbId) return null;
        lock (_gate)
        {
            var manager = _mounted.FirstOrDefault(m => m.DbId == dbId);
            if (manager is not null) _mounted.Remove(manager);
            return manager;
        }
    }

    /// <summary>Закрыть отцепленную БД: контекст плюс пул подключений к ее файлу (сброс пула —
    /// внутри DatabaseManager.Dispose; без него повторное монтирование после потери брало бы
    /// из пула мертвый хендл сетевого файла, см. AppDbContext.ResetPool). Сбой закрытия не
    /// мешает: база уже не в списке.</summary>
    public static void Release(DatabaseManager manager)
    {
        try { manager.Dispose(); }
        catch (Exception ex) { Log.Warning(ex, "Закрытие БД «{Name:l}» завершилось ошибкой", manager.Name); }
    }

    /// <summary>
    /// Закрыть все базы при выходе из приложения — с пределом времени. Закрытие сетевой SQLite-базы
    /// на зависшей папке не возвращается вовсе (операция стоит в ядре, ее не отменить): раньше
    /// обработчик выхода ждал ее без предела на UI-потоке, окно уже исчезло, а процесс оставался в
    /// Диспетчере задач с занятым exe — обновление и повторный запуск были невозможны. Теперь каждая
    /// база закрывается в фоне, ждем не дольше <paramref name="budget"/> на всех; не успевшие
    /// остаются системе (в режиме DELETE файлу это ничем не грозит: все зафиксированное уже на
    /// диске). Возвращает число закрытых вовремя.
    /// </summary>
    public static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(15);

    public int CloseAll(TimeSpan budget)
    {
        List<DatabaseManager> list;
        lock (_gate)
        {
            list = _mounted.ToList();
            _mounted.Clear();
        }
        var deadline = DateTime.UtcNow + budget;
        var closing  = list.Select(m => (Manager: m, Task: Task.Run(() => Release(m)))).ToList();
        var closed   = 0;
        foreach (var (manager, task) in closing)
        {
            var left = deadline - DateTime.UtcNow;
            if (task.IsCompleted || (left > TimeSpan.Zero && task.Wait(left))) { closed++; continue; }
            Log.Warning("База «{Name:l}» не закрылась за отведенное время ({Budget:l}) — закрытие оставлено системе: {Where:l}",
                        manager.Name, Logging.Diag.Duration(budget), manager.Context.DisplayPath);
        }
        return closed;
    }

    /// <summary>Рабочее пространство поверх УЖЕ открытой дефолтной базы — для стендов и тестов,
    /// где штатный <see cref="Open"/> не годится (он открывает базу по пути приложения).</summary>
    public static Workspace Attach(DatabaseRegistry registry, DatabaseManager def) => new(registry, def);

    /// <summary>Найти смонтированную БД по ее DbId.</summary>
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
