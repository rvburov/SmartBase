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

    /// <summary>Направление, о котором спрашивают в большинстве проверок, — ключ карточки запроса.</summary>
    private const int Direction = 7;

    /// <summary>
    /// Счетчик, который отдает заданные числа и запоминает, о чем спрашивали. Байты — по
    /// направлениям: <c>byDirection</c> задает расход поименно, <c>bytes</c> — для всех прочих.
    /// </summary>
    private sealed class Spent(int runs, long bytes,
                               Dictionary<int, long>? byDirection = null) : IEgressUsage
    {
        public DateTimeOffset? RunsFrom  { get; private set; }
        public DateTimeOffset? BytesFrom { get; private set; }
        public int?            AskedFor  { get; private set; }

        public int RunsSince(DateTimeOffset moment)
        {
            RunsFrom = moment;
            return runs;
        }

        public long BytesSince(DateTimeOffset moment, int direction)
        {
            BytesFrom = moment;
            AskedFor  = direction;
            return byDirection is not null && byDirection.TryGetValue(direction, out var own)
                ? own
                : bytes;
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

        Assert.True(EgressGate.CheckQuota(Now, Direction).Allowed);
        Assert.Equal(long.MaxValue, EgressGate.BytesLeft(Now, Direction));
    }

    /// <summary>Закрытый шлюз отвечает своей причиной, а не «квота выбрана».</summary>
    [Fact]
    public void Closed_gate_answers_before_the_quota()
    {
        EgressGate.Close();

        var decision = EgressGate.CheckQuota(Now, Direction);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.NoPolicy, decision.Reason);
        Assert.Equal(0, EgressGate.BytesLeft(Now, Direction));
    }

    // ── Запуски в час ─────────────────────────────────────────────────────────

    [Fact]
    public void Runs_below_the_ceiling_are_allowed()
    {
        Attach(runsPerHour: 10);
        EgressGate.AttachUsage(new Spent(runs: 9, bytes: 0));

        Assert.True(EgressGate.CheckQuota(Now, Direction).Allowed);
    }

    [Fact]
    public void Runs_at_the_ceiling_are_refused()
    {
        Attach(runsPerHour: 10);
        EgressGate.AttachUsage(new Spent(runs: 10, bytes: 0));

        var decision = EgressGate.CheckQuota(Now, Direction);

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

        EgressGate.CheckQuota(Now, Direction);

        Assert.Equal(Now - TimeSpan.FromHours(1), spent.RunsFrom);
    }

    // ── Байты в сутки ─────────────────────────────────────────────────────────

    [Fact]
    public void Bytes_over_the_daily_ceiling_are_refused()
    {
        Attach(bytesPerDay: 1000);
        EgressGate.AttachUsage(new Spent(runs: 0, bytes: 1001));

        var decision = EgressGate.CheckQuota(Now, Direction);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.TooManyBytes, decision.Reason);
    }

    /// <summary>Заявленный объем прибавляется к израсходованному: потолок спрашивают ДО обмена.</summary>
    [Fact]
    public void Announced_size_is_counted_in_advance()
    {
        Attach(bytesPerDay: 1000);
        EgressGate.AttachUsage(new Spent(runs: 0, bytes: 900));

        Assert.True(EgressGate.CheckQuota(Now, Direction, bytes: 100).Allowed);
        Assert.False(EgressGate.CheckQuota(Now, Direction, bytes: 101).Allowed);
    }

    // ── Потолок объема — на направление ───────────────────────────────────────

    /// <summary>
    /// Суточный потолок объема назначен НАПРАВЛЕНИЮ, а не обмену целиком (10.6): подтверждают и
    /// отзывают карточку запроса, ей же принадлежит и счет. Общая сумма означала бы, что один
    /// шумный обмен останавливает все прочие, настроенные правильно, — а заодно что читающий
    /// опрос каждые пять минут выбирает потолок, заведенный против выноса данных.
    /// </summary>
    [Fact]
    public void A_direction_that_spent_its_day_does_not_stop_the_others()
    {
        Attach(bytesPerDay: 1000);
        EgressGate.AttachUsage(new Spent(runs: 0, bytes: 0,
            byDirection: new Dictionary<int, long> { [7] = 1000, [8] = 10 }));

        Assert.False(EgressGate.CheckQuota(Now, direction: 7).Allowed);
        Assert.True(EgressGate.CheckQuota(Now, direction: 8).Allowed);
    }

    /// <summary>Счетчик спрашивают именно о том направлении, о котором спросили шлюз.</summary>
    [Fact]
    public void The_counter_is_asked_about_the_direction_in_question()
    {
        Attach();
        var spent = new Spent(runs: 0, bytes: 0);
        EgressGate.AttachUsage(spent);

        EgressGate.CheckQuota(Now, direction: 42);

        Assert.Equal(42, spent.AskedFor);
    }

    /// <summary>Остаток на сутки — тоже свой у каждого направления: по нему идет выгрузка порциями.</summary>
    [Fact]
    public void Remaining_budget_is_counted_per_direction()
    {
        Attach(bytesPerDay: 1000);
        EgressGate.AttachUsage(new Spent(runs: 0, bytes: 0,
            byDirection: new Dictionary<int, long> { [7] = 900, [8] = 100 }));

        Assert.Equal(100, EgressGate.BytesLeft(Now, direction: 7));
        Assert.Equal(900, EgressGate.BytesLeft(Now, direction: 8));
    }

    /// <summary>
    /// А вот потолок запусков в час — общий, и это не недосмотр: он про то, как часто программа
    /// вообще стучится наружу. Раздай его по направлениям — и десять карточек дали бы десятикратную
    /// частоту обращений к одной и той же чужой программе.
    /// </summary>
    [Fact]
    public void The_hourly_run_ceiling_stays_common_to_the_whole_exchange()
    {
        Attach(runsPerHour: 10);
        EgressGate.AttachUsage(new Spent(runs: 10, bytes: 0));

        Assert.Equal(EgressDenyReason.TooManyRuns, EgressGate.CheckQuota(Now, direction: 7).Reason);
        Assert.Equal(EgressDenyReason.TooManyRuns, EgressGate.CheckQuota(Now, direction: 8).Reason);
    }

    [Fact]
    public void Day_window_slides_from_now()
    {
        Attach();
        var spent = new Spent(runs: 0, bytes: 0);
        EgressGate.AttachUsage(spent);

        EgressGate.BytesLeft(Now, Direction);

        Assert.Equal(Now - TimeSpan.FromDays(1), spent.BytesFrom);
    }

    [Fact]
    public void Remaining_budget_is_what_is_left_of_the_day()
    {
        Attach(bytesPerDay: 1000);
        EgressGate.AttachUsage(new Spent(runs: 0, bytes: 400));

        Assert.Equal(600, EgressGate.BytesLeft(Now, Direction));
    }

    /// <summary>Перерасход дает ноль, а не отрицательное число: остаток не бывает отрицательным.</summary>
    [Fact]
    public void Overspent_budget_is_zero_not_negative()
    {
        Attach(bytesPerDay: 1000);
        EgressGate.AttachUsage(new Spent(runs: 0, bytes: 5000));

        Assert.Equal(0, EgressGate.BytesLeft(Now, Direction));
    }

    /// <summary>Отключение счетчика вместе со шлюзом: иначе он пережил бы смену политики.</summary>
    [Fact]
    public void Closing_the_gate_drops_the_counter()
    {
        Attach();
        EgressGate.AttachUsage(new Spent(runs: 1000, bytes: 1000));

        EgressGate.Close();
        Attach();

        Assert.True(EgressGate.CheckQuota(Now, Direction).Allowed);
    }
}
