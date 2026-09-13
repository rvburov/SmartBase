using System.Globalization;
using System.Text;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Что именно просят разрешить: адрес, зачем он нужен и что через него пойдет. Собирается из
/// карточек, а не пишется человеком.
/// </summary>
/// <param name="Address">Адрес соединения — то, что просят добавить в политику.</param>
/// <param name="Connection">Имя соединения: им человек называет эту дверь на своем экране.</param>
/// <param name="Refusal">Как программа отказала сейчас — дословно.</param>
/// <param name="Uses">Что через это соединение пойдет: запрос, коллекция, поля.</param>
/// <param name="Who">Кто просит.</param>
/// <param name="Machine">С какой машины: политика бывает разной на разных рабочих местах.</param>
public sealed record AddressRequest(
    string                Address,
    string                Connection,
    string                Refusal,
    IReadOnlyList<string> Uses,
    string                Who,
    string                Machine);

/// <summary>
/// Готовая заявка на разрешение адреса — текст в буфер обмена (docs/ROADMAP-API.md, 10.18).
///
/// <b>Зачем это вообще.</b> Отказ «адрес не входит в белый список организации» оставляет человека
/// с задачей, которую он решать не умеет: к кому идти, что писать, какие сведения приложить.
/// Ответственный за интеграции все равно спросит то же самое — адрес, зачем, какая коллекция,
/// какие поля, — и спросит не сразу, а через день. Заявка собирает эти ответы из карточек, где
/// они уже лежат, и кладет в буфер готовым письмом.
///
/// <b>Секретов в заявке нет.</b> Она уходит в переписку и пересылается дальше, а это ровно тот
/// путь, на котором токен теряется навсегда. Адрес берется тем же замаскированным видом, каким он
/// попадает в журналы; ни секрета соединения, ни значений полей здесь нет — только имена.
///
/// Текст без ввода-вывода и без экрана: его проверяют тестами построчно, а кладет в буфер
/// вызывающий.
/// </summary>
public static class AddressRequestNote
{
    /// <summary>
    /// Собрать заявку по соединению. Запросы, коллекции и поля берутся из реестра — те же, что
    /// человек видит на экране.
    /// </summary>
    /// <param name="registry">Реестр интеграций.</param>
    /// <param name="connection">Соединение, чей адрес не прошел.</param>
    /// <param name="refusal">Текст отказа шлюза — дословно, как его прочитал человек.</param>
    /// <param name="collections">Как зовется коллекция по ключу; не нашлась — пусто.</param>
    /// <param name="who">Кто просит.</param>
    /// <param name="machine">Имя машины.</param>
    public static AddressRequest Gather(IntegrationsRegistry  registry,
                                        IntegrationConnection connection,
                                        string                refusal,
                                        Func<int, int, string> collections,
                                        string                who,
                                        string                machine)
    {
        var uses = new List<string>();

        foreach (var request in registry.RequestsOf(connection.Id))
        {
            var line = new StringBuilder($"«{request.Name}» {request.Method.ToString().ToUpperInvariant()} {request.Path}");

            if (registry.Binding(request.Id) is { } binding)
            {
                var collection = collections(binding.DbId, binding.CollectionId);

                if (collection.Length > 0) line.Append($" → коллекция «{collection}»");

                var columns = binding.Columns.Select(c => c.Column).Where(c => c.Length > 0).ToList();

                if (columns.Count > 0) line.Append(", поля: " + string.Join(", ", columns));
            }
            else
            {
                line.Append(" (укладка в коллекцию еще не настроена)");
            }

            uses.Add(line.ToString());
        }

        return new AddressRequest(connection.Address, connection.Name, refusal, uses, who, machine);
    }

    /// <summary>
    /// Заявка текстом — то, что ложится в буфер. Письмо, а не отчет: его прочитает человек,
    /// который про эту интеграцию ничего не знает, и ему нужен адрес, причина и что пойдет.
    /// </summary>
    /// <param name="request">Собранная заявка.</param>
    /// <param name="policy">Действующая политика — что разрешено СЕЙЧАС. Ответственному это
    /// нужно: он правит файл и должен видеть, к чему дописывает.</param>
    /// <param name="today">Сегодняшняя дата; передается явно — тест на часах не строится.</param>
    public static string Text(AddressRequest request, EgressPolicySet policy, DateOnly today)
    {
        var text = new StringBuilder();

        text.AppendLine("Заявка на разрешение адреса в политике интеграций");
        text.AppendLine();
        text.AppendLine($"Кто просит: {request.Who}");
        text.AppendLine($"Машина:     {request.Machine}");
        text.AppendLine($"Дата:       {today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}");
        text.AppendLine();

        text.AppendLine("Прошу разрешить обращение к адресу:");
        text.AppendLine($"    {request.Address}");
        text.AppendLine($"    (соединение «{request.Connection}»)");
        text.AppendLine();

        text.AppendLine("Программа отказывает сейчас так:");
        text.AppendLine($"    {request.Refusal}");
        text.AppendLine();

        text.AppendLine("Через это соединение пойдет:");

        if (request.Uses.Count == 0)
        {
            // Запросов еще нет — обычное дело: соединение заводят первым. Молчать об этом нельзя,
            // иначе ответственный решит, что сведения потерялись.
            text.AppendLine("    запросы еще не заведены — соединение настраивают первым");
        }
        else
        {
            foreach (var use in request.Uses) text.AppendLine($"    • {use}");
        }

        text.AppendLine();
        text.AppendLine("Разрешено политикой сейчас:");
        text.AppendLine($"    режим      {Mode(policy.Mode)}");
        text.AppendLine($"    имена      {Utils.Sizes.List(Names(policy))}");
        text.AppendLine($"    сети       {Utils.Sizes.List(Networks(policy))}");
        text.AppendLine($"    источники  {Utils.Sizes.List([.. policy.Sources.Select(p => p.Source)])}");
        text.AppendLine();

        text.AppendLine("Значений полей и секретов в этой заявке нет — только имена.");

        return text.ToString();
    }

    /// <summary>
    /// Имена и сети собираются ПО ВСЕМ источникам вместе. Разрешено при этом пересечение, а не
    /// объединение, и заявка об этом не врет: она показывает, к чему дописывать, а не что
    /// действует, — и правит ответственный ровно один файл из этого списка.
    /// </summary>
    private static IReadOnlyList<string> Names(EgressPolicySet policy) =>
        [.. policy.Sources.SelectMany(p => p.Hosts).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <inheritdoc cref="Names"/>
    private static IReadOnlyList<string> Networks(EgressPolicySet policy) =>
        [.. policy.Sources.SelectMany(p => p.Networks).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static string Mode(EgressMode mode) => mode switch
    {
        EgressMode.Isolated => "изолированный (исходящих нет вовсе)",
        EgressMode.Internal => "внутренний (только адреса организации)",
        _                   => "внешний (поименные узлы из списка)",
    };
}
