using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Журнал изменений (change_log): мутации автоматически логируются, страница читается
/// keyset-обходом с фильтрами/сортировкой, счётчики группируют значения колонки,
/// очистка удаляет строки.
/// </summary>
public sealed class ChangeLogTests
{
    // Создаёт коллекцию + поле + запись → несколько строк журнала «create».
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
    public async Task Purge_removes_all_entries()
    {
        using var db = new TempDatabase();
        SeedActivity(db.Constructor);

        var removed = db.Constructor.PurgeChangeLog(olderThanUtc: null); // весь журнал
        Assert.True(removed > 0);

        var page = await db.Constructor.GetChangeLogPageAsync(new ConstructorRepository.ChangeLogQuery(), take: 100);
        Assert.Empty(page);
    }
}
