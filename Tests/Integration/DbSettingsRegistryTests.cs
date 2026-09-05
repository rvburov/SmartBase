using SmartBase.Database;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Методы реестра под окно «Настройки базы данных»: переименование записи
/// (<see cref="DatabaseRegistry.Rename"/>) и смена сохраненного пароля PG-подключения
/// (<see cref="DatabaseRegistry.UpdatePgPassword"/>). Временный app.db на диске —
/// как в IntroSeedTests; PG-сервер не нужен (проверяется только реестр).
/// </summary>
public sealed class DbSettingsRegistryTests
{
    private static (string Dir, string AppDb, string DataDb) TempPaths()
    {
        var dir = Directory.CreateTempSubdirectory("sbreg_").FullName;
        return (dir, Path.Combine(dir, "app.db"), Path.Combine(dir, "database.db"));
    }

    private static void Cleanup(string dir, string appDb, string dataDb)
    {
        TempDatabase.Cleanup(appDb);
        TempDatabase.Cleanup(dataDb);
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Rename_changes_name_and_survives_reopen()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            int id;
            using (var registry = DatabaseRegistry.Open(dataDb, appDb))
            {
                id = registry.Add("Старое имя", Path.Combine(dir, "extra.db")).Id;
                registry.Rename(id, "  Новое имя  ");   // пробелы по краям срезаются
                Assert.Equal("Новое имя", registry.GetAll().Single(d => d.Id == id).Name);

                registry.Rename(id, "   ");             // пустое имя игнорируется
                Assert.Equal("Новое имя", registry.GetAll().Single(d => d.Id == id).Name);
            }

            using (var again = DatabaseRegistry.Open(dataDb, appDb))
                Assert.Equal("Новое имя", again.GetAll().Single(d => d.Id == id).Name);
        }
        finally
        {
            Cleanup(dir, appDb, dataDb);
        }
    }

    [Fact]
    public void Rename_works_for_default_database_too()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var def = registry.GetAll().Single(d => d.IsDefault);
            registry.Rename(def.Id, "Моя основная");
            Assert.Equal("Моя основная", registry.GetAll().Single(d => d.IsDefault).Name);
        }
        finally
        {
            Cleanup(dir, appDb, dataDb);
        }
    }

    [Fact]
    public void UpdatePgPassword_replaces_secret_and_ignores_sqlite_rows()
    {
        var (dir, appDb, dataDb) = TempPaths();
        try
        {
            using var registry = DatabaseRegistry.Open(dataDb, appDb);
            var info = new PgConnectionInfo("db.local", 5432, "projects", "projects", "старый", "Prefer");
            var pg   = registry.AddPostgres("Сервер", info);

            registry.UpdatePgPassword(pg.Id, "новый-пароль");
            var updated = registry.GetAll().Single(d => d.Id == pg.Id);
            // Читаем той же единственной точкой, что и приложение: ToPgInfo расшифровывает.
            Assert.Equal("новый-пароль", updated.ToPgInfo()!.Password);

            // SQLite-запись метод не трогает (и не падает).
            var sq = registry.Add("Файловая", Path.Combine(dir, "extra.db"));
            registry.UpdatePgPassword(sq.Id, "не применится");
            Assert.Null(registry.GetAll().Single(d => d.Id == sq.Id).PgPassword);

            // Подчистка секрета из хранилища ОС (на macOS — элемент Связки ключей).
            SecretProtector.Delete(updated.PgPassword ?? "");
        }
        finally
        {
            Cleanup(dir, appDb, dataDb);
        }
    }
}
