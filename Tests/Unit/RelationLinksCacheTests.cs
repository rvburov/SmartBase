using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Контракт табличного кеша связей (батч видимой страницы вместо N точечных GetLinks): Prime кладёт
/// и пустые наборы, TryGet различает «нет ребра» и «не прогрето», Invalidate/Clear сбрасывают.
/// </summary>
public sealed class RelationLinksCacheTests
{
    private static Dictionary<int, List<(int Id, string Title)>> Batch(params (int Rec, (int, string)[] Links)[] rows)
    {
        var d = new Dictionary<int, List<(int Id, string Title)>>();
        foreach (var (rec, links) in rows) d[rec] = links.ToList();
        return d;
    }

    [Fact]
    public void Prime_stores_links_and_empties_for_records_without_edges()
    {
        var cache = new RelationLinksCache();
        var batch = Batch((10, [(1, "A"), (2, "B")]));   // у записи 11 рёбер нет

        cache.Prime(fieldId: 5, recordIds: [10, 11], batch);

        Assert.True(cache.TryGet(5, 10, out var l10));
        Assert.Equal([(1, "A"), (2, "B")], l10);
        Assert.True(cache.TryGet(5, 11, out var l11));   // прогрето, но пусто (не будет перезапроса)
        Assert.Empty(l11);
    }

    [Fact]
    public void TryGet_misses_for_unprimed_record_or_field()
    {
        var cache = new RelationLinksCache();
        cache.Prime(5, [10], Batch((10, [(1, "A")])));

        Assert.False(cache.TryGet(5, 99, out _));   // запись не прогрета
        Assert.False(cache.TryGet(6, 10, out _));   // другое поле не прогрето
    }

    [Fact]
    public void Invalidate_and_clear_drop_entries()
    {
        var cache = new RelationLinksCache();
        cache.Prime(5, [10, 11], Batch((10, [(1, "A")]), (11, [(2, "B")])));

        cache.Invalidate(5, 10);
        Assert.False(cache.TryGet(5, 10, out _));   // сброшено точечно
        Assert.True(cache.TryGet(5, 11, out _));    // соседняя запись цела

        cache.Clear();
        Assert.False(cache.TryGet(5, 11, out _));   // всё сброшено
    }
}
