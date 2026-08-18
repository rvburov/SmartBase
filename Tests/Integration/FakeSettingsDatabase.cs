using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// In-memory реализация <see cref="ISettingsDatabase"/> для тестов дельт настроек:
/// хранит только явно записанные ключи, поэтому позволяет проверить инвариант
/// «в БД лежат лишь отличия от дефолта» (см. <c>AppSettings</c>). <see cref="Count"/> и
/// <see cref="Has"/> дают заглянуть в фактически сохранённые ключи.
/// </summary>
public sealed class FakeSettingsDatabase : ISettingsDatabase
{
    private readonly Dictionary<string, string> _store = new();

    public int Count => _store.Count;
    public bool Has(string key) => _store.ContainsKey(key);
    public IReadOnlyDictionary<string, string> Snapshot => _store;

    public string? Get(string key) => _store.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string value) => _store[key] = value;
    public void Remove(string key) => _store.Remove(key);

    public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
        _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
}
