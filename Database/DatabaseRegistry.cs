using Microsoft.EntityFrameworkCore;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// Реестр пользовательских БД поверх системной app.db. Гарантирует наличие единственной
/// дефолтной БД (текущий database.db), которую нельзя удалить. Здесь же — хранилище
/// настроек приложения (<see cref="ISettingsDatabase"/>, таблица app_settings): контекст
/// app.db живет все время работы приложения, поэтому настройки читаются/пишутся через него.
/// </summary>
public sealed class DatabaseRegistry : IDisposable, ISettingsDatabase
{
    private readonly SystemDbContext _db;

    private DatabaseRegistry(SystemDbContext db, bool freshInstall)
    {
        _db = db;
        IsFreshInstall = freshInstall;
        Integrations   = new IntegrationsRegistry(db);
    }

    /// <summary>
    /// Соединения интеграций — они тоже живут в app.db и потому открываются вместе с реестром,
    /// на том же контексте: второе подключение к app.db не нужно ни одному из них.
    /// </summary>
    public IntegrationsRegistry Integrations { get; }

    /// <summary>
    /// Чистая установка: до этого запуска у приложения не было НИ ОДНОЙ базы данных —
    /// реестр пуст (ни дефолтной записи, ни подключенных баз) и файла основной БД на диске
    /// тоже нет. Единственное основание завести вводный чек-лист (см. <see cref="IntroSeed"/>):
    /// там, где базы уже жили, их содержимое — данные пользователя, и дополнять их нечем.
    /// </summary>
    public bool IsFreshInstall { get; }

    /// <summary>Открыть реестр и гарантировать дефолтную запись на путь данных по умолчанию.
    /// <paramref name="systemDbPath"/> — путь app.db; null = штатный
    /// <see cref="AppSettings.SystemDbPath"/> (иное задают только тесты: признак чистой
    /// установки читается именно из app.db, и проверять его на живом реестре нельзя).</summary>
    public static DatabaseRegistry Open(string defaultDataPath, string? systemDbPath = null)
    {
        var db = new SystemDbContext(systemDbPath ?? AppSettings.SystemDbPath);
        db.Database.EnsureCreated();   // создает app.db и таблицы, если их нет; существующие данные не трогает
        EnsureSettingsTable(db);       // app_settings могло не быть в старых app.db (EnsureCreated ее не достроит)
        EnsurePgColumns(db);           // Pg*-колонки реестра появились позже — достраиваем аддитивно
        EnsureIntegrationTables(db);   // соединения интеграций появились позже — по тому же правилу
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");  // WAL: параллельные чтения во время записи
        // Самолечение раздутого app.db: реестр и настройки крошечные, раздуться нечему,
        // но для полноты — если файл вдруг раздулся свободными страницами, разово ужимаем.
        // Порог ниже, чем у данных-БД (см. SqliteMaintenance): app.db всегда должен быть мал.
        SqliteMaintenance.CompactIfBloated(db, 1L * 1024 * 1024);

        // Признак чистой установки снимаем ДО EnsureDefault: дальше в реестре уже будет
        // дефолтная запись, и «никогда не запускались» станет неотличимо от «все удалили».
        // Пустого реестра мало: app.db мог быть удален (или не пережить перенос) при живом
        // database.db — база с данными пользователя, а не чистый старт.
        var freshInstall = !db.Databases.Any() &&
                           !System.IO.File.Exists(System.IO.Path.GetFullPath(defaultDataPath));

        var registry = new DatabaseRegistry(db, freshInstall);
        registry.EnsureDefault(defaultDataPath);
        registry.ProtectLegacyPasswords();
        return registry;
    }

    // Пароли PG, сохраненные до появления защищенного хранилища (или на другой ОС),
    // разово переводим в SecretProtector. На Linux Protect возвращает строку как есть —
    // сравнение оберегает от вечной перезаписи.
    private void ProtectLegacyPasswords()
    {
        var dirty = false;
        foreach (var d in _db.Databases.Where(d => d.Provider == "postgres").ToList())
        {
            if (string.IsNullOrEmpty(d.PgPassword) || SecretProtector.IsProtected(d.PgPassword))
                continue;
            var stored = SecretProtector.Protect(d.PgPassword);
            if (stored == d.PgPassword) continue;
            d.PgPassword = stored;
            dirty = true;
        }
        if (dirty) _db.SaveChanges();
    }

