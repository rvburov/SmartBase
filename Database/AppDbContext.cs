using Microsoft.EntityFrameworkCore;
using SmartBase.Database.Entities;

namespace SmartBase.Database;

public class AppDbContext : DbContext
{
    public DbSet<CategoryEntity>           Categories           { get; set; } = null!;
    public DbSet<DbGroupEntity>            DbGroups             { get; set; } = null!;
    public DbSet<DbCollectionEntity>       DbCollections        { get; set; } = null!;
    public DbSet<DbFieldEntity>            DbFields             { get; set; } = null!;
    public DbSet<DbRecordEntity>           DbRecords            { get; set; } = null!;
    public DbSet<DbFieldValueEntity>       DbFieldValues        { get; set; } = null!;
    public DbSet<CalendarIndexEntity>      CalendarIndex        { get; set; } = null!;
    public DbSet<UiColWidthEntity>         UiColWidths          { get; set; } = null!;
    public DbSet<UiRowHeightEntity>        UiRowHeights         { get; set; } = null!;
    public DbSet<UiCollectionSettingEntity> UiCollectionSettings { get; set; } = null!;
    public DbSet<ColumnMetaEntity>         ColumnMeta           { get; set; } = null!;
    public DbSet<RecordDisplayEntity>      RecordDisplay        { get; set; } = null!;
    public DbSet<DbUserEntity>             DbUsers              { get; set; } = null!;
    public DbSet<DbPermissionEntity>       DbPermissions        { get; set; } = null!;
    public DbSet<DbMetaEntity>             DbMeta               { get; set; } = null!;
    public DbSet<ChangeLogEntity>          ChangeLog            { get; set; } = null!;

    private readonly string _dbPath;

    /// <summary>Путь к файлу БД — нужен, чтобы открыть параллельное read-only подключение
    /// (фоновые запросы, WAL допускает одновременное чтение) без общего контекста.
    /// Для PostgreSQL здесь строка подключения Npgsql — фоновые места
    /// (<c>new AppDbContext(db.DbPath)</c>) продолжают работать без правок.</summary>
    public string DbPath => _dbPath;

    /// <summary>Провайдер этой БД: строка подключения PostgreSQL вместо пути к файлу SQLite.
    /// Признак — «Host=» в строке (см. PgConnectionInfo.IsPostgresConnectionString).</summary>
    public bool IsPostgres { get; }

    public AppDbContext(string dbPath)
    {
        _dbPath    = dbPath;
        IsPostgres = PgConnectionInfo.IsPostgresConnectionString(dbPath);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (IsPostgres) options.UseNpgsql(_dbPath);
        else            options.UseSqlite($"Data Source={_dbPath}");
        // Один тип контекста обслуживает два провайдера: без своего ключа кеша EF отдал бы
        // модель первого открытого провайдера второму (у PG имена колонок lowercase — см.
        // ApplyLowercaseNames), что молча ломало бы SQL. Ключ учитывает IsPostgres.
        options.ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory,
                               ProviderModelCacheKeyFactory>();
    }

