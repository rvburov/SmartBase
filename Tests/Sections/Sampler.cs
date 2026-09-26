using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace SmartBase.Tests.Sections;

/// <summary>
/// Выборки стеков процесса через EventPipe — тот же механизм, что у внешнего профилировщика, но
/// сессию открывает сам стенд: трасса пишется в файл, после прогона выборки UI-потока внутри
/// каждой паузы сводятся к методам программы, на которых поток стоял. Выборка — раз в
/// миллисекунду на каждый управляемый поток; стоящий в нативном вызове (SQLite, ожидание) поток
/// тоже дает выборку со своим стеком.
/// </summary>
sealed class Sampler
{
    private const string SampleProvider = "Microsoft-DotNETCore-SampleProfiler";

    private readonly EventPipeSession _session;
    private readonly FileStream       _file;
    private readonly Task             _copy;

    public string Path { get; }

    public Sampler(string path)
    {
        Path = path;
        var providers = new List<EventPipeProvider>
        {
            new(SampleProvider, EventLevel.Informational),
            // Загрузка методов — имена кадров для кода, собранного во время прогона.
            new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational,
                (long)(ClrTraceEventParser.Keywords.Jit | ClrTraceEventParser.Keywords.Loader)),
        };
        var client = new DiagnosticsClient(Environment.ProcessId);
        _session = client.StartEventPipeSession(providers, requestRundown: true, circularBufferMB: 1024);
        _file    = File.Create(path);
        _copy    = Task.Run(() => _session.EventStream.CopyTo(_file));
    }

    public void Stop()
    {
        _session.Stop();
        _copy.Wait();
        _file.Dispose();
    }

    /// <summary>Окно разбора по часам UTC: пауза UI-потока или медленный замер. <paramref name="AnyThread"/>
    /// — выборки всех потоков (фоновая работа замера), а не только UI-потока.</summary>
    public sealed record Span(DateTime StartUtc, DateTime EndUtc, string Label, bool AnyThread = false)
    {
        /// <summary>Выборки UI-потока внутри окна: стек от листа к корню.</summary>
        public List<string[]> Samples { get; } = [];
    }

    /// <summary>Разложить выборки UI-потока (<paramref name="threadId"/> — системный номер) по окнам.</summary>
    public static void Collect(string path, int threadId, IReadOnlyList<Span> spans)
    {
        if (spans.Count == 0) return;
        var sorted = spans.OrderBy(s => s.StartUtc).ToArray();
        var starts = sorted.Select(s => s.StartUtc).ToArray();

        var etlx = TraceLog.CreateFromEventPipeDataFile(path);
        using var log = new TraceLog(etlx);
        foreach (var ev in log.Events)
        {
            if (ev.ProviderName != SampleProvider) continue;
            var ui = ev.ThreadID == threadId;
            var t = ev.TimeStamp.ToUniversalTime();
            var i = Array.BinarySearch(starts, t);
            if (i < 0) i = ~i - 1;
            // Окна могут перекрываться (пауза внутри шага): кладем во все, что накрывают момент.
            string[]? frames = null;
            for (var k = i; k >= 0 && k > i - 64; k--)
            {
                var s = sorted[k];
                if (t < s.StartUtc || t > s.EndUtc || !(ui || s.AnyThread)) continue;
                frames ??= Frames(ev.CallStack());
                // Фоновые потоки без кадров программы — простой пула и таймеров, не работа замера.
                if (s.AnyThread && !frames.Any(IsApp)) continue;
                s.Samples.Add(frames);
            }
        }
    }

    private static string[] Frames(TraceCallStack? cs)
    {
        var list = new List<string>();
        for (; cs is not null; cs = cs.Caller)
            list.Add(cs.CodeAddress.FullMethodName is { Length: > 0 } n ? n : cs.CodeAddress.ModuleName + "!?");
        return [.. list];
    }

    // ── Сводка ───────────────────────────────────────────────────────────────

    /// <summary>Кадр программы (не стенда): за него отвечает наш код.</summary>
    public static bool IsApp(string frame) =>
        frame.StartsWith("SmartBase.", StringComparison.Ordinal) &&
        !frame.StartsWith("SmartBase.Tests.", StringComparison.Ordinal);

    /// <summary>Короткое имя кадра: «Класс.Метод», у async — «Класс.Метод async», у лямбды — «Класс.Метод λ».</summary>
    public static string Short(string frame)
    {
        var name = frame;
        var paren = name.IndexOf('(');
        if (paren > 0) name = name[..paren];
        // Состояние async-метода: Тип+<Метод>d__12.MoveNext → Тип.Метод async
        var lt = name.IndexOf("+<", StringComparison.Ordinal);
        var suffix = "";
        if (lt > 0)
        {
            var gt = name.IndexOf('>', lt);
            var inner = gt > lt ? name[(lt + 2)..gt] : "";
            var tail = gt > 0 ? name[(gt + 1)..] : "";
            if (tail.StartsWith("d__", StringComparison.Ordinal)) suffix = " async";
            else if (inner.Length == 0 || tail.Contains("DisplayClass")) suffix = " λ";
            else suffix = " λ";
            // Лямбда: Тип+<>c__DisplayClass5_0.<Метод>b__3 → имя метода из последних угловых скобок
            var lastLt = name.LastIndexOf('<');
            var lastGt = name.LastIndexOf('>');
            if (inner.Length == 0 && lastLt > lt && lastGt > lastLt) inner = name[(lastLt + 1)..lastGt];
            name = name[..lt] + "." + inner;
        }
        else if (name.Contains(".<", StringComparison.Ordinal))
        {
            // Лямбда без вложенного типа: Тип.<Метод>b__3_0
            var a = name.IndexOf(".<", StringComparison.Ordinal);
            var b = name.IndexOf('>', a);
            if (b > a) { name = name[..a] + "." + name[(a + 2)..b]; suffix = " λ"; }
        }
        var parts = name.Split('.');
        var shortName = parts.Length >= 2 ? parts[^2] + "." + parts[^1] : name;
        return shortName + suffix;
    }

    /// <summary>Главные цепочки окна: до <paramref name="top"/> ключей «ближайшие кадры программы ← …
    /// | лист» с долей выборок.</summary>
    public static IEnumerable<(double Share, string Key)> Top(Span span, int top = 4, int depth = 3)
    {
        if (span.Samples.Count == 0) yield break;
        var counts = new Dictionary<string, int>();
        foreach (var frames in span.Samples)
        {
            var app = frames.Where(IsApp).Select(Short).Distinct().Take(depth).ToList();
            var leaf = frames.Length > 0 ? Short(frames[0]) : "?";
            var key = (app.Count > 0 ? string.Join(" ← ", app) : "(вне кода программы)") + "  | лист " + leaf;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        foreach (var (key, n) in counts.OrderByDescending(kv => kv.Value).Take(top))
            yield return (100.0 * n / span.Samples.Count, key);
    }

    /// <summary>Включающее время ЛЮБЫХ методов (библиотек тоже) в окне: что именно дорого внутри
    /// метода программы. Кадры, через которые прошли почти все выборки (цикл диспетчера, сам
    /// обработчик), отбрасываются — они не отвечают на вопрос «на что ушло».</summary>
    public static IEnumerable<(int Samples, string Method)> InclusiveAll(Span span, int top = 20)
    {
        var counts = new Dictionary<string, int>();
        foreach (var frames in span.Samples)
            foreach (var m in frames.Select(Short).Distinct())
                counts[m] = counts.GetValueOrDefault(m) + 1;
        var cut = span.Samples.Count * 0.9;
        return counts.Where(kv => kv.Value < cut && !kv.Key.EndsWith("!?", StringComparison.Ordinal))
                     .OrderByDescending(kv => kv.Value).Take(top).Select(kv => (kv.Value, kv.Key));
    }

    /// <summary>Что внутри метода (<paramref name="method"/> — короткое имя, как в отчете): включающие доли
    /// кадров, вызванных из него, по выборкам окна, прошедшим через него. Total — сколько таких выборок.</summary>
    public static (int Total, List<(int Samples, string Method)> Top) Under(Span span, string method, int top = 12)
    {
        var counts = new Dictionary<string, int>();
        var total  = 0;
        foreach (var frames in span.Samples)
        {
            var names = frames.Select(Short).ToArray();
            var at = Array.IndexOf(names, method);   // кадры от листа к корню: вызванные — левее
            if (at < 0) continue;
            total++;
            foreach (var m in names.Take(at).Where(n => !n.EndsWith("!?", StringComparison.Ordinal)).Distinct())
                counts[m] = counts.GetValueOrDefault(m) + 1;
        }
        return (total, counts.OrderByDescending(kv => kv.Value).Take(top).Select(kv => (kv.Value, kv.Key)).ToList());
    }

    /// <summary>Самые частые цепочки вызовов от <paramref name="method"/> до листа (кадры библиотек тоже,
    /// до <paramref name="depth"/> кадров от листа): чей именно код горячий внутри метода.</summary>
    public static List<(int Samples, string Chain)> Chains(Span span, string method, int top = 3, int depth = 14)
    {
        var counts = new Dictionary<string, int>();
        foreach (var frames in span.Samples)
        {
            var names = frames.Select(Short).ToArray();
            var at = Array.IndexOf(names, method);
            if (at <= 0) continue;
            var chain = string.Join(" ← ", names.Take(Math.Min(at, depth)));
            counts[chain] = counts.GetValueOrDefault(chain) + 1;
        }
        return counts.OrderByDescending(kv => kv.Value).Take(top).Select(kv => (kv.Value, kv.Key)).ToList();
    }

    /// <summary>Включающее время методов программы по всем окнам: сколько выборок прошло через метод.</summary>
    public static IEnumerable<(int Samples, string Method)> Inclusive(IEnumerable<Span> spans, int top = 25)
    {
        var counts = new Dictionary<string, int>();
        foreach (var span in spans)
            foreach (var frames in span.Samples)
                foreach (var m in frames.Where(IsApp).Select(Short).Distinct())
                    counts[m] = counts.GetValueOrDefault(m) + 1;
        return counts.OrderByDescending(kv => kv.Value).Take(top).Select(kv => (kv.Value, kv.Key));
    }
}
