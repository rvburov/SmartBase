using System.Net;
using System.Net.Sockets;
using System.Text;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>Отправляющий слой на локальном сервере: методы, кодировки, перенаправления, предел ответа, таймауты и повторы.</summary>
[Collection(EgressCollection.Name)]
public sealed class EgressHttpTests
{

    private static IntegrationConnection Connection(TinyServer server, string host = "127.0.0.1") =>
        IntegrationConnection.New() with
        {
            Id             = 1,
            Name           = "Учетная система",
            Address        = $"http://{host}:{server.Port}",
            TimeoutSeconds = 10,
            Retries        = 0,
        };

    private static IntegrationRequest Request(
        RequestMethod method = RequestMethod.Get, string path = "/orders", string body = "") =>
        IntegrationRequest.New() with
        {
            Name         = "Заказы",
            ConnectionId = 1,
            Method       = method,
            Path         = path,
            Body         = body,
        };

    // ── Обычный обмен ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_reaches_the_server_and_brings_the_answer()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(200, """{"orders":[]}"""));

        var answer = await EgressHttp.SendAsync(Connection(server), Request(), secret: "");

        Assert.True(answer.Ok, answer.Explanation);
        Assert.Equal(200, answer.StatusCode);
        Assert.Equal("""{"orders":[]}""", answer.Body);
        Assert.Equal(1, answer.Attempts);
        Assert.False(answer.Truncated);

