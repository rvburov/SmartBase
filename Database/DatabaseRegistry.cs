using Microsoft.EntityFrameworkCore;
using DataBase.Database.Entities;
using DataBase.Utils;

namespace DataBase.Database;

/// <summary>
/// Реестр пользовательских БД поверх системной app.db. Гарантирует наличие единственной
/// дефолтной БД (текущий database.db), которую нельзя удалить. Здесь же — хранилище
/// настроек приложения (<see cref="ISettingsDatabase"/>, таблица app_settings): контекст
/// app.db живёт всё время работы приложения, поэтому настройки читаются/пишутся через него.
/// </summary>
public sealed class DatabaseRegistry : IDisposable, ISettingsDatabase
{
    private readonly SystemDbContext _db;

    private DatabaseRegistry(SystemDbContext db) => _db = db;

    /// <summary>Открыть реестр и гарантировать дефолтную запись на путь данных по умолчанию.</summary>
    public static DatabaseRegistry Open(string defaultDataPath)
    {
        var db = new SystemDbContext(AppSettings.SystemDbPath);
        db.Database.EnsureCreated();   // создаёт app.db и таблицы, если их нет; существующие данные не трогает
        EnsureSettingsTable(db);       // app_settings могло не быть в старых app.db (EnsureCreated её не достроит)
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");  // WAL: параллельные чтения во время записи
        var registry = new DatabaseRegistry(db);
        registry.EnsureDefault(defaultDataPath);
        return registry;
    }

    // EnsureCreated() не изменяет уже существующую БД, поэтому в app.db, созданных до
    // появления настроек, таблицы app_settings нет — создаём её аддитивно при открытии.
    private static void EnsureSettingsTable(SystemDbContext db) =>
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"app_settings\" (" +
            "\"Key\" TEXT NOT NULL CONSTRAINT \"PK_app_settings\" PRIMARY KEY, " +
            "\"Value\" TEXT NOT NULL);");

    // ── Настройки приложения (ISettingsDatabase поверх app.db) ──────────────────

    /// <summary>Значение настройки по ключу или null, если ключа нет.</summary>
    public string? Get(string key) =>
        _db.Settings.AsNoTracking().FirstOrDefault(s => s.Key == key)?.Value;

    /// <summary>Записать/обновить значение настройки по ключу.</summary>
    public void Set(string key, string value)
    {
        var row = _db.Settings.FirstOrDefault(s => s.Key == key);
        if (row is null) _db.Settings.Add(new AppSettingEntity { Key = key, Value = value });
        else             row.Value = value;
        _db.SaveChanges();
    }

    /// <summary>Удалить настройку — значение возвращается к дефолту в коде. Нет строки — no-op.</summary>
    public void Remove(string key)
    {
        var row = _db.Settings.FirstOrDefault(s => s.Key == key);
        if (row is null) return;
        _db.Settings.Remove(row);
        _db.SaveChanges();
    }

    // Если дефолтной БД ещё нет — регистрируем текущий database.db как основную.
    private void EnsureDefault(string defaultDataPath)
    {
        if (_db.Databases.Any(d => d.IsDefault)) return;

        _db.Databases.Add(new DatabaseRefEntity
        {
            Name      = "Основная",
            Path      = System.IO.Path.GetFullPath(defaultDataPath),
            Provider  = "sqlite",
            IsDefault = true,
        });
        _db.SaveChanges();
    }

    /// <summary>Все БД: дефолтная первой, затем по порядку добавления.</summary>
    public List<DatabaseRefEntity> GetAll() =>
        _db.Databases.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Id).ToList();

    /// <summary>
    /// Зарегистрировать SQLite-БД по пути. Если такой путь уже есть — возвращаем
    /// существующую запись (без дублей). Путь нормализуется в абсолютный.
    /// </summary>
    public DatabaseRefEntity Add(string name, string path)
    {
        var full = System.IO.Path.GetFullPath(path);

        var existing = _db.Databases.FirstOrDefault(d => d.Path == full);
        if (existing is not null) return existing;

        var entity = new DatabaseRefEntity
        {
            Name      = string.IsNullOrWhiteSpace(name) ? "База данных" : name.Trim(),
            Path      = full,
            Provider  = "sqlite",
            IsDefault = false,
        };
        _db.Databases.Add(entity);
        _db.SaveChanges();
        return entity;
    }

    /// <summary>Удалить БД из реестра. Дефолтную удалить нельзя.</summary>
    public void Remove(int id)
    {
        var entity = _db.Databases.FirstOrDefault(d => d.Id == id);
        if (entity is null || entity.IsDefault) return;
        _db.Databases.Remove(entity);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();
}
