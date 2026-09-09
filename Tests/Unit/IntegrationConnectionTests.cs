using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Карточка соединения (<see cref="IntegrationConnection"/>): что считается заполненным
/// правильно и что нет.
///
/// Проверяется ФОРМА, а не доступность. Разрешен ли адрес политикой — вопрос
/// <see cref="EgressGate"/>, отвечает ли сервер — кнопки «Проверить связь». Смешивать эти три
/// вопроса нельзя: адрес, запрещенный политикой, все равно должен сохраняться (политику
/// расширят завтра), а неразобравшийся адрес — нет.
/// </summary>
public sealed class IntegrationConnectionTests
{
    private static IntegrationConnection Valid() =>
        IntegrationConnection.New() with
        {
            Name    = "Учетная система",
            Address = "https://srv.zavod.local/exchange",
        };

    // ── Заполнено правильно ───────────────────────────────────────────────────

    [Fact]
    public void Filled_card_has_no_problems() =>
        Assert.Empty(Valid().Problems());

    [Fact]
    public void New_card_starts_with_sane_defaults()
    {
        var card = IntegrationConnection.New();

        Assert.Equal(IntegrationConnection.DefaultTimeoutSeconds, card.TimeoutSeconds);
        Assert.Equal(IntegrationConnection.DefaultRetries, card.Retries);
        Assert.Equal(ConnectionAuth.None, card.Auth);
        Assert.Empty(card.Headers);
        Assert.False(card.NeedsSecret);
    }

    // ── Адрес ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("",                          "Адрес не заполнен")]
    [InlineData("srv.zavod.local/exchange",  "не разобрался")]      // без схемы
    [InlineData("ftp://srv.zavod.local",     "не поддерживается")]
    [InlineData("file:///P:/obmen",          "не поддерживается")]
    public void Bad_address_is_reported(string address, string expected)
    {
        var problems = (Valid() with { Address = address }).Problems();

        Assert.Contains(problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Address_that_policy_would_refuse_is_still_a_valid_card()
    {
        // Форма правильная — сохранять можно; разрешит ли политика, решает шлюз при отправке.
        Assert.Empty((Valid() with { Address = "https://api.attacker.example" }).Problems());
    }

    [Fact]
    public void Base_address_is_parsed_and_trimmed()
    {
        var card = Valid() with { Address = "  https://srv.zavod.local/exchange  " };

        Assert.Equal("srv.zavod.local", card.BaseAddress?.Host);
        Assert.Null((Valid() with { Address = "не адрес" }).BaseAddress);
    }

    // ── Авторизация ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ConnectionAuth.None,   false, false)]
    [InlineData(ConnectionAuth.Bearer, true,  false)]
    [InlineData(ConnectionAuth.Header, true,  true)]
    [InlineData(ConnectionAuth.Query,  true,  true)]
    [InlineData(ConnectionAuth.Basic,  true,  true)]
    public void Auth_kind_says_what_it_needs(ConnectionAuth auth, bool secret, bool name)
    {
        var card = Valid() with { Auth = auth };

        Assert.Equal(secret, card.NeedsSecret);
        Assert.Equal(name, card.NeedsAuthName);
    }

    [Fact]
    public void Header_auth_without_a_header_name_is_reported()
    {
        var problems = (Valid() with { Auth = ConnectionAuth.Header, AuthName = "" }).Problems();

        Assert.Contains(problems, p => p.Contains("заголовка", StringComparison.Ordinal));
    }

    [Fact]
    public void Basic_auth_without_a_user_name_is_reported()
    {
        var problems = (Valid() with { Auth = ConnectionAuth.Basic, AuthName = "" }).Problems();

        Assert.Contains(problems, p => p.Contains("имя пользователя", StringComparison.Ordinal));
    }

    /// <summary>Токену имя не нужно: он всегда уходит в заголовке Authorization.</summary>
    [Fact]
    public void Bearer_needs_no_name()
    {
        Assert.Empty((Valid() with { Auth = ConnectionAuth.Bearer, AuthName = "" }).Problems());
    }

    // ── Границы ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1000)]
    public void Timeout_outside_the_bounds_is_reported(int seconds)
    {
        var problems = (Valid() with { TimeoutSeconds = seconds }).Problems();

        Assert.Contains(problems, p => p.Contains("Таймаут", StringComparison.Ordinal));
    }

    /// <summary>Потолок повторов нужен затем, что обрыв связи иначе дает бесконечный цикл.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Retries_outside_the_bounds_are_reported(int retries)
    {
        var problems = (Valid() with { Retries = retries }).Problems();

        Assert.Contains(problems, p => p.Contains("Повторов", StringComparison.Ordinal));
    }

    [Fact]
    public void Zero_retries_is_allowed()
    {
        Assert.Empty((Valid() with { Retries = 0 }).Problems());
    }

    // ── Заголовки ─────────────────────────────────────────────────────────────

    [Fact]
    public void Header_without_a_name_is_reported()
    {
        var problems = (Valid() with { Headers = [new IntegrationHeader("", "значение")] }).Problems();

        Assert.Contains(problems, p => p.Contains("заголовка не заполнено", StringComparison.Ordinal));
    }

    [Fact]
    public void Several_problems_are_reported_at_once()
    {
        var card = IntegrationConnection.New() with { Auth = ConnectionAuth.Header, TimeoutSeconds = 0 };

        // Имя, адрес, имя заголовка, таймаут — четыре разом: человек должен увидеть все, а не
        // исправлять по одному и жать «Сохранить» четыре раза.
        Assert.Equal(4, card.Problems().Count);
    }
}
