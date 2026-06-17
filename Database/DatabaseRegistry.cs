using DataBase.Database.Entities;
using DataBase.Utils;

namespace DataBase.Database;

/// <summary>
/// Реестр пользовательских БД поверх системной app.db.
/// Гарантирует наличие единственной дефолтной БД (текущий database.db), которую
/// нельзя удалить. Добавленные базы пока только записываются в реестр —
/// монтирование (открытие и работа с ними) появится на следующих этапах.
/// </summary>
public sealed class DatabaseRegistry : IDisposable
{
    private readonly SystemDbContext _db;

    private DatabaseRegistry(SystemDbContext db) => _db = db;

    /// <summary>Открыть реестр и гарантировать дефолтную запись на путь данных по умолчанию.</summary>
    public static DatabaseRegistry Open(string defaultDataPath)
    {
        var db = new SystemDbContext(AppSettings.SystemDbPath);
        db.Database.EnsureCreated();   // создаёт app.db и таблицу, если их нет; существующие данные не трогает
        var registry = new DatabaseRegistry(db);
        registry.EnsureDefault(defaultDataPath);
        return registry;
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
