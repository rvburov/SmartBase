using System.Text.Json;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Прием по сети целиком (<see cref="IngressHandler"/>): обращение сторонней программы становится
/// записями коллекции — или отказом с причиной. Сокета здесь нет ни одного: дверь отделена от
/// слушателя, и каждую развилку приема можно проверить.
///
/// <list type="bullet">
///   <item>без токена и с чужим токеном не проходит ничего;</item>
///   <item>база определяется токеном, параметр «database» обязан с ней совпасть;</item>
///   <item>записи подписаны ИМЕНЕМ РОБОТА, а не человеком за машиной;</item>
///   <item>права робота — обычная маска пользователя базы: по умолчанию запись без чтения;</item>
///   <item>повтор возвращает тот же ответ и не заводит вторых записей.</item>
/// </list>
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class IngressHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("sbingress_").FullName;
    private readonly TempDatabase _db = new(dbId: 7, name: "Основная");
    private readonly DatabaseRegistry _registry;
    private readonly IngressReplies _replies;

    public IngressHandlerTests()
    {
        _registry = DatabaseRegistry.Open(Path.Combine(_dir, "default.db"), Path.Combine(_dir, "app.db"));
        _replies = new IngressReplies(Path.Combine(_dir, "integrations.db"));
        RunJournal.Attach(null);
    }

    public void Dispose()
    {
        RunJournal.Attach(null);
        _registry.Dispose();
        _db.Dispose();
        try { SqliteGate.ClearAllPools(); } catch { /* временный стенд */ }
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    private IntegrationsRegistry Cards => _registry.Integrations;

    /// <summary>Выдать токен и завести под него робота — как это делает экран.</summary>
    private string Issue(string name = "Обмен учетной системы", DatabaseManager? db = null)
    {
        var target = db ?? _db.Manager;
        var (token, secret) = Cards.Issue(name, target.DbId, "ПК17", Now);
        target.Permissions.EnsureRobot(token.RobotKey, token.Name, Now);
        return secret;
    }

    /// <summary>Дать роботу токена права сверх записи — той же маской, что в панели администратора.</summary>
    private static void Allow(DatabaseManager db, AccessToken token, PermAction extra)
    {
        var robot = db.Context.DbUsers.Single(u => u.UserKey == token.RobotKey);
        var mask = db.Permissions.GetMask(robot.Id, null);
        db.Permissions.SetMask(robot.Id, null, mask | (int)extra);
    }

    private (int Id, int Number, int State) Orders()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Заказы");
        var number = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Номер");
        var state = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Статус", position: 2000);
        return (collection.Id!.Value, number.Id!.Value, state.Id!.Value);
    }

    private IngressAnswer Post(string token, string body, string query = "") =>
        IngressHandler.Handle(new IngressCall("POST", IngressApi.InboxPath, token, body, "127.0.0.1", query),
            Cards, [_db.Manager], _replies, Now);

    private IngressAnswer Get(string path, string token, string query = "") =>
        IngressHandler.Handle(new IngressCall("GET", path, token, "", "127.0.0.1", query),
            Cards, [_db.Manager], _replies, Now);

    private static string Message(string collection, string mode, string key, string id, string rows) =>
        $$"""
        { "collection": {{Json(collection)}}, "mode": {{Json(mode)}},
          "key": {{Json(key)}}, "id": {{Json(id)}}, "rows": {{rows}} }
        """;

    private static string Json(string text) => JsonSerializer.Serialize(text);
    private static JsonElement Body(IngressAnswer answer) => JsonDocument.Parse(answer.Body).RootElement;

    // ── Токен ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Without_a_token_nothing_gets_in()
    {
        Orders();

        Assert.Equal(401, Post("", Message("Заказы", "add", "", "m1", """[ { "Номер": "А-1" } ]""")).Status);
        Assert.Equal(401, Get(IngressApi.PingPath, "").Status);
    }

    [Fact]
    public void A_wrong_token_gets_the_same_answer_as_no_token()
    {
        Orders();
        var real = Issue();

        var wrong = Post(real + "1", Message("Заказы", "add", "", "m1", """[ { "Номер": "А-1" } ]"""));
        var none  = Post("",         Message("Заказы", "add", "", "m2", """[ { "Номер": "А-2" } ]"""));

        // Ответ обязан быть тот же: «токен есть, но не подошел» помогает подбирать.
        Assert.Equal(401, wrong.Status);
        Assert.Equal(none.Status, wrong.Status);
        Assert.Equal(none.Body,   wrong.Body);
    }

    [Fact]
    public void A_switched_off_token_stops_working_at_once()
    {
        Orders();
        var secret = Issue();

        Cards.EnableToken(Cards.Tokens()[0].Id, enabled: false);

        Assert.Equal(401, Post(secret, Message("Заказы", "add", "", "m1", """[ { "Номер": "А-1" } ]""")).Status);
    }

    [Fact]
    public void A_removed_token_stops_working_at_once()
    {
        Orders();
        var secret = Issue();

        Cards.RemoveToken(Cards.Tokens()[0].Id);

        Assert.Equal(401, Post(secret, Message("Заказы", "add", "", "m1", """[ { "Номер": "А-1" } ]""")).Status);
    }

    // ── Адреса ────────────────────────────────────────────────────────────────

    /// <summary>«Такого адреса нет» отвечается до проверки токена и о токенах не говорит.</summary>
    [Fact]
    public void An_unknown_address_lists_the_supported_paths()
    {
        var answer = Get("/v1/tables", "");

        Assert.Equal(404, answer.Status);
        Assert.Contains(IngressApi.InboxPath, answer.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("токен", answer.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_inbox_answers_only_to_post()
    {
        Assert.Equal(405, Get(IngressApi.InboxPath, Issue()).Status);
    }

    [Fact]
    public void Ping_names_the_database_and_the_token()
    {
        Orders();

        var answer = Get(IngressApi.PingPath, Issue());
        var body   = Body(answer);

        Assert.Equal(200, answer.Status);
        Assert.True(body.GetProperty("ready").GetBoolean());
        Assert.Equal("Основная", body.GetProperty("database").GetString());
        Assert.Equal(7, body.GetProperty("databaseId").GetInt32());
        Assert.Equal("Обмен учетной системы", body.GetProperty("token").GetString());
    }

    /// <summary>
    /// Токен сам по себе не читает: робот по умолчанию только пишет, и чтение ему открывает
    /// администратор правом «Просмотр коллекции». Придуманных адресов «вычитать сбоку» нет.
    /// </summary>
    [Fact]
    public void A_token_alone_does_not_read_records_out()
    {
        Orders();
        var secret = Issue();

        Assert.Equal(403, Get(IngressApi.RecordsPath, secret, "collection=Заказы").Status);
        Assert.Equal(403, Get(IngressApi.ChangesPath, secret, "collection=Заказы").Status);

        foreach (var path in new[] { "/v1/rows", "/v1/collections/Заказы" })
            Assert.Equal(404, Get(path, secret).Status);
    }

    [Fact]
    public void The_schema_lists_collections_and_no_values()
    {
        var (id, _, _) = Orders();
        SampleData.AddRecord(_db.Constructor, id, "Молоток");

        var answer = Get(IngressApi.SchemaPath, Issue());

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

        var answer = Post(Issue(), Message("Заказы", "add", "", "m1", """
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

    /// <summary>
    /// Записи подписаны именем РОБОТА: в истории изменений должно стоять «Обмен склада создал
    /// запись», иначе чужие записи неотличимы от своих.
    /// </summary>
    [Fact]
    public void Records_are_signed_by_the_robot_not_by_the_person_at_the_machine()
    {
        var (id, _, _) = Orders();

        Post(Issue("Обмен склада"), Message("Заказы", "add", "", "m1", """[ { "Название": "Молоток" } ]"""));

        var record = _db.Constructor.GetRecords(id).Single();

        Assert.Equal("Обмен склада", _db.Constructor.GetRecord(record.Id!.Value)?.CreatedBy);

        // И то же имя — в истории изменений: отзывать доступ роботу будут по нему.
        Assert.Contains(_db.Context.ChangeLog.ToList(),
                        row => row.UserName == "Обмен склада");

        // Подмена имени кончается вместе с обращением: следующая запись человека — снова его.
        Assert.False(SmartBase.Utils.CurrentUser.ActingAsRobot);
    }

    [Fact]
    public void A_repeat_returns_the_very_same_answer_and_creates_nothing()
    {
        var (id, _, _) = Orders();

        var message = Message("Заказы", "add", "", "m-42", """[ { "Название": "Молоток" } ]""");
        var secret  = Issue();

        var first  = Post(secret, message);
        var second = Post(secret, message);

        Assert.Equal(200, first.Status);
        Assert.Equal(200, second.Status);
        Assert.Equal(first.Body, second.Body);

        Assert.Single(_db.Constructor.GetRecords(id));
    }

    [Fact]
    public void A_message_that_is_not_json_is_refused_with_a_reason()
    {
        Orders();

        var answer = Post(Issue(), "это не json");

        Assert.Equal(400, answer.Status);
        Assert.Contains("error", answer.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_add_without_a_message_id_is_refused()
    {
        Orders();

        // Без идентификатора повторная доставка завела бы записи второй раз.
        var answer = Post(Issue(), Message("Заказы", "add", "", "", """[ { "Название": "Х" } ]"""));

        Assert.Equal(400, answer.Status);
    }

    [Fact]
    public void An_unknown_collection_is_refused_as_a_content_problem()
    {
        Orders();

        var answer = Post(Issue(), Message("Поставки", "add", "", "m1",
                                          """[ { "Название": "Х" } ]"""));

        Assert.Equal(422, answer.Status);
        Assert.Contains("Поставки", answer.Body, StringComparison.Ordinal);
    }

    // ── Права робота ──────────────────────────────────────────────────────────

    [Fact]
    public void A_robot_awaiting_approval_is_refused_and_told_why()
    {
        Orders();
        var secret = Issue();

        var robot = _db.Context.DbUsers.First(u => u.UserKey == Cards.Tokens()[0].RobotKey);
        robot.IsApproved = false;
        _db.Context.SaveChanges();

        var answer = Post(secret, Message("Заказы", "add", "", "m1", """[ { "Название": "Х" } ]"""));

        Assert.Equal(403, answer.Status);
        Assert.Contains("допуск", answer.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Токен, под который робота не завели, не проходит. Так выглядит перенос app.db на другую
    /// машину: токены переехали, а пользователей в базе под них нет.
    /// </summary>
    [Fact]
    public void A_token_without_a_robot_gets_nowhere()
    {
        Orders();
        var (_, secret) = Cards.Issue("Ничей", 7, "ПК17", Now);   // робота не заводим

        var answer = Post(secret, Message("Заказы", "add", "", "m1", """[ { "Название": "Х" } ]"""));

        Assert.Equal(403, answer.Status);
    }

    /// <summary>Маска робота по умолчанию — только запись: читать и удалять он не может.</summary>
    [Fact]
    public void A_robot_is_given_writing_and_no_reading()
    {
        Orders();
        Issue();

        var access = _db.Permissions.Robot(Cards.Tokens()[0].RobotKey);

        Assert.True(access.Can(PermAction.CreateRecord));
        Assert.True(access.Can(PermAction.EditRecord));
        Assert.False(access.Can(PermAction.ViewCollection));
        Assert.False(access.Can(PermAction.DeleteRecord));
        Assert.False(access.Can(PermAction.CreateCollection));
    }

    /// <summary>Удаление — по праву «Удаление записи» у робота, а не по одному токену.</summary>
    [Fact]
    public void Deleting_needs_the_delete_right_of_the_robot()
    {
        var (id, _, _) = Orders();
        var secret = Issue();
        Post(secret, Message("Заказы", "add", "", "m1", """[ { "Название": "Х", "Номер": "А-1" } ]"""));

        var refused = Post(secret, Message("Заказы", "delete", "Номер", "", """[ { "Номер": "А-1" } ]"""));
        Assert.Equal(403, refused.Status);
        Assert.Single(_db.Constructor.GetRecords(id));

        Allow(_db.Manager, Cards.Tokens()[0], PermAction.DeleteRecord);
        var deleted = Post(secret, Message("Заказы", "delete", "Номер", "", """[ { "Номер": "А-1" } ]"""));

        Assert.Equal(1, Body(deleted).GetProperty("deleted").GetInt32());
        Assert.Empty(_db.Constructor.GetRecords(id));
    }

    // ── База ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_token_of_a_database_that_is_not_open_is_told_so()
    {
        Orders();
        var (_, secret) = Cards.Issue("Чужая база", dbId: 99, "ПК17", Now);

        var answer = Post(secret, Message("Заказы", "add", "", "m1", """[ { "Название": "Х" } ]"""));

        Assert.Equal(503, answer.Status);
    }

    /// <summary>
    /// База определяется токеном даже среди нескольких открытых: параметр не нужен, а
    /// указанный — тот же самый ключ.
    /// </summary>
    [Fact]
    public void The_token_routes_to_its_own_database()
    {
        using var other = new TempDatabase(dbId: 9, name: "Другая");
        var secret = Issue("Обмен другой базы", other.Manager);
        IngressAnswer Ping(string query) => IngressHandler.Handle(
            new IngressCall("GET", IngressApi.PingPath, secret, "", Query: query),
            Cards, [_db.Manager, other.Manager], _replies, Now);

        foreach (var query in new[] { "", "database=9" })
        {
            var answer = Ping(query);
            Assert.Equal(200, answer.Status);
            Assert.Equal("Другая", Body(answer).GetProperty("database").GetString());
            Assert.Equal(9, Body(answer).GetProperty("databaseId").GetInt32());
        }
    }

    /// <summary>Чужой ключ базы в параметре — отказ, а не тихая подмена базой токена.</summary>
    [Fact]
    public void A_database_parameter_of_another_database_is_refused()
    {
        using var other = new TempDatabase(dbId: 9, name: "Другая");
        var secret = Issue();

        var answer = IngressHandler.Handle(
            new IngressCall("GET", IngressApi.PingPath, secret, "", Query: "database=9"),
            Cards, [_db.Manager, other.Manager], _replies, Now);

        Assert.Equal(403, answer.Status);
        Assert.DoesNotContain("Другая", answer.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("database=0")]
    [InlineData("database=-1")]
    [InlineData("database=1.2")]
    [InlineData("database=2147483648")]
    [InlineData("database=нет")]
    [InlineData("database=7&database=9")]
    public void An_invalid_database_parameter_is_refused(string query)
    {
        Assert.Equal(400, Get(IngressApi.PingPath, Issue(), query).Status);
    }

    [Fact]
    public void The_same_message_id_in_different_databases_does_not_share_a_reply()
    {
        var (ownId, _, _) = Orders();
        using var other = new TempDatabase(dbId: 9, name: "Другая");
        var otherCollection = SampleData.AddCollection(other.Constructor, "Заказы").Id!.Value;
        var own = Issue();
        var foreign = Issue("Обмен другой базы", other.Manager);
        var text = Message("Заказы", "add", "", "shared-id", """[ { "Название": "Запись" } ]""");
        IngressAnswer Send(string token) => IngressHandler.Handle(
            new IngressCall("POST", IngressApi.InboxPath, token, text),
            Cards, [_db.Manager, other.Manager], _replies, Now);

        Assert.Equal(200, Send(own).Status);
        var firstOther = Send(foreign);
        Assert.Equal(200, firstOther.Status);
        Assert.Equal(firstOther.Body, Send(foreign).Body);
        Assert.Single(_db.Constructor.GetRecords(ownId));
        Assert.Single(other.Constructor.GetRecords(otherCollection));
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Api_crud_and_reading_work_on_both_providers_with_robot_rights(string profile)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var secret = Issue("Робот стенда", fixture.Manager);
        Allow(fixture.Manager, Cards.Tokens().Single(t => t.Name == "Робот стенда"),
              PermAction.CreateCollection | PermAction.CreateField | PermAction.DeleteRecord
              | PermAction.ViewCollection);
        IngressAnswer Send(string path, string body = "", string query = "") => IngressHandler.Handle(
            new IngressCall(path == IngressApi.InboxPath ? "POST" : "GET", path, secret, body,
                Query: $"database={fixture.Manager.DbId}&{query}"),
            Cards, [fixture.Manager], _replies, Now);

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