    /// <summary>
    /// Прагмы скорости на ПОДКЛЮЧЕНИЕ (не на файл). Применять и к основному контексту, и к КАЖДОМУ
    /// свежему фоновому (импорт/копия дубля/подгрузка строк и значений) — иначе фоновые операции
    /// деградируют: без <c>mmap_size</c> чтения страниц индексов идут pread-syscall'ами (импорт
    /// 425k в БД с уже большими коллекциями — минуты вместо секунд, т.к. индексы db_field_values
    /// глобальные); без <c>busy_timeout</c> параллельные писатели падают с SQLITE_BUSY. journal_mode=WAL
    /// здесь НЕ ставим — он персистится в заголовке файла (см. DatabaseManager.EnableWal).
    /// Централизовано, чтобы наборы прагм основного и фоновых подключений не расходились.
    /// </summary>
    public void ApplyPerformancePragmas()
    {
        // PostgreSQL: PRAGMA не существует, тюнинг (shared_buffers, work_mem) — забота сервера;
        // FK enforcement и MVCC включены всегда. Один guard нейтрализует все вызовы из репозиториев.
        if (IsPostgres) return;

        // Сетевой путь (SMB/NFS, см. NetworkPath): другой профиль надёжности — БД общая,
        // с ней могут работать несколько пользователей с разных машин:
        //  • busy_timeout 15 с — чужие писатели по сети держат замок дольше;
        //  • synchronous=FULL — журнал DELETE (не WAL, см. DatabaseManager.ApplyJournalMode),
        //    NORMAL в rollback-режиме допускает порчу при сбое питания сервера;
        //  • mmap_size=0 — mmap поверх сетевой ФС прямо не рекомендован SQLite
        //    (когерентность кеша страниц между машинами не гарантируется).
        var shared = NetworkPath.IsNetwork(_dbPath);

        // Каскадное удаление (записи→значения, коллекция→поля/настройки) держится на FK ON DELETE
        // CASCADE — а оно работает только при включённом enforcement. Включаем явно на КАЖДОМ
        // подключении (и фоновом), чтобы batched-удаление и удаление группы/коллекции не оставляли
        // «осиротевшие» значения/поля. PRAGMA вне транзакции (вызываем сразу после создания контекста).
        Database.ExecuteSqlRaw("PRAGMA foreign_keys=ON;");
        Database.ExecuteSqlRaw(shared
            ? "PRAGMA busy_timeout=15000;"
            : "PRAGMA busy_timeout=10000;");   // ждать чужого писателя, не падать SQLITE_BUSY
        Database.ExecuteSqlRaw(shared
            ? "PRAGMA synchronous=FULL;"
            : "PRAGMA synchronous=NORMAL;");   // с WAL локально безопасно и быстрее коммитов
        Database.ExecuteSqlRaw("PRAGMA temp_store=MEMORY;");
        Database.ExecuteSqlRaw(shared
            ? "PRAGMA mmap_size=0;"
            : "PRAGMA mmap_size=268435456;");  // локально: чтение индексов через mmap, а не syscalls
        Database.ExecuteSqlRaw("PRAGMA cache_size=-65536;");    // ~64 МБ страничного кеша
    }

    /// <summary>
    /// Профиль прагм для МАССОВЫХ операций (дубль/перенос/удаление/импорт миллионов строк) на
    /// изолированном фоновом подключении: поверх <see cref="ApplyPerformancePragmas"/> поднимает
    /// кеш до ~512 МБ и mmap до 2 ГБ. Глобальные индексы (db_field_values, calendar_index) на
    /// больших БД (десятки ГБ) не влезают в стандартные 64 МБ — каждая вставка/удаление строки
    /// деградирует в случайный дисковый ввод-вывод (замер на стенде 10 млн записей: дубль коллекции
    /// шёл ~35 мин). Только для короткоживущих фоновых подключений — основному контексту держать
    /// сотни МБ кеша весь сеанс ни к чему (те же значения использует генератор стенда).
    /// </summary>
    public void ApplyBulkPerformancePragmas()
    {
        if (IsPostgres) return;   // см. ApplyPerformancePragmas

        ApplyPerformancePragmas();
        // На сетевом пути mmap остаётся выключенным (см. ApplyPerformancePragmas) —
        // поднимаем только страничный кеш.
        if (!NetworkPath.IsNetwork(_dbPath))
            Database.ExecuteSqlRaw("PRAGMA mmap_size=2147483648;");
        Database.ExecuteSqlRaw("PRAGMA cache_size=-524288;");
    }

