using Serilog;
using Serilog.Core;
using Serilog.Events;
using SmartBase.Logging;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Обвязка диагностики (<see cref="Diag"/>): замер операции пишет в журнал только при
/// превышении порога и с контекстом, реестр активных операций отражает вложенность,
/// разовые и редкие записи (<see cref="Diag.Once"/>, <see cref="Diag.Throttle"/>) режут шум,
/// форматирование размеров и длительностей — человеческое.
/// </summary>
[Collection("Serilog")]
public sealed class DiagTests : IDisposable
{
    private readonly List<LogEvent> _events = [];

    public DiagTests()
    {
        Diag.ResetForTests();
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(new ListSink(_events)).CreateLogger();
    }

    public void Dispose()
    {
        Log.Logger = Logger.None;
        Diag.ResetForTests();
    }

    private string Messages() => string.Join("\n", _events.Select(e => e.RenderMessage()));

    [Fact]
    public void Slow_operation_is_logged_with_context_and_threshold()
    {
        using (var scope = Diag.Measure("открытие коллекции", TimeSpan.Zero, "база test, коллекция 5"))
        {
            scope.Context += ", отобрано 12";
            Thread.Sleep(5);
        }

        var e = Assert.Single(_events);
        Assert.Equal(LogEventLevel.Warning, e.Level);
        var msg = e.RenderMessage();
        Assert.Contains("Медленно: открытие коллекции", msg);
        Assert.Contains("база test, коллекция 5, отобрано 12", msg);
        Assert.Contains("при пороге 0 мс", msg);
    }

    [Fact]
    public void Fast_operation_is_silent()
    {
        using (Diag.Measure("быстрая операция", TimeSpan.FromSeconds(30))) { }
        Assert.Empty(_events);
    }

    [Fact]
    public void Active_operations_reflect_nesting_and_release()
    {
        Assert.Equal("", Diag.ActiveOperations);
        using (Diag.Measure("внешняя", TimeSpan.FromMinutes(1)))
        {
            using (Diag.Measure("внутренняя", TimeSpan.FromMinutes(1)))
            using (Diag.Measure("внутренняя", TimeSpan.FromMinutes(1)))
                Assert.Equal("внешняя, внутренняя", Diag.ActiveOperations);
            Assert.Equal("внешняя", Diag.ActiveOperations);
        }
        Assert.Equal("", Diag.ActiveOperations);
    }

    [Fact]
    public void Dispose_twice_releases_once()
    {
        var scope = Diag.Measure("операция", TimeSpan.FromMinutes(1));
        scope.Dispose();
        scope.Dispose();
        Assert.Equal("", Diag.ActiveOperations);
        Assert.Empty(_events);
    }

    [Fact]
    public void Once_fires_a_single_time_per_key()
    {
        Assert.True(Diag.Once("k1"));
        Assert.False(Diag.Once("k1"));
        Assert.True(Diag.Once("k2"));
    }

    [Fact]
    public void Throttle_allows_first_then_blocks_until_period_passes()
    {
        var period = TimeSpan.FromMilliseconds(60);
        Assert.True(Diag.Throttle("t", period));
        Assert.False(Diag.Throttle("t", period));
        Thread.Sleep(100);
        Assert.True(Diag.Throttle("t", period));
        Assert.True(Diag.Throttle("other", period));   // ключи независимы
    }

    [Theory]
    [InlineData(500, "500 Б")]
    [InlineData(2048, "2 КБ")]
    public void Size_is_human_readable(long bytes, string expected) =>
        Assert.Equal(expected, Diag.Size(bytes));

    [Fact]
    public void Size_scales_to_megabytes_and_gigabytes()
    {
        Assert.EndsWith("МБ", Diag.Size(5L * 1024 * 1024));
        Assert.EndsWith("ГБ", Diag.Size(3L * 1024 * 1024 * 1024));
    }

    [Theory]
    [InlineData(30, "30 с")]
    [InlineData(5 * 60, "5 мин")]
    [InlineData(2 * 3600 + 15 * 60, "2 ч 15 мин")]
    [InlineData(3 * 3600, "3 ч")]
    public void Duration_is_human_readable(int seconds, string expected) =>
        Assert.Equal(expected, Diag.Duration(TimeSpan.FromSeconds(seconds)));

    private sealed class ListSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) { lock (events) events.Add(logEvent); }
    }
}
