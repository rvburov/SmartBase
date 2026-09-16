using System.Net;
using System.Net.Sockets;
using System.Text;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Отправляющий слой (<see cref="EgressHttp"/>) на настоящем сокете: запросы уходят на
/// крошечный сервер, поднятый тут же на петле.
///
/// Проверяется не «работает ли HTTP» — это забота платформы, — а обещания слоя, которые легко
/// потерять правкой: шлюз спрашивается ДО отправки, за перенаправлением слой идет только проверив
/// новый адрес заново, ответ
/// обрезается по потолку, POST не повторяется, а отказ политики выглядит отказом политики, а
/// не сетевым сбоем.
///
/// Сервер свой, на <see cref="TcpListener"/>, а не <c>HttpListener</c>: тому на Windows нужен
/// либо администратор, либо заранее выданная запись urlacl, и тест переставал бы работать на
/// чужой машине по причине, к делу не относящейся.
///
/// Шлюз — глобальное состояние, поэтому каждый тест сам ставит нужную политику и в конце
/// закрывает шлюз. Тесты одного класса xUnit выполняет по очереди, так что мешать друг другу
/// они не могут.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class EgressHttpTests : IDisposable
{
    public void Dispose() => EgressGate.Close();

    /// <summary>Политика «можно на петлю»: и по имени, и по фактическому адресу.</summary>
    private static void AllowLoopback() =>
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal,
            Hosts:           ["127.0.0.1", "localhost"],
            Networks:        ["127.0.0.0/8", "::1/128"],
            Folders:         [],
            MaxRowsPerRun:   1000,
            MaxBytesPerDay:  1024 * 1024,
            MaxRunsPerHour:  100,
            ValidUntil:      null,
            UserMayAddHosts: false,
            Source:          "тест")));

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
        AllowLoopback();
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
        AllowLoopback();
        using var server = new TinyServer(_ =>
            TinyServer.Reply(200, "Зарегистрирован", "text/plain; charset=utf-8"));

        var answer = await EgressHttp.SendAsync(Connection(server), Request(), secret: "");

        Assert.Equal("Зарегистрирован", answer.Body);
    }

    [Fact]
    public async Task Post_carries_the_body_the_headers_and_the_secret()
    {
        AllowLoopback();
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
        AllowLoopback();
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
        AllowLoopback();
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
        AllowLoopback();
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
    /// Перенаправление НА ЗАПРЕЩЕННЫЙ адрес не проходит: новый адрес — это новое обращение
    /// наружу, и шлюз спрашивается о нем заново. Без этого одним ответом чужого сервера белый
    /// список обходился бы целиком.
    /// </summary>
    [Fact]
    public async Task A_redirect_outside_the_white_list_is_refused()
    {
        AllowLoopback();   // разрешена только петля
        using var server = new TinyServer(_ => TinyServer.Reply(
            302, "", "text/plain", "Location: http://api.postavshik.example/забрать"));

        var answer = await EgressHttp.SendAsync(Connection(server), Request(), secret: "");

        Assert.False(answer.Ok);
        Assert.Equal(EgressDenyReason.HostNotAllowed, answer.Reason);
        Assert.Contains("перенаправление", answer.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// За перенаправлением запроса С ТЕЛОМ слой не идет. Тело на новом адресе можно либо
    /// отправить туда, куда человек не собирался (это вынос данных), либо тихо выбросить — и оба
    /// пути хуже отказа.
    /// </summary>
    [Fact]
    public async Task A_redirect_of_a_request_with_a_body_is_refused()
    {
        AllowLoopback();
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
        AllowLoopback();
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
        AllowLoopback();
        using var server = new TinyServer(_ => null);   // принимает и молчит

        var connection = Connection(server) with { TimeoutSeconds = 1 };
        var answer     = await EgressHttp.SendAsync(connection, Request(), secret: "");

        Assert.False(answer.Sent);
        Assert.Equal(EgressDenyReason.None, answer.Reason);   // это не отказ политики
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
        AllowLoopback();
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
        AllowLoopback();
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
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, "ok", "text/plain"), truncateFirst: true);

        var connection = Connection(server) with { Retries = 3, TimeoutSeconds = 5 };
        var answer     = await EgressHttp.SendAsync(
            connection, Request(RequestMethod.Patch, body: "a=1"), secret: "");

        Assert.False(answer.Sent);
        Assert.Equal(1, answer.Attempts);
    }

    // ── Шлюз ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Без политики наружу нельзя ничего, и запрос до сокета не доходит. Это состояние по
    /// умолчанию: программа, у которой политику не настроили, молчит.
    /// </summary>
    [Fact]
    public async Task Without_a_policy_nothing_leaves()
    {
        EgressGate.Close();
        using var server = new TinyServer(_ => TinyServer.Reply(200, "ok", "text/plain"));

        var answer = await EgressHttp.SendAsync(Connection(server), Request(), secret: "");

        Assert.False(answer.Sent);
        Assert.Equal(EgressDenyReason.NoPolicy, answer.Reason);
        Assert.Empty(server.Seen);
    }

    /// <summary>Адрес не из белого списка: отказ выглядит отказом политики и объясняет себя.</summary>
    [Fact]
    public async Task Address_outside_the_list_is_refused_before_the_socket()
    {
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal, ["srv.zavod.local"], ["10.0.0.0/8"], [],
            1000, 1024, 100, null, false, "тест")));

        using var server = new TinyServer(_ => TinyServer.Reply(200, "ok", "text/plain"));

        var answer = await EgressHttp.SendAsync(Connection(server), Request(), secret: "");

        Assert.False(answer.Sent);
        Assert.Equal(EgressDenyReason.HostNotAllowed, answer.Reason);
        Assert.NotEqual("", answer.Explanation);
        Assert.Empty(server.Seen);
    }

    /// <summary>
    /// Имя разрешено, а фактический адрес — нет. Именно этим и ловится подмена записи DNS
    /// между сверкой имени и соединением.
    /// </summary>
    [Fact]
    public async Task Allowed_name_with_a_forbidden_address_is_refused()
    {
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal, ["127.0.0.1"], ["10.0.0.0/8"], [],
            1000, 1024, 100, null, false, "тест")));

        using var server = new TinyServer(_ => TinyServer.Reply(200, "ok", "text/plain"));

        var answer = await EgressHttp.SendAsync(Connection(server), Request(), secret: "");

        Assert.False(answer.Sent);
        Assert.Equal(EgressDenyReason.AddressNotAllowed, answer.Reason);
        Assert.Empty(server.Seen);
    }

    /// <summary>Негодная карточка до сети не доходит вовсе.</summary>
    [Fact]
    public async Task Broken_card_is_refused_with_its_own_reason()
    {
        AllowLoopback();

        var answer = await EgressHttp.SendAsync(
            IntegrationConnection.New() with { Name = "Без адреса" }, Request(), secret: "");

        Assert.False(answer.Sent);
        Assert.Equal(EgressDenyReason.BadAddress, answer.Reason);
        Assert.Contains("Адрес не заполнен", answer.Explanation, StringComparison.Ordinal);
    }

}
