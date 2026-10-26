using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace SmartBase.Tests.Extensions.Shared;

/// <summary>
/// Небольшой общий старт независимых обработок. Фикстуры создаются до потоков, уборка идет
/// только после Join всех участников. Тысяча заданий/пользователей этим помощником не моделируется.
/// </summary>
public static class ConcurrentTasks
{
    public static string Fingerprint(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    public static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static TResult[] Run<TFixture, TResult>(int count, Func<int, TFixture> prepare,
        Func<TFixture, int, CancellationToken, TResult> work, TimeSpan? timeout = null,
        Action<TResult[]>? verify = null, IDisposable? shared = null)
        where TFixture : IDisposable
    {
        if (count is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(count));
        var limit = timeout ?? TimeSpan.FromMinutes(2);
        if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var fixtures = new List<TFixture>(count);
        var started = new List<Thread>(count);
        var results = new TResult[count];
        var completed = new bool[count];
        var errors = new ConcurrentQueue<Exception>();
        var ready = new CountdownEvent(count);
        var gate = new ManualResetEventSlim(false);
        var stop = new CancellationTokenSource();
        var joined = true;
        try
        {
            for (var i = 0; i < count; i++) fixtures.Add(prepare(i));
            Exception? controllerError = null;
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var index = i;
                    var thread = new Thread(() =>
                    {
                        ready.Signal();
                        try
                        {
                            gate.Wait(stop.Token);
                            stop.Token.ThrowIfCancellationRequested();
                            results[index] = work(fixtures[index], index, stop.Token);
                            completed[index] = true;
                        }
                        catch (Exception ex)
                        {
                            errors.Enqueue(new InvalidOperationException($"Параллельное задание {index}", ex));
                        }
                    }) { IsBackground = true, Name = $"extension-job-{index}" };
                    thread.Start();
                    started.Add(thread);
                }
                if (!ready.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("Участники не подготовились к общему старту");
            }
            catch (Exception ex)
            {
                controllerError = ex;
                stop.Cancel();
            }
            finally
            {
                gate.Set();
                var elapsed = Stopwatch.StartNew();
                foreach (var thread in started)
                {
                    var remaining = limit - elapsed.Elapsed;
                    joined &= thread.Join(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                }
                if (!joined)
                {
                    stop.Cancel();
                    var grace = Stopwatch.StartNew();
                    joined = true;
                    foreach (var thread in started)
                    {
                        var remaining = TimeSpan.FromSeconds(30) - grace.Elapsed;
                        joined &= thread.Join(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                    }
                    controllerError ??= new TimeoutException("Параллельные обработки превысили лимит времени");
                }
            }
            if (!joined)
                throw new TimeoutException("Обработки не остановились после отмены; фикстуры оставлены живым потокам");
            if (controllerError is not null) throw controllerError;
            try { verify?.Invoke(results); }
            catch (Exception ex) { errors.Enqueue(new InvalidOperationException("Итоговая проверка общей фикстуры", ex)); }
            if (!errors.IsEmpty) throw new AggregateException(errors);
            if (!completed.All(x => x)) throw new InvalidOperationException("Завершились не все запрошенные обработки");
            return results;
        }
        finally
        {
            if (joined)
            {
                foreach (var fixture in fixtures) fixture.Dispose();
                shared?.Dispose();
                ready.Dispose();
                gate.Dispose();
                stop.Dispose();
            }
        }
    }
}
