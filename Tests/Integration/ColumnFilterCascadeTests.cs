using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Каскад счетчиков в окне фильтра колонки (<c>ConstructorRepository.GetColumnCounts</c>):
/// список значений колонки считается только по записям, прошедшим фильтры ОСТАЛЬНЫХ колонок
/// и не скрытым в «Отображении записей». Свой фильтр колонки список не режет — иначе снятое
/// значение нельзя вернуть в отбор. Счетчики и сам фильтр (<c>GetFilteredRecordIds</c>)
/// построены на одном запросе, поэтому «в списке одно, после применения другое» быть не должно.
/// </summary>
public sealed class ColumnFilterCascadeTests
{
    // Ключ системной колонки «Название» (см. ConstructorRepository / TableRowVm.TitleKey).
    private const string TitleKey = "__title__";

    // Коллекция «Заявки»: город × статус, пустые ячейки есть в обеих колонках.
    //   З1  Москва   Новая
    //   З2  Москва   Готово
    //   З3  Казань   Новая
    //   З4  Казань   (Пусто)
    //   З5  (Пусто)  Готово
    private static (int Col, int City, int Status, int[] Ids) Seed(ConstructorRepository repo)
    {
        var col    = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var city   = SampleData.AddField(repo, col, "Город",  position: 10).Id!.Value;
        var status = SampleData.AddField(repo, col, "Статус", position: 20).Id!.Value;
        var rows = new (string City, string Status)[]
        {
            ("Москва", "Новая"), ("Москва", "Готово"), ("Казань", "Новая"), ("Казань", ""), ("", "Готово"),
        };
        var ids = new int[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            ids[i] = SampleData.AddRecord(repo, col, $"З{i + 1}", (i + 1) * 10).Id!.Value;
            repo.SetFieldValue(ids[i], city,   rows[i].City);
            repo.SetFieldValue(ids[i], status, rows[i].Status);
        }
        return (col, city, status, ids);
    }

    private static Dictionary<string, HashSet<string>> F(params (int Field, string[] Values)[] filters) =>
        filters.ToDictionary(f => f.Field.ToString(), f => new HashSet<string>(f.Values));

    private static List<(string Value, int Count)> L(params (string Value, int Count)[] items) => [.. items];

    [Fact]
    public void Without_filters_counts_cover_whole_column()
    {
        using var db = new TempDatabase();
        var (col, _, status, _) = Seed(db.Constructor);

        // Порядок таблицы (по первой позиции), «(Пусто)» последним — как и раньше.
        Assert.Equal(L(("Новая", 2), ("Готово", 2), ("", 1)),
                     db.Constructor.GetColumnCounts(col, status.ToString()));
    }

    [Fact]
    public void Counts_follow_filters_of_other_columns()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var (col, city, status, _) = Seed(repo);

        // Отобрали Москву — в списке статусов только ее строки: по одной «Новая» и «Готово», пустых нет.
        var counts = repo.GetColumnCounts(col, status.ToString(), F((city, ["Москва"])));
        Assert.Equal(L(("Новая", 1), ("Готово", 1)), counts);

