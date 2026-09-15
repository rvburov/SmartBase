using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Журнал изменений (change_log): мутации автоматически логируются, страница читается
/// keyset-обходом с фильтрами/сортировкой, счетчики группируют значения колонки,
/// очистка удаляет строки.
/// </summary>
public sealed class ChangeLogTests
{
    // Создает коллекцию + поле + запись → несколько строк журнала «create».
    private static void SeedActivity(ConstructorRepository repo)
    {
        var col = SampleData.AddCollection(repo, "Журналируемая");
        var f   = SampleData.AddField(repo, col.Id!.Value, "Текст");
        var r   = SampleData.AddRecord(repo, col.Id!.Value, "Запись");
        repo.SetFieldValue(r.Id!.Value, f.Id!.Value, "значение");
    }

    [Fact]
    public async Task Mutations_are_logged_and_page_reads_them()
    {
        using var db = new TempDatabase();
        SeedActivity(db.Constructor);

        var page = await db.Constructor.GetChangeLogPageAsync(new ConstructorRepository.ChangeLogQuery(), take: 100);

        Assert.NotEmpty(page);
        Assert.Contains(page, r => r.Action == "create" && r.TargetType == "collection");
        Assert.Contains(page, r => r.Action == "create" && r.TargetType == "record");
    }

    [Fact]
    public async Task Filter_by_action_narrows_page()
    {
        using var db = new TempDatabase();
        SeedActivity(db.Constructor);

        var query = new ConstructorRepository.ChangeLogQuery
        {
            Filters = new Dictionary<string, IReadOnlyCollection<string>> { ["action"] = ["create"] },
        };
        var page = await db.Constructor.GetChangeLogPageAsync(query, take: 100);

        Assert.NotEmpty(page);
        Assert.All(page, r => Assert.Equal("create", r.Action));
    }

    [Fact]
    public async Task Counts_group_by_column()
    {
        using var db = new TempDatabase();
        SeedActivity(db.Constructor);

        var counts = await db.Constructor.GetChangeLogCountsAsync("type");

        Assert.Contains(counts, c => c.Value == "collection" && c.Count >= 1);
        Assert.Contains(counts, c => c.Value == "record" && c.Count >= 1);
    }

    [Fact]
    public async Task File_field_change_logs_file_name_not_path()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Документы");
        var f    = SampleData.AddField(repo, col.Id!.Value, "Файл", "file");
        var r    = SampleData.AddRecord(repo, col.Id!.Value, "Запись");

        const string path = "/Users/roman/Downloads/Проценты_по_месяцам-2.xlsx";
        repo.SetFieldValue(r.Id!.Value, f.Id!.Value, path);

        var page = await repo.GetChangeLogPageAsync(new ConstructorRepository.ChangeLogQuery(), take: 100);
        var upd  = page.Single(x => x.Action == "update" && x.TargetType == "record");

        // В журнале — имя файла, а не абсолютный путь (как в ячейке и фильтре).
        Assert.Contains("Проценты_по_месяцам-2.xlsx", upd.Detail);
        Assert.DoesNotContain("/Users/roman/Downloads", upd.Detail);
    }

    [Fact]
    public async Task Purge_removes_all_entries()
    {
        using var db = new TempDatabase();
        SeedActivity(db.Constructor);

        var removed = db.Constructor.PurgeChangeLog(olderThanUtc: null); // весь журнал
        Assert.True(removed > 0);

        var page = await db.Constructor.GetChangeLogPageAsync(new ConstructorRepository.ChangeLogQuery(), take: 100);
        Assert.Empty(page);
    }

    // ── Окно фильтра и сортировка — как в таблице конструктора ─────────────────

    /// <summary>
    /// Каскад: список значений колонки считается по строкам, прошедшим фильтры ОСТАЛЬНЫХ колонок,
    /// а свой фильтр колонки его не режет — иначе снятое значение не вернуть в отбор.
    /// </summary>
    [Fact]
    public async Task Counts_follow_filters_of_other_columns_but_not_their_own()
    {
        using var db = new TempDatabase();
        SeedActivity(db.Constructor);
        var repo = db.Constructor;

        var all     = await repo.GetChangeLogPageAsync(new ConstructorRepository.ChangeLogQuery(), take: 1000);
        var updates = new Dictionary<string, IReadOnlyCollection<string>> { ["action"] = ["update"] };

        // Отобрали «Изменение» — в «Типе» только типы измененных объектов, со своими счетчиками.
        var types    = await repo.GetChangeLogCountsAsync("type", updates);
        var expected = all.Where(r => r.Action == "update")
                          .GroupBy(r => r.TargetType)
                          .ToDictionary(g => g.Key, g => g.Count());
        Assert.NotEmpty(expected);
        Assert.Equal(expected, types.ToDictionary(c => c.Value, c => c.Count));

        // «Действие» с фильтром по самому «Действию» — полный список, как без фильтра.
        Assert.Equal(await repo.GetChangeLogCountsAsync("action"),
                     await repo.GetChangeLogCountsAsync("action", updates));
    }

    /// <summary>
    /// «Действие» сортируется по подписи в ячейке, а не по коду: «Изменение» раньше «Создания»,
    /// хотя код «update» позже «create». Страницы по одной строке дают тот же порядок — курсор
    /// (<see cref="ConstructorRepository.ChangeLogSortValue"/>) совпадает с выражением сортировки.
    /// </summary>
    [Fact]
    public async Task Sort_by_action_follows_labels_and_pages_do_not_lose_rows()
    {
        using var db = new TempDatabase();
        SeedActivity(db.Constructor);
        var repo  = db.Constructor;
        var query = new ConstructorRepository.ChangeLogQuery { SortColumn = "action", SortAsc = true };

        var all    = await repo.GetChangeLogPageAsync(query, take: 1000);
        var labels = all.Select(r => SmartBase.Core.Models.ChangeLogLabels.ActionLabel(r.Action)).ToList();
        Assert.Equal(labels.OrderBy(l => l, StringComparer.Ordinal), labels);
        Assert.True(all.FindIndex(r => r.Action == "update") < all.FindIndex(r => r.Action == "create"));

        var paged = new List<ConstructorRepository.ChangeLogRow>();
        ConstructorRepository.ChangeLogRow? last = null;
        while (true)
        {
            var page = await repo.GetChangeLogPageAsync(query with
            {
                AfterId    = last?.Id,
                AfterValue = last is null ? null : ConstructorRepository.ChangeLogSortValue("action", last),
            }, take: 1);
            if (page.Count == 0) break;
            paged.AddRange(page);
            last = page[^1];
        }

        Assert.Equal(all.Select(r => r.Id), paged.Select(r => r.Id));
    }
}
