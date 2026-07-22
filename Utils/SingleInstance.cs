namespace SmartBase.Utils;

/// <summary>
/// Единственный экземпляр приложения на локальную папку данных. Нужен для фонового режима:
/// автозапуск + ручной запуск не должны поднять ВТОРОЙ фоновый процесс (иначе — двойные
/// уведомления), а повторный запуск должен вернуть окно уже работающего.
///
/// Реализация — эксклюзивный лок-файл (<c>instance.lock</c>, <see cref="FileShare.None"/>),
/// удерживаемый до выхода процесса: второй захват падает. Второй экземпляр пишет
/// <c>show.signal</c> и завершается; работающий следит за файлом (<see cref="FileSystemWatcher"/>)
/// и поднимает <see cref="ShowRequested"/>. Лок по ЛОКАЛЬНОЙ папке данных — сценарий общей
/// сетевой БД между разными машинами не затрагивается (у каждой своя папка).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string LockName   = "instance.lock";
    private const string SignalName = "show.signal";

    private readonly FileStream _lock;
    private readonly string _dir;
    private FileSystemWatcher? _watcher;

    /// <summary>Работающий экземпляр попросили показать окно (пришёл второй запуск).</summary>
    public event Action? ShowRequested;

    private SingleInstance(FileStream lockStream, string dir)
    {
        _lock = lockStream;
        _dir  = dir;
    }

    /// <summary>Стать единственным экземпляром для папки <paramref name="dataDir"/>.
    /// Возвращает владельца лока или <c>null</c>, если другой экземпляр уже работает.</summary>
    public static SingleInstance? TryAcquire(string dataDir)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var fs = new FileStream(Path.Combine(dataDir, LockName),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstance(fs, dataDir);
        }
        catch (IOException)
        {
            return null;   // лок занят живым процессом
        }
    }

    /// <summary>Из второго экземпляра: попросить работающий показать окно (перед выходом).</summary>
    public static void SignalShow(string dataDir)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, SignalName),
                DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { /* не смогли сигналить — не критично */ }
    }

    /// <summary>Начать слушать сигнал «показать окно» (вызывать после создания окна).</summary>
    public void StartListening()
    {
        try
        {
            _watcher = new FileSystemWatcher(_dir, SignalName)
            {
                NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, _) => ShowRequested?.Invoke();
            _watcher.Changed += (_, _) => ShowRequested?.Invoke();
        }
        catch { /* нет наблюдателя (напр. сетевой путь) — вернуть окно можно кликом/треем */ }
    }

    public void Dispose()
    {
        try { _watcher?.Dispose(); } catch { }
        try { _lock.Dispose(); }    catch { }
    }
}
