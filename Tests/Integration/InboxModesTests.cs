using System.Text.Json;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>Удаление и подготовка коллекций через HTTP API: атомарность, связи и повторная доставка.</summary>
[Collection(EgressCollection.Name)]
public sealed class InboxModesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("sbmodes_").FullName;
    private readonly TempDatabase _db = new(dbId: 7, name: "Основная");
    private readonly IngressReplies _replies;

    public InboxModesTests() => _replies = new IngressReplies(Path.Combine(_dir, "integrations.db"));
    public void Dispose()
    {
        _db.Dispose();
        try { SqliteGate.ClearAllPools(); } catch { /* временный стенд */ }
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

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

    private IngressAnswer Post(string body) => IngressHandler.Handle(
        new IngressCall("POST", IngressApi.InboxPath, body, "127.0.0.1"), [_db.Manager], _replies, Now);
    private static JsonElement Body(IngressAnswer answer) => JsonDocument.Parse(answer.Body).RootElement;

    [Fact]
    public void Delete_finds_by_key_and_counts_what_it_removed()
    {
        var (id, _) = Orders();

        var answer = Post("""
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

    [Fact]
    public void Deletions_are_signed_by_the_api_source_in_the_change_log()
    {
        Orders();

        Post("""
            {"collection":"Заказы","mode":"delete","key":"Номер","rows":[{"Номер":"А-2"}]}
            """);

        Assert.Contains(_db.Context.ChangeLog.ToList(),
                        row => row.Action == "delete"
                            && row.TargetName == "Заказ 2"
                            && row.UserName == "Обмен по API");
    }

    [Fact]
    public void An_ambiguous_key_deletes_nothing()
    {
        var (id, number) = Orders();

        var twin = SampleData.AddRecord(_db.Constructor, id, "Двойник", 9000);
        _db.Constructor.SetFieldValue(twin.Id!.Value, number, "А-1");


        var answer = Post("""
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

        var answer = Post("""
            {"collection":"Заказы","mode":"delete","key":"Название",
             "rows":[{"Название":"Заказ 2"}]}
            """);

        Assert.Equal(200, answer.Status);
        Assert.Equal(1, Body(answer).GetProperty("deleted").GetInt32());
        Assert.DoesNotContain(_db.Constructor.GetRecords(id), r => r.Title == "Заказ 2");
    }

    [Fact]
    public void A_repeated_delete_returns_the_same_answer_and_removes_nothing_more()
    {
        var (id, _) = Orders();

        var message = """
            {"collection":"Заказы","mode":"delete","key":"Номер","id":"wipe-1",
             "rows":[{"Номер":"А-1"}]}
            """;

        var first  = Post(message);
        var second = Post(message);

        Assert.Equal(first.Body, second.Body);
        Assert.Equal(2, _db.Constructor.GetRecords(id).Count);
    }

    [Fact]
    public void A_delete_with_an_unknown_key_column_is_refused_with_words()
    {
        Orders();

        var answer = Post("""
            {"collection":"Заказы","mode":"delete","key":"Артикул","rows":[{"Артикул":"7"}]}
            """);

        Assert.Equal(422, answer.Status);
        Assert.Contains("Артикул", answer.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_restricted_link_refuses_the_whole_delete_message()
    {
        var (id, _) = Orders();
        var records = _db.Constructor.GetRecords(id);
        var invoices = SampleData.AddCollection(_db.Constructor, "Счета").Id!.Value;
        var invoice  = SampleData.AddRecord(_db.Constructor, invoices, "Счет 1").Id!.Value;
        var order    = SampleData.AddField(_db.Constructor, invoices, "Заказ", "relation");
        order.ConfigJson = new RelationConfig(id, true, RestrictDelete: true).ToConfigJson();
        _db.Constructor.UpdateField(order);
        _db.Constructor.SetLinks(order.Id!.Value, invoice, [records.Single(r => r.Title == "Заказ 1").Id!.Value]);

        var answer = Post("""
            {"collection":"Заказы","mode":"delete","key":"Номер","rows":[{"Номер":"А-2"},{"Номер":"А-1"}]}
            """);

        Assert.Equal(422, answer.Status);
        Assert.Contains("«Счета · Заказ» (1 запись)", answer.Body, StringComparison.Ordinal);
        Assert.Equal(3, _db.Constructor.GetRecords(id).Count);
    }

    [Fact]
    public void Links_between_records_of_one_delete_message_do_not_block_it()
    {
        var (id, _) = Orders();
        var records = _db.Constructor.GetRecords(id);
        var parent = SampleData.AddField(_db.Constructor, id, "Основной заказ", "relation");
        parent.ConfigJson = new RelationConfig(id, true, RestrictDelete: true).ToConfigJson();
        _db.Constructor.UpdateField(parent);
        // А-3 ссылается на А-2, а в сообщении А-2 идет раньше А-3.
        _db.Constructor.SetLinks(parent.Id!.Value, records.Single(r => r.Title == "Заказ 3").Id!.Value,
            [records.Single(r => r.Title == "Заказ 2").Id!.Value]);

        var answer = Post("""
            {"collection":"Заказы","mode":"delete","key":"Номер","rows":[{"Номер":"А-1"},{"Номер":"А-2"},{"Номер":"А-3"}]}
            """);

        Assert.Equal(200, answer.Status);
        Assert.Equal(3, Body(answer).GetProperty("deleted").GetInt32());
        Assert.Empty(_db.Constructor.GetRecords(id));
    }

    [Fact]
    public void Ensure_creates_the_collection_with_its_fields_once()
    {

        var message = """
            {"collection":"Поставки","mode":"ensure-collection",
             "fields":[{"name":"Номер","type":"text"},{"name":"Сумма","type":"number"}]}
            """;

        var first = Post(message);

        Assert.Equal(200, first.Status);
        Assert.Contains("создана", first.Body, StringComparison.Ordinal);

        var collection = _db.Constructor.GetAllCollections().Single(c => c.Name == "Поставки");
        var fields     = _db.Constructor.GetFields(collection.Id!.Value);

        Assert.Equal(["Номер", "Сумма"], fields.Select(f => f.Name).ToArray());
        Assert.Equal("number", fields.Single(f => f.Name == "Сумма").FieldType);

        // Повтор находит все готовым — в этом весь смысл «ensure»: сторонняя программа шлет его
        // при каждом своем запуске, не проверяя ничего.
        var second = Post(message);

        Assert.Equal(200, second.Status);
        Assert.Contains("уже есть", second.Body, StringComparison.Ordinal);
        Assert.Equal(2, _db.Constructor.GetFields(collection.Id!.Value).Count);
    }

    [Fact]
    public void Ensure_adds_only_the_missing_fields_to_an_existing_collection()
    {
        var (id, _) = Orders();

        var answer = Post("""
            {"collection":"Заказы","mode":"ensure-collection",
             "fields":[{"name":"Номер","type":"text"},{"name":"Срочно","type":"checkbox"}]}
            """);

        Assert.Equal(200, answer.Status);
        Assert.Contains("Срочно", answer.Body, StringComparison.Ordinal);

        var fields = _db.Constructor.GetFields(id);
        Assert.Equal(2, fields.Count);
        Assert.Equal("checkbox", fields.Single(f => f.Name == "Срочно").FieldType);
    }

    [Fact]
    public void A_type_conflict_refuses_everything_and_touches_nothing()
    {
        var (id, _) = Orders();

        var answer = Post("""
            {"collection":"Заказы","mode":"ensure-collection",
             "fields":[{"name":"Номер","type":"number"},{"name":"Срочно","type":"checkbox"}]}
            """);

        Assert.Equal(422, answer.Status);
        Assert.Contains("не совпали", answer.Body, StringComparison.Ordinal);

        var fields = _db.Constructor.GetFields(id);
        Assert.Equal("Номер", Assert.Single(fields).Name);
        Assert.Equal("text", fields[0].FieldType);   // существующее поле не перекроено
    }

    [Fact]
    public void An_unknown_mode_is_refused_and_nothing_is_written()
    {
        var (id, _) = Orders();

        var answer = Post("""
            {"collection":"Заказы","mode":"merge","id":"m1","rows":[{"Номер":"А-9"}]}
            """);

        Assert.Equal(400, answer.Status);
        Assert.Contains("merge", answer.Body, StringComparison.Ordinal);
        Assert.Equal(3, _db.Constructor.GetRecords(id).Count);
    }
    [Fact]
    public void An_ambiguous_collection_name_does_not_choose_the_first_for_inbox_writes()
    {
        var (id, _) = Orders();
        var group = _db.Constructor.AddGroup(new DbGroup { Name = "Другая группа" }).Id!.Value;
        var other = SampleData.AddCollection(_db.Constructor, "Заказы", group).Id!.Value;
        var answer = Post("""
            {"collection":"Заказы","mode":"add","id":"ambiguous","rows":[{"Название":"Не записывать"}]}
            """);
        Assert.Equal(422, answer.Status);
        Assert.Contains("несколько коллекций", answer.Body, StringComparison.Ordinal);
        Assert.Equal(3, _db.Constructor.GetRecords(id).Count);
        Assert.Empty(_db.Constructor.GetRecords(other));
    }

    [Fact]
    public void A_read_only_collection_key_is_not_silently_ignored_for_inbox_writes()
    {
        var (id, _) = Orders();
        var call = new IngressCall("POST", IngressApi.InboxPath, """
            {"collection":"Заказы","mode":"add","id":"unsupported-selector","rows":[{"Название":"Не записывать"}]}
            """, Query: $"collectionId={id}");
        var answer = IngressHandler.Handle(call, [_db.Manager], _replies, Now);
        Assert.Equal(400, answer.Status);
        Assert.Contains("только при чтении", answer.Body, StringComparison.Ordinal);
        Assert.Equal(3, _db.Constructor.GetRecords(id).Count);
    }

}
