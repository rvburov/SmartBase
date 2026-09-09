using System.Net;
using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Сравнение адресов для белого списка (<see cref="NetworkAddress"/>). Проверяется ровно то,
/// чем белый список обходится, если сверять строки как есть: другой регистр, завершающая
/// точка, IPv4 внутри IPv6, маска домена, покрывающая больше задуманного.
///
/// Разрешения имен здесь нет и быть не может — это чистая логика; проверка фактического
/// адреса соединения живет в <see cref="EgressGate.CheckResolved(IPAddress?, DateOnly)"/>.
/// </summary>
public sealed class NetworkAddressTests
{
    // ── Канон имени ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("API.Zavod.Local",  "api.zavod.local")]
    [InlineData("api.zavod.local.", "api.zavod.local")]   // абсолютная форма имени
    [InlineData("  api.zavod.local  ", "api.zavod.local")]
    [InlineData("[::1]",            "::1")]               // скобки — часть синтаксиса URL
    [InlineData("[::ffff:10.0.0.1]", "10.0.0.1")]         // IPv4, записанный внутри IPv6
    [InlineData("",                 "")]
    public void Host_is_reduced_to_a_canon(string input, string expected) =>
        Assert.Equal(expected, NetworkAddress.NormalizeHost(input));

    [Fact]
    public void Same_host_written_differently_matches_the_same_mask()
    {
        Assert.True(NetworkAddress.HostMatches("API.Zavod.local.", "api.zavod.local"));
        Assert.True(NetworkAddress.HostMatches("[::ffff:10.0.0.1]", "10.0.0.1"));
    }

    // ── Маски ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Star_mask_covers_subdomains()
    {
        Assert.True(NetworkAddress.HostMatches("api.zavod.local", "*.zavod.local"));
        Assert.True(NetworkAddress.HostMatches("srv.buh.zavod.local", "*.zavod.local"));
    }

    /// <summary>
    /// «*.zavod.local» НЕ разрешает сам «zavod.local». Иначе одна строка в белом списке
    /// открывает больше, чем имел в виду тот, кто ее вписал, — а список читают глазами.
    /// </summary>
    [Fact]
    public void Star_mask_does_not_cover_the_domain_itself()
    {
        Assert.False(NetworkAddress.HostMatches("zavod.local", "*.zavod.local"));
    }

    /// <summary>Похожее имя не должно проходить: сравнение идет по границе метки, а не по хвосту строки.</summary>
    [Fact]
    public void Star_mask_does_not_cover_a_lookalike_domain()
    {
        Assert.False(NetworkAddress.HostMatches("api.zavod.local.attacker.example", "*.zavod.local"));
        Assert.False(NetworkAddress.HostMatches("notzavod.local", "*.zavod.local"));
    }

    [Fact]
    public void Exact_mask_matches_only_itself()
    {
        Assert.True(NetworkAddress.HostMatches("api.zavod.local", "api.zavod.local"));
        Assert.False(NetworkAddress.HostMatches("srv.api.zavod.local", "api.zavod.local"));
    }

    [Theory]
    [InlineData("",                 "*.zavod.local")]
    [InlineData("api.zavod.local",  "")]
    [InlineData(null,               "*.zavod.local")]
    public void Empty_host_or_mask_never_matches(string? host, string? mask) =>
        Assert.False(NetworkAddress.HostMatches(host, mask));

    /// <summary>
    /// Голая звездочка — не «разрешить все»: маска обязана называть домен. Записи «открыть
    /// наружу целиком» в белом списке нет вовсе, и появиться она не должна по недосмотру —
    /// расширять список можно только поименно.
    /// </summary>
    [Fact]
    public void Bare_star_is_not_a_wildcard()
    {
        Assert.False(NetworkAddress.HostMatches("api.attacker.example", "*"));
        Assert.False(NetworkAddress.HostMatches("zavod.local", "*"));
    }

    // ── Сети ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("10.1.2.3",     "10.0.0.0/8",     true)]
    [InlineData("11.1.2.3",     "10.0.0.0/8",     false)]
    [InlineData("172.16.0.1",   "172.16.0.0/12",  true)]
    [InlineData("172.31.255.1", "172.16.0.0/12",  true)]
    [InlineData("172.32.0.1",   "172.16.0.0/12",  false)]   // граница маски не по байту
    [InlineData("172.15.0.1",   "172.16.0.0/12",  false)]
    [InlineData("10.0.7.15",    "10.0.7.15",      true)]    // голый адрес — сеть из одного узла
    [InlineData("10.0.7.16",    "10.0.7.15",      false)]
    [InlineData("::1",          "::1/128",        true)]
    [InlineData("10.1.2.3",     "::1/128",        false)]   // разные семейства адресов
    public void Network_membership_is_checked_by_bits(string address, string network, bool expected) =>
        Assert.Equal(expected, NetworkAddress.InNetwork(IPAddress.Parse(address), network));

    /// <summary>
    /// Негодная запись сети — запрет, а не разрешение. Строка в политике может быть опечаткой,
    /// и трактовать ее «раз не разобралось, значит подходит» нельзя ни при каких условиях.
    /// </summary>
    [Theory]
    [InlineData("10.0.0.0/40")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("мусор")]
    [InlineData("")]
    [InlineData(null)]
    public void Broken_network_record_denies(string? network) =>
        Assert.False(NetworkAddress.InNetwork(IPAddress.Parse("10.1.2.3"), network));

    // ── Приватность ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.0.1")]     // адрес канала связи
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]         // уникальные локальные адреса IPv6
    [InlineData("::ffff:10.0.0.1")] // IPv4 внутри IPv6 — тот же приватный адрес
    public void Private_ranges_are_recognized(string address) =>
        Assert.True(NetworkAddress.IsPrivate(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("192.169.1.1")]
    [InlineData("2001:4860:4860::8888")]
    public void Public_addresses_are_not_private(string address) =>
        Assert.False(NetworkAddress.IsPrivate(IPAddress.Parse(address)));
}
