using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Serilog;
using Serilog.Events;
using SmartBase.Database.Repositories;
using SmartBase.Logging;

namespace SmartBase.Database;

/// <summary>
/// Открытая база данных: контекст + репозитории + путь к файлу.
/// Сюда вынесена инициализация из App (миграции, бэкафилл, сидинг), чтобы один и тот же
/// конвейер применялся к каждому открываемому файлу. Пока — только провайдер SQLite;
/// ветка под PostgreSQL добавится здесь же в будущем без переписывания вызывающего кода.
/// </summary>
public sealed partial class DatabaseManager : IDisposable
{
    // Идентичность смонтированной БД из реестра: DbId стабилен между запусками
    // и служит «префиксом» для изоляции id при мульти-маунте (Этап 3).
    public int                   DbId        { get; }
    // Имя изменяемо: переименование в окне настроек базы обновляет смонтированную БД
    // на месте (MainWindow), без ремаунта — сайдбары перечитают его в SyncMountedToUi.
    public string                Name        { get; set; }
    // Для SQLite — путь к файлу; для PostgreSQL — строка подключения Npgsql
    // (фоновые контексты открываются по ней же: new AppDbContext(db.DbPath)).
    public string                Path        { get; }
    // Провайдер записи реестра: "sqlite" | "postgres".
    public string                Provider    { get; }
    // Общая БД нескольких пользователей: файл на сетевом пути (SMB/NFS/внешний том —
    // журнал DELETE вместо WAL, консервативные прагмы) или серверная PostgreSQL
    // (многопользовательская по природе). Влияет на мониторинг и тексты сообщений.
    public bool                  IsShared    { get; }
    public AppDbContext          Context     { get; }
    public CategoryRepository    Categories  { get; }
    public ConstructorRepository Constructor { get; }
    public PermissionService     Permissions { get; }

    /// <summary>Режим журнала SQLite после монтирования («wal» локально, «delete» на сетевом
    /// пути); у PostgreSQL — пусто. Расхождение с ожидаемым пишется в журнал диагностики.</summary>
    public string   JournalMode   { get; private set; } = "";
    /// <summary>Сколько заняло монтирование целиком: миграции, сжатие, индексация календаря,
    /// регистрация пользователя. Дольше <see cref="Diag.Mount"/> — предупреждение в журнале.</summary>
    public TimeSpan MountDuration { get; private set; }

    // Фоновые чистки, запущенные при монтировании (автоочистка журнала и очереди задач).
    // Их обязан дождаться Dispose — см. WaitMaintenance.
    private readonly List<Task> _maintenance = [];

    // Что сделали миграции при монтировании — для записи об открытии базы.
    private readonly record struct MigrationReport(string From, string To, int Count, TimeSpan Elapsed);

