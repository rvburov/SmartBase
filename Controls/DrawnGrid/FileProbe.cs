using System.Diagnostics;
using Avalonia.Threading;

namespace SmartBase.Controls.DrawnGrid;

/// <summary>
/// Проверка файлов для рисованных ячеек и редактора. UI читает ограниченный кеш, а неизвестные
/// и просроченные пути проверяют четыре фоновых обработчика. Повтор одного пути не создает
/// вторую проверку; заполненная очередь не задерживает кадр. Ответы приходят по мере готовности,
/// даже когда другие пути еще ждут медленного сетевого диска.
/// </summary>
internal static class FileProbe
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    internal const int CacheCapacity = 4096;
    internal const int QueueCapacity = 256;
    internal const int WorkerCount = 4;
    private const int MaxPathCharacters = 32768;
    private const int CacheCharacters = 1024 * 1024;
    private const int QueueCharacters = 256 * 1024;

    private readonly record struct ProbeKey(string Path, bool Folder);
    private sealed class Cached(bool exists, long at, LinkedListNode<ProbeKey> node)
    {
        public bool Exists = exists;
        public long At = at;
        public readonly LinkedListNode<ProbeKey> Node = node;
    }
    private sealed class Pending(ProbeKey key)
    {
        public readonly ProbeKey Key = key;
        public readonly TaskCompletionSource<bool> Done = NewSignal();
        public bool Invalidated;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<ProbeKey, Cached> Cache = new();
    private static readonly LinkedList<ProbeKey> Recent = new();
    private static readonly Dictionary<ProbeKey, Pending> PendingByKey = new();
    private static readonly Queue<Pending> Queue = new();
    private static readonly SemaphoreSlim Ready = new(0);
    private static TaskCompletionSource<bool> _capacityChanged = NewSignal();
    private static bool _workersStarted;
    private static int _cachedCharacters;
    private static int _queuedCharacters;
    private static int _synchronous;
    private static int _notificationScheduled;
    private static int _completedSinceNotification;
    private static Func<string, bool, bool>? _probeOverride;

    /// <summary>Свежие ответы — на UI-потоке. Несколько готовых ответов объединяются в одно
    /// уведомление, а не ждут опустошения всей очереди.</summary>
    public static event Action? Resolved;

    /// <summary>Стенды: синхронный ответ вместо фона, чтобы кадр сразу был окончательным.</summary>
    public static bool Synchronous
    {
        get => Volatile.Read(ref _synchronous) != 0;
        set => Volatile.Write(ref _synchronous, value ? 1 : 0);
    }

    // Управляемая задержка/ошибка диска в самостоятельном стенде; в программе не задается.
    internal static Func<string, bool, bool>? ProbeOverride
    {
        get => Volatile.Read(ref _probeOverride);
        set => Volatile.Write(ref _probeOverride, value);
    }

    internal static (int Cached, int Queued, int Pending) Counts
    {
        get { lock (Gate) return (Cache.Count, Queue.Count, PendingByKey.Count); }
    }

    public static bool? FileExists(string path) => Lookup(new(path, false));
    public static bool? FolderExists(string folder) => Lookup(new(folder, true));
    public static Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
        ResolveAsync(new(path, false), cancellationToken);
    public static Task<bool> FolderExistsAsync(string folder, CancellationToken cancellationToken = default) =>
        ResolveAsync(new(folder, true), cancellationToken);

    /// <summary>Забыть ответ после выбора/правки. Уже выполняющийся запрос остается единственным
    /// для пути, но его старый ответ больше не попадет в кеш.</summary>
    public static void Forget(string path, bool folder = false)
    {
        lock (Gate)
        {
            var key = new ProbeKey(path, folder);
            if (Cache.Remove(key, out var cached))
            {
                Recent.Remove(cached.Node);
                _cachedCharacters -= key.Path.Length;
            }
            if (PendingByKey.TryGetValue(key, out var pending)) pending.Invalidated = true;
        }
    }

    /// <summary>После явного обновления файлов старые положительные и отрицательные ответы
    /// больше не действуют. IO не выполняется; видимые редакторы запросят свежие ответы в фоне.</summary>
    public static void ForgetAll()
    {
        lock (Gate)
        {
            Cache.Clear();
            Recent.Clear();
            _cachedCharacters = 0;
            foreach (var pending in PendingByKey.Values) pending.Invalidated = true;
        }
        NotifyResolved();
    }

    private static bool? Lookup(ProbeKey key)
    {
        if (key.Path.Length == 0 || key.Path.Length > MaxPathCharacters) return false;
        if (Synchronous)
        {
            var exists = Probe(key);
            lock (Gate)
            {
                if (PendingByKey.TryGetValue(key, out var pending)) pending.Invalidated = true;
                Store(key, exists);
            }
            return exists;
        }
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                Recent.Remove(cached.Node);
                Recent.AddLast(cached.Node);
                if (Stopwatch.GetElapsedTime(cached.At) >= Ttl) Schedule(key);
                return cached.Exists;
            }
            Schedule(key);
            return null;
        }
    }

    private static async Task<bool> ResolveAsync(ProbeKey key, CancellationToken cancellationToken)
    {
        if (key.Path.Length == 0 || key.Path.Length > MaxPathCharacters) return false;
        if (Synchronous) return Lookup(key) == true;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task signal;
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached) && Stopwatch.GetElapsedTime(cached.At) < Ttl)
                    return cached.Exists;
                signal = Schedule(key)?.Done.Task ?? _capacityChanged.Task;
            }
            // Отмена снимает только ожидание редактора; общий запрос нужен другим ячейкам.
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Вызывается под Gate; очередь и pending вместе ограничены QueueCapacity + WorkerCount.
    private static Pending? Schedule(ProbeKey key)
    {
        if (PendingByKey.TryGetValue(key, out var pending)) return pending;
        if (Queue.Count >= QueueCapacity || _queuedCharacters + key.Path.Length > QueueCharacters) return null;
        pending = new Pending(key);
        PendingByKey.Add(key, pending);
        Queue.Enqueue(pending);
        _queuedCharacters += key.Path.Length;
        if (!_workersStarted)
        {
            _workersStarted = true;
            for (var i = 0; i < WorkerCount; i++) _ = Task.Run(Work);
        }
        Ready.Release();
        return pending;
    }

    private static async Task Work()
    {
        while (true)
        {
            await Ready.WaitAsync().ConfigureAwait(false);
            Pending request;
            lock (Gate)
            {
                request = Queue.Dequeue();
                _queuedCharacters -= request.Key.Path.Length;
                var capacity = _capacityChanged;
                _capacityChanged = NewSignal();
                capacity.TrySetResult(true);
            }
            var exists = Probe(request.Key);
            lock (Gate)
            {
                PendingByKey.Remove(request.Key);
                if (!request.Invalidated) Store(request.Key, exists);
            }
            request.Done.TrySetResult(true);
            NotifyResolved();
        }
    }

    private static bool Probe(ProbeKey key)
    {
        try
        {
            var probe = ProbeOverride;
            return probe is not null ? probe(key.Path, key.Folder)
                : key.Folder ? Directory.Exists(key.Path) : File.Exists(key.Path);
        }
        catch { return false; }
    }

    private static void Store(ProbeKey key, bool exists)
    {
        if (Cache.TryGetValue(key, out var cached))
        {
            cached.Exists = exists;
            cached.At = Stopwatch.GetTimestamp();
            Recent.Remove(cached.Node);
            Recent.AddLast(cached.Node);
            return;
        }
        while ((Cache.Count >= CacheCapacity || _cachedCharacters + key.Path.Length > CacheCharacters)
               && Recent.First is { } first)
        {
            Cache.Remove(first.Value);
            _cachedCharacters -= first.Value.Path.Length;
            Recent.RemoveFirst();
        }
        var node = Recent.AddLast(key);
        Cache.Add(key, new Cached(exists, Stopwatch.GetTimestamp(), node));
        _cachedCharacters += key.Path.Length;
    }

    private static void NotifyResolved()
    {
        Interlocked.Increment(ref _completedSinceNotification);
        if (Interlocked.CompareExchange(ref _notificationScheduled, 1, 0) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _completedSinceNotification, 0);
            try { Resolved?.Invoke(); }
            finally
            {
                Volatile.Write(ref _notificationScheduled, 0);
                if (Volatile.Read(ref _completedSinceNotification) > 0) NotifyResolved();
            }
        }, DispatcherPriority.Background);
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
