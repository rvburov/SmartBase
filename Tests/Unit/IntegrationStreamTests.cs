using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Карточка потока событий (<see cref="IntegrationStream"/>) и триггер «по событию потока»
/// (<see cref="ScenarioTrigger"/>): форма и ее проверка.
///
/// Главное здесь — путь. Он обязан быть относительным по той же причине, что у запроса: хост
/// проверяет политика, и позволить в карточке потока полный адрес значило бы показывать одно
/// соединение, а слушать другое.
/// </summary>
public sealed class IntegrationStreamTests
{
    private static IntegrationStream Sample =>
        IntegrationStream.New() with { Name = "События", ConnectionId = 1, Path = "/events" };

    private static readonly IntegrationConnection Connection =
        IntegrationConnection.New() with { Name = "Учетная система", Address = "http://сервер:8080/обмен" };

    // ── Карточка ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_filled_card_has_no_problems() => Assert.Empty(Sample.Problems());

    [Fact]
    public void A_card_without_a_name_or_connection_is_refused()
    {
        var problems = IntegrationStream.New().Problems();

        Assert.Contains(problems, p => p.Contains("Название", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("соединение", StringComparison.Ordinal));
    }

    /// <summary>Полный адрес, чужой хост и выход выше адреса соединения — все это отказ.</summary>
    [Theory]
    [InlineData("ws://чужой/events")]
    [InlineData("//чужой/events")]
    [InlineData("/../events")]
    [InlineData("events")]
    [InlineData("")]
    public void The_path_must_stay_inside_the_connection(string path) =>
        Assert.NotEmpty((Sample with { Path = path }).Problems());

    /// <summary>Подписка без тем не подписана ни на что — и событий не дождется никогда.</summary>
    [Fact]
    public void A_subscription_needs_a_topic()
    {
        var stream = Sample with { Protocol = StreamProtocol.Stomp, Topics = [" "] };

        Assert.Contains(stream.Problems(), p => p.Contains("тема", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Перевод строки в теме превратил бы ее хвост в следующий заголовок кадра.</summary>
    [Fact]
    public void A_topic_is_a_single_line()
    {
        var stream = Sample with { Protocol = StreamProtocol.Stomp, Topics = ["/topic/a\nack:client"] };

        Assert.Contains(stream.Problems(), p => p.Contains("одна строка", StringComparison.Ordinal));
    }

    /// <summary>У простого потока темы не спрашиваются вовсе.</summary>
    [Fact]
    public void A_plain_stream_needs_no_topics() =>
        Assert.Empty((Sample with { Protocol = StreamProtocol.Plain, Topics = [] }).Problems());

    /// <summary>Адрес потока — адрес соединения плюс путь, как у запроса.</summary>
    [Fact]
    public void The_address_is_built_from_the_connection()
    {
        var address = Sample.BuildAddress(Connection);

        Assert.NotNull(address);
        Assert.EndsWith("/events", address!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(8080, address.Port);
    }

    // ── Триггер ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("stream 3 0")]
    [InlineData("stream 3 7")]
    [InlineData("stream 3 7 Сервер не работает")]
    public void The_trigger_survives_a_round_trip(string stored) =>
        Assert.Equal(stored, ScenarioTrigger.Parse(stored).ToString());

    /// <summary>Текст ответа занимает хвост строки целиком — с пробелами, как его ввел человек.</summary>
    [Fact]
    public void The_fail_text_keeps_its_spaces()
    {
        var trigger = ScenarioTrigger.Parse("stream 3 7 Сервер не работает, обратитесь позже");

        Assert.Equal(ScenarioTriggerKind.Stream, trigger.Kind);
        Assert.Equal(3, trigger.StreamId);
        Assert.Equal(7, trigger.RequestId);
        Assert.Equal("Сервер не работает, обратитесь позже", trigger.FailText);
        Assert.True(trigger.Replies);
    }

    /// <summary>Мусор читается как «вручную» — самое безобидное из прочтений.</summary>
    [Theory]
    [InlineData("stream")]
    [InlineData("stream 0 0")]
    [InlineData("stream x 1")]
    [InlineData("stream 3 -1")]
    public void A_garbled_stream_trigger_is_manual(string stored) =>
        Assert.Equal(ScenarioTriggerKind.Manual, ScenarioTrigger.Parse(stored).Kind);

    /// <summary>Перевод строки в тексте развалил бы строку хранения — он сворачивается в пробел.</summary>
    [Fact]
    public void A_line_break_in_the_fail_text_is_folded()
    {
        var trigger = ScenarioTrigger.OnStream(3, 7, "Сервер\nне работает");

        Assert.Equal("Сервер не работает", trigger.FailText);
        Assert.Equal(trigger, ScenarioTrigger.Parse(trigger.ToString()));
    }

    [Fact]
    public void A_stream_trigger_needs_a_stream() =>
        Assert.Contains(ScenarioTrigger.OnStream(0).Problems(),
                        p => p.Contains("поток", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Текст ответа без запроса ответа не уйдет никуда, а человек считал бы, что ответ настроен.
    /// </summary>
    [Fact]
    public void A_fail_text_without_a_reply_request_is_refused()
    {
        var trigger = new ScenarioTrigger(ScenarioTriggerKind.Stream, 0, default,
                                          StreamId: 3, FailText: "Сервер не работает");

        Assert.Contains(trigger.Problems(), p => p.Contains("запрос ответа", StringComparison.Ordinal));
    }

    [Fact]
    public void A_long_fail_text_is_refused()
    {
        var trigger = ScenarioTrigger.OnStream(
            3, 7, new string('я', ScenarioTrigger.MaxFailTextLength + 1));

        Assert.Contains(trigger.Problems(), p => p.Contains("длиннее", StringComparison.Ordinal));
    }

    /// <summary>Сценарий потока ходит сам, но не по часам: расписание о нем не знает.</summary>
    [Fact]
    public void A_stream_scenario_is_automatic_but_never_due()
    {
        var trigger = ScenarioTrigger.OnStream(3);
        var now     = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

        var scenario = Scenario.New() with
        {
            Name = "По событию", Enabled = true, Trigger = trigger,
            Steps = [ScenarioStep.OfNotice("Пришло")],
        };

        Assert.True(scenario.Automatic);
        Assert.Empty(scenario.Problems());
        Assert.Null(trigger.NextRun(now));
        Assert.False(trigger.IsDue(now.AddDays(-1), now));
    }

    [Fact]
    public void The_trigger_describes_itself_with_the_stream_name()
    {
        Assert.Equal("по событию потока «События»", ScenarioTrigger.OnStream(3).Describe("События"));
        Assert.Equal("по событию потока «События», с ответом",
                     ScenarioTrigger.OnStream(3, 7).Describe("События"));
    }
}