    private DatabaseManager(int dbId, string name, string path, string provider, Action<string, double?>? stage)
    {
        DbId     = dbId;
        Name     = name;
        Path     = path;
        Provider = provider;

        var watch = Stopwatch.StartNew();
        MigrationReport? migrated = null;

        if (provider == "postgres")
        {
            // Серверная БД: журнал/прагмы/VACUUM — забота сервера (MVCC, autovacuum).
            // Схему внутри уже созданной администратором базы строит Migrate() —
            // набор миграций PgAppDbContext (Database/Migrations/Pg). Замок миграций
            // на PG — advisory-lock сессии: смерть убитого процесса снимает его сама,
            // аналог ClearStaleMigrationsLock не нужен.
            IsShared = true;
            Context  = new PgAppDbContext(path);
            stage?.Invoke($"Подключаюсь к серверу «{name}»…", null);
            var pending = Context.Database.GetPendingMigrations().ToList();
            var t       = Stopwatch.StartNew();
            Context.Database.Migrate();
            if (pending.Count > 0)
                migrated = new MigrationReport(
                    Context.Database.GetAppliedMigrations().Where(m => !pending.Contains(m)).LastOrDefault() ?? "(пусто)",
                    pending[^1], pending.Count, t.Elapsed);
        }
        else
        {
            IsShared = NetworkPath.IsNetwork(path);
            Context  = new AppDbContext(path);

            // Все изменения схемы применяются автоматически через EF Core Migrations.
            // Любая будущая правка сущностей → `dotnet ef migrations add ...` →
            // при следующем запуске Migrate() накатит ее, не трогая существующие данные.
            migrated = ApplySchemaMigrations(Context, path, name, stage);

            // Режим журнала — по типу пути (WAL локально / DELETE на сети, см. ApplyJournalMode).
            JournalMode = ApplyJournalMode(Context, IsShared);

            // Настройки скорости под большие коллекции (100k–10M строк). В отличие от WAL
            // (живет в заголовке файла) эти прагмы — на соединение, и теперь вызов лишь
            // ЗАПОМИНАЕТ профиль: сами прагмы ставятся при каждом открытии соединения
            // (см. AppDbContext.ApplyConnectionPragmas). Раньше их ставили разово, и они
            // молча терялись, как только пул отдавал контексту другое соединение —
            // главный контекст оставался с busy_timeout=0 и кешем в 2 МБ.
            TunePerformancePragmas(Context);
            VerifyConnectionPragmas();

            // Самолечение раздутого файла — ТОЛЬКО локально. SQLite без auto_vacuum не возвращает
            // освобожденные страницы в ОС: после «загрузил свод заново, старый удалил» половина
            // файла — мертвые страницы (прежний порог >50 % ровно на этом случае и не
            // срабатывал). Порог теперь: свободно > 64 МБ И (≥ 25 % файла ИЛИ ≥ 512 МБ), см.
            // NeedsCompaction. С этапом и живым прогрессом на сплэше.
            //
            // Общая база (сетевая папка) здесь НЕ сжимается никогда: VACUUM переписывает весь файл
            // через сеть под эксклюзивным замком, и первый смонтировавший заставил бы остальных
            // двадцать человек минуты смотреть на «database is locked». Для нее — ручное
            // «Сжать файл» администратора (CompactFile) и резервная копия (BackupTo).
            if (!IsShared)
                CompactWithProgress(Context, path, name, stage,
                                    minFreeBytes: CompactMinFreeBytes, minFreeRatio: CompactMinFreeRatio,
                                    alwaysAboveBytes: CompactAlwaysAboveBytes);
        }

        // Пользователи/права этой базы: регистрируем текущего пользователя (первый
        // открывший базу без админов становится администратором) и строим кеш прав.
        // Репозиторий получает сервис прав — его мутаторы проверяют права первой строкой.
        Permissions = new PermissionService(Context);
        Permissions.EnsureCurrentUserRegistered();

        Categories  = new CategoryRepository(Context);
        Constructor = new ConstructorRepository(Context) { Permissions = Permissions };

        // Незавершенный generic-перенос (kill посреди пакетной заливки из БД другого
        // провайдера) оставляет частичную копию под маркером crossmove.pending.* —
        // доудаляем ее при монтировании (источник переносом не тронут).
        Constructor.SweepUnfinishedCrossMove();

        // Системное поле «Дата» → тип date + источник календаря; бэкафилл StartDt
        // для записей, у которых дата задана только в поле конструктора. На старых больших
        // базах первое построение проекции календаря — минуты: репортим этап и процент по
        // обработанным строкам (total=0 — тяжелая фаза началась, объем еще неизвестен).
        var calendarWatch = Stopwatch.StartNew();
        Constructor.MigrateAndBackfillCalendarDates((done, total) => stage?.Invoke(
            total > 0
                ? $"Индексирую календарь «{name}»\n{done:N0} из {total:N0}"
                : $"Индексирую календарь «{name}»…",
            total > 0 ? Math.Min(0.99, done / (double)total) : null));
        var calendar = calendarWatch.Elapsed;

        // Разметка ролей разделов: поля с двумя ролями сразу, лишние исполнители (раньше их
        // могло быть несколько на коллекцию) и роли, оставшиеся на неподходящем типе. Запреты
        // нового кода такие базы сами не лечат — нужна разовая починка при монтировании
        // (обычная база платит два индексных запроса и ни одной записи). Идет ПОСЛЕ миграции
        // календарных дат: та переводит legacy-«Дату» из text в date, и до нее починка сняла
        // бы с такого поля роль как с несовместимого.
        Constructor.RepairTaskFieldRoles();

        // Автоочистка журнала по возрасту (политика в db_meta, задается администратором):
        // в фоне на свежем подключении — старт не блокируется.
        _maintenance.Add(
            Constructor.ApplyChangeLogRetention(Permissions.RetentionEnabled, Permissions.RetentionMonths));

        // Очередь уведомлений по задачам: события старше квартала доставлять уже некому и
        // незачем. Тоже в фоне и тоже best-effort.
        _maintenance.Add(Constructor.ApplyTaskEventsRetention());

        // NULL в «Названии» записей старых файлов — разовый ремонт в фоне (см. RepairNullTitles).
        if (provider != "postgres") _maintenance.Add(RepairNullTitles(Context));

        Categories.SeedDefaults();

        // Сведения о файле для «Управления базой данных» — снимаем здесь, на потоке монтирования.
        if (provider != "postgres") ReadStorage();

        MountDuration = watch.Elapsed;
        LogMounted(migrated, calendar);
    }

    // Одна запись на монтирование — история работы с базами в журнале диагностики: где лежит,
    // в каком режиме журнала, какого размера, что сделали миграции и сколько все заняло.
    // Дольше порога Diag.Mount — предупреждение: медленное монтирование видно до жалобы.
    private void LogMounted(MigrationReport? migrated, TimeSpan calendar)
    {
        var where = Provider == "postgres" ? "сервер PostgreSQL"
                  : IsShared               ? $"сетевой путь {Path}"
                                           : $"локальный путь {Path}";
        var journal = JournalMode.Length > 0 ? $", журнал {JournalMode}" : "";
        var file    = Storage is { } s ? $", файл {Diag.Size(s.FileBytes)} (свободно {s.FreeRatio * 100:N0} %)" : "";
        var extra   = (migrated is { } m ? $", миграции {m.From} → {m.To} ({m.Count}) за {Diag.Duration(m.Elapsed)}" : "")
                    + (calendar > TimeSpan.FromSeconds(2) ? $", индексация календаря {Diag.Duration(calendar)}" : "");
        var level   = MountDuration > Diag.Mount ? LogEventLevel.Warning : LogEventLevel.Information;
        Log.Write(level, "База «{Name:l}» открыта за {Ms:N0} мс: {Provider:l}, {Where:l}{Journal:l}{File:l}{Extra:l}",
                  Name, MountDuration.TotalMilliseconds, Provider, where, journal, file, extra);
    }

