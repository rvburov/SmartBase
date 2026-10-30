using System.Net;
using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>Определение локальных адресов для показа адреса HTTP-слушателя.</summary>
public sealed class NetworkAddressTests
{
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
