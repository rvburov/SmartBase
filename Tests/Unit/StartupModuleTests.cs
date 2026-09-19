using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Стартовый раздел навигации (<c>startupModule</c>): список значений закрытый, поэтому
/// модуль, добавленный в панель навигации, но забытый в <c>LoadStartupModule</c>, молча
/// не сохранялся бы — выбор в настройках откатывался бы к стандарту. Тест держит список
/// в согласии с модулями панели (MainWindow.ShowModule / ActivityBar.SetModuleActive).
/// </summary>
public sealed class StartupModuleTests
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

    // Модули панели навигации, которые можно открыть при запуске. Берутся из самого каталога,
    // а не переписываются списком: раньше здесь лежала копия, и добавленный модуль надо было
    // вспомнить и дописать в ДВУХ местах — иначе тест оставался зеленым, а выбор в настройках
    // молча откатывался к стандарту. «Настройки» и «Профиль» в каталог не входят вовсе: это
    // служебные разделы, стартовым ни один из них не выбирается.
    public static TheoryData<string> NavigationModules =>
        [.. SmartBase.Settings.ViewCatalog.Navigation.Select(v => v.Key)];

    [Theory]
    [MemberData(nameof(NavigationModules))]
    public void Every_Navigation_Module_Survives_Save_And_Load(string module)
    {
        AppSettings.AttachStore(new FakeStore());

        AppSettings.SaveStartupModule(module);

        Assert.Equal(module, AppSettings.LoadStartupModule());
    }

    [Fact]
    public void Unknown_Value_Falls_Back_To_Default()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        store.Set("startupModule", "модуль-которого-нет");

        Assert.Equal(AppSettings.StartupModuleDefault, AppSettings.LoadStartupModule());
    }

    [Fact]
    public void Default_Is_Stored_As_Absence_Of_Key()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        AppSettings.SaveStartupModule("calendar");

        AppSettings.SaveStartupModule(AppSettings.StartupModuleDefault);

        Assert.False(store.Has("startupModule"));
        Assert.Equal(AppSettings.StartupModuleDefault, AppSettings.LoadStartupModule());
    }

    [Fact]
    public void Nothing_Saved_Reads_As_Default()
    {
        AppSettings.AttachStore(new FakeStore());

        Assert.Equal(AppSettings.StartupModuleDefault, AppSettings.LoadStartupModule());
    }

    // ── Стартовый вид задач ───────────────────────────────────────────────────
    //
    // Список видов пока состоит из одного «list», но правило то же, что у конструктора:
    // стандарт хранится отсутствием ключа, незнакомое значение заменяется стандартом.

    [Fact]
    public void Tasks_View_Default_Is_The_Only_View()
    {
        AppSettings.AttachStore(new FakeStore());

        Assert.Equal("list", AppSettings.TasksViewDefault);
        Assert.Equal(AppSettings.TasksViewDefault, AppSettings.LoadTasksView());
    }

    [Fact]
    public void Tasks_View_Default_Is_Stored_As_Absence_Of_Key()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);

        AppSettings.SaveTasksView(AppSettings.TasksViewDefault);

        Assert.False(store.Has("tasksView"));
        Assert.Equal(AppSettings.TasksViewDefault, AppSettings.LoadTasksView());
    }

    [Fact]
    public void Unknown_Tasks_View_Falls_Back_To_Default()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        store.Set("tasksView", "вида-такого-нет");

        Assert.Equal(AppSettings.TasksViewDefault, AppSettings.LoadTasksView());
    }
}
