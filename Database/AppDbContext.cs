using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Serilog;
using SmartBase.Database.Entities;
using SmartBase.Logging;

namespace SmartBase.Database;

public class AppDbContext : DbContext
{
    public DbSet<CategoryEntity>           Categories           { get; set; } = null!;
    public DbSet<DbGroupEntity>            DbGroups             { get; set; } = null!;
    public DbSet<DbCollectionEntity>       DbCollections        { get; set; } = null!;
    public DbSet<DbFieldEntity>            DbFields             { get; set; } = null!;
    public DbSet<DbRecordEntity>           DbRecords            { get; set; } = null!;
    public DbSet<DbFieldValueEntity>       DbFieldValues        { get; set; } = null!;
    public DbSet<DbRecordLinkEntity>       DbRecordLinks        { get; set; } = null!;
    public DbSet<CalendarIndexEntity>      CalendarIndex        { get; set; } = null!;
    public DbSet<UiColWidthEntity>         UiColWidths          { get; set; } = null!;
    public DbSet<UiRowHeightEntity>        UiRowHeights         { get; set; } = null!;
    public DbSet<UiCollectionSettingEntity> UiCollectionSettings { get; set; } = null!;
    public DbSet<ColumnMetaEntity>         ColumnMeta           { get; set; } = null!;
    public DbSet<RecordDisplayEntity>      RecordDisplay        { get; set; } = null!;
    public DbSet<CellStyleEntity>          CellStyles           { get; set; } = null!;
    public DbSet<DbUserEntity>             DbUsers              { get; set; } = null!;
    public DbSet<DbPermissionEntity>       DbPermissions        { get; set; } = null!;
    public DbSet<DbMetaEntity>             DbMeta               { get; set; } = null!;
    public DbSet<ChangeLogEntity>          ChangeLog            { get; set; } = null!;
    public DbSet<TaskEventEntity>          TaskEvents           { get; set; } = null!;

    private readonly string _dbPath;

    // Профиль объема этого контекста: обычный (~64 МБ кеша) или массовый (~512 МБ, mmap 2 ГБ).
    // Хранится на контексте, а не ставится разово: соединение закрывается и открывается заново
    // между операциями, и объемные прагмы обязаны восстановиться вместе с ним.
    private bool _bulkProfile;
    // Перенос журнала этой правки — в фоне (DeferCheckpoint, WalCheckpointer).
    private bool _deferCheckpoint;

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

        // Прагмы надежности — на КАЖДОЕ открытие соединения (см. ApplyConnectionPragmas).
        // Подписка именно на StateChange самого соединения, а не на интерцептор EF: около
        // тридцати мест (пагинация, календарь, массовые операции) открывают соединение
        // контекста НАПРЯМУЮ — `conn.Open()` мимо Database.OpenConnection(), и интерцептор
        // EF там просто не сработал бы. У контекста ровно одно соединение на весь его срок
        // жизни, поэтому одной подписки достаточно.
        if (!IsPostgres)
            Database.GetDbConnection().StateChange += OnConnectionStateChange;

