using SmartBase.Utils;

namespace SmartBase.Tests.Extensions.Registry;

/// <summary>Хранилище настроек в памяти — подставляется через <c>AppSettings.AttachStore</c>,
/// чтобы проверка уборки сирот не трогала настоящую app.db. Локальная копия обвязки ядра:
/// тесты расширений не зависят от тестов программы.</summary>
internal sealed class FakeSettingsStore : ISettingsDatabase
{
    private readonly Dictionary<string, string> _d = new();

    public int Count => _d.Count;
    public bool Has(string key) => _d.ContainsKey(key);

    public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string value) => _d[key] = value;
    public void Remove(string key) => _d.Remove(key);

    public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
        _d.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
}
