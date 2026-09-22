using SmartBase.Views.Library;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Ворота переписи кеша (<c>Views/Library/DocRunGate.cs</c>): не больше одного прогона разом,
/// а просьбы во время прогона сливаются в ОДИН повтор после него.
///
/// Каждое касание галочки в сайдбаре перезапускает ленту раздела, а с ней и перепись, которая
/// идет на диск за каждым файлом. Без ворот пять касаний подряд давали пять обходов диска разом
/// — на сетевой папке это пять очередей по двадцать тысяч вопросов к серверу, из которых нужна
/// была только последняя.
/// </summary>
public sealed class DocRunGateTests
{
    [Fact]
    public void The_first_request_starts_at_once()
    {
        var gate = new DocRunGate();

        Assert.True(gate.Request());
        Assert.True(gate.Running);
        Assert.False(gate.Stale);
    }

    /// <summary>
    /// Просьба во время прогона не начинает второй, а делает итог идущего устаревшим: он про
    /// прежний набор документов, и показывать его нельзя.
    /// </summary>
    [Fact]
    public void A_request_during_a_run_waits_and_makes_the_run_stale()
    {
        var gate = new DocRunGate();
        gate.Request();

        Assert.False(gate.Request());
        Assert.True(gate.Stale);
    }

    [Fact]
    public void Five_requests_during_a_run_give_one_repeat()
    {
        var gate = new DocRunGate();
        gate.Request();
        for (var i = 0; i < 5; i++) gate.Request();

        Assert.True(gate.Done());       // один повтор…
        Assert.False(gate.Stale);       // …и его итог свежий
        Assert.False(gate.Done());      // больше ничего
        Assert.False(gate.Running);
    }

    [Fact]
    public void A_run_nobody_interrupted_frees_the_gate()
    {
        var gate = new DocRunGate();
        gate.Request();

        Assert.False(gate.Done());
        Assert.False(gate.Running);
        Assert.True(gate.Request());    // следующая просьба снова начинает сразу
    }

    /// <summary>
    /// Повтор держит ворота занятыми: просьба, пришедшая уже во время повтора, снова ждет, а
    /// не запускает третий обход рядом со вторым.
    /// </summary>
    [Fact]
    public void The_repeat_keeps_the_gate_busy()
    {
        var gate = new DocRunGate();
        gate.Request();
        gate.Request();

        Assert.True(gate.Done());
        Assert.True(gate.Running);
        Assert.False(gate.Request());
        Assert.True(gate.Stale);
    }
}
