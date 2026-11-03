using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Extensions;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>Файл проходит через настоящий сценарий вместе с полями события;
/// отсутствие вложения, сбой сети и отмена не превращаются в частичную запись.</summary>
[Collection(EgressCollection.Name)]
public sealed class ScenarioDownloadTests : IDisposable
{
    private static readonly byte[] Payload = [0x50, 0x4b, 0, 255, 192, 128, 13, 10, 0, 254];
    private readonly TempDatabase _db = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("sb-download-step-").FullName;
    private readonly DatabaseRegistry _registry;
    private readonly int _collection;

    public ScenarioDownloadTests()
    {
        _registry = DatabaseRegistry.Open(Path.Combine(_dir, "database.db"), Path.Combine(_dir, "app.db"));
        _collection = SampleData.AddCollection(_db.Constructor, "Принятые заявки").Id!.Value;
    }

    private IntegrationsRegistry Cards => _registry.Integrations;

    [Theory]
    [InlineData(false, "Список КД.xlsx")]
    [InlineData(true, "Исходный список КД.xlsx")]
    public async Task Server_filename_is_used_only_when_the_download_step_explicitly_requests_it(
        bool useResponseFileName, string expectedName)
    {
        using var server = new FileServer(Payload, contentDisposition:
            "attachment; filename=fallback.xlsx; filename*=UTF-8''%D0%98%D1%81%D1%85%D0%BE%D0%B4%D0%BD%D1%8B%D0%B9%20%D1%81%D0%BF%D0%B8%D1%81%D0%BE%D0%BA%20%D0%9A%D0%94.xlsx");
        var request = Request(server, path: "/assets/read");
        var action = new Receiver();
        var download = ScenarioStep.OfDownload(request.Id, "{{file-name}}") with
            { UseResponseFileName = useResponseFileName };

        var outcome = await Run([download, Action(action)], [action], Event());

        Assert.True(outcome.Ok, outcome.Problem);
        var received = Assert.Single(action.Seen);
        Assert.Equal(expectedName, received.FileName);
        Assert.Equal(Payload, received.Bytes);
        Assert.Equal("message-42", received.Values["message-id"]);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("attachment")]
    [InlineData("attachment; filename=\"../escape.xlsx\"")]
    [InlineData("attachment; filename=\"unsafe?.xlsx\"")]
    public async Task Unusable_server_filename_keeps_the_configured_name_and_unchanged_file(string? disposition)
    {
        using var server = new FileServer(Payload, contentDisposition: disposition);
        var request = Request(server, path: "/assets/read");
        var action = new Receiver();
        var download = ScenarioStep.OfDownload(request.Id, "{{file-name}}") with { UseResponseFileName = true };

        var outcome = await Run([download, Action(action)], [action], Event());

        Assert.True(outcome.Ok, outcome.Problem);
        var received = Assert.Single(action.Seen);
        Assert.Equal("Список КД.xlsx", received.FileName);
        Assert.Equal(Payload, received.Bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Preferring_the_server_name_still_skips_events_without_a_filename_before_network_work(string? name)
    {
        using var server = new FileServer(Payload, contentDisposition: "attachment; filename=server.xlsx");
        var request = Request(server, path: "/assets/read");
        var action = new Receiver();
        var download = ScenarioStep.OfDownload(request.Id, "{{file-name}}") with { UseResponseFileName = true };

        var outcome = await Run([download, Action(action)], [action], Event(name));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Empty(server.Requests);
        var received = Assert.Single(action.Seen);
        Assert.Equal("", received.FileName);
        Assert.Null(received.Bytes);
    }

    [Fact]
    public async Task Binary_file_and_original_event_fields_reach_the_action_without_a_collection_binding()
    {
        using var server = new FileServer(Payload);
        var request = Request(server);
        var action = new Receiver();
        using var stop = new CancellationTokenSource();
        Assert.Null(Cards.Binding(request.Id));

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}"), Action(action)],
            [action], Event(), stop.Token);

        Assert.True(outcome.Ok, outcome.Problem);
        var received = Assert.Single(action.Seen);
        Assert.Equal(Payload, received.Bytes);
        Assert.Equal("Список КД.xlsx", received.FileName);
        Assert.Equal("Заявка участника", received.Values["title"]);
        Assert.Equal("participant-17", received.Values["sender"]);
        Assert.Equal("message-42", received.Values["message-id"]);
        Assert.Equal(stop.Token, received.Token);
        Assert.StartsWith("GET /files/asset-23 ", Assert.Single(server.Requests), StringComparison.Ordinal);
        Assert.Equal("Заявка участника", Assert.Single(_db.Constructor.GetRecords(_collection)).Title);
    }

