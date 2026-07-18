using Microsoft.EntityFrameworkCore;
using SmartBase.Database.Repositories;

namespace SmartBase.Database;

/// <summary>
/// Открытая база данных: контекст + репозитории + путь к файлу.
/// Сюда вынесена инициализация из App (миграции, бэкафилл, сидинг), чтобы один и тот же
/// конвейер применялся к каждому открываемому файлу. Пока — только провайдер SQLite;
/// ветка под PostgreSQL добавится здесь же в будущем без переписывания вызывающего кода.
/// </summary>
public sealed class DatabaseManager : IDisposable
{
    // Идентичность смонтированной БД из реестра: DbId стабилен между запусками
    // и служит «префиксом» для изоляции id при мульти-маунте (Этап 3).
    public int                   DbId        { get; }
    public string                Name        { get; }
    public string                Path        { get; }
    public AppDbContext          Context     { get; }
    public CategoryRepository    Categories  { get; }
    public ConstructorRepository Constructor { get; }
    public PermissionService     Permissions { get; }

    private DatabaseManager(int dbId, string name, string path)
    {
        DbId    = dbId;
        Name    = name;
        Path    = path;
        Context = new AppDbContext(path);

        // Все изменения схемы применяются автоматически через EF Core Migrations.
        // Любая будущая правка сущностей → `dotnet ef migrations add ...` →
        // при следующем запуске Migrate() накатит её, не трогая существующие данные.
        ApplySchemaMigrations(Context, path);

        // Режим WAL: параллельные чтения во время записи + быстрее коммиты. Настройка
        // персистится в заголовке файла; на сетевой ФС SQLite её тихо игнорирует.
        EnableWal(Context);

        // Настройки скорости под большие коллекции (100k–10M строк). В отличие от WAL
        // (живёт в заголовке файла) эти прагмы — на соединение; ставим на основном
        // соединении контекста, которое живёт весь сеанс работы с БД.
        TunePerformancePragmas(Context);

        // Самолечение раздутого файла: остатки массовых импортов/удалений — SQLite без
        // auto_vacuum не возвращает освобождённые страницы в ОС, файл копится в разы
        // больше данных. Порог для данных-БД — 16 МБ свободного (см. SqliteMaintenance).
        SqliteMaintenance.CompactIfBloated(Context, 16L * 1024 * 1024);

        // Пользователи/права этой базы: регистрируем текущего пользователя (первый
        // открывший базу без админов становится администратором) и строим кеш прав.
        // Репозиторий получает сервис прав — его мутаторы проверяют права первой строкой.
        Permissions = new PermissionService(Context);
        Permissions.EnsureCurrentUserRegistered();

        Categories  = new CategoryRepository(Context);
        Constructor = new ConstructorRepository(Context) { Permissions = Permissions };

        // Системное поле «Дата» → тип date + источник календаря; бэкафилл StartDt
        // для записей, у которых дата задана только в поле конструктора.
        Constructor.MigrateAndBackfillCalendarDates();

        Categories.SeedDefaults();
    }

    /// <summary>Смонтировать SQLite-БД с её идентичностью из реестра (DbId, имя, путь).</summary>
    public static DatabaseManager Open(int dbId, string name, string path) => new(dbId, name, path);

    public void Dispose() => Context.Dispose();

    // Включить WAL для файла БД (идемпотентно). На сетевой ФС SQLite оставит прежний режим.
    private static void EnableWal(AppDbContext db) =>
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

    // Прагмы скорости (на соединение):
    //  • synchronous=NORMAL — с WAL безопасно и заметно быстрее коммитов (импорт пачками);
    //  • temp_store=MEMORY  — временные индексы/сортировки в RAM, не на диск;
    //  • mmap_size=256MB    — чтение через memory-map: меньше syscalls на больших скан-ах;
    //  • cache_size=-65536  — страничный кеш ~64 МБ (значение в КБ со знаком «−»), дефолт ~2 МБ.
    // Прагмы скорости (на соединение) — единый набор для основного и фоновых подключений,
    // см. AppDbContext.ApplyPerformancePragmas (busy_timeout/synchronous/temp_store/mmap/cache).
    private static void TunePerformancePragmas(AppDbContext db) => db.ApplyPerformancePragmas();

    // ── Миграции ────────────────────────────────────────────────────────────────

    // Мост со старого EnsureCreated на EF Migrations.
    //
    // У ранних пользователей БД создавалась через EnsureCreated() — там нет таблицы
    // __EFMigrationsHistory, поэтому Migrate() попытается выполнить InitialCreate
    // и упадёт на «таблица уже существует». Чтобы этого избежать, перед Migrate()
    // вручную создаём таблицу истории и фиксируем в ней InitialCreate как
    // «уже применённую». После этого Migrate() накатит только последующие миграции.
    private static void ApplySchemaMigrations(AppDbContext db, string path)
    {
        var dbFileExists = File.Exists(path);

        // Случай 1: БД ещё не существует — Migrate() сам создаст её и применит все миграции по порядку.
        if (!dbFileExists)
        {
            db.Database.Migrate();
            return;
        }

        // Случай 2: БД есть. Проверяем, есть ли в ней таблица __EFMigrationsHistory
        // и реальные таблицы схемы (например, db_records — создаётся InitialCreate).
        var hasMigrationsTable = HasMigrationsHistoryTable(db);
        var hasSchemaTables    = HasTable(db, "db_records");

        if (!hasMigrationsTable && hasSchemaTables)
        {
            // Старая БД из времён EnsureCreated. Считаем, что её схема соответствует
            // InitialCreate, и помечаем эту миграцию как применённую — чтобы Migrate()
            // не пытался создавать таблицы повторно.
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                    ""MigrationId""    TEXT NOT NULL CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY,
                    ""ProductVersion"" TEXT NOT NULL
                );
            ");
            db.Database.ExecuteSqlRaw(@"
                INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                VALUES ('20260607161850_InitialCreate', '9.0.0');
            ");
        }

        // Теперь EF корректно увидит, какие миграции применены, и накатит недостающие.
        db.Database.Migrate();
    }

    private static bool HasMigrationsHistoryTable(AppDbContext db) =>
        HasTable(db, "__EFMigrationsHistory");

    private static bool HasTable(AppDbContext db, string tableName)
    {
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$name;";
            var p = cmd.CreateParameter();
            p.ParameterName = "$name";
            p.Value = tableName;
            cmd.Parameters.Add(p);
            using var r = cmd.ExecuteReader();
            return r.Read();
        }
        finally
        {
            if (opened) conn.Close();
        }
    }
}
