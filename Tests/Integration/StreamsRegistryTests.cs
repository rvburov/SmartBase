using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Потоки событий в app.db (<c>IntegrationsRegistry.Streams</c>) и то, что держит их
/// целостность: таблицы заводятся сырым DDL, внешних ключей нет, и правила «не удалять то, на
/// что ссылаются» живут в реестре.
///
/// Заодно — перенос между машинами (<see cref="ScenarioTransfer"/>): сценарий потока без самого
/// потока и без запроса ответа приехал бы на ту сторону пустой оболочкой.
/// </summary>
public sealed class StreamsRegistryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("sbstreams_").FullName;

    public void Dispose()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { /* best-effort */ }
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    /// <summary>Отдельная машина: свой app.db, свой реестр.</summary>
    private DatabaseRegistry Machine(string name) =>
        DatabaseRegistry.Open(Path.Combine(_dir, name + ".db"), Path.Combine(_dir, name + "-app.db"));

    private static IntegrationConnection Connection(IntegrationsRegistry cards) =>
        cards.Save(IntegrationConnection.New() with
        {
            Name = "Учетная система", Address = "https://api.zavod.local", TimeoutSeconds = 30,
        });

    private static IntegrationStream Stream(IntegrationsRegistry cards, int connectionId) =>
        cards.SaveStream(IntegrationStream.New() with
        {
            Name         = "События",
            ConnectionId = connectionId,
            Path         = "/events",
            Protocol     = StreamProtocol.Stomp,
            Topics       = ["/topic/orders", "/topic/stock"],
            ItemsPath    = "data",
            KeyField     = "id",
        });

    private static IntegrationRequest Reply(IntegrationsRegistry cards, int connectionId) =>
        cards.SaveRequest(IntegrationRequest.New() with
        {
            Name         = "Ответ на событие",
            ConnectionId = connectionId,
            Method       = RequestMethod.Put,
            Path         = "/reply",
            Body         = """{"message": "{{ответ.текст}}"}""",
        });

    private static Scenario Scenario(IntegrationsRegistry cards, int streamId, int replyId = 0) =>
        cards.SaveScenario(new Scenario(0, "По событию", false,
            ScenarioTrigger.OnStream(streamId, replyId, replyId > 0 ? "Сервер не работает" : "",
                                     replyId > 0 ? "<br>" : ""),
            [ScenarioStep.OfNotice("Пришло событие")]));

    // ── Хранение ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_stream_survives_reopening_the_registry()
    {
        int id;

        using (var registry = Machine("a"))
        {
            var cards = registry.Integrations;
            id = Stream(cards, Connection(cards).Id).Id;
        }

        using (var registry = Machine("a"))
        {
            var stream = registry.Integrations.FindStream(id);

            Assert.NotNull(stream);
            Assert.Equal("События", stream!.Name);
            Assert.Equal("/events", stream.Path);
            Assert.Equal(StreamProtocol.Stomp, stream.Protocol);
            Assert.Equal(["/topic/orders", "/topic/stock"], stream.Topics);
            Assert.Equal("data", stream.ItemsPath);
            Assert.Equal("id",   stream.KeyField);
        }
    }

    [Fact]
    public void A_stream_without_a_connection_is_not_saved()
    {
        using var registry = Machine("a");

        var failure = Assert.Throws<InvalidOperationException>(
            () => registry.Integrations.SaveStream(IntegrationStream.New() with
            {
                Name = "События", ConnectionId = 99, Path = "/events",
            }));

        Assert.Contains("соединение не найдено", failure.Message, StringComparison.Ordinal);
    }

    // ── Целостность ───────────────────────────────────────────────────────────

    /// <summary>Соединение, через которое открываются потоки, не удаляется.</summary>
    [Fact]
    public void A_connection_with_streams_is_not_deleted()
    {
        using var registry = Machine("a");
        var cards      = registry.Integrations;
        var connection = Connection(cards);

        Stream(cards, connection.Id);

        Assert.Throws<InvalidOperationException>(() => cards.Delete(connection.Id));
        Assert.NotNull(cards.Find(connection.Id));
    }

    /// <summary>Поток, события которого заводят сценарий, не удаляется.</summary>
    [Fact]
    public void A_stream_with_scenarios_is_not_deleted()
    {
        using var registry = Machine("a");
        var cards  = registry.Integrations;
        var stream = Stream(cards, Connection(cards).Id);

        var scenario = Scenario(cards, stream.Id);

        Assert.Throws<InvalidOperationException>(() => cards.DeleteStream(stream.Id));

        cards.DeleteScenario(scenario.Id);
        cards.DeleteStream(stream.Id);

        Assert.Null(cards.FindStream(stream.Id));
    }

    /// <summary>
    /// Запрос, которым сценарий отвечает на событие, не удаляется: без него сценарий примет
    /// событие и промолчит, а та сторона будет ждать ответа.
    /// </summary>
    [Fact]
    public void A_reply_request_is_not_deleted_while_a_scenario_answers_with_it()
    {
        using var registry = Machine("a");
        var cards      = registry.Integrations;
        var connection = Connection(cards);
        var reply      = Reply(cards, connection.Id);

        Scenario(cards, Stream(cards, connection.Id).Id, reply.Id);

        Assert.Throws<InvalidOperationException>(() => cards.DeleteRequest(reply.Id));
    }

    [Fact]
    public void A_scenario_of_a_missing_stream_is_not_saved()
    {
        using var registry = Machine("a");

        Assert.Throws<InvalidOperationException>(() => Scenario(registry.Integrations, streamId: 42));
    }

    /// <summary>
    /// Что мешает сценарию потока, названо словами: нет потока, запрос ответа только читает.
    /// Ответ — отправка, и идти ему через замок направлений; читающий запрос прошел бы мимо.
    /// </summary>
    [Fact]
    public void A_reading_request_cannot_be_the_reply()
    {
        using var registry = Machine("a");
        var cards      = registry.Integrations;
        var connection = Connection(cards);

        var reading = cards.SaveRequest(IntegrationRequest.New() with
        {
            Name = "Только читает", ConnectionId = connection.Id, Path = "/status",
        });

        var scenario = Scenario(cards, Stream(cards, connection.Id).Id, reading.Id);

        Assert.Contains("только читает", ScenarioRunner.StreamTrouble(scenario, cards) ?? "",
                        StringComparison.Ordinal);
    }

    // ── Перенос между машинами ────────────────────────────────────────────────

    /// <summary>
    /// Сценарий потока уезжает вместе с потоком, запросом ответа и их соединением, а на той
    /// стороне связывается с ними по именам — ключи у каждой машины свои.
    /// </summary>
    [Fact]
    public void A_stream_scenario_travels_with_its_stream_and_reply()
    {
        ScenarioBundle bundle;

        using (var source = Machine("a"))
        {
            var cards      = source.Integrations;
            var connection = Connection(cards);

            var scenario = Scenario(cards, Stream(cards, connection.Id).Id,
                                    Reply(cards, connection.Id).Id);

            bundle = ScenarioTransfer.Export(cards, [scenario], [], "машина-а", Now);
        }

        Assert.Equal(ScenarioBundle.SupportedVersion, bundle.Version);
        Assert.Single(bundle.StreamList);
        Assert.Single(bundle.Requests);
        Assert.Single(bundle.Connections);

        var parsed = ScenarioBundle.TryParse(bundle.ToJson(), out var error);
        Assert.Equal("", error);

        using var target = Machine("b");

        // На той стороне ключи другие: сначала заводим постороннее, чтобы номера разошлись.
        target.Integrations.Save(IntegrationConnection.New() with
        {
            Name = "Другое", Address = "https://other.zavod.local",
        });

        ScenarioTransfer.Import(parsed!, target.Integrations, [], Now);

        var arrived = Assert.Single(target.Integrations.Scenarios());

        Assert.False(arrived.Enabled);
        Assert.Equal(ScenarioTriggerKind.Stream, arrived.Trigger.Kind);
        Assert.Equal("Сервер не работает", arrived.Trigger.FailText);
        Assert.Equal("<br>", arrived.Trigger.ReplyBreak);

        var stream = target.Integrations.FindStream(arrived.Trigger.StreamId);
        var reply  = target.Integrations.FindRequest(arrived.Trigger.RequestId);

        Assert.Equal("События", stream!.Name);
        Assert.Equal(["/topic/orders", "/topic/stock"], stream.Topics);
        Assert.Equal("Ответ на событие", reply!.Name);
        Assert.Null(ScenarioRunner.StreamTrouble(arrived, target.Integrations));
    }

    /// <summary>
    /// Набор без потоков пишется прежней версией: его поймет и выпуск программы, который о
    /// потоках еще не знает.
    /// </summary>
    [Fact]
    public void A_bundle_without_streams_keeps_the_plain_version()
    {
        using var source = Machine("a");
        var cards = source.Integrations;

        var request = cards.SaveRequest(IntegrationRequest.New() with
        {
            Name = "Забрать заказы", ConnectionId = Connection(cards).Id, Path = "/orders",
        });

        var scenario = cards.SaveScenario(new Scenario(0, "Заказы", false,
            ScenarioTrigger.EveryMinutes(30), [ScenarioStep.OfRequest(request.Id)]));

        var bundle = ScenarioTransfer.Export(cards, [scenario], [], "машина-а", Now);

        Assert.Equal(ScenarioBundle.PlainVersion, bundle.Version);
        Assert.DoesNotContain("\"streams\": [", bundle.ToJson(), StringComparison.Ordinal);
    }
}
