using System.Net;
using System.Net.Sockets;
using System.Text;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>Общая очередь тестов обмена: рабочий журнал и настройки процесса имеют статическое состояние.</summary>
[CollectionDefinition(EgressCollection.Name)]
public sealed class EgressCollection
{
    public const string Name = "Шлюз интеграций";
}

/// <summary>Что сервер увидел в запросе.</summary>
internal sealed record Seen(string Method, string Path, IReadOnlyList<IntegrationHeader> Headers, string Body)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}


/// <summary>
/// Сервер на полсотни строк: принять соединение, прочитать запрос, ответить заготовленной
/// строкой. Ответ <c>null</c> — соединение обрывается молча; так проверяются повторы и
/// таймаут.
/// </summary>
internal sealed class TinyServer : IDisposable
{
    private readonly TcpListener              _listener;
    private readonly CancellationTokenSource  _stop = new();
    private readonly Func<Seen, string?>      _reply;
    private readonly bool                     _dropFirst;
    private readonly bool                     _truncateFirst;
    private readonly bool                     _stallAfterHeaders;
    private readonly List<Seen>               _seen = [];
    private          int                      _handled;

    public TinyServer(
        Func<Seen, string?> reply,
        bool dualStack = false,
        bool dropFirst = false,
        bool truncateFirst = false,
        bool stallAfterHeaders = false)
    {
        _reply         = reply;
        _dropFirst     = dropFirst;
        _truncateFirst = truncateFirst;
        _stallAfterHeaders = stallAfterHeaders;

        if (dualStack)
        {
            _listener = new TcpListener(IPAddress.IPv6Any, 0);
            _listener.Server.DualMode = true;
        }
        else
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
        }

        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    public IReadOnlyList<Seen> Seen
    {
        get { lock (_seen) return [.. _seen]; }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { return; }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream  = client.GetStream();
                var request = await ReadRequestAsync(stream);
                if (request is null) return;

                lock (_seen) _seen.Add(request);

                var index = Interlocked.Increment(ref _handled);
                if (_dropFirst && index == 1) return;   // молча роняем связь

                // Заголовок ушел, тело оборвалось: платформа такой ответ не переигрывает —
                // повтор виден только свой.
                if (_truncateFirst && index == 1)
                {
                    var half = Encoding.UTF8.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\nполовина");

                    await stream.WriteAsync(half, _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                    return;
                }

                if (_stallAfterHeaders)
                {
                    var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\n[");
                    await stream.WriteAsync(head, _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                    return;
                }

                var answer = _reply(request);
                if (answer is null)
                {
                    // Молчим, пока тест не закончится: так проверяется таймаут.
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(answer);
                await stream.WriteAsync(bytes, _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
            catch
            {
                // Оборванная связь и остановка сервера — обычное дело для этого стенда.
            }
        }
    }

    private static async Task<Seen?> ReadRequestAsync(NetworkStream stream)
    {
        var head = new MemoryStream();
        var one  = new byte[1];

        while (!EndOfHead(head))
        {
            var read = await stream.ReadAsync(one);
            if (read == 0) return null;
            head.WriteByte(one[0]);
        }

        var lines = Encoding.UTF8.GetString(head.ToArray())
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        var start = lines[0].Split(' ');

        var headers = lines.Skip(1)
            .Select(line => line.Split(':', 2))
            .Where(pair => pair.Length == 2)
            .Select(pair => new IntegrationHeader(pair[0].Trim(), pair[1].Trim()))
            .ToList();

        var length = headers.FirstOrDefault(
            h => h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase));

        var body = "";
        if (length is not null && int.TryParse(length.Value, out var size) && size > 0)
        {
            var buffer = new byte[size];
            var got    = 0;
            while (got < size)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(got, size - got));
                if (read == 0) break;
                got += read;
            }

            body = Encoding.UTF8.GetString(buffer, 0, got);
        }

        return new Seen(start[0], start.Length > 1 ? start[1] : "", headers, body);
    }

    private static bool EndOfHead(MemoryStream head)
    {
        if (head.Length < 4) return false;

        var bytes = head.GetBuffer();
        var end   = (int)head.Length;

        return bytes[end - 4] == '\r' && bytes[end - 3] == '\n'
            && bytes[end - 2] == '\r' && bytes[end - 1] == '\n';
    }

    /// <summary>Готовый ответ строкой; <paramref name="extra"/> — дополнительные заголовки.</summary>
    public static string Reply(int code, string body, string contentType = "application/json", string extra = "")
    {
        var text = new StringBuilder();
        text.Append("HTTP/1.1 ").Append(code).Append(" OK\r\n");
        text.Append("Content-Type: ").Append(contentType).Append("\r\n");
        text.Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(body)).Append("\r\n");
        text.Append("Connection: close\r\n");

        if (extra.Length > 0) text.Append(extra).Append("\r\n");

        text.Append("\r\n").Append(body);
        return text.ToString();
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { /* уже остановлен */ }
        _stop.Dispose();
    }
}
