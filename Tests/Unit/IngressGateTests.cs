using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>Проверка технических настроек HTTP-слушателя без открытия порта.</summary>
public sealed class IngressGateTests
{
    [Fact]
    public void By_default_nothing_is_listening()
    {
        var decision = IngressGate.Check(IngressMode.Off, IngressGate.DefaultPort);
        Assert.False(decision.Allowed);
        Assert.Equal(IngressDenyReason.Disabled, decision.Reason);
    }

    [Theory]
    [InlineData(IngressMode.Loopback)]
    [InlineData(IngressMode.Network)]
    public void Configured_listener_modes_need_no_external_policy(IngressMode mode)
    {
        Assert.True(IngressGate.Check(mode, IngressGate.DefaultPort).Allowed);
    }

    [Fact]
    public void Unknown_mode_is_reported_instead_of_opening_a_different_listener()
    {
        var decision = IngressGate.Check((IngressMode)99, IngressGate.DefaultPort);
        Assert.False(decision.Allowed);
        Assert.Equal(IngressDenyReason.BadMode, decision.Reason);
    }

    [Fact]
    public void Network_listener_binds_all_addresses_and_loopback_binds_only_localhost()
    {
        Assert.Equal($"http://+:{IngressGate.DefaultPort}/",
            IngressGate.Prefix(IngressMode.Network, IngressGate.DefaultPort));
        Assert.Equal($"http://{IngressGate.LoopbackHost}:{IngressGate.DefaultPort}/",
            IngressGate.Prefix(IngressMode.Loopback, IngressGate.DefaultPort));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(1023)]
    [InlineData(65536)]
    public void System_and_impossible_ports_are_refused(int port)
    {
        var decision = IngressGate.Check(IngressMode.Loopback, port);
        Assert.False(decision.Allowed);
        Assert.Equal(IngressDenyReason.BadPort, decision.Reason);
        Assert.False(IngressGate.ValidPort(port));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(8787)]
    [InlineData(65535)]
    public void Valid_port_boundaries_are_supported(int port)
    {
        Assert.True(IngressGate.Check(IngressMode.Loopback, port).Allowed);
        Assert.True(IngressGate.ValidPort(port));
    }

    [Fact]
    public void Disabled_and_invalid_listeners_do_not_advertise_an_address()
    {
        Assert.Equal($"http://{IngressGate.LoopbackHost}:{IngressGate.DefaultPort}",
            IngressGate.Address(IngressMode.Loopback, IngressGate.DefaultPort));
        Assert.Equal("", IngressGate.Address(IngressMode.Off, IngressGate.DefaultPort));
        Assert.Equal("", IngressGate.Address(IngressMode.Network, 0));
        Assert.Equal("", IngressGate.Address((IngressMode)99, IngressGate.DefaultPort));
    }

    [Fact]
    public void The_http_body_cap_matches_reading_into_memory()
    {
        Assert.Equal(InboxFolder.MaxFileBytes, IngressGate.MaxBodyBytes);
    }

    [Fact]
    public void The_ordinary_build_has_a_listener() => Assert.True(IngressGate.ListenerBuild);
}
