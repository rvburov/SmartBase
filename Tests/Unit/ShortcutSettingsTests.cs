using Avalonia.Input;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Пользовательские привязки сочетаний: инвариант проекта «в app.db только отличия от
/// стандарта» (назначить стандартный жест = убрать ключ) плюс правило фичи «привязка
/// заменяет ВЕСЬ список стандартных жестов» — назначил свое, и стандартный псевдоним
/// (Backspace у удалений) гаснет.
/// </summary>
public sealed class ShortcutSettingsTests
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

    private static FakeStore Attach()
    {
        var store = new FakeStore();
        AppSettings.AttachStore(store);
        ShortcutSettings.Init();
        return store;
    }

    private static ShortcutGesture G(string text) =>
        ShortcutGesture.Parse(text) ?? throw new Xunit.Sdk.XunitException($"не разобрано: {text}");

    // ── Стандарт = отсутствие ключа ───────────────────────────────────────────

    [Fact]
    public void Nothing_saved_means_the_standard_gestures_work()
    {
        Attach();
        Assert.False(ShortcutSettings.IsCustomized("table.copy"));
        Assert.Null(ShortcutSettings.UserGesture("table.copy"));
        Assert.Equal("Ctrl+C", ShortcutSettings.PrimaryGesture("table.copy")?.Canonical);
        Assert.True(ShortcutSettings.Matches("table.copy", Key.C, KeyModifiers.Control));
        // Псевдоним стандарта тоже действует.
        Assert.True(ShortcutSettings.Matches("table.clearCells", Key.Back, KeyModifiers.None));
    }

    [Fact]
    public void A_custom_gesture_survives_a_round_trip_through_the_store()
    {
        var store = Attach();
        ShortcutSettings.SetGesture("table.copy", G("Ctrl+Shift+C"));

        Assert.True(store.Has("shortcut.table.copy"));
        Assert.Equal("Ctrl+Shift+C", store.Get("shortcut.table.copy"));

        ShortcutSettings.Init();   // как при следующем запуске
        Assert.True(ShortcutSettings.IsCustomized("table.copy"));
        Assert.Equal("Ctrl+Shift+C", ShortcutSettings.UserGesture("table.copy")?.Canonical);
    }

    [Fact]
    public void Assigning_the_standard_gesture_removes_the_key()
    {
        var store = Attach();
        ShortcutSettings.SetGesture("table.copy", G("Ctrl+Shift+C"));
        Assert.True(store.Has("shortcut.table.copy"));

        ShortcutSettings.SetGesture("table.copy", G("Ctrl+C"));
        Assert.False(store.Has("shortcut.table.copy"));
        Assert.False(ShortcutSettings.IsCustomized("table.copy"));
    }

    [Fact]
    public void Reset_removes_the_key_and_brings_the_standard_back()
    {
        var store = Attach();
        ShortcutSettings.SetGesture("notes.bold", G("Ctrl+Alt+B"));
        ShortcutSettings.Reset("notes.bold");

        Assert.False(store.Has("shortcut.notes.bold"));
        Assert.True(ShortcutSettings.Matches("notes.bold", Key.B, KeyModifiers.Control));
    }

    [Fact]
    public void Garbage_and_unknown_ids_in_the_store_read_as_the_standard()
    {
        var store = Attach();
        store.Set("shortcut.table.copy", "Ctrl+нечто");
        store.Set("shortcut.table.thereIsNoSuchAction", "Ctrl+J");
        store.Set("shortcut.table.ref.move", "Ctrl+J");     // справочная строка не переназначается
        ShortcutSettings.Init();

        Assert.False(ShortcutSettings.IsCustomized("table.copy"));
        Assert.True(ShortcutSettings.Matches("table.copy", Key.C, KeyModifiers.Control));
        Assert.False(ShortcutSettings.IsCustomized("table.ref.move"));
    }

    // ── Применение ────────────────────────────────────────────────────────────

    [Fact]
    public void A_custom_gesture_replaces_the_whole_standard_list()
    {
        Attach();
        ShortcutSettings.SetGesture("table.clearCells", G("Ctrl+Delete"));

        Assert.True(ShortcutSettings.Matches("table.clearCells", Key.Delete, KeyModifiers.Control));
        Assert.False(ShortcutSettings.Matches("table.clearCells", Key.Delete, KeyModifiers.None));
        Assert.False(ShortcutSettings.Matches("table.clearCells", Key.Back, KeyModifiers.None));
    }

    [Fact]
    public void Matching_stays_strict_about_extra_modifiers()
    {
        Attach();
        Assert.True(ShortcutSettings.Matches("table.copy", Key.C, KeyModifiers.Meta));
        Assert.False(ShortcutSettings.Matches("table.copy", Key.C, KeyModifiers.Control | KeyModifiers.Shift));
    }

    [Fact]
    public void Changed_fires_on_a_real_change_only()
    {
        Attach();
        var fired = 0;
        void Handler() => fired++;

        ShortcutSettings.Changed += Handler;
        try
        {
            ShortcutSettings.SetGesture("table.copy", G("Ctrl+Alt+C"));
            Assert.Equal(1, fired);

            ShortcutSettings.SetGesture("table.copy", G("Ctrl+Alt+C"));   // то же самое
            Assert.Equal(1, fired);

            ShortcutSettings.Reset("table.copy");
            Assert.Equal(2, fired);

            ShortcutSettings.Reset("table.copy");                          // сбрасывать нечего
            Assert.Equal(2, fired);
        }
        finally { ShortcutSettings.Changed -= Handler; }
    }

    // ── Конфликты ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_taken_gesture_inside_one_context_is_a_conflict()
    {
        Attach();
        Assert.Equal("table.paste", ShortcutSettings.ConflictOf(G("Ctrl+V"), "table.copy")?.Id);
        Assert.Null(ShortcutSettings.ConflictOf(G("Ctrl+Alt+J"), "table.copy"));
        // Свой же жест конфликтом не считается.
        Assert.Null(ShortcutSettings.ConflictOf(G("Ctrl+C"), "table.copy"));
    }

    [Fact]
    public void Different_local_contexts_may_share_a_gesture_but_global_may_not()
    {
        Attach();
        // Delete в боковой панели не мешает Delete в таблице.
        Assert.Null(ShortcutSettings.ConflictOf(G("Delete"), "calendar.deleteSelection"));
        // А глобальное действие сталкивается с любым блоком.
        Assert.Equal("notes.bold", ShortcutSettings.ConflictOf(G("Ctrl+B"), "app.quit")?.Id);
    }

    [Fact]
    public void A_conflict_is_computed_against_the_gestures_in_force()
    {
        Attach();
        ShortcutSettings.SetGesture("table.paste", G("Ctrl+Alt+V"));

        // Ctrl+V освободился, а Ctrl+Alt+V теперь занят.
        Assert.Null(ShortcutSettings.ConflictOf(G("Ctrl+V"), "table.copy"));
        Assert.Equal("table.paste", ShortcutSettings.ConflictOf(G("Ctrl+Alt+V"), "table.copy")?.Id);
    }

    [Fact]
    public void The_delete_trio_of_the_table_is_not_reported_as_a_conflict()
    {
        Attach();
        Assert.Null(ShortcutSettings.ConflictOf(G("Delete"), "table.clearCells"));
        Assert.Null(ShortcutSettings.ConflictOf(G("Delete"), "table.deleteColumns"));
    }

    [Fact]
    public void Clearing_and_deleting_show_different_keys_but_delete_still_works_for_both()
    {
        Attach();

        // В меню у «Очистить значения» и «Удалить» стоят РАЗНЫЕ клавиши — одна и та же
        // читалась как ошибка.
        Assert.Equal("Delete",      ShortcutSettings.PrimaryGesture("table.clearCells")?.Canonical);
        Assert.Equal("Ctrl+Delete", ShortcutSettings.PrimaryGesture("table.deleteRecords")?.Canonical);
        Assert.Equal("Alt+Delete",  ShortcutSettings.PrimaryGesture("table.deleteColumns")?.Canonical);

        // При этом привычное поведение цело: Delete удаляет то, что выделено.
        Assert.True(ShortcutSettings.Matches("table.deleteRecords", Key.Delete, KeyModifiers.None));
        Assert.True(ShortcutSettings.Matches("table.deleteColumns", Key.Delete, KeyModifiers.None));
        Assert.True(ShortcutSettings.Matches("table.deleteColumns", Key.Back, KeyModifiers.None));
    }

    // ── Уборка сирот ──────────────────────────────────────────────────────────

    [Fact]
    public void Orphan_bindings_are_removed_and_live_ones_are_kept()
    {
        var store = Attach();
        ShortcutSettings.SetGesture("table.copy", G("Ctrl+Alt+C"));
        store.Set("shortcut.table.actionFromAnOlderVersion", "Ctrl+J");

        var removed = AppSettings.RemoveOrphanShortcutSettings(
            ShortcutActions.All.Where(a => a.Rebindable).Select(a => a.Id));

        Assert.Equal(1, removed);
        Assert.False(store.Has("shortcut.table.actionFromAnOlderVersion"));
        Assert.True(store.Has("shortcut.table.copy"));
    }
}
