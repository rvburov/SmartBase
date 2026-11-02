using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Core.Models;
using SmartBase.Extensions.DocRegistry;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>Настоящий путь книги: HTTP-байты, сценарий, действие регистрации и ответ участнику.</summary>
public sealed class FileMessageScenarioTests : IDisposable
{
    private readonly TempDatabase _db = new("file-message-scenario");
    private readonly TempDir _dir = new("file-message-scenario");
    private readonly DatabaseRegistry _registry;
    private readonly RegistryShape _shape;
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private const int PostMessageId = int.MaxValue - 7;
    private const int PostAssetId = int.MaxValue - 8;

    public FileMessageScenarioTests()
    {
        _registry = DatabaseRegistry.Open(Path.Combine(_dir.Path, "database.db"),
            Path.Combine(_dir.Path, "app.db"));
        AppSettings.AttachStore(_registry);
        AppSettings.SaveExtensionSetting(DocRegistryExtension.ExtensionId,
            DocRegistryExtension.OrgCodesKey, "АБВГ");
        _shape = JournalStand.Shape(_db.Constructor, JournalStand.Create(_db.Constructor));
    }

    public void Dispose()
    {
        AppSettings.AttachStore(new NoSettings());
        RunJournal.Attach(null);
        _registry.Dispose();
        _db.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task An_attached_workbook_reaches_registration_with_its_original_event_and_exact_bytes()
    {
        Seed("301121");
        Seed("301122");
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Name: "Втулка", Accounting: "Учтен"),
            new FileMessageBook.Row(5, "АБВГ.301122.ХХХ", "Корпус", "Петров Петр Петрович", "Второй проект"));
        using var server = new FileServer(bytes);
        var scenario = Scenario(server);

        // Текстового поля в событии вообще нет: вкладка передает только вложение и метаданные.
        var outcome = await Run(scenario, Event());

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Equal(2, outcome.Rows);
        var reply = ReplyMarkup.Text(outcome.Reply);
        Assert.Contains("2 из 2", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.301121.002", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.301122.002", reply, StringComparison.Ordinal);
        Assert.StartsWith("GET /files/asset-23 ", Assert.Single(server.Requests), StringComparison.Ordinal);
        var records = _db.Constructor.GetRecords(_shape.CollectionId);
        var first = Assert.Single(records, record => record.Title == "Втулка");
        Assert.Equal("Учтен", _db.Constructor.GetFieldValues(first.Id!.Value)[_shape.AccountingFieldId]);
        var second = Assert.Single(records, record => record.Title == "Корпус");
        var values = _db.Constructor.GetFieldValues(second.Id!.Value);
        Assert.Equal("Петров Петр Петрович", values[_shape.FioFieldId]);
        Assert.Equal("Второй проект", values[_shape.ProjectFieldId]);

