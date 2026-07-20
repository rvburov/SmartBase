using SmartBase.Core.Models;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Keyset-пагинация записей (row-value <c>(Position, Id) &gt; (курсор)</c>) — ключевой
/// перф-инвариант из docs/architecture/performance.md. Проверяем, что постраничный обход
/// покрывает все строки без дублей и пропусков, совпадает с offset-обходом, устойчив к
/// равным Position и к удалению строк в середине.
/// </summary>
public sealed class KeysetPaginationTests
{
    // Прогоняет keyset вперёд страницами по pageSize и собирает Id в порядке обхода.
    private static List<int> WalkKeyset(SmartBase.Database.Repositories.ConstructorRepository repo,
                                        int collectionId, int pageSize)
    {
        var ids = new List<int>();
        int? afterPos = null, afterId = null;
        while (true)
        {
            var page = repo.GetRecordsPageAfter(collectionId, afterPos, afterId, pageSize);
            if (page.Count == 0) break;
            ids.AddRange(page.Select(r => r.Id!.Value));
            var last = page[^1];
            afterPos = last.Position;
            afterId  = last.Id;
            if (page.Count < pageSize) break;
        }
        return ids;
    }

    private static List<int> AllByOffset(SmartBase.Database.Repositories.ConstructorRepository repo, int collectionId)
        => repo.GetRecordsPage(collectionId, 0, 100_000).Select(r => r.Id!.Value).ToList();

    [Fact]
    public void Keyset_walk_covers_all_rows_without_gaps_or_duplicates()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        SampleData.AddRecords(repo, col.Id!.Value, count: 25, step: 10);

        var expected = AllByOffset(repo, col.Id!.Value);
        var walked   = WalkKeyset(repo, col.Id!.Value, pageSize: 7);

        Assert.Equal(25, walked.Count);
        Assert.Equal(expected, walked);           // тот же порядок, что offset-обход
        Assert.Equal(walked.Count, walked.Distinct().Count()); // без дублей
    }

    [Fact]
    public void Keyset_first_page_starts_at_beginning()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var recs = SampleData.AddRecords(repo, col.Id!.Value, count: 10, step: 10);

        var firstPage = repo.GetRecordsPageAfter(col.Id!.Value, null, null, 3);
        Assert.Equal(3, firstPage.Count);
        Assert.Equal(recs[0].Id, firstPage[0].Id);
        Assert.Equal(recs[1].Id, firstPage[1].Id);
        Assert.Equal(recs[2].Id, firstPage[2].Id);
    }

    [Fact]
    public void Keyset_is_stable_when_positions_are_equal()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        // Все позиции равны → упорядочение решает Id (вторая часть row-value ключа).
        for (var i = 0; i < 15; i++)
            SampleData.AddRecord(repo, col.Id!.Value, $"E{i}", position: 100);

        var expected = AllByOffset(repo, col.Id!.Value);
        var walked   = WalkKeyset(repo, col.Id!.Value, pageSize: 4);

        Assert.Equal(expected, walked);
        Assert.Equal(walked.Count, walked.Distinct().Count());
    }

    [Fact]
    public void Keyset_stays_consistent_after_deleting_middle_row()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var recs = SampleData.AddRecords(repo, col.Id!.Value, count: 20, step: 10);

        repo.DeleteRecord(recs[9].Id!.Value); // удалить середину

        var expected = AllByOffset(repo, col.Id!.Value);
        var walked   = WalkKeyset(repo, col.Id!.Value, pageSize: 6);

        Assert.Equal(19, walked.Count);
        Assert.DoesNotContain(recs[9].Id!.Value, walked);
        Assert.Equal(expected, walked);
    }
}
