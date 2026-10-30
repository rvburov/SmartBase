using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace SmartBase.Integrations;

/// <summary>
/// Сетевой транспорт запросов и потоков событий: таймауты, отмена, ограничение ответа,
/// повторы идемпотентных запросов и проверка сертификата средствами HTTP-клиента.
/// </summary>
// Сетевые обращения собраны здесь; сборка запрещает сетевые типы вне транспорта.
#pragma warning disable RS0030
public static class EgressHttp
{
    /// <summary>
    /// Потолок ответа. Не защита от злого умысла, а защита от ошибки: перепутанный адрес,
    /// отдающий бесконечный поток, не должен доводить программу до нехватки памяти.
    /// </summary>
    public const long MaxResponseBytes = 32L * 1024 * 1024;

    /// <summary>Сколько ждать самого соединения — отдельно от ожидания ответа.</summary>
    private const int ConnectSeconds = 15;

    /// <summary>
    /// Отправить запрос через соединение. Секрет берется в момент вызова и нигде не оседает.
    /// </summary>
    public static Task<EgressResponse> SendAsync(
        IntegrationConnection connection,
        IntegrationRequest    request,
        string                secret,
        CancellationToken     ct = default)
    {
        var plan = EgressPlan.Build(connection, request, secret, out var error);

        return plan is null
            ? Task.FromResult(EgressResponse.Failed(error))
            : SendAsync(plan, ct);
    }

    /// <summary>Кнопка «Проверить связь»: GET по адресу соединения.</summary>
    public static Task<EgressResponse> ProbeAsync(
        IntegrationConnection connection,
        string                secret,
        CancellationToken     ct = default)
    {
        var plan = EgressPlan.Probe(connection, secret, out var error);

        return plan is null
            ? Task.FromResult(EgressResponse.Failed(error))
            : SendAsync(plan, ct);
    }

    /// <summary>
    /// Отправить готовый вызов.
    ///
    /// <paramref name="maxResponseBytes"/> потолок только ОПУСКАЕТ: поднять его вызывающим
    /// кодом нельзя, иначе смысл потолка терялся бы вместе с первым же удобным случаем.
    /// </summary>
    public static async Task<EgressResponse> SendAsync(
        EgressPlan plan, CancellationToken ct = default, long maxResponseBytes = MaxResponseBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxResponseBytes);
        var ceiling = Math.Min(maxResponseBytes, MaxResponseBytes);

        if (!plan.Address.IsAbsoluteUri || plan.Address.Scheme is not ("http" or "https"))
            return EgressResponse.Failed("для запроса нужен абсолютный адрес http или https",
                                         plan.SafeAddress);
        if (Encoding.UTF8.GetByteCount(plan.Body) > MaxResponseBytes)
            return EgressResponse.Failed("тело запроса больше технического ограничения 32 МБ; "
                                         + "сузьте отбор отправляемых данных", plan.SafeAddress);

        var watch = Stopwatch.StartNew();

