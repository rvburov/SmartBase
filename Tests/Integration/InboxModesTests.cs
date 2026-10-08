using System.Text.Json;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Режимы сообщения «delete» и «ensure-collection» (docs/ROADMAP-API.md, шаг 11) — через
/// сетевую дверь, потому что именно там стоят маски робота. Укладка у всех трех каналов одна
/// (<see cref="InboxIntake"/>), так что проверенное здесь верно и для папки, и для командной
/// строки; у тех двух свои тесты на транспорт.
///
/// Проверяется то, чего эти режимы не должны делать никогда:
///
/// <list type="bullet">
///   <item>робот со стандартной маской «только запись» не удаляет и не заводит коллекций;</item>
///   <item>удаление не трогает ничего при неоднозначном ключе;</item>
///   <item>подготовка коллекции не переделывает существующие поля — конфликт типов
///   отказывает ЦЕЛИКОМ, до первого созданного поля;</item>
///   <item>незнакомое слово режима — отказ, а не тихое «добавить».</item>
/// </list>
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class InboxModesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly string           _dir;
    private readonly TempDatabase     _db = new(dbId: 7, name: "Основная");
    private readonly DatabaseRegistry _registry;
    private readonly IngressReplies   _replies;

    public InboxModesTests()
    {
        _dir      = Directory.CreateTempSubdirectory("sbmodes_").FullName;
        _registry = DatabaseRegistry.Open(Path.Combine(_dir, "default.db"),
                                          Path.Combine(_dir, "app.db"));
        _replies  = new IngressReplies(Path.Combine(_dir, "integrations.db"));
    }

    public void Dispose()
    {
        _registry.Dispose();
        _db.Dispose();

        try { SqliteGate.ClearAllPools(); } catch { /* best-effort */ }
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    // ── Обвязка ───────────────────────────────────────────────────────────────

    private IntegrationsRegistry Cards => _registry.Integrations;

    /// <summary>Токен с роботом; маска — стандартная плюс перечисленные права.</summary>
    private string Issue(params PermAction[] extra)
    {
        var (token, secret) = Cards.Issue("Обмен учетной системы", 7, "ПК17", Now);
        _db.Permissions.EnsureRobot(token.RobotKey, token.Name, Now);

        if (extra.Length > 0)
        {
            var mask  = extra.Aggregate(PermissionService.RobotDefaultMask, (m, a) => m | a);
            var robot = _db.Context.DbUsers.First(u => u.UserKey == token.RobotKey);
            _db.Permissions.SetMask(robot.Id, null, (int)mask);
        }

        return secret;
    }

    /// <summary>«Заказы» с «Номером» и три записи: А-1, А-2, А-3.</summary>
    private (int Id, int Number) Orders()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Заказы");
        var number     = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Номер");

        foreach (var n in new[] { 1, 2, 3 })
        {
            var record = SampleData.AddRecord(_db.Constructor, collection.Id!.Value,
                                              $"Заказ {n}", n * 1000);
            _db.Constructor.SetFieldValue(record.Id!.Value, number.Id!.Value, $"А-{n}");
        }

        return (collection.Id!.Value, number.Id!.Value);
    }

    private IngressAnswer Post(string token, string body) =>
        IngressHandler.Handle(
            new IngressCall("POST", IngressApi.InboxPath, token, body, "127.0.0.1"),
            Cards, [_db.Manager], _replies, Now);

    private static JsonElement Body(IngressAnswer answer) =>
        JsonDocument.Parse(answer.Body).RootElement;

    // ── Удаление: маска ───────────────────────────────────────────────────────

    /// <summary>Стандартная маска робота — только запись: удаление не проходит, и отказ
    /// называет право, за которым идти к администратору.</summary>
    [Fact]
    public void A_default_robot_cannot_delete()
    {
        var (id, _) = Orders();
        var secret  = Issue();

        var answer = Post(secret, """
            {"collection":"Заказы","mode":"delete","key":"Номер","rows":[{"Номер":"А-1"}]}
            """);

        Assert.Equal(403, answer.Status);
        Assert.Contains("Удаление записи", answer.Body, StringComparison.Ordinal);
        Assert.Equal(3, _db.Constructor.GetRecords(id).Count);
    }

    // ── Удаление: работа ──────────────────────────────────────────────────────

    [Fact]
    public void Delete_finds_by_key_and_counts_what_it_removed()
    {
        var (id, _) = Orders();
        var secret  = Issue(PermAction.DeleteRecord);

        var answer = Post(secret, """
            {"collection":"Заказы","mode":"delete","key":"Номер",
             "rows":[{"Номер":"А-1"},{"Номер":"А-3"},{"Номер":"Б-9"}]}
            """);

        Assert.Equal(200, answer.Status);

        var body = Body(answer);
        Assert.Equal(2, body.GetProperty("deleted").GetInt32());
        Assert.Equal(1, body.GetProperty("skipped").GetInt32());   // Б-9 не было — и не беда

        var left = _db.Constructor.GetRecords(id);
        Assert.Equal("Заказ 2", Assert.Single(left).Title);
    }

    /// <summary>Удаленное попадает в журнал изменений с именем робота — это тот самый след,
    /// который потом отдает <c>/v1/changes</c> и по которому отзывают доступ.</summary>
    [Fact]
    public void Deletions_are_signed_by_the_robot_in_the_change_log()
    {
        Orders();
        var secret = Issue(PermAction.DeleteRecord);

        Post(secret, """
            {"collection":"Заказы","mode":"delete","key":"Номер","rows":[{"Номер":"А-2"}]}
            """);

        Assert.Contains(_db.Context.ChangeLog.ToList(),
                        row => row.Action == "delete"
                            && row.TargetName == "Заказ 2"
                            && row.UserName == "Обмен учетной системы");
    }

    /// <summary>Две записи с одним ключом — не тронута ни одна: удалять наугад одну из
    /// одинаковых хуже, чем отказаться и сказать об этом.</summary>
    [Fact]
    public void An_ambiguous_key_deletes_nothing()
    {
        var (id, number) = Orders();

        var twin = SampleData.AddRecord(_db.Constructor, id, "Двойник", 9000);
        _db.Constructor.SetFieldValue(twin.Id!.Value, number, "А-1");

        var secret = Issue(PermAction.DeleteRecord);

        var answer = Post(secret, """
            {"collection":"Заказы","mode":"delete","key":"Номер","rows":[{"Номер":"А-1"}]}
            """);

        Assert.Equal(200, answer.Status);
        Assert.Equal(1, Body(answer).GetProperty("ambiguous").GetInt32());
        Assert.Equal(0, Body(answer).GetProperty("deleted").GetInt32());
        Assert.Equal(4, _db.Constructor.GetRecords(id).Count);
    }

    [Fact]
    public void Delete_matches_by_the_record_title_too()
    {
        var (id, _) = Orders();
        var secret  = Issue(PermAction.DeleteRecord);

        var answer = Post(secret, """
            {"collection":"Заказы","mode":"delete","key":"Название",
             "rows":[{"Название":"Заказ 2"}]}
            """);

        Assert.Equal(200, answer.Status);
        Assert.Equal(1, Body(answer).GetProperty("deleted").GetInt32());
        Assert.DoesNotContain(_db.Constructor.GetRecords(id), r => r.Title == "Заказ 2");
    }

    /// <summary>Повтор с тем же «id» возвращает ТОТ ЖЕ ответ и не удаляет больше ничего.</summary>
    [Fact]
    public void A_repeated_delete_returns_the_same_answer_and_removes_nothing_more()
    {
        var (id, _) = Orders();
        var secret  = Issue(PermAction.DeleteRecord);

        var message = """
            {"collection":"Заказы","mode":"delete","key":"Номер","id":"wipe-1",
             "rows":[{"Номер":"А-1"}]}
            """;

        var first  = Post(secret, message);
        var second = Post(secret, message);

        Assert.Equal(first.Body, second.Body);
        Assert.Equal(2, _db.Constructor.GetRecords(id).Count);
    }

    [Fact]
    public void A_delete_with_an_unknown_key_column_is_refused_with_words()
    {
        Orders();
        var secret = Issue(PermAction.DeleteRecord);

        var answer = Post(secret, """
            {"collection":"Заказы","mode":"delete","key":"Артикул","rows":[{"Артикул":"7"}]}
            """);

        Assert.Equal(422, answer.Status);
        Assert.Contains("Артикул", answer.Body, StringComparison.Ordinal);
    }

    // ── Подготовка коллекции: маска ───────────────────────────────────────────

    [Fact]
    public void A_default_robot_cannot_prepare_collections()
    {
        var secret = Issue();

        var answer = Post(secret, """
            {"collection":"Поставки","mode":"ensure-collection",
             "fields":[{"name":"Номер","type":"text"}]}
            """);

        Assert.Equal(403, answer.Status);
        Assert.Contains("Создание коллекции", answer.Body, StringComparison.Ordinal);
    }

    // ── Подготовка коллекции: работа ──────────────────────────────────────────

    [Fact]
    public void Ensure_creates_the_collection_with_its_fields_once()
    {
        var secret = Issue(PermAction.CreateCollection, PermAction.CreateField);

        var message = """
            {"collection":"Поставки","mode":"ensure-collection",
             "fields":[{"name":"Номер","type":"text"},{"name":"Сумма","type":"number"}]}
            """;

        var first = Post(secret, message);

        Assert.Equal(200, first.Status);
        Assert.Contains("создана", first.Body, StringComparison.Ordinal);

        var collection = _db.Constructor.GetAllCollections().Single(c => c.Name == "Поставки");
        var fields     = _db.Constructor.GetFields(collection.Id!.Value);

        Assert.Equal(["Номер", "Сумма"], fields.Select(f => f.Name).ToArray());
        Assert.Equal("number", fields.Single(f => f.Name == "Сумма").FieldType);

        // Повтор находит все готовым — в этом весь смысл «ensure»: сторонняя программа шлет его
        // при каждом своем запуске, не проверяя ничего.
        var second = Post(secret, message);

        Assert.Equal(200, second.Status);
        Assert.Contains("уже есть", second.Body, StringComparison.Ordinal);
        Assert.Equal(2, _db.Constructor.GetFields(collection.Id!.Value).Count);
    }

    [Fact]
    public void Ensure_adds_only_the_missing_fields_to_an_existing_collection()
    {
        var (id, _) = Orders();
        var secret  = Issue(PermAction.CreateCollection, PermAction.CreateField);

        var answer = Post(secret, """
            {"collection":"Заказы","mode":"ensure-collection",
             "fields":[{"name":"Номер","type":"text"},{"name":"Срочно","type":"checkbox"}]}
            """);

        Assert.Equal(200, answer.Status);
        Assert.Contains("Срочно", answer.Body, StringComparison.Ordinal);

        var fields = _db.Constructor.GetFields(id);
        Assert.Equal(2, fields.Count);
        Assert.Equal("checkbox", fields.Single(f => f.Name == "Срочно").FieldType);
    }

    /// <summary>Конфликт типов отказывает ЦЕЛИКОМ: ни одно поле не создается, существующее не
    /// трогается — коллекция не должна оставаться в состоянии, которого не просил никто.</summary>
    [Fact]
    public void A_type_conflict_refuses_everything_and_touches_nothing()
    {
        var (id, _) = Orders();
        var secret  = Issue(PermAction.CreateCollection, PermAction.CreateField);

        var answer = Post(secret, """
            {"collection":"Заказы","mode":"ensure-collection",
             "fields":[{"name":"Номер","type":"number"},{"name":"Срочно","type":"checkbox"}]}
            """);

        Assert.Equal(422, answer.Status);
        Assert.Contains("не совпали", answer.Body, StringComparison.Ordinal);

        var fields = _db.Constructor.GetFields(id);
        Assert.Equal("Номер", Assert.Single(fields).Name);
        Assert.Equal("text", fields[0].FieldType);   // существующее поле не перекроено
    }

    // ── Незнакомый режим ──────────────────────────────────────────────────────

    /// <summary>Незнакомое слово режима — отказ, а не тихое «добавить»: прочесть чужое
    /// намерение не тем действием хуже, чем сказать «приемник старее вашего формата».</summary>
    [Fact]
    public void An_unknown_mode_is_refused_and_nothing_is_written()
    {
        var (id, _) = Orders();
        var secret  = Issue();

        var answer = Post(secret, """
            {"collection":"Заказы","mode":"merge","id":"m1","rows":[{"Номер":"А-9"}]}
            """);

        Assert.Equal(400, answer.Status);
        Assert.Contains("merge", answer.Body, StringComparison.Ordinal);
        Assert.Equal(3, _db.Constructor.GetRecords(id).Count);
    }
}
