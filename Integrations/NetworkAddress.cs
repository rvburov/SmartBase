using System.Net;
using System.Net.Sockets;

namespace SmartBase.Integrations;

/// <summary>
/// Разбор и сравнение сетевых адресов для белого списка: приведение имени к канону,
/// сопоставление с маской (<c>*.zavod.local</c>), проверка вхождения адреса в сеть
/// (<c>10.0.0.0/8</c>) и признак приватного диапазона.
///
/// Чистая логика без единого обращения к сети: имена здесь НЕ разрешаются в адреса.
/// Так и задумано — разрешение имени это ввод-вывод, оно живет в отправляющем слое, а сюда
/// приходит уже готовый <see cref="IPAddress"/> (см. <see cref="EgressGate"/>: проверка идет
/// дважды — по имени до запроса и по фактическому адресу перед соединением).
///
/// Почему сравнение по канону, а не по строке: один и тот же узел записывается десятком
/// способов — <c>API.Zavod.local.</c>, <c>[::ffff:10.0.0.1]</c>, <c>010.0.0.1</c>. Белый
/// список, сверяющий строки как есть, обходится сменой регистра, и это первое, что проверит
/// служба безопасности.
/// </summary>
public static class NetworkAddress
{
    /// <summary>
    /// Имя узла в каноне: нижний регистр, без завершающей точки и без скобок IPv6.
    /// Пустая строка на входе дает пустую на выходе — вызывающий решает, что это значит.
    /// </summary>
    public static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "";

        var value = host.Trim().ToLowerInvariant();

        // [::1] → ::1. Скобки — часть синтаксиса адреса в URL, к самому адресу они не относятся.
        if (value.Length > 1 && value[0] == '[' && value[^1] == ']')
            value = value[1..^1];

        // Завершающая точка — абсолютная форма доменного имени: «zavod.local.» и «zavod.local»
        // это один узел. Убираем ее, иначе маска перестает совпадать.
        value = value.TrimEnd('.');

        // Литерал адреса приводим к канону самого адреса: ::ffff:10.0.0.1 и 10.0.0.1 —
        // один узел, а строки разные.
        if (IPAddress.TryParse(value, out var ip))
            return Canonical(ip).ToString();

        return value;
    }

    /// <summary>
    /// Канон адреса: IPv4, записанный внутри IPv6 (<c>::ffff:10.0.0.1</c>), сводится к IPv4.
    /// Без этого одна и та же машина проходит проверку сетей в одной записи и не проходит
    /// в другой.
    /// </summary>
    public static IPAddress Canonical(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>
    /// Совпадает ли имя узла с маской белого списка. Поддерживаются две записи:
    /// <c>api.zavod.local</c> — точное имя; <c>*.zavod.local</c> — любое имя ВНУТРИ домена.
    ///
    /// Звездочка намеренно не покрывает сам домен: <c>*.zavod.local</c> разрешает
    /// <c>api.zavod.local</c>, но не <c>zavod.local</c>. Разрешение «домена целиком» должно
    /// быть написано отдельной строкой — иначе одна маска незаметно открывает больше, чем
    /// имел в виду тот, кто ее вписал.
    /// </summary>
    public static bool HostMatches(string? host, string? pattern)
    {
        var name = NormalizeHost(host);
        var mask = NormalizeHost(pattern);
        if (name.Length == 0 || mask.Length == 0) return false;

        if (mask.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = mask[1..];                  // «*.zavod.local» → «.zavod.local»
            return name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length;
        }

        return string.Equals(name, mask, StringComparison.Ordinal);
    }

    /// <summary>
    /// Разбор записи сети <c>10.0.0.0/8</c>. Голый адрес без дроби тоже разбирается — как
    /// сеть из одного адреса (<c>/32</c> либо <c>/128</c>): в белом списке «один сервер»
    /// пишут именно так. <c>false</c> — запись не разобралась, и вызывающий обязан считать
    /// такую строку запретом, а не разрешением.
    /// </summary>
    public static bool TryParseNetwork(string? value, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix  = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text  = value.Trim();
        var slash = text.IndexOf('/');

        var addressPart = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(addressPart, out var parsed)) return false;
        parsed = Canonical(parsed);

        var bits = parsed.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;

        if (slash < 0)
        {
            network = parsed;
            prefix  = bits;
            return true;
        }

        if (!int.TryParse(text[(slash + 1)..], out var declared)) return false;
        if (declared < 0 || declared > bits) return false;

        network = parsed;
        prefix  = declared;
        return true;
    }

    /// <summary>Входит ли адрес в сеть, записанную маской <c>10.0.0.0/8</c>.</summary>
    public static bool InNetwork(IPAddress? address, string? network)
    {
        if (address is null) return false;
        if (!TryParseNetwork(network, out var net, out var prefix)) return false;

        var value = Canonical(address);
        if (value.AddressFamily != net.AddressFamily) return false;

        var left  = value.GetAddressBytes();
        var right = net.GetAddressBytes();

        var wholeBytes = prefix / 8;
        for (var i = 0; i < wholeBytes; i++)
            if (left[i] != right[i]) return false;

        var restBits = prefix % 8;
        if (restBits == 0) return true;

        var mask = (byte)(0xFF << (8 - restBits));
        return (left[wholeBytes] & mask) == (right[wholeBytes] & mask);
    }

    /// <summary>
    /// Приватный ли адрес: сети организации, петля и адреса канала связи. Внутренний режим
    /// контура держится на этом признаке — он отсекает случай, когда имя из белого списка
    /// разрешилось в публичный адрес (подменили запись DNS либо просто переехал сервис).
    /// </summary>
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
