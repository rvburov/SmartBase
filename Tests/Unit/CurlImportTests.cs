using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Разбор строки cURL (<see cref="CurlParser"/>) — самый короткий путь к «настрой сам»:
/// человек копирует строку из чужой документации, программа заполняет карточки.
///
/// Две вещи здесь важнее остальных. Первая — секрет: строка из документации почти всегда его
/// содержит, и он должен быть вынут отдельно, а не осесть открытым текстом в заголовках, откуда
/// попал бы в app.db и в журнал запусков. Вторая — разбор ничего не выполняет и не читает с
/// диска: строку принесли снаружи.
/// </summary>
public sealed class CurlImportTests
{
    private static CurlImport Parse(string text)
    {
        Assert.True(CurlParser.TryParse(text, out var import, out var error), error);
        return import!;
    }

    private static string Fails(string text)
    {
        Assert.False(CurlParser.TryParse(text, out _, out var error));
        return error;
    }

    // ── Простые строки ────────────────────────────────────────────────────────

    [Fact]
    public void Bare_address_gives_a_get_request()
    {
        var import = Parse("curl https://srv.zavod.local/orders");

        Assert.Equal("https://srv.zavod.local", import.Address);
        Assert.Equal("/orders", import.Path);
        Assert.Equal(RequestMethod.Get, import.Method);
        Assert.Equal(ConnectionAuth.None, import.Auth);
        Assert.Empty(import.Headers);
        Assert.Equal("", import.Body);
    }

    [Fact]
    public void Port_stays_with_the_connection_address()
    {
        var import = Parse("curl 'http://srv.zavod.local:8080/api/v2/orders?since=1'");

        Assert.Equal("http://srv.zavod.local:8080", import.Address);
        Assert.Equal("/api/v2/orders?since=1", import.Path);
    }

    /// <summary>Схемы в строке может не быть; догадка в пользу https и о ней сказано вслух.</summary>
    [Fact]
    public void Missing_scheme_becomes_https_with_a_note()
    {
        var import = Parse("curl srv.zavod.local/orders");

        Assert.Equal("https://srv.zavod.local", import.Address);
        Assert.Contains(import.Notes, n => n.Contains("https", StringComparison.Ordinal));
    }

    [Fact]
    public void Url_can_come_from_the_url_key()
    {
        Assert.Equal("/orders", Parse("curl --url https://srv.zavod.local/orders").Path);
    }

    // ── Метод и тело ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("curl -X POST https://srv.zavod.local/orders",       RequestMethod.Post)]
    [InlineData("curl -XPOST https://srv.zavod.local/orders",        RequestMethod.Post)]
    [InlineData("curl --request PUT https://srv.zavod.local/orders", RequestMethod.Put)]
    [InlineData("curl --request=patch https://srv.zavod.local/o",    RequestMethod.Patch)]
    [InlineData("curl -X DELETE https://srv.zavod.local/orders/7",   RequestMethod.Delete)]
    public void Method_is_read_in_every_form(string text, RequestMethod expected) =>
        Assert.Equal(expected, Parse(text).Method);

    /// <summary>Тело без явного метода означает POST — так работает и сам cURL.</summary>
    [Fact]
    public void Body_without_a_method_means_post()
    {
        var import = Parse("""curl https://srv.zavod.local/orders -d '{"name":"Иван"}'""");

        Assert.Equal(RequestMethod.Post, import.Method);
        Assert.Equal("""{"name":"Иван"}""", import.Body);
    }

    /// <summary>В двойных кавычках экранирование снимается, а «\n» внутри JSON должен дожить.</summary>
    [Fact]
    public void Escapes_inside_double_quotes_are_unwrapped()
    {
        var import = Parse("""curl https://srv.zavod.local/o -d "{\"note\":\"первая\nвторая\"}" """);

        Assert.Equal("""{"note":"первая\nвторая"}""", import.Body);
    }

    [Fact]
    public void Several_data_keys_are_joined()
    {
        var import = Parse("curl https://srv.zavod.local/o -d 'a=1' -d 'b=2'");

        Assert.Equal("a=1&b=2", import.Body);
    }

    /// <summary>«-G» переносит данные в адрес: так пишут GET с длинным списком параметров.</summary>
    [Fact]
    public void Get_key_moves_the_body_into_the_address()
    {
        var import = Parse("curl -G https://srv.zavod.local/orders -d 'since=1' -d 'limit=100'");

        Assert.Equal(RequestMethod.Get, import.Method);
        Assert.Equal("/orders?since=1&limit=100", import.Path);
        Assert.Equal("", import.Body);
    }

    [Fact]
    public void Json_key_fills_the_body_and_the_headers()
    {
        var import = Parse("""curl https://srv.zavod.local/o --json '{"a":1}'""");

        Assert.Equal("""{"a":1}""", import.Body);
        Assert.Equal("application/json",
            import.Headers.First(h => h.Name == "Content-Type").Value);
    }