    [Fact]
    public async Task Post_download_uses_numeric_event_keys_after_compute_and_preserves_binary_input_metadata()
    {
        using var server = new FileServer(Payload);
        var request = Request(server, path: "/assets/read", method: RequestMethod.Post,
            body: """{"messageKey":{{поле.message-id}},"assetKey":{{поле.attachment-id}}}""");
        var action = new Receiver();
        using var stop = new CancellationTokenSource();

        var outcome = await Run([
            AttachmentColumns(), ScenarioStep.OfDownload(request.Id, "{{download-name}}"), Action(action),
        ], [action], AttachmentEvent(true), stop.Token);

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Null(Cards.Binding(request.Id));
        Assert.StartsWith("POST /assets/read ", Assert.Single(server.Requests), StringComparison.Ordinal);
        using var body = JsonDocument.Parse(Assert.Single(server.Bodies));
        Assert.Equal(JsonValueKind.Number, body.RootElement.GetProperty("messageKey").ValueKind);
        Assert.Equal(42, body.RootElement.GetProperty("messageKey").GetInt32());
        Assert.Equal(JsonValueKind.Number, body.RootElement.GetProperty("assetKey").ValueKind);
        Assert.Equal(23, body.RootElement.GetProperty("assetKey").GetInt32());
        var received = Assert.Single(action.Seen);
        Assert.Equal(Payload, received.Bytes);
        Assert.Equal("Заявка.xlsx", received.FileName);
        Assert.Equal("Заявка участника", received.Values["title"]);
        Assert.Equal("participant-17", received.Values["sender"]);
        Assert.Equal("42", received.Values["message-id"]);
        Assert.Equal(stop.Token, received.Token);
        Assert.Equal("Заявка участника", Assert.Single(_db.Constructor.GetRecords(_collection)).Title);
    }

    [Theory]
    [InlineData("Обычный текст")]
    [InlineData("Да")]
    [InlineData("Нет")]
    public async Task Compute_skips_a_post_download_for_text_and_confirmation_events_without_files(string text)
    {
        using var server = new FileServer(Payload);
        // Пустой вычисленный ключ оставил бы в этом теле неверный JSON. Шаг обязан
        // пропуститься по пустому имени файла раньше подготовки и отправки запроса.
        var request = Request(server, path: "/assets/read", method: RequestMethod.Post,
            body: """{"messageKey":{{поле.message-id}},"assetKey":{{поле.attachment-id}}}""");
        var first = new Receiver("first", writes: false);
        var second = new Receiver("second");

        var outcome = await Run([
            AttachmentColumns(), ScenarioStep.OfDownload(request.Id, "{{download-name}}"),
            Action(first), Action(second),
        ], [first, second], AttachmentEvent(false, text));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Empty(server.Requests);
        foreach (var action in new[] { first, second })
        {
            var received = Assert.Single(action.Seen);
            Assert.Null(received.Bytes);
            Assert.Equal("", received.FileName);
            Assert.Equal(text, received.Values["title"]);
            Assert.Equal("42", received.Values["message-id"]);
            Assert.Equal("participant-17", received.Values["sender"]);
        }
        Assert.Equal(text, Assert.Single(_db.Constructor.GetRecords(_collection)).Title);
    }