        var seen = Assert.Single(server.Seen);
        Assert.Equal("GET", seen.Method);
        Assert.Equal("/orders", seen.Path);
    }

    /// <summary>Кириллица в ответе не должна превращаться в мусор.</summary>
    [Fact]
    public async Task Answer_is_read_in_the_declared_encoding()
    {

        using var server = new TinyServer(_ =>
            TinyServer.Reply(200, "Зарегистрирован", "text/plain; charset=utf-8"));

        var answer = await EgressHttp.SendAsync(Connection(server), Request(), secret: "");

        Assert.Equal("Зарегистрирован", answer.Body);
    }

    [Fact]
    public async Task Post_carries_the_body_the_headers_and_the_secret()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(201, "ok", "text/plain"));

        var connection = Connection(server) with
        {
            Auth     = ConnectionAuth.Bearer,
            Headers  = [new IntegrationHeader("X-Client", "SmartBase")],
        };

        var answer = await EgressHttp.SendAsync(
            connection, Request(RequestMethod.Post, body: """{"a":1}"""), secret: "T0K3N");

        Assert.Equal(201, answer.StatusCode);

        var seen = Assert.Single(server.Seen);
        Assert.Equal("POST", seen.Method);
        Assert.Equal("""{"a":1}""", seen.Body);
        Assert.Equal("Bearer T0K3N", seen.Header("Authorization"));
        Assert.Equal("SmartBase", seen.Header("X-Client"));
        Assert.StartsWith("application/json", seen.Header("Content-Type"), StringComparison.Ordinal);
    }

    /// <summary>Ключ в параметрах доезжает до сервера, но в безопасном адресе его нет.</summary>
    [Fact]
    public async Task Key_in_the_address_travels_but_stays_out_of_the_journal()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(200, "ok", "text/plain"));

        var connection = Connection(server) with
        {
            Auth     = ConnectionAuth.Query,
            AuthName = "api_key",
        };

        var answer = await EgressHttp.SendAsync(connection, Request(), secret: "T0K3N");

        Assert.Equal("/orders?api_key=T0K3N", Assert.Single(server.Seen).Path);
        Assert.DoesNotContain("T0K3N", answer.SafeAddress, StringComparison.Ordinal);
        Assert.EndsWith("api_key=***", answer.SafeAddress, StringComparison.Ordinal);
    }

    /// <summary>
    /// Имя разрешается, и берется тот адрес, который разрешил шлюз. На разных машинах
    /// «localhost» дает то IPv6, то IPv4 — сервер поэтому слушает оба.
    /// </summary>
    [Fact]
    public async Task Name_is_resolved_and_the_allowed_address_is_used()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(200, "ok", "text/plain"), dualStack: true);

        var answer = await EgressHttp.SendAsync(Connection(server, "localhost"), Request(), secret: "");

        Assert.True(answer.Ok, answer.Explanation);
    }

    // ── Обещания слоя ─────────────────────────────────────────────────────────

    /// <summary>
    /// За перенаправлением слой ИДЕТ — но проверив новый адрес с самого начала (10.5: «каждый
    /// переход проверяется заново»).
    ///
    /// Пока переходы были запрещены целиком, самый обычный <c>http → https</c> у чужого API
    /// кончался отказом, и выглядело это как поломка программы, а не как правило организации.
    /// Автоматический переход HttpClient при этом по-прежнему выключен: он прошел бы мимо шлюза,
    /// и одним ответом чужого сервера белый список обходился бы весь.
    /// </summary>
    [Fact]
    public async Task An_allowed_redirect_is_followed_after_a_fresh_check()
    {

        using var elsewhere = new TinyServer(_ => TinyServer.Reply(200, """[{"а":1}]"""));
        using var server = new TinyServer(_ => TinyServer.Reply(
            302, "", "text/plain", $"Location: http://127.0.0.1:{elsewhere.Port}/забрать"));

        var answer = await EgressHttp.SendAsync(Connection(server), Request(), secret: "");

        Assert.True(answer.Ok, answer.Explanation);
        Assert.Equal(200, answer.StatusCode);
        Assert.Single(elsewhere.Seen);
        Assert.Contains("перенаправлений: 1", answer.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// За перенаправлением запроса С ТЕЛОМ слой не идет. Тело на новом адресе можно либо
    /// отправить туда, куда человек не собирался (это вынос данных), либо тихо выбросить — и оба
    /// пути хуже отказа.
    /// </summary>
    [Fact]
    public async Task A_redirect_of_a_request_with_a_body_is_refused()
    {

        using var elsewhere = new TinyServer(_ => TinyServer.Reply(200, "секрет", "text/plain"));
        using var server = new TinyServer(_ => TinyServer.Reply(
            302, "", "text/plain", $"Location: http://127.0.0.1:{elsewhere.Port}/забрать"));

        var answer = await EgressHttp.SendAsync(
            Connection(server),
            Request() with { Method = RequestMethod.Post, Body = """{"тело":"важное"}""" },
            secret: "");

        Assert.Equal(302, answer.StatusCode);
        Assert.False(answer.Ok);
        Assert.Contains("только у GET", answer.Explanation, StringComparison.Ordinal);
        Assert.Empty(elsewhere.Seen);
    }

    /// <summary>Потолок ответа: сторонняя программа не должна уметь занять всю память.</summary>
    [Fact]
    public async Task Answer_longer_than_the_ceiling_is_cut()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(200, new string('a', 5000), "text/plain"));

        var plan = EgressPlan.Build(Connection(server), Request(), "", out var error);
        Assert.True(plan is not null, error);

        var answer = await EgressHttp.SendAsync(plan!, maxResponseBytes: 1000);

        Assert.True(answer.Truncated);
        Assert.Equal(1000, answer.Bytes);
        Assert.Equal(1000, answer.Body.Length);
    }

    /// <summary>Потолок можно только опустить: поднять его вызовом нельзя.</summary>
    [Fact]
    public void Ceiling_cannot_be_raised()
    {
        Assert.Equal(32L * 1024 * 1024, EgressHttp.MaxResponseBytes);
    }

    /// <summary>
    /// Сервер молчит — слой не ждет вечно и объясняет, что именно случилось: «не удалось
    /// подключиться» не дает понять, выключен сервер или истек таймаут.
    /// </summary>
    [Fact]
    public async Task Silent_server_ends_with_a_timeout_and_a_reason()
    {

        using var server = new TinyServer(_ => null);   // принимает и молчит

        var connection = Connection(server) with { TimeoutSeconds = 1 };
        var answer     = await EgressHttp.SendAsync(connection, Request(), secret: "");

        Assert.False(answer.Sent);

        Assert.Contains("не ответил", answer.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ответ оборвался на середине — GET повторяется. Обрыв именно ответа, а не соединения:
    /// соединение, оборванное до первого байта, переигрывает сама платформа, и своего повтора
    /// тут было бы не видно.
    /// </summary>
    [Fact]
    public async Task Get_is_repeated_after_a_broken_answer()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(200, "ok", "text/plain"), truncateFirst: true);

        var connection = Connection(server) with { Retries = 1, TimeoutSeconds = 5 };
        var answer     = await EgressHttp.SendAsync(connection, Request(), secret: "");

        Assert.True(answer.Ok, answer.Explanation);
        Assert.Equal(2, answer.Attempts);
        Assert.Equal(2, server.Seen.Count);
    }

    /// <summary>
    /// POST не повторяется ни своим слоем, ни платформой: обрыв не значит, что запрос не дошел,
    /// и повтор зарегистрировал бы документ дважды.
    /// </summary>
    [Fact]
    public async Task Post_is_never_repeated()
    {

        using var server = new TinyServer(_ => null, dropFirst: true);

        var connection = Connection(server) with { Retries = 3, TimeoutSeconds = 5 };
        var answer     = await EgressHttp.SendAsync(
            connection, Request(RequestMethod.Post, body: "a=1"), secret: "");

        Assert.False(answer.Sent);
        Assert.Equal(1, answer.Attempts);
        Assert.Single(server.Seen);
    }

    [Fact]
    public async Task Patch_is_never_repeated_either()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(200, "ok", "text/plain"), truncateFirst: true);

        var connection = Connection(server) with { Retries = 3, TimeoutSeconds = 5 };
        var answer     = await EgressHttp.SendAsync(
            connection, Request(RequestMethod.Patch, body: "a=1"), secret: "");

        Assert.False(answer.Sent);
        Assert.Equal(1, answer.Attempts);
    }

    /// <summary>Негодная карточка до сети не доходит вовсе.</summary>
    [Fact]
    public async Task Broken_card_is_refused_with_its_own_reason()
    {

        var answer = await EgressHttp.SendAsync(
            IntegrationConnection.New() with { Name = "Без адреса" }, Request(), secret: "");

        Assert.False(answer.Sent);

        Assert.Contains("Адрес не заполнен", answer.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirect_to_another_port_keeps_the_response_but_does_not_forward_connection_credentials()
    {
        using var target = new TinyServer(_ => TinyServer.Reply(200, "redirected", "text/plain"));
        using var origin = new TinyServer(_ => TinyServer.Reply(302, "", "text/plain",
            $"Location: http://127.0.0.1:{target.Port}/result"));
        var connection = Connection(origin) with
        {
            Auth = ConnectionAuth.Header, AuthName = "X-Service-Key",
            Headers = [new IntegrationHeader("X-Service-Header", "original-service")],
        };
        var answer = await EgressHttp.SendAsync(connection, Request(), "fake-service-secret");
        Assert.True(answer.Ok, answer.Explanation);
        Assert.Equal("redirected", answer.Body);
        var source = Assert.Single(origin.Seen);
        Assert.Equal("fake-service-secret", source.Header("X-Service-Key"));
        Assert.Equal("original-service", source.Header("X-Service-Header"));
        var redirected = Assert.Single(target.Seen);
        Assert.Null(redirected.Header("X-Service-Key"));
        Assert.Null(redirected.Header("X-Service-Header"));
    }

    [Fact]
    public async Task Redirect_inside_the_same_service_preserves_its_credentials()
    {
        using var server = new TinyServer(request => request.Path == "/orders"
            ? TinyServer.Reply(302, "", "text/plain", "Location: /result")
            : TinyServer.Reply(200, "same-service", "text/plain"));
        var connection = Connection(server) with { Auth = ConnectionAuth.Header, AuthName = "X-Service-Key" };
        var answer = await EgressHttp.SendAsync(connection, Request(), "fake-service-secret");
        Assert.True(answer.Ok, answer.Explanation);
        Assert.Equal("same-service", answer.Body);
        Assert.Equal(2, server.Seen.Count);
        Assert.All(server.Seen, request => Assert.Equal("fake-service-secret", request.Header("X-Service-Key")));
    }
    [Fact]
    public async Task Timeout_covers_a_body_that_stalls_after_successful_headers()
    {
        using var server = new TinyServer(_ => null, stallAfterHeaders: true);
        var connection = Connection(server) with { TimeoutSeconds = 1, Retries = 0 };
        var answer = await EgressHttp.SendAsync(connection, Request(), "").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(answer.Ok);
        Assert.Contains("не ответил", answer.Explanation, StringComparison.Ordinal);
        Assert.Equal(1, answer.Attempts);
        Assert.Single(server.Seen);
    }
}