        // Список и таблица считаны одним запросом: сумма счетчиков = числу отобранных строк.
        Assert.Equal(repo.GetFilteredRecordIds(col, F((city, ["Москва"]))).Count, counts.Sum(c => c.Count));
    }

    [Fact]
    public void Own_filter_does_not_cut_own_list()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var (col, city, status, ids) = Seed(repo);
        var both = F((city, ["Москва"]), (status, ["Новая"]));

        // Фильтры на обеих колонках. Список статусов режет только фильтр города (свой — нет):
        // снятая «Готово» остается в списке, ее можно вернуть в отбор.
        Assert.Equal(L(("Новая", 1), ("Готово", 1)), repo.GetColumnCounts(col, status.ToString(), both));

        // И наоборот: список городов режет только фильтр статуса.
        Assert.Equal(L(("Москва", 1), ("Казань", 1)), repo.GetColumnCounts(col, city.ToString(), both));

        // Сама таблица при этом отобрана обоими фильтрами (И).
        Assert.Equal([ids[0]], repo.GetFilteredRecordIds(col, both));
    }

    [Fact]
    public void Empty_bucket_counts_only_rows_in_scope()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var (col, city, status, _) = Seed(repo);

        // «(Пусто)» — не «все записи минус непустые», а пустые СРЕДИ отобранных.
        Assert.Equal(L(("Новая", 1), ("", 1)), repo.GetColumnCounts(col, status.ToString(), F((city, ["Казань"]))));

        // Отбор по «(Пусто)» другой колонки — такой же срез.
        Assert.Equal(L(("Готово", 1)), repo.GetColumnCounts(col, status.ToString(), F((city, [""]))));
    }

    [Fact]
    public void Hidden_records_are_excluded_when_table_hides_them()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var (col, city, status, ids) = Seed(repo);
        repo.SaveRecordDisplay(col, [(ids[0], false, 1)]);   // З1 (Москва, Новая) скрыта

        // Таблица без скрытых — и список без них: «Новая» осталась одна (З3), а первой по позиции
        // теперь «Готово» (З2).
        Assert.Equal(L(("Готово", 2), ("Новая", 1), ("", 1)),
                     repo.GetColumnCounts(col, status.ToString(), excludeHidden: true));

        // Без флага (в таблице скрытых нет) считается все.
        Assert.Equal(L(("Новая", 2), ("Готово", 2), ("", 1)),
                     repo.GetColumnCounts(col, status.ToString(), excludeHidden: false));

        // Скрытие и фильтр другой колонки — вместе.
        Assert.Equal(L(("Готово", 1)),
                     repo.GetColumnCounts(col, status.ToString(), F((city, ["Москва"])), excludeHidden: true));
    }

    [Fact]
    public void Title_column_cascades_both_ways()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var (col, _, status, _) = Seed(repo);

        // Список названий по фильтру статуса.
        Assert.Equal(L(("З2", 1), ("З5", 1)), repo.GetColumnCounts(col, TitleKey, F((status, ["Готово"]))));

        // Список статусов по фильтру названий.
        var byTitle = new Dictionary<string, HashSet<string>> { [TitleKey] = ["З3", "З4"] };
        Assert.Equal(L(("Новая", 1), ("", 1)), repo.GetColumnCounts(col, status.ToString(), byTitle));
    }

    [Fact]
    public void Relation_column_counts_only_sources_in_scope()
    {
        using var db = new TempDatabase();
        var repo    = db.Constructor;
        var projCol = SampleData.AddCollection(repo, "Проекты").Id!.Value;
        var taskCol = SampleData.AddCollection(repo, "Задачи").Id!.Value;
        var rel     = SampleData.AddField(repo, taskCol, "Проект", "relation");
        rel.ConfigJson = new RelationConfig(projCol, false).ToConfigJson();
        repo.UpdateField(rel);
        var relId = rel.Id!.Value;
        var kind  = SampleData.AddField(repo, taskCol, "Вид", position: 20).Id!.Value;

        var a  = SampleData.AddRecord(repo, projCol, "Альфа").Id!.Value;
        var b  = SampleData.AddRecord(repo, projCol, "Бета").Id!.Value;
        var t1 = SampleData.AddRecord(repo, taskCol, "T1", 10).Id!.Value;
        var t2 = SampleData.AddRecord(repo, taskCol, "T2", 20).Id!.Value;
        var t3 = SampleData.AddRecord(repo, taskCol, "T3", 30).Id!.Value;   // без связей
        var t4 = SampleData.AddRecord(repo, taskCol, "T4", 40).Id!.Value;   // без связей
        repo.SetLinks(relId, t1, [a, b]);
        repo.SetLinks(relId, t2, [a]);
        repo.SetFieldValue(t1, kind, "Ошибка");
        repo.SetFieldValue(t2, kind, "Задача");
        repo.SetFieldValue(t3, kind, "Ошибка");
        repo.SetFieldValue(t4, kind, "Задача");

        // Только «Ошибка»: T1 (Альфа, Бета) и T3 (без связей). Без каскада «Альфа» было бы 2.
        var links = repo.GetColumnCounts(taskCol, relId.ToString(), F((kind, ["Ошибка"])));
        Assert.Equal(("Альфа", 1), links.Single(c => c.Value == "Альфа"));
        Assert.Equal(("Бета",  1), links.Single(c => c.Value == "Бета"));
        Assert.Equal(("",      1), links[^1]);   // «(нет связей)» — T3, последним

        // И обратно: список видов по фильтру связи «Альфа» — T1 и T2.
        var byLink = new Dictionary<string, HashSet<string>> { [relId.ToString()] = ["Альфа"] };
        Assert.Equal(L(("Ошибка", 1), ("Задача", 1)), repo.GetColumnCounts(taskCol, kind.ToString(), byLink));

        // «(нет связей)» в фильтре связи — T3 и T4.
        var noLinks = new Dictionary<string, HashSet<string>> { [relId.ToString()] = [""] };
        Assert.Equal(L(("Ошибка", 1), ("Задача", 1)), repo.GetColumnCounts(taskCol, kind.ToString(), noLinks));
    }

    [Fact]
    public async Task Background_counts_match_foreground()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var (col, city, status, ids) = Seed(repo);
        repo.SaveRecordDisplay(col, [(ids[4], false, 1)]);   // З5 скрыта

        // Окно фильтра считает в фоне (отдельное подключение) — тот же срез, что и в основном.
        var filters  = F((city, ["Москва", "Казань"]));
        var expected = repo.GetColumnCounts(col, status.ToString(), filters, excludeHidden: true);
        Assert.Equal(L(("Новая", 2), ("Готово", 1), ("", 1)), expected);
        Assert.Equal(expected, await repo.GetColumnCountsAsync(col, status.ToString(), filters, excludeHidden: true));

        // Свой фильтр колонки фон тоже отбрасывает.
        Assert.Equal(repo.GetColumnCounts(col, status.ToString()),
                     await repo.GetColumnCountsAsync(col, status.ToString(), F((status, ["Новая"]))));
    }

    /// <summary>
    /// Чекбокс в окне фильтра — две строки, «отмечено» и «не отмечено», как бы ни были записаны
    /// значения: снятая в таблице галочка («0»), нетронутая ячейка, текст от прежнего типа столбца.
    /// Раньше каждое такое значение было своей строкой «—» рядом с «(Пусто)». Отбор по группе
    /// находит все ее записи — и по прежним сохраненным фильтрам со значениями как есть.
    /// </summary>
    [Fact]
    public void Checkbox_column_filters_by_two_groups()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Проверки").Id!.Value;
        var box  = SampleData.AddField(repo, col, "Ошибки", "checkbox", position: 10).Id!.Value;
        var values = new[] { "1", "true", "0", "нет", "", "", "1" };
        var ids = values.Select((v, i) =>
        {
            var id = SampleData.AddRecord(repo, col, $"П{i + 1}", position: (i + 1) * 10).Id!.Value;
            if (v != "") repo.SetFieldValue(id, box, v);
            return id;
        }).ToArray();
        int[] on  = [ids[0], ids[1], ids[6]];
        int[] off = [ids[2], ids[3], ids[4], ids[5]];

        Assert.Equal(L(("1", 3), ("", 4)), repo.GetColumnCounts(col, box.ToString()));

        Assert.Equal(on,  repo.GetFilteredRecordIds(col, F((box, ["1"]))));
        Assert.Equal(off, repo.GetFilteredRecordIds(col, F((box, [""]))));
        Assert.Equal(off, repo.GetFilteredRecordIds(col, F((box, ["0"]))));     // прежний фильтр «—»
        Assert.Equal(ids, repo.GetFilteredRecordIds(col, F((box, ["1", ""]))));
        Assert.Empty(repo.GetFilteredRecordIds(col, new Dictionary<string, HashSet<string>> { [box.ToString()] = [] }));
        Assert.Equal(off, repo.GetFilteredRecordIds(col,
            new Dictionary<string, HashSet<string>> { [box.ToString()] = FilterExclusion.Exclude(["1"]) }));

        // Снятая галочка остается в отборе «не отмечено», а не выпадает из него.
        repo.SetFieldValue(ids[0], box, "0");
        Assert.Equal(L(("1", 2), ("", 5)), repo.GetColumnCounts(col, box.ToString()));
        Assert.Contains(ids[0], repo.GetFilteredRecordIds(col, F((box, [""]))));

        // Каскад: по записям, прошедшим фильтр другой колонки.
        var titles = new Dictionary<string, HashSet<string>> { [TitleKey] = ["П2", "П3"] };
        Assert.Equal(L(("1", 1), ("", 1)), repo.GetColumnCounts(col, box.ToString(), titles));
    }
}