        // Повтор — только для того, что можно повторить безопасно. Обрыв связи не значит, что
        // запрос не дошел: он мог выполниться, а ответ потеряться, и повтор POST зарегистрирует
        // документ дважды.
        var attempts = plan.Idempotent ? plan.Retries + 1 : 1;
        var failure  = "";

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waiting.CancelAfter(TimeSpan.FromSeconds(plan.TimeoutSeconds));
                return await OnceAsync(plan, attempt, watch, ceiling, waiting.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return EgressResponse.Failed("отменено", plan.SafeAddress, attempt, watch.Elapsed);
            }
            catch (Exception error) when (error is HttpRequestException or SocketException
                                                or IOException or OperationCanceledException)
            {
                failure = Explain(error, plan.TimeoutSeconds);

                if (attempt == attempts) break;

                // Пауза удваивается: сторонняя программа могла перезагружаться, и долбить ее
                // раз в секунду — верный способ не дождаться ответа никогда.
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 << (attempt - 1), 30)), ct);
            }
        }

        return EgressResponse.Failed(failure, plan.SafeAddress, attempts, watch.Elapsed);
    }

    // ── Поток событий ─────────────────────────────────────────────────────────

    /// <summary>
    /// Сколько байт может занять одно сообщение потока. Событие — сотни байт, редко килобайты;
    /// мегабайт означает, что по адресу отвечает что-то не то, и держать это в памяти незачем.
    /// </summary>
    public const int MaxStreamMessageBytes = 1024 * 1024;

    /// <summary>
    /// Как часто спрашивать ту сторону «ты здесь?» и сколько ждать ответа. Без этого соединение,
    /// у которого пропала вторая сторона, висело бы открытым часами, а события за это время шли
    /// бы мимо.
    /// </summary>
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Слушать поток событий сторонней программы по WebSocket до отключения или отмены.
    /// Переподключением управляет вызывающая служба.
    /// </summary>
    /// <param name="path">Путь потока относительно адреса соединения.</param>
    /// <param name="greeting">Текстовые кадры, отправляемые сразу после соединения.</param>
    /// <param name="opened">Соединение установлено и приветствие ушло — с этого момента события
    /// не теряются.</param>
    /// <param name="received">Пришло текстовое сообщение; <c>false</c> — слушать дальше незачем.</param>
    public static async Task<EgressResponse> ListenAsync(
        IntegrationConnection connection,
        string                secret,
        string                path,
        IReadOnlyList<string> greeting,
        Action                opened,
        Func<string, bool>    received,
        CancellationToken     ct = default)
    {
        var plan = EgressPlan.Build(connection,
                                    IntegrationRequest.New() with { Name = "Поток событий", Path = path },
                                    secret, out var error);

        if (plan is null) return EgressResponse.Failed(error);

        return await StreamAsync(plan, greeting, opened, received, ct);
    }

    private static async Task<EgressResponse> StreamAsync(
        EgressPlan plan, IReadOnlyList<string> greeting,
        Action opened, Func<string, bool> received, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var bytes = 0L;

        using var handler = HandlerFor(plan);
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var socket  = new ClientWebSocket();

        socket.Options.KeepAliveInterval = KeepAlive;
        socket.Options.KeepAliveTimeout  = KeepAlive;

        foreach (var header in plan.Headers)
            socket.Options.SetRequestHeader(header.Name, header.Value);

        var address = new UriBuilder(plan.Address)
        {
            Scheme = plan.Address.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Port   = plan.Address.Port,
        }.Uri;

        try
        {
            using var connecting = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connecting.CancelAfter(TimeSpan.FromSeconds(plan.TimeoutSeconds));

            await socket.ConnectAsync(address, invoker, connecting.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return EgressResponse.Failed("отменено", plan.SafeAddress, 1, watch.Elapsed);
        }
        catch (Exception error) when (error is WebSocketException or HttpRequestException
                                            or SocketException or IOException
                                            or OperationCanceledException)
        {
            return EgressResponse.Failed(ExplainStream(error, plan.TimeoutSeconds), plan.SafeAddress,
                                         1, watch.Elapsed);
        }

        string ending;

        try
        {
            foreach (var frame in greeting)
            {
                var data = Encoding.UTF8.GetBytes(frame);
                bytes += data.Length;
                await socket.SendAsync(data, WebSocketMessageType.Text, endOfMessage: true, ct);
            }

            opened();
            ending = await ReceiveAsync(socket, received, count => bytes += count, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ending = "остановлено";
        }
        catch (Exception error) when (error is WebSocketException or SocketException or IOException)
        {
            ending = "связь потеряна: " + error.Message;
        }

        if (socket.State == WebSocketState.Open)
        {
            try
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", closing.Token);
            }
            catch (Exception error) when (error is WebSocketException or IOException
                                                or OperationCanceledException)
            {
                // Закрыть вежливо не вышло — соединение уйдет вместе с сокетом.
            }
        }

        return new EgressResponse(
            Sent:        true,
            Explanation: ending,
            StatusCode:  (int)HttpStatusCode.SwitchingProtocols,
            Body:        "",
            Bytes:       bytes,
            Truncated:   false,
            Elapsed:     watch.Elapsed,
            Headers:     [],
            SafeAddress: plan.SafeAddress,
            Attempts:    1);
    }

    /// <summary>
    /// Читать сообщения потока до конца. Возвращает, почему кончилось. Сообщение длиннее
    /// потолка обрывает поток: по адресу отвечает что-то не то, и дочитывать его незачем.
    /// </summary>
    private static async Task<string> ReceiveAsync(ClientWebSocket socket, Func<string, bool> received,
                                                   Action<int> counted, CancellationToken ct)
    {
        var buffer  = new byte[16 * 1024];
        var message = new MemoryStream();

        while (true)
        {
            var chunk = await socket.ReceiveAsync(buffer, ct);

            if (chunk.MessageType == WebSocketMessageType.Close)
                return "та сторона закрыла поток"
                     + (string.IsNullOrWhiteSpace(socket.CloseStatusDescription)
                         ? ""
                         : ": " + socket.CloseStatusDescription);

            counted(chunk.Count);

            if (message.Length + chunk.Count > MaxStreamMessageBytes)
                return $"сообщение потока длиннее {MaxStreamMessageBytes:N0} байт — поток оборван";

            message.Write(buffer, 0, chunk.Count);

            if (!chunk.EndOfMessage) continue;

            // Двоичные сообщения не разбираются: события приходят текстом, а гадать о формате
            // чужих байт программа не берется.
            var binary = chunk.MessageType == WebSocketMessageType.Binary;
            var text   = binary ? "" : Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);

            message.SetLength(0);

            if (!binary && !received(text)) return "слушать дальше незачем";
        }
    }

    /// <summary>
    /// Почему поток не открылся — словами. Отказ на рукопожатии чаще всего означает, что по
    /// этому пути потока нет, и сказать это надо прямо: «связи нет» отправило бы человека
    /// проверять сеть, которая в порядке.
    /// </summary>
    private static string ExplainStream(Exception error, int timeoutSeconds) => error switch
    {
        WebSocketException { InnerException: { } inner } when inner is HttpRequestException or SocketException
                           => Explain(inner, timeoutSeconds),
        WebSocketException => "та сторона не открыла поток по этому пути: " + error.Message,
        _                  => Explain(error, timeoutSeconds),
    };

    // ── Одна попытка ──────────────────────────────────────────────────────────

    /// <summary>
    /// Сколько перенаправлений разрешено пройти за одну попытку. Три — это «http → https» плюс
    /// смена пути и еще один запас; дальше начинается не перенаправление, а петля, и ходить по
    /// ней значит дать сторонней программе водить нас по адресам сколько ей угодно.
    /// </summary>
    private const int MaxRedirects = 3;

    private static async Task<EgressResponse> OnceAsync(
        EgressPlan plan, int attempt, Stopwatch watch, long ceiling, CancellationToken ct)
    {
        var address = plan.Address;
        var hops    = 0;

        while (true)
        {
            using var handler = HandlerFor(plan);
            using var client  = new HttpClient(handler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromSeconds(plan.TimeoutSeconds),
            };

            using var message  = MessageFor(plan, address);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)response.StatusCode is >= 300 and < 400
                && Redirect(plan, response, address) is { } next
                && hops < MaxRedirects)
            {
                address = next;
                hops++;
                continue;
            }

            var (body, bytes, truncated) = await ReadAsync(response, ceiling, ct);

            var headers = response.Headers
                .Concat(response.Content.Headers)
                .Select(h => new IntegrationHeader(h.Key, string.Join(", ", h.Value)))
                .ToList();

            return new EgressResponse(
                Sent:        true,
                Explanation: Note(plan, response, hops),
                StatusCode:  (int)response.StatusCode,
                Body:        body,
                Bytes:       bytes,
                Truncated:   truncated,
                Elapsed:     watch.Elapsed,
                Headers:     headers,
                SafeAddress: plan.SafeAddress,
                Attempts:    attempt);
        }
    }

    /// <summary>
    /// Куда зовет перенаправление; <c>null</c> — идти за ним нельзя или некуда.
    ///
    /// <b>Идем только за GET.</b> У остальных методов есть тело, и с ним на новый адрес два пути,
    /// оба плохие: отправить его туда, куда человек не собирался (это вынос данных), — или тихо
    /// выбросить и отправить пустой запрос, о чем никто не узнает. Отказ здесь честнее обоих.
    ///
    /// Относительный адрес разрешается от текущего: сторонние программы пишут <c>Location: /v2/orders</c>
    /// не реже полного адреса.
    /// </summary>
    private static Uri? Redirect(EgressPlan plan, HttpResponseMessage response, Uri from)
    {
        if (plan.Method != RequestMethod.Get) return null;

        if (response.Headers.Location is not { } location) return null;

        return Uri.TryCreate(from, location, out var next)
            && next.Scheme is "http" or "https" ? next : null;
    }

    /// <summary>
    /// Пояснение к ответу: за перенаправлением не пошли — сказать почему; прошли — сказать
    /// сколько. Второе не мелочь: ответ, пришедший с ТРЕТЬЕГО адреса, человек должен видеть как
    /// пришедший оттуда, а не с того, что написан в карточке.
    /// </summary>
    private static string Note(EgressPlan plan, HttpResponseMessage response, int hops)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            if (plan.Method != RequestMethod.Get)
                return "сторонняя программа ответила перенаправлением; за ним программа идет только у GET: "
                     + "у остальных методов есть тело, и оно ушло бы туда, куда его не посылали. "
                     + "Укажите итоговый адрес в соединении.";

            // Сюда с GET попадают три случая, и валить их в один значит соврать о причине:
            // перенаправление без нового адреса, адрес, который не разобрался, и настоящая петля.
            if (response.Headers.Location is null)
                return "сторонняя программа ответила перенаправлением, не назвав нового адреса "
                     + "(нет заголовка Location). Укажите итоговый адрес в соединении.";

            return hops < MaxRedirects
                ? "адрес перенаправления не разобрался. Укажите итоговый адрес в соединении."
                : "перенаправлений подряд больше " + MaxRedirects
                + ": похоже на петлю. Укажите итоговый адрес в соединении.";
        }

        return hops == 0 ? "" : $"пройдено перенаправлений: {hops}";
    }

    /// <summary>Отдельный клиент вызова с управляемыми перенаправлениями и временем ожидания.</summary>
    private static SocketsHttpHandler HandlerFor(EgressPlan plan) => new()
    {
        AllowAutoRedirect        = false,
        UseCookies               = false,
        AutomaticDecompression   = DecompressionMethods.All,
        ConnectTimeout           = TimeSpan.FromSeconds(Math.Min(ConnectSeconds, plan.TimeoutSeconds)),
        PooledConnectionLifetime = TimeSpan.FromSeconds(plan.TimeoutSeconds),
    };

    private static HttpRequestMessage MessageFor(EgressPlan plan, Uri address)
    {
        var message = new HttpRequestMessage(MethodFor(plan.Method), address);

        if (plan.Body.Length > 0)
        {
            var content = new StringContent(plan.Body, Encoding.UTF8);

            try
            {
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(plan.ContentType);
            }
            catch (FormatException)
            {
                // Человек мог написать в заголовке что угодно; вид содержимого — не повод
                // отказаться отправлять.
            }

            message.Content = content;
        }

        var sameOrigin = address.Scheme == plan.Address.Scheme
                      && address.Host.Equals(plan.Address.Host, StringComparison.OrdinalIgnoreCase)
                      && address.Port == plan.Address.Port;

        foreach (var header in plan.Headers)
        {
            if (!sameOrigin) continue;

            // Content-Type уже применен к телу, а класть его в заголовки запроса нельзя.
            if (header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;

            if (!message.Headers.TryAddWithoutValidation(header.Name, header.Value))
                message.Content?.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        return message;
    }

    private static HttpMethod MethodFor(RequestMethod method) => method switch
    {
        RequestMethod.Post   => HttpMethod.Post,
        RequestMethod.Put    => HttpMethod.Put,
        RequestMethod.Patch  => HttpMethod.Patch,
        RequestMethod.Delete => HttpMethod.Delete,
        _                    => HttpMethod.Get,
    };

    /// <summary>
    /// Прочитать ответ, но не больше потолка. Читается поток, а не строка целиком: длину из
    /// заголовка сторонняя программа может и соврать.
    /// </summary>
    private static async Task<(string Body, long Bytes, bool Truncated)> ReadAsync(
        HttpResponseMessage response, long ceiling, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        var buffer    = new byte[81920];
        var collected = new MemoryStream();
        var truncated = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;

            var room = ceiling - collected.Length;
            if (read > room)
            {
                collected.Write(buffer, 0, (int)room);
                truncated = true;
                break;
            }

            collected.Write(buffer, 0, read);
        }

        var encoding = EncodingOf(response);
        return (encoding.GetString(collected.ToArray()), collected.Length, truncated);
    }

    /// <summary>Кодировка из заголовка; незнакомая или отсутствующая — UTF-8.</summary>
    private static Encoding EncodingOf(HttpResponseMessage response)
    {
        var name = response.Content.Headers.ContentType?.CharSet;
        if (string.IsNullOrWhiteSpace(name)) return Encoding.UTF8;

        try
        {
            return Encoding.GetEncoding(name.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// Объяснение сбоя человеческим языком. «Не удалось подключиться» не годится: по такому
    /// сообщению нельзя понять, выключен сервер, не сходится сертификат или истек таймаут.
    /// </summary>
    private static string Explain(Exception error, int timeoutSeconds) => error switch
    {
        OperationCanceledException  => $"сервер не ответил за {timeoutSeconds} с",
        SocketException socket      => $"связи нет: {socket.Message}",
        HttpRequestException http when http.InnerException is SocketException inner
                                    => $"связи нет: {inner.Message}",
        HttpRequestException http when http.InnerException is System.Security.Authentication.AuthenticationException tls
                                    => $"сертификат не принят: {tls.Message}",
        _                           => error.Message,
    };
}