        // Шина своих изменений: что затронуто, собирается по событиям трекера (без лишнего
        // DetectChanges), выпускается после SaveChanges или на коммите транзакции.
        ChangeTracker.Tracked      += (_, e) => NoteDirty(e.Entry.Entity, e.Entry.State);
        ChangeTracker.StateChanged += (_, e) => NoteDirty(e.Entry.Entity, e.NewState);
    }

    private void OnConnectionStateChange(object? sender, System.Data.StateChangeEventArgs e)
    {
        if (e.CurrentState != System.Data.ConnectionState.Open || sender is not DbConnection conn) return;
        ApplyConnectionPragmas(conn);
        // Поиск без учета регистра (SqlText.ContainsIgnoreCase): встроенные LIKE и lower() SQLite
        // регистр различают только у латиницы. Функция — на подключение, как и прагмы.
        if (conn is Microsoft.Data.Sqlite.SqliteConnection sqlite)
            sqlite.CreateFunction(SqlText.ContainsName, (string? value, string? part) =>
                value is not null && part is not null && value.Contains(part, StringComparison.OrdinalIgnoreCase),
                isDeterministic: true);
    }

    // ── Аудит-метаданные записей (для типов свойств «Дата/Создание/Изменение …») ──
    // Централизованно проставляем автора/время СОЗДАНИЯ новым записям (Added) прямо перед
    // сохранением: покрывает все EF-пути создания через инициализаторы объектов (одиночный
    // дубль, релокация, календарные копии, восстановление снапшота) — вызывающему коду не
    // нужно заполнять поля вручную. Явно заданные значения (см. ConstructorRepository.AddRecord)
    // не перетираем. Сырые INSERT мимо EF (импорт/пакетный дубль/перенос) стампят метаданные в
    // своем SQL. «Изменение» стампится точечно в методах правки (TouchUpdated/SetFieldValue).
    public override int SaveChanges() => SaveChanges(acceptAllChangesOnSuccess: true);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampNewRecordAudit();
        var n = SaveReconcilingPhantomDeletes(() => base.SaveChanges(acceptAllChangesOnSuccess));
        NoteSaved();
        return n;
    }

    // ── Шина своих изменений (ChangeBus) ──────────────────────────────────────
    //
    // Что изменилось, собирается по событиям трекера: Tracked — сущность попала в контекст
    // (Added — создание), StateChanged — сменила состояние (Modified при DetectChanges внутри
    // SaveChanges, Deleted при Remove). Это дешевле, чем обходить ChangeTracker.Entries()
    // перед сохранением: обход сам вызывает DetectChanges, то есть удваивал бы его на каждом
    // SaveChanges. Строки change_log несут действие/тип/коллекцию как есть; сущности данных
    // сводятся к тем же типам, что в журнале; UI-таблицы (ширины, стили, отображение),
    // очередь уведомлений и метаданные экрану других разделов не нужны — пропускаются.
    // Правки мимо трекера (ExecuteDelete, сырой SQL массовых путей) видны через строку журнала,
    // которую такие пути пишут сами (LogNow).

    private readonly HashSet<ChangeBus.Change> _dirty = [];   // с последнего SaveChanges
    private HashSet<ChangeBus.Change>? _pendingCommit;        // сохранено внутри транзакции — ждет коммита

    private void NoteDirty(object entity, EntityState state)
    {
        if (state is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) return;

        if (entity is ChangeLogEntity log)
        {
            if (state == EntityState.Added)
                _dirty.Add(new ChangeBus.Change(log.Action, log.TargetType, log.CollectionName));
            return;
        }

        var type = entity switch
        {
            DbGroupEntity      => "group",
            DbCollectionEntity => "collection",
            DbFieldEntity      => "field",
            DbRecordEntity or DbFieldValueEntity or DbRecordLinkEntity or CalendarIndexEntity => "record",
            DbUserEntity or DbPermissionEntity => "user",
            CategoryEntity     => "category",
            _                  => null,
        };
        if (type is null) return;

        var action = state switch
        {
            EntityState.Added   => "create",
            EntityState.Deleted => "delete",
            _                   => "update",
        };
        _dirty.Add(new ChangeBus.Change(action, type, null));
    }

    // После удачного SaveChanges: вне транзакции — сразу в шину, внутри — отложить до коммита
    // (см. ChangeBusInterceptor): до него соседнее подключение изменений не увидит.
    private void NoteSaved()
    {
        if (_dirty.Count == 0) return;
        var changes = _dirty.ToList();
        _dirty.Clear();

        if (Database.CurrentTransaction is null)
            ChangeBus.Publish(_dbPath, changes);
        else
            (_pendingCommit ??= []).UnionWith(changes);
    }

    internal void OnTransactionStarted() => _pendingCommit?.Clear();

    internal void OnTransactionCommitted()
    {
        if (_pendingCommit is not { Count: > 0 } pending) return;
        var changes = pending.ToList();
        pending.Clear();
        ChangeBus.Publish(_dbPath, changes);
    }

    internal void OnTransactionRolledBack()
    {
        _pendingCommit?.Clear();
        _dirty.Clear();   // несохраненное откатилось вместе с трекером (WriteScope чистит его)
    }

    /// <summary>
    /// Строки, которые мы правили, уже удалил другой пользователь: сохранение прошло БЕЗ них
    /// (см. <see cref="SaveReconcilingPhantomDeletes"/>). Аргумент — сколько правок отброшено.
    /// Ставит MainWindow (сообщение человеку); вызывается на потоке сохранения — подписчик
    /// маршалит в UI сам.
    /// </summary>
    public static Action<int>? PhantomRowsDropped;

    // Общий контекст живет все время работы с БД. Если SaveChanges падает concurrency-исключением
    // «ожидалась 1 строка, затронуто 0» — этой строки уже нет: ее снес каскад в ЭТОМ же контексте
    // (удалили группу/коллекцию → дочерние коллекции/записи/значения ушли по FK, а EF все еще
    // держал их в трекере) или другой пользователь общей базы. Токенов конкурентности у модели
    // нет, поэтому «0 строк» означает ровно «строки нет», и для любой такой строки правильный
    // исход один — забыть о ней и сохранить остальное:
    //  • Deleted — удаление уже состоялось, это УСПЕХ;
    //  • Modified — чужое удаление под нашей правкой: воскрешать строку нельзя, а остальные
    //    изменения пакета (например, вставка новой записи при сдвиге позиций соседей, которых
    //    сосед успел удалить) обязаны доехать. Раньше здесь бросалось наверх, и диалоги без
    //    фильтра DbErrors роняли приложение (PropertyEditorDialog.TrySave в crash.log). Теперь
    //    человеку сообщает хук PhantomRowsDropped, а поллер чужих правок через пару секунд
    //    уберет удаленное и из нашего UI.
    // Критично не оставить «фантом» в трекере: иначе он повисает и роняет КАЖДЫЙ следующий
    // (даже несвязанный) SaveChanges — отсюда каскад ошибок в диагностике на создание/удаление групп.
    private int SaveReconcilingPhantomDeletes(Func<int> save)
    {
        var droppedEdits = 0;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var n = save();
                if (droppedEdits > 0)
                {
                    Log.Warning("Отброшено правок строк, удаленных другим пользователем: {Count} ({Db:l})",
                                droppedEdits, DisplayPath);
                    PhantomRowsDropped?.Invoke(droppedEdits);
                }
                return n;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < 5 && ex.Entries.Count > 0)
            {
                foreach (var e in ex.Entries)
                {
                    if (e.State != EntityState.Deleted) droppedEdits++;
                    e.State = EntityState.Detached;
                }
            }
        }
    }

    public override async System.Threading.Tasks.Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        System.Threading.CancellationToken cancellationToken = default)
    {
        StampNewRecordAudit();
        var n = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        NoteSaved();
        return n;
    }

    private void StampNewRecordAudit()
    {
        foreach (var entry in ChangeTracker.Entries<DbRecordEntity>())
        {
            if (entry.State != EntityState.Added) continue;
            var r = entry.Entity;
            if (string.IsNullOrEmpty(r.CreatedBy)) r.CreatedBy = Utils.CurrentUser.DisplayName;
            if (string.IsNullOrEmpty(r.UpdatedBy)) r.UpdatedBy = r.CreatedBy;
            if (string.IsNullOrEmpty(r.UpdatedAt))
                r.UpdatedAt = string.IsNullOrEmpty(r.CreatedAt) ? System.DateTime.Now.ToString("o") : r.CreatedAt;
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (IsPostgres)
        {
            options.UseNpgsql(_dbPath);
        }
        else
        {
            // Подключение — из SqliteGate: открывается под замком своего пула (обход гонки пула
            // Microsoft.Data.Sqlite, см. там). Закрывает его сам контекст.
            options.UseSqlite(SqliteGate.NewConnection(SqliteConnectionString(_dbPath)),
                              contextOwnsConnection: true,
                              o => o.CommandTimeout(WaitSeconds(_dbPath)));
        }
        // Один тип контекста обслуживает два провайдера: без своего ключа кеша EF отдал бы
        // модель первого открытого провайдера второму (у PG имена колонок lowercase — см.
        // ApplyLowercaseNames), что молча ломало бы SQL. Ключ учитывает IsPostgres.
        options.ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory,
                               ProviderModelCacheKeyFactory>();
        // Границы транзакций для шины своих изменений (см. NoteSaved).
        options.AddInterceptors(ChangeBusInterceptor.Instance);
    }

    /// <summary>
    /// Предел ожидания занятой базы, секунды. Один и тот же для трех независимых механизмов,
    /// иначе они дают неожиданную сумму:
    ///  • <c>CommandTimeout</c> EF — сколько Microsoft.Data.Sqlite РЕТРАИТ команду на SQLITE_BUSY;
    ///  • <c>Default Timeout</c> строки подключения — то же для <c>BeginTransaction</c>,
    ///    который CommandTimeout НЕ смотрит (замер: BEGIN IMMEDIATE ждал 33 с при
    ///    CommandTimeout=2 — держал дефолтные 30 с соединения);
    ///  • <c>PRAGMA busy_timeout</c> — ожидание внутри самого SQLite (см. ConnectionPragmas).
    /// Дефолт Microsoft.Data.Sqlite — 30 с, и это ощущалось как полное зависание окна: поток
    /// UI замирал на полминуты и только потом показывал «database is locked».
    ///
    /// Значение РАВНО busy_timeout, а не «чуть больше»: Microsoft.Data.Sqlite после каждого
    /// SQLITE_BUSY сверяет прошедшее время с CommandTimeout и, пока оно меньше, повторяет шаг —
    /// а шаг сам ждет полный busy_timeout внутри SQLite. С 18 с против 15 с это давало ДВА
    /// цикла ожидания: замер на стенде (чужой BEGIN IMMEDIATE, сетевой профиль) — 33,3 с до
    /// «database is locked». При равных значениях первый же отказ SQLite выходит наружу.
    /// </summary>
    private static int WaitSeconds(string path) => NetworkPath.IsNetwork(path) ? 15 : 10;

    /// <summary>Предел размера <c>-wal</c> локальной базы после чекпоинта (см. ApplyConnectionPragmas).</summary>
    public const long WalSizeLimitBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Закрытие контекста массовой правки ставит перенос ее журнала в файл — в фоне
    /// (<see cref="WalCheckpointer"/>): правка к этому моменту закоммичена и видна, а перенос
    /// (на широкой коллекции — 80 % времени правки) пользователь не ждет. Здесь, а не в каждом
    /// массовом пути (импорт, свод, дубль, перенос, удаление — их полтора десятка): контексты
    /// массового профиля живут под <c>using</c>, так что закрытие гарантировано.
    /// </summary>
    public override void Dispose()
    {
        CheckpointOnBulkClose();
        base.Dispose();
    }

    /// <summary>Асинхронное закрытие — та же пара к широкому окну чекпоинта, что и у
    /// <see cref="Dispose"/>: <c>await using</c> идет мимо него, и без этой ветки журнал
    /// массовой операции молча остался бы непернесенным.</summary>
    public override async ValueTask DisposeAsync()
    {
        CheckpointOnBulkClose();
        await base.DisposeAsync();
    }

    private void CheckpointOnBulkClose()
    {
        if (_bulkProfile || _deferCheckpoint) WalCheckpointer.Schedule(_dbPath, BulkCheckpointDelay);
    }

    // Сколько перенос журнала ждет после массовой правки: интерфейс успевает перечитать колонки и
    // первые строки, пока диск свободен (см. WalCheckpointer.Schedule с отсрочкой).
    private static readonly TimeSpan BulkCheckpointDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Чекпоинт WAL после массовой операции (импорт, свод, дубль, удаление) — best-effort, без
    /// ожидания читателей (PASSIVE): переносит в файл все, что можно, и вместе с
    /// <c>journal_size_limit</c> усекает журнал до лимита. Иначе после заливки 600 тыс. строк
    /// <c>-wal</c> лежит рядом с базой в размере всей операции до следующего удобного случая.
    /// Массовые пути зовут его не сами — это делает <see cref="Dispose"/>.
    /// PostgreSQL и сетевые пути (журнал DELETE) — no-op.
    /// </summary>
    public void CheckpointWal()
    {
        if (IsPostgres || NetworkPath.IsNetwork(_dbPath)) return;
        try { Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(PASSIVE);"); }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            // Занято — штатно, усечется при следующем чекпоинте. Остальное (ошибка ввода-вывода,
            // повреждение) — в журнал: -wal без чекпоинта растет, пока не кончится диск.
            if (!DbHealthMonitor.IsBusyError(ex))
                Log.Warning(ex, "Чекпоинт WAL не выполнен: {Path:l}", _dbPath);
        }
    }

    /// <summary>Путь для журнала диагностики: файл SQLite как есть, у PostgreSQL — без строки
    /// подключения (в ней пароль).</summary>
    public string DisplayPath => IsPostgres ? "PostgreSQL" : _dbPath;

    private static string SqliteConnectionString(string path) =>
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource     = path,
            DefaultTimeout = WaitSeconds(path),
        }.ToString();

    /// <summary>
    /// Сбросить пул подключений Microsoft.Data.Sqlite к файлу <paramref name="dbPath"/>.
    ///
    /// Пул держит открытые native-соединения по строке подключения и переиспользует их для
    /// каждого нового <c>new AppDbContext(path)</c> и каждого открытия соединения общего
    /// контекста. После перезагрузки файлового сервера хендлы SMB в этих соединениях мертвы,
    /// а SQLite на Windows при отказе <c>LockFileEx</c> отвечает SQLITE_BUSY — не IOERR: каждый
    /// запрос ждет busy_timeout и падает «database is locked», хотя сервер уже поднялся и свежее
    /// соединение работает. Так и повторное монтирование брало из пула тот же мертвый хендл.
    /// Вызывать при размонтировании, при потере базы и когда сетевая база ответила BUSY, а
    /// свежая проба монитора прошла (см. DbHealthMonitor). Занятые соединения пул не трогает —
    /// закроет их при возврате; для PostgreSQL — no-op.
    /// </summary>
    public static void ResetPool(string dbPath)
    {
        if (PgConnectionInfo.IsPostgresConnectionString(dbPath)) return;
        try
        {
            using var key = SqliteGate.NewConnection(SqliteConnectionString(dbPath));
            SqliteGate.ClearPool(key);
        }
        catch { /* best-effort: без сброса просто останется старое поведение */ }
    }

    /// <summary>
    /// Прагмы подключения — применяются при КАЖДОМ его открытии (подписка на StateChange,
    /// см. конструктор).
    ///
    /// Почему не разово на контекст, как было раньше. EF не держит соединение открытым: сразу
    /// после <c>ExecuteSqlRaw("PRAGMA …")</c> оно закрывается и уходит в пул Microsoft.Data.Sqlite
    /// (замер: <c>GetDbConnection().State == Closed</c>). Пока с файлом работает один контекст,
    /// пул отдает то же физическое соединение и прагмы «как бы» держатся. Но стоит рядом ожить
    /// второму контексту (фоновых у нас десятки — Task.Run в репозитории, автоочистки, поллеры),
    /// как первый получает из пула ДРУГОЕ соединение — с дефолтами SQLite. Замер на этой сборке:
    ///
    ///   ГЛАВНЫЙ до появления фонового:  busy_timeout=15000, cache_size=-65536
    ///   ГЛАВНЫЙ при живом фоновом:      busy_timeout=0,     cache_size=-2000
    ///
    /// busy_timeout=0 означает, что при чужом писателе запрос падает SQLITE_BUSY немедленно,
    /// не выждав ни секунды из заявленных 10–15. Именно так UI-поток ловил «database is locked»
    /// на обычном чтении (SidebarTree → GetAllGroups).
    ///
    /// journal_mode здесь НЕ ставим — он персистится в заголовке файла
    /// (см. DatabaseManager.ApplyJournalMode).
    ///
    /// Сетевой путь (SMB/NFS, см. NetworkPath) — другой профиль надежности: БД общая,
    /// с ней работают несколько пользователей с разных машин.
    ///  • busy_timeout 15 с — чужие писатели по сети держат замок дольше;
    ///  • synchronous=FULL — там журнал DELETE (не WAL), а NORMAL в rollback-режиме
    ///    допускает порчу при сбое питания сервера;
    ///  • mmap_size=0 — mmap поверх сетевой ФС прямо не рекомендован SQLite
    ///    (когерентность кеша страниц между машинами не гарантируется).
    /// foreign_keys включаем явно: каскадное удаление (записи→значения, коллекция→поля/настройки)
    /// держится на FK ON DELETE CASCADE, а оно работает только при включенном enforcement.
    ///
    /// journal_size_limit (только локально, где журнал WAL): после массовой операции (свод на
    /// 600 тыс. строк, импорт, удаление коллекции) файл <c>-wal</c> оставался сотнями мегабайт —
    /// SQLite после чекпоинта не усекает его, а переиспользует. Лимит усекает журнал до 64 МБ
    /// на ближайшем чекпоинте (это делает любое подключение, лимит на подключение — поэтому он
    /// здесь, а не разово). На сетевом пути журнал DELETE, лимит ни на что не влияет.
    /// </summary>
    private void ApplyConnectionPragmas(DbConnection conn)
    {
        var shared = NetworkPath.IsNetwork(_dbPath);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                  "PRAGMA foreign_keys=ON;"
                + (shared ? "PRAGMA busy_timeout=15000;" : "PRAGMA busy_timeout=10000;")
                + (shared ? "PRAGMA synchronous=FULL;"   : "PRAGMA synchronous=NORMAL;")
                + (shared ? "" : $"PRAGMA journal_size_limit={WalSizeLimitBytes};")
                // Автоперенос журнала выключен: в тело файла его переносит фоновый WalCheckpointer —
                // по размеру журнала (раз в секунду, порог journal_size_limit) и после массовых
                // правок (закрытие их контекста). Автоперенос SQLite идет внутри коммита той
                // правки, которой выпало перевалить порог: после дубля столбца на 100 тыс. записей
                // (журнал 400 МБ) его сделала мелкая правка интерфейса — 17 с на UI-потоке. Порог
                // во весь дозволенный размер журнала — прежнее окно массового профиля: повторные
                // правки одной страницы схлопываются в журнале и переносятся один раз (перенос
                // после каждого пакета давал на удалении коллекции в 650 тыс. записей ~5-6 ГБ
                // физической записи на 0,5 ГБ данных). Транзакционных гарантий это не меняет —
                // только момент и поток переноса.
                + (shared ? "" : "PRAGMA wal_autocheckpoint=0;")
                + "PRAGMA temp_store=MEMORY;"
                // Объем — по профилю ЭТОГО контекста И по памяти машины (см. VolumeBudget).
                // Без mmap чтения страниц индексов идут pread-syscall'ами (импорт 425k в БД
                // с большими коллекциями — минуты вместо секунд: индексы db_field_values
                // глобальные).
                + (shared ? "PRAGMA mmap_size=0;" : $"PRAGMA mmap_size={MmapBytes(_bulkProfile)};")
                + $"PRAGMA cache_size=-{CacheKib(_bulkProfile)};";
            cmd.ExecuteNonQuery();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            // Прагмы — не повод не дать открыть соединение: без них работа просто менее
            // терпима к конкуренции. Реальная ошибка все равно вылезет на первом запросе.
            // Но след нужен (один на файл за сеанс — соединений открывается сотни): база
            // работает с настройками SQLite по умолчанию, и «database is locked» без ожидания
            // у одного из пользователей иначе не с чем связать.
            if (Diag.Once("pragmas:" + _dbPath))
                Log.Warning(ex, "Прагмы подключения не применились: {Path:l} — база работает с настройками SQLite по умолчанию", _dbPath);
        }
    }

    // ── Объем кеша и mmap по памяти МАШИНЫ ────────────────────────────────────
    //
    // Раньше размеры были константами: 64 МБ страничного кеша (512 МБ на массовых операциях)
    // и 256 МБ mmap (2 ГБ на массовых) на КАЖДОЕ подключение — одинаково на рабочей станции
    // с 32 ГБ и на ноутбуке с 8 ГБ. А подключений в сеансе не одно: свой контекст у каждой
    // смонтированной базы, у поллера на каждую базу, у каждой фоновой операции. На машине с
    // 8 ГБ, где память и без того занята на три четверти, это и давало «программа съела
    // 1,2-1,4 ГБ и все встало»: замер удаления коллекции на 650 тыс. записей показал пик
    // 1,67 ГБ у SQLite против 145 МБ у PostgreSQL, который те же данные удаляет на сервере.
    //
    // Теперь бюджет считается от объема памяти машины: доля от него, но не больше прежних
    // значений (на большой машине поведение ровно прежнее) и не меньше разумного минимума —
    // ниже него SQLite деградирует в случайный дисковый ввод-вывод на глобальных индексах.
    //
    // TotalAvailableMemoryBytes — сколько памяти отдано под кучу этого процесса: физическая
    // память по отчету ОС (она на проценты ниже номинала — часть забирают прошивка и ядро),
    // а в контейнере — предел .NET, около трех четвертей лимита cgroup. Это НЕ «свободно
    // сейчас»: свободное меняется каждую секунду, и настраивать по нему прагмы, которые живут
    // все время подключения, было бы гаданием.

    private static readonly long MachineMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <summary>Страничный кеш подключения в КиБ (для PRAGMA cache_size со знаком минус).
    /// Обычный профиль — 1/512 памяти машины, массовый — 1/64; потолок у обоих прежний
    /// (64 и 512 МБ), пол — 16 и 64 МБ. То есть на 32 ГБ значения ровно прежние, на 8 ГБ
    /// вчетверо меньше: 16 МБ обычный и 128 МБ массовый.</summary>
    private static long CacheKib(bool bulk)
    {
        var share = bulk ? MachineMemoryBytes / 64 : MachineMemoryBytes / 512;
        var (min, max) = bulk ? (64L << 20, 512L << 20) : (16L << 20, 64L << 20);
        return Math.Clamp(share, min, max) / 1024;
    }

    /// <summary>Окно mmap подключения в байтах. Страницы mmap — чистые файловые, ОС вытесняет
    /// их без свопа, поэтому доля щедрее кеша: 1/16 памяти машины в массовом профиле и 1/128
    /// в обычном. На 32 ГБ — прежние 2 ГБ и 256 МБ, на 8 ГБ — 512 и 64 МБ.</summary>
    private static long MmapBytes(bool bulk)
    {
        var share = bulk ? MachineMemoryBytes / 16 : MachineMemoryBytes / 128;
        var (min, max) = bulk ? (256L << 20, 2L << 30) : (64L << 20, 256L << 20);
        return Math.Clamp(share, min, max);
    }

    /// <summary>
    /// Обычный профиль объема для этого контекста (кеш до ~64 МБ, см. <see cref="CacheKib"/>).
    /// Вызов остается точкой, где код заявляет «работаю с этой базой всерьез»; сами прагмы
    /// теперь ставятся на каждом открытии соединения, поэтому переоткрытие мимо пула их
    /// больше не теряет.
    /// </summary>
    public void ApplyPerformancePragmas() => SetVolumeProfile(bulk: false);

    /// <summary>
    /// Профиль прагм для МАССОВЫХ операций (дубль/перенос/удаление/импорт миллионов строк) на
    /// изолированном фоновом подключении: кеш до ~512 МБ и mmap до 2 ГБ. Глобальные индексы
    /// (db_field_values, calendar_index) на больших БД (десятки ГБ) не влезают в стандартные
    /// 64 МБ — каждая вставка/удаление строки деградирует в случайный дисковый ввод-вывод
    /// (замер на стенде 10 млн записей: дубль коллекции шел ~35 мин). Только для
    /// короткоживущих фоновых подключений — основному контексту держать сотни МБ кеша весь
    /// сеанс ни к чему (те же значения использует генератор стенда).
    /// На сетевом пути mmap остается выключенным — поднимается только страничный кеш.
    /// </summary>
    public void ApplyBulkPerformancePragmas() => SetVolumeProfile(bulk: true);

    /// <summary>
    /// Поставить перенос журнала этой правки в фон при закрытии контекста
    /// (<see cref="WalCheckpointer"/>) — как у массового профиля, но без его объемов памяти. Для
    /// правок, которых ждут в окне прогресса (столбцы, диапазоны ячеек): на широкой коллекции
    /// перенос журнала — 80 % их времени, а правка надежна уже после коммита.
    /// </summary>
    public void DeferCheckpoint()
    {
        if (IsPostgres || NetworkPath.IsNetwork(_dbPath)) return;
        _deferCheckpoint = true;
    }

    // Профиль запоминается на контексте: соединение может закрываться и открываться заново
    // между операциями, и объемные прагмы обязаны восстановиться вместе с ним. Если соединение
    // уже открыто — применяем немедленно, иначе профиль подхватит ближайшее открытие.
    private void SetVolumeProfile(bool bulk)
    {
        // PostgreSQL: PRAGMA не существует, тюнинг (shared_buffers, work_mem) — забота сервера;
        // FK enforcement и MVCC включены всегда. Один guard нейтрализует все вызовы из репозиториев.
        if (IsPostgres) return;

        _bulkProfile = bulk;
        var conn = Database.GetDbConnection();
        if (conn.State == System.Data.ConnectionState.Open) ApplyConnectionPragmas(conn);
    }

    /// <summary>
    /// Прагмы для РАЗОВЫХ миграций схемы — вызывать ДО <c>Database.Migrate()</c>, когда есть
    /// ожидающие миграции. Table-rebuild миграции (пример: SchemaCompact) перекладывают миллионы
    /// строк, а с дефолтным кешем SQLite ~2 МБ и без mmap это часы вместо минут. Отличия от
    /// <see cref="ApplyBulkPerformancePragmas"/>: <c>foreign_keys</c> НЕ включаем (дефолтный OFF —
    /// перекладка идет без FK-проверки на каждую строку, сами объявления FK копируются в DDL);
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
        // Объемы — тот же бюджет по памяти машины, что у массового профиля (см. CacheKib):
        // на ноутбуке с 8 ГБ разовая миграция не должна забирать под кеш полгигабайта.
        // EF1002 «возможная SQL-инъекция»: в строку подставляются только числа, посчитанные
        // здесь же из объема памяти машины. Параметры в PRAGMA не поддерживаются.
        #pragma warning disable EF1002
        if (!shared) Database.ExecuteSqlRaw($"PRAGMA mmap_size={MmapBytes(bulk: true)};");
        Database.ExecuteSqlRaw($"PRAGMA cache_size=-{CacheKib(bulk: true)};");
        #pragma warning restore EF1002
        Database.ExecuteSqlRaw("PRAGMA threads=4;");   // воркеры сортировщика (no-op, если сборка SQLite без них)
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDbFunction(typeof(SqlText).GetMethod(nameof(SqlText.ContainsIgnoreCase))!)
             .HasName(SqlText.ContainsName);

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
            // Фильтр по значению поля (Value IN ...) и подсчет уникальных значений (GROUP BY Value)
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

        model.Entity<DbRecordLinkEntity>(e =>
        {
            e.ToTable("db_record_links");
            e.HasKey(x => x.Id);
            // Чипы ячейки: связи одного поля одной записи в порядке Position (горячий путь показа).
            e.HasIndex(x => new { x.FieldId, x.FromRecordId, x.Position });
            // Очистка при удалении цели В ЭТОЙ ЖЕ БД (TargetDbId=0): кто ссылается на запись.
            e.HasIndex(x => x.ToRecordId);
            // Не даем задвоить связь; TargetDbId в ключе различает одинаковые id в разных целевых БД.
            e.HasIndex(x => new { x.FieldId, x.FromRecordId, x.TargetDbId, x.ToRecordId }).IsUnique();
            // Каскады на поле-связь и запись-источник (всегда в этой же БД). FK на ToRecordId НЕТ:
            // цель может быть в другом файле (межбазовая связь), а FK между файлами невозможен —
            // удаление цели в этой же БД (TargetDbId=0) чистит прикладной код в путях удаления.
            e.HasOne<DbFieldEntity>().WithMany()
             .HasForeignKey(x => x.FieldId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<DbRecordEntity>().WithMany()
             .HasForeignKey(x => x.FromRecordId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<CalendarIndexEntity>(e =>
        {
            e.ToTable("calendar_index");
            e.HasKey(x => new { x.RecordId, x.FieldId });
            // Горячий путь календаря: WHERE CollectionId IN(...) AND StartIso < end AND EndIso >= start.
            e.HasIndex(x => new { x.CollectionId, x.StartIso });
            // Сплит окна на два под-запроса по AllDay: короткие timed — с нижней границей StartIso,
            // многодневные/all-day — без нее (их мало). Индекс изолирует каждый класс, чтобы
            // под-запрос не сканировал все от начала. См. ConstructorRepository.ReadCalendarEvents.
            // Хвост (RecordId, FieldId) дает полный порядок (StartIso, RecordId, FieldId) — на нем
            // «Расписание» согласует offset-прыжки и keyset-скролл (одинаковый порядок → без дублей/
            // пропусков на границах страниц с равным StartIso), а глубокий offset идет по индексу без
            // временной сортировки. Прежний префикс (CollectionId, AllDay, StartIso) остается рабочим
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

        model.Entity<CellStyleEntity>(e =>
        {
            e.ToTable("cell_style");
            e.HasKey(x => new { x.CollectionId, x.RecordId, x.Key });
            // Страница таблицы спрашивает цвета сразу за 200 записей одной коллекции —
            // префикс PK (CollectionId, RecordId) закрывает этот запрос сиком.
            e.HasOne(x => x.Record)
             .WithMany()
             .HasForeignKey(x => x.RecordId)
             .OnDelete(DeleteBehavior.Cascade);
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
            // Чтение идет keyset-пагинацией по Id (rowid) DESC — индексы не нужны.
        });

        model.Entity<TaskEventEntity>(e =>
        {
            e.ToTable("task_events");
            e.HasKey(x => x.Id);
            // Без FK по тем же причинам, что у журнала: уведомление «вам назначена задача»
            // должно доехать, даже если запись успели удалить, а каскад по db_records
            // молча уносил бы очередь.
            //
            // Горячий путь опроса — диапазон по PK (Id > курсор), обычно пустой. Индекс по
            // получателю нужен второму сценарию: человек не заходил неделю, в очереди
            // накопились чужие события, и «мои после курсора» надо взять индексом, а не
            // фильтром поверх всего хвоста.
            e.HasIndex(x => new { x.RecipientKey, x.Id });
        });

        if (IsPostgres) ApplyLowercaseNames(model);
    }

    /// <summary>
    /// PG-модель: все имена колонок в lowercase (таблицы уже lowercase). PostgreSQL сворачивает
    /// неэкранированные идентификаторы к нижнему регистру — благодаря этому весь общий raw SQL
    /// репозиториев (<c>WHERE CollectionId = @c</c> и т.п.) работает на обоих провайдерах без
    /// правок: SQLite матчит имена без учета регистра, PG находит колонку <c>collectionid</c>.
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
