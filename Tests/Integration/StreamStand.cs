using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Стенд сторонней программы с потоком событий: на петлевом адресе принимает и обычные запросы, и
/// соединение WebSocket, по которому тест шлет события.
///
/// Отдельный от <see cref="TinyServer"/> намеренно: тому хватает «принять, ответить, закрыть», а
/// здесь соединение живет, пока идет тест, и говорит по нему стенд. Рукопожатие WebSocket
/// написано руками — сервер в полсотни строк не стоит зависимости, — а кадры ведет платформа
/// (<see cref="WebSocket.CreateFromStream(Stream, WebSocketCreationOptions)"/>).
///
/// Стенд выдуманный: ни одну настоящую программу он не изображает, и события в тестах — той
/// формы, которую назвал сам тест.
/// </summary>
internal sealed class StreamStand : IDisposable
{
    private readonly TcpListener             _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop     = new();

    private readonly List<Seen>      _seen     = [];
    private readonly List<string>    _greeting = [];
    private readonly List<WebSocket> _sockets  = [];

    private readonly SemaphoreSlim _connected = new(0);

    private readonly string _path;
    private readonly string _answer;

    /// <param name="path">Путь, по которому стенд открывает поток; по другому — отказывает.</param>
    /// <param name="answer">Тело ответа на обычные запросы.</param>
    public StreamStand(string path = "/events", string answer = """{"accepted":true}""")
    {
        _path   = path;
        _answer = answer;

        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    public string Address => $"http://127.0.0.1:{Port}";

    /// <summary>Обычные запросы, которые стенд увидел, — ответы на события.</summary>
    public IReadOnlyList<Seen> Requests
    {
        get { lock (_seen) return [.. _seen]; }
    }

    /// <summary>Что клиент прислал по потоку — кадры подписки.</summary>
    public IReadOnlyList<string> Greeting
    {
        get { lock (_greeting) return [.. _greeting]; }
    }

    /// <summary>Сколько раз открывали поток — так видно переподключение.</summary>
    public int Connections { get; private set; }

    /// <summary>Дождаться, пока клиент откроет поток (очередной раз).</summary>
    public async Task WaitConnectedAsync(TimeSpan patience)
    {
        if (!await _connected.WaitAsync(patience))
            throw new TimeoutException("поток так и не открыли");
    }

    /// <summary>Послать событие всем, кто слушает.</summary>
    public async Task SendAsync(string text)
    {
        List<WebSocket> sockets;
        lock (_sockets) sockets = [.. _sockets.Where(s => s.State == WebSocketState.Open)];

        foreach (var socket in sockets)
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text,
                                   endOfMessage: true, _stop.Token);
    }

    /// <summary>Оборвать открытые потоки — так проверяется переподключение.</summary>
    public void Drop()
    {
        List<WebSocket> sockets;
        lock (_sockets) { sockets = [.. _sockets]; _sockets.Clear(); }

        foreach (var socket in sockets)
        {
            try { socket.Abort(); } catch { /* уже закрыт */ }
            socket.Dispose();
        }
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
        try
        {
            var stream  = client.GetStream();
            var request = await ReadRequestAsync(stream);

            if (request is null) { client.Dispose(); return; }

            if (request.Header("Upgrade")?.Equals("websocket", StringComparison.OrdinalIgnoreCase) == true)
            {
                await StreamAsync(client, stream, request);
                return;
            }

            lock (_seen) _seen.Add(request);

            await stream.WriteAsync(Encoding.UTF8.GetBytes(TinyServer.Reply(200, _answer)), _stop.Token);
            await stream.FlushAsync(_stop.Token);
            client.Dispose();
        }
        catch
        {
            // Оборванная связь и остановка стенда — обычное дело в конце теста.
            client.Dispose();
        }
    }

    private async Task StreamAsync(TcpClient client, NetworkStream stream, Seen request)
    {
        if (request.Path != _path)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(TinyServer.Reply(404, "{}")), _stop.Token);
            client.Dispose();
            return;
        }

        var key    = request.Header("Sec-WebSocket-Key") ?? "";
        var accept = Convert.ToBase64String(SHA1.HashData(
            Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n"
          + "Upgrade: websocket\r\n"
          + "Connection: Upgrade\r\n"
          + $"Sec-WebSocket-Accept: {accept}\r\n\r\n"), _stop.Token);

        var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });

        lock (_sockets) _sockets.Add(socket);

        Connections++;
        _connected.Release();

        // Читаем то, что шлет клиент: кадры подписки. Конец чтения — конец соединения.
        var buffer = new byte[16 * 1024];

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var chunk = await socket.ReceiveAsync(buffer, _stop.Token);
                if (chunk.MessageType == WebSocketMessageType.Close) break;

                lock (_greeting) _greeting.Add(Encoding.UTF8.GetString(buffer, 0, chunk.Count));
            }
        }
        catch
        {
            // Клиент ушел или стенд остановили.
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task<Seen?> ReadRequestAsync(NetworkStream stream)
    {
        var head = new MemoryStream();
        var one  = new byte[1];

        while (!EndOfHead(head))
        {
            if (await stream.ReadAsync(one) == 0) return null;
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

    public void Dispose()
    {
        _stop.Cancel();
        Drop();
        try { _listener.Stop(); } catch { /* уже остановлен */ }
        _stop.Dispose();
    }
}
