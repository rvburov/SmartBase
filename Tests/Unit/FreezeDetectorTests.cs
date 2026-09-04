using SmartBase.Logging;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Чистая логика сторожа UI-потока (<see cref="FreezeDetector"/>): зависание обнаруживается,
/// когда отметка старше порога, отчет отдается один раз после оттаивания с длительностью и
/// операциями, снятыми в момент обнаружения; регулярные отметки отчетов не дают.
/// </summary>
public sealed class FreezeDetectorTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Regular_beats_produce_no_report()
    {
        var d = new FreezeDetector(S(3));
        for (var t = 0.0; t < 20; t += 0.5)
        {
            d.Beat(S(t));
            Assert.Null(d.Check(S(t + 0.4), () => "x"));
        }
    }

    [Fact]
    public void Freeze_is_reported_once_after_thaw_with_operations_at_detection()
    {
        var d = new FreezeDetector(S(3));
        d.Beat(S(10));

        var ops = "открытие коллекции";
        Assert.Null(d.Check(S(12), () => ops));      // еще в пределах порога
        Assert.Null(d.Check(S(14), () => ops));      // обнаружено — но отчет только после оттаивания
        ops = "другая";                              // позже операции сменились — в отчете останутся первые
        Assert.Null(d.Check(S(16), () => ops));

        d.Beat(S(17.5));                             // UI оттаял
        var report = d.Check(S(18), () => ops);
        Assert.NotNull(report);
        Assert.Equal(S(7.5), report!.Value.Duration);
        Assert.Equal("открытие коллекции", report.Value.Operations);

        Assert.Null(d.Check(S(19), () => ops));      // второго отчета о том же зависании нет
    }

    [Fact]
    public void Next_freeze_is_detected_after_recovery()
    {
        var d = new FreezeDetector(S(1));
        d.Beat(S(0));
        Assert.Null(d.Check(S(2), () => "a"));
        d.Beat(S(3));
        Assert.NotNull(d.Check(S(3.1), () => "a"));

        Assert.Null(d.Check(S(5), () => "b"));       // новое зависание обнаружено
        d.Beat(S(6));
        var second = d.Check(S(6.1), () => "b");
        Assert.NotNull(second);
        Assert.Equal(S(3), second!.Value.Duration);
        Assert.Equal("b", second.Value.Operations);
    }

    [Fact]
    public void Operations_are_read_only_at_detection()
    {
        var d = new FreezeDetector(S(1));
        d.Beat(S(0));
        var calls = 0;
        Assert.Null(d.Check(S(0.5), () => { calls++; return ""; }));
        Assert.Equal(0, calls);                      // порог не превышен — не читаем
        Assert.Null(d.Check(S(2), () => { calls++; return ""; }));
        Assert.Null(d.Check(S(3), () => { calls++; return ""; }));
        Assert.Equal(1, calls);                      // один раз — в момент обнаружения
    }
}
