using SmartBase.Views.Profile;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Кто попадает в сводный вид «Все базы данных» (<see cref="AcrossPeople"/>): люди базы,
/// выбранной в панели администратора, с местами во всех админских базах. Человек, который к
/// выбранной базе никогда не подключался, не должен появляться ни в таблице, ни в фильтре —
/// иначе под выбранной базой показывались бы его права из другой.
/// </summary>
public sealed class AcrossPeopleTests
{
    private static AcrossPeople.Entry E(string key, string name, int dbId, string seen) =>
        new(key, name, seen, new AdminUserPlace(dbId, $"База {dbId}", UserId: dbId * 10, Mask: 0,
                                                IsAdmin: false, IsApproved: true,
                                                CreatedAt: seen, LastSeenAt: seen));

    [Fact]
    public void People_absent_from_selected_database_are_left_out()
    {
        // Выбрана база 1: в ней только Иванов. Петров есть лишь в базе 2 — его в сводном виде нет.
        var people = AcrossPeople.Collect(new HashSet<string> { "ivanov@pc" },
        [
            E("ivanov@pc", "Иванов", 1, "2026-09-01T00:00:00Z"),
            E("ivanov@pc", "Иванов", 2, "2026-09-01T00:00:00Z"),
            E("petrov@pc", "Петров", 2, "2026-09-01T00:00:00Z"),
        ]);

        var person = Assert.Single(people);
        Assert.Equal("ivanov@pc", person.UserKey);
        // Права Иванова — во всех базах, где он есть, а не только в выбранной.
        Assert.Equal([1, 2], person.Places.Select(p => p.DbId));
    }

    [Fact]
    public void Freshest_name_wins()
    {
        // Человек сменил имя: в базе, куда он заходил последним, имя новое.
        var people = AcrossPeople.Collect(new HashSet<string> { "ivanov@pc" },
        [
            E("ivanov@pc", "Иванов И. И.", 2, "2026-09-10T00:00:00Z"),
            E("ivanov@pc", "Иванов",       1, "2026-01-10T00:00:00Z"),
        ]);

        Assert.Equal("Иванов И. И.", Assert.Single(people).DisplayName);
    }

    [Fact]
    public void Selected_database_without_users_gives_empty_table()
    {
        var people = AcrossPeople.Collect(new HashSet<string>(),
        [
            E("petrov@pc", "Петров", 2, "2026-09-01T00:00:00Z"),
        ]);

        Assert.Empty(people);
    }
}
