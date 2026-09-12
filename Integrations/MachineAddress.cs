using System.Net;
using System.Net.NetworkInformation;

namespace SmartBase.Integrations;

/// <summary>
/// Адрес этой машины в локальной сети — тот, который называют отправителю в сетевом режиме
/// приема (docs/ROADMAP-API.md, 6.2).
///
/// <b>Имя не разрешается.</b> Адреса берутся у сетевых плат самой машины
/// (<c>NetworkInterface</c>), а не через DNS: запрос к DNS — уже обращение наружу, и он под
/// запретом анализатора (10.5). Здесь нет ни сокета, ни соединения — только чтение настроек
/// собственных плат, которое ничего никому не сообщает.
///
/// Ответ — <b>подсказка человеку</b>, а не то, что программа куда-то подставляет. Слушает она
/// все адреса сразу (<c>http://+:порт/</c>), потому что на машине их бывает несколько:
/// проводная сеть, беспроводная, виртуальная плата. Выбрать «правильный» за человека нельзя —
/// но показать самый вероятный и перечислить остальные можно и нужно, иначе адрес приходится
/// выяснять командой в консоли.
/// </summary>
public static class MachineAddress
{
    /// <summary>
    /// Самый вероятный адрес машины; пусто — подходящих не нашлось. Предпочтение отдается
    /// адресам из частных сетей организации: публичный адрес на настольной машине почти всегда
    /// означает не то, что человек ищет.
    /// </summary>
    public static string Local()
    {
        var all = All();

        return all.FirstOrDefault(a => IPAddress.TryParse(a, out var parsed)
                                       && NetworkAddress.IsPrivate(parsed))
            ?? all.FirstOrDefault()
            ?? "";
    }

    /// <summary>
    /// Все адреса машины, годные для приема: IPv4, на работающих платах, кроме петлевой и
    /// туннельных. IPv6 не берется намеренно — отправители, которых закрывает этот канал
    /// (учетная система, скрипт, планировщик), почти всегда настраиваются адресом IPv4, а
    /// показать оба вида значило бы предложить выбор, которого человек не делает.
    /// </summary>
    public static IReadOnlyList<string> All()
    {
        var found = new List<string>();

        try
        {
            foreach (var card in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (card.OperationalStatus != OperationalStatus.Up) continue;
                if (card.NetworkInterfaceType is NetworkInterfaceType.Loopback
                                              or NetworkInterfaceType.Tunnel) continue;

                foreach (var address in card.GetIPProperties().UnicastAddresses)
                {
                    var ip = address.Address;

                    // IPv4 отличается длиной, а не перечислением семейства: так обходимся без
                    // типа из пространства сокетов, которое в этом файле неуместно.
                    if (ip.GetAddressBytes().Length != 4) continue;
                    if (IPAddress.IsLoopback(ip)) continue;

                    var text = ip.ToString();
                    if (!found.Contains(text, StringComparer.Ordinal)) found.Add(text);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Список плат не прочитался — не беда: адрес это подсказка, а слушать программа
            // будет все равно все адреса сразу.
        }

        return found;
    }
}