    /// <summary>
    /// «-d @файл» не читается сознательно: строку принесли снаружи, и лезть по ней в файловую
    /// систему нельзя.
    /// </summary>
    [Fact]
    public void Body_from_a_file_is_not_read()
    {
        var import = Parse("curl https://srv.zavod.local/o -d @/etc/passwd");

        Assert.Equal("", import.Body);
        Assert.Contains(import.Notes, n => n.Contains("файла", StringComparison.Ordinal));
    }

    // ── Заголовки ─────────────────────────────────────────────────────────────

    [Fact]
    public void Headers_are_read_in_every_form()
    {
        var import = Parse("curl https://srv.zavod.local/o -H 'Accept: application/json' "
                         + "--header='X-Client: SmartBase' -H'X-Room: 12'");

        Assert.Equal(["Accept", "X-Client", "X-Room"], import.Headers.Select(h => h.Name));
        Assert.Equal("application/json", import.Headers[0].Value);
    }

    /// <summary>Служебные заголовки считает транспорт; перенести их значило бы сломать запрос.</summary>
    [Fact]
    public void Transport_headers_are_dropped()
    {
        var import = Parse("curl https://srv.zavod.local/o -H 'Host: srv' -H 'Content-Length: 12' "
                         + "-H 'Accept: */*'");

        Assert.Equal(["Accept"], import.Headers.Select(h => h.Name));
    }

    // ── Секрет ────────────────────────────────────────────────────────────────

    [Fact]
    public void Bearer_token_is_taken_out_of_the_headers()
    {
        var import = Parse("curl https://srv.zavod.local/o -H 'Authorization: Bearer T0K3N'");

        Assert.Equal(ConnectionAuth.Bearer, import.Auth);
        Assert.Equal("T0K3N", import.Secret);
        Assert.Empty(import.Headers);
        Assert.Contains(import.Notes, n => n.Contains("отдельно", StringComparison.Ordinal));
    }

    [Fact]
    public void Basic_auth_is_decoded_from_the_header()
    {
        // base64("ivanov:пароль")
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("ivanov:пароль"));
        var import  = Parse($"curl https://srv.zavod.local/o -H 'Authorization: Basic {encoded}'");