    // Контроль прагм подключения после монтирования: busy_timeout=0 или потерянный лимит WAL
    // означают, что профиль подключения не применился (см. AppDbContext.ApplyConnectionPragmas)
    // и база работает с настройками SQLite по умолчанию — это должно быть видно в журнале,
    // а не всплывать «database is locked» без ожидания у одного из двадцати пользователей.
    private void VerifyConnectionPragmas()
    {
        try
        {
            Context.Database.OpenConnection();
            try
            {
                using var cmd = Context.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "PRAGMA busy_timeout;";
                var busy = Convert.ToInt64(cmd.ExecuteScalar());
                cmd.CommandText = "PRAGMA journal_size_limit;";
                var limit = Convert.ToInt64(cmd.ExecuteScalar());
                if (busy <= 0)
                    Log.Warning("База «{Name:l}»: busy_timeout не применился ({Value}) — при чужом писателе запросы будут падать «database is locked» без ожидания",
                                Name, busy);
                if (!IsShared && limit != AppDbContext.WalSizeLimitBytes)
                    Log.Warning("База «{Name:l}»: лимит журнала WAL не применился ({Value}) — файл -wal может расти без усечения",
                                Name, limit);
            }
            finally
            {
                Context.Database.CloseConnection();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "База «{Name:l}»: не удалось проверить прагмы подключения", Name);
        }
    }

    /// <summary>Смонтировать БД с ее идентичностью из реестра (DbId, имя, путь/строка подключения).
    /// <paramref name="provider"/> — "sqlite" (по умолчанию) или "postgres" (тогда
    /// <paramref name="path"/> — строка подключения Npgsql, см. PgConnectionInfo).
    /// <paramref name="stage"/> — опциональный прогресс долгих этапов (разовая миграция/сжатие)
    /// для сплэша: текст + доля этапа 0..1 (null = доля неизвестна). Без него пользователь
    /// видит «зависший» прогресс десятки секунд.</summary>
    public static DatabaseManager Open(int dbId, string name, string path,
        string provider = "sqlite", Action<string, double?>? stage = null)
        => new(dbId, name, path, provider, stage);

    public void Dispose()
    {
        WaitMaintenance();
        Context.Dispose();
        // Закрыли базу — закрываем и пул подключений к ее файлу. Иначе простаивающие
        // соединения пула держат файл открытым (в WAL — с общим замком: другой процесс не
        // сможет перевести журнал в DELETE, стенд --check-pragmas ловил здесь «database is
        // locked»), а после потери сетевой базы повторное монтирование брало бы из пула
        // мертвый хендл (см. AppDbContext.ResetPool).
        if (Provider == "sqlite") AppDbContext.ResetPool(Path);
    }

    /// <summary>
    /// Дождаться фоновых чисток, запущенных при монтировании, прежде чем закрывать базу.
    ///
    /// Без этого Dispose врал: он возвращал управление, пока чистки еще держали файл своими
    /// подключениями. Замер до правки — из 12 открытий-закрытий файл оставался занят сразу
    /// после Dispose семь раз (освобождался в пределах ~700 мс). Настоящая цена не в уборке
    /// временных файлов, а в перемонтировании сразу после размонтирования — штатном сценарии
    /// DbHealthMonitor после «связь потеряна»: оно первым делом выполняет PRAGMA journal_mode,
    /// а она падает SQLITE_BUSY, пока файл держит другое соединение, и монтирование уходит
    /// на новый круг ретраев.
    ///
    /// Ожидание ограничено: чистки — best-effort (свой catch внутри), и застрявшая на
    /// недоступной сетевой шаре не должна вешать закрытие приложения. Вышли за предел —
    /// закрываемся как раньше: хуже, чем было, не станет.
    /// </summary>
    private void WaitMaintenance()
    {
        if (_maintenance.Count == 0) return;
        try { Task.WaitAll([.. _maintenance], MaintenanceWaitMs); }
        catch { /* сами задачи best-effort: их отказ уже проглочен внутри */ }
        _maintenance.Clear();
    }

    private const int MaintenanceWaitMs = 5_000;