    [Fact]
    public async Task Post_download_can_have_an_empty_request_body()
    {
        using var server = new FileServer(Payload);
        var request = Request(server, method: RequestMethod.Post);
        var action = new Receiver();

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}"), Action(action)],
            [action], Event());

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.StartsWith("POST /files/asset-23 ", Assert.Single(server.Requests), StringComparison.Ordinal);
        Assert.Equal("", Assert.Single(server.Bodies));
        Assert.Equal(Payload, Assert.Single(action.Seen).Bytes);
    }

    [Theory]
    [InlineData(RequestMethod.Get)]
    [InlineData(RequestMethod.Post)]
    public async Task Explicit_wait_repeats_only_the_file_read_and_delivers_one_ready_file_with_unchanged_context(
        RequestMethod method)
    {
        using var server = new FileServer(Payload, missingResponses: 1);
        var body = method == RequestMethod.Post
            ? """{"messageKey":{{поле.message-id}},"assetKey":{{поле.attachment-id}}}"""
            : "";
        var request = Request(server, path: "/assets/{{поле.attachment-id}}", method: method, body: body);
        var action = new Receiver();
        var journal = new RunJournal(Path.Combine(_dir, "wait-journal.db"));
        RunJournal.Attach(journal);
        using var stop = new CancellationTokenSource();

        var outcome = await Run([
            AttachmentColumns(), ScenarioStep.OfDownload(request.Id, "{{download-name}}", 4), Action(action),
        ], [action], AttachmentEvent(true), stop.Token).WaitAsync(TimeSpan.FromSeconds(8));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, head => Assert.StartsWith(
            method.ToString().ToUpperInvariant() + " /assets/23 ", head, StringComparison.Ordinal));
        Assert.Equal(server.Bodies[0], server.Bodies[1]);
        if (method == RequestMethod.Post)
        {
            foreach (var sent in server.Bodies)
            {
                using var json = JsonDocument.Parse(sent);
                Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("messageKey").ValueKind);
                Assert.Equal(42, json.RootElement.GetProperty("messageKey").GetInt32());
                Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("assetKey").ValueKind);
                Assert.Equal(23, json.RootElement.GetProperty("assetKey").GetInt32());
            }
        }
        else Assert.All(server.Bodies, sent => Assert.Equal("", sent));
        var received = Assert.Single(action.Seen);
        Assert.Equal(Payload, received.Bytes);
        Assert.Equal("Заявка.xlsx", received.FileName);
        Assert.Equal("Заявка участника", received.Values["title"]);
        Assert.Equal("participant-17", received.Values["sender"]);
        Assert.Equal("42", received.Values["message-id"]);
        Assert.Equal(stop.Token, received.Token);
        Assert.Equal("Заявка участника", Assert.Single(_db.Constructor.GetRecords(_collection)).Title);
        var logged = Assert.Single(journal.Recent(), entry => entry.Channel == RunChannel.Request);
        Assert.True(logged.Ok);
        Assert.Equal(request.Name, logged.Name);
    }

    [Theory]
    [InlineData(RequestMethod.Get)]
    [InlineData(RequestMethod.Post)]
    public async Task Default_wait_stops_on_the_first_404_even_if_the_next_read_would_succeed(RequestMethod method)
    {
        using var server = new FileServer(Payload, missingResponses: 1);
        var request = Request(server, method: method);
        var action = new Receiver();

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}"), Action(action)],
            [action], Event());

        Assert.False(outcome.Ok);
        Assert.Contains("404", outcome.Problem, StringComparison.Ordinal);
        Assert.Single(server.Requests);
        Assert.Empty(action.Seen);
        Assert.Empty(_db.Constructor.GetRecords(_collection));
    }

    [Fact]
    public async Task Waiting_keeps_the_original_numeric_request_context_when_the_source_row_and_saved_card_change()
    {
        using var server = new FileServer(Payload, missingResponses: 1);
        var request = Request(server, path: "/assets/{{поле.attachment-id}}", method: RequestMethod.Post,
            body: """{"messageKey":{{поле.message-id}},"assetKey":{{поле.attachment-id}}}""");
        var record = new Dictionary<string, string>
        {
            ["message-id"] = "42", ["attachment-id"] = "23",
        };

        var pending = RequestLauncher.DownloadAsync(Cards, Cards.Find(request.ConnectionId)!, request,
            record, "Ожидание файла", "1", DateTimeOffset.Now, waitSeconds: 4);
        await server.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        record["message-id"] = "900";
        record["attachment-id"] = "800";
        Cards.SaveRequest(request with { Path = "/different", Body = "{}" });

        var response = await pending.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.True(response.Ok, response.Explanation);
        Assert.Equal(Payload, response.Content);
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, head => Assert.StartsWith("POST /assets/23 ", head, StringComparison.Ordinal));
        Assert.All(server.Bodies, body =>
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(42, json.RootElement.GetProperty("messageKey").GetInt32());
            Assert.Equal(23, json.RootElement.GetProperty("assetKey").GetInt32());
        });
    }

    [Fact]
    public async Task Waiting_for_a_missing_file_expires_without_calling_the_action_or_flooding_the_server()
    {
        using var server = new FileServer(Payload, status: 404);
        var request = Request(server, method: RequestMethod.Post);
        var action = new Receiver();
        var watch = Stopwatch.StartNew();

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}", 1), Action(action)],
            [action], Event()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(outcome.Ok);
        Assert.Contains("404", outcome.Problem, StringComparison.Ordinal);
        Assert.Contains("ожид", outcome.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(server.Requests.Count, 1, 3);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(700), "Шаг не использовал включенное ожидание.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), "Шаг вышел за ограниченный бюджет ожидания.");
        Assert.Empty(action.Seen);
        Assert.Empty(_db.Constructor.GetRecords(_collection));
    }

    [Fact]
    public async Task Cancelling_while_waiting_after_404_does_not_send_another_request_or_run_the_action()
    {
        using var server = new FileServer(Payload, status: 404);
        var request = Request(server, method: RequestMethod.Post);
        var action = new Receiver();
        using var stop = new CancellationTokenSource();

        var pending = Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}", 30), Action(action)],
            [action], Event(), stop.Token);
        await server.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Ответ уже отправлен. Даем клиенту перейти к ожидающему продолжению, не дожидаясь
        // следующей секундной попытки: отмена должна прервать задержку, а не ждать ее конца.
        await Task.Delay(80);
        Assert.False(pending.IsCompleted);
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Single(server.Requests);
        Assert.Empty(action.Seen);
        Assert.Empty(_db.Constructor.GetRecords(_collection));
    }

    [Theory]
    [InlineData(302)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task Explicit_wait_does_not_repeat_a_redirect_or_a_different_http_failure(int status)
    {
        using var server = new FileServer(Payload, status: status);
        var request = Request(server, method: RequestMethod.Post);
        var action = new Receiver();

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}", 3), Action(action)],
            [action], Event()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(outcome.Ok);
        Assert.Single(server.Requests);
        Assert.Empty(action.Seen);
        Assert.Empty(_db.Constructor.GetRecords(_collection));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Empty_post_does_not_replay_when_the_connection_closes_without_an_http_response(int waitSeconds)
    {
        using var server = new FileServer(Payload, disconnectBeforeHeaders: true);
        var request = Request(server, method: RequestMethod.Post);
        var action = new Receiver();

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}", waitSeconds), Action(action)],
            [action], Event()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(outcome.Ok);
        Assert.Single(server.Requests);
        Assert.Empty(action.Seen);
        Assert.Empty(_db.Constructor.GetRecords(_collection));
    }

    [Theory]
    [InlineData("Обычный текст")]
    [InlineData("Да")]
    [InlineData("Нет")]
    public async Task Enabled_wait_is_skipped_for_text_events_and_confirmations_without_a_file(string text)
    {
        using var server = new FileServer(Payload, status: 404);
        var request = Request(server, path: "/assets/read", method: RequestMethod.Post,
            body: """{"assetKey":{{поле.attachment-id}}}""");
        var action = new Receiver();

        var outcome = await Run([
            AttachmentColumns(), ScenarioStep.OfDownload(request.Id, "{{download-name}}", 300), Action(action),
        ], [action], AttachmentEvent(false, text)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Empty(server.Requests);
        var received = Assert.Single(action.Seen);
        Assert.Null(received.Bytes);
        Assert.Equal("", received.FileName);
        Assert.Equal(text, received.Values["title"]);
        Assert.Single(_db.Constructor.GetRecords(_collection));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Text_event_without_a_filename_skips_network_and_still_reaches_the_action(string? fileName)
    {
        using var server = new FileServer(Payload);
        var request = Request(server);
        var action = new Receiver();

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}"), Action(action)],
            [action], Event(fileName));

        Assert.True(outcome.Ok, outcome.Problem);
        var received = Assert.Single(action.Seen);
        Assert.Null(received.Bytes);
        Assert.Equal("", received.FileName);
        Assert.Equal("message-42", received.Values["message-id"]);
        Assert.Empty(server.Requests);
        Assert.Single(_db.Constructor.GetRecords(_collection));
    }

    [Fact]
    public async Task Skipped_second_download_discards_the_first_file_instead_of_reusing_it()
    {
        using var server = new FileServer(Payload);
        var request = Request(server);
        var action = new Receiver();

        var outcome = await Run([
            ScenarioStep.OfDownload(request.Id, "{{file-name}}"),
            ScenarioStep.OfDownload(request.Id, "{{another-file-name}}"),
            Action(action),
        ], [action], Event());

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Single(server.Requests);
        var received = Assert.Single(action.Seen);
        Assert.Null(received.Bytes);
        Assert.Equal("", received.FileName);
    }

    [Fact]
    public async Task A_file_is_consumed_by_the_first_action_and_is_not_delivered_twice()
    {
        using var server = new FileServer(Payload);
        var request = Request(server);
        var first = new Receiver("first");
        var second = new Receiver("second", writes: false);

        var outcome = await Run([
            ScenarioStep.OfDownload(request.Id, "{{file-name}}"), Action(first), Action(second),
        ], [first, second], Event());

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Equal(Payload, Assert.Single(first.Seen).Bytes);
        var later = Assert.Single(second.Seen);
        Assert.Null(later.Bytes);
        Assert.Equal("", later.FileName);
        Assert.Equal("participant-17", later.Values["sender"]);
        Assert.Single(_db.Constructor.GetRecords(_collection));
    }

    [Fact]
    public async Task A_notice_between_download_and_action_does_not_lose_the_file()
    {
        using var server = new FileServer(Payload);
        var request = Request(server);
        var action = new Receiver();

        var outcome = await Run([
            ScenarioStep.OfDownload(request.Id, "{{file-name}}"),
            ScenarioStep.OfNotice("Получен файл"), Action(action),
        ], [action], Event());

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Equal(Payload, Assert.Single(action.Seen).Bytes);
    }

    [Theory]
    [InlineData(ExtensionActionKind.Operation)]
    [InlineData(ExtensionActionKind.Transformer)]
    [InlineData(ExtensionActionKind.Rows)]
    public async Task A_request_cannot_fan_out_one_attachment_into_several_action_inputs(ExtensionActionKind kind)
    {
        using var files = new FileServer(Payload);
        var download = Request(files);
        using var rows = new TinyServer(_ => TinyServer.Reply(200,
            """[{"title":"Первая","sender":"participant-17","message-id":"message-42"},{"title":"Вторая","sender":"participant-17","message-id":"message-42"}]"""));
        var connection = Cards.Save(IntegrationConnection.New() with
        {
            Name = "Источник строк", Address = $"http://127.0.0.1:{rows.Port}", TimeoutSeconds = 10, Retries = 0,
        });
        var request = Cards.SaveRequest(IntegrationRequest.New() with
        {
            Name = "Подменить строки", ConnectionId = connection.Id, Path = "/rows",
        });
        var copies = SampleData.AddCollection(_db.Constructor, "Копия ответа запроса").Id!.Value;
        Cards.SaveBinding(request.Id, new CollectionBinding(_db.Manager.DbId, copies, [new BindingColumn("title", null)]));
        var receiver = new Receiver();
        var reader = new FileReader(kind);
        IExtensionAction action = kind == ExtensionActionKind.Operation ? receiver : reader;
        var last = kind == ExtensionActionKind.Operation ? Action(receiver)
            : ScenarioStep.OfAction("file-stand", reader.Key, _db.Manager.DbId, _collection);

        var outcome = await Run([
            ScenarioStep.OfDownload(download.Id, "{{file-name}}"), ScenarioStep.OfRequest(request.Id), last,
        ], [action], Event());

        Assert.False(outcome.Ok);
        Assert.Single(files.Requests);
        Assert.Single(rows.Seen);
        Assert.Equal(["Первая", "Вторая"], _db.Constructor.GetRecords(copies).Select(record => record.Title));
        Assert.Empty(receiver.Seen);
        Assert.Empty(reader.Seen);
        Assert.Empty(_db.Constructor.GetRecords(_collection));
    }

    [Theory]
    [InlineData(ExtensionActionKind.Transformer)]
    [InlineData(ExtensionActionKind.Rows)]
    public async Task Binary_input_reaches_a_transformer_or_row_action_before_a_following_writer(ExtensionActionKind kind)
    {
        using var server = new FileServer(Payload);
        var request = Request(server);
        var reader = new FileReader(kind);
        var writer = new Receiver();
        using var stop = new CancellationTokenSource();

        var outcome = await Run([
            ScenarioStep.OfDownload(request.Id, "{{file-name}}"),
            ScenarioStep.OfAction("file-stand", reader.Key, _db.Manager.DbId, _collection), Action(writer),
        ], [reader, writer], Event(), stop.Token);

        Assert.True(outcome.Ok, outcome.Problem);
        var input = Assert.Single(reader.Seen);
        Assert.Equal(Payload, input.Bytes);
        Assert.Equal("Список КД.xlsx", input.FileName);
        Assert.Equal(stop.Token, Assert.Single(reader.Tokens));
        if (kind == ExtensionActionKind.Rows)
            Assert.Equal("asset-23", Assert.Single(input.Table)["asset-id"]);
        else
            Assert.Empty(input.Table);
        var received = Assert.Single(writer.Seen);
        Assert.Null(received.Bytes);
        Assert.Equal("", received.FileName);
        Assert.Equal("Из файла", received.Values["title"]);
        Assert.Equal("Из файла", Assert.Single(_db.Constructor.GetRecords(_collection)).Title);
    }

    [Fact]
    public async Task An_action_that_does_not_accept_files_is_not_called_with_missing_data()
    {
        using var server = new FileServer(Payload);
        var request = Request(server);
        var action = new Receiver(acceptsFile: false);

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}"), Action(action)],
            [action], Event());

        Assert.False(outcome.Ok);
        Assert.Contains("файл", outcome.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(action.Seen);
        Assert.Empty(_db.Constructor.GetRecords(_collection));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Download_requires_one_unambiguous_event_row(int count)
    {
        using var server = new FileServer(Payload);
        var request = Request(server);
        var action = new Receiver();
        var original = Event();
        var table = new StepTable(original.Columns, Enumerable.Repeat(original.Rows[0], count).ToArray());

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}"), Action(action)],
            [action], table);

        Assert.False(outcome.Ok);
        Assert.Empty(server.Requests);
        Assert.Empty(action.Seen);
        Assert.Empty(_db.Constructor.GetRecords(_collection));
    }

    [Theory]
    [InlineData("../list.xlsx")]
    [InlineData("folder/list.xlsx")]
    [InlineData("folder\\list.xlsx")]
    [InlineData("list?.xlsx")]
    [InlineData("list:part.xlsx")]
    [InlineData("list\n.xlsx")]
    public async Task Invalid_attachment_names_are_rejected_before_download(string fileName)
    {
        using var server = new FileServer(Payload);
        var request = Request(server);
        var action = new Receiver();

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}"), Action(action)],
            [action], Event(fileName));

        Assert.False(outcome.Ok);
        Assert.Empty(server.Requests);
        Assert.Empty(action.Seen);
    }

    [Fact]
    public async Task Filename_length_is_checked_after_expanding_the_event_column()
    {
        using var server = new FileServer(Payload);
        var request = Request(server);
        var action = new Receiver();

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}"), Action(action)],
            [action], Event(new string('a', ScenarioStep.MaxTextLength + 1) + ".xlsx"));

        Assert.False(outcome.Ok);
        Assert.Empty(server.Requests);
        Assert.Empty(action.Seen);
    }

    [Theory]
    [InlineData(RequestMethod.Put, RequestPaging.None)]
    [InlineData(RequestMethod.Patch, RequestPaging.None)]
    [InlineData(RequestMethod.Delete, RequestPaging.None)]
    [InlineData(RequestMethod.Get, RequestPaging.Page)]
    [InlineData(RequestMethod.Get, RequestPaging.Offset)]
    [InlineData(RequestMethod.Get, RequestPaging.Cursor)]
    [InlineData(RequestMethod.Post, RequestPaging.Page)]
    [InlineData(RequestMethod.Post, RequestPaging.Offset)]
    [InlineData(RequestMethod.Post, RequestPaging.Cursor)]
    public async Task Download_does_not_run_an_unsupported_method_or_paginated_request(RequestMethod method, RequestPaging paging)
    {
        using var server = new FileServer(Payload);
        var request = Request(server, method: method, paging: paging);
        var action = new Receiver();

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}"), Action(action)],
            [action], Event());

        Assert.False(outcome.Ok);
        Assert.Empty(server.Requests);
        Assert.Empty(action.Seen);
        Assert.Empty(_db.Constructor.GetRecords(_collection));
    }

    [Fact]
    public void A_get_download_request_with_a_body_cannot_be_saved_for_a_scenario()
    {
        using var server = new FileServer(Payload);
        var request = Request(server);

        var error = Assert.Throws<InvalidOperationException>(() =>
            Cards.SaveRequest(request with { Body = "{}" }));

        Assert.Contains("GET", error.Message, StringComparison.Ordinal);
        Assert.Equal("", Cards.FindRequest(request.Id)!.Body);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Unknown_request_path_columns_stop_the_scenario_before_network_work()
    {
        using var server = new FileServer(Payload);
        var request = Request(server, path: "/files/{{поле.missing-asset}}");
        var action = new Receiver();

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}"), Action(action)],
            [action], Event());

        Assert.False(outcome.Ok);
        Assert.Contains("missing-asset", outcome.Problem, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
        Assert.Empty(action.Seen);
    }

    [Fact]
    public async Task Http_error_stops_before_the_action_and_never_writes_a_record()
    {
        using var server = new FileServer(Payload, status: 500);
        var request = Request(server);
        var action = new Receiver();

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}"), Action(action)],
            [action], Event());

        Assert.False(outcome.Ok);
        Assert.Contains("500", outcome.Problem, StringComparison.Ordinal);
        Assert.Single(server.Requests);
        Assert.Empty(action.Seen);
        Assert.Empty(_db.Constructor.GetRecords(_collection));
    }

    [Fact]
    public async Task A_truncated_response_is_not_forwarded_as_a_ready_attachment()
    {
        using var server = new FileServer(Payload, declaredLength: Payload.Length + 100);
        var request = Request(server);
        var action = new Receiver();

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}"), Action(action)],
            [action], Event());

        Assert.False(outcome.Ok);
        Assert.Empty(action.Seen);
        Assert.Empty(_db.Constructor.GetRecords(_collection));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(404)]
    public async Task File_larger_than_the_transport_ceiling_stops_without_calling_the_action_or_waiting_again(int status)
    {
        using var server = new FileServer(new byte[checked((int)EgressHttp.MaxResponseBytes + 1)], status: status);
        var request = Request(server);
        var action = new Receiver();

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}", 3), Action(action)],
            [action], Event()).WaitAsync(TimeSpan.FromSeconds(8));

        Assert.False(outcome.Ok);
        Assert.Single(server.Requests);
        Assert.Empty(action.Seen);
        Assert.Empty(_db.Constructor.GetRecords(_collection));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public async Task Cancellation_during_download_never_calls_the_action(int waitSeconds)
    {
        using var server = new FileServer(Payload, declaredLength: Payload.Length + 100, stall: true);
        var request = Request(server);
        var action = new Receiver();
        using var stop = new CancellationTokenSource();
        var pending = Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}", waitSeconds), Action(action)],
            [action], Event(), stop.Token);
        await server.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Empty(action.Seen);
        Assert.Empty(_db.Constructor.GetRecords(_collection));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public async Task Missing_event_columns_become_empty_only_for_explicitly_optional_inputs(
        bool missingIsEmpty, bool required, bool expectedSuccess)
    {
        var action = new Receiver(optionalEmpty: missingIsEmpty, optionalRequired: required);
        var step = Action(action);
        var values = new Dictionary<string, string>(step.Fields) { ["optional"] = "{{absent-column}}" };

        var outcome = await Run([step with { FieldValues = values }], [action], Event());

        Assert.Equal(expectedSuccess, outcome.Ok);
        if (expectedSuccess)
        {
            Assert.Equal("", Assert.Single(action.Seen).Values["optional"]);
            Assert.Single(_db.Constructor.GetRecords(_collection));
        }
        else
        {
            Assert.Empty(action.Seen);
            Assert.Empty(_db.Constructor.GetRecords(_collection));
        }
    }

    private IntegrationRequest Request(FileServer server, string path = "/files/{{поле.asset-id}}",
        RequestMethod method = RequestMethod.Get, RequestPaging paging = RequestPaging.None, string body = "")
    {
        var connection = Cards.Save(IntegrationConnection.New() with
        {
            Name = "Файловое соединение " + Cards.Connections().Count,
            Address = $"http://127.0.0.1:{server.Port}", TimeoutSeconds = 10, Retries = 0,
        });
        return Cards.SaveRequest(IntegrationRequest.New() with
        {
            Name = "Получение вложения " + Cards.Requests().Count,
            ConnectionId = connection.Id, Path = path, Method = method, Body = body,
            Paging = paging, PagingField = paging == RequestPaging.None ? "" : "page",
        });
    }

    private ScenarioStep Action(Receiver action) => ScenarioStep.OfAction("file-stand", action.Key,
        _db.Manager.DbId, _collection, new Dictionary<string, string>
        {
            ["title"] = "{{title}}", ["sender"] = "{{sender}}", ["message-id"] = "{{message-id}}",
        });

    private Task<ScenarioOutcome> Run(IReadOnlyList<ScenarioStep> steps, IReadOnlyList<IExtensionAction> actions,
        StepTable initial, CancellationToken ct = default) => ScenarioRunner.RunAsync(
            Scenario.New() with { Name = "Файл участника", Steps = steps }, Cards, [_db.Manager],
            [new Stand(actions)], DateTimeOffset.Now, _ => { }, ct, initial);

    private static StepTable Event(string? fileName = "Список КД.xlsx")
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = "Заявка участника", ["sender"] = "participant-17",
            ["message-id"] = "message-42", ["asset-id"] = "asset-23",
        };
        if (fileName is not null) row["file-name"] = fileName;
        return new StepTable(row.Keys.ToArray(), [row]);
    }

    private static ScenarioStep AttachmentColumns() => ScenarioStep.OfCompute(
        new ScenarioFormula("attachment-id",
            """ЕСЛИ({{has-attachment}}; ЧИСЛО(ЗАМЕНИТЬ(ЗАМЕНИТЬ({{attachment-ids}}; "["; ""); "]"; "")); "")"""),
        new ScenarioFormula("download-name", """ЕСЛИ({{has-attachment}}; "Заявка.xlsx"; "")"""));

    private static StepTable AttachmentEvent(bool hasAttachment, string text = "Заявка участника")
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = text, ["sender"] = "participant-17", ["message-id"] = "42",
            ["has-attachment"] = hasAttachment ? "true" : "false",
            ["attachment-ids"] = hasAttachment ? "[23]" : "[]",
        };
        return new StepTable(row.Keys.ToArray(), [row]);
    }

    private sealed class Stand(IReadOnlyList<IExtensionAction> actions) : IExtension
    {
        public string Id => "file-stand";
        public string Name => "Прием файлов";
        public string MarkdownDescription => "";
        public bool CanRun => false;
        public IReadOnlyList<IExtensionAction> Actions => actions;
        public Window? CreateWindow() => null;
    }

    private sealed record Receipt(byte[]? Bytes, string FileName, IReadOnlyDictionary<string, string> Values,
        CancellationToken Token);

    private sealed class FileReader(ExtensionActionKind kind) : IExtensionAction
    {
        public string Key => "file-reader";
        public string Name => "Разобрать вложение";
        public string Description => "Стендовый разбор файла";
        public ExtensionActionKind Kind => kind;
        public bool AcceptsFile => true;
        public List<ActionInput> Seen { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public ActionResult Run(ActionContext context, ActionInput input)
        {
            Seen.Add(input);
            Tokens.Add(context.CancellationToken);
            return ActionResult.Table(
                """[{"title":"Из файла","sender":"participant-17","message-id":"message-42"}]""", 1);
        }
    }

    private sealed class Receiver(string key = "receive", bool acceptsFile = true, bool writes = true,
        bool? optionalEmpty = null, bool optionalRequired = false) : IExtensionAction
    {
        public string Key => key;
        public string Name => "Принять данные";
        public string Description => "Стендовое действие";
        public ExtensionActionKind Kind => ExtensionActionKind.Operation;
        public bool AcceptsFile => acceptsFile;
        public IReadOnlyList<ExtensionActionField> Fields => optionalEmpty is bool empty
            ? [new("title", "Название"), new("sender", "Отправитель"), new("message-id", "Сообщение"),
                new("optional", "Дополнительно", Required: optionalRequired, MissingIsEmpty: empty)]
            : [new("title", "Название"), new("sender", "Отправитель"), new("message-id", "Сообщение")];
        public List<Receipt> Seen { get; } = [];

        public ActionResult Run(ActionContext context, ActionInput input)
        {
            Seen.Add(new Receipt(input.Bytes?.ToArray(), input.FileName,
                new Dictionary<string, string>(input.Values), context.CancellationToken));
            if (writes)
                context.Repo!.AddRecord(new DbRecord { CollectionId = context.CollectionId, Title = input.Value("title") });
            return ActionResult.Done("Данные приняты", writes ? 1 : 0);
        }
    }

    public void Dispose()
    {
        RunJournal.Attach(null);
        _registry.Dispose();
        _db.Dispose();
        TempDatabase.Cleanup(Path.Combine(_dir, "app.db"));
        TempDatabase.Cleanup(Path.Combine(_dir, "database.db"));
        try { Directory.Delete(_dir, recursive: true); } catch { /* Временная папка стенда. */ }
    }

    /// <summary>Двоичный ответ не проходит через строковое перекодирование тестового сервера.</summary>
    private sealed class FileServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<string> _requests = new();
        private readonly ConcurrentQueue<string> _bodies = new();
        private readonly byte[] _content;
        private readonly int _status;
        private readonly int _length;
        private readonly bool _stall;
        private readonly int _missingResponses;
        private readonly bool _disconnectBeforeHeaders;
        private readonly string? _contentDisposition;
        private readonly Task _work;
        public int Port { get; }
        public IReadOnlyList<string> Requests => _requests.ToArray();
        public IReadOnlyList<string> Bodies => _bodies.ToArray();
        public TaskCompletionSource BodyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FileServer(byte[] content, int status = 200, int? declaredLength = null, bool stall = false,
            int missingResponses = 0, bool disconnectBeforeHeaders = false, string? contentDisposition = null)
        {
            _content = content;
            _status = status;
            _length = declaredLength ?? content.Length;
            _stall = stall;
            _missingResponses = missingResponses;
            _disconnectBeforeHeaders = disconnectBeforeHeaders;
            _contentDisposition = contentDisposition;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _work = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    using var head = new MemoryStream();
                    var one = new byte[1];
                    while (head.Length < 16 * 1024)
                    {
                        if (await stream.ReadAsync(one, _stop.Token) == 0) return;
                        head.WriteByte(one[0]);
                        if (head.Length >= 4 && Encoding.ASCII.GetString(head.GetBuffer(), (int)head.Length - 4, 4) == "\r\n\r\n") break;
                    }
                    var requestHead = Encoding.ASCII.GetString(head.ToArray());
                    var lengthHeader = requestHead.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    var bodyLength = lengthHeader is null ? 0 : int.Parse(lengthHeader["Content-Length:".Length..].Trim());
                    var body = new byte[bodyLength];
                    var read = 0;
                    while (read < body.Length)
                    {
                        var count = await stream.ReadAsync(body.AsMemory(read), _stop.Token);
                        if (count == 0) return;
                        read += count;
                    }
                    _requests.Enqueue(requestHead);
                    _bodies.Enqueue(Encoding.UTF8.GetString(body));
                    if (_disconnectBeforeHeaders) continue;
                    var status = _requests.Count <= _missingResponses ? 404 : _status;
                    var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Reply\r\n"
                        + (_contentDisposition is null ? "" : $"Content-Disposition: {_contentDisposition}\r\n")
                        + $"Content-Type: application/octet-stream\r\nContent-Length: {_length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _stop.Token);
                    await stream.WriteAsync(_content, _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                    BodyStarted.TrySetResult();
                    if (_stall) await Task.Delay(Timeout.Infinite, _stop.Token);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // Отмена и прекращение чтения большого файла закрывают соединение раньше сервера.
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _work.GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
