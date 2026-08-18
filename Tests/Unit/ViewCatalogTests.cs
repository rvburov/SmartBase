using SmartBase.Settings;
using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Каталог видов и настройка их видимости («Оформление → Отображение раздела …»).
/// Проверяется главное: скрытие пунктов не сдвигает глобальные номера видов (на них
/// завязаны сохранённый <c>calendarView</c> и <c>SwitchView</c>), пустым список видимых
/// не бывает, а хранятся именно СКРЫТЫЕ пункты — пустая настройка означает «видно всё».
/// </summary>
public sealed class ViewCatalogTests
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

    private static readonly string[] Scopes =
        [ViewCatalog.ScopeNavigation, ViewCatalog.ScopeCalendar,
         ViewCatalog.ScopeTasks, ViewCatalog.ScopeConstructor];

    // ── Каталог ───────────────────────────────────────────────────────────────

    [Fact]
    public void Every_scope_has_items_with_unique_keys()
    {
        foreach (var scope in Scopes)
        {
            var all = ViewCatalog.All(scope);
            Assert.NotEmpty(all);

            var keys = all.Select(v => v.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
            Assert.All(all, v => Assert.False(string.IsNullOrWhiteSpace(v.Label)));
        }
    }

    [Fact]
    public void Unknown_scope_is_empty()
    {
        Assert.Empty(ViewCatalog.All("такого-раздела-нет"));
    }

    // Порядок видов календаря — это и есть сохранённые номера (calendarView), менять его
    // нельзя: 4 обязан оставаться «Месяцем».
    [Fact]
    public void Calendar_view_order_matches_stored_indexes()
    {
        Assert.Equal(7, ViewCatalog.Calendar.Count);
        Assert.Equal("month", ViewCatalog.KeyAt(ViewCatalog.ScopeCalendar, 4));
        Assert.Equal(4, ViewCatalog.IndexOf(ViewCatalog.ScopeCalendar, "month"));
    }

    [Fact]
    public void Navigation_never_offers_to_hide_settings()
    {
        var keys = ViewCatalog.Navigation.Select(v => v.Key).ToList();

        Assert.DoesNotContain("settings", keys);
        Assert.DoesNotContain("profile", keys);
    }

    [Fact]
    public void KeyAt_outside_range_gives_first_item()
    {
        Assert.Equal("schedule", ViewCatalog.KeyAt(ViewCatalog.ScopeCalendar, -1));
        Assert.Equal("schedule", ViewCatalog.KeyAt(ViewCatalog.ScopeCalendar, 99));
    }

    // ── Видимость ─────────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_hidden_by_default()
    {
        AppSettings.AttachStore(new FakeStore());

        foreach (var scope in Scopes)
            Assert.Equal(ViewCatalog.All(scope).Count, ViewCatalog.Visible(scope).Count);
    }

    [Fact]
    public void Hidden_items_disappear_but_indexes_stay()
    {
        AppSettings.AttachStore(new FakeStore());
        AppSettings.SaveHiddenViews(ViewCatalog.ScopeCalendar, ["schedule", "journal"]);

        var visible = ViewCatalog.Visible(ViewCatalog.ScopeCalendar);

        Assert.Equal(5, visible.Count);
        Assert.Equal("day", visible[0].Key);
        Assert.False(ViewCatalog.IsVisible(ViewCatalog.ScopeCalendar, "schedule"));
        // Номер «Месяца» не сдвинулся, хотя два вида перед ним скрыты.
        Assert.Equal(4, ViewCatalog.IndexOf(ViewCatalog.ScopeCalendar, "month"));
    }

    [Fact]
    public void Empty_hidden_set_removes_the_key()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        AppSettings.SaveHiddenViews(ViewCatalog.ScopeTasks, ["list"]);

        AppSettings.SaveHiddenViews(ViewCatalog.ScopeTasks, []);

        Assert.False(store.Has("hiddenViews.tasks"));
        Assert.Empty(AppSettings.LoadHiddenViews(ViewCatalog.ScopeTasks));
    }

    [Fact]
    public void Hidden_set_survives_save_and_load()
    {
        AppSettings.AttachStore(new FakeStore());

        AppSettings.SaveHiddenViews(ViewCatalog.ScopeNavigation, ["extensions", "tasks"]);

        Assert.Equal(["extensions", "tasks"],
                     AppSettings.LoadHiddenViews(ViewCatalog.ScopeNavigation).OrderBy(k => k, StringComparer.Ordinal));
    }

    // Скрыть ВСЁ нельзя: раздел без единого вида неработоспособен, поэтому даже испорченная
    // настройка оставляет первый пункт.
    [Fact]
    public void Hiding_everything_still_leaves_one_view()
    {
        AppSettings.AttachStore(new FakeStore());
        AppSettings.SaveHiddenViews(ViewCatalog.ScopeConstructor, ["table", "list"]);

        var visible = ViewCatalog.Visible(ViewCatalog.ScopeConstructor);

        Assert.Single(visible);
        Assert.Equal("table", visible[0].Key);
        Assert.Equal("table", ViewCatalog.FirstVisible(ViewCatalog.ScopeConstructor)?.Key);
    }

    // Скрытые сохраняются отсортированными: один и тот же набор не должен писаться в базу
    // каждый раз новой строкой.
    [Fact]
    public void Hidden_set_is_stored_in_stable_order()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);

        AppSettings.SaveHiddenViews(ViewCatalog.ScopeCalendar, ["year", "day", "journal"]);
        var first = store.Get("hiddenViews.calendar");

        AppSettings.SaveHiddenViews(ViewCatalog.ScopeCalendar, ["journal", "year", "day"]);

        Assert.Equal(first, store.Get("hiddenViews.calendar"));
    }

    // Настройки одного раздела не задевают другой — ключ включает scope.
    [Fact]
    public void Scopes_do_not_collide()
    {
        AppSettings.AttachStore(new FakeStore());

        AppSettings.SaveHiddenViews(ViewCatalog.ScopeCalendar, ["day"]);

        Assert.Empty(AppSettings.LoadHiddenViews(ViewCatalog.ScopeConstructor));
        Assert.Equal(ViewCatalog.Constructor.Count, ViewCatalog.Visible(ViewCatalog.ScopeConstructor).Count);
    }
}