    // Режим журнала (персистится в заголовке файла, идемпотентно):
    //  • локально — WAL: параллельные чтения во время записи + быстрые коммиты;
    //  • сетевой путь — DELETE: WAL требует когерентной разделяемой памяти (-shm),
    //    которой между машинами на SMB/NFS нет — одновременная работа с разных
    //    компьютеров в WAL молча портит файл. Файл, созданный локально, несет WAL
    //    в заголовке — при первом открытии с шары конвертируем (нужен эксклюзив;
    //    если его держит старая версия приложения в WAL — прагма упадет SQLITE_BUSY,
    //    монтирование честно покажет «недоступна», а не будет работать небезопасно).
    //    Смена режима требует эксклюзивного замка на файл, поэтому сначала читаем текущий и
    //    трогаем файл, только если режим другой: общую базу открывают десятки копий приложения,
    //    и каждая безусловная запись режима на ходу у соседей заканчивалась бы SQLITE_BUSY.
    private static string ApplyJournalMode(AppDbContext db, bool shared)
    {
        var want = shared ? "delete" : "wal";
        db.Database.OpenConnection();
        try
        {
            using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            var current = Convert.ToString(cmd.ExecuteScalar())?.ToLowerInvariant() ?? "";
            if (current == want) return current;
            cmd.CommandText = $"PRAGMA journal_mode={want.ToUpperInvariant()};";
            // PRAGMA journal_mode=X отвечает режимом, который ДЕЙСТВУЕТ: не смог переключить
            // (нет прав на файл, чужой замок) — вернет прежний. Для общей базы прежний WAL на
            // сетевом пути — риск порчи при сбое сервера, и молчать об этом нельзя.
            var result = Convert.ToString(cmd.ExecuteScalar())?.ToLowerInvariant() ?? "";
            if (result != want)
                Log.Warning("База {Path:l}: режим журнала {Mode:l} вместо {Want:l} — {Why:l}",
                            db.DbPath, result, want,
                            shared ? "WAL на сетевом пути не защищает от порчи при сбое сервера; проверьте права на файл"
                                   : "без WAL чтение и запись будут блокировать друг друга");
            return result;
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    // Разовый ремонт данных: в старых файлах встречаются записи с NULL в db_records.Title
    // (колонка объявлена NOT NULL DEFAULT '' с миграции AddRecordTitleAndDescription, но
    // файлы, созданные до нее иным путем, ограничения не несут). EF при чтении такой строки
    // в сущность падает «The data is NULL at ordinal» — так рушилось открытие панели записи
    // (crash.log). Проекции страниц терпят NULL сами (COALESCE), а здесь чиним источник:
    // один раз на файл (флаг в db_meta), в фоне на свежем подключении, и сначала ЧТЕНИЕМ —
    // UPDATE держал бы write-замок общей базы на весь скан таблицы, а NULL в ней, как
    // правило, нет.
    private const string TitleRepairKey = "repair.title-null";

    private static Task RepairNullTitles(AppDbContext db)
    {
        var path = db.DbPath;
        return Task.Run(() =>
        {
            try
            {
                using var ctx = new AppDbContext(path);
                ctx.ApplyPerformancePragmas();
                ctx.Database.OpenConnection();
                try
                {
                    var conn = ctx.Database.GetDbConnection();
                    if (ReadMetaLong(conn, TitleRepairKey) is not null) return;
                    using (var probe = conn.CreateCommand())
                    {
                        probe.CommandText = "SELECT 1 FROM db_records WHERE \"Title\" IS NULL LIMIT 1;";
                        if (probe.ExecuteScalar() is not null)
                        {
                            using var fix = conn.CreateCommand();
                            fix.CommandText = "UPDATE db_records SET \"Title\" = '' WHERE \"Title\" IS NULL;";
                            var n = fix.ExecuteNonQuery();
                            Log.Information("Ремонт «Название» в {Path:l}: пустых значений вместо NULL — {Count}", path, n);
                        }
                    }
                    using var mark = conn.CreateCommand();
                    mark.CommandText = "INSERT INTO db_meta (\"Key\", \"Value\") VALUES ('" + TitleRepairKey + "', '1') " +
                                       "ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = '1';";
                    mark.ExecuteNonQuery();
                }
                finally { ctx.Database.CloseConnection(); }
            }
            catch { /* best-effort: повторится при следующем монтировании */ }
        });
    }

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
    // и упадет на «таблица уже существует». Чтобы этого избежать, перед Migrate()
    // вручную создаем таблицу истории и фиксируем в ней InitialCreate как
    // «уже примененную». После этого Migrate() накатит только последующие миграции.
    private static MigrationReport? ApplySchemaMigrations(AppDbContext db, string path, string name, Action<string, double?>? stage)
    {
        var dbFileExists = File.Exists(path);

        // Случай 1: БД еще не существует — Migrate() сам создаст ее и применит все миграции по порядку.
        if (!dbFileExists)
        {
            var all   = db.Database.GetPendingMigrations().ToList();
            var fresh = Stopwatch.StartNew();
            db.Database.Migrate();
            return all.Count > 0 ? new MigrationReport("(новый файл)", all[^1], all.Count, fresh.Elapsed) : null;
        }

        // Случай 2: БД есть. Проверяем, есть ли в ней таблица __EFMigrationsHistory
        // и реальные таблицы схемы (например, db_records — создается InitialCreate).
        var hasMigrationsTable = HasMigrationsHistoryTable(db);
        var hasSchemaTables    = HasTable(db, "db_records");

        if (!hasMigrationsTable && hasSchemaTables)
        {
            // Старая БД из времен EnsureCreated. Считаем, что ее схема соответствует
            // InitialCreate, и помечаем эту миграцию как примененную — чтобы Migrate()
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

        // Протухший замок миграций от убитого процесса — иначе запуск виснет навечно.
        ClearStaleMigrationsLock(db);

        var pending = db.Database.GetPendingMigrations().ToList();

        // Для записи в журнал: откуда и куда двигаем схему и сколько это заняло.
        var from    = db.Database.GetAppliedMigrations().LastOrDefault() ?? "(пусто)";
        var to      = pending.Count > 0 ? pending[^1] : "";
        var planned = pending.Count;
        var watch   = Stopwatch.StartNew();

        // Тяжелая SchemaCompact (разовая перекладка миллионов строк) идет СОБСТВЕННЫМ
        // возобновляемым конвейером (партиал DatabaseManager.SchemaCompact.cs): порции с
        // чекпоинтами — выключение программы в любой момент теряет максимум последнюю
        // порцию, при следующем запуске перекладка продолжается с места остановки.
        // Одна EF-транзакция на все, как раньше, при убийстве процесса откатывалась
        // целиком — на больших базах обновление могло не завершиться никогда.
        if (pending.Contains(SchemaCompactId))
        {
            // Дешевые миграции ДО SchemaCompact (мосты со старых схем) — обычным путем.
            var idx = pending.IndexOf(SchemaCompactId);
            if (idx > 0)
                db.GetService<IMigrator>().Migrate(pending[idx - 1]);

            RunResumableSchemaCompact(db, path, name, stage);
            pending = db.Database.GetPendingMigrations().ToList();
        }

        // Остальные (дешевые) миграции — обычным Migrate().
        if (pending.Count > 0)
        {
            // Держим соединение открытым: прагмы, счетчик прогресса и Migrate()
            // гарантированно работают на ОДНОМ физическом подключении (а не через пул).
            db.Database.OpenConnection();
            try
            {
                db.ApplyMigrationPragmas();
                using (SqliteStageProgress.ForMigration(db, path, name, stage))
                    db.Database.Migrate();
            }
            finally
            {
                db.Database.CloseConnection();
            }
            SetNeedsCompact(db);
        }
        else
        {
            db.Database.Migrate();   // история актуальна — быстрый no-op
        }

        // Отложенное сжатие после миграций: пересоздания таблиц оставляют ~40% файла
        // свободными страницами — ниже порога планового самолечения (>50%), сам файл не
        // ужмется никогда. Флаг в db_meta переживает убийство процесса ВО ВРЕМЯ VACUUM
        // (сам VACUUM неделим): миграция уже зафиксирована, при следующем запуске
        // повторится только сжатие. Порог только абсолютный (minFreeRatio: 0).
        if (NeedsCompact(db))
        {
            CompactWithProgress(db, path, name, stage, minFreeBytes: 16L * 1024 * 1024, minFreeRatio: 0);
            ClearNeedsCompact(db);
        }

        return planned > 0 ? new MigrationReport(from, to, planned, watch.Elapsed) : null;
    }

    // ── Свободное место и сжатие ─────────────────────────────────────────────────

    /// <summary>Порог планового самолечения локальной базы при монтировании: свободных страниц
    /// больше 64 МБ И (не меньше четверти файла ИЛИ больше 512 МБ). Те же числа — подсказка
    /// «стоит сжать» у общей базы в «Управлении базой данных».</summary>
    public const long   CompactMinFreeBytes     = 64L * 1024 * 1024;
    public const double CompactMinFreeRatio     = 0.25;
    public const long   CompactAlwaysAboveBytes = 512L * 1024 * 1024;

    /// <summary>Файл стоит сжать: свободного места больше порога (см. константы выше).</summary>
    public static bool NeedsCompaction(long freeBytes, double freeRatio) =>
        freeBytes > CompactMinFreeBytes &&
        (freeRatio >= CompactMinFreeRatio || freeBytes >= CompactAlwaysAboveBytes);

    /// <summary>Что занимает файл SQLite-базы: размер файла, живые данные, свободные страницы,
    /// журнал рядом. У PostgreSQL — null.</summary>
    public sealed record StorageStats(long FileBytes, long LiveBytes, long FreeBytes, double FreeRatio, long WalBytes)
    {
        public bool NeedsCompaction => DatabaseManager.NeedsCompaction(FreeBytes, FreeRatio);
    }

    /// <summary>Сведения о файле, снятые при монтировании (<see cref="ReadStorage"/>). UI читает
    /// ЭТО, а не ходит в файл: список баз в настройках перестраивается на UI-потоке, а сетевая
    /// база в момент потери связи отвечала бы на PRAGMA только через busy_timeout.</summary>
    public StorageStats? Storage { get; private set; }

    /// <summary>Снять сведения о файле этой базы (три PRAGMA + размер файла; мгновенно) и
    /// обновить <see cref="Storage"/>. Вызывать с фонового потока или там, где база точно жива.</summary>
    public StorageStats? ReadStorage()
    {
        if (Provider != "sqlite") return null;
        try
        {
            var (freeBytes, freeRatio) = SqliteMaintenance.ReadFreeSpace(Context);
            var file = new FileInfo(Path).Length;
            var wal  = Len(Path + "-wal") + Len(Path + "-journal");
            return Storage = new StorageStats(file, Math.Max(0, file - freeBytes), freeBytes, freeRatio, wal);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось прочитать сведения о файле базы «{Name:l}»", Name);
            return null;
        }
    }

    private static long Len(string f) => File.Exists(f) ? new FileInfo(f).Length : 0;

    // Сжатие файла (VACUUM) с этапом и живым прогрессом на сплэше. Пороги: minFreeBytes —
    // абсолютный (ниже него не трогаем никогда), minFreeRatio — относительный (доля свободных
    // страниц; 0 — после миграций), alwaysAboveBytes — абсолютный порог, при котором доля уже
    // не важна (гигабайт мертвых страниц в 5-гигабайтном файле — это все равно гигабайт).
    // VACUUM переписывает живые данные целиком — на гигабайтных базах это минуты, и без этапа
    // с процентами выглядело зависанием (наблюдалось вживую на базе 5,8 ГБ).
    private static void CompactWithProgress(AppDbContext db, string path, string name,
        Action<string, double?>? stage, long minFreeBytes, double minFreeRatio,
        long alwaysAboveBytes = long.MaxValue)
    {
        var (freeBytes, freeRatio) = SqliteMaintenance.ReadFreeSpace(db);
        if (freeBytes <= minFreeBytes) return;
        if (freeRatio < minFreeRatio && freeBytes < alwaysAboveBytes) return;

        Vacuum(db, path, name, stage);
    }

    // ВАЖНО (Windows): физическое усечение файла после VACUUM в WAL-режиме делает чекпоинт, а
    // SetEndOfFile молча не срабатывает, пока файл отображен в память ЛЮБЫМ другим открытым
    // подключением — в том числе простаивающим в пуле Microsoft.Data.Sqlite: наш профиль ставит
    // mmap_size на каждое подключение, и такое подключение держит отображение до закрытия.
    // Итог без сброса пула: freelist пуст, page_count меньше, а файл на диске остается прежнего
    // размера навсегда (опыт: 39 МБ вместо 20 МБ даже после закрытия всех подключений; свое
    // отображение VACUUM снимает сам). Поэтому перед VACUUM пул к файлу закрывается; занятые
    // прямо сейчас подключения (фоновые чтения) он не трогает — вызывающий обязан обеспечить,
    // что их нет (монтирование: база еще никому не отдана; «Сжать файл»: база отцеплена).
    // На сетевом пути mmap выключен, и проблемы нет.
    private static void Vacuum(AppDbContext db, string path, string name, Action<string, double?>? stage)
    {
        var before    = new FileInfo(path).Length;
        var watch     = Stopwatch.StartNew();
        var liveBytes = Math.Max(1, before - SqliteMaintenance.ReadFreeSpace(db).FreeBytes);
        AppDbContext.ResetPool(path);
        db.Database.OpenConnection();
        try
        {
            using (SqliteStageProgress.ForVacuum(db, path, name, liveBytes, stage))
                db.Database.ExecuteSqlRaw("VACUUM;");
            // Усечение файла в WAL-режиме делает чекпоинт, а автоматический после коммита
            // VACUUM запускается только при журнале длиннее 1000 страниц: у сильно опустевшей
            // базы (свод удален целиком) он не срабатывает, и усечение откладывалось до чужого
            // чекпоинта — уже при живых mmap-подключениях, где оно молча не проходит (см. выше).
            // Делаем чекпоинт сами, пока файл никем не отображен. На DELETE-журнале — no-op.
            db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");
        }
        finally
        {
            db.Database.CloseConnection();
        }
        // Было → стало по файлу на диске (а не по page_count): именно размер на диске
        // молча не менялся, пока файл держало отображенным чужое подключение.
        Log.Information("База «{Name:l}» сжата: {Before:l} → {After:l} за {Ms:N0} мс ({Path:l})",
                        name, Diag.Size(before), Diag.Size(new FileInfo(path).Length),
                        watch.Elapsed.TotalMilliseconds, path);
    }

    /// <summary>
    /// Ручное сжатие файла (кнопка «Сжать файл» в «Управлении базой данных») — для общей базы,
    /// которую при монтировании не сжимаем. База должна быть ОТЦЕПЛЕНА от рабочего пространства
    /// (Workspace.Detach + Release), иначе ее собственные подключения и пул мешают VACUUM.
    /// Идет на свежем подключении с объемным профилем; прогресс — тем же stage-колбэком, что
    /// и на сплэше («Сжимаю файл «имя»» + «осталось ≈ N мин»). Занятый файл (чужой писатель,
    /// busy_timeout истек) — SqliteException «database is locked» наверх, файл цел: VACUUM
    /// транзакционен.
    /// </summary>
    public static void CompactFile(string path, string name, Action<string, double?>? stage)
    {
        AppDbContext.ResetPool(path);
        using var db = new AppDbContext(path);
        db.ApplyBulkPerformancePragmas();
        Vacuum(db, path, name, stage);
        AppDbContext.ResetPool(path);
    }

    /// <summary>
    /// Резервная копия: <c>VACUUM INTO</c> в файл <paramref name="targetPath"/> — компактная
    /// (без свободных страниц) и консистентная копия на момент начала; писателей не блокирует
    /// (держит только снимок чтения), базу отцеплять не нужно. Существующий файл-цель — ошибка
    /// (VACUUM INTO требует пустой/несуществующий файл). Прогресс — по росту файла-копии
    /// относительно живых данных источника. После копирования — <c>integrity_check</c> копии;
    /// сбой проверки — InvalidOperationException, битая копия удаляется.
    /// </summary>
    public static void BackupTo(string path, string name, string targetPath, Action<string, double?>? stage)
    {
        if (File.Exists(targetPath)) File.Delete(targetPath);   // VACUUM INTO не перезаписывает

        var watch = Stopwatch.StartNew();
        using var db = new AppDbContext(path);
        db.ApplyPerformancePragmas();
        var liveBytes = Math.Max(1, new FileInfo(path).Length - SqliteMaintenance.ReadFreeSpace(db).FreeBytes);

        using var timer = stage is null ? null : new System.Threading.Timer(_ =>
        {
            var done = Len(targetPath);
            var frac = Math.Min(0.99, done / (double)liveBytes);
            stage($"Копирую «{name}»\n{done / 1048576.0:N0} из ≈ {liveBytes / 1048576.0:N0} МБ", frac);
        }, null, 0, 500);

        // Путь — параметром: VACUUM INTO принимает выражение, апострофы в имени файла не ломают SQL.
        db.Database.ExecuteSqlRaw("VACUUM INTO {0};", targetPath);
        timer?.Dispose();

        stage?.Invoke($"Проверяю копию «{name}»…", null);
        using var check = new AppDbContext(targetPath);
        var conn = check.Database.GetDbConnection();
        conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            cmd.CommandTimeout = 24 * 3600;
            var result = cmd.ExecuteScalar()?.ToString();
            if (result != "ok")
            {
                conn.Close();
                AppDbContext.ResetPool(targetPath);
                try { File.Delete(targetPath); } catch { /* best-effort */ }
                throw new InvalidOperationException($"Копия не прошла проверку целостности: {result}");
            }
        }
        finally
        {
            if (conn.State == System.Data.ConnectionState.Open) conn.Close();
            AppDbContext.ResetPool(targetPath);
        }
        Log.Information("Резервная копия базы «{Name:l}» сохранена: {Target:l} ({Size:l}) за {Ms:N0} мс",
                        name, targetPath, Diag.Size(Len(targetPath)), watch.Elapsed.TotalMilliseconds);
    }

    // Замок миграций EF 9 (__EFMigrationsLock): Migrate() вставляет строку на время работы и
    // удаляет по завершении, а убитый посреди миграции процесс (kill, свет) оставляет ее
    // НАВСЕГДА — и каждый следующий запуск бесконечно ждет освобождения: тихо, без ошибки,
    // с нулевым CPU (наблюдалось вживую). Снимаем замок перед Migrate():
    //  • владелец мертв — DELETE проходит мгновенно (или строки нет — no-op);
    //  • владелец ЖИВ и прямо сейчас перекладывает данные — его транзакция держит write-lock
    //    файла, DELETE отваливается по busy_timeout, замок не трогаем: Migrate() дождется
    //    штатно. Возраст замка не критерий: типичный сценарий — «убил и тут же перезапустил»,
    //    замок при этом свежий. Кража возможна лишь в паузах живого мигратора (второй
    //    экземпляр приложения на том же файле) — тогда одна из миграций упадет с ошибкой DDL
    //    без порчи данных (миграции транзакционны), что несравнимо лучше вечного зависания.
    private static void ClearStaleMigrationsLock(AppDbContext db)
    {
        if (!HasTable(db, "__EFMigrationsLock")) return;
        try
        {
            db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=3000;");
            db.Database.ExecuteSqlRaw("DELETE FROM \"__EFMigrationsLock\";");
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // BUSY: замок держит живой мигратор — пусть Migrate() ждет его штатно.
        }
    }

    // ── Живой прогресс долгих этапов (разовая миграция, VACUUM) ────────────────────
    //
    // Готовых колбэков прогресса у EF/SQLite нет; складываем два честных сигнала, каждый
    // закрывает свою фазу работы (веса 50/50):
    //  • sqlite3_progress_handler (VM-шаги SQLite) — тикает при скане источника и подаче
    //    строк в сортировщик/копировании таблиц;
    //  • прирост журнала (-wal/-journal) относительно стартового размера — фаза записи
    //    результата (вставка, индексы), где VM-шаги редки. Стартовый размер вычитается:
    //    WAL-файл не ужимается после чекпоинта и мог остаться большим от прошлой работы.
    // Финальная внешняя сортировка — одна VM-инструкция, на ней пауза неизбежна у обоих
    // сигналов (полоса сплэша в это время «доползает» сама, см. ProgressIndicator).
    //
    // Вторая строка сплэша — живая оценка времени: до первых процентов — по объему работы
    // и калиброванной скорости (стенд 1,6 ГБ: ~30 с/ГБ миграция, ~10 с/ГБ сжатие), дальше —
    // пересчет по фактической скорости со сглаживанием (EMA), округление до минут
    // («осталось ≈ 2 мин» / «меньше минуты») — без ложной точности. Текст и доля уходят в
    // stage-колбэк раз в полсекунды; мгновенные этапы таймер не видит (первый тик 0,5 с).
    // Хук живет на текущем физическом соединении — вызывающий держит его открытым
    // (OpenConnection) на время этапа; Dispose снимает хук и таймер.
    private sealed class SqliteStageProgress : IDisposable
    {
        private readonly System.Threading.Timer _timer;
        private readonly SQLitePCL.sqlite3 _handle;
        private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
        private long   _steps;
        private double _emaRemaining;

        private double _lastFrac;
        private double _lastMoveAt;

        private SqliteStageProgress(AppDbContext db, string path, string phase, string context,
                                    string stallText, double expectedSteps, double expectedJournal,
                                    double initialEtaSeconds, Action<string, double?> stage)
        {
            _handle = ((Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection()).Handle!;

            // Каждые 100k VM-инструкций — инкремент; накладные расходы ничтожны (тысячи
            // вызовов на гигабайтный этап). Возврат 0 — «продолжать» (не-0 прервал бы запрос).
            SQLitePCL.raw.sqlite3_progress_handler(_handle, 100_000,
                _ => { Interlocked.Add(ref _steps, 100_000); return 0; }, null);

            var wal      = path + "-wal";
            var journal  = path + "-journal";
            var baseline = Len(wal) + Len(journal);

            _timer = new System.Threading.Timer(_ =>
            {
                var grown       = Math.Max(0, Len(wal) + Len(journal) - baseline);
                var stepsFrac   = Math.Min(1.0, Interlocked.Read(ref _steps) / expectedSteps);
                var journalFrac = Math.Min(1.0, grown / expectedJournal);
                var frac        = Math.Min(0.99, 0.5 * stepsFrac + 0.5 * journalFrac);

                // Детектор «немой» фазы: проценты не двигались > 5 с (обычно это внешняя
                // сортировка/запись — одна неделимая операция SQLite без сигналов прогресса).
                // Пишем в доп. строку, ЧЕМ заняты, — стоящий процент без объяснения
                // неотличим от зависания (на многогигабайтных базах фаза длится минуты).
                if (frac > _lastFrac + 0.001) { _lastFrac = frac; _lastMoveAt = _watch.Elapsed.TotalSeconds; }
                var reason = _watch.Elapsed.TotalSeconds - _lastMoveAt > 5 ? stallText : context;

                stage($"{phase}\n{reason}{EtaLine(frac, initialEtaSeconds)}", frac);
            }, null, dueTime: 0, period: 500);

            static long Len(string f) => File.Exists(f) ? new FileInfo(f).Length : 0;
        }

        // «осталось ≈ N мин» с первой секунды: до первых процентов — оценка по объему работы,
        // дальше — по фактической скорости (elapsed × (1−f)/f, сглажено EMA — цифра не
        // дергается, а на паузе сортировки медленно держится, не улетая).
        private string EtaLine(double frac, double initialEtaSeconds)
        {
            double remaining;
            if (frac < 0.03)
            {
                remaining = Math.Max(5, initialEtaSeconds - _watch.Elapsed.TotalSeconds);
            }
            else
            {
                var raw = _watch.Elapsed.TotalSeconds * (1 - frac) / frac;
                _emaRemaining = _emaRemaining <= 0 ? raw : 0.7 * _emaRemaining + 0.3 * raw;
                remaining = _emaRemaining;
            }
            return remaining < 50 ? "осталось меньше минуты"
                                  : $"осталось ≈ {Math.Ceiling(remaining / 60):0} мин";
        }

        /// <summary>Этап «обновление» (Migrate с ожидающими миграциями). В доп. строке — причина
        /// («миграция данных»), чтобы пользователь понимал, откуда взялась разовая долгая операция.</summary>
        public static SqliteStageProgress? ForMigration(AppDbContext db, string path, string name,
                                                        Action<string, double?>? stage)
        {
            if (stage is null) return null;
            var bytes = Math.Max(1.0, new FileInfo(path).Length);
            return new SqliteStageProgress(db, path,
                phase: $"Обновление «{name}»",
                context: "миграция данных · ",
                stallText: "миграция данных · сортировка · ",
                expectedSteps: bytes * 0.35, expectedJournal: bytes * 0.6,
                initialEtaSeconds: bytes / 1e9 * 30, stage);
        }

        /// <summary>Этап «сжатие файла» (VACUUM); liveBytes — живые данные (файл минус freelist).
        /// Ожидания меньше миграционных: VACUUM копирует страницы крупными блоками, VM-шагов и
        /// журнала на байт выходит в разы меньше (калибровка на стенде 0,9 ГБ живых данных).</summary>
        public static SqliteStageProgress? ForVacuum(AppDbContext db, string path, string name,
                                                     long liveBytes, Action<string, double?>? stage)
        {
            if (stage is null) return null;
            return new SqliteStageProgress(db, path,
                phase: $"Сжимаю файл «{name}»",
                context: "",
                stallText: "записываю файл · ",
                expectedSteps: liveBytes * 0.07, expectedJournal: liveBytes * 0.2,
                initialEtaSeconds: liveBytes / 1e9 * 10, stage);
        }

        public void Dispose()
        {
            _timer.Dispose();
            SQLitePCL.raw.sqlite3_progress_handler(_handle, 0, null!, null);
        }
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
