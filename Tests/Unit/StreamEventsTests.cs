using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// То, что ходит по потоку событий (<see cref="StreamEvents"/>): кадры подписки и разбор
/// пришедшего в строки.
///
/// Главная проверка — событие становится строками по тем же правилам, что ответ запроса, и поля
/// конверта доезжают до каждой записи: без них отбор «вид события равно …» строить не на чем.
/// Вторая — чужое событие не роняет разбор: по одному потоку ходят события разной формы, и
/// событие без записей — не ошибка.
///
/// События в тестах выдуманные: о настоящих сторонних программах разбор не знает ничего, и
/// проверять его на их форме значило бы привязать тест к чужой документации.
/// </summary>
public sealed class StreamEventsTests
{
    private static IntegrationStream Stomp(params string[] topics) =>
        IntegrationStream.New() with
        {
            Name = "События", ConnectionId = 1, Path = "/events",
            Protocol = StreamProtocol.Stomp, Topics = topics,
        };

    private static IntegrationStream Plain =>
        IntegrationStream.New() with { Name = "События", ConnectionId = 1, Path = "/events" };

    // ── Подписка ──────────────────────────────────────────────────────────────

    /// <summary>У простого потока приветствия нет: события идут сами.</summary>
    [Fact]
    public void A_plain_stream_sends_nothing_on_connect() =>
        Assert.Empty(StreamEvents.Greeting(Plain, "сервер"));

    /// <summary>
    /// У подписки — представиться и по кадру на тему. Кадр кончается нулевым знаком: без него та
    /// сторона ждала бы продолжения и не подписала бы нас ни на что.
    /// </summary>
    [Fact]
    public void A_subscription_greets_and_subscribes_to_every_topic()
    {
        var frames = StreamEvents.Greeting(Stomp("/topic/orders", "/topic/stock"), "сервер");

        Assert.Equal(3, frames.Count);

        Assert.StartsWith("CONNECT\n", frames[0], StringComparison.Ordinal);
        Assert.Contains("host:сервер\n", frames[0], StringComparison.Ordinal);

        Assert.StartsWith("SUBSCRIBE\n", frames[1], StringComparison.Ordinal);
        Assert.Contains("destination:/topic/orders\n", frames[1], StringComparison.Ordinal);
        Assert.Contains("destination:/topic/stock\n",  frames[2], StringComparison.Ordinal);

        // У каждой подписки свой номер: с одинаковыми та сторона оставила бы последнюю.
        Assert.Contains("id:sub-0\n", frames[1], StringComparison.Ordinal);
        Assert.Contains("id:sub-1\n", frames[2], StringComparison.Ordinal);

        Assert.All(frames, frame => Assert.EndsWith("\n\n\0", frame, StringComparison.Ordinal));
    }

    /// <summary>Пустые строки и повторы в списке тем подпиской не становятся.</summary>
    [Fact]
    public void Blank_and_repeated_topics_are_not_subscribed()
    {
        var frames = StreamEvents.Greeting(Stomp("/topic/orders", " ", "/topic/orders"), "сервер");

        Assert.Equal(2, frames.Count);   // представиться и одна подписка
    }

    // ── Кадры ─────────────────────────────────────────────────────────────────

    /// <summary>У простого потока сообщение и есть событие.</summary>
    [Fact]
    public void A_plain_message_is_the_event_itself()
    {
        var bodies = StreamEvents.Bodies(Plain, """{"id":1}""", out var trouble);

        Assert.Equal("", trouble);
        Assert.Equal(["""{"id":1}"""], bodies);
    }

    /// <summary>
    /// У подписки событие — тело кадра <c>MESSAGE</c>. Кадр подтверждения соединения событием
    /// не является, а кадров в одном сообщении бывает несколько.
    /// </summary>
    [Fact]
    public void Only_message_frames_carry_events()
    {
        var text = "CONNECTED\nversion:1.2\n\n\0"
                 + "MESSAGE\ndestination:/topic/orders\nmessage-id:1\n\n{\"id\":1}\0"
                 + "\n"   // сердцебиение
                 + "MESSAGE\r\ndestination:/topic/orders\r\n\r\n{\"id\":2}\0";

        var bodies = StreamEvents.Bodies(Stomp("/topic/orders"), text, out var trouble);

        Assert.Equal("", trouble);
        Assert.Equal(["""{"id":1}""", """{"id":2}"""], bodies);
    }

