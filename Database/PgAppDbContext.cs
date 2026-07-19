namespace SmartBase.Database;

/// <summary>
/// Контекст пользовательской БД на PostgreSQL. Пустой подкласс существует ради миграций:
/// EF Core привязывает набор миграций к точному типу контекста (атрибут [DbContext(...)]
/// на классах в Database/Migrations/Pg), поэтому SQLite-миграции AppDbContext и PG-миграции
/// живут в одной сборке, не мешая друг другу. Инстанцирует его только DatabaseManager
/// (единственное место, где вызывается Migrate()); фоновые подключения репозиториев
/// (new AppDbContext(db.DbPath)) остаются базовым типом — провайдер выбирается по строке.
/// </summary>
public sealed class PgAppDbContext(string connectionString) : AppDbContext(connectionString);
