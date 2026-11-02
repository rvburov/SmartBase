using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>Файл доходит байт в байт; неполный ответ, ошибка и отмена не становятся готовым файлом.</summary>
[Collection(EgressCollection.Name)]
public sealed class EgressBinaryTests
{
    private static readonly byte[] FileBytes = [0x50, 0x4b, 0, 255, 192, 128, 13, 10, 0, 254];

    [Theory]
    [InlineData(RequestMethod.Get)]
    [InlineData(RequestMethod.Post)]
    public async Task Download_keeps_non_utf8_bytes_and_does_not_turn_them_into_text(RequestMethod method)
    {
        using var server = new BinaryServer(FileBytes);

        var answer = await EgressHttp.DownloadAsync(Plan(server.Port, method));

        Assert.True(answer.Ok, answer.Explanation);
        Assert.Equal(FileBytes, Assert.IsType<byte[]>(answer.Content));
        Assert.Equal(FileBytes.Length, answer.Bytes);
        Assert.Equal("", answer.Body);
        Assert.False(answer.Truncated);
        Assert.Equal(1, answer.Attempts);
        Assert.StartsWith($"{method.ToString().ToUpperInvariant()} /file ", Assert.Single(server.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_substitutes_event_fields_as_json_numbers_and_keeps_the_binary_answer()
    {
        using var server = new BinaryServer(FileBytes);
        var connection = Connection(server.Port);
        var request = IntegrationRequest.New() with
        {
            Name = "Получить файл", Method = RequestMethod.Post, Path = "/assets/read",
            Body = """{"messageKey": {{поле.event-key}}, "assetKey": {{поле.computed-asset-key}}}""",
        };
        var context = VariableContext.Now(null, connection.Address, new Dictionary<string, string>
        {
            ["event-key"] = "42", ["computed-asset-key"] = "23",
        });

        var answer = await RequestRunner.DownloadAsync(connection, request, "", context);

        Assert.True(answer.Ok, answer.Explanation);
        Assert.Equal(FileBytes, Assert.IsType<byte[]>(answer.Content));
        Assert.Empty(answer.Body);
        var sent = Assert.Single(server.Requests);
        Assert.StartsWith("POST /assets/read ", sent, StringComparison.Ordinal);
        Assert.Contains("Content-Type: application/json", sent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{{", sent, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(sent[(sent.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
        Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("messageKey").ValueKind);
        Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("assetKey").ValueKind);
        Assert.Equal(42, json.RootElement.GetProperty("messageKey").GetInt32());
        Assert.Equal(23, json.RootElement.GetProperty("assetKey").GetInt32());
    }

    [Fact]
    public async Task Unknown_post_body_field_stops_before_sending_any_request()
    {
        using var server = new BinaryServer(FileBytes);
        var connection = Connection(server.Port);
        var request = IntegrationRequest.New() with
        {
            Name = "Получить файл", Method = RequestMethod.Post, Path = "/assets/read",
            Body = """{"messageKey": {{поле.event-key}}, "assetKey": {{поле.computed-asset-key}}}""",
        };
        var context = VariableContext.Now(null, connection.Address, new Dictionary<string, string>
        {
            ["event-key"] = "42",
        });

        var answer = await RequestRunner.DownloadAsync(connection, request, "", context);

        Assert.False(answer.Sent);
        Assert.Contains("поле.computed-asset-key", answer.Explanation, StringComparison.Ordinal);
        Assert.Null(answer.Content);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Ordinary_request_still_decodes_text_and_has_no_binary_payload()
    {
        const string text = "Ответ сторонней программы";
        using var server = new BinaryServer(Encoding.UTF8.GetBytes(text));

        var answer = await EgressHttp.SendAsync(Plan(server.Port));

        Assert.True(answer.Ok, answer.Explanation);
        Assert.Equal(text, answer.Body);
        Assert.Null(answer.Content);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), answer.Bytes);
    }

    [Theory]
    [InlineData(RequestMethod.Get)]
    [InlineData(RequestMethod.Post)]
    public async Task Lower_ceiling_discards_the_whole_incomplete_file(RequestMethod method)
    {
        using var server = new BinaryServer(Enumerable.Repeat((byte)255, 5000).ToArray());

        var answer = await EgressHttp.DownloadAsync(Plan(server.Port, method), maxResponseBytes: 1000);

        Assert.True(answer.Sent);
        Assert.True(answer.Truncated);
        Assert.Equal(1000, answer.Bytes);
        Assert.Null(answer.Content);
        Assert.Empty(answer.Body);
    }

    [Theory]
    [InlineData(RequestMethod.Get)]
    [InlineData(RequestMethod.Post)]
    public async Task Empty_response_is_a_complete_zero_byte_payload(RequestMethod method)
    {
        using var server = new BinaryServer([]);

        var answer = await EgressHttp.DownloadAsync(Plan(server.Port, method), maxResponseBytes: 0);

        Assert.True(answer.Ok, answer.Explanation);
        Assert.NotNull(answer.Content);
        Assert.Empty(answer.Content);
        Assert.Equal(0, answer.Bytes);
        Assert.False(answer.Truncated);
    }

    [Theory]
    [InlineData(RequestMethod.Put, "")]
    [InlineData(RequestMethod.Patch, "")]
    [InlineData(RequestMethod.Delete, "")]
    [InlineData(RequestMethod.Get, "тело")]
    public async Task Download_rejects_unsupported_methods_and_get_with_body(RequestMethod method, string body)
    {
        using var server = new BinaryServer(FileBytes);

        var answer = await EgressHttp.DownloadAsync(Plan(server.Port) with { Method = method, Body = body });

        Assert.False(answer.Sent);
        Assert.Contains("GET без тела или POST", answer.Explanation, StringComparison.Ordinal);
        Assert.Null(answer.Content);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData(RequestMethod.Get, "тело", RequestPaging.None)]
    [InlineData(RequestMethod.Put, "", RequestPaging.None)]
    [InlineData(RequestMethod.Patch, "", RequestPaging.None)]
    [InlineData(RequestMethod.Delete, "", RequestPaging.None)]
    [InlineData(RequestMethod.Get, "", RequestPaging.Page)]
    [InlineData(RequestMethod.Post, "{}", RequestPaging.Page)]
    [InlineData(RequestMethod.Post, "{}", RequestPaging.Offset)]
    [InlineData(RequestMethod.Post, "{}", RequestPaging.Cursor)]
    public async Task Request_runner_rejects_unsupported_downloads_before_sending(
        RequestMethod method, string body, RequestPaging paging)
    {
        using var server = new BinaryServer(FileBytes);
        var connection = Connection(server.Port);
        var request = IntegrationRequest.New() with
        {
            Name = "Получить файл", Path = "/file", Method = method, Body = body,
            Paging = paging, PagingField = "page",
        };

        var answer = await RequestRunner.DownloadAsync(connection, request, "", VariableContext.Now(null));

        Assert.False(answer.Sent);
        Assert.Contains("GET без тела или POST", answer.Explanation, StringComparison.Ordinal);
        Assert.Null(answer.Content);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Non_http_address_is_rejected_without_opening_a_file()
    {
        var answer = await EgressHttp.DownloadAsync(Plan(1) with
        {
            Address = new Uri("file:///not-a-service"), SafeAddress = "file:///not-a-service",
        });

        Assert.False(answer.Sent);
        Assert.Contains("http или https", answer.Explanation, StringComparison.Ordinal);
        Assert.Null(answer.Content);
    }

    [Fact]
    public void Negative_ceiling_is_rejected_before_network_work()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = EgressHttp.DownloadAsync(Plan(1), maxResponseBytes: -1);
        });
    }

    [Theory]
    [InlineData(RequestMethod.Get, 400)]
    [InlineData(RequestMethod.Get, 401)]
    [InlineData(RequestMethod.Get, 403)]
    [InlineData(RequestMethod.Get, 404)]
    [InlineData(RequestMethod.Get, 500)]
    [InlineData(RequestMethod.Post, 400)]
    [InlineData(RequestMethod.Post, 401)]
    [InlineData(RequestMethod.Post, 403)]
    [InlineData(RequestMethod.Post, 404)]
    [InlineData(RequestMethod.Post, 500)]
    public async Task Http_error_body_is_not_offered_as_a_downloaded_file(RequestMethod method, int status)
    {
        using var server = new BinaryServer(FileBytes, status);

        var answer = await EgressHttp.DownloadAsync(Plan(server.Port, method));

        Assert.True(answer.Sent);
        Assert.False(answer.Ok);
        Assert.Equal(status, answer.StatusCode);
        Assert.Null(answer.Content);
        Assert.Empty(answer.Body);
    }

    [Theory]
    [InlineData(RequestMethod.Get)]
    [InlineData(RequestMethod.Post)]
    public async Task Cancellation_during_the_body_drops_its_received_prefix(RequestMethod method)
    {
        using var server = new BinaryServer(FileBytes, stall: true);
        using var stop = new CancellationTokenSource();
        var pending = EgressHttp.DownloadAsync(Plan(server.Port, method), stop.Token);
        await server.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();
        var answer = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(answer.Sent);
        Assert.Equal("отменено", answer.Explanation);
        Assert.Null(answer.Content);
        Assert.Empty(answer.Body);
        Assert.Equal(1, answer.Attempts);
    }

    [Theory]
    [InlineData(RequestMethod.Get)]
    [InlineData(RequestMethod.Post)]
    public async Task Timeout_covers_a_file_body_that_stops_after_headers(RequestMethod method)
    {
        using var server = new TinyServer(_ => null, stallAfterHeaders: true);

        var answer = await EgressHttp.DownloadAsync(Plan(server.Port, method) with { TimeoutSeconds = 1 })
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(answer.Sent);
        Assert.Contains("не ответил", answer.Explanation, StringComparison.Ordinal);
        Assert.Null(answer.Content);
        Assert.Equal(1, answer.Attempts);
    }

    [Fact]
    public async Task Redirect_downloads_exact_bytes_without_forwarding_connection_credentials()
    {
        using var target = new BinaryServer(FileBytes);
        using var source = new TinyServer(_ => TinyServer.Reply(302, "", "text/plain",
            $"Location: http://127.0.0.1:{target.Port}/file"));
        var plan = Plan(source.Port) with
        {
            Headers = [new IntegrationHeader("X-Service-Key", "fake-download-secret")],
            SecretHeader = "X-Service-Key",
        };

        var answer = await EgressHttp.DownloadAsync(plan);

        Assert.True(answer.Ok, answer.Explanation);
        Assert.Equal(FileBytes, Assert.IsType<byte[]>(answer.Content));
        Assert.Contains("перенаправлений: 1", answer.Explanation, StringComparison.Ordinal);
        Assert.Equal("fake-download-secret", Assert.Single(source.Seen).Header("X-Service-Key"));
        Assert.DoesNotContain("X-Service-Key", Assert.Single(target.Requests), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Redirect_loop_never_returns_its_body_as_a_file()
    {
        using var server = new TinyServer(_ => TinyServer.Reply(302, "not a file", "text/plain",
            "Location: /file"));

        var answer = await EgressHttp.DownloadAsync(Plan(server.Port));

        Assert.True(answer.Redirected);
        Assert.False(answer.Ok);
        Assert.Null(answer.Content);
        Assert.Empty(answer.Body);
        Assert.Contains("перенаправлений подряд", answer.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_retries_a_broken_body_and_returns_only_the_complete_attempt()
    {
        using var server = new TinyServer(_ => TinyServer.Reply(200, "complete", "application/octet-stream"),
            truncateFirst: true);

        var answer = await EgressHttp.DownloadAsync(Plan(server.Port) with { Retries = 1 });

        Assert.True(answer.Ok, answer.Explanation);
        Assert.Equal(Encoding.UTF8.GetBytes("complete"), Assert.IsType<byte[]>(answer.Content));
        Assert.Equal(2, answer.Attempts);
        Assert.Equal(2, server.Seen.Count);
    }

    [Fact]
    public async Task Post_preserves_a_configured_non_json_body()
    {
        const string body = "messageKey=42&assetKey=23";
        using var server = new TinyServer(_ => TinyServer.Reply(200, "complete", "application/octet-stream"));

        var answer = await EgressHttp.DownloadAsync(Plan(server.Port, RequestMethod.Post, body));

        Assert.True(answer.Ok, answer.Explanation);
        Assert.Equal(Encoding.UTF8.GetBytes("complete"), Assert.IsType<byte[]>(answer.Content));
        var sent = Assert.Single(server.Seen);
        Assert.Equal("POST", sent.Method);
        Assert.Equal(body, sent.Body);
        Assert.Equal("application/x-www-form-urlencoded", sent.Header("Content-Type"));
    }

    [Fact]
    public async Task Post_does_not_repeat_a_broken_file_body_even_when_retries_are_configured()
    {
        using var server = new TinyServer(_ => TinyServer.Reply(200, "complete", "application/octet-stream"),
            truncateFirst: true);

        var answer = await EgressHttp.DownloadAsync(Plan(server.Port, RequestMethod.Post, "{}") with { Retries = 3 });

        Assert.False(answer.Ok);
        Assert.Null(answer.Content);
        Assert.Empty(answer.Body);
        Assert.Equal(1, answer.Attempts);
        Assert.Single(server.Seen);
    }

    [Theory]
    [InlineData(303)]
    [InlineData(307)]
    public async Task Post_does_not_follow_a_redirect_or_return_its_body_as_a_file(int status)
    {
        using var target = new BinaryServer(FileBytes);
        using var source = new TinyServer(_ => TinyServer.Reply(status, "not a file", "text/plain",
            $"Location: http://127.0.0.1:{target.Port}/file"));

        var answer = await EgressHttp.DownloadAsync(Plan(source.Port, RequestMethod.Post, "{}") with { Retries = 3 });

        Assert.True(answer.Redirected);
        Assert.False(answer.Ok);
        Assert.Equal(status, answer.StatusCode);
        Assert.Null(answer.Content);
        Assert.Empty(answer.Body);
        Assert.Equal(1, answer.Attempts);
        Assert.Contains("только у GET", answer.Explanation, StringComparison.Ordinal);
        Assert.Single(source.Seen);
        Assert.Empty(target.Requests);
    }

    private static IntegrationConnection Connection(int port) =>
        IntegrationConnection.New() with
        {
            Name = "Сервис файлов", Address = $"http://127.0.0.1:{port}", TimeoutSeconds = 10, Retries = 0,
        };

    private static EgressPlan Plan(int port, RequestMethod method = RequestMethod.Get, string body = "")
    {
        var request = IntegrationRequest.New() with
        {
            Name = "Получить файл", Path = "/file", Method = method, Body = body,
        };
        var plan = EgressPlan.Build(Connection(port), request, "", out var error);
        Assert.True(plan is not null, error);
        return plan!;
    }

    /// <summary>Отдает произвольные байты, которые строковый стенд перекодировал бы в UTF-8.</summary>
    private sealed class BinaryServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<string> _requests = new();
        private readonly byte[] _content;
        private readonly int _status;
        private readonly bool _stall;
        private readonly Task _work;
        internal int Port { get; }
        internal IReadOnlyList<string> Requests => _requests.ToArray();
        internal TaskCompletionSource BodyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal BinaryServer(byte[] content, int status = 200, bool stall = false)
        {
            _content = content;
            _status = status;
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
                        var got = await stream.ReadAsync(one, _stop.Token);
                        if (got == 0) return;
                        head.WriteByte(one[0]);
                        if (head.Length >= 4 && Encoding.ASCII.GetString(head.GetBuffer(), (int)head.Length - 4, 4) == "\r\n\r\n")
                            break;
                    }
                    var requestHead = Encoding.ASCII.GetString(head.ToArray());
                    var length = requestHead.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    var requestBody = Array.Empty<byte>();
                    if (length is not null && int.TryParse(length["Content-Length:".Length..].Trim(), out var count)
                        && count > 0)
                    {
                        requestBody = new byte[count];
                        var got = 0;
                        while (got < count)
                        {
                            var read = await stream.ReadAsync(requestBody.AsMemory(got, count - got), _stop.Token);
                            if (read == 0) return;
                            got += read;
                        }
                    }
                    _requests.Enqueue(requestHead + Encoding.UTF8.GetString(requestBody));
                    var size = _stall ? _content.Length + 100 : _content.Length;
                    var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {_status} Reply\r\n"
                        + $"Content-Type: application/octet-stream\r\nContent-Length: {size}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _stop.Token);
                    await stream.WriteAsync(_content, _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                    BodyStarted.TrySetResult();
                    if (_stall) await Task.Delay(Timeout.Infinite, _stop.Token);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or SocketException
                                             or ObjectDisposedException)
            {
                // Тест может остановить загрузку либо сервер, не дочитав ответ.
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
