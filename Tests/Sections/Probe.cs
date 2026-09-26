using System.Diagnostics;
using Avalonia.Threading;
using Serilog.Core;
using Serilog.Events;

namespace SmartBase.Tests.Sections;

/// <summary>
/// Проба UI-потока: фоновый поток ставит в диспетчер задание высшего приоритета и ждет его
/// выполнения. Задержка ответа — это остаток задания, которое поток выполнял в этот момент:
/// дольше порога — пауза (интерфейс в это время не отвечал бы ни на клик, ни на кадр).
/// </summary>
sealed class StallProbe
{
    public sealed record Stall(DateTime StartUtc, double Ms, string Step);

    private readonly double  _minMs;
    private readonly Thread  _thread;
    private volatile bool    _stop;

    /// <summary>Текущий шаг сценария — им подписывается пауза.</summary>
    public volatile string Step = "";

    public List<Stall> Stalls { get; } = [];

    public StallProbe(double minMs)
    {
        _minMs  = minMs;
        _thread = new Thread(Run) { IsBackground = true, Name = "stall-probe" };
    }

    public void Start() => _thread.Start();

    public void Stop()
    {
        _stop = true;
        _thread.Join(3000);
    }

    private void Run()
    {
        using var done = new AutoResetEvent(false);
        while (!_stop)
        {
            var t0 = DateTime.UtcNow;
            var sw = Stopwatch.StartNew();
            Dispatcher.UIThread.Post(() => done.Set(), DispatcherPriority.Send);
            while (!done.WaitOne(200))
                if (_stop) return;
            var ms = sw.Elapsed.TotalMilliseconds;
            if (ms >= _minMs)
                lock (Stalls) Stalls.Add(new Stall(t0, ms, Step));
            Thread.Sleep(5);
        }
    }
}

/// <summary>Журнал программы в памяти стенда: записи с моментом от старта, Warning и выше — сразу на экран.</summary>
sealed class MemorySink(Stopwatch clock, bool echoInfo) : ILogEventSink
{
    public sealed record Entry(double AtSec, LogEventLevel Level, string Text);

    public List<Entry> Entries { get; } = [];

    public void Emit(LogEvent e)
    {
        var text = e.RenderMessage();
        if (e.Exception is { } ex) text += " | " + ex.GetType().Name + ": " + ex.Message;
        var entry = new Entry(clock.Elapsed.TotalSeconds, e.Level, text);
        lock (Entries) Entries.Add(entry);
        if (e.Level >= LogEventLevel.Warning || echoInfo)
            Console.WriteLine($"    [журнал {entry.AtSec,6:0.0} с] {Level(e.Level)} {Trim(text, 220)}");
    }

    public static string Level(LogEventLevel l) => l switch
    {
        LogEventLevel.Warning => "ПРЕДУПР",
        LogEventLevel.Error   => "ОШИБКА",
        LogEventLevel.Fatal   => "СБОЙ",
        _                     => "сведения",
    };

    public static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Все замеры Diag стенда: операция, длительность, контекст, шаг сценария.</summary>
sealed class DiagLog(Stopwatch clock, StallProbe probe)
{
    public sealed record Op(double EndSec, string Name, double Ms, string? Context, string Step);

    public List<Op> Ops { get; } = [];

    public void Observe(string name, TimeSpan elapsed, string? context)
    {
        lock (Ops) Ops.Add(new Op(clock.Elapsed.TotalSeconds, name, elapsed.TotalMilliseconds, context, probe.Step));
    }
}
