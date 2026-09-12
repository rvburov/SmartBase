using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Выдача чтения как модель (<see cref="ReadGrant"/>): сроки, простой, разбор состава — все,
/// что решается без базы и без двери.
/// </summary>
public sealed class ReadGrantTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static ReadGrant Make(DateTimeOffset? until = null, DateTimeOffset? used = null,
                                  IReadOnlyList<string>? keys = null) =>
        new(1, 5, 9, "Заказы", keys ?? [ReadGrant.TitleKey, "12"], "Название, Номер",
            "Буров Р. В.", Now.AddDays(-1), until ?? Now.AddDays(30), used);

    // ── Срок и простой ────────────────────────────────────────────────────────

    [Fact]
    public void A_fresh_grant_works() => Assert.Null(Make().Trouble(Now));

    [Fact]
    public void An_expired_grant_names_the_date()
    {
        var trouble = Make(until: Now.AddDays(-1)).Trouble(Now);

        Assert.NotNull(trouble);
        Assert.Contains("срок выдачи вышел", trouble, StringComparison.Ordinal);
    }

    /// <summary>Настроенное и забытое чтение — открытая дверь: месяц простоя гасит выдачу,
    /// тем же сроком, что у исходящего направления.</summary>
    [Fact]
    public void An_idle_grant_switches_itself_off()
    {
        var grant = Make(until: Now.AddDays(300)) with { At = Now.AddDays(-40) };

        Assert.Contains("не пользовались", grant.Trouble(Now)!, StringComparison.Ordinal);

        // Чтение было недавно — простой считается от него, а не от подтверждения.
        Assert.Null((grant with { UsedAt = Now.AddDays(-2) }).Trouble(Now));
    }

    [Fact]
    public void A_grant_without_columns_does_not_work()
    {
        Assert.NotNull(Make(keys: []).Trouble(Now));
    }

    // ── Состав ────────────────────────────────────────────────────────────────

    [Fact]
    public void Keys_survive_the_round_trip()
    {
        var packed = ReadGrant.PackKeys([ReadGrant.TitleKey, "12", "15"]);

        Assert.Equal("t,12,15", packed);
        Assert.Equal([ReadGrant.TitleKey, "12", "15"], ReadGrant.UnpackKeys(packed));
    }

    /// <summary>Мусор из правленного руками app.db выдачу не расширяет — он отбрасывается.</summary>
    [Theory]
    [InlineData("t, DROP TABLE, 12", 2)]
    [InlineData("0,-5,abc", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void Garbage_keys_are_dropped(string? text, int kept) =>
        Assert.Equal(kept, ReadGrant.UnpackKeys(text).Count);

    // ── Параметры обращения ───────────────────────────────────────────────────

    [Fact]
    public void Call_parameters_are_parsed_and_decoded()
    {
        var call = new IngressCall("GET", "/v1/records", "", "",
                                   Query: "?collection=%D0%97%D0%B0%D0%BA%D0%B0%D0%B7%D1%8B&after=5");

        Assert.Equal("Заказы", call.Param("collection"));
        Assert.Equal("5",      call.Param("after"));
        Assert.Equal("",       call.Param("since"));
    }

    [Fact]
    public void A_plus_in_a_parameter_is_a_space()
    {
        var call = new IngressCall("GET", "/v1/records", "", "",
                                   Query: "collection=Заказы+за+сутки");

        Assert.Equal("Заказы за сутки", call.Param("collection"));
    }
}
