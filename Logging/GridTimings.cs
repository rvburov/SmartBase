using System.Collections.Concurrent;
using System.Diagnostics;

namespace SmartBase.Logging;

/// <summary>
/// Счетчики времени UI-потока для стендов рисованной сетки: сколько раз и как долго шли
/// заполнение страницы, дочитка колонок, смена длины списка, рисование, синхронизация полос.
/// В программе выключены (<see cref="Enabled"/> = false) — ноль работы, кроме проверки флага;
/// стенд включает и печатает <see cref="Report"/>. Нужны, чтобы отличать цену самой сетки от
/// артефактов безголовой платформы и чтобы видеть, какая именно операция съела кадр.
/// </summary>
internal static class GridTimings
{
    public static bool Enabled { get; set; }

    private sealed class Slot
    {
        public int    Count;
        public double MaxMs;
        public double TotalMs;
        public string Note = "";
    }

    private static readonly ConcurrentDictionary<string, Slot> _slots = new();
    private static readonly ConcurrentQueue<(string Name, double Ms, string Note)> _trace = new();
    private const int TraceLimit = 400;

    /// <summary>Записать длительность операции (мс) под именем; <paramref name="note"/> — контекст самого долгого случая.</summary>
    public static void Record(string name, double ms, string? note = null)
    {
        if (!Enabled) return;
        var s = _slots.GetOrAdd(name, _ => new Slot());
        lock (s)
        {
            s.Count++;
            s.TotalMs += ms;
            if (ms >= s.MaxMs) { s.MaxMs = ms; if (note is not null) s.Note = note; }
        }
        _trace.Enqueue((name, ms, note ?? ""));
        while (_trace.Count > TraceLimit) _trace.TryDequeue(out _);
    }

    /// <summary>Последние записи по порядку (до 400) — чтобы видеть каждую страницу и каждый кадр, а не только максимум.</summary>
    public static IReadOnlyList<(string Name, double Ms, string Note)> Trace() => _trace.ToArray();

    /// <summary>Засечь начало; вернуть timestamp для <see cref="Stop"/>. Ноль — счетчики выключены.</summary>
    public static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static void Stop(long start, string name, string? note = null)
    {
        if (start == 0) return;
        Record(name, Stopwatch.GetElapsedTime(start).TotalMilliseconds, note);
    }

    public static void Clear() { _slots.Clear(); _trace.Clear(); }

    /// <summary>Строки отчета: имя, число вызовов, сумма, максимум, контекст максимума.</summary>
    public static IReadOnlyList<(string Name, int Count, double TotalMs, double MaxMs, string Note)> Report() =>
        _slots.OrderByDescending(kv => kv.Value.TotalMs)
              .Select(kv => (kv.Key, kv.Value.Count, kv.Value.TotalMs, kv.Value.MaxMs, kv.Value.Note))
              .ToList();
}
