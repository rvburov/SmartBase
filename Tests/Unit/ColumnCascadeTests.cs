using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Каскад фильтров служебных таблиц (<see cref="ColumnCascade"/>) — правило таблицы конструктора:
/// список значений колонки считается по строкам, прошедшим фильтры ОСТАЛЬНЫХ колонок, а свой
/// фильтр колонки его не режет. Иначе фильтр в одной колонке не отражался бы в списке другой, а
/// снятое значение нельзя было бы вернуть.
/// </summary>
public sealed class ColumnCascadeTests
{
    private sealed record Row(string User, string Action);

    // Журнал: два человека, у одной строки автор пустой — «(Пусто)» тоже значение.
    private static readonly Row[] Rows =
    [
        new("Иванов", "Создание"),
        new("Иванов", "Удаление"),
        new("Петров", "Создание"),
        new("Петров", "Изменение"),
        new("",       "Создание"),
    ];

    private static string ValueOf(string key, Row row) => key == "user" ? row.User : row.Action;

    private static Dictionary<string, HashSet<string>> F(params (string Key, string[] Values)[] filters) =>
        filters.ToDictionary(f => f.Key, f => new HashSet<string>(f.Values));

    private static Dictionary<string, int> Map(IEnumerable<(string Value, int Count)> counts) =>
        counts.ToDictionary(c => c.Value, c => c.Count);

    [Fact]
    public void Counts_follow_filters_of_other_columns()
    {
        // Отобрали Иванова — в «Действии» только его действия со своими счетчиками.
        var counts = ColumnCascade.Counts(Rows, "action", F(("user", ["Иванов"])), ValueOf);

        Assert.Equal(new Dictionary<string, int> { ["Создание"] = 1, ["Удаление"] = 1 }, Map(counts));
    }

    [Fact]
    public void Own_filter_does_not_shrink_its_list()
    {
        // Открыли снова «Пользователя» — список полный: Петрова можно вернуть в отбор.
        var counts = ColumnCascade.Counts(Rows, "user", F(("user", ["Иванов"])), ValueOf);

        Assert.Equal(new Dictionary<string, int> { ["Иванов"] = 2, ["Петров"] = 2, [""] = 1 }, Map(counts));
    }

    [Fact]
    public void Filters_of_different_columns_add_up()
    {
        var rows = ColumnCascade.Pass(Rows, F(("user", ["Иванов", "Петров"]), ("action", ["Создание"])), ValueOf);

        Assert.Equal([new Row("Иванов", "Создание"), new Row("Петров", "Создание")], rows.ToList());
    }

    [Fact]
    public void Snapshot_of_other_filters_is_a_copy_without_own_column()
    {
        var filters = F(("user", ["Иванов"]), ("action", ["Создание"]));

        var others = ColumnCascade.Others(filters, "user");

        Assert.Equal(["action"], others.Keys);
        // Фоновый запрос держит копию: правка фильтра таблицы после открытия окна его не трогает.
        filters["action"].Add("Удаление");
        Assert.Equal(["Создание"], others["action"]);
    }
}