    /// <summary>Кадр ошибки возвращается словами той стороны — он объясняет, почему событий нет.</summary>
    [Fact]
    public void An_error_frame_is_reported_in_words()
    {
        var bodies = StreamEvents.Bodies(Stomp("/topic/orders"),
                                         "ERROR\nmessage:подписка отклонена\n\n\0", out var trouble);

        Assert.Empty(bodies);
        Assert.Equal("подписка отклонена", trouble);
    }

    // ── Строки ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Записи лежат по пути, а поля конверта дописываются к каждой: вид события лежит снаружи
    /// записи, и без него отбор по виду не построить.
    /// </summary>
    [Fact]
    public void Envelope_fields_travel_with_every_record()
    {
        var rows = StreamEvents.Rows(
            """{"kind":"created","data":[{"id":7,"title":"Первая"},{"id":8,"title":"Вторая"}]}""",
            "data", out var trouble);

        Assert.Equal("", trouble);
        Assert.Equal(2, rows.Count);

        Assert.Equal("7",       rows[0]["id"]);
        Assert.Equal("Первая",  rows[0]["title"]);
        Assert.Equal("created", rows[0]["kind"]);
        Assert.Equal("created", rows[1]["kind"]);
    }

    /// <summary>Поле записи старше одноименного поля конверта: запись знает о себе лучше.</summary>
    [Fact]
    public void A_record_field_wins_over_the_envelope()
    {
        var rows = StreamEvents.Rows("""{"id":"конверт","data":{"id":"запись"}}""", "data", out _);

        Assert.Equal("запись", Assert.Single(rows)["id"]);
    }

    /// <summary>Вложенный объект записи разворачивается точкой — как в ответе запроса.</summary>
    [Fact]
    public void Nested_objects_unfold_with_a_dot()
    {
        var rows = StreamEvents.Rows("""{"data":{"author":{"id":5,"name":"Иванов"}}}""", "data", out _);

        var row = Assert.Single(rows);
        Assert.Equal("5",      row["author.id"]);
        Assert.Equal("Иванов", row["author.name"]);
    }

    /// <summary>Путь пуст — записью считается само событие.</summary>
    [Fact]
    public void Without_a_path_the_event_is_the_record()
    {
        var rows = StreamEvents.Rows("""{"id":1,"text":"Привет"}""", "", out var trouble);

        Assert.Equal("", trouble);
        Assert.Equal("Привет", Assert.Single(rows)["text"]);
    }

    /// <summary>Имена колонок ищутся без учета регистра: их пишет человек в шаге сценария.</summary>
    [Fact]
    public void Columns_are_found_regardless_of_case()
    {
        var rows = StreamEvents.Rows("""{"data":{"AuthorId":5}}""", "data", out _);

        Assert.Equal("5", Assert.Single(rows)["authorid"]);
    }

    /// <summary>
    /// Событие другого вида — без ветки записей — не ошибка: по одному потоку ходят события
    /// разной формы. Строк нет, и жаловаться не на что.
    /// </summary>
    [Fact]
    public void An_event_without_the_records_branch_gives_no_rows()
    {
        var rows = StreamEvents.Rows("""{"kind":"ping"}""", "data", out var trouble);

        Assert.Empty(rows);
        Assert.Equal("", trouble);
    }

    /// <summary>То, что не является JSON, названо словами — и самого текста в объяснении нет.</summary>
    [Fact]
    public void A_garbled_event_is_named_without_quoting_it()
    {
        var rows = StreamEvents.Rows("секретный текст", "data", out var trouble);

        Assert.Empty(rows);
        Assert.Contains("не является JSON", trouble, StringComparison.Ordinal);
        Assert.DoesNotContain("секретный", trouble, StringComparison.Ordinal);
    }

    /// <summary>По пути лежит одиночное значение — это ошибка настройки пути, и она названа.</summary>
    [Fact]
    public void A_scalar_at_the_records_path_is_a_named_trouble()
    {
        var rows = StreamEvents.Rows("""{"data":5}""", "data", out var trouble);

        Assert.Empty(rows);
        Assert.Contains("одиночное значение", trouble, StringComparison.Ordinal);
    }
}
