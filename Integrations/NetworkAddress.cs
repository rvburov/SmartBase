using System.Net;
using System.Net.Sockets;

namespace SmartBase.Integrations;

/// <summary>Определение локальных адресов для показа адреса машины в настройках приема.</summary>
public static class NetworkAddress
{
    /// <summary>Привести IPv4 внутри IPv6 к обычной записи IPv4.</summary>
    public static IPAddress Canonical(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>Локальный, петлевой или адрес канала связи.</summary>
    public static bool IsPrivate(IPAddress? address)
    {
        if (address is null) return false;
        var value = Canonical(address);

        if (IPAddress.IsLoopback(value)) return true;

        if (value.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = value.GetAddressBytes();
            return b[0] switch
            {
                10  => true,                              // 10.0.0.0/8
                172 => b[1] >= 16 && b[1] <= 31,          // 172.16.0.0/12
                192 => b[1] == 168,                       // 192.168.0.0/16
                169 => b[1] == 254,                       // 169.254.0.0/16 — адрес канала связи
                _   => false,
            };
        }

        if (value.IsIPv6LinkLocal || value.IsIPv6SiteLocal) return true;

        // fc00::/7 — уникальные локальные адреса IPv6, аналог 10.0.0.0/8.
        var bytes = value.GetAddressBytes();
        return (bytes[0] & 0xFE) == 0xFC;
    }
}
