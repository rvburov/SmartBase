using System.Diagnostics;
using System.Text.Json;
using SmartBase.Core.Models;
using SmartBase.Database;

namespace SmartBase.Tests.Benchmarks;

/// <summary>Замер на десяти миллионах синтетических путей. Создается настоящий локальный
/// SQLite-каталог, но не миллионы файлов: отдельно измеряется алгоритм, а не скорость диска
/// или сетевой папки. Обычный цикл продолжает работать, пока каталог/поиск идет в фоне.</summary>
public static class FileAutoBench
{
    public static int Run(string[] args, Action? uiPulse = null)
    {
        SQLitePCL.Batteries_V2.Init();
        var index = Array.IndexOf(args, "--files");
        var count = index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var parsed)
            ? parsed : 10_000_000;
        T Measure<T>(string operation, int files, Func<T> action) => MeasureWithPulse(operation, files, action, uiPulse);
        if (count <= 0)
        {
            Console.Error.WriteLine("Число путей --files должно быть положительным.");
            return 1;
        }
        var partial = !args.Contains("--exact", StringComparer.Ordinal);
        var rule = new FileAutoRule(FileAutoRule.TitleSource, "/synthetic", "pdf", PartialMatch: partial);
        Console.WriteLine($"Синтетический каталог: {count:N0} путей; подстрочный индекс: {partial}.");
        Console.WriteLine("Реальные файлы и сеть не измеряются. Создается только временный SQLite-каталог.");
        Console.WriteLine(uiPulse is null
            ? "Память относится ко всему процессу; heartbeat — контрольный цикл, не окно приложения."
            : "Память относится ко всему процессу; heartbeat включает dispatcher и ввод стенда интерфейса.");

        long reported = 0;
        using var catalog = Measure("build", count, () => FileAutoCatalog.Build(rule, Names(count), scanned: n =>
        {
            if (n - reported < 1_000_000) return;
            reported = n;
            Console.WriteLine($"  перечислено {n:N0} путей");
        }));
        if (catalog.ScannedCount != count || catalog.MatchedFileCount != count)
            throw new InvalidOperationException("Каталог потерял синтетические пути.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            catalog.ScannedCount, catalog.MatchedFileCount, catalog.HasSubstringIndex,
            catalogBytes = new FileInfo(catalog.StoragePath).Length,
        }));

        var middle = count / 2;
        var expected = SyntheticPath(middle);
        var exact = rule with { PartialMatch = false };
        var exactResult = Measure("exact", count, () => catalog.Match(exact, $"Документ_{middle:D10}"));
        if (exactResult != expected) throw new InvalidOperationException("Точное совпадение потеряно.");

        if (partial)
        {
            var found = Measure("substring-selective", count, () => catalog.Match(rule, middle.ToString("D10")));
            if (found != expected) throw new InvalidOperationException("Выборочная подстрока потеряна.");
            var missing = Measure("substring-short-full-scan", count, () => catalog.Match(rule, "#"));
            if (missing != "") throw new InvalidOperationException("Короткая подстрока дала ложное совпадение.");
            var limited = Measure("substring-wide-limit", count, () =>
            {
                try
                {
                    var result = catalog.Match(rule, "Документ");
                    if (count > FileAutoCatalog.MaxMatches)
                        throw new InvalidOperationException("Широкий поиск не остановлен пределом результата.");
                    return FileValue.ReadPaths(result).Count;
                }
                catch (FileAutoMatchLimitException) { return -1; }
            });
            Console.WriteLine($"Широкий поиск: {(limited < 0 ? "отказ без усеченного значения" : $"{limited} совпадений")}.");

            using var cancellation = new CancellationTokenSource();
            var cancelled = Measure("cancel-short-full-scan", count, () =>
            {
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(30));
                try { catalog.Match(rule, "#", cancellation.Token); return false; }
                catch (OperationCanceledException) { return true; }
            });
            Console.WriteLine(cancelled ? "Отмена длинного запроса подтверждена." : "Запрос успел завершиться до отмены.");
            if (!cancelled && count >= 1_000_000)
                throw new InvalidOperationException("Крупный проход закончился до проверки отмены; результат замера требует разбора.");
            if (catalog.Match(exact, $"Документ_{middle:D10}") != expected)
                throw new InvalidOperationException("После отмены каталог не отвечает.");
        }
        return 0;
    }

    private static IEnumerable<string> Names(int count)
    {
        for (var i = 0; i < count; i++) yield return SyntheticPath(i);
    }

    private static string SyntheticPath(int index) => $"/synthetic/{index % 1000:D3}/Документ_{index:D10}.pdf";

    private static T MeasureWithPulse<T>(string operation, int files, Func<T> action, Action? uiPulse)
    {
        using var process = Process.GetCurrentProcess();
        var clock = Stopwatch.StartNew();
        var previous = clock.Elapsed.TotalMilliseconds;
        double longestHeartbeat = 0;
        long workingSet = 0, privateBytes = 0, managedBytes = 0, heartbeats = 0;
        var work = Task.Run(action);
        while (!work.IsCompleted)
        {
            process.Refresh();
            workingSet = Math.Max(workingSet, process.WorkingSet64);
            privateBytes = Math.Max(privateBytes, process.PrivateMemorySize64);
            managedBytes = Math.Max(managedBytes, GC.GetTotalMemory(forceFullCollection: false));
            uiPulse?.Invoke();
            Thread.Sleep(20);
            var current = clock.Elapsed.TotalMilliseconds;
            longestHeartbeat = Math.Max(longestHeartbeat, current - previous);
            previous = current;
            heartbeats++;
        }
        var result = work.GetAwaiter().GetResult();
        uiPulse?.Invoke();
        process.Refresh();
        workingSet = Math.Max(workingSet, process.WorkingSet64);
        privateBytes = Math.Max(privateBytes, process.PrivateMemorySize64);
        managedBytes = Math.Max(managedBytes, GC.GetTotalMemory(forceFullCollection: false));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            operation, files, elapsedMs = clock.Elapsed.TotalMilliseconds,
            workingSetPeakBytes = workingSet, privatePeakBytes = privateBytes, managedPeakBytes = managedBytes,
            heartbeats, longestHeartbeatMs = longestHeartbeat,
        }));
        return result;
    }
}
