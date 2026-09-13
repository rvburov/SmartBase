using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Стартовые значения UI-настроек коллекции (ui_collection_settings) и их отношения с разделом
/// «Настройки → Конструктор базы данных». Правило: у коллекции со своей записью побеждает ее
/// запись, у коллекции без записи действуют настройки раздела. Своей записи у коллекции нет,
/// пока режим не переключили вручную, — поэтому настройка «дотягивается» и до давно созданных
/// коллекций, и это задокументировано именно так.
/// </summary>
public sealed class ConstructorUiSettingsTests
{
    private sealed class FakeStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _d = new();
        public bool Has(string key) => _d.ContainsKey(key);
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _d[key] = value;
        public void Remove(string key) => _d.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            [.. _d.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))];
    }

    private static void AttachSettings()
    {
        AppSettings.AttachStore(new FakeStore());
        ConstructorSettings.Init();
    }

    [Fact]
    public void Collection_without_its_own_row_follows_the_section_settings()
    {
        AttachSettings();
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);

        // Стандарт раздела = прежнее поведение: ручные размеры выключены, вид — таблица.
        var initial = repo.GetUiSettings(col.Id!.Value);
        Assert.False(initial.ColResize);
        Assert.False(initial.RowResize);
        Assert.Equal("table", initial.ViewMode);

        ConstructorSettings.SetColResize(true);
        ConstructorSettings.SetRowResize(true);

        var afterSetting = repo.GetUiSettings(col.Id!.Value);
        Assert.True(afterSetting.ColResize);
        Assert.True(afterSetting.RowResize);
    }

    /// <summary>Переключили режим в меню коллекции — дальше живет ее собственное значение, и
    /// общий тумблер раздела эту коллекцию больше не двигает.</summary>
    [Fact]
    public void Own_row_wins_over_the_section_settings()
    {
        AttachSettings();
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);

        repo.SaveUiSettings(col.Id!.Value, colResize: false, rowResize: false, viewMode: "list");

        ConstructorSettings.SetColResize(true);
        ConstructorSettings.SetRowResize(true);

        var got = repo.GetUiSettings(col.Id!.Value);
        Assert.False(got.ColResize);
        Assert.False(got.RowResize);
        Assert.Equal("list", got.ViewMode);
    }
}