        Assert.Equal(ConnectionAuth.Basic, import.Auth);
        Assert.Equal("ivanov", import.AuthName);
        Assert.Equal("пароль", import.Secret);
    }

    [Fact]
    public void User_key_becomes_basic_auth()
    {
        var import = Parse("curl https://srv.zavod.local/o -u ivanov:secret");

        Assert.Equal(ConnectionAuth.Basic, import.Auth);
        Assert.Equal("ivanov", import.AuthName);
        Assert.Equal("secret", import.Secret);
    }

    [Fact]
    public void Unknown_authorization_scheme_becomes_a_header_secret()
    {
        var import = Parse("curl https://srv.zavod.local/o -H 'Authorization: SBToken abc'");

        Assert.Equal(ConnectionAuth.Header, import.Auth);
        Assert.Equal("Authorization", import.AuthName);
        Assert.Equal("SBToken abc", import.Secret);
        Assert.Empty(import.Headers);
    }

    [Fact]
    public void Known_key_header_is_taken_out()
    {
        var import = Parse("curl https://srv.zavod.local/o -H 'X-Api-Key: K3Y' -H 'Accept: */*'");

        Assert.Equal(ConnectionAuth.Header, import.Auth);
        Assert.Equal("X-Api-Key", import.AuthName);
        Assert.Equal("K3Y", import.Secret);
        Assert.Equal(["Accept"], import.Headers.Select(h => h.Name));
    }

    /// <summary>
    /// Ключ в адресе опаснее прочих: не вынь его — и он ляжет в журнал запусков вместе с
    /// адресом, а журнал станет местом утечки.
    /// </summary>
    [Fact]
    public void Key_in_the_address_is_taken_out_of_the_path()
    {
        var import = Parse("curl 'https://srv.zavod.local/orders?since=1&token=T0K3N&limit=10'");

        Assert.Equal(ConnectionAuth.Query, import.Auth);
        Assert.Equal("token", import.AuthName);
        Assert.Equal("T0K3N", import.Secret);
        Assert.Equal("/orders?since=1&limit=10", import.Path);
        Assert.Contains(import.Notes, n => n.Contains("token", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_the_last_query_key_leaves_the_path_empty_of_it()
    {
        Assert.Equal("/orders", Parse("curl 'https://srv.zavod.local/orders?api_key=K'").Path);
    }

    /// <summary>Заголовок авторизации главнее ключа «-u»: он ближе к тому, что уйдет в сеть.</summary>
    [Fact]
    public void Authorization_header_wins_over_the_user_key()
    {
        var import = Parse("curl https://srv.zavod.local/o -u ivanov:secret -H 'Authorization: Bearer T'");

        Assert.Equal(ConnectionAuth.Bearer, import.Auth);
        Assert.Equal("T", import.Secret);
    }

    /// <summary>Секрет не должен уехать в карточки — они хранятся и показываются как есть.</summary>
    [Fact]
    public void Secret_does_not_reach_the_cards()
    {
        var import = Parse("curl https://srv.zavod.local/orders?token=T0K3N -H 'X-Client: SmartBase'");

        var connection = import.ToConnection("Учетная система");
        var request    = import.ToRequest("Заказы", connectionId: 7);

        Assert.DoesNotContain("T0K3N", connection.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("T0K3N", request.ToString(), StringComparison.Ordinal);
    }

    // ── Карточки на выходе ────────────────────────────────────────────────────

    [Fact]
    public void Cards_from_an_import_are_ready_to_save()
    {
        var import = Parse("curl -X POST 'https://srv.zavod.local/exchange/orders' "
                         + "-H 'Authorization: Bearer T' -d 'a=1' --max-time 60");

        var connection = import.ToConnection("Учетная система");
        var request    = import.ToRequest("Заказы", connectionId: 7);

        Assert.Empty(connection.Problems());
        Assert.Empty(request.Problems());
        Assert.Equal(60, connection.TimeoutSeconds);
        Assert.Equal("https://srv.zavod.local/exchange/orders",
            request.BuildAddress(connection with { Id = 7 })?.ToString());
    }

    [Fact]
    public void Timeout_is_read_from_max_time()
    {
        Assert.Equal(45, Parse("curl -m 45 https://srv.zavod.local/o").TimeoutSeconds);
        Assert.Null(Parse("curl https://srv.zavod.local/o").TimeoutSeconds);
    }

    // ── Переносы строк ────────────────────────────────────────────────────────

    /// <summary>
    /// Строку копируют из bash, из cmd и из PowerShell — человеку все равно, какая оболочка ее
    /// породила, поэтому все три вида переноса разбираются одинаково.
    /// </summary>
    [Theory]
    [InlineData("\\")]
    [InlineData("^")]
    [InlineData("`")]
    public void Line_continuations_of_every_shell_are_understood(string sign)
    {
        var import = Parse($"curl 'https://srv.zavod.local/orders' {sign}\n"
                         + $"  -H 'Accept: application/json' {sign}\r\n"
                         + "  -H 'X-Client: SmartBase'");

        Assert.Equal("/orders", import.Path);
        Assert.Equal(["Accept", "X-Client"], import.Headers.Select(h => h.Name));
    }

    [Fact]
    public void Shell_prompt_in_front_is_ignored()
    {
        Assert.Equal("/orders", Parse("$ curl https://srv.zavod.local/orders").Path);
    }

    // ── Что не разбирается ────────────────────────────────────────────────────

    [Theory]
    [InlineData("wget https://srv.zavod.local/o",   "curl")]
    [InlineData("curl -X POST",                     "адреса")]
    [InlineData("",                                 "Пусто")]
    [InlineData("curl -X HEAD https://srv.local/o", "HEAD")]
    [InlineData("curl ftp://srv.zavod.local/o",     "не поддерживается")]
    [InlineData("curl 'https://srv.local/o",        "кавычка")]
    public void Unusable_string_gives_a_readable_reason(string text, string expected) =>
        Assert.Contains(expected, Fails(text), StringComparison.Ordinal);

    /// <summary>
    /// Незнакомое не роняет разбор: половина настроенного запроса лучше отказа со словом
    /// «непонятно». Все пропущенное перечислено в примечаниях.
    /// </summary>
    [Fact]
    public void Unknown_keys_become_notes_and_do_not_stop_the_parse()
    {
        var import = Parse("curl -sSL --compressed --insecure --proxy http://p:3128 "
                         + "-F 'file=@a.txt' --unheard-of https://srv.zavod.local/orders");

        Assert.Equal("/orders", import.Path);
        Assert.Contains(import.Notes, n => n.Contains("--proxy", StringComparison.Ordinal));
        Assert.Contains(import.Notes, n => n.Contains("--unheard-of", StringComparison.Ordinal));
        Assert.Contains(import.Notes, n => n.Contains("сертификат", StringComparison.Ordinal));
        Assert.Contains(import.Notes, n => n.Contains("форма", StringComparison.Ordinal));
    }

    [Fact]
    public void Extra_fragment_is_noted_not_swallowed()
    {
        var import = Parse("curl https://srv.zavod.local/orders лишнее");

        Assert.Equal("/orders", import.Path);
        Assert.Contains(import.Notes, n => n.Contains("лишнее", StringComparison.Ordinal));
    }
}
