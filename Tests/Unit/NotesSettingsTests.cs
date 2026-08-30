using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Настройки раздела «Заметки». Правило хранилища то же, что у остальных: app.db держит только
/// отличия от стандарта, поэтому «сброс» — это отсутствие ключа, а незнакомое значение молча
/// заменяется стандартом, а не роняет раздел.
/// </summary>
public sealed class NotesSettingsTests
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

    private static FakeStore Attach()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        return store;
    }

    // ── Папка хранилища ─────────────────────────────────────────────────────────

    [Fact]
    public void Vault_Is_Empty_Until_Chosen()
    {
        Attach();
        Assert.Equal("", AppSettings.LoadNotesVault());
    }

    [Fact]
    public void Vault_Survives_Save_And_Load()
    {
        Attach();

        AppSettings.SaveNotesVault("/Users/тест/Заметки");

        Assert.Equal("/Users/тест/Заметки", AppSettings.LoadNotesVault());
    }

    [Fact]
    public void Empty_Vault_Is_Stored_As_Absence_Of_Key()
    {
        var store = Attach();
        AppSettings.SaveNotesVault("/Users/тест/Заметки");

        AppSettings.SaveNotesVault("");

        Assert.False(store.Has("notesVault"));
        Assert.Equal("", AppSettings.LoadNotesVault());
    }

    [Fact]
    public void Vault_Path_Is_Trimmed()
    {
        Attach();

        AppSettings.SaveNotesVault("  /Users/тест/Заметки  ");

        Assert.Equal("/Users/тест/Заметки", AppSettings.LoadNotesVault());
    }

    // ── Последняя открытая заметка ──────────────────────────────────────────────

    [Fact]
    public void Changing_The_Vault_Forgets_The_Last_Note()
    {
        // Путь относительный: в другой папке он означал бы другой файл или пустоту.
        var store = Attach();
        AppSettings.SaveNotesVault("/старое");
        AppSettings.SaveNotesLastFile("Работа/Итог.md");

        AppSettings.SaveNotesVault("/новое");

        Assert.False(store.Has("notesLastFile"));
        Assert.Equal("", AppSettings.LoadNotesLastFile());
    }

    [Fact]
    public void Saving_The_Same_Vault_Keeps_The_Last_Note()
    {
        Attach();
        AppSettings.SaveNotesVault("/хранилище");
        AppSettings.SaveNotesLastFile("Работа/Итог.md");

        AppSettings.SaveNotesVault("/хранилище");

        Assert.Equal("Работа/Итог.md", AppSettings.LoadNotesLastFile());
    }

    // ── Режим показа ────────────────────────────────────────────────────────────

    [Fact]
    public void View_Mode_Default_Is_Split()
    {
        Attach();
        Assert.Equal("split", AppSettings.NotesViewModeDefault);
        Assert.Equal("split", AppSettings.LoadNotesViewMode());
    }

    [Theory]
    [InlineData("source")]
    [InlineData("preview")]
    public void View_Mode_Survives_Save_And_Load(string mode)
    {
        Attach();

        AppSettings.SaveNotesViewMode(mode);

        Assert.Equal(mode, AppSettings.LoadNotesViewMode());
    }

    [Fact]
    public void View_Mode_Default_Is_Stored_As_Absence_Of_Key()
    {
        var store = Attach();
        AppSettings.SaveNotesViewMode("preview");

        AppSettings.SaveNotesViewMode(AppSettings.NotesViewModeDefault);

        Assert.False(store.Has("notesViewMode"));
    }

    [Fact]
    public void Unknown_View_Mode_Falls_Back_To_Default()
    {
        var store = Attach();
        store.Set("notesViewMode", "режима-такого-нет");

        Assert.Equal(AppSettings.NotesViewModeDefault, AppSettings.LoadNotesViewMode());
    }

    // ── Сортировка ──────────────────────────────────────────────────────────────

    [Fact]
    public void Sort_Has_Only_Two_Positions()
    {
        // У файлов нет собственной позиции, поэтому «без сортировки» не бывает.
        var store = Attach();

        AppSettings.SaveNotesSort("desc");
        Assert.Equal("desc", AppSettings.LoadNotesSort());

        store.Set("notesSort", "none");
        Assert.Equal("asc", AppSettings.LoadNotesSort());
    }

    [Fact]
    public void Sort_Default_Is_Stored_As_Absence_Of_Key()
    {
        var store = Attach();
        AppSettings.SaveNotesSort("desc");

        AppSettings.SaveNotesSort(AppSettings.NotesSortDefault);

        Assert.False(store.Has("notesSort"));
        Assert.Equal("asc", AppSettings.LoadNotesSort());
    }
}