    /// <summary>
    /// Прагмы для РАЗОВЫХ миграций схемы — вызывать ДО <c>Database.Migrate()</c>, когда есть
    /// ожидающие миграции. Table-rebuild миграции (пример: SchemaCompact) перекладывают миллионы
    /// строк, а с дефолтным кешем SQLite ~2 МБ и без mmap это часы вместо минут. Отличия от
    /// <see cref="ApplyBulkPerformancePragmas"/>: <c>foreign_keys</c> НЕ включаем (дефолтный OFF —
    /// перекладка идёт без FK-проверки на каждую строку, сами объявления FK копируются в DDL);
    /// <c>temp_store</c> оставляем дисковым (внешняя сортировка ORDER BY на гигабайтах не должна
    /// раздувать память). После Migrate() основной контекст возвращается к обычному профилю —
    /// TunePerformancePragmas перезаписывает кеш/mmap штатными значениями.
    /// </summary>
    public void ApplyMigrationPragmas()
    {
        if (IsPostgres) return;   // см. ApplyPerformancePragmas

        var shared = NetworkPath.IsNetwork(_dbPath);
        Database.ExecuteSqlRaw("PRAGMA busy_timeout=10000;");
        // На сетевом пути — FULL и без mmap (те же причины, что в ApplyPerformancePragmas):
        // разовая миграция общей базы медленнее, но не рискует файлом при сбое сервера.
        Database.ExecuteSqlRaw(shared ? "PRAGMA synchronous=FULL;" : "PRAGMA synchronous=NORMAL;");
        if (!shared) Database.ExecuteSqlRaw("PRAGMA mmap_size=2147483648;");
        Database.ExecuteSqlRaw("PRAGMA cache_size=-524288;");
        Database.ExecuteSqlRaw("PRAGMA threads=4;");   // воркеры сортировщика (no-op, если сборка SQLite без них)
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<CategoryEntity>(e =>
        {
            e.ToTable("categories");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
        });

        model.Entity<DbGroupEntity>(e =>
        {
            e.ToTable("db_groups");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Parent)
             .WithMany(x => x.Children)
             .HasForeignKey(x => x.ParentId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<DbCollectionEntity>(e =>
        {
            e.ToTable("db_collections");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Group)
             .WithMany(x => x.Collections)
             .HasForeignKey(x => x.GroupId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<DbFieldEntity>(e =>
        {
            e.ToTable("db_fields");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.CollectionId, x.Name }).IsUnique();
            e.HasOne(x => x.Collection)
             .WithMany(x => x.Fields)
             .HasForeignKey(x => x.CollectionId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<DbRecordEntity>(e =>
        {
            e.ToTable("db_records");
            e.HasKey(x => x.Id);
            // Составной индекс под горячий путь таблицы: WHERE CollectionId ORDER BY Position.
            // Без него SQLite пересортировывает всю коллекцию на каждый запрос страницы —
            // на больших коллекциях (100k+) открытие/скролл деградируют. С индексом записи
            // уже лежат в нужном порядке: и фильтр по коллекции, и keyset-пагинация по Position.
            e.HasIndex(x => new { x.CollectionId, x.Position });
            e.HasOne(x => x.Collection)
             .WithMany(x => x.Records)
             .HasForeignKey(x => x.CollectionId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<DbFieldValueEntity>(e =>
        {
            e.ToTable("db_field_values");
            // Естественный PK (RecordId, FieldId): физически таблица WITHOUT ROWID (см. миграцию
            // SchemaCompact) — кластеризована по PK, значения одной записи лежат рядом, и отдельный
            // уникальный индекс пары не нужен (минус целое B-дерево на самой большой таблице).
            e.HasKey(x => new { x.RecordId, x.FieldId });
            // Фильтр по значению поля (Value IN ...) и подсчёт уникальных значений (GROUP BY Value)
            // идут по этому индексу — иначе скан всех значений поля. См. GetFilteredRecordIds.
            e.HasIndex(x => new { x.FieldId, x.Value });
            e.HasOne(x => x.Record)
             .WithMany(x => x.FieldValues)
             .HasForeignKey(x => x.RecordId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Field)
             .WithMany(x => x.FieldValues)
             .HasForeignKey(x => x.FieldId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<CalendarIndexEntity>(e =>
        {
            e.ToTable("calendar_index");
            e.HasKey(x => new { x.RecordId, x.FieldId });
            // Горячий путь календаря: WHERE CollectionId IN(...) AND StartIso < end AND EndIso >= start.
            e.HasIndex(x => new { x.CollectionId, x.StartIso });
            // Сплит окна на два под-запроса по AllDay: короткие timed — с нижней границей StartIso,
            // многодневные/all-day — без неё (их мало). Индекс изолирует каждый класс, чтобы
            // под-запрос не сканировал всё от начала. См. ConstructorRepository.ReadCalendarEvents.
            // Хвост (RecordId, FieldId) даёт полный порядок (StartIso, RecordId, FieldId) — на нём
            // «Расписание» согласует offset-прыжки и keyset-скролл (одинаковый порядок → без дублей/
            // пропусков на границах страниц с равным StartIso), а глубокий offset идёт по индексу без
            // временной сортировки. Прежний префикс (CollectionId, AllDay, StartIso) остаётся рабочим
            // для сплит-окон. См. CalendarDayVirtualList / GetCalendarDayEventsByOffset.
            e.HasIndex(x => new { x.CollectionId, x.AllDay, x.StartIso, x.RecordId, x.FieldId });
            // Спилловеры окна сетки: timed-события, начавшиеся ДО окна и пересекающие его, ищутся
            // сиком по EndIso ∈ [начало окна, начало+буфер) — без этого индекса проверка EndIso шла
            // чтением строк по всему 62-дневному StartIso-буферу, и день-гигант в буфере стоил
            // секунд на каждое открытие «Дня»/«Недели» в следующие 62 дня после него. Хвост
            // (StartIso, RecordId, FieldId) делает запрос покрывающим.
            // См. GetCalendarRecordsByRangeCapped.
            e.HasIndex(x => new { x.CollectionId, x.AllDay, x.EndIso, x.StartIso, x.RecordId, x.FieldId });
            e.HasOne(x => x.Record)
             .WithMany()
             .HasForeignKey(x => x.RecordId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Field)
             .WithMany()
             .HasForeignKey(x => x.FieldId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<UiColWidthEntity>(e =>
        {
            e.ToTable("ui_col_widths");
            e.HasKey(x => new { x.CollectionId, x.FieldId });
        });

        model.Entity<UiRowHeightEntity>(e =>
        {
            e.ToTable("ui_row_heights");
            e.HasKey(x => new { x.CollectionId, x.RecordId });
        });

        model.Entity<UiCollectionSettingEntity>(e =>
        {
            e.ToTable("ui_collection_settings");
            e.HasKey(x => x.CollectionId);
        });

        model.Entity<ColumnMetaEntity>(e =>
        {
            e.ToTable("column_meta");
            e.HasKey(x => new { x.CollectionId, x.Key });
        });

        model.Entity<RecordDisplayEntity>(e =>
        {
            e.ToTable("record_display");
            e.HasKey(x => new { x.CollectionId, x.RecordId });
        });

        model.Entity<DbUserEntity>(e =>
        {
            e.ToTable("db_users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserKey).IsUnique();
        });

        model.Entity<DbPermissionEntity>(e =>
        {
            e.ToTable("db_permissions");
            e.HasKey(x => x.Id);
            // Одна маска на пользователя и «область» (вся база / конкретная коллекция).
            e.HasIndex(x => new { x.UserId, x.CollectionId }).IsUnique();
            e.HasOne(x => x.User)
             .WithMany(x => x.Permissions)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Collection)
             .WithMany()
             .HasForeignKey(x => x.CollectionId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<DbMetaEntity>(e =>
        {
            e.ToTable("db_meta");
            e.HasKey(x => x.Key);
        });

        model.Entity<ChangeLogEntity>(e =>
        {
            e.ToTable("change_log");
            e.HasKey(x => x.Id);
            // Без FK: журнал переживает удаление коллекций/записей/пользователей.
            // Чтение идёт keyset-пагинацией по Id (rowid) DESC — индексы не нужны.
        });

        if (IsPostgres) ApplyLowercaseNames(model);
    }

    /// <summary>
    /// PG-модель: все имена колонок в lowercase (таблицы уже lowercase). PostgreSQL сворачивает
    /// неэкранированные идентификаторы к нижнему регистру — благодаря этому весь общий raw SQL
    /// репозиториев (<c>WHERE CollectionId = @c</c> и т.п.) работает на обоих провайдерах без
    /// правок: SQLite матчит имена без учёта регистра, PG находит колонку <c>collectionid</c>.
    /// Флаги (bool) маппятся в integer 0/1, как SQLite хранит их физически: иначе boolean-колонки
    /// PG сломали бы общий raw SQL (`IsCalStart = 1`, `AllDay = 0`, конкатенации, int-биндинги).
    /// SQLite-модель не трогаем — имена колонок там исторически PascalCase.
    /// </summary>
    private static void ApplyLowercaseNames(ModelBuilder model)
    {
        var boolToInt = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.BoolToZeroOneConverter<int>();
        foreach (var entity in model.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(property.GetColumnName().ToLowerInvariant());
                if (property.ClrType == typeof(bool) || property.ClrType == typeof(bool?))
                {
                    property.SetValueConverter(boolToInt);   // null (bool?) проходит мимо конвертера
                    property.SetColumnType("integer");
                }
            }
    }
}
