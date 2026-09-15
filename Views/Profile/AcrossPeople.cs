namespace SmartBase.Views.Profile;

/// <summary>
/// Кто попадает в сводный вид «Все базы данных» панели пользователей и где у него права.
///
/// <b>Люди — только те, кто есть в базе, выбранной в панели администратора.</b> Сводный вид
/// отвечает на вопрос «какие права у людей ЭТОЙ базы во всех моих базах», а не «кто вообще бывал
/// хоть в одной из них». Выбрали базу, куда человек ни разу не подключался, — его нет ни в таблице,
/// ни в списке фильтра: иначе под выбранной базой показывались бы галки из чужой.
///
/// <b>Места человека</b> — открытые базы, где вы администратор и где он есть. В базах, где его
/// нет, прав у него нет и в расхождение «по-разному» они не идут.
///
/// Чистая функция, а не кусок панели: на живом окне состав строк не проверить, а тут проверено
/// (Tests/Unit/AcrossPeopleTests.cs).
/// </summary>
public static class AcrossPeople
{
    /// <summary>Человек в одной базе: ключ, имя в ней, последний заход и его место.</summary>
    public sealed record Entry(string UserKey, string DisplayName, string LastSeenAt, AdminUserPlace Place);

    /// <summary>Строка сводного вида: ключ, самое свежее имя и все места.</summary>
    public sealed record Person(string UserKey, string DisplayName, IReadOnlyList<AdminUserPlace> Places);

    /// <summary>
    /// Свести места по людям. <paramref name="members"/> — ключи пользователей выбранной базы;
    /// остальные записи отбрасываются. Люди сводятся по ключу «учетная запись@компьютер»: он один
    /// на машину, а номер пользователя у каждой базы свой.
    /// </summary>
    public static List<Person> Collect(IReadOnlySet<string> members, IEnumerable<Entry> entries)
    {
        var places = new Dictionary<string, List<AdminUserPlace>>(StringComparer.Ordinal);
        var names  = new Dictionary<string, (string Name, string SeenAt)>(StringComparer.Ordinal);
        var order  = new List<string>();

        foreach (var entry in entries)
        {
            if (!members.Contains(entry.UserKey)) continue;

            if (!places.TryGetValue(entry.UserKey, out var list))
            {
                places[entry.UserKey] = list = [];
                order.Add(entry.UserKey);
            }
            list.Add(entry.Place);

            // Имя берем из базы, куда человек заходил последним: он мог поправить профиль, а в
            // давнюю базу новое имя еще не заезжало.
            if (!names.TryGetValue(entry.UserKey, out var known)
                || string.CompareOrdinal(entry.LastSeenAt, known.SeenAt) > 0)
                names[entry.UserKey] = (entry.DisplayName, entry.LastSeenAt);
        }

        return [.. order.Select(key => new Person(key, names[key].Name, places[key]))];
    }
}
