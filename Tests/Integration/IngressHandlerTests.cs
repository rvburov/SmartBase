using System.Text.Json;
using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>Маршрутизация и прием HTTP API без входа, токенов и внешней политики.</summary>
[Collection(EgressCollection.Name)]
public sealed class IngressHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("sbingress_").FullName;
    private readonly TempDatabase _db = new(dbId: 7, name: "Основная");
    private readonly IngressReplies _replies;

    public IngressHandlerTests()
    {
        _replies = new IngressReplies(Path.Combine(_dir, "integrations.db"));
        RunJournal.Attach(null);
    }

    public void Dispose()
    {
        RunJournal.Attach(null);
        _db.Dispose();
        try { SqliteGate.ClearAllPools(); } catch { /* временный стенд */ }
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    private (int Id, int Number, int State) Orders()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Заказы");
        var number = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Номер");
        var state = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Статус", position: 2000);
        return (collection.Id!.Value, number.Id!.Value, state.Id!.Value);
    }

    private IngressAnswer Post(string body, string query = "") =>
        IngressHandler.Handle(new IngressCall("POST", IngressApi.InboxPath, body, "127.0.0.1", query),
            [_db.Manager], _replies, Now);

    private IngressAnswer Get(string path, string query = "") =>
        IngressHandler.Handle(new IngressCall("GET", path, "", "127.0.0.1", query),
            [_db.Manager], _replies, Now);

    private static string Message(string collection, string mode, string key, string id, string rows) =>
        $$"""
        { "collection": {{Json(collection)}}, "mode": {{Json(mode)}},
          "key": {{Json(key)}}, "id": {{Json(id)}}, "rows": {{rows}} }
        """;

    private static string Json(string text) => JsonSerializer.Serialize(text);
    private static JsonElement Body(IngressAnswer answer) => JsonDocument.Parse(answer.Body).RootElement;
    [Fact]
    public void An_unknown_address_lists_the_supported_paths()
    {
        var answer = Get("/v1/tables");

        Assert.Equal(404, answer.Status);
        Assert.Contains(IngressApi.InboxPath, answer.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_inbox_answers_only_to_post()
    {
        Assert.Equal(405, Get(IngressApi.InboxPath).Status);
    }

    [Fact]
    public void Ping_names_the_database_without_authentication()
    {
        Orders();

        var answer = Get(IngressApi.PingPath);
        var body   = Body(answer);

        Assert.Equal(200, answer.Status);
        Assert.True(body.GetProperty("ready").GetBoolean());
        Assert.Equal("Основная", body.GetProperty("database").GetString());
        Assert.Equal(7, body.GetProperty("databaseId").GetInt32());
        Assert.False(body.TryGetProperty("token", out _));
    }

    [Fact]
    public void The_schema_lists_collections_and_no_values()
    {
        var (id, _, _) = Orders();
        SampleData.AddRecord(_db.Constructor, id, "Молоток");

        var answer = Get(IngressApi.SchemaPath);

        Assert.Equal(200, answer.Status);
        Assert.Contains("Заказы",  answer.Body, StringComparison.Ordinal);
        Assert.Contains("Номер",   answer.Body, StringComparison.Ordinal);

        // Значений записей в схеме нет и быть не может.
        Assert.DoesNotContain("Молоток", answer.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_becomes_records_in_the_named_collection()
    {
        var (id, number, state) = Orders();

        var answer = Post(Message("Заказы", "add", "", "m1", """
            [ { "Название": "Молоток",  "Номер": "А-1", "Статус": "новый" },
              { "Название": "Отвертка", "Номер": "А-2", "Статус": "в работе" } ]
            """));

        Assert.Equal(200, answer.Status);

        var body = Body(answer);
        Assert.True(body.GetProperty("accepted").GetBoolean());
        Assert.Equal(2, body.GetProperty("created").GetInt32());

        var records = _db.Constructor.GetRecords(id);
        Assert.Equal(2, records.Count);

        var values = _db.Constructor.GetFieldValues(records[0].Id!.Value);
        Assert.Equal("А-1",   values[number]);
        Assert.Equal("новый", values[state]);
    }

    [Fact]
    public void Records_are_signed_by_the_api_source_and_restore_the_local_actor()
    {
        var (id, _, _) = Orders();

        Post(Message("Заказы", "add", "", "m1", """[ { "Название": "Молоток" } ]"""));

        var record = _db.Constructor.GetRecords(id).Single();

        Assert.Equal("Обмен по API", _db.Constructor.GetRecord(record.Id!.Value)?.CreatedBy);

        // И то же имя — в истории изменений.
        Assert.Contains(_db.Context.ChangeLog.ToList(),
                        row => row.UserName == "Обмен по API");

        // Подмена имени кончается вместе с обращением: следующая запись человека — снова его.
        Assert.False(SmartBase.Utils.CurrentUser.ActingAsRobot);
    }

    [Fact]
    public void A_repeat_returns_the_very_same_answer_and_creates_nothing()
    {
        var (id, _, _) = Orders();

        var message = Message("Заказы", "add", "", "m-42", """[ { "Название": "Молоток" } ]""");

        var first  = Post(message);
        var second = Post(message);

        Assert.Equal(200, first.Status);
        Assert.Equal(200, second.Status);
        Assert.Equal(first.Body, second.Body);

        Assert.Single(_db.Constructor.GetRecords(id));
    }

    [Fact]
    public void A_message_that_is_not_json_is_refused_with_a_reason()
    {
        Orders();

        var answer = Post("это не json");

        Assert.Equal(400, answer.Status);
        Assert.Contains("error", answer.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_add_without_a_message_id_is_refused()
    {
        Orders();

        // Без идентификатора повторная доставка завела бы записи второй раз.
        var answer = Post(Message("Заказы", "add", "", "", """[ { "Название": "Х" } ]"""));

        Assert.Equal(400, answer.Status);
    }

    [Fact]
    public void An_unknown_collection_is_refused_as_a_content_problem()
    {
        Orders();

        var answer = Post(Message("Поставки", "add", "", "m1",
                                          """[ { "Название": "Х" } ]"""));

        Assert.Equal(422, answer.Status);
        Assert.Contains("Поставки", answer.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void No_open_database_is_reported_before_processing()
    {
        var answer = IngressHandler.Handle(new IngressCall("GET", IngressApi.PingPath, ""), [], null, Now);
        Assert.Equal(503, answer.Status);
    }

    [Fact]
    public void Multiple_open_databases_require_an_explicit_selection()
    {
        using var other = new TempDatabase(dbId: 9, name: "Другая");
        var call = new IngressCall("GET", IngressApi.PingPath, "");
        var answer = IngressHandler.Handle(call, [_db.Manager, other.Manager], _replies, Now);
        Assert.Equal(400, answer.Status);
        Assert.Contains("database", answer.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Основная", answer.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_selection_routes_to_the_named_database()
    {
        using var other = new TempDatabase(dbId: 9, name: "Другая");
        var call = new IngressCall("GET", IngressApi.PingPath, "", Query: "database=9");
        var answer = IngressHandler.Handle(call, [_db.Manager, other.Manager], _replies, Now);
        Assert.Equal(200, answer.Status);
        Assert.Equal("Другая", Body(answer).GetProperty("database").GetString());
        Assert.Equal(9, Body(answer).GetProperty("databaseId").GetInt32());
    }

    [Theory]
    [InlineData("database=0", 400)]
    [InlineData("database=-1", 400)]
    [InlineData("database=1.2", 400)]
    [InlineData("database=2147483648", 400)]
    [InlineData("database=нет", 400)]
    [InlineData("database=7&database=9", 400)]
    [InlineData("database=99", 404)]
    public void Invalid_or_unavailable_database_is_not_replaced_with_the_first(string query, int status)
    {
        var answer = Get(IngressApi.PingPath, query);
        Assert.Equal(status, answer.Status);
    }

    [Fact]
    public void The_same_message_id_in_different_databases_does_not_share_a_reply()
    {
        var (ownId, _, _) = Orders();
        using var other = new TempDatabase(dbId: 9, name: "Другая");
        var otherCollection = SampleData.AddCollection(other.Constructor, "Заказы").Id!.Value;
        var text = Message("Заказы", "add", "", "shared-id", """[ { "Название": "Запись" } ]""");
        IngressAnswer Send(int id) => IngressHandler.Handle(
            new IngressCall("POST", IngressApi.InboxPath, text, Query: $"database={id}"),
            [_db.Manager, other.Manager], _replies, Now);

        Assert.Equal(200, Send(7).Status);
        var firstOther = Send(9);
        Assert.Equal(200, firstOther.Status);
        Assert.Equal(firstOther.Body, Send(9).Body);
        Assert.Single(_db.Constructor.GetRecords(ownId));
        Assert.Single(other.Constructor.GetRecords(otherCollection));
    }
    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Api_crud_and_reading_work_on_both_providers_without_authentication(string profile)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        IngressAnswer Send(string path, string body = "", string query = "") => IngressHandler.Handle(
            new IngressCall(path == IngressApi.InboxPath ? "POST" : "GET", path, body,
                Query: $"database={fixture.Manager.DbId}&{query}"),
            [fixture.Manager], _replies, Now);

        var prepared = Send(IngressApi.InboxPath, """
            {"collection":"API","mode":"ensure-collection","fields":[{"name":"Номер","type":"text"}]}
            """);
        Assert.Equal(200, prepared.Status);
        var inserted = Send(IngressApi.InboxPath, """
            {"collection":"API","mode":"add","id":"crud-add",
             "rows":[{"Название":"Первая","Номер":"A1"},{"Название":"Вторая","Номер":"A2"}]}
            """);
        Assert.Equal(2, Body(inserted).GetProperty("created").GetInt32());
        Assert.Equal(inserted.Body, Send(IngressApi.InboxPath, """
            {"collection":"API","mode":"add","id":"crud-add",
             "rows":[{"Название":"Первая","Номер":"A1"},{"Название":"Вторая","Номер":"A2"}]}
            """).Body);
        var updated = Send(IngressApi.InboxPath, """
            {"collection":"API","mode":"update","key":"Номер","rows":[{"Название":"Изменена","Номер":"A1"}]}
            """);
        Assert.Equal(1, Body(updated).GetProperty("updated").GetInt32());
        var deleted = Send(IngressApi.InboxPath, """
            {"collection":"API","mode":"delete","key":"Номер","rows":[{"Номер":"A2"}]}
            """);
        Assert.Equal(1, Body(deleted).GetProperty("deleted").GetInt32());
        var read = Send(IngressApi.RecordsPath, query: "collection=API");
        Assert.Equal(200, read.Status);
        var row = Assert.Single(Body(read).GetProperty("rows").EnumerateArray());
        Assert.Equal("Изменена", row.GetProperty("values").GetProperty("Название").GetString());
        var changes = Send(IngressApi.ChangesPath, query: "collection=API");
        Assert.Equal(200, changes.Status);
        Assert.Contains(Body(changes).GetProperty("changes").EnumerateArray(),
            c => c.GetProperty("action").GetString() == "delete"
                 && c.GetProperty("name").GetString() == "Вторая");
        using var context = fixture.OpenContext();
        MultiUserTestDatabase.AssertHealthy(context);
    }

}
