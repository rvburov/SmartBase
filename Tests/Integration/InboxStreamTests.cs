using System.Text;
using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Потоковый прием файла: конверт читается токен за токеном, записи остаются в файле и читаются
/// оттуда потоковым источником импорта.
///
/// <b>Ради чего это написано.</b> Раньше входящий файл читался в строку целиком и разбирался
/// поверх нее, поэтому один файл стоил нескольких своих размеров памяти — отсюда и брался потолок
/// в 32 МБ. Потолок был честной защитой от нехватки памяти, но упирался в него не злоумышленник, а
/// обычная большая выгрузка. Теперь память не зависит от размера файла, и потолка у этого канала
/// нет вовсе.
///
/// Разбор при этом ОДИН на оба транспорта: тело HTTP-обращения и файл идут через один и тот же код
/// (<c>InboxMessage.Read</c>), и проверка <see cref="Both_ways_in_understand_the_same_message"/>
/// сторожит именно это — второе понимание формата разошлось бы с первым на первой же правке.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class InboxStreamTests : IDisposable
{
    private readonly string       _root = Path.Combine(Path.GetTempPath(), $"sbstream_{Guid.NewGuid():N}");
    private readonly TempDatabase _db   = new();

    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

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
        return layout!;
    }

    private (int Id, int Number) Orders()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Заказы");
        var number     = SampleData.AddField(_db.Constructor, collection.Id!.Value, "Номер");

        return (collection.Id!.Value, number.Id!.Value);
    }

    /// <summary>Положить файл во «Входящие» так, чтобы приемник счел его готовым.</summary>
    private static string Put(InboxLayout layout, string name, string text)
    {
        var path = Path.Combine(layout.Incoming, name);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, (Now - TimeSpan.FromMinutes(1)).UtcDateTime);
        return path;
    }

    /// <summary>Одинокий файл — без папок обмена: разбору конверта они не нужны.</summary>
    private string Loose(string name, string text)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    // ── Разбор конверта из файла ──────────────────────────────────────────────

    /// <summary>Обычный конверт: поля прочитаны, записи оставлены в файле, счет верен.</summary>
    [Fact]
    public void An_envelope_is_read_and_the_rows_stay_in_the_file()
    {
        var path = Loose("заказы.json", """
            { "collection": "Заказы", "mode": "update", "key": "Номер", "id": "m-7",
              "rows": [ { "Номер": "А-1" }, { "Номер": "А-2" }, { "Номер": "А-3" } ] }
            """);

        var message = InboxMessage.TryParseFile(path, out var error);

        Assert.Equal("", error);
        Assert.NotNull(message);
        Assert.Equal("Заказы", message!.Collection);
        Assert.Equal(InboxMode.Update, message.Mode);
        Assert.Equal("Номер", message.Key);
        Assert.Equal("m-7", message.MessageId);
        Assert.Equal(3, message.Rows);

        // Записи НЕ забраны в память: сообщение несет путь, а не текст.
        Assert.Equal(path, message.RowsFile);
        Assert.Equal("[]", message.RowsJson);
    }

    /// <summary>
    /// Поля конверта, написанные ПОСЛЕ массива записей, читаются наравне с остальными. Порядок
    /// полей в JSON ничего не значит, и отправитель вправе собрать файл как ему удобно — а вот
    /// разбор, который на этом ломается, нашел бы отправитель не сразу и не сам.
    /// </summary>
    [Fact]
    public void Envelope_fields_written_after_the_rows_are_still_read()
    {
        var path = Loose("заказы.json", """
            { "rows": [ { "Номер": "А-1" }, { "Номер": "А-2" } ],
              "collection": "Заказы", "mode": "delete", "key": "Номер", "id": "m-8" }
            """);

        var message = InboxMessage.TryParseFile(path, out var error);

        Assert.Equal("", error);
        Assert.Equal("Заказы", message!.Collection);
        Assert.Equal(InboxMode.Delete, message.Mode);
        Assert.Equal("Номер", message.Key);
        Assert.Equal("m-8", message.MessageId);
        Assert.Equal(2, message.Rows);
    }

    /// <summary>Чужие поля конверта не мешают: отправитель кладет в файл и свое.</summary>
    [Fact]
    public void Foreign_envelope_fields_are_passed_over()
    {
        var path = Loose("заказы.json", """
            { "выгрузил": "1С 8.3", "когда": {"дата":"2026-09-13","смена":2},
              "collection": "Заказы", "mode": "add", "id": "m-9",
              "rows": [ { "Номер": "А-1" } ], "подпись": [1,2,3] }
            """);

        var message = InboxMessage.TryParseFile(path, out var error);

        Assert.Equal("", error);
        Assert.Equal("Заказы", message!.Collection);
        Assert.Equal(1, message.Rows);
        Assert.Empty(message.Problems());
    }

    /// <summary>Состав полей «ensure-collection» читается из файла так же, как из строки.</summary>
    [Fact]
    public void A_schema_message_is_read_from_a_file()
    {
        var path = Loose("схема.json", """
            { "collection": "Заказы", "mode": "ensure-collection",
              "fields": [ { "name": "Номер", "type": "text" },
                          { "name": "Сумма", "type": "number" } ] }
            """);

        var message = InboxMessage.TryParseFile(path, out var error);

        Assert.Equal("", error);
        Assert.Equal(InboxMode.EnsureCollection, message!.Mode);
        Assert.Equal(2, message.Schema.Count);
        Assert.Equal("Номер", message.Schema[0].Name);
        Assert.Equal("number", message.Schema[1].Type);
        Assert.Empty(message.Problems());

        // Записей нет — и пути к ним тоже: читать из файла нечего.
        Assert.Equal("", message.RowsFile);
    }

    /// <summary>
    /// Оба входа понимают ОДНО И ТО ЖЕ сообщение одинаково. Это и есть причина, по которой разбор
    /// файла не написан отдельно: сообщение, принятое папкой, не должно отвергаться по сети — и
    /// наоборот.
    /// </summary>
    [Fact]
    public void Both_ways_in_understand_the_same_message()
    {
        const string text = """
            { "collection": "Заказы", "mode": "update", "key": "Номер", "id": "m-10",
              "rows": [ { "Номер": "А-1" }, { "Номер": "А-2" } ] }
            """;

        var path = Loose("заказы.json", text);

        var fromText = InboxMessage.TryParse(text, out var textError);
        var fromFile = InboxMessage.TryParseFile(path, out var fileError);

        Assert.Equal("", textError);
        Assert.Equal("", fileError);

        Assert.Equal(fromText!.Collection, fromFile!.Collection);
        Assert.Equal(fromText.Mode,        fromFile.Mode);
        Assert.Equal(fromText.Key,         fromFile.Key);
        Assert.Equal(fromText.MessageId,   fromFile.MessageId);
        Assert.Equal(fromText.Rows,        fromFile.Rows);
        Assert.Equal(fromText.Problems(),  fromFile.Problems());
    }

    /// <summary>Пустой файл и развал структуры объясняются словами — отправитель прочтет их в квитанции.</summary>
    [Fact]
    public void A_broken_file_is_explained_in_words()
    {
        Assert.Null(InboxMessage.TryParseFile(Loose("пусто.json", ""), out var empty));
        Assert.Contains("пустой", empty, StringComparison.Ordinal);

        Assert.Null(InboxMessage.TryParseFile(Loose("обрыв.json", """{"collection": "Зака"""),
                                              out var broken));
        Assert.Contains("не является JSON", broken, StringComparison.Ordinal);

        Assert.Null(InboxMessage.TryParseFile(Loose("массив.json", """[{"а":1}]"""), out var notObject));
        Assert.Contains("объект", notObject, StringComparison.Ordinal);

        Assert.Null(InboxMessage.TryParseFile(Loose("нет.json", """{"collection":"Заказы"}"""),
                                              out var noRows));
        Assert.Contains("rows", noRows, StringComparison.Ordinal);
    }

    /// <summary>Файла нет — это тоже ответ словами, а не падение обхода.</summary>
    [Fact]
    public void A_missing_file_is_explained_too()
    {
        Directory.CreateDirectory(_root);

        Assert.Null(InboxMessage.TryParseFile(Path.Combine(_root, "которого-нет.json"), out var error));
        Assert.NotEqual("", error);
    }

    // ── Потолка больше нет ────────────────────────────────────────────────────

    /// <summary>
    /// Файл ЗАВЕДОМО больше прежнего потолка в 32 МБ принимается и становится записями.
    ///
    /// Это и есть та самая проверка, ради которой весь потоковый разбор и писался: раньше такой
    /// файл получал квитанцию «файл слишком велик» и уезжал в «Ошибки», причем виноват в этом был
    /// не отправитель, а способ чтения на нашей стороне.
    ///
    /// Записи здесь крупные и их немного — нарочно: проверяется снятый потолок по РАЗМЕРУ ФАЙЛА,
    /// а не скорость записи в базу. Скорость укладки — отдельный предел, и мерить его файлом на
    /// тридцать мегабайт значило бы проверять одним тестом две разные вещи.
    /// </summary>
    [Fact]
    public void A_file_far_over_the_old_ceiling_is_accepted()
    {
        var layout = Layout();
        var (id, number) = Orders();

        var padding = new string('щ', 120_000);   // ~240 КБ на запись в UTF-8
        var rows    = new StringBuilder("[");

        for (var i = 0; i < 150; i++)
        {
            if (i > 0) rows.Append(',');
            rows.Append($$"""{"Название":"Заказ {{i}}","Номер":"А-{{i}}","Примечание":"{{padding}}"}""");
        }

        rows.Append(']');

        var path = Put(layout, "большой.json",
            $$"""{ "collection": "Заказы", "mode": "add", "id": "m-big", "rows": {{rows}} }""");

        Assert.True(new FileInfo(path).Length > InboxFolder.MaxFileBytes,
                    "файл должен быть заведомо больше прежнего потолка");

        // Прежним путем — чтением в строку — этот файл не прошел бы и сейчас. Потолок никуда не
        // делся и правильно стоит там, где память тратится; он просто перестал быть на дороге у
        // службы приема. Проверка держит обе половины этого утверждения сразу.
        Assert.Null(InboxFolder.ReadText(path, out var refusal));
        Assert.Contains("слишком велик", refusal, StringComparison.Ordinal);

        var run = InboxReceiver.Run(layout, _db.Constructor, dbId: 1, machine: "ПК17", now: Now);

        Assert.Equal("", run.Problem);
        Assert.Equal(1, run.Accepted);
        Assert.Equal(0, run.Rejected);

        var records = _db.Constructor.GetRecords(id);
        Assert.Equal(150, records.Count);
        Assert.Equal("А-0", _db.Constructor.GetFieldValues(
            records.First(r => r.Title == "Заказ 0").Id!.Value)[number]);
    }

    // ── Знак жизни ────────────────────────────────────────────────────────────

    /// <summary>Отметка живого захвата двигает время записи; пропавший файл ее не роняет.</summary>
    [Fact]
    public void Touching_a_claim_moves_its_write_time()
    {
        var layout = Layout();
        var path   = Put(layout, "заказы.json", "{}");

        InboxFolder.Touch(path, Now.AddHours(3));

        Assert.Equal(Now.AddHours(3).UtcDateTime, File.GetLastWriteTimeUtc(path),
                     TimeSpan.FromSeconds(2));

        InboxFolder.Touch(Path.Combine(layout.Incoming, "которого-нет.json"), Now);   // не падает
    }

    /// <summary>
    /// Долгая укладка подает знак жизни, и захват не выглядит брошенным.
    ///
    /// Без этого сосед через четыре часа вернул бы файл во «Входящие» и разобрал его ВТОРОЙ РАЗ —
    /// параллельно с первым. От повторов защищает квитанция, но она ложится только по окончании
    /// укладки, и защититься ею посреди нее нечем. Пока файл был ограничен потолком в 32 МБ, такая
    /// обработка не могла идти четыре часа физически; теперь может.
    ///
    /// Строк здесь больше <see cref="InboxIntake.BeatEvery"/>, а режим — «удалить» по ключу,
    /// которого в коллекции нет: проход по строкам настоящий, а записи в базу не идут, и проверка
    /// не превращается в замер скорости.
    /// </summary>
    [Fact]
    public void A_long_intake_keeps_its_claim_alive()
    {
        var layout = Layout();
        Orders();

        var rows = new StringBuilder("[");
        for (var i = 0; i < InboxIntake.BeatEvery + 500; i++)
        {
            if (i > 0) rows.Append(',');
            rows.Append($$"""{"Номер":"нет-{{i}}"}""");
        }
        rows.Append(']');

        Put(layout, "удаления.json",
            $$"""{ "collection": "Заказы", "mode": "delete", "key": "Номер", "id": "m-long", "rows": {{rows}} }""");

        var beats = 0;

        var run = InboxReceiver.Run(layout, _db.Constructor, dbId: 1, machine: "ПК17", now: Now,
                                    clock: () => { beats++; return Now.AddMinutes(beats); });

        Assert.Equal(1, run.Accepted);
        Assert.True(beats >= 1, "за время укладки захват обязан быть отмечен живым хотя бы раз");
    }
}
