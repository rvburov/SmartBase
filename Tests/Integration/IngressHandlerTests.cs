using System.Text.Json;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Прием по сети целиком (<see cref="IngressHandler"/>): обращение сторонней программы становится
/// записями коллекции — или отказом с причиной.
///
/// Сокета здесь нет ни одного, и это не упрощение ради теста: дверь отделена от слушателя именно
/// затем, чтобы каждую развилку приема можно было проверить. Проверяется то, ради чего канал
/// существует и чего он не должен делать никогда:
///
/// <list type="bullet">
///   <item>без токена и с чужим токеном не проходит ничего;</item>
///   <item>записи подписаны ИМЕНЕМ РОБОТА, а не человеком за машиной;</item>
///   <item>повтор возвращает тот же ответ и не заводит вторых записей;</item>
///   <item>закрытая база недоступна входящим;</item>
///   <item>данных наружу дверь не отдает — только схему.</item>
/// </list>
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class IngressHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly string           _dir;
    private readonly string           _appDb;
    private readonly TempDatabase     _db = new(dbId: 7, name: "Основная");
    private readonly DatabaseRegistry _registry;
    private readonly IngressReplies   _replies;

    public IngressHandlerTests()
    {
        _dir      = Directory.CreateTempSubdirectory("sbingress_").FullName;
        _appDb    = Path.Combine(_dir, "app.db");
        _registry = DatabaseRegistry.Open(Path.Combine(_dir, "default.db"), _appDb);
        _replies  = new IngressReplies(Path.Combine(_dir, "integrations.db"));
    }

    public void Dispose()
    {
        EgressGate.Close();
        _registry.Dispose();
        _db.Dispose();

        try { SqliteGate.ClearAllPools(); } catch { /* best-effort */ }
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    // ── Обвязка ───────────────────────────────────────────────────────────────

    private IntegrationsRegistry Cards => _registry.Integrations;

    /// <summary>Выдать токен и завести под него робота — как это делает экран.</summary>
    private string Issue(string name = "Обмен учетной системы", int dbId = 7)
    {
        var (token, secret) = Cards.Issue(name, dbId, "ПК17", Now);
        _db.Permissions.EnsureRobot(token.RobotKey, token.Name, Now);
        return secret;
    }

    /// <summary>Коллекция «Заказы» со свойствами «Номер» и «Статус».</summary>
    private (int Id, int Number, int State) Orders()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Заказы");
        var number     = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Номер");
        var state      = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Статус",
                                             position: 2000);

        return (collection.Id!.Value, number.Id!.Value, state.Id!.Value);
    }

    private IngressAnswer Post(string token, string body) =>
        IngressHandler.Handle(
            new IngressCall("POST", IngressApi.InboxPath, token, body, "127.0.0.1"),
            Cards, [_db.Manager], _replies, Now);

    private IngressAnswer Get(string path, string token) =>
        IngressHandler.Handle(new IngressCall("GET", path, token, "", "127.0.0.1"),
                              Cards, [_db.Manager], _replies, Now);

    private static string Message(string collection, string mode, string key, string id, string rows) =>
        $$"""
        { "collection": {{Json(collection)}}, "mode": {{Json(mode)}},
          "key": {{Json(key)}}, "id": {{Json(id)}}, "rows": {{rows}} }
        """;

    private static string Json(string text) => JsonSerializer.Serialize(text);

    private static JsonElement Body(IngressAnswer answer) =>
        JsonDocument.Parse(answer.Body).RootElement;

    // ── Токен ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Without_a_token_nothing_gets_in()
    {
        Orders();

        var answer = Post("", Message("Заказы", "add", "", "m1", """[ { "Номер": "А-1" } ]"""));

        Assert.Equal(401, answer.Status);
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

        Assert.Equal(401, Post(secret, Message("Заказы", "add", "", "m1",
                                               """[ { "Номер": "А-1" } ]""")).Status);
    }

    [Fact]
    public void A_removed_token_stops_working_at_once()
    {
        Orders();
        var secret = Issue();

        Cards.RemoveToken(Cards.Tokens()[0].Id);

        Assert.Equal(401, Post(secret, Message("Заказы", "add", "", "m1",
                                               """[ { "Номер": "А-1" } ]""")).Status);
    }

    // ── Адреса ────────────────────────────────────────────────────────────────

    [Fact]
    public void An_unknown_address_says_so_without_mentioning_tokens()
    {
        var answer = Get("/v1/tables", "");

        Assert.Equal(404, answer.Status);
        Assert.Contains(IngressApi.InboxPath, answer.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_inbox_answers_only_to_post()
    {
        Assert.Equal(405, Get(IngressApi.InboxPath, "").Status);
    }

    /// <summary>
    /// Токен сам по себе не читает: у чтения свой замок — выдача, и без нее <c>/v1/records</c>
    /// отказывает (полные проверки замка — в <c>IngressReadTests</c>). Придуманных адресов
    /// «вычитать сбоку» по-прежнему не существует.
    /// </summary>
    [Fact]
    public void A_token_alone_does_not_read_records_out()
    {
        Orders();
        var secret = Issue();

        Assert.Equal(403, Get("/v1/records", secret).Status);

        foreach (var path in new[] { "/v1/rows", "/v1/collections/Заказы" })
            Assert.Equal(404, Get(path, secret).Status);
    }

    // ── Проверка связи и схема ────────────────────────────────────────────────

    [Fact]
    public void Ping_names_the_database_and_the_token()
    {
        Orders();
        var secret = Issue();

        var answer = Get(IngressApi.PingPath, secret);
        var body   = Body(answer);

        Assert.Equal(200, answer.Status);
        Assert.True(body.GetProperty("ready").GetBoolean());
        Assert.Equal("Основная", body.GetProperty("database").GetString());
        Assert.Equal("Обмен учетной системы", body.GetProperty("token").GetString());
    }

    [Fact]
    public void The_schema_lists_collections_and_no_values()
    {
        var (id, _, _) = Orders();
        SampleData.AddRecord(_db.Constructor, id, "Молоток");

        var secret = Issue();
        var answer = Get(IngressApi.SchemaPath, secret);

        Assert.Equal(200, answer.Status);
        Assert.Contains("Заказы",  answer.Body, StringComparison.Ordinal);
        Assert.Contains("Номер",   answer.Body, StringComparison.Ordinal);

        // Значений записей в схеме нет и быть не может.
        Assert.DoesNotContain("Молоток", answer.Body, StringComparison.Ordinal);
    }

    // ── Прием сообщения ───────────────────────────────────────────────────────

    [Fact]
    public void A_message_becomes_records_in_the_named_collection()
    {
        var (id, number, state) = Orders();
        var secret = Issue();

        var answer = Post(secret, Message("Заказы", "add", "", "m1", """
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
    /// Записи подписаны именем РОБОТА: в истории изменений должно стоять «Обмен учетной системы
    /// создал запись», иначе чужие записи неотличимы от своих и разбирать потом нечего.
    /// </summary>
    [Fact]
    public void Records_are_signed_by_the_robot_not_by_the_person_at_the_machine()
    {
        var (id, _, _) = Orders();
        var secret = Issue("Обмен склада");

        Post(secret, Message("Заказы", "add", "", "m1", """[ { "Название": "Молоток" } ]"""));

        var record = _db.Constructor.GetRecords(id).Single();

        Assert.Equal("Обмен склада", _db.Constructor.GetRecord(record.Id!.Value)?.CreatedBy);

        // И то же имя — в журнале изменений: отзывать доступ роботу будут по нему.
        Assert.Contains(_db.Context.ChangeLog.ToList(),
                        row => row.UserName == "Обмен склада");

        // Подмена имени кончается вместе с обращением: следующая запись человека — снова его.
        Assert.False(SmartBase.Utils.CurrentUser.ActingAsRobot);
    }

    [Fact]
    public void A_repeat_returns_the_very_same_answer_and_creates_nothing()
    {
        var (id, _, _) = Orders();
        var secret = Issue();

        var message = Message("Заказы", "add", "", "m-42", """[ { "Название": "Молоток" } ]""");

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
        var secret = Issue();

        var answer = Post(secret, "это не json");

        Assert.Equal(400, answer.Status);
        Assert.Contains("error", answer.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_add_without_a_message_id_is_refused()
    {
        Orders();
        var secret = Issue();

        // Без идентификатора повторная доставка завела бы записи второй раз.
        var answer = Post(secret, Message("Заказы", "add", "", "", """[ { "Название": "Х" } ]"""));

        Assert.Equal(400, answer.Status);
    }

    /// <summary>
    /// Сообщение понято, но сделать по нему нечего — это 422, а не 400: отвечать «неправильно
    /// сформирован запрос» на верно сформированный значит отправить человека править не то.
    /// </summary>
    [Fact]
    public void An_unknown_collection_is_refused_as_a_content_problem()
    {
        Orders();
        var secret = Issue();

        var answer = Post(secret, Message("Поставки", "add", "", "m1",
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

    /// <summary>Маска робота по умолчанию — только запись: читать базу через этот канал нельзя.</summary>
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

    // ── Закрытая база ─────────────────────────────────────────────────────────

    [Fact]
    public void A_closed_loop_database_is_unavailable_to_incoming_tokens()
    {
        Orders();
        var secret = Issue();

        Assert.Equal("", _db.Constructor.SetDatabaseLabel(ConfidentialityLabel.Closed));

        var answer = Post(secret, Message("Заказы", "add", "", "m1", """[ { "Название": "Х" } ]"""));

        Assert.Equal(403, answer.Status);
        Assert.Contains("закрытым контуром", answer.Body, StringComparison.Ordinal);
    }

    // ── База не открыта ───────────────────────────────────────────────────────

    [Fact]
    public void A_token_of_a_database_that_is_not_open_is_told_so()
    {
        Orders();
        var (_, secret) = Cards.Issue("Чужая база", dbId: 99, "ПК17", Now);

        var answer = Post(secret, Message("Заказы", "add", "", "m1", """[ { "Название": "Х" } ]"""));

        Assert.Equal(503, answer.Status);
    }
}
