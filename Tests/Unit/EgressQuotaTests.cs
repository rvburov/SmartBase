using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Накопительные потолки политики (<see cref="EgressGate.CheckQuota"/> и
/// <see cref="EgressGate.BytesLeft"/>): запусков в час и байт в сутки.
///
/// Потолки эти отличаются от остальных проверок шлюза тем, что зависят от УЖЕ израсходованного,
/// а расход считает журнал. Поэтому счетчик подключается отдельно (<see cref="IEgressUsage"/>),
/// и здесь он поддельный: решения шлюза обязаны проверяться без единого файла.
/// </summary>
public sealed class EgressQuotaTests : IDisposable
{
    public void Dispose() => EgressGate.Close();

    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Счетчик, который просто отдает заданные числа и запоминает, о чем спрашивали.</summary>
    private sealed class Spent(int runs, long bytes) : IEgressUsage
    {
        public DateTimeOffset? RunsFrom  { get; private set; }
        public DateTimeOffset? BytesFrom { get; private set; }

        public int RunsSince(DateTimeOffset moment)
        {
            RunsFrom = moment;
            return runs;
        }

        public long BytesSince(DateTimeOffset moment)
        {
            BytesFrom = moment;
            return bytes;
        }
    }

    private static void Attach(int runsPerHour = 10, long bytesPerDay = 1000) =>
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.External, ["api.zavod.local"], ["10.0.0.0/8"], [],
            MaxRowsPerRun:   1000,
            MaxBytesPerDay:  bytesPerDay,
            MaxRunsPerHour:  runsPerHour,
            ValidUntil:      null,
            UserMayAddHosts: false,
            Source:          "тест")));

    // ── Без счетчика ──────────────────────────────────────────────────────────

    /// <summary>
    /// Счетчик не подключен — расход нулевой. Это не дыра: без журнала нет и обмена, который в
    /// него пишет, а запирать шлюз из-за отсутствия счетчика значило бы ломать первый запуск на
    /// пустой машине.
    /// </summary>
    [Fact]
    public void Without_a_counter_nothing_is_spent()
    {
        Attach();

        Assert.True(EgressGate.CheckQuota(Now).Allowed);
        Assert.Equal(long.MaxValue, EgressGate.BytesLeft(Now));
    }

    /// <summary>Закрытый шлюз отвечает своей причиной, а не «квота выбрана».</summary>
    [Fact]
    public void Closed_gate_answers_before_the_quota()
    {
        EgressGate.Close();

        var decision = EgressGate.CheckQuota(Now);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.NoPolicy, decision.Reason);
        Assert.Equal(0, EgressGate.BytesLeft(Now));
    }

    // ── Запуски в час ─────────────────────────────────────────────────────────

    [Fact]
    public void Runs_below_the_ceiling_are_allowed()
    {
        Attach(runsPerHour: 10);
        EgressGate.AttachUsage(new Spent(runs: 9, bytes: 0));

        Assert.True(EgressGate.CheckQuota(Now).Allowed);
    }

    [Fact]
    public void Runs_at_the_ceiling_are_refused()
    {
        Attach(runsPerHour: 10);
        EgressGate.AttachUsage(new Spent(runs: 10, bytes: 0));

        var decision = EgressGate.CheckQuota(Now);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.TooManyRuns, decision.Reason);
        Assert.Contains("10", decision.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Окно скользящее, а не календарное. Календарное обнуляется в ноль минут, и тысяча
    /// запросов, поставленная на 59-ю и 61-ю минуту, прошла бы дважды за две минуты.
    /// </summary>
    [Fact]
    public void Hour_window_slides_from_now()
    {
        Attach();
        var spent = new Spent(runs: 0, bytes: 0);
        EgressGate.AttachUsage(spent);

        EgressGate.CheckQuota(Now);

        Assert.Equal(Now - TimeSpan.FromHours(1), spent.RunsFrom);
    }

    // ── Байты в сутки ─────────────────────────────────────────────────────────

    [Fact]
    public void Bytes_over_the_daily_ceiling_are_refused()
    {
        Attach(bytesPerDay: 1000);
        EgressGate.AttachUsage(new Spent(runs: 0, bytes: 1001));

        var decision = EgressGate.CheckQuota(Now);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.TooManyBytes, decision.Reason);
    }

    /// <summary>Заявленный объем прибавляется к израсходованному: потолок спрашивают ДО обмена.</summary>
    [Fact]
    public void Announced_size_is_counted_in_advance()
    {
        Attach(bytesPerDay: 1000);
        EgressGate.AttachUsage(new Spent(runs: 0, bytes: 900));

        Assert.True(EgressGate.CheckQuota(Now, bytes: 100).Allowed);
        Assert.False(EgressGate.CheckQuota(Now, bytes: 101).Allowed);
    }

    [Fact]
    public void Day_window_slides_from_now()
    {
        Attach();
        var spent = new Spent(runs: 0, bytes: 0);
        EgressGate.AttachUsage(spent);

        EgressGate.BytesLeft(Now);

        Assert.Equal(Now - TimeSpan.FromDays(1), spent.BytesFrom);
    }

    [Fact]
    public void Remaining_budget_is_what_is_left_of_the_day()
    {
        Attach(bytesPerDay: 1000);
        EgressGate.AttachUsage(new Spent(runs: 0, bytes: 400));

        Assert.Equal(600, EgressGate.BytesLeft(Now));
    }

    /// <summary>Перерасход дает ноль, а не отрицательное число: остаток не бывает отрицательным.</summary>
    [Fact]
    public void Overspent_budget_is_zero_not_negative()
    {
        Attach(bytesPerDay: 1000);
        EgressGate.AttachUsage(new Spent(runs: 0, bytes: 5000));

        Assert.Equal(0, EgressGate.BytesLeft(Now));
    }

    /// <summary>Отключение счетчика вместе со шлюзом: иначе он пережил бы смену политики.</summary>
    [Fact]
    public void Closing_the_gate_drops_the_counter()
    {
        Attach();
        EgressGate.AttachUsage(new Spent(runs: 1000, bytes: 1000));

        EgressGate.Close();
        Attach();

        Assert.True(EgressGate.CheckQuota(Now).Allowed);
    }
}
