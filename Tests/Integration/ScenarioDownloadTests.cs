using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
    [InlineData(RequestMethod.Post, RequestPaging.None)]
    [InlineData(RequestMethod.Get, RequestPaging.Page)]
    public async Task Download_does_not_run_an_outgoing_or_paginated_request(RequestMethod method, RequestPaging paging)
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

    [Fact]
    public async Task File_larger_than_the_transport_ceiling_stops_without_calling_the_action()
    {
        using var server = new FileServer(new byte[checked((int)EgressHttp.MaxResponseBytes + 1)]);
        var request = Request(server);
        var action = new Receiver();

        var outcome = await Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}"), Action(action)],
            [action], Event());

        Assert.False(outcome.Ok);
        Assert.Empty(action.Seen);
        Assert.Empty(_db.Constructor.GetRecords(_collection));
    }

    [Fact]
    public async Task Cancellation_during_download_never_calls_the_action()
    {
        using var server = new FileServer(Payload, declaredLength: Payload.Length + 100, stall: true);
        var request = Request(server);
        var action = new Receiver();
        using var stop = new CancellationTokenSource();
        var pending = Run([ScenarioStep.OfDownload(request.Id, "{{file-name}}"), Action(action)],
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
        RequestMethod method = RequestMethod.Get, RequestPaging paging = RequestPaging.None)
    {
        var connection = Cards.Save(IntegrationConnection.New() with
        {
            Name = "Файловое соединение " + Cards.Connections().Count,
            Address = $"http://127.0.0.1:{server.Port}", TimeoutSeconds = 10, Retries = 0,
        });
        return Cards.SaveRequest(IntegrationRequest.New() with
        {
            Name = "Получение вложения " + Cards.Requests().Count,
            ConnectionId = connection.Id, Path = path, Method = method,
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
        private readonly byte[] _content;
        private readonly int _status;
        private readonly int _length;
        private readonly bool _stall;
        private readonly Task _work;
        public int Port { get; }
        public IReadOnlyList<string> Requests => _requests.ToArray();
        public TaskCompletionSource BodyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FileServer(byte[] content, int status = 200, int? declaredLength = null, bool stall = false)
        {
            _content = content;
            _status = status;
            _length = declaredLength ?? content.Length;
            _stall = stall;
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
                    _requests.Enqueue(Encoding.ASCII.GetString(head.ToArray()));
                    var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {_status} Reply\r\n"
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
