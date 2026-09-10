using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Приемник целиком (<see cref="InboxReceiver"/>): файл из папки становится записями коллекции.
///
/// Проверяется то, ради чего канал и существует: повторная доставка не плодит дубли, отправитель
/// на любой исход получает квитанцию, а выброшенные колонки в ней названы. Молчание в ответ на
/// присланный файл — худшее, чем может кончиться обмен, и половина проверок здесь именно об этом.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class InboxReceiverTests : IDisposable
{
    private readonly string       _root = Path.Combine(Path.GetTempPath(), $"sbinbox_{Guid.NewGuid():N}");
    private readonly TempDatabase _db   = new();

    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        EgressGate.Close();
        _db.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* временная папка */ }
    }

    private InboxLayout Layout()
    {
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal, [], [], [_root], 1000, 1024 * 1024, 100, null, false, "тест")));

        var layout = InboxFolder.Open(_root, out var explanation);
        Assert.NotNull(layout);
        Assert.Equal("", explanation);
        return layout!;
    }

    /// <summary>Коллекция «Заказы» со свойствами «Номер» и «Статус».</summary>
    private (int Id, int Number, int State) Orders()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Заказы");
        var number     = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Номер");
        var state      = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Статус", position: 2000);

        return (collection.Id!.Value, number.Id!.Value, state.Id!.Value);
    }

    /// <summary>Положить файл во «Входящие» так, чтобы приемник счел его готовым.</summary>
    private static void Put(InboxLayout layout, string name, string text)
    {
        var path = Path.Combine(layout.Incoming, name);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, (Now - TimeSpan.FromMinutes(1)).UtcDateTime);
    }

    private InboxRun Run(InboxLayout layout) =>
        InboxReceiver.Run(layout, _db.Constructor, dbId: 1, machine: "ПК17", now: Now);

    private static string Message(string collection, string mode, string key, string id, string rows) =>
        $$"""
        { "collection": {{Json(collection)}}, "mode": {{Json(mode)}},
          "key": {{Json(key)}}, "id": {{Json(id)}}, "rows": {{rows}} }
        """;

    private static string Json(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    private string Value(int recordId, int fieldId) =>
        _db.Constructor.GetFieldValues(recordId).TryGetValue(fieldId, out var value) ? value : "";

    // ── Обычный прием ─────────────────────────────────────────────────────────

    [Fact]
    public void File_becomes_records_in_the_named_collection()
    {
        var layout = Layout();
        var (id, number, state) = Orders();

        Put(layout, "заказы.json", Message("Заказы", "add", "", "m1", """
            [ { "Название": "Молоток",  "Номер": "А-1", "Статус": "новый" },
              { "Название": "Отвертка", "Номер": "А-2", "Статус": "в работе" } ]
            """));

        var run = Run(layout);

        Assert.Equal(1, run.Accepted);
        Assert.Equal(0, run.Rejected);

        var records = _db.Constructor.GetRecords(id);
        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.Title == "Молоток");
        Assert.Equal("А-1", Value(records.First(r => r.Title == "Молоток").Id!.Value, number));
        Assert.Equal("в работе", Value(records.First(r => r.Title == "Отвертка").Id!.Value, state));
    }

    [Fact]
    public void Accepted_file_leaves_incoming_and_gets_a_receipt()
    {
        var layout = Layout();
        Orders();

        Put(layout, "заказы.json", Message("Заказы", "add", "", "m1",
            """[ { "Название": "Молоток", "Номер": "А-1" } ]"""));

        Run(layout);

        Assert.Empty(Directory.EnumerateFiles(layout.Incoming));
        Assert.True(File.Exists(Path.Combine(layout.Processed, "2026-09", "заказы.json")));
        Assert.True(File.Exists(Path.Combine(layout.Receipts, "m1.json")));

        var receipt = InboxReceipt.TryParse(File.ReadAllText(Path.Combine(layout.Receipts, "m1.json")));
        Assert.True(receipt!.Accepted);
        Assert.Equal(1, receipt.Received);
        Assert.Equal(1, receipt.Created);
    }

    /// <summary>
    /// То, ради чего у сообщения есть ключ сопоставления: вторая доставка правит те же записи,
    /// а не заводит вторые.
    /// </summary>
    [Fact]
    public void Second_delivery_updates_instead_of_duplicating()
    {
        var layout = Layout();
        var (id, _, state) = Orders();

        Put(layout, "заказы.json", Message("Заказы", "update", "Номер", "m1",
            """[ { "Название": "Молоток", "Номер": "А-1", "Статус": "новый" } ]"""));
        Run(layout);

        Put(layout, "заказы-2.json", Message("Заказы", "update", "Номер", "m2",
            """[ { "Название": "Молоток", "Номер": "А-1", "Статус": "закрыт" } ]"""));
        var second = Run(layout);

        Assert.Equal(1, second.Accepted);

        var records = _db.Constructor.GetRecords(id);
        Assert.Single(records);
        Assert.Equal("закрыт", Value(records[0].Id!.Value, state));
    }

    /// <summary>
    /// Отправитель при обрыве пришлет ТОТ ЖЕ файл дважды. Прежняя квитанция не переписывается:
    /// перечитав ее, он обязан получить тот же ответ, что и в первый раз.
    /// </summary>
    [Fact]
    public void Repeat_of_the_same_message_is_not_applied_twice()
    {
        var layout = Layout();
        var (id, _, _) = Orders();

        var text = Message("Заказы", "add", "", "m1",
            """[ { "Название": "Молоток", "Номер": "А-1" } ]""");

        Put(layout, "заказы.json", text);
        Run(layout);

        Put(layout, "заказы-повтор.json", text);
        var second = Run(layout);

        Assert.Equal(1, second.Repeats);
        Assert.Equal(0, second.Accepted);
        Assert.Single(_db.Constructor.GetRecords(id));

        // Файл повтора убран из «Входящих», иначе каждый обход спотыкался бы о него заново.
        Assert.Empty(Directory.EnumerateFiles(layout.Incoming));

        var receipt = InboxReceipt.TryParse(File.ReadAllText(Path.Combine(layout.Receipts, "m1.json")));
        Assert.Equal("заказы.json", receipt!.File);   // квитанция первой доставки, не второй
    }

    // ── Отказы ────────────────────────────────────────────────────────────────

    [Fact]
    public void Unknown_collection_is_rejected_with_a_reason()
    {
        var layout = Layout();
        Orders();

        Put(layout, "склад.json", Message("Склад", "add", "", "m1",
            """[ { "Название": "Молоток" } ]"""));

        var run = Run(layout);

        Assert.Equal(1, run.Rejected);
        Assert.Contains("не найдена", run.Receipts[0].Explanation, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(layout.Failed, "склад.json")));
    }

    /// <summary>
    /// Две коллекции с одним именем — обычное дело в большой базе. Выбрать любую значило бы
    /// однажды уложить заказы в чужую таблицу и не сказать об этом никому.
    /// </summary>
    [Fact]
    public void Ambiguous_collection_name_is_rejected()
    {
        var layout = Layout();
        Orders();
        SampleData.AddCollection(_db.Constructor, "Заказы");

        Put(layout, "заказы.json", Message("Заказы", "add", "", "m1",
            """[ { "Название": "Молоток" } ]"""));

        var run = Run(layout);

        Assert.Equal(1, run.Rejected);
        Assert.Contains("несколько коллекций", run.Receipts[0].Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Broken_file_gets_a_receipt_and_goes_to_errors()
    {
        var layout = Layout();
        Orders();

        Put(layout, "битый.json", "{ это не json");

        var run = Run(layout);

        Assert.Equal(1, run.Rejected);
        Assert.Contains("не является JSON", run.Receipts[0].Explanation, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(layout.Failed, "битый.json")));
        Assert.True(File.Exists(Path.Combine(layout.Receipts, "битый.result.json")));
    }

    [Fact]
    public void Update_without_a_key_is_rejected()
    {
        var layout = Layout();
        Orders();

        Put(layout, "заказы.json", Message("Заказы", "update", "", "m1",
            """[ { "Название": "Молоток", "Номер": "А-1" } ]"""));

        var run = Run(layout);

        Assert.Equal(1, run.Rejected);
        Assert.Contains("update", run.Receipts[0].Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Key_that_is_not_a_column_of_the_collection_is_rejected()
    {
        var layout = Layout();
        Orders();

        Put(layout, "заказы.json", Message("Заказы", "update", "Артикул", "m1",
            """[ { "Название": "Молоток", "Артикул": "А-1" } ]"""));

        var run = Run(layout);

        Assert.Equal(1, run.Rejected);
        Assert.Contains("Артикул", run.Receipts[0].Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void File_with_no_matching_columns_at_all_is_rejected()
    {
        var layout = Layout();
        Orders();

        Put(layout, "заказы.json", Message("Заказы", "add", "", "m1",
            """[ { "Цвет": "красный", "Вес": "10" } ]"""));

        var run = Run(layout);

        Assert.Equal(1, run.Rejected);
        Assert.Contains("ни одна колонка", run.Receipts[0].Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Лишнее чужая программа отдает почти всегда, и это не ошибка. Но молча выбросить
    /// присланное нельзя: отправитель годами будет думать, что колонка доходит.
    /// </summary>
    [Fact]
    public void Columns_with_no_place_are_named_in_the_receipt()
    {
        var layout = Layout();
        var (id, number, _) = Orders();

        Put(layout, "заказы.json", Message("Заказы", "add", "", "m1", """
            [ { "Название": "Молоток", "Номер": "А-1", "Цвет": "красный", "Вес": "10" } ]
            """));

        var run = Run(layout);

        Assert.Equal(1, run.Accepted);
        Assert.Contains("Цвет", run.Receipts[0].Explanation, StringComparison.Ordinal);
        Assert.Contains("Вес",  run.Receipts[0].Explanation, StringComparison.Ordinal);

        // А то, чему место нашлось, уложено.
        var record = _db.Constructor.GetRecords(id).Single();
        Assert.Equal("Молоток", record.Title);
        Assert.Equal("А-1", Value(record.Id!.Value, number));
    }

    // ── Соседи по папке ───────────────────────────────────────────────────────

    /// <summary>Файл, уже захваченный соседом, не трогается: без единой записи, молча.</summary>
    [Fact]
    public void File_claimed_by_a_neighbour_is_left_alone()
    {
        var layout = Layout();
        Orders();

        var name = "заказы.json";
        Put(layout, name, Message("Заказы", "add", "", "m1", """[ { "Название": "Молоток" } ]"""));

        var claimed = InboxFolder.TryClaim(
            new InboxFile(Path.Combine(layout.Incoming, name), name), "ПК18");

        Assert.NotNull(claimed);

        var run = Run(layout);

        Assert.Equal(0, run.Files);
        Assert.Empty(_db.Constructor.GetAllCollections()
                        .SelectMany(c => _db.Constructor.GetRecords(c.Id!.Value)));
    }

    /// <summary>
    /// Выключенная посреди обмена машина иначе унесла бы файл с собой навсегда. Возвращенный
    /// захват разбирается тем же обходом, что его вернул.
    /// </summary>
    [Fact]
    public void Abandoned_claim_returns_and_is_processed()
    {
        var layout = Layout();
        var (id, _, _) = Orders();

        var name = "заказы.json";
        Put(layout, name, Message("Заказы", "add", "", "m1", """[ { "Название": "Молоток" } ]"""));

        var claimed = InboxFolder.TryClaim(
            new InboxFile(Path.Combine(layout.Incoming, name), name), "ПК18")!;

        File.SetLastWriteTimeUtc(claimed, (Now - TimeSpan.FromDays(1)).UtcDateTime);

        var run = Run(layout);

        Assert.Equal(1, run.Reclaimed);
        Assert.Equal(1, run.Accepted);
        Assert.Single(_db.Constructor.GetRecords(id));
    }

    [Fact]
    public void Empty_folder_says_so_without_touching_anything()
    {
        var layout = Layout();
        Orders();

        var run = Run(layout);

        Assert.Equal(0, run.Files);
        Assert.Equal("новых файлов нет", run.Summary());
    }

    /// <summary>Порядок разбора — по времени записи: правки одной записи приходят подряд.</summary>
    [Fact]
    public void Files_are_processed_in_the_order_they_were_written()
    {
        var layout = Layout();
        var (id, _, state) = Orders();

        var first = Path.Combine(layout.Incoming, "второй.json");
        File.WriteAllText(first, Message("Заказы", "update", "Номер", "m2",
            """[ { "Название": "Молоток", "Номер": "А-1", "Статус": "закрыт" } ]"""));
        File.SetLastWriteTimeUtc(first, (Now - TimeSpan.FromMinutes(1)).UtcDateTime);

        var second = Path.Combine(layout.Incoming, "первый.json");
        File.WriteAllText(second, Message("Заказы", "update", "Номер", "m1",
            """[ { "Название": "Молоток", "Номер": "А-1", "Статус": "новый" } ]"""));
        File.SetLastWriteTimeUtc(second, (Now - TimeSpan.FromMinutes(5)).UtcDateTime);

        var run = Run(layout);

        Assert.Equal(2, run.Accepted);
        Assert.Equal("закрыт", Value(_db.Constructor.GetRecords(id).Single().Id!.Value, state));
    }
}
