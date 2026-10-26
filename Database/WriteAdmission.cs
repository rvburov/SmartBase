using System.Collections.Concurrent;

namespace SmartBase.Database;

/// <summary>Ограниченная очередь писателей одного процесса. Разрешение передается старшему
/// ожидающему до пробуждения его потока: новый вызов не может его обогнать.</summary>
internal sealed class WriteAdmission
{
    private static readonly ConcurrentDictionary<string, WriteAdmission> Databases = new(StringComparer.Ordinal);
    internal static WriteAdmission ForDatabase(string key, int capacity) =>
        Databases.GetOrAdd(key, _ => new WriteAdmission(capacity));

    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _waiting = new();
    private readonly int _capacity;
    private int _active;

    internal WriteAdmission(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    internal int WaitingCount { get { lock (_sync) return _waiting.Count; } }

    internal IDisposable Enter(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        using var waiter = new Waiter(async: false);
        lock (_sync)
        {
            if (_active < _capacity && _waiting.Count == 0)
            {
                _active++;
                return new Lease(this);
            }
            waiter.Node = _waiting.AddLast(waiter);
        }

        try { waiter.Signal!.Wait(timeout, cancellationToken); }
        catch
        {
            var release = false;
            lock (_sync)
            {
                if (waiter.Granted) release = true;
                else _waiting.Remove(waiter.Node!);
            }
            if (release) Release();
            throw;
        }
        lock (_sync)
        {
            // На границе таймаута разрешение уже могло перейти этому участнику.
            // В таком случае его нельзя потерять или отдать еще одному писателю.
            if (waiter.Granted) return new Lease(this);
            _waiting.Remove(waiter.Node!);
            waiter.Node = null;
        }
        throw new TimeoutException("Очередь записи базы занята. Повторите действие.");
    }

    internal async ValueTask<IDisposable> EnterAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        using var waiter = new Waiter(async: true);
        lock (_sync)
        {
            if (_active < _capacity && _waiting.Count == 0)
            {
                _active++;
                return new Lease(this);
            }
            waiter.Node = _waiting.AddLast(waiter);
        }
        try { await waiter.AsyncSignal!.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lock (_sync)
            {
                if (waiter.Granted) return new Lease(this);
                _waiting.Remove(waiter.Node!);
            }
            throw new TimeoutException("Очередь записи базы занята. Повторите действие.");
        }
        catch
        {
            var release = false;
            lock (_sync)
            {
                if (waiter.Granted) release = true;
                else _waiting.Remove(waiter.Node!);
            }
            if (release) Release();
            throw;
        }
        return new Lease(this);
    }

    private void Release()
    {
        lock (_sync)
        {
            if (_waiting.First is { } head)
            {
                _waiting.RemoveFirst();
                head.Value.Node = null;
                head.Value.Granted = true;
                // Сигнал под тем же замком: Enter не уничтожит событие до Set.
                if (head.Value.Signal is { } signal) signal.Set();
                else head.Value.AsyncSignal!.TrySetResult(true);
            }
            else _active--;
        }
    }

    private sealed class Waiter(bool async) : IDisposable
    {
        internal readonly ManualResetEventSlim? Signal = async ? null : new(false, spinCount: 0);
        internal readonly TaskCompletionSource<bool>? AsyncSignal = async
            ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        internal LinkedListNode<Waiter>? Node;
        internal bool Granted;
        public void Dispose() => Signal?.Dispose();
    }

    private sealed class Lease(WriteAdmission owner) : IDisposable
    {
        private WriteAdmission? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
