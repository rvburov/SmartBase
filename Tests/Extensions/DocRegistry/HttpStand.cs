using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Стенд чужой программы: слушает петлевой адрес и на каждый запрос отвечает одним и тем же
/// JSON. Нужен сквозному прогону «ответ запроса → строка журнала» (<see cref="ScenarioRunTests"/>).
///
/// Урезанная копия <c>TinyServer</c> из <c>Tests/Integration</c>, и копия, а не ссылка, по той же
/// причине, что у PDF-стендов: проекты разные, общего кода у них нет, а стенд обязан уехать вместе
/// с расширением. В общую обвязку он не идет — там только то, что понадобилось двум наборам.
/// Повторов, обрывов и разбора заголовков здесь нет: регистрации они не касаются.
/// </summary>
internal sealed class HttpStand : IDisposable
{
    private readonly TcpListener             _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop     = new();
    private readonly byte[]                  _answer;

    public HttpStand(string json)
    {
        _answer = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\n"
          + "Content-Type: application/json\r\n"
          + $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n"
          + "Connection: close\r\n"
          + "\r\n"
          + json);

        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { return; }

            _ = Task.Run(() => AnswerAsync(client));
        }
    }

    private async Task AnswerAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();

                // Сначала дочитать запрос до пустой строки: закрой стенд соединение с непрочитанным
                // запросом, и часть систем отдаст клиенту обрыв вместо ответа. Тела у GET нет, так
                // что конец заголовков и есть конец запроса.
                var matched = 0;
                var one     = new byte[1];

                while (matched < 4)
                {
                    if (await stream.ReadAsync(one, _stop.Token) == 0) return;

                    var expected = matched % 2 == 0 ? '\r' : '\n';
                    matched = one[0] == expected ? matched + 1 : one[0] == '\r' ? 1 : 0;
                }

                await stream.WriteAsync(_answer, _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
            catch
            {
                // Остановка стенда посреди ответа — обычное дело в конце теста.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { /* уже остановлен */ }
        _stop.Dispose();
    }
}
