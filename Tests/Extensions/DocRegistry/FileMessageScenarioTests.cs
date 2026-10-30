using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database;
using SmartBase.Database.Repositories;
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

    private Scenario Scenario(FileServer server)
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
            Path = "/files/{{поле.asset-id}}", Method = RequestMethod.Get,
        });
        Assert.Null(cards.Binding(request.Id));
        var stream = cards.SaveStream(IntegrationStream.New() with
        {
            Name = "События", ConnectionId = connection.Id, Path = "/events", ItemsPath = "data",
        });
        return cards.SaveScenario(SmartBase.Integrations.Scenario.New() with
        {
            Name = "Книга из сообщения", Enabled = true, Trigger = ScenarioTrigger.OnStream(stream.Id),
            Steps =
            [
                ScenarioStep.OfDownload(request.Id, "{{file-name}}"),
                ScenarioStep.OfAction(DocRegistryExtension.ExtensionId, RegisterMessageAction.ActionKey,
                    _db.Manager.DbId, _shape.CollectionId, new Dictionary<string, string>
                    {
                        [RegisterMessageAction.TextField] = "{{body}}",
                        [RegisterMessageAction.SenderField] = "{{author}}",
                        [RegisterMessageAction.MessageIdField] = "{{event-id}}",
                    }),
            ],
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
        internal int Port { get; }
        internal ConcurrentQueue<string> Requests { get; } = new();

        internal FileServer(byte[] content, int status = 200)
        {
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Result\r\nContent-Type: application/octet-stream\r\n"
                + $"Content-Length: {content.Length}\r\nConnection: close\r\n\r\n");
            _answer = new byte[header.Length + content.Length];
            header.CopyTo(_answer, 0);
            content.CopyTo(_answer, header.Length);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

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
                    Requests.Enqueue(request.ToString().Split('\r', '\n')[0]);
                    await stream.WriteAsync(_answer, _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException
                    or ObjectDisposedException or SocketException) { }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch (SocketException) { }
            _stop.Dispose();
        }
    }
}
