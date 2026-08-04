using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartBase.Database.Repositories;

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
    public string                Name        { get; }
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

    private DatabaseManager(int dbId, string name, string path, string provider, Action<string, double?>? stage)
    {
        DbId     = dbId;
        Name     = name;
        Path     = path;
        Provider = provider;

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
            Context.Database.Migrate();
        }
        else
        {
            IsShared = NetworkPath.IsNetwork(path);
            Context  = new AppDbContext(path);

            // Все изменения схемы применяются автоматически через EF Core Migrations.
            // Любая будущая правка сущностей → `dotnet ef migrations add ...` →
            // при следующем запуске Migrate() накатит её, не трогая существующие данные.
            ApplySchemaMigrations(Context, path, name, stage);

            // Режим журнала — по типу пути (WAL локально / DELETE на сети, см. ApplyJournalMode).
            ApplyJournalMode(Context, IsShared);

            // Настройки скорости под большие коллекции (100k–10M строк). В отличие от WAL
            // (живёт в заголовке файла) эти прагмы — на соединение; ставим на основном
            // соединении контекста, которое живёт весь сеанс работы с БД.
            TunePerformancePragmas(Context);

            // Самолечение раздутого файла: остатки массовых импортов/удалений — SQLite без
            // auto_vacuum не возвращает освобождённые страницы в ОС, файл копится в разы
            // больше данных. Порог: >50% файла свободно И >16 МБ. С этапом и живым прогрессом
            // на сплэше — разовый VACUUM большого файла не должен выглядеть зависанием.
            CompactWithProgress(Context, path, name, stage, minFreeBytes: 16L * 1024 * 1024, minFreeRatio: 0.5);
        }

        // Пользователи/права этой базы: регистрируем текущего пользователя (первый
        // открывший базу без админов становится администратором) и строим кеш прав.
        // Репозиторий получает сервис прав — его мутаторы проверяют права первой строкой.
        Permissions = new PermissionService(Context);
        Permissions.EnsureCurrentUserRegistered();

        Categories  = new CategoryRepository(Context);
        Constructor = new ConstructorRepository(Context) { Permissions = Permissions };

        // Незавершённый generic-перенос (kill посреди пакетной заливки из БД другого
        // провайдера) оставляет частичную копию под маркером crossmove.pending.* —
        // доудаляем её при монтировании (источник переносом не тронут).
        Constructor.SweepUnfinishedCrossMove();

        // Поля с двумя ролями задач сразу (наследие баз, открытых до запрета): исполнитель
        // и «Назначил» в одном FieldId затирали друг друга. Сам запрет старые базы не лечит —
        // разовая починка при монтировании (обычная база платит один запрос).
        Constructor.RepairDoubleRoleTaskFields();

        // Системное поле «Дата» → тип date + источник календаря; бэкафилл StartDt
        // для записей, у которых дата задана только в поле конструктора. На старых больших
        // базах первое построение проекции календаря — минуты: репортим этап и процент по
        // обработанным строкам (total=0 — тяжёлая фаза началась, объём ещё неизвестен).
        Constructor.MigrateAndBackfillCalendarDates((done, total) => stage?.Invoke(
            total > 0
                ? $"Индексирую календарь «{name}»\n{done:N0} из {total:N0}"
                : $"Индексирую календарь «{name}»…",
            total > 0 ? Math.Min(0.99, done / (double)total) : null));

        // Автоочистка журнала по возрасту (политика в db_meta, задаётся администратором):
        // в фоне на свежем подключении — старт не блокируется.
        Constructor.ApplyChangeLogRetention(Permissions.RetentionEnabled, Permissions.RetentionMonths);

        Categories.SeedDefaults();
    }

    /// <summary>Смонтировать БД с её идентичностью из реестра (DbId, имя, путь/строка подключения).
    /// <paramref name="provider"/> — "sqlite" (по умолчанию) или "postgres" (тогда
    /// <paramref name="path"/> — строка подключения Npgsql, см. PgConnectionInfo).
    /// <paramref name="stage"/> — опциональный прогресс долгих этапов (разовая миграция/сжатие)
    /// для сплэша: текст + доля этапа 0..1 (null = доля неизвестна). Без него пользователь
    /// видит «зависший» прогресс десятки секунд.</summary>
    public static DatabaseManager Open(int dbId, string name, string path,
        string provider = "sqlite", Action<string, double?>? stage = null)
        => new(dbId, name, path, provider, stage);

    public void Dispose() => Context.Dispose();

    // Режим журнала (персистится в заголовке файла, идемпотентно):
    //  • локально — WAL: параллельные чтения во время записи + быстрые коммиты;
    //  • сетевой путь — DELETE: WAL требует когерентной разделяемой памяти (-shm),
    //    которой между машинами на SMB/NFS нет — одновременная работа с разных
    //    компьютеров в WAL молча портит файл. Файл, созданный локально, несёт WAL
    //    в заголовке — при первом открытии с шары конвертируем (нужен эксклюзив;
    //    если его держит старая версия приложения в WAL — прагма упадёт SQLITE_BUSY,
    //    монтирование честно покажет «недоступна», а не будет работать небезопасно).
    private static void ApplyJournalMode(AppDbContext db, bool shared) =>
        db.Database.ExecuteSqlRaw(shared ? "PRAGMA journal_mode=DELETE;" : "PRAGMA journal_mode=WAL;");

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
    private static void ApplySchemaMigrations(AppDbContext db, string path, string name, Action<string, double?>? stage)
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

        // Протухший замок миграций от убитого процесса — иначе запуск виснет навечно.
        ClearStaleMigrationsLock(db);

        var pending = db.Database.GetPendingMigrations().ToList();

        // Тяжёлая SchemaCompact (разовая перекладка миллионов строк) идёт СОБСТВЕННЫМ
        // возобновляемым конвейером (партиал DatabaseManager.SchemaCompact.cs): порции с
        // чекпоинтами — выключение программы в любой момент теряет максимум последнюю
        // порцию, при следующем запуске перекладка продолжается с места остановки.
        // Одна EF-транзакция на всё, как раньше, при убийстве процесса откатывалась
        // целиком — на больших базах обновление могло не завершиться никогда.
        if (pending.Contains(SchemaCompactId))
        {
            // Дешёвые миграции ДО SchemaCompact (мосты со старых схем) — обычным путём.
            var idx = pending.IndexOf(SchemaCompactId);
            if (idx > 0)
                db.GetService<IMigrator>().Migrate(pending[idx - 1]);

            RunResumableSchemaCompact(db, path, name, stage);
            pending = db.Database.GetPendingMigrations().ToList();
        }

        // Остальные (дешёвые) миграции — обычным Migrate().
        if (pending.Count > 0)
        {
            // Держим соединение открытым: прагмы, счётчик прогресса и Migrate()
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
        // ужмётся никогда. Флаг в db_meta переживает убийство процесса ВО ВРЕМЯ VACUUM
        // (сам VACUUM неделим): миграция уже зафиксирована, при следующем запуске
        // повторится только сжатие. Порог только абсолютный (minFreeRatio: 0).
        if (NeedsCompact(db))
        {
            CompactWithProgress(db, path, name, stage, minFreeBytes: 16L * 1024 * 1024, minFreeRatio: 0);
            ClearNeedsCompact(db);
        }
    }

    // Сжатие файла (VACUUM) с этапом и живым прогрессом на сплэше. Пороги: minFreeBytes —
    // абсолютный, minFreeRatio — относительный (доля свободных страниц; 0.5 для планового
    // самолечения при обычном открытии, 0 — после миграций). VACUUM переписывает живые данные
    // целиком — на гигабайтных базах это минуты, и без этапа с процентами выглядело зависанием
    // (наблюдалось вживую на базе 5,8 ГБ).
    private static void CompactWithProgress(AppDbContext db, string path, string name,
        Action<string, double?>? stage, long minFreeBytes, double minFreeRatio)
    {
        var (freeBytes, freeRatio) = SqliteMaintenance.ReadFreeSpace(db);
        if (freeBytes <= minFreeBytes || freeRatio < minFreeRatio) return;

        var liveBytes = Math.Max(1, new FileInfo(path).Length - freeBytes);
        db.Database.OpenConnection();
        try
        {
            using (SqliteStageProgress.ForVacuum(db, path, name, liveBytes, stage))
                db.Database.ExecuteSqlRaw("VACUUM;");
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    // Замок миграций EF 9 (__EFMigrationsLock): Migrate() вставляет строку на время работы и
    // удаляет по завершении, а убитый посреди миграции процесс (kill, свет) оставляет её
    // НАВСЕГДА — и каждый следующий запуск бесконечно ждёт освобождения: тихо, без ошибки,
    // с нулевым CPU (наблюдалось вживую). Снимаем замок перед Migrate():
    //  • владелец мёртв — DELETE проходит мгновенно (или строки нет — no-op);
    //  • владелец ЖИВ и прямо сейчас перекладывает данные — его транзакция держит write-lock
    //    файла, DELETE отваливается по busy_timeout, замок не трогаем: Migrate() дождётся
    //    штатно. Возраст замка не критерий: типичный сценарий — «убил и тут же перезапустил»,
    //    замок при этом свежий. Кража возможна лишь в паузах живого мигратора (второй
    //    экземпляр приложения на том же файле) — тогда одна из миграций упадёт с ошибкой DDL
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
            // BUSY: замок держит живой мигратор — пусть Migrate() ждёт его штатно.
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
    // Вторая строка сплэша — живая оценка времени: до первых процентов — по объёму работы
    // и калиброванной скорости (стенд 1,6 ГБ: ~30 с/ГБ миграция, ~10 с/ГБ сжатие), дальше —
    // пересчёт по фактической скорости со сглаживанием (EMA), округление до минут
    // («осталось ≈ 2 мин» / «меньше минуты») — без ложной точности. Текст и доля уходят в
    // stage-колбэк раз в полсекунды; мгновенные этапы таймер не видит (первый тик 0,5 с).
    // Хук живёт на текущем физическом соединении — вызывающий держит его открытым
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

        // «осталось ≈ N мин» с первой секунды: до первых процентов — оценка по объёму работы,
        // дальше — по фактической скорости (elapsed × (1−f)/f, сглажено EMA — цифра не
        // дёргается, а на паузе сортировки медленно держится, не улетая).
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
