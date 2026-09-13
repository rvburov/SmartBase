using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Память фильтров и сортировки коллекций (настройка «Запоминать фильтры и сортировку»):
/// круг «сохранили — прочитали», раздельность коллекций и баз, вытеснение старых записей,
/// отказ помнить слишком большой отбор и устойчивость к мусору в хранилище.
/// </summary>
public sealed class ConstructorFilterMemoryTests
{
    private sealed class FakeStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _d = new();

        /// <summary>Сколько раз хранилище трогали на запись. В настоящем app.db каждое такое
        /// обращение — SaveChanges на UI-потоке, поэтому холостые записи важно ловить.</summary>
        public int Writes { get; private set; }

        public bool Has(string key) => _d.ContainsKey(key);
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) { Writes++; _d[key] = value; }
        public void Remove(string key) { if (_d.Remove(key)) Writes++; }
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            [.. _d.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))];
    }

    private static FakeStore Attach()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        return store;
    }

    private static Dictionary<string, HashSet<string>> Filter(string column, params string[] values) =>
        new() { [column] = [.. values] };

    [Fact]
    public void Saved_filters_and_sort_come_back()
    {
        Attach();

        ConstructorFilterMemory.Save("/data/base.db", 5,
                                     Filter("city", "Москва", "Тверь"), sortKey: "name", sortAsc: false);

        var got = ConstructorFilterMemory.Load("/data/base.db", 5);

        Assert.NotNull(got);
        Assert.Equal("name", got!.SortKey);
        Assert.False(got.SortAsc);
        Assert.Equal(["Москва", "Тверь"], got.Filters["city"].OrderBy(v => v, StringComparer.Ordinal));
    }

    /// <summary>Одна и та же коллекция в разных базах — разные записи: id коллекций совпадают
    /// сплошь и рядом, а отбор у них свой.</summary>
    [Fact]
    public void Collections_and_databases_do_not_mix()
    {
        Attach();

        ConstructorFilterMemory.Save("/a.db", 1, Filter("c", "A"), null, true);
        ConstructorFilterMemory.Save("/b.db", 1, Filter("c", "B"), null, true);
        ConstructorFilterMemory.Save("/a.db", 2, Filter("c", "C"), null, true);

        Assert.Equal(["A"], ConstructorFilterMemory.Load("/a.db", 1)!.Filters["c"]);
        Assert.Equal(["B"], ConstructorFilterMemory.Load("/b.db", 1)!.Filters["c"]);
        Assert.Equal(["C"], ConstructorFilterMemory.Load("/a.db", 2)!.Filters["c"]);
        Assert.Null(ConstructorFilterMemory.Load("/b.db", 2));
    }

    /// <summary>Снятый отбор стирает запись: иначе «снял все фильтры» не пережило бы перезапуск.</summary>
    [Fact]
    public void Clearing_the_filter_forgets_the_collection()
    {
        var store = Attach();

        ConstructorFilterMemory.Save("/a.db", 1, Filter("c", "A"), null, true);
        ConstructorFilterMemory.Save("/a.db", 1, new Dictionary<string, HashSet<string>>(), null, true);

        Assert.Null(ConstructorFilterMemory.Load("/a.db", 1));
        Assert.False(store.Has("constructorFilters"));   // последняя запись ушла — ключа нет
    }

    /// <summary>Коллекция без отбора не должна ничего писать: ApplyFilters зовется при каждом
    /// открытии таблицы, и такая запись била бы по app.db на ровном месте.</summary>
    [Fact]
    public void Nothing_to_remember_writes_nothing()
    {
        var store = Attach();

        ConstructorFilterMemory.Save("/a.db", 1, new Dictionary<string, HashSet<string>>(), null, true);

        Assert.False(store.Has("constructorFilters"));
    }

    /// <summary>Повторное сохранение того же отбора не трогает хранилище: Save зовется с каждой
    /// перечиткой строк, в том числе по тику опроса чужих правок, а запись в app.db —
    /// это SaveChanges на UI-потоке.</summary>
    [Fact]
    public void Saving_the_same_selection_twice_does_not_write()
    {
        var store = Attach();
        ConstructorFilterMemory.Save("/a.db", 1, Filter("c", "A", "B"), "name", false);
        var writesAfterFirst = store.Writes;

        // Тот же отбор, другой порядок значений в наборе — для нас это одно и то же.
        ConstructorFilterMemory.Save("/a.db", 1, Filter("c", "B", "A"), "name", false);
        Assert.Equal(writesAfterFirst, store.Writes);

        // А настоящая смена — пишет.
        ConstructorFilterMemory.Save("/a.db", 1, Filter("c", "A"), "name", false);
        Assert.True(store.Writes > writesAfterFirst);
        Assert.Equal(["A"], ConstructorFilterMemory.Load("/a.db", 1)!.Filters["c"]);
    }

    /// <summary>Направление сортировки без самой сортировки ничего не значит и записи не вызывает.</summary>
    [Fact]
    public void Sort_direction_without_sorting_is_not_a_change()
    {
        var store = Attach();
        ConstructorFilterMemory.Save("/a.db", 1, Filter("c", "A"), sortKey: null, sortAsc: true);
        var writes = store.Writes;

        ConstructorFilterMemory.Save("/a.db", 1, Filter("c", "A"), sortKey: null, sortAsc: false);

        Assert.Equal(writes, store.Writes);
    }

    /// <summary>Открытие коллекции без отбора не пишет вообще ничего: ApplyFilters зовется с
    /// каждой перечиткой строк, в том числе по тику опроса чужих правок.</summary>
    [Fact]
    public void Reopening_a_collection_without_changes_is_free()
    {
        var store = Attach();
        ConstructorFilterMemory.Save("/a.db", 1, Filter("c", "A"), null, true);
        ConstructorFilterMemory.Save("/a.db", 2, new Dictionary<string, HashSet<string>>(), null, true);
        var writes = store.Writes;

        for (var tick = 0; tick < 10; tick++)
        {
            ConstructorFilterMemory.Save("/a.db", 1, Filter("c", "A"), null, true);
            ConstructorFilterMemory.Save("/a.db", 2, new Dictionary<string, HashSet<string>>(), null, true);
        }

        Assert.Equal(writes, store.Writes);
    }

    [Fact]
    public void Oldest_entries_are_pushed_out()
    {
        Attach();

        for (var i = 1; i <= ConstructorFilterMemory.MaxEntries + 3; i++)
            ConstructorFilterMemory.Save("/a.db", i, Filter("c", $"v{i}"), null, true);

        Assert.Null(ConstructorFilterMemory.Load("/a.db", 1));
        Assert.Null(ConstructorFilterMemory.Load("/a.db", 3));
        Assert.NotNull(ConstructorFilterMemory.Load("/a.db", 4));
        Assert.NotNull(ConstructorFilterMemory.Load("/a.db", ConstructorFilterMemory.MaxEntries + 3));
    }

    /// <summary>Отбор в сотню тысяч значений не помним — но сортировку той же коллекции помним.</summary>
    [Fact]
    public void Huge_filters_are_not_remembered_but_sorting_is()
    {
        Attach();

        var huge = Enumerable.Range(0, ConstructorFilterMemory.MaxValuesPerColumn + 1)
                             .Select(i => i.ToString())
                             .ToHashSet();
        ConstructorFilterMemory.Save("/a.db", 1,
                                     new Dictionary<string, HashSet<string>> { ["big"] = huge },
                                     sortKey: "name", sortAsc: true);

        var got = ConstructorFilterMemory.Load("/a.db", 1);

        Assert.NotNull(got);
        Assert.Empty(got!.Filters);
        Assert.Equal("name", got.SortKey);
    }

    [Fact]
    public void Garbage_in_the_store_reads_as_nothing_remembered()
    {
        var store = Attach();
        store.Set("constructorFilters", "{не json");

        Assert.Null(ConstructorFilterMemory.Load("/a.db", 1));

        // И поверх мусора можно писать: он просто перетирается.
        ConstructorFilterMemory.Save("/a.db", 1, Filter("c", "A"), null, true);
        Assert.Equal(["A"], ConstructorFilterMemory.Load("/a.db", 1)!.Filters["c"]);
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        Attach();
        ConstructorFilterMemory.Save("/a.db", 1, Filter("c", "A"), null, true);
        ConstructorFilterMemory.Save("/a.db", 2, Filter("c", "B"), null, true);

        ConstructorFilterMemory.Clear();

        Assert.Null(ConstructorFilterMemory.Load("/a.db", 1));
        Assert.Null(ConstructorFilterMemory.Load("/a.db", 2));
    }
}
