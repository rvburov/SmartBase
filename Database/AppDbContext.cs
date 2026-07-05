using Microsoft.EntityFrameworkCore;
using DataBase.Database.Entities;

namespace DataBase.Database;

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
    public DbSet<SystemFieldMetaEntity>    SystemFieldMeta      { get; set; } = null!;
    public DbSet<ColumnOrderEntity>        ColumnOrder          { get; set; } = null!;
    public DbSet<ColumnDisplayEntity>      ColumnDisplay        { get; set; } = null!;
    public DbSet<RecordDisplayEntity>      RecordDisplay        { get; set; } = null!;

    private readonly string _dbPath;

    /// <summary>Путь к файлу БД — нужен, чтобы открыть параллельное read-only подключение
    /// (фоновые запросы, WAL допускает одновременное чтение) без общего контекста.</summary>
    public string DbPath => _dbPath;

    public AppDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Data Source={_dbPath}");

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
        // Каскадное удаление (записи→значения, коллекция→поля/настройки) держится на FK ON DELETE
        // CASCADE — а оно работает только при включённом enforcement. Включаем явно на КАЖДОМ
        // подключении (и фоновом), чтобы batched-удаление и удаление группы/коллекции не оставляли
        // «осиротевшие» значения/поля. PRAGMA вне транзакции (вызываем сразу после создания контекста).
        Database.ExecuteSqlRaw("PRAGMA foreign_keys=ON;");
        Database.ExecuteSqlRaw("PRAGMA busy_timeout=10000;");   // ждать чужого писателя, не падать SQLITE_BUSY
        Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");   // с WAL безопасно и быстрее коммитов
        Database.ExecuteSqlRaw("PRAGMA temp_store=MEMORY;");
        Database.ExecuteSqlRaw("PRAGMA mmap_size=268435456;");  // чтение индексов через mmap, а не syscalls
        Database.ExecuteSqlRaw("PRAGMA cache_size=-65536;");    // ~64 МБ страничного кеша
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
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.RecordId, x.FieldId }).IsUnique();
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

        model.Entity<SystemFieldMetaEntity>(e =>
        {
            e.ToTable("system_field_meta");
            e.HasKey(x => new { x.CollectionId, x.Key });
        });

        model.Entity<ColumnOrderEntity>(e =>
        {
            e.ToTable("column_order");
            e.HasKey(x => new { x.CollectionId, x.Key });
        });

        model.Entity<ColumnDisplayEntity>(e =>
        {
            e.ToTable("column_display");
            e.HasKey(x => new { x.CollectionId, x.Key });
        });

        model.Entity<RecordDisplayEntity>(e =>
        {
            e.ToTable("record_display");
            e.HasKey(x => new { x.CollectionId, x.RecordId });
        });
    }
}
