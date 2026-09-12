using System.Text.Json;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Чтение по выдаче (<see cref="IngressHandler"/>, <c>GET /v1/records</c> и
/// <c>GET /v1/changes</c> — docs/ROADMAP-API.md, шаг 10).
///
/// Ответ на чтение — данные, ПОКИДАЮЩИЕ программу, и проверяется здесь ровно то, на чем держится
/// замок:
///
/// <list type="bullet">
///   <item>без выдачи и без маски чтения не отдается ничего;</item>
///   <item>отдаются только поля выдачи, а метка, поставленная после, останавливает чтение;</item>
///   <item>каждый ответ — строка в журнале безопасности; журнал недоступен — чтения нет;</item>
///   <item>суточный потолок выдачи останавливает чтение, и окно у него скользящее;</item>
///   <item>журнал изменений отвечает про удаления — именами, как и ведется.</item>
/// </list>
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class IngressReadTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private const string Master = "секрет-организации";

    private readonly string           _dir;
    private readonly TempDatabase     _db = new(dbId: 7, name: "Основная");
    private readonly DatabaseRegistry _registry;

    public IngressReadTests()
    {
        _dir      = Directory.CreateTempSubdirectory("sbread_").FullName;
        _registry = DatabaseRegistry.Open(Path.Combine(_dir, "default.db"),
                                          Path.Combine(_dir, "app.db"));

        // Чтению нужны оба журнала: по рабочему считается потолок выдачи, в журнал
        // безопасности ложится след КАЖДОГО ответа — и без него чтения нет.
        RunJournal.Attach(new RunJournal(Path.Combine(_dir, "integrations.db")));
        SecurityJournal.Attach(new SecurityJournal(Path.Combine(_dir, "security.db")));

        Policy();
    }

    public void Dispose()
    {
        RunJournal.Attach(null);
        SecurityJournal.Attach(null);
        EgressGate.Close();

        _registry.Dispose();
        _db.Dispose();

        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { /* best-effort */ }
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    // ── Обвязка ───────────────────────────────────────────────────────────────

    private IntegrationsRegistry Cards => _registry.Integrations;

    private void Policy(long maxBytes = 1024 * 1024) =>
        EgressGate.AttachPolicy(new EgressPolicySet(
            [new EgressPolicy(EgressMode.Internal, [], [], [], 1000, maxBytes, 60, null, false,
                              "тест", MasterPassword.Hash(Master))]));

    /// <summary>Токен с маской чтения — как его собирает администратор: выдача токена, робот,
    /// галка «Просмотр коллекции» в панели пользователей.</summary>
    private (string Secret, AccessToken Token) IssueReader(bool canRead = true)
    {
        var (token, secret) = Cards.Issue("Отчеты цеха", 7, "ПК17", Now);
        _db.Permissions.EnsureRobot(token.RobotKey, token.Name, Now);

        if (canRead)
        {
            var robot = _db.Permissions.GetUsers().First(u => u.UserKey == token.RobotKey);
            _db.Permissions.SetMask(robot.Id, null,
                (int)(PermissionService.RobotDefaultMask | PermAction.ViewCollection));
        }

        return (secret, token);
    }

    /// <summary>Коллекция «Заказы»: «Номер» и «Сумма», две записи со значениями.</summary>
    private (int Id, int Number, int Total) Orders()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Заказы");
        var number     = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Номер");
        var total      = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Сумма",
                                             position: 2000);

        var first  = SampleData.AddRecord(_db.Constructor, collection.Id!.Value, "Молоток");
        var second = SampleData.AddRecord(_db.Constructor, collection.Id!.Value, "Гвозди",
                                          position: 2000);

        _db.Constructor.SetFieldValue(first.Id!.Value,  number.Id!.Value, "А-1");
        _db.Constructor.SetFieldValue(first.Id!.Value,  total.Id!.Value,  "100");
        _db.Constructor.SetFieldValue(second.Id!.Value, number.Id!.Value, "А-2");
        _db.Constructor.SetFieldValue(second.Id!.Value, total.Id!.Value,  "250");

        return (collection.Id!.Value, number.Id!.Value, total.Id!.Value);
    }

    private ReadGrant Grant(AccessToken token, int collectionId, params string[] keys)
    {
        var grant = Cards.ConfirmGrant(token.Id, collectionId, "Заказы",
                                       keys, "поля выдачи", Master, "Буров Р. В.", 90, Now,
                                       out var trouble);

        Assert.Equal("", trouble);
        return grant!;
    }

    private IngressAnswer Get(string path, string secret, string query,
                              DateTimeOffset? moment = null) =>
        IngressHandler.Handle(
            new IngressCall("GET", path, secret, "", "127.0.0.1", query),
            Cards, [_db.Manager], null, moment ?? Now);

    private IngressAnswer Read(string secret, string query = "collection=Заказы",
                               DateTimeOffset? moment = null) =>
        Get(IngressApi.RecordsPath, secret, query, moment);

    private static JsonElement Body(IngressAnswer answer) =>
        JsonDocument.Parse(answer.Body).RootElement;

    // ── Замок ─────────────────────────────────────────────────────────────────

    /// <summary>Маска есть, выдачи нет — и это НЕ то же самое, что «нет такого адреса»:
    /// человек должен узнать, что чтение существует, но его разрешают выдачей.</summary>
    [Fact]
    public void Without_a_grant_reading_is_refused()
    {
        Orders();
        var (secret, _) = IssueReader();

        var answer = Read(secret);

        Assert.Equal(403, answer.Status);
        Assert.Contains("выдачи нет", answer.Body, StringComparison.Ordinal);
    }

    /// <summary>Выдача есть, а маски чтения у робота нет — токен по умолчанию только пишет.</summary>
    [Fact]
    public void Without_the_read_mask_reading_is_refused()
    {
        var (id, number, _) = Orders();
        var (secret, token) = IssueReader(canRead: false);

        Grant(token, id, ReadGrant.TitleKey, number.ToString());

        Assert.Equal(403, Read(secret).Status);
    }

    [Fact]
    public void A_wrong_master_password_grants_nothing()
    {
        var (id, _, _) = Orders();
        var (_, token) = IssueReader();

        var grant = Cards.ConfirmGrant(token.Id, id, "Заказы", [ReadGrant.TitleKey], "Название",
                                       "не тот пароль", "Буров", 90, Now, out var trouble);

        Assert.Null(grant);
        Assert.Contains("не подошел", trouble, StringComparison.Ordinal);
    }

    [Fact]
    public void An_expired_grant_refuses_with_the_date()
    {
        var (id, number, _) = Orders();
        var (secret, token) = IssueReader();

        Grant(token, id, ReadGrant.TitleKey, number.ToString());

        var answer = Read(secret, moment: Now.AddDays(120));

        Assert.Equal(403, answer.Status);
        Assert.Contains("срок выдачи вышел", answer.Body, StringComparison.Ordinal);
    }

    /// <summary>Токен убрали — выдачи ушли вместе с ним, а не остались строками в паспорте.</summary>
    [Fact]
    public void Grants_leave_together_with_their_token()
    {
        var (id, _, _) = Orders();
        var (_, token) = IssueReader();

        Grant(token, id, ReadGrant.TitleKey);
        Cards.RemoveToken(token.Id);

        Assert.Empty(Cards.Grants());
    }

    // ── Чтение ────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_the_granted_fields_leave()
    {
        var (id, number, _) = Orders();
        var (secret, token) = IssueReader();

        Grant(token, id, ReadGrant.TitleKey, number.ToString());

        var answer = Read(secret);

        Assert.Equal(200, answer.Status);

        var rows = Body(answer).GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());

        var values = rows[0].GetProperty("values");

        Assert.Equal("Молоток", values.GetProperty("Название").GetString());
        Assert.Equal("А-1",     values.GetProperty("Номер").GetString());

        // «Сумма» в выдаче не названа — ее в ответе нет вовсе, ни у одной записи.
        Assert.DoesNotContain("Сумма", answer.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("100",   answer.Body, StringComparison.Ordinal);
    }

    /// <summary>Метка, поставленная ПОСЛЕ выдачи, останавливает чтение целиком — тем же вторым
    /// рубежом, что у исходящей привязки.</summary>
    [Fact]
    public void A_label_set_after_the_grant_stops_the_read()
    {
        var (id, number, _) = Orders();
        var (secret, token) = IssueReader();

        Grant(token, id, ReadGrant.TitleKey, number.ToString());

        Assert.Equal("", _db.Constructor.SetFieldLabel(number, ConfidentialityLabel.Restricted));

        var answer = Read(secret);

        Assert.Equal(403, answer.Status);
        Assert.Contains("Номер", answer.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cursor_pages_by_key()
    {
        var (id, number, _) = Orders();
        var (secret, token) = IssueReader();

        Grant(token, id, ReadGrant.TitleKey, number.ToString());

        var first = Read(secret, "collection=Заказы&limit=1");

        Assert.Equal(200, first.Status);
        Assert.Equal(1, Body(first).GetProperty("rows").GetArrayLength());

        var next = Body(first).GetProperty("next").GetInt32();

        var second = Read(secret, $"collection=Заказы&limit=1&after={next}");

        Assert.Equal(200, second.Status);
        Assert.Equal(1, Body(second).GetProperty("rows").GetArrayLength());

        // Страницы не пересекаются: вторая начинается ЗА ключом первой.
        Assert.True(Body(second).GetProperty("rows")[0].GetProperty("id").GetInt32() > next);
    }

    [Fact]
    public void Since_narrows_to_records_touched_after_the_moment()
    {
        var (id, number, _) = Orders();
        var (secret, token) = IssueReader();

        Grant(token, id, ReadGrant.TitleKey, number.ToString());

        // Момент в далеком будущем — не правился никто; в далеком прошлом — правились все.
        Assert.Equal(0, Body(Read(secret, "collection=Заказы&since=2199-01-01T00:00:00Z"))
                            .GetProperty("rows").GetArrayLength());
        Assert.Equal(2, Body(Read(secret, "collection=Заказы&since=2000-01-01T00:00:00Z"))
                            .GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public void Garbage_parameters_are_named_not_guessed()
    {
        var (id, number, _) = Orders();
        var (secret, token) = IssueReader();

        Grant(token, id, ReadGrant.TitleKey, number.ToString());

        Assert.Equal(400, Read(secret, "collection=Заказы&since=вчера").Status);
        Assert.Equal(400, Read(secret, "collection=Заказы&after=-5").Status);
        Assert.Equal(400, Read(secret, "").Status);
        Assert.Equal(404, Read(secret, "collection=Накладные").Status);
    }

    // ── Журналы и потолок ─────────────────────────────────────────────────────

    [Fact]
    public void Every_answer_leaves_a_security_trail_with_the_body_hash()
    {
        var (id, number, _) = Orders();
        var (secret, token) = IssueReader();

        Grant(token, id, ReadGrant.TitleKey, number.ToString());

        var answer = Read(secret);
        Assert.Equal(200, answer.Status);

        var trail = SecurityJournal.Current!.Page(
            new SecurityFilter(SecurityEvent.Read, BlockedOnly: false, Since: null), take: 10);

        var entry = Assert.Single(trail);

        Assert.True(entry.Allowed);
        Assert.Equal("Отчеты цеха", entry.Who);
        Assert.Equal(2, entry.Rows);
        Assert.True(entry.Bytes > 0);
        Assert.Equal(SecurityJournal.Sha256(answer.Body), entry.BodyHash);
    }

    [Fact]
    public void A_refused_read_is_journaled_too()
    {
        Orders();
        var (secret, _) = IssueReader();   // выдачи нет

        Assert.Equal(403, Read(secret).Status);

        var trail = SecurityJournal.Current!.Page(
            new SecurityFilter(SecurityEvent.Read, BlockedOnly: true, Since: null), take: 10);

        Assert.Single(trail);
    }

    /// <summary>Правило «не записалось — не отправляем» переносится на чтение дословно.</summary>
    [Fact]
    public void No_security_journal_means_no_data_out()
    {
        var (id, number, _) = Orders();
        var (secret, token) = IssueReader();

        Grant(token, id, ReadGrant.TitleKey, number.ToString());

        SecurityJournal.Attach(null);

        var answer = Read(secret);

        Assert.Equal(503, answer.Status);
        Assert.DoesNotContain("А-1", answer.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_daily_ceiling_stops_the_read()
    {
        var (id, number, _) = Orders();
        var (secret, token) = IssueReader();

        Grant(token, id, ReadGrant.TitleKey, number.ToString());

        // Потолок в десять байт: первый ответ проходит (израсходовано ноль) и занимает сотни,
        // второй упирается в выбранный потолок.
        Policy(maxBytes: 10);

        Assert.Equal(200, Read(secret).Status);

        var answer = Read(secret);

        Assert.Equal(429, answer.Status);
        Assert.Contains("потолок", answer.Body, StringComparison.Ordinal);
    }

    /// <summary>Расход выдачи не занимает потолки исходящих направлений: счет отдельный.</summary>
    [Fact]
    public void Read_bytes_do_not_eat_the_outgoing_quota()
    {
        var (id, number, _) = Orders();
        var (secret, token) = IssueReader();

        Grant(token, id, ReadGrant.TitleKey, number.ToString());

        Assert.Equal(200, Read(secret).Status);

        // Общий счет исходящих (Counts = 1) пуст: чтение в него не легло.
        Assert.Equal((0, 0L), RunJournal.Current!.Since(Now.AddDays(-1)));
    }

    // ── Журнал изменений ──────────────────────────────────────────────────────

    [Fact]
    public void Changes_answer_deletions_by_name()
    {
        var (id, number, _) = Orders();
        var (secret, token) = IssueReader();

        Grant(token, id, ReadGrant.TitleKey, number.ToString());

        var doomed = _db.Constructor.GetRecords(id).First(r => r.Title == "Гвозди");
        _db.Constructor.DeleteRecord(doomed.Id!.Value);

        var answer = Get(IngressApi.ChangesPath, secret, "collection=Заказы");

        Assert.Equal(200, answer.Status);

        var changes = Body(answer).GetProperty("changes").EnumerateArray().ToList();

        Assert.Contains(changes, c => c.GetProperty("action").GetString() == "delete"
                                   && c.GetProperty("name").GetString() == "Гвозди");
    }

    [Fact]
    public void Changes_are_locked_by_the_same_grant()
    {
        Orders();
        var (secret, _) = IssueReader();   // выдачи нет

        Assert.Equal(403, Get(IngressApi.ChangesPath, secret, "collection=Заказы").Status);
    }
}