        // Хеш постоянной квитанции считается расширением с полученных байтов, уже после HTTP.
        var receipt = Assert.Single(_db.Context.DbMeta.AsNoTracking().Where(item =>
            item.Key.StartsWith("extension-state:doc-registry:file:")));
        using var saved = JsonDocument.Parse(receipt.Value);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), saved.RootElement.GetProperty("Digest").GetString());
    }

    [Fact]
    public async Task A_text_yes_without_attachment_metadata_confirms_the_same_scenarios_complete_file()
    {
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Name: "Первая серия"),
            new FileMessageBook.Row(7, "АБВГ.806121.ХХХ", "Неизвестная серия"));
        using var server = new FileServer(bytes);
        var scenario = Scenario(server);
        Assert.True(scenario.Id > 0);
        Assert.True(scenario.Trigger.StreamId > 0);

        var preview = await Run(scenario, Event());

        Assert.True(preview.Ok, preview.Problem);
        Assert.Equal(0, preview.Rows);
        Assert.Empty(_db.Constructor.GetRecords(_shape.CollectionId));
        Assert.Contains("АБВГ.301121.001", ReplyMarkup.Text(preview.Reply), StringComparison.Ordinal);
        Assert.Contains("АБВГ.806121.001", ReplyMarkup.Text(preview.Reply), StringComparison.Ordinal);
        Assert.Contains("не найден", ReplyMarkup.Text(preview.Reply), StringComparison.Ordinal);
        Assert.Empty(_db.Context.DbMeta.AsNoTracking().Where(item =>
            item.Key.StartsWith("extension-state:doc-registry:file:")));

        var confirmed = await Run(scenario, Event(text: "Да", attachment: false, messageId: "confirmation-2"));

        Assert.True(confirmed.Ok, confirmed.Problem);
        Assert.Equal(2, confirmed.Rows);
        Assert.Contains("2 из 2", ReplyMarkup.Text(confirmed.Reply), StringComparison.Ordinal);
        Assert.Equal(new[] { "АБВГ.301121.001", "АБВГ.806121.001" },
            DocRegistrar.Designations(_db.Constructor, _shape).Order(StringComparer.Ordinal));
        Assert.Single(server.Requests);
        var again = await Run(scenario, Event(text: "Да", attachment: false, messageId: "confirmation-3"));
        Assert.False(again.Ok);
        Assert.Equal(0, again.Rows);
        Assert.Equal(2, _db.Constructor.GetRecords(_shape.CollectionId).Count);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Redelivery_after_reopening_the_database_returns_the_original_text_without_new_rows()
    {
        Seed("301121");
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Name: "Первый"), new FileMessageBook.Row(3, Name: "Второй"));
        using var server = new FileServer(bytes);
        var scenario = Scenario(server);
        var initial = await Run(scenario, Event());
        Assert.True(initial.Ok, initial.Problem);
        Assert.Equal(2, initial.Rows);
        var id = _db.Manager.DbId;
        _db.Manager.Dispose();

        using var reopened = DatabaseManager.Open(id, "Повторно открытая база", _db.Path);
        var replay = await Run(scenario, Event(), reopened);

        Assert.True(replay.Ok, replay.Problem);
        Assert.Equal(0, replay.Rows);
        Assert.Equal(initial.Reply, replay.Reply);
        Assert.Equal(3, reopened.Constructor.GetRecords(_shape.CollectionId).Count);
        Assert.Equal(new[] { "АБВГ.301121.001", "АБВГ.301121.002", "АБВГ.301121.003" },
            DocRegistrar.Designations(reopened.Constructor, _shape).Order(StringComparer.Ordinal));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task One_bad_worksheet_row_returns_all_errors_through_the_scenario_and_writes_nothing()
    {
        Seed("301121");
        var before = _db.Context.ChangeLog.AsNoTracking().Count();
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Name: "Годная строка"),
            new FileMessageBook.Row(7, "АБВГ.30112.ХХХ", "Ошибка кода"));
        using var server = new FileServer(bytes);

        var result = await Run(Scenario(server), Event());

        Assert.False(result.Ok);
        Assert.Equal(0, result.Rows);
        var reply = ReplyMarkup.Text(result.Reply);
        Assert.Contains("0 из 2", reply, StringComparison.Ordinal);
        Assert.Contains("Строка 7", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.30112.ХХХ", reply, StringComparison.Ordinal);
        Assert.Equal("Существующий", Assert.Single(_db.Constructor.GetRecords(_shape.CollectionId)).Title);
        Assert.Equal(before, _db.Context.ChangeLog.AsNoTracking().Count());
        Assert.Empty(_db.Context.DbMeta.AsNoTracking().Where(item =>
            item.Key.StartsWith("extension-state:doc-registry:file:")));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_download_error_stops_before_registration_and_creates_no_receipt()
    {
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2));
        using var server = new FileServer(bytes, status: 500);

        var result = await Run(Scenario(server), Event());

        Assert.False(result.Ok);
        Assert.Equal(0, result.Rows);
        Assert.Contains("500", result.Problem, StringComparison.Ordinal);
        Assert.Empty(_db.Constructor.GetRecords(_shape.CollectionId));
        Assert.Empty(_db.Context.DbMeta.AsNoTracking().Where(item =>
            item.Key.StartsWith("extension-state:doc-registry:file:")));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_post_download_uses_numeric_ids_computed_from_the_event_and_registers_the_exact_workbook()
    {
        Seed("301121");
        Seed("301122");
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Name: "Втулка", Accounting: "Учтен"),
            new FileMessageBook.Row(6, "АБВГ.301122.ХХХ", "Корпус", "Петров Петр Петрович", "Второй проект"));
        using var server = new FileServer(bytes, messageId: PostMessageId, assetId: PostAssetId);

        var outcome = await Run(Scenario(server, post: true), PostEvent());

        Assert.True(outcome.Ok, outcome.Problem + " " + string.Join("; ", server.ContractErrors));
        Assert.Equal(2, outcome.Rows);
        var reply = ReplyMarkup.Text(outcome.Reply);
        Assert.Contains("2 из 2", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.301121.002", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.301122.002", reply, StringComparison.Ordinal);
        AssertPostRequests(server, 1);
        var records = _db.Constructor.GetRecords(_shape.CollectionId);
        var first = Assert.Single(records, record => record.Title == "Втулка");
        Assert.Equal("Учтен", _db.Constructor.GetFieldValues(first.Id!.Value)[_shape.AccountingFieldId]);
        var second = Assert.Single(records, record => record.Title == "Корпус");
        var values = _db.Constructor.GetFieldValues(second.Id!.Value);
        Assert.Equal("Петров Петр Петрович", values[_shape.FioFieldId]);
        Assert.Equal("Второй проект", values[_shape.ProjectFieldId]);
        var receipt = Assert.Single(_db.Context.DbMeta.AsNoTracking().Where(item =>
            item.Key.StartsWith("extension-state:doc-registry:file:")));
        using var saved = JsonDocument.Parse(receipt.Value);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), saved.RootElement.GetProperty("Digest").GetString());
    }

    [Fact]
    public async Task A_post_file_waits_for_plain_yes_with_false_and_empty_ids_and_redelivery_replays_its_receipt()
    {
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Name: "Первая серия"),
            new FileMessageBook.Row(7, "АБВГ.806121.ХХХ", "Неизвестная серия"));
        using var server = new FileServer(bytes, messageId: PostMessageId, assetId: PostAssetId);
        var scenario = Scenario(server, post: true);

        var preview = await Run(scenario, PostEvent());

        Assert.True(preview.Ok, preview.Problem + " " + string.Join("; ", server.ContractErrors));
        Assert.Equal(0, preview.Rows);
        Assert.Empty(_db.Constructor.GetRecords(_shape.CollectionId));
        Assert.Contains("АБВГ.301121.001", ReplyMarkup.Text(preview.Reply), StringComparison.Ordinal);
        Assert.Contains("АБВГ.806121.001", ReplyMarkup.Text(preview.Reply), StringComparison.Ordinal);
        Assert.Empty(_db.Context.DbMeta.AsNoTracking().Where(item =>
            item.Key.StartsWith("extension-state:doc-registry:file:")));
        AssertPostRequests(server, 1);

        // Поля контракта остаются в обычном сообщении: false и [] проходят тот же Compute,
        // пустое вычисленное имя пропускает скачивание, а текст доезжает до подтверждения.
        var plain = PostEvent(text: "Да", attachment: false, messageId: PostMessageId + 1);
        Assert.Equal("false", Assert.Single(plain.Rows)["is-file"]);
        Assert.Equal("[]", Assert.Single(plain.Rows)["file-ids"]);
        var confirmed = await Run(scenario, plain);

        Assert.True(confirmed.Ok, confirmed.Problem);
        Assert.Equal(2, confirmed.Rows);
        Assert.Contains("2 из 2", ReplyMarkup.Text(confirmed.Reply), StringComparison.Ordinal);
        AssertPostRequests(server, 1);
        var numbers = DocRegistrar.Designations(_db.Constructor, _shape).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "АБВГ.301121.001", "АБВГ.806121.001" }, numbers);

        var replay = await Run(scenario, PostEvent());

        Assert.True(replay.Ok, replay.Problem);
        Assert.Equal(0, replay.Rows);
        Assert.Equal(confirmed.Reply, replay.Reply);
        Assert.Equal(numbers, DocRegistrar.Designations(_db.Constructor, _shape).Order(StringComparer.Ordinal));
        Assert.Equal(2, _db.Constructor.GetRecords(_shape.CollectionId).Count);
        Assert.Single(_db.Context.DbMeta.AsNoTracking().Where(item =>
            item.Key.StartsWith("extension-state:doc-registry:file:")));
        AssertPostRequests(server, 2);
    }

    [Fact]
    public async Task A_bad_row_in_a_post_downloaded_workbook_refuses_the_entire_request_without_rows_or_receipt()
    {
        Seed("301121");
        var before = _db.Context.ChangeLog.AsNoTracking().Count();
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Name: "Годная строка"),
            new FileMessageBook.Row(7, "АБВГ.30112.ХХХ", "Ошибка кода"));
        using var server = new FileServer(bytes, messageId: PostMessageId, assetId: PostAssetId);

        var result = await Run(Scenario(server, post: true), PostEvent());

        Assert.False(result.Ok);
        Assert.Equal(0, result.Rows);
        var reply = ReplyMarkup.Text(result.Reply);
        Assert.Contains("0 из 2", reply, StringComparison.Ordinal);
        Assert.Contains("Строка 7", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.30112.ХХХ", reply, StringComparison.Ordinal);
        Assert.Equal("Существующий", Assert.Single(_db.Constructor.GetRecords(_shape.CollectionId)).Title);
        Assert.Equal(before, _db.Context.ChangeLog.AsNoTracking().Count());
        Assert.Empty(_db.Context.DbMeta.AsNoTracking().Where(item =>
            item.Key.StartsWith("extension-state:doc-registry:file:")));
        AssertPostRequests(server, 1);
    }

    [Fact]
    public async Task A_workbook_waits_for_post_readiness_without_early_writes_and_is_registered_once()
    {
        Seed("301121");
        var before = _db.Context.ChangeLog.AsNoTracking().Count();
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Name: "После готовности"),
            new FileMessageBook.Row(5, Name: "Вторая строка"));
        using var server = new FileServer(bytes, messageId: PostMessageId, assetId: PostAssetId,
            waitUntilReady: true);
        var scenario = Scenario(server, post: true, waitSeconds: 3);

        var pending = Run(scenario, PostEvent());
        await server.FirstUnavailable.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Существующий", Assert.Single(_db.Constructor.GetRecords(_shape.CollectionId)).Title);
        Assert.Equal(before, _db.Context.ChangeLog.AsNoTracking().Count());
        Assert.Empty(_db.Context.DbMeta.AsNoTracking().Where(item =>
            item.Key.StartsWith("extension-state:doc-registry:file:")));
        Assert.DoesNotContain(200, server.ResponseStatuses);

        server.SetReady();
        var registered = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(registered.Ok, registered.Problem + " " + string.Join("; ", server.ContractErrors));
        Assert.Equal(2, registered.Rows);
        Assert.Contains("2 из 2", ReplyMarkup.Text(registered.Reply), StringComparison.Ordinal);
        Assert.Contains(404, server.ResponseStatuses);
        Assert.Equal(1, server.ResponseStatuses.Count(status => status == 200));
        var attempts = server.Requests.Count;
        Assert.True(attempts >= 2);
        AssertPostRequests(server, attempts);
        Assert.Single(server.Bodies.Distinct(StringComparer.Ordinal));
        var records = _db.Constructor.GetRecords(_shape.CollectionId);
        Assert.Equal(3, records.Count);
        foreach (var record in records.Where(record => record.Title != "Существующий"))
        {
            var values = _db.Constructor.GetFieldValues(record.Id!.Value);
            Assert.Equal("Иванов Иван Иванович", values[_shape.FioFieldId]);
            Assert.Equal("Проект", values[_shape.ProjectFieldId]);
            Assert.True(DateValue.TryParseStorage(values[_shape.DateFieldId], out var date));
            Assert.Equal(Now.LocalDateTime, date.Start);
        }
        var receipt = Assert.Single(_db.Context.DbMeta.AsNoTracking().Where(item =>
            item.Key.StartsWith("extension-state:doc-registry:file:")));
        using var saved = JsonDocument.Parse(receipt.Value);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), saved.RootElement.GetProperty("Digest").GetString());

        var numbers = DocRegistrar.Designations(_db.Constructor, _shape).Order(StringComparer.Ordinal).ToArray();
        var replay = await Run(scenario, PostEvent());

        Assert.True(replay.Ok, replay.Problem);
        Assert.Equal(0, replay.Rows);
        Assert.Equal(registered.Reply, replay.Reply);
        Assert.Equal(numbers, DocRegistrar.Designations(_db.Constructor, _shape).Order(StringComparer.Ordinal));
        Assert.Equal(3, _db.Constructor.GetRecords(_shape.CollectionId).Count);
        Assert.Single(_db.Context.DbMeta.AsNoTracking().Where(item =>
            item.Key.StartsWith("extension-state:doc-registry:file:")));
        AssertPostRequests(server, attempts + 1);
        Assert.Equal(2, server.ResponseStatuses.Count(status => status == 200));
    }

    [Fact]
    public async Task A_ready_workbook_keeps_its_sender_and_plain_yes_skips_download_waiting()
    {
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Name: "Новая серия"));
        using var server = new FileServer(bytes, messageId: PostMessageId, assetId: PostAssetId,
            waitUntilReady: true);
        var scenario = Scenario(server, post: true, waitSeconds: 3);

        var pending = Run(scenario, PostEvent());
        await server.FirstUnavailable.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(_db.Constructor.GetRecords(_shape.CollectionId));
        Assert.Empty(_db.Context.DbMeta.AsNoTracking().Where(item =>
            item.Key.StartsWith("extension-state:doc-registry:file:")));
        server.SetReady();
        var preview = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(preview.Ok, preview.Problem);
        Assert.Equal(0, preview.Rows);
        Assert.Contains("АБВГ.301121.001", ReplyMarkup.Text(preview.Reply), StringComparison.Ordinal);
        Assert.Empty(_db.Constructor.GetRecords(_shape.CollectionId));
        Assert.Empty(_db.Context.DbMeta.AsNoTracking().Where(item =>
            item.Key.StartsWith("extension-state:doc-registry:file:")));
        var attempts = server.Requests.Count;
        Assert.True(attempts >= 2);
        AssertPostRequests(server, attempts);

        // Повторная загрузка больше недоступна. Оба текстовых ответа обязаны обойти
        // скачивание и его ожидание, а подтверждать может только исходный отправитель.
        server.SetUnavailable();
        var wrongSender = await Run(scenario, PostEvent("Да", attachment: false,
            messageId: PostMessageId + 1, author: "participant-18")).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(wrongSender.Ok);
        Assert.Empty(_db.Constructor.GetRecords(_shape.CollectionId));
        AssertPostRequests(server, attempts);

        var plain = PostEvent("Да", attachment: false, messageId: PostMessageId + 2);
        Assert.Equal("false", Assert.Single(plain.Rows)["is-file"]);
        Assert.Equal("[]", Assert.Single(plain.Rows)["file-ids"]);
        var confirmed = await Run(scenario, plain).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(confirmed.Ok, confirmed.Problem);
        Assert.Equal(1, confirmed.Rows);
        Assert.Contains("1 из 1", ReplyMarkup.Text(confirmed.Reply), StringComparison.Ordinal);
        var record = Assert.Single(_db.Constructor.GetRecords(_shape.CollectionId));
        var values = _db.Constructor.GetFieldValues(record.Id!.Value);
        Assert.Equal("АБВГ.301121.001", values[_shape.DesignationFieldId]);
        Assert.True(DateValue.TryParseStorage(values[_shape.DateFieldId], out var date));
        Assert.Equal(Now.LocalDateTime, date.Start);
        Assert.Single(_db.Context.DbMeta.AsNoTracking().Where(item =>
            item.Key.StartsWith("extension-state:doc-registry:file:")));
        AssertPostRequests(server, attempts);
    }

    private Scenario Scenario(FileServer server, bool post = false, int waitSeconds = 0)
    {
        var cards = _registry.Integrations;
        var connection = cards.Save(IntegrationConnection.New() with
        {
            Name = "Сторонняя программа", Address = $"http://127.0.0.1:{server.Port}",
            TimeoutSeconds = 10, Retries = 0,
        });
        var request = cards.SaveRequest(IntegrationRequest.New() with
        {
            Name = "Получение вложения", ConnectionId = connection.Id,
            Path = post ? "/files/download" : "/files/{{поле.asset-id}}",
            Method = post ? RequestMethod.Post : RequestMethod.Get,
            Body = post ? """{"messageId":{{поле.event-id}},"assetId":{{поле.asset-id}}}""" : "",
            Headers = post ? [new IntegrationHeader("Content-Type", "application/json")] : [],
        });
        Assert.Null(cards.Binding(request.Id));
        var stream = cards.SaveStream(IntegrationStream.New() with
        {
            Name = "События", ConnectionId = connection.Id, Path = "/events", ItemsPath = "data",
        });
        var registration = ScenarioStep.OfAction(DocRegistryExtension.ExtensionId, RegisterMessageAction.ActionKey,
            _db.Manager.DbId, _shape.CollectionId, new Dictionary<string, string>
            {
                [RegisterMessageAction.TextField] = "{{body}}",
                [RegisterMessageAction.SenderField] = "{{author}}",
                [RegisterMessageAction.MessageIdField] = post ? "{{request-id}}" : "{{event-id}}",
            });
        return cards.SaveScenario(SmartBase.Integrations.Scenario.New() with
        {
            Name = "Книга из сообщения", Enabled = true, Trigger = ScenarioTrigger.OnStream(stream.Id),
            Steps = post
                ? [
                    ScenarioStep.OfCompute(
                        new ScenarioFormula("asset-id", """ЕСЛИ({{is-file}}; ЧИСЛО(ЗАМЕНИТЬ(ЗАМЕНИТЬ({{file-ids}}; "["; ""); "]"; "")); "")"""),
                        new ScenarioFormula("file-name", """ЕСЛИ({{is-file}}; "Список КД.xlsx"; "")"""),
                        new ScenarioFormula("request-id", """СЦЕПИТЬ({{event-id}}; ":"; {{asset-id}})""")),
                    ScenarioStep.OfDownload(request.Id, "{{file-name}}", waitSeconds: waitSeconds),
                    registration,
                ]
                : [ScenarioStep.OfDownload(request.Id, "{{file-name}}", waitSeconds: waitSeconds), registration],
        }, Now);
    }

    private Task<ScenarioOutcome> Run(Scenario scenario, StepTable initial, DatabaseManager? manager = null) =>
        ScenarioRunner.RunAsync(scenario, _registry.Integrations, [manager ?? _db.Manager],
            [new DocRegistryExtension()], Now, _ => { }, initial: initial);

    private static StepTable Event(string? text = null, bool attachment = true, string messageId = "event-1")
    {
        var source = new Dictionary<string, string>
        {
            ["author"] = "participant-17", ["event-id"] = messageId,
        };
        if (text is not null) source["body"] = text;
        if (attachment)
        {
            source["file-name"] = "Список КД.xlsx";
            source["asset-id"] = "asset-23";
        }
        var rows = StreamEvents.Rows(JsonSerializer.Serialize(new { data = new[] { source } }), "data", out var trouble);
        Assert.Equal("", trouble);
        var row = Assert.Single(rows);
        return new StepTable(row.Keys.ToArray(), [row]);
    }

    private static StepTable PostEvent(string? text = null, bool attachment = true, int messageId = PostMessageId,
                                      string author = "participant-17")
    {
        var source = new Dictionary<string, object>
        {
            ["author"] = author, ["event-id"] = messageId,
            ["is-file"] = attachment, ["file-ids"] = attachment ? new[] { PostAssetId } : Array.Empty<int>(),
        };
        if (text is not null) source["body"] = text;
        var rows = StreamEvents.Rows(JsonSerializer.Serialize(new { data = new[] { new[] { source } } }),
            "data", out var trouble);
        Assert.Equal("", trouble);
        var row = Assert.Single(rows);
        return new StepTable(row.Keys.ToArray(), [row]);
    }

    private static void AssertPostRequests(FileServer server, int count)
    {
        Assert.Empty(server.ContractErrors);
        Assert.Equal(count, server.Requests.Count);
        Assert.All(server.Requests, line => Assert.StartsWith("POST /files/download ", line, StringComparison.Ordinal));
        Assert.Equal(count, server.Bodies.Count);
        Assert.All(server.Bodies, body =>
        {
            using var request = JsonDocument.Parse(body);
            var fields = request.RootElement;
            Assert.Equal(2, fields.EnumerateObject().Count());
            Assert.Equal(JsonValueKind.Number, fields.GetProperty("messageId").ValueKind);
            Assert.Equal(JsonValueKind.Number, fields.GetProperty("assetId").ValueKind);
            Assert.Equal(PostMessageId, fields.GetProperty("messageId").GetInt32());
            Assert.Equal(PostAssetId, fields.GetProperty("assetId").GetInt32());
        });
    }

    private void Seed(string code) =>
        JournalStand.AddRow(_db.Constructor, _shape, "Существующий", $"АБВГ.{code}.001");

    private sealed class NoSettings : ISettingsDatabase
    {
        public string? Get(string key) => null;
        public void Set(string key, string value) { }
        public void Remove(string key) { }
        public IReadOnlyList<string> KeysWithPrefix(string prefix) => [];
    }

    /// <summary>Локальная выдача байтов книги без перекодирования и без внешнего API.</summary>
    private sealed class FileServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly byte[] _answer;
        private readonly int _status;
        private readonly int? _messageId;
        private readonly int? _assetId;
        private readonly TaskCompletionSource _firstUnavailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Port { get; }
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal ConcurrentQueue<string> Bodies { get; } = new();
        internal ConcurrentQueue<string> ContractErrors { get; } = new();
        internal ConcurrentQueue<int> ResponseStatuses { get; } = new();
        internal Task FirstUnavailable => _firstUnavailable.Task;

        internal FileServer(byte[] content, int status = 200, int? messageId = null, int? assetId = null,
                            bool waitUntilReady = false)
        {
            _status = status;
            _messageId = messageId;
            _assetId = assetId;
            if (!waitUntilReady) SetReady();
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Result\r\nContent-Type: application/octet-stream\r\n"
                + $"Content-Length: {content.Length}\r\nConnection: close\r\n\r\n");
            _answer = new byte[header.Length + content.Length];
            header.CopyTo(_answer, 0);
            content.CopyTo(_answer, header.Length);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

        internal void SetReady() => _ready.TrySetResult();

        internal void SetUnavailable() =>
            _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var request = new StringBuilder();
                    var one = new byte[1];
                    var matched = 0;
                    while (matched < 4)
                    {
                        if (await stream.ReadAsync(one, _stop.Token) == 0) return;
                        request.Append((char)one[0]);
                        var expected = matched % 2 == 0 ? '\r' : '\n';
                        matched = one[0] == expected ? matched + 1 : one[0] == '\r' ? 1 : 0;
                    }
                    var headers = request.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                    var contentLength = headers.FirstOrDefault(header =>
                        header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    var length = contentLength is null ? 0
                        : int.Parse(contentLength["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);
                    var bytes = new byte[length];
                    await stream.ReadExactlyAsync(bytes, _stop.Token);
                    var body = Encoding.UTF8.GetString(bytes);
                    Requests.Enqueue(headers[0]);
                    Bodies.Enqueue(body);
                    var valid = _messageId is null || ValidPost(headers, body);
                    var status = !valid ? 400 : !_ready.Task.IsCompleted ? 404 : _status;
                    var answer = valid && status == _status ? _answer : Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status} Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    ResponseStatuses.Enqueue(status);
                    await stream.WriteAsync(answer, _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                    if (status == 404) _firstUnavailable.TrySetResult();
                }
                catch (Exception error) when (error is IOException or OperationCanceledException
                    or ObjectDisposedException or SocketException) { }
            }
        }

        private bool ValidPost(string[] headers, string body)
        {
            try
            {
                using var request = JsonDocument.Parse(body);
                var fields = request.RootElement;
                if (headers[0].StartsWith("POST /files/download ", StringComparison.Ordinal)
                    && headers.Any(header => header.StartsWith("Content-Type: application/json", StringComparison.OrdinalIgnoreCase))
                    && fields.ValueKind == JsonValueKind.Object && fields.EnumerateObject().Count() == 2
                    && fields.TryGetProperty("messageId", out var message) && message.ValueKind == JsonValueKind.Number
                    && message.TryGetInt32(out var messageId) && messageId == _messageId
                    && fields.TryGetProperty("assetId", out var asset) && asset.ValueKind == JsonValueKind.Number
                    && asset.TryGetInt32(out var assetId) && assetId == _assetId)
                    return true;
            }
            catch (JsonException) { }
            ContractErrors.Enqueue("Ожидался POST с JSON и двумя точными числовыми идентификаторами Int32.");
            return false;
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch (SocketException) { }
            _stop.Dispose();
        }
    }
}
