using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Подготовка вызова (<see cref="EgressPlan"/>): что именно уйдет наружу — адрес, заголовки,
/// тело, можно ли повторять.
///
/// Все это проверяется БЕЗ сети, и в этом смысл разделения: сетевой слой должен быть тонким и
/// скучным, а решения, которые можно перепутать, — здесь, под тестами.
///
/// Две вещи важнее прочих. Первая: секрет попадает в вызов, но не попадает в его безопасный
/// вид — журнал, в котором виден токен, это место утечки, а не журнал. Вторая: POST и PATCH не
/// повторяются, потому что обрыв связи не значит, что запрос не дошел.
/// </summary>
public sealed class EgressPlanTests
{
    private const string Token = "T0K3N-СЕКРЕТ";

    private static IntegrationConnection Connection(
        ConnectionAuth auth = ConnectionAuth.None,
        string authName = "",
        string address = "https://srv.zavod.local/exchange") =>
        IntegrationConnection.New() with
        {
            Id       = 7,
            Name     = "Учетная система",
            Address  = address,
            Auth     = auth,
            AuthName = authName,
        };

    private static IntegrationRequest Request(
        RequestMethod method = RequestMethod.Get, string path = "/orders", string body = "") =>
        IntegrationRequest.New() with
        {
            Name         = "Заказы",
            ConnectionId = 7,
            Method       = method,
            Path         = path,
            Body         = body,
        };

    private static EgressPlan Plan(
        IntegrationConnection connection, IntegrationRequest request, string secret = Token)
    {
        var plan = EgressPlan.Build(connection, request, secret, out var error);
        Assert.True(plan is not null, error);
        return plan!;
    }

    // ── Адрес ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Address_keeps_the_subfolder_of_the_connection()
    {
        var plan = Plan(Connection(), Request());

        Assert.Equal("https://srv.zavod.local/exchange/orders", plan.Address.AbsoluteUri);
        Assert.Equal(plan.Address.AbsoluteUri, plan.SafeAddress);
    }

    [Fact]
    public void Broken_cards_give_no_plan_and_a_readable_reason()
    {
        Assert.Null(EgressPlan.Build(
            Connection() with { Address = "не адрес" }, Request(), Token, out var error));

        Assert.Contains("Адрес", error, StringComparison.Ordinal);
    }

    // ── Авторизация ───────────────────────────────────────────────────────────

    [Fact]
    public void Bearer_goes_into_the_authorization_header()
    {
        var plan = Plan(Connection(ConnectionAuth.Bearer), Request());

        Assert.Equal("Bearer " + Token, Header(plan.Headers, "Authorization"));
        Assert.Equal(EgressPlan.Mask, Header(plan.SafeHeaders, "Authorization"));
    }

    [Fact]
    public void Basic_packs_the_user_and_the_secret()
    {
        var plan = Plan(Connection(ConnectionAuth.Basic, "ivanov"), Request());

        var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("ivanov:" + Token));

