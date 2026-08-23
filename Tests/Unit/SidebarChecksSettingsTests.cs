using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Галочки коллекций боковой панели в app.db (<see cref="AppSettings.LoadHiddenCollections"/>).
///
/// Хранятся СНЯТЫЕ галочки: по умолчанию видно все, поэтому пустая настройка — это стандарт,
/// а новая коллекция появляется отмеченной сама. У календаря и задач наборы разные.
/// Инвариант дельт — как у остальных настроек ([[settings-architecture]]).
/// </summary>
public sealed class SidebarChecksSettingsTests
{
    private sealed class FakeStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _d = new();
        public bool Has(string key) => _d.ContainsKey(key);
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _d[key] = value;
        public void Remove(string key) => _d.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            _d.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    [Fact]
    public void Nothing_is_hidden_without_a_stored_value()
    {
        AppSettings.AttachStore(new FakeStore());
        Assert.Empty(AppSettings.LoadHiddenCollections("tasks"));
    }

    [Fact]
    public void Hidden_collections_round_trip()
    {
        AppSettings.AttachStore(new FakeStore());

        AppSettings.SaveHiddenCollections("tasks", [(1, 5), (2, 3)]);

        var back = AppSettings.LoadHiddenCollections("tasks");
        Assert.Equal(2, back.Count);
        Assert.Contains((1, 5), back);
        Assert.Contains((2, 3), back);
    }

    // Одинаковый id коллекции в разных базах — разные ключи (id между базами пересекаются).
    [Fact]
    public void Same_collection_id_in_another_database_is_another_key()
    {
        AppSettings.AttachStore(new FakeStore());

        AppSettings.SaveHiddenCollections("calendar", [(1, 7), (2, 7)]);

        Assert.Equal(2, AppSettings.LoadHiddenCollections("calendar").Count);
    }

    [Fact]
    public void Clearing_everything_removes_the_key()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);

        AppSettings.SaveHiddenCollections("tasks", [(1, 5)]);
        Assert.True(store.Has("hiddenCollections.tasks"));

        AppSettings.SaveHiddenCollections("tasks", []);        // назад к стандарту
        Assert.False(store.Has("hiddenCollections.tasks"));
        Assert.Empty(AppSettings.LoadHiddenCollections("tasks"));
    }

    // Календарь и задачи ведут галочки порознь: снятое в одном разделе не гасит коллекцию
    // в другом.
    [Fact]
    public void Calendar_and_tasks_keep_separate_sets()
    {
        AppSettings.AttachStore(new FakeStore());

        AppSettings.SaveHiddenCollections("calendar", [(1, 5)]);
        AppSettings.SaveHiddenCollections("tasks", [(1, 9)]);

        Assert.Contains((1, 5), AppSettings.LoadHiddenCollections("calendar"));
        Assert.DoesNotContain((1, 5), AppSettings.LoadHiddenCollections("tasks"));
    }

    // Один и тот же набор обязан писаться одной строкой: множество неупорядочено, и без
    // сортировки каждое сохранение выглядело бы новым значением.
    [Fact]
    public void Storage_string_does_not_depend_on_insertion_order()
    {
        var first = new FakeStore();
        AppSettings.AttachStore(first);
        AppSettings.SaveHiddenCollections("tasks", [(2, 3), (1, 5)]);

        var second = new FakeStore();
        AppSettings.AttachStore(second);
        AppSettings.SaveHiddenCollections("tasks", [(1, 5), (2, 3)]);

        Assert.Equal(first.Get("hiddenCollections.tasks"), second.Get("hiddenCollections.tasks"));
    }

    // Испорченный app.db не должен ронять старт: мусор пропускается, целые пары читаются.
    // Худшее последствие — лишняя отмеченная коллекция, а не отказ запуска.
    [Fact]
    public void Broken_values_are_skipped_and_valid_pairs_survive()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        store.Set("hiddenCollections.tasks", "abc;1;1:2:3;;4:7;x:y");

        var back = AppSettings.LoadHiddenCollections("tasks");

        Assert.Equal([(4, 7)], back);
    }
}
