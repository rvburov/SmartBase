using System.Diagnostics;
using SmartBase.Core.Models;

namespace SmartBase.Database;

/// <summary>Неизменяемые снимки каталога живут на диске, в памяти — два кешированных дескриптора и снимки активных читателей.
/// Сохранение/обновление строит новый снимок; старый удаляется после последнего читателя.
/// Большие обходы идут последовательно, повторный ввод использует готовый каталог.</summary>
internal static class FileAutoCatalogCache
{
    private sealed record Key(string Folder, string Format, bool Recursive, bool Partial);
    internal sealed class Entry(FileAutoCatalog catalog)
    {
        internal readonly FileAutoCatalog Catalog = catalog;
        internal readonly object QueryLock = new();
        internal long BuiltAt = Stopwatch.GetTimestamp();
        internal long UsedAt;
        internal int Readers;
        internal bool Retired;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<Key, Entry> Entries = [];
    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    private const int Capacity = 2;

    static FileAutoCatalogCache() => AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        lock (Gate)
        {
            foreach (var entry in Entries.Values) entry.Catalog.Dispose();
            Entries.Clear();
        }
    };

    internal sealed class Lease : IDisposable
    {
        private Entry? _entry;
        internal Lease(Entry entry) => _entry = entry;

        internal string Match(FileAutoRule rule, string source, CancellationToken ct)
        {
            var entry = _entry ?? throw new ObjectDisposedException(nameof(Lease));
            // У каталога одно подключение. Ожидание отменяется, запрос никогда не идет на UI.
            while (!Monitor.TryEnter(entry.QueryLock, 50)) ct.ThrowIfCancellationRequested();
            try { ct.ThrowIfCancellationRequested(); return entry.Catalog.Match(rule, source, ct); }
            finally { Monitor.Exit(entry.QueryLock); }
        }

        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry is null) return;
            lock (Gate)
            {
                entry.Readers--;
                if (entry.Retired && entry.Readers == 0) entry.Catalog.Dispose();
            }
        }
    }

    internal static Lease Acquire(FileAutoRule rule, bool refresh, CancellationToken ct,
        Action<long>? scanned = null)
    {
        ct.ThrowIfCancellationRequested();
        var requested = Stopwatch.GetTimestamp();
        var key = new Key(rule.Folder, rule.Format, rule.Recursive, rule.PartialMatch);
        lock (Gate)
            if (!refresh && Entries.TryGetValue(key, out var ready)) return Take(ready);

        BuildGate.Wait(ct);
        try
        {
            lock (Gate)
                if (Entries.TryGetValue(key, out var ready) && (!refresh || ready.BuiltAt >= requested))
                    return Take(ready);

            var fresh = new Entry(FileAutoCatalog.CreateFromFolder(rule, ct, scanned));
            lock (Gate)
            {
                if (Entries.Remove(key, out var old)) Retire(old);
                Entries[key] = fresh;
                // Активные читатели держат собственный lease; вытеснение не закрывает их файл.
                while (Entries.Count > Capacity)
                {
                    var oldest = Entries.Where(p => p.Key != key).MinBy(p => p.Value.UsedAt);
                    Entries.Remove(oldest.Key);
                    Retire(oldest.Value);
                }
                return Take(fresh);
            }
        }
        finally { BuildGate.Release(); }
    }

    private static Lease Take(Entry entry)
    {
        entry.Readers++;
        entry.UsedAt = Stopwatch.GetTimestamp();
        return new Lease(entry);
    }

    private static void Retire(Entry entry)
    {
        entry.Retired = true;
        if (entry.Readers == 0) entry.Catalog.Dispose();
    }
}
