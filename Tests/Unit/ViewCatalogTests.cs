using SmartBase.Settings;
using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Каталог видов и настройка их видимости («Оформление → Отображение раздела …»).
/// Проверяется главное: скрытие пунктов не сдвигает глобальные номера видов (на них
/// завязаны сохраненный <c>calendarView</c> и <c>SwitchView</c>), пустым список видимых
/// не бывает, а хранятся именно СКРЫТЫЕ пункты — пустая настройка означает «видно все».
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

    // Только разделы со СТАТИЧЕСКИМ списком: у расширений он динамический (реестр), и в
    // сборке без расширений пуст — общие проверки «пунктов не бывает ноль» к нему неприменимы.
    // Его правила проверяются отдельно, ниже, и переживают любой состав реестра.
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

    // Порядок видов календаря — это и есть сохраненные номера (calendarView), менять его
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

    // Скрыть ВСЕ нельзя: раздел без единого вида неработоспособен, поэтому даже испорченная
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

    // ── Какой вид показать ────────────────────────────────────────────────────
    //
    // Resolve решает, что увидит человек на месте запрошенного вида: на старте, у коллекции со
    // своим сохраненным видом и после того, как открытый вид скрыли в настройках.

    [Fact]
    public void Resolve_keeps_a_visible_view()
    {
        Assert.Equal("day", ViewCatalog.Resolve(["schedule", "day", "month"], "day", "month"));
    }

    // Скрытый вид уступает место виду по умолчанию — туда же раздел открывается на старте, — а
    // не первому в списке.
    [Fact]
    public void Resolve_replaces_a_hidden_view_with_the_default()
    {
        Assert.Equal("month", ViewCatalog.Resolve(["schedule", "week", "month"], "day", "month"));
    }

    // Скрыто и умолчание (настройку правили руками) — первый видимый: пустым ответ не бывает.
    [Fact]
    public void Resolve_falls_back_to_the_first_visible_view()
    {
        Assert.Equal("schedule", ViewCatalog.Resolve(["schedule", "week"], "day", "month"));
        Assert.Equal("schedule", ViewCatalog.Resolve(["schedule", "week"], null, null));
    }

    [Fact]
    public void Resolve_without_views_gives_empty_key()
    {
        Assert.Equal("", ViewCatalog.Resolve([], "day", "month"));
    }

    // ── Расширения: динамический раздел ───────────────────────────────────────
    //
    // Пункты берутся из реестра, а не перечислены в каталоге. Поэтому проверки здесь НЕ
    // называют ни одного расширения и не рассчитывают на их число: удалили расширение (или
    // все сразу) — тесты обязаны остаться зелеными, иначе обещание «убрал папку, и больше
    // ничего править не нужно» не выполняется. Само зеркало «пункты = реестр» проверяется в
    // Tests/Extensions/Registry (ViewCatalogContractTests): тесты ядра не обращаются ни к
    // конкретным расширениям, ни к реестру, который их создает, — за этим следит сторож «Ядро не
    // знает про расширения» в CI.

    // Скрываем все, кроме первого: работает и на пустом реестре (скрывать нечего, видимых нет),
    // и на одном расширении, и на шести.
    [Fact]
    public void Hidden_extensions_disappear_from_the_visible_list()
    {
        AppSettings.AttachStore(new FakeStore());
        var all = ViewCatalog.All(ViewCatalog.ScopeExtensions);

        AppSettings.SaveHiddenViews(ViewCatalog.ScopeExtensions, all.Skip(1).Select(v => v.Key).ToList());

        Assert.Equal(all.Take(1).Select(v => v.Key),
                     ViewCatalog.Visible(ViewCatalog.ScopeExtensions).Select(v => v.Key));
    }

    // ── Уборка «скрытых» у динамического раздела ──────────────────────────────

    /// <summary>Id удаленного расширения не должен пережить его самого: иначе строка живет в
    /// app.db вечно, а вернувшееся расширение молча оказывается скрытым.</summary>
    [Fact]
    public void Prune_drops_keys_that_are_no_longer_offered()
    {
        AppSettings.AttachStore(new FakeStore());
        AppSettings.SaveHiddenViews(ViewCatalog.ScopeExtensions, ["живое", "удаленное"]);

        var removed = AppSettings.PruneHiddenViews(ViewCatalog.ScopeExtensions, ["живое", "другое"]);

        Assert.Equal(1, removed);
        Assert.Equal(["живое"], AppSettings.LoadHiddenViews(ViewCatalog.ScopeExtensions));
    }

    [Fact]
    public void Prune_removes_the_key_when_nothing_is_left()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        AppSettings.SaveHiddenViews(ViewCatalog.ScopeExtensions, ["удаленное"]);

        var removed = AppSettings.PruneHiddenViews(ViewCatalog.ScopeExtensions, ["живое"]);

        Assert.Equal(1, removed);
        Assert.False(store.Has("hiddenViews.extensions"));
    }

    // Уборка зовется на КАЖДОМ запуске — когда убирать нечего, она не должна трогать базу.
    [Fact]
    public void Prune_keeps_live_keys_untouched()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        AppSettings.SaveHiddenViews(ViewCatalog.ScopeExtensions, ["живое"]);
        var before = store.Get("hiddenViews.extensions");

        var removed = AppSettings.PruneHiddenViews(ViewCatalog.ScopeExtensions, ["живое", "другое"]);

        Assert.Equal(0, removed);
        Assert.Equal(before, store.Get("hiddenViews.extensions"));
    }

    [Fact]
    public void Prune_of_an_empty_setting_does_nothing()
    {
        AppSettings.AttachStore(new FakeStore());

        Assert.Equal(0, AppSettings.PruneHiddenViews(ViewCatalog.ScopeExtensions, ["живое"]));
    }
}
