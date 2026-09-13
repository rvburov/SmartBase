using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace SmartBase.Integrations;

/// <summary>
/// Единственная дверь наружу: только здесь программа обращается к чужим программам по сети.
/// Правило сторожит сборка — <c>Tests/Unit/OneDoorOutwardTests.cs</c> роняет тесты, если
/// сетевой тип встретился вне <c>Integrations/</c>.
///
/// В сборке для закрытого контура (<c>-p:ClosedLoop=true</c>) этот файл в компиляцию НЕ
/// попадает: вместо него берется <c>EgressHttp.Closed.cs</c>, и сетевого клиента в дистрибутиве
/// нет вовсе. Подмена сделана в SmartBase.csproj файлом, а не через <c>#if</c>: условная
/// компиляция живет ровно в одном месте — в шлюзе, — и расползаться ей нельзя.
///
/// Слой намеренно тонкий и скучный. Все, что можно перепутать, решено до него в
/// <see cref="EgressPlan"/> и проверено тестами без сети; здесь — соединиться с проверенным
/// адресом и вернуть ответ.
///
/// Что этот слой делает всегда, независимо от настроек человека:
/// <list type="bullet">
/// <item>спрашивает <see cref="EgressGate"/> дважды — по имени и по фактическому адресу;</item>
/// <item>соединяется РОВНО с проверенным адресом, а не с тем, что даст DNS во второй раз;</item>
/// <item>не ходит за перенаправлениями: новый адрес шлюза не проходил;</item>
/// <item>пользуется прокси ТОЛЬКО тем, который назвала политика организации, и проверяет его
///       адрес как всякий другой;</item>
/// <item>не хранит cookie: чужое состояние между запусками нам не нужно;</item>
/// <item>проверяет сертификат — всегда, отключить это нечем;</item>
/// <item>обрезает ответ по потолку: чужая программа не должна уметь занять всю память;</item>
/// <item>повторяет только то, что можно повторить безопасно.</item>
/// </list>
/// </summary>
//
// Запрет сетевых типов снят НА ВЕСЬ ФАЙЛ, и это не поблажка, а определение двери: файл и есть
// то единственное место, где им позволено быть (BannedSymbols.txt, правило RS0030). Снятие
// пофразно — по строке на каждый вызов — говорило бы, что дверей много и каждая маленькая.
// Мест с этой строкой в репозитории два, и второе — проба порта PostgreSQL; появление третьего
// роняет тест OneDoorOutwardTests.Suppressions_live_only_in_the_gate_and_in_the_postgres_probe.
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
            ? Task.FromResult(EgressResponse.Denied(EgressDenyReason.BadAddress, error))
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
            ? Task.FromResult(EgressResponse.Denied(EgressDenyReason.BadAddress, error))
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
        var ceiling = Math.Min(maxResponseBytes, MaxResponseBytes);

        // Первая проверка — по имени. До нее не делается вообще ничего, даже разрешения имени:
        // сам факт запроса к DNS уже говорит наружу, что программа этим адресом интересуется.
        var byName = EgressGate.Check(plan.Address);
        if (!byName.Allowed)
            return EgressResponse.Denied(byName.Reason, byName.Explanation, plan.SafeAddress);

        // Прокси организации: если он назван политикой, соединяемся МЫ с ним, а не с чужой
        // программой, — значит и проверять адресом надо его. Имя цели при этом проверено выше и
        // остается обязательным: прокси не разрешение ходить куда угодно, а способ дойти.
        var viaProxy = EgressGate.CheckProxy(out var proxy);
        if (!viaProxy.Allowed)
            return EgressResponse.Denied(viaProxy.Reason, viaProxy.Explanation, plan.SafeAddress);

        var watch = Stopwatch.StartNew();

        var target = proxy ?? plan.Address;
        var pinned = await ResolveAndCheckAsync(target, ct);
        if (pinned.Address is null)
            return EgressResponse.Denied(pinned.Reason, pinned.Explanation, plan.SafeAddress);

        // Повтор — только для того, что можно повторить безопасно. Обрыв связи не значит, что
        // запрос не дошел: он мог выполниться, а ответ потеряться, и повтор POST зарегистрирует
        // документ дважды.
        var attempts = plan.Idempotent ? plan.Retries + 1 : 1;
        var failure  = "";

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return await OnceAsync(plan, pinned.Address, target.Port, proxy, attempt, watch, ceiling, ct);
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

                // Пауза удваивается: чужая программа могла перезагружаться, и долбить ее
                // раз в секунду — верный способ не дождаться ответа никогда.
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 << (attempt - 1), 30)), ct);
            }
        }

        return EgressResponse.Failed(failure, plan.SafeAddress, attempts, watch.Elapsed);
    }

    // ── Разрешение имени и вторая проверка ────────────────────────────────────

    /// <summary>
    /// Разрешить имя и спросить шлюз о фактическом адресе. Вторая проверка обязательна: между
    /// сверкой имени и соединением запись DNS может смениться, и белый список по именам сам по
    /// себе не значит ничего (docs/ROADMAP-API.md, 10.5).
    ///
    /// Адрес запоминается и дальше используется именно он — иначе защита была бы показной:
    /// проверили одно, соединились с тем, что DNS отдаст в следующий раз.
    ///
    /// <paramref name="target"/> — то, с чем мы СОЕДИНЯЕМСЯ, а это не всегда адрес чужой
    /// программы: при прокси организации соединение идет с прокси, и проверять надо его адрес.
    /// </summary>
    private static async Task<(IPAddress? Address, EgressDenyReason Reason, string Explanation)>
        ResolveAndCheckAsync(Uri target, CancellationToken ct)
    {
        IPAddress[] candidates;

        if (IPAddress.TryParse(target.Host.Trim('[', ']'), out var literal))
        {
            candidates = [literal];
        }
        else
        {
            try
            {
                candidates = await Dns.GetHostAddressesAsync(target.Host, ct);
            }
            catch (Exception error) when (error is SocketException or ArgumentException)
            {
                return (null, EgressDenyReason.BadAddress,
                        $"имя «{target.Host}» не разрешилось: {error.Message}");
            }
        }

        if (candidates.Length == 0)
            return (null, EgressDenyReason.BadAddress, $"имя «{target.Host}» не разрешилось");

        // Берется первый РАЗРЕШЕННЫЙ, а не первый попавшийся: у имени может быть и адрес IPv6,
        // и адрес IPv4, а в политике перечислена одна из сетей. Отказ по такому поводу выглядел
        // бы как поломка, хотя разрешенный путь есть.
        var decision = EgressDecision.Deny(EgressDenyReason.AddressNotAllowed, "адрес не разрешен");

        foreach (var candidate in candidates)
        {
            decision = EgressGate.CheckResolved(candidate);
            if (decision.Allowed) return (candidate, EgressDenyReason.None, "");
        }

        return (null, decision.Reason, decision.Explanation);
    }

    // ── Одна попытка ──────────────────────────────────────────────────────────

    private static async Task<EgressResponse> OnceAsync(
        EgressPlan plan, IPAddress pinned, int port, Uri? proxy,
        int attempt, Stopwatch watch, long ceiling, CancellationToken ct)
    {
        using var handler = HandlerFor(plan, pinned, port, proxy);
        using var client  = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(plan.TimeoutSeconds),
        };

        using var message  = MessageFor(plan);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);

        var (body, bytes, truncated) = await ReadAsync(response, ceiling, ct);

        var headers = response.Headers
            .Concat(response.Content.Headers)
            .Select(h => new IntegrationHeader(h.Key, string.Join(", ", h.Value)))
            .ToList();

        var explanation = (int)response.StatusCode is >= 300 and < 400
            ? "чужая программа ответила перенаправлением; программа за ним не идет — "
            + "новый адрес шлюз не проверял. Укажите итоговый адрес в соединении."
            : "";

        return new EgressResponse(
            Sent:        true,
            Reason:      EgressDenyReason.None,
            Explanation: explanation,
            StatusCode:  (int)response.StatusCode,
            Body:        body,
            Bytes:       bytes,
            Truncated:   truncated,
            Elapsed:     watch.Elapsed,
            Headers:     headers,
            SafeAddress: plan.SafeAddress,
            Attempts:    attempt);
    }

    /// <summary>
    /// Клиент, соединяющийся ровно с проверенным адресом. Свой на каждый вызов: пул соединений
    /// пережил бы смену закрепленного адреса и однажды отправил бы запрос по старому.
    ///
    /// <paramref name="pinned"/> и <paramref name="port"/> — это то, куда идет ТСР-соединение: сама
    /// чужая программа либо прокси организации. При прокси имя цели остается в самом запросе, и
    /// разрешает его прокси, а не мы, — потому и проверять адресом мы можем только прокси.
    /// </summary>
    private static SocketsHttpHandler HandlerFor(EgressPlan plan, IPAddress pinned, int port, Uri? proxy)
    {
        return new SocketsHttpHandler
        {
            // Перенаправление — это новый адрес, а его шлюз не проверял.
            AllowAutoRedirect = false,

            // Прокси — только тот, который назвала политика организации. Своего у пользователя
            // нет и быть не может: через прокси проходит содержимое запроса целиком.
            UseProxy = proxy is not null,
            Proxy    = proxy is null ? null : new WebProxy(proxy) { BypassProxyOnLocal = false },

            // Чужое состояние между запусками нам не нужно.
            UseCookies = false,

            AutomaticDecompression   = DecompressionMethods.All,
            ConnectTimeout           = TimeSpan.FromSeconds(Math.Min(ConnectSeconds, plan.TimeoutSeconds)),
            PooledConnectionLifetime = TimeSpan.FromSeconds(plan.TimeoutSeconds),

            // Вот здесь и живет закрепление адреса: имя из запроса игнорируется, соединение
            // идет с тем адресом, который прошел проверку. Проверка сертификата при этом
            // остается по имени из адреса — ее делает сам HttpClient поверх этого потока.
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(pinned.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };

                try
                {
                    await socket.ConnectAsync(new IPEndPoint(pinned, port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }

    private static HttpRequestMessage MessageFor(EgressPlan plan)
    {
        var message = new HttpRequestMessage(MethodFor(plan.Method), plan.Address);

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

        foreach (var header in plan.Headers)
        {
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
    /// заголовка чужая программа может и соврать.
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