        Assert.Equal("Basic " + expected, Header(plan.Headers, "Authorization"));
        Assert.Equal(EgressPlan.Mask, Header(plan.SafeHeaders, "Authorization"));
    }

    [Fact]
    public void Key_goes_into_its_own_header()
    {
        var plan = Plan(Connection(ConnectionAuth.Header, "X-Api-Key"), Request());

        Assert.Equal(Token, Header(plan.Headers, "X-Api-Key"));
        Assert.Equal(EgressPlan.Mask, Header(plan.SafeHeaders, "X-Api-Key"));
    }

    /// <summary>
    /// Ключ в адресе — самый опасный вид: не вырежи его из безопасного адреса, и он уйдет в
    /// журнал вместе с адресом.
    /// </summary>
    [Fact]
    public void Key_in_the_address_is_added_and_masked()
    {
        var plan = Plan(Connection(ConnectionAuth.Query, "api_key"), Request());

        Assert.EndsWith("?api_key=" + Uri.EscapeDataString(Token), plan.Address.AbsoluteUri,
            StringComparison.Ordinal);
        Assert.EndsWith("?api_key=" + EgressPlan.Mask, plan.SafeAddress, StringComparison.Ordinal);
    }

    [Fact]
    public void Key_joins_an_address_that_already_has_a_query()
    {
        var plan = Plan(Connection(ConnectionAuth.Query, "api_key"), Request(path: "/orders?since=1"));

        Assert.Contains("?since=1&api_key=", plan.Address.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("https://srv.zavod.local/exchange/orders?since=1&api_key=" + EgressPlan.Mask,
            plan.SafeAddress);
    }

    /// <summary>Общая проверка: в безопасном виде вызова секрета нет нигде.</summary>
    [Theory]
    [InlineData(ConnectionAuth.Bearer, "")]
    [InlineData(ConnectionAuth.Basic,  "ivanov")]
    [InlineData(ConnectionAuth.Header, "X-Api-Key")]
    [InlineData(ConnectionAuth.Query,  "api_key")]
    public void Secret_never_shows_in_the_safe_view(ConnectionAuth auth, string authName)
    {
        var plan = Plan(Connection(auth, authName), Request());

        var shown = plan.SafeAddress + " " + string.Join(" ", plan.SafeHeaders.Select(h => $"{h.Name}: {h.Value}"));

        Assert.DoesNotContain(Token, shown, StringComparison.Ordinal);
    }

    // ── Заголовки ─────────────────────────────────────────────────────────────

    /// <summary>Общее правило снизу, частный случай сверху: заголовок запроса перекрывает общий.</summary>
    [Fact]
    public void Request_header_overrides_the_connection_header()
    {
        var connection = Connection() with
        {
            Headers = [new IntegrationHeader("Accept", "*/*"), new IntegrationHeader("X-Client", "SmartBase")],
        };

        var request = Request() with { Headers = [new IntegrationHeader("Accept", "application/json")] };

        var plan = Plan(connection, request);

        Assert.Equal("application/json", Header(plan.Headers, "Accept"));
        Assert.Equal("SmartBase", Header(plan.Headers, "X-Client"));
        Assert.Equal(2, plan.Headers.Count);
    }

    // ── Тело и тип содержимого ────────────────────────────────────────────────

    /// <summary>GET с телом — негодная карточка, и вызов из нее не собирается вовсе.</summary>
    [Fact]
    public void Get_with_a_body_gives_no_plan()
    {
        Assert.Null(EgressPlan.Build(
            Connection(), Request(RequestMethod.Get) with { Body = "a=1" }, Token, out var error));

        Assert.Contains("GET", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_carries_no_body()
    {
        Assert.Equal("", Plan(Connection(), Request(RequestMethod.Get)).Body);
    }

    [Theory]
    [InlineData("""{"a":1}""", "application/json")]
    [InlineData("[1,2]",       "application/json")]
    [InlineData("a=1&b=2",     "application/x-www-form-urlencoded")]
    public void Content_type_is_guessed_the_way_curl_does(string body, string expected)
    {
        var plan = Plan(Connection(), Request(RequestMethod.Post, body: body));

        Assert.Equal(expected, plan.ContentType);
    }

    [Fact]
    public void Declared_content_type_wins_over_the_guess()
    {
        var request = Request(RequestMethod.Post, body: "<xml/>") with
        {
            Headers = [new IntegrationHeader("Content-Type", "application/xml")],
        };

        Assert.Equal("application/xml", Plan(Connection(), request).ContentType);
    }

    // ── Повторы ───────────────────────────────────────────────────────────────

    /// <summary>
    /// POST и PATCH не повторяются: обрыв связи не значит, что запрос не дошел, — он мог
    /// выполниться, а ответ потеряться. Повтор зарегистрировал бы документ дважды.
    /// </summary>
    [Theory]
    [InlineData(RequestMethod.Get,    true)]
    [InlineData(RequestMethod.Put,    true)]
    [InlineData(RequestMethod.Delete, true)]
    [InlineData(RequestMethod.Post,   false)]
    [InlineData(RequestMethod.Patch,  false)]
    public void Only_safe_methods_may_be_repeated(RequestMethod method, bool expected)
    {
        var body = method == RequestMethod.Get ? "" : "a=1";

        Assert.Equal(expected, Plan(Connection(), Request(method, body: body)).Idempotent);
    }

    // ── Проверка связи ────────────────────────────────────────────────────────

    /// <summary>
    /// Проверяют обычно недописанную карточку: сохраненного запроса еще нет, ключа соединения
    /// тоже. План при этом должен собираться.
    /// </summary>
    [Fact]
    public void Probe_works_on_an_unsaved_card()
    {
        var card = IntegrationConnection.New() with
        {
            Name    = "Учетная система",
            Address = "https://srv.zavod.local/exchange",
            Auth    = ConnectionAuth.Bearer,
        };

        var plan = EgressPlan.Probe(card, Token, out var error);

        Assert.True(plan is not null, error);
        Assert.Equal("https://srv.zavod.local/exchange", plan!.Address.AbsoluteUri);
        Assert.Equal(RequestMethod.Get, plan.Method);
        Assert.Equal("", plan.Body);
    }

    private static string? Header(IEnumerable<IntegrationHeader> headers, string name) =>
        headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}
