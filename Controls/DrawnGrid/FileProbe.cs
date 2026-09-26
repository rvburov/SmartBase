using System.Collections.Concurrent;
using Avalonia.Threading;

namespace SmartBase.Controls.DrawnGrid;

/// <summary>
/// Есть ли файл по пути — для ячеек типа «файл»: имя рисуется ссылкой, когда файл на месте, и
/// серым «— файл не найден», когда его нет; авто-привязка показывает «папка недоступна». Нынешняя
/// таблица спрашивала диск с UI-потока при каждом построении ячейки; на сетевой папке одна такая
/// проверка стоит десятки миллисекунд, а в кадре их десятки. Здесь ответ берется из кеша, а
/// неизвестные пути проверяются в фоне пачкой; по приходе ответов поверхность перерисовывается
/// (<see cref="Resolved"/>). Пока ответа нет, ячейка рисуется как «файл есть» — так она и будет
/// выглядеть в подавляющем большинстве случаев. Ответ живет <see cref="Ttl"/>: файл могли
/// удалить или положить, пока таблица открыта.
/// </summary>
internal static class FileProbe
{
    /// <summary>Срок годности ответа; по истечении путь перепроверяется в фоне, старый ответ
    /// показывается до нового.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private static readonly ConcurrentDictionary<string, (bool Exists, DateTime At)> _files   = new();
    private static readonly ConcurrentDictionary<string, (bool Exists, DateTime At)> _folders = new();
    private static readonly ConcurrentQueue<(string Path, bool Folder)> _queue = new();
    private static int _running;

    /// <summary>Пришли ответы фоновой проверки — на UI-потоке, один раз на пачку.</summary>
    public static event Action? Resolved;

    /// <summary>Стенды: отвечать синхронно (без фона), чтобы кадр с данными был окончательным.</summary>
    public static bool Synchronous { get; set; }

    /// <summary>Файл существует? null — пока неизвестно, проверка поставлена в очередь.</summary>
    public static bool? FileExists(string path) => Lookup(_files, path, folder: false);

    /// <summary>Папка существует? null — пока неизвестно.</summary>
    public static bool? FolderExists(string folder) => Lookup(_folders, folder, folder: true);

    /// <summary>Забыть ответ (после правки значения или выбора файла).</summary>
    public static void Forget(string path) => _files.TryRemove(path, out _);

    private static bool? Lookup(ConcurrentDictionary<string, (bool Exists, DateTime At)> map, string path, bool folder)
    {
        if (path.Length == 0) return false;
        if (Synchronous)
        {
            var now = folder ? Directory.Exists(path) : File.Exists(path);
            map[path] = (now, DateTime.UtcNow);
            return now;
        }
        if (map.TryGetValue(path, out var e))
        {
            if (DateTime.UtcNow - e.At > Ttl) Enqueue(path, folder);
            return e.Exists;
        }
        Enqueue(path, folder);
        return null;
    }

    private static void Enqueue(string path, bool folder)
    {
        _queue.Enqueue((path, folder));
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
            _ = Task.Run(Drain);
    }

    private static void Drain()
    {
        try
        {
            var any = false;
            while (_queue.TryDequeue(out var item))
            {
                bool exists;
                try { exists = item.Folder ? Directory.Exists(item.Path) : File.Exists(item.Path); }
                catch { exists = false; }
                (item.Folder ? _folders : _files)[item.Path] = (exists, DateTime.UtcNow);
                any = true;
            }
            if (any) Dispatcher.UIThread.Post(() => Resolved?.Invoke(), DispatcherPriority.Background);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            // Пока сбрасывали флаг, могли положить еще — не оставляем очередь без обработчика.
            if (!_queue.IsEmpty && Interlocked.CompareExchange(ref _running, 1, 0) == 0)
                _ = Task.Run(Drain);
        }
    }
}