    // EnsureCreated() не изменяет уже существующую БД, поэтому в app.db, созданных до
    // появления настроек, таблицы app_settings нет — создаем ее аддитивно при открытии.
    private static void EnsureSettingsTable(SystemDbContext db) =>
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"app_settings\" (" +
            "\"Key\" TEXT NOT NULL CONSTRAINT \"PK_app_settings\" PRIMARY KEY, " +
            "\"Value\" TEXT NOT NULL);");

    // Таблицы интеграций появились позже: в существующих app.db их нет, а EnsureCreated()
    // достраивать схему не умеет. Правило то же, что и у остальных: только аддитивно.
    private static void EnsureIntegrationTables(SystemDbContext db)
    {
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"integration_connections\" (" +
            "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_integration_connections\" PRIMARY KEY AUTOINCREMENT, " +
            "\"Name\" TEXT NOT NULL, " +
            "\"Address\" TEXT NOT NULL, " +
            "\"Auth\" TEXT NOT NULL, " +
            "\"AuthName\" TEXT NULL, " +
            "\"Secret\" TEXT NULL, " +
            "\"HeadersJson\" TEXT NULL, " +
            "\"TimeoutSeconds\" INTEGER NOT NULL, " +
            "\"Retries\" INTEGER NOT NULL);");

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"integration_requests\" (" +
            "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_integration_requests\" PRIMARY KEY AUTOINCREMENT, " +
            "\"Name\" TEXT NOT NULL, " +
            "\"ConnectionId\" INTEGER NOT NULL, " +
            "\"Method\" TEXT NOT NULL, " +
            "\"Path\" TEXT NOT NULL, " +
            "\"HeadersJson\" TEXT NULL, " +
            "\"Body\" TEXT NULL, " +
            "\"Paging\" TEXT NOT NULL, " +
            "\"PagingField\" TEXT NULL, " +
            "\"ItemsPath\" TEXT NULL, " +
            "\"BindingJson\" TEXT NULL, " +
            "\"OutgoingJson\" TEXT NULL, " +
            "\"LastRunAt\" TEXT NULL);");

        // Привязки и отметка о последнем запуске появились позже самой таблицы: в app.db,
        // созданных до них, этих колонок нет, а CREATE IF NOT EXISTS существующую таблицу не
        // достраивает. Правило то же, что у остальных, — только аддитивно.
        AddColumns(db, "integration_requests",
                   [("BindingJson", "TEXT"), ("OutgoingJson", "TEXT"), ("LastRunAt", "TEXT")]);

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"integration_scenarios\" (" +
            "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_integration_scenarios\" PRIMARY KEY AUTOINCREMENT, " +
            "\"Name\" TEXT NOT NULL, " +
            "\"Enabled\" INTEGER NOT NULL, " +
            "\"Trigger\" TEXT NOT NULL, " +
            "\"StepsJson\" TEXT NULL, " +
            "\"LastRunAt\" TEXT NULL);");

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"integration_directions\" (" +
            "\"RequestId\" INTEGER NOT NULL CONSTRAINT \"PK_integration_directions\" PRIMARY KEY, " +
            "\"Fingerprint\" TEXT NOT NULL, " +
            "\"Confirmed\" TEXT NOT NULL, " +
            "\"At\" TEXT NOT NULL, " +
            "\"Until\" TEXT NOT NULL, " +
            "\"UsedAt\" TEXT NULL);");
    }

    /// <summary>
    /// Дописать колонки, которых в таблице еще нет. Общий прием app.db: он никогда не
    /// пересоздается, поэтому схема догоняется <c>ALTER ADD</c>, а не пересборкой.
    /// </summary>
    private static void AddColumns(
        SystemDbContext db, string table, IReadOnlyList<(string Name, string Type)> columns)
    {
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info(\"{table}\");";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) have.Add(reader.GetString(1));
        }

        // EF1002 «возможная SQL-инъекция»: имя таблицы и колонок приходит из кода рядом,
        // значений от пользователя в запросе нет. Параметром идентификатор сделать нельзя.
        #pragma warning disable EF1002
        foreach (var (name, type) in columns)
            if (!have.Contains(name))
                db.Database.ExecuteSqlRaw($"ALTER TABLE \"{table}\" ADD COLUMN \"{name}\" {type} NULL;");
        #pragma warning restore EF1002
    }

    // Колонки PG-подключений появились позже таблицы databases: в старых app.db их нет,
    // а EnsureCreated() существующую таблицу не достраивает. Правило app.db — только
    // аддитивные изменения (ALTER ADD), чтобы не терять реестр и настройки.
    private static void EnsurePgColumns(SystemDbContext db)
    {
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(\"databases\");";
            using var r = cmd.ExecuteReader();
            while (r.Read()) have.Add(r.GetString(1));
        }

        // EF1002 «возможная SQL-инъекция»: имя и тип колонки берутся из списка прямо над этой
        // строкой, значений от пользователя в запросе нет. Параметром имя колонки сделать
        // нельзя — это идентификатор, а не значение.
        #pragma warning disable EF1002
        foreach (var (name, type) in new[]
        {
            ("PgHost", "TEXT"), ("PgPort", "INTEGER"), ("PgDatabase", "TEXT"),
            ("PgUser", "TEXT"), ("PgPassword", "TEXT"), ("PgSslMode", "TEXT"),
        })
            if (!have.Contains(name))
                db.Database.ExecuteSqlRaw(
                    $"ALTER TABLE \"databases\" ADD COLUMN \"{name}\" {type} NULL;");
        #pragma warning restore EF1002
    }

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

    /// <summary>Ключи настроек, начинающиеся с приставки (группа с заранее неизвестным
    /// составом — например, настройки расширений под <c>ext.</c>).</summary>
    public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
        _db.Settings.AsNoTracking()
                    .Where(s => s.Key.StartsWith(prefix))
                    .Select(s => s.Key)
                    .ToList();

    // Если дефолтной БД еще нет — регистрируем текущий database.db как основную.
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

    /// <summary>
    /// Зарегистрировать подключение к PostgreSQL. Если сервер+порт+база уже есть в реестре —
    /// возвращаем существующую запись (без дублей), как <see cref="Add"/> для путей.
    /// </summary>
    public DatabaseRefEntity AddPostgres(string name, PgConnectionInfo info)
    {
        var existing = _db.Databases.AsEnumerable().FirstOrDefault(d =>
            d.IsPostgres
            && string.Equals(d.PgHost, info.Host, StringComparison.OrdinalIgnoreCase)
            && (d.PgPort ?? PgConnectionInfo.DefaultPort) == info.Port
            && d.PgDatabase == info.Database);
        if (existing is not null) return existing;

        var entity = new DatabaseRefEntity
        {
            Name       = string.IsNullOrWhiteSpace(name) ? info.Database : name.Trim(),
            Path       = info.ToDisplayString(),
            Provider   = "postgres",
            IsDefault  = false,
            PgHost     = info.Host,
            PgPort     = info.Port,
            PgDatabase = info.Database,
            PgUser     = info.User,
            PgPassword = SecretProtector.Protect(info.Password),
            PgSslMode  = info.SslMode,
        };
        _db.Databases.Add(entity);
        _db.SaveChanges();
        return entity;
    }

    /// <summary>Переименовать БД в списке (окно настроек базы). Имя — только подпись:
    /// путь/подключение не меняются. Пустое имя игнорируется.</summary>
    public void Rename(int id, string name)
    {
        var entity = _db.Databases.FirstOrDefault(d => d.Id == id);
        if (entity is null || string.IsNullOrWhiteSpace(name)) return;
        entity.Name = name.Trim();
        _db.SaveChanges();
    }

    /// <summary>Сменить сохраненный пароль подключения PG-записи (пароль роли поменяли на
    /// сервере). Старый секрет удаляется из хранилища ОС, новый защищается заново.
    /// Работоспособность пароля проверяет вызывающий (окно делает пробу до записи).</summary>
    public void UpdatePgPassword(int id, string password)
    {
        var entity = _db.Databases.FirstOrDefault(d => d.Id == id);
        if (entity is null || !entity.IsPostgres) return;
        SecretProtector.Delete(entity.PgPassword ?? "");
        entity.PgPassword = SecretProtector.Protect(password);
        _db.SaveChanges();
    }

    /// <summary>Удалить БД из реестра. Дефолтную удалить нельзя.</summary>
    public void Remove(int id)
    {
        var entity = _db.Databases.FirstOrDefault(d => d.Id == id);
        if (entity is null || entity.IsDefault) return;
        if (entity.IsPostgres) SecretProtector.Delete(entity.PgPassword ?? "");
        _db.Databases.Remove(entity);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();
}
