using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Привязки сочетаний клавиш на настоящем app.db (не на подменном хранилище): назначенный
/// жест переживает перезапуск программы, сброс убирает строку из таблицы настроек, и в базе
/// лежат только отличия от стандарта. Временный app.db на диске — как в DbSettingsRegistryTests.
/// </summary>
public sealed class DbShortcutSettingsTests
{
    private static (string Dir, string AppDb, string DataDb) TempPaths()
    {
        var dir = Directory.CreateTempSubdirectory("sbkeys_").FullName;
        return (dir, Path.Combine(dir, "app.db"), Path.Combine(dir, "database.db"));
    }

    private static void Cleanup(string dir, string appDb, string dataDb)
    {
        TempDatabase.Cleanup(appDb);
        TempDatabase.Cleanup(dataDb);
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    private static ShortcutGesture G(string text) =>
        ShortcutGesture.Parse(text) ?? throw new Xunit.Sdk.XunitException($"не разобрано: {text}");

    [Fact]
    public void A_custom_gesture_survives_reopening_the_database()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using (var registry = DatabaseRegistry.Open(dataDb, appDb))
            {
                AppSettings.AttachStore(registry);
                ShortcutSettings.Init();
                ShortcutSettings.SetGesture("table.copy", G("Ctrl+Shift+C"));
            }

            using (var again = DatabaseRegistry.Open(dataDb, appDb))
            {
                AppSettings.AttachStore(again);
                ShortcutSettings.Init();                         // как при следующем запуске

                Assert.True(ShortcutSettings.IsCustomized("table.copy"));
                Assert.Equal("Ctrl+Shift+C", ShortcutSettings.UserGesture("table.copy")?.Canonical);
                // Нетронутое действие осталось на стандарте.
                Assert.False(ShortcutSettings.IsCustomized("table.paste"));
            }
        }
        finally
        {
            Cleanup(dir, appDb, dataDb);
        }
    }

    [Fact]
    public void Only_the_differences_from_the_standard_are_stored()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            AppSettings.AttachStore(registry);
            ShortcutSettings.Init();

            Assert.Empty(AppSettings.LoadShortcuts());

            ShortcutSettings.SetGesture("notes.bold", G("Ctrl+Alt+B"));
            var saved = AppSettings.LoadShortcuts();
            Assert.Equal(("notes.bold", "Ctrl+Alt+B"), Assert.Single(saved));

            // Назначение стандартного жеста — это сброс: строки в базе не остается.
            ShortcutSettings.SetGesture("notes.bold", G("Ctrl+B"));
            Assert.Empty(AppSettings.LoadShortcuts());

            ShortcutSettings.SetGesture("notes.bold", G("Ctrl+Alt+B"));
            ShortcutSettings.Reset("notes.bold");
            Assert.Empty(AppSettings.LoadShortcuts());
        }
        finally
        {
            Cleanup(dir, appDb, dataDb);
        }
    }

    [Fact]
    public void Orphan_bindings_are_swept_on_start()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            AppSettings.AttachStore(registry);
            ShortcutSettings.Init();

            ShortcutSettings.SetGesture("table.copy", G("Ctrl+Alt+C"));
            registry.Set("shortcut.table.actionFromAnOlderVersion", "Ctrl+J");

            var removed = AppSettings.RemoveOrphanShortcutSettings(
                ShortcutActions.All.Where(a => a.Rebindable).Select(a => a.Id));

            Assert.Equal(1, removed);
            Assert.Equal(("table.copy", "Ctrl+Alt+C"), Assert.Single(AppSettings.LoadShortcuts()));
        }
        finally
        {
            Cleanup(dir, appDb, dataDb);
        }
    }
}
