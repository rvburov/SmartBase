using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Память размера окон КЛАССА «данные» (<c>dialog.&lt;окно&gt;.size</c>,
/// docs/ROADMAP-API.md, шаг 14).
///
/// Проверяется не запись в словарь, а правила, на которые опирается окно: размер из чужого
/// app.db не открывает окно за краем экрана, вернувшийся к исходному ключ уходит из базы, а
/// мусор читается как «не запоминали» — окно при этом открывается своим размером из разметки.
/// </summary>
public sealed class DialogSizeMemoryTests
{
    private sealed class FakeStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public bool    Has(string key)    => _values.ContainsKey(key);
        public string? Get(string key)    => _values.TryGetValue(key, out var value) ? value : null;
        public void    Set(string key, string value) => _values[key] = value;
        public void    Remove(string key) => _values.Remove(key);

        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            [.. _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))];
    }

    private static FakeStore Store()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        return store;
    }

    [Fact]
    public void A_size_survives_save_and_load()
    {
        Store();

        AppSettings.SaveDialogSize("binding-in", 1200, 800);

        Assert.Equal((1200, 800), AppSettings.LoadDialogSize("binding-in"));
    }

    /// <summary>Окна помнят каждое свое: раскладку тянут шире, чем карточку запроса.</summary>
    [Fact]
    public void Windows_do_not_share_their_size()
    {
        Store();

        AppSettings.SaveDialogSize("binding-in", 1200, 800);
        AppSettings.SaveDialogSize("scenario",    900, 640);

        Assert.Equal((1200, 800), AppSettings.LoadDialogSize("binding-in"));
        Assert.Equal((900,  640), AppSettings.LoadDialogSize("scenario"));
    }

    [Fact]
    public void Nothing_remembered_reads_as_nothing()
    {
        Store();

        Assert.Null(AppSettings.LoadDialogSize("binding-in"));
    }

    /// <summary>
    /// Файл app.db переезжает между машинами: размер, снятый с четырехмониторной стены, не
    /// должен открывать окно за краем ноутбука. Годность проверяется при ЧТЕНИИ.
    /// </summary>
    [Theory]
    [InlineData("10x800")]
    [InlineData("1200x10")]
    [InlineData("99999x800")]
    [InlineData("1200x99999")]
    public void An_impossible_size_is_ignored(string stored)
    {
        var store = Store();
        store.Set("dialog.binding-in.size", stored);

        Assert.Null(AppSettings.LoadDialogSize("binding-in"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("широкое")]
    [InlineData("1200")]
    [InlineData("1200x")]
    [InlineData("1200x800x600")]
    public void Garbage_reads_as_nothing(string stored)
    {
        var store = Store();
        store.Set("dialog.binding-in.size", stored);

        Assert.Null(AppSettings.LoadDialogSize("binding-in"));
    }

    /// <summary>Невозможный размер и не записывается: в базе ему делать нечего.</summary>
    [Fact]
    public void An_impossible_size_is_not_written()
    {
        var store = Store();

        AppSettings.SaveDialogSize("binding-in", 10, 800);

        Assert.False(store.Has("dialog.binding-in.size"));
    }

    /// <summary>
    /// Вернулся к исходному — ключ уходит из базы. Правило настроек целиком: база хранит только
    /// дельты, сброс — это удаление ключа.
    /// </summary>
    [Fact]
    public void Forgetting_removes_the_key()
    {
        var store = Store();

        AppSettings.SaveDialogSize("binding-in", 1200, 800);
        AppSettings.ForgetDialogSize("binding-in");

        Assert.False(store.Has("dialog.binding-in.size"));
        Assert.Null(AppSettings.LoadDialogSize("binding-in"));
    }
}
