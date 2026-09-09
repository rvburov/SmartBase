using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Карточка запроса (<see cref="IntegrationRequest"/>): что считается заполненным правильно и
/// какой адрес из нее получается.
///
/// Главная проверка здесь — относительность пути. Полный адрес в поле пути означал бы, что
/// карточка показывает одно соединение, а запрос уходит на другой хост; хост берется только из
/// соединения, потому что проверяет политика именно его.
/// </summary>
public sealed class IntegrationRequestTests
{
    private static IntegrationRequest Valid() =>
        IntegrationRequest.New() with
        {
            Name         = "Заказы за сутки",
            ConnectionId = 7,
            Path         = "/orders?since=2026-09-01",
        };

    private static IntegrationConnection Connection(string address) =>
        IntegrationConnection.New() with { Name = "Учетная система", Address = address };

    // ── Заполнено правильно ───────────────────────────────────────────────────

    [Fact]
    public void Filled_card_has_no_problems() =>
        Assert.Empty(Valid().Problems());

    [Fact]
    public void New_card_starts_with_sane_defaults()
    {
        var card = IntegrationRequest.New();

        Assert.Equal(RequestMethod.Get, card.Method);
        Assert.Equal(RequestPaging.None, card.Paging);
        Assert.Equal("/", card.Path);
        Assert.Empty(card.Headers);
        Assert.Equal("", card.Body);
        Assert.False(card.NeedsPagingField);
    }

    [Fact]
    public void Name_and_connection_are_required()
    {
        var problems = (IntegrationRequest.New() with { Path = "/orders" }).Problems();

        Assert.Contains(problems, p => p.Contains("Название", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("соединение", StringComparison.Ordinal));
    }

    // ── Путь ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Полный адрес, «//сервер» и «..» в пути уводят запрос с хоста соединения — а карточка при
    /// этом показывала бы прежнее безобидное соединение.
    /// </summary>
    [Theory]
    [InlineData("https://api.attacker.example/orders", "относительным")]
    [InlineData("//api.attacker.example/orders",       "относительным")]
    [InlineData("/../../admin",                        "«..»")]
    [InlineData("orders",                              "начинаться")]
    [InlineData("",                                    "не заполнен")]
    public void Bad_path_is_reported(string path, string expected)
    {
        var problems = (Valid() with { Path = path }).Problems();

        Assert.Contains(problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    /// <summary>Путь с подстановкой — обычное дело, разбираться он не обязан.</summary>
    [Fact]
    public void Path_with_a_variable_is_allowed() =>
        Assert.Empty((Valid() with { Path = "/orders?since={{последний_запуск}}" }).Problems());

    // ── Тело и метод ──────────────────────────────────────────────────────────

    [Fact]
    public void Get_with_a_body_is_reported()
    {
        var problems = (Valid() with { Body = "{\"a\":1}" }).Problems();

        Assert.Contains(problems, p => p.Contains("GET", StringComparison.Ordinal));
    }

    [Fact]
    public void Post_with_a_body_is_fine() =>
        Assert.Empty((Valid() with { Method = RequestMethod.Post, Body = "{\"a\":1}" }).Problems());

    // ── Разбивка на страницы ──────────────────────────────────────────────────

    [Theory]
    [InlineData(RequestPaging.Page,   "параметра")]
    [InlineData(RequestPaging.Offset, "параметра")]
    [InlineData(RequestPaging.Cursor, "следующей порции")]
    public void Paging_without_a_field_is_reported(RequestPaging paging, string expected)
    {
        var problems = (Valid() with { Paging = paging, PagingField = "" }).Problems();

        Assert.Contains(problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Paging_with_a_field_is_fine() =>
        Assert.Empty((Valid() with { Paging = RequestPaging.Cursor, PagingField = "next_cursor" }).Problems());

    // ── Заголовки ─────────────────────────────────────────────────────────────

    [Fact]
    public void Header_without_a_name_is_reported()
    {
        var problems = (Valid() with { Headers = [new IntegrationHeader("", "значение")] }).Problems();

        Assert.Contains(problems, p => p.Contains("заголовка не заполнено", StringComparison.Ordinal));
    }

    // ── Сборка адреса ─────────────────────────────────────────────────────────

    /// <summary>
    /// Подкаталог в адресе соединения сохраняется. По правилам Uri «/orders» заменил бы весь
    /// путь и дал «https://srv/orders» — человек, настроивший соединение на «/обмен», получил бы
    /// запрос не туда.
    /// </summary>
    [Theory]
    [InlineData("https://srv.zavod.local",          "/orders", "https://srv.zavod.local/orders")]
    [InlineData("https://srv.zavod.local/",         "/orders", "https://srv.zavod.local/orders")]
    [InlineData("https://srv.zavod.local/exchange", "/orders", "https://srv.zavod.local/exchange/orders")]
    [InlineData("https://srv.zavod.local:8443/v2",  "/orders", "https://srv.zavod.local:8443/v2/orders")]
    public void Address_is_built_from_connection_and_path(string address, string path, string expected)
    {
        var built = (Valid() with { Path = path }).BuildAddress(Connection(address));

        Assert.Equal(expected, built?.ToString());
    }

    [Fact]
    public void Root_path_gives_the_connection_address()
    {
        var built = (Valid() with { Path = "/" }).BuildAddress(Connection("https://srv.zavod.local/exchange"));

        Assert.Equal("https://srv.zavod.local/exchange", built?.ToString());
    }

    [Fact]
    public void Query_survives_the_build()
    {
        var built = Valid().BuildAddress(Connection("https://srv.zavod.local"));

        Assert.Equal("since=2026-09-01", built?.Query.TrimStart('?'));
    }

    /// <summary>Негодная карточка адреса не дает: отправлять по половине настроек нельзя.</summary>
    [Fact]
    public void Broken_card_gives_no_address()
    {
        Assert.Null((Valid() with { Path = "https://api.attacker.example" })
            .BuildAddress(Connection("https://srv.zavod.local")));

        Assert.Null(Valid().BuildAddress(Connection("не адрес")));
    }
}
