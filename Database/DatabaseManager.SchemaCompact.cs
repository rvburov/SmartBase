using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace SmartBase.Database;

/// <summary>
/// Возобновляемое выполнение тяжелой миграции SchemaCompact (см.
/// Migrations/20260718213811_SchemaCompact.cs — конечное состояние схемы задает ОНА,
/// здесь то же самое достигается порциями). Зачем: EF выполняет миграцию одной
/// транзакцией — на больших базах (16 ГБ — десятки минут) выключение программы
/// откатывало все, и обновление каждый раз начиналось с нуля. Здесь каждая порция
/// (~200 тыс. строк, секунды) коммитится отдельно: выключение в любой момент теряет
/// максимум последнюю порцию, при следующем запуске перекладка продолжается с места
/// остановки (точка возобновления — хвост новой таблицы, читается мгновенно по PK).
///
/// Конвейер — последовательность идемпотентных шагов, состояние определяется по самой
/// схеме (какие таблицы существуют и в каком формате), отдельного файла состояния нет:
///   1. слияние трех таблиц колонок в column_meta (мелкий шаг, одна транзакция);
///   2. db_field_values → WITHOUT ROWID: порционная перекладка + короткая атомарная
///      подмена (DROP + RENAME) + индекс;
///   3. calendar_index → WITHOUT ROWID: так же;
///   4. отметка миграции примененной в __EFMigrationsHistory (паттерн моста
///      InitialCreate) + флаг отложенного сжатия.
/// Прогресс честный: доля = переложено/всего, «осталось ≈ N мин» по скорости текущего
/// сеанса; после перезапуска процент СРАЗУ показывает достигнутое место.
/// </summary>
public sealed partial class DatabaseManager
{
    private const string SchemaCompactId = "20260718213811_SchemaCompact";

    // Размер порции: ~1–3 с работы — компромисс между потерей при выключении
    // (теряется максимум одна порция) и накладными расходами на коммиты.
    private const int ChunkRows = 200_000;

    private static void RunResumableSchemaCompact(AppDbContext db, string path, string name,
                                                  Action<string, double?>? stage)
    {
        db.Database.OpenConnection();
        try
        {
            db.ApplyMigrationPragmas();
            var conn = db.Database.GetDbConnection();

            // Одна монотонная шкала на все обновление: под-этапам отведены сегменты общего
            // прогресса (доли — по типичному соотношению объемов работ), иначе процент
            // «прыгал назад» на границах (копия 99% → индексы 6%) и выглядел сломанным.
            Action<string, double?> Seg(double from, double to) => (msg, frac) =>
                stage?.Invoke(msg, frac is null ? null : from + Math.Clamp(frac.Value, 0, 1) * (to - from));
            var segValuesCopy    = Seg(0.00, 0.70);   // самая большая таблица
            var segValuesIndex   = Seg(0.70, 0.87);
            var segCalendarCopy  = Seg(0.87, 0.89);
            var segCalendarIndex = Seg(0.89, 0.99);

            // ── 1) column_meta: слить три таблицы колонок (строк немного — одна транзакция).
            //     Наличие старых таблиц и есть признак «шаг еще не сделан».
            if (TableExists(conn, "column_order"))
                Exec(conn, @"
                    BEGIN IMMEDIATE;
                    CREATE TABLE IF NOT EXISTS ""column_meta"" (
                        ""CollectionId"" INTEGER NOT NULL,
                        ""Key""          TEXT NOT NULL,
                        ""Position""     INTEGER NULL,
                        ""Visible""      INTEGER NULL,
                        ""Level""        INTEGER NULL,
                        ""DisplayName""  TEXT NULL,
                        CONSTRAINT ""PK_column_meta"" PRIMARY KEY (""CollectionId"", ""Key"")
                    );
                    INSERT OR IGNORE INTO column_meta (CollectionId, Key, Position, Visible, Level, DisplayName)
                    SELECT k.CollectionId, k.Key, co.Position, cd.Visible, cd.Level, sm.DisplayName
                    FROM (
                        SELECT CollectionId, Key FROM column_order
                        UNION SELECT CollectionId, Key FROM column_display
                        UNION SELECT CollectionId, Key FROM system_field_meta WHERE DisplayName IS NOT NULL
                    ) k
                    LEFT JOIN column_order      co ON co.CollectionId = k.CollectionId AND co.Key = k.Key
                    LEFT JOIN column_display    cd ON cd.CollectionId = k.CollectionId AND cd.Key = k.Key
                    LEFT JOIN system_field_meta sm ON sm.CollectionId = k.CollectionId AND sm.Key = k.Key;
                    DROP TABLE column_display;
                    DROP TABLE column_order;
                    DROP TABLE system_field_meta;
                    COMMIT;");

            // ── 2) db_field_values: WITHOUT ROWID, PK (RecordId, FieldId), без Id.
            if (IsRowidTable(conn, "db_field_values"))
                RebuildChunked(conn, name, segValuesCopy,
                    source: "db_field_values",
                    createNewSql: @"
                        CREATE TABLE IF NOT EXISTS ""db_field_values_new"" (
                            ""RecordId"" INTEGER NOT NULL,
                            ""FieldId""  INTEGER NOT NULL,
                            ""Value""    TEXT NOT NULL,
                            CONSTRAINT ""PK_db_field_values"" PRIMARY KEY (""RecordId"", ""FieldId""),
                            CONSTRAINT ""FK_db_field_values_db_fields_FieldId"" FOREIGN KEY (""FieldId"")
                                REFERENCES ""db_fields"" (""Id"") ON DELETE CASCADE,
                            CONSTRAINT ""FK_db_field_values_db_records_RecordId"" FOREIGN KEY (""RecordId"")
                                REFERENCES ""db_records"" (""Id"") ON DELETE CASCADE
                        ) WITHOUT ROWID;",
                    columns: "\"RecordId\", \"FieldId\", \"Value\"",
                    totalKey: "compact.values.total");
            CreateMissingIndexes(db, path, name, segValuesIndex,
                ("IX_db_field_values_FieldId_Value",
                 "CREATE INDEX \"IX_db_field_values_FieldId_Value\" ON \"db_field_values\" (\"FieldId\", \"Value\");"));

            // ── 3) calendar_index: WITHOUT ROWID (PK пары был и раньше — уникальный индекс есть).
            if (IsRowidTable(conn, "calendar_index"))
                RebuildChunked(conn, name, segCalendarCopy,
                    source: "calendar_index",
                    createNewSql: @"
                        CREATE TABLE IF NOT EXISTS ""calendar_index_new"" (
                            ""RecordId""     INTEGER NOT NULL,
                            ""FieldId""      INTEGER NOT NULL,
                            ""CollectionId"" INTEGER NOT NULL,
                            ""StartIso""     TEXT NOT NULL,
                            ""EndIso""       TEXT NOT NULL,
                            ""AllDay""       INTEGER NOT NULL,
                            CONSTRAINT ""PK_calendar_index"" PRIMARY KEY (""RecordId"", ""FieldId""),
                            CONSTRAINT ""FK_calendar_index_db_fields_FieldId"" FOREIGN KEY (""FieldId"")
                                REFERENCES ""db_fields"" (""Id"") ON DELETE CASCADE,
                            CONSTRAINT ""FK_calendar_index_db_records_RecordId"" FOREIGN KEY (""RecordId"")
                                REFERENCES ""db_records"" (""Id"") ON DELETE CASCADE
                        ) WITHOUT ROWID;",
                    columns: "\"RecordId\", \"FieldId\", \"CollectionId\", \"StartIso\", \"EndIso\", \"AllDay\"",
                    totalKey: "compact.calendar.total");
            CreateMissingIndexes(db, path, name, segCalendarIndex,
                ("IX_calendar_index_FieldId",
                 "CREATE INDEX \"IX_calendar_index_FieldId\" ON \"calendar_index\" (\"FieldId\");"),
                ("IX_calendar_index_CollectionId_StartIso",
                 "CREATE INDEX \"IX_calendar_index_CollectionId_StartIso\" ON \"calendar_index\" (\"CollectionId\", \"StartIso\");"),
                ("IX_calendar_index_CollectionId_AllDay_StartIso_RecordId_FieldId",
                 "CREATE INDEX \"IX_calendar_index_CollectionId_AllDay_StartIso_RecordId_FieldId\" ON \"calendar_index\" (\"CollectionId\", \"AllDay\", \"StartIso\", \"RecordId\", \"FieldId\");"),
                ("IX_calendar_index_CollectionId_AllDay_EndIso_StartIso_RecordId_FieldId",
                 "CREATE INDEX \"IX_calendar_index_CollectionId_AllDay_EndIso_StartIso_RecordId_FieldId\" ON \"calendar_index\" (\"CollectionId\", \"AllDay\", \"EndIso\", \"StartIso\", \"RecordId\", \"FieldId\");"));

            // ── 4) Готово: миграция достигнута порциями — фиксируем в истории EF (паттерн
            //     моста InitialCreate), чистим счетчики, поднимаем флаг отложенного сжатия.
            Exec(conn, $@"
                BEGIN IMMEDIATE;
                CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                    ""MigrationId""    TEXT NOT NULL CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY,
                    ""ProductVersion"" TEXT NOT NULL
                );
                INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('{SchemaCompactId}', '9.0.0');
                DELETE FROM db_meta WHERE ""Key"" LIKE 'compact.%.total';
                INSERT INTO db_meta (""Key"", ""Value"") VALUES ('compact.vacuum', '1')
                    ON CONFLICT(""Key"") DO UPDATE SET ""Value"" = '1';
                COMMIT;");
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    // Порционная перекладка source → source_new с чекпоинтом на каждой порции.
    // Порции идут в порядке (RecordId, FieldId) по уникальному индексу пары СТАРОЙ таблицы:
    // порядок детерминирован (точка возобновления = максимальный ключ новой таблицы,
    // мгновенно по PK), новая WITHOUT ROWID-таблица наполняется последовательно (быстрая
    // дозапись в хвост B-дерева), а физический порядок строк старой таблицы почти совпадает
    // с порядком пары (значения пишутся по мере создания записей) — чтение близко к
    // последовательному. INSERT OR IGNORE — страховка идемпотентности порции.
    private static void RebuildChunked(DbConnection conn, string name, Action<string, double?>? stage,
                                       string source, string createNewSql, string columns, string totalKey)
    {
        var newTable = source + "_new";
        Exec(conn, createNewSql);

        // Общий объем: считается один раз (скан индекса пары) и хранится в db_meta —
        // после перезапуска процент восстанавливается без повторного пересчета.
        var total = ReadMetaLong(conn, totalKey);
        if (total is null)
        {
            stage?.Invoke($"Обновление «{name}»\nмиграция данных · подсчет объема…", null);
            total = Scalar(conn, $"SELECT COUNT(*) FROM \"{source}\";");
            Exec(conn, $"INSERT INTO db_meta (\"Key\", \"Value\") VALUES ('{totalKey}', '{total}') " +
                       $"ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = '{total}';");
        }

        // Сколько уже переложено — хвост новой таблицы говорит и точку, и (через счетчик
        // порций) объем; счетчик держим в памяти, а на старте сеанса берем COUNT новой
        // таблицы ТОЛЬКО если она непуста и мала… COUNT по WITHOUT ROWID — полный скан,
        // на гигабайтах дорого, поэтому прогресс сеанса восстанавливаем дешево: доля
        // = позиция ключа возобновления в старой таблице непредсказуема, но монотонна —
        // используем счетчик done из db_meta, обновляемый в ОДНОЙ транзакции с порцией.
        var doneKey = totalKey.Replace(".total", ".done");
        var done    = ReadMetaLong(conn, doneKey) ?? 0;

        var watch        = System.Diagnostics.Stopwatch.StartNew();
        var sessionBase  = done;
        var emaRemaining = -1.0;

        Report();   // процент виден сразу — в т.ч. достигнутый до перезапуска

        while (true)
        {
            // Точка возобновления — максимальный ключ уже переложенного.
            var (lastR, lastF) = MaxKey(conn, newTable);

            long inserted;
            Exec(conn, "BEGIN IMMEDIATE;");
            try
            {
                inserted = ExecCount(conn, $@"
                    INSERT OR IGNORE INTO ""{newTable}"" ({columns})
                        SELECT {columns} FROM ""{source}""
                        WHERE (""RecordId"", ""FieldId"") > ({lastR}, {lastF})
                        ORDER BY ""RecordId"", ""FieldId""
                        LIMIT {ChunkRows};");
                done += inserted;
                Exec(conn, $"INSERT INTO db_meta (\"Key\", \"Value\") VALUES ('{doneKey}', '{done}') " +
                           $"ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = '{done}';");
                Exec(conn, "COMMIT;");
            }
            catch
            {
                try { Exec(conn, "ROLLBACK;"); } catch { }
                throw;
            }

            Report();
            if (inserted < ChunkRows) break;   // источник исчерпан
        }

        // Короткая атомарная подмена: с этого места таблица уже в новом формате.
        Exec(conn, $@"
            BEGIN IMMEDIATE;
            DROP TABLE ""{source}"";
            ALTER TABLE ""{newTable}"" RENAME TO ""{source}"";
            DELETE FROM db_meta WHERE ""Key"" = '{doneKey}';
            COMMIT;");

        void Report()
        {
            var frac = total > 0 ? Math.Min(0.99, done / (double)total.Value) : 0;

            // Оценка времени — по скорости ТЕКУЩЕГО сеанса (после перезапуска прошлые
            // сеансы в темп не входят), сглаживание EMA как у SqliteStageProgress.
            var eta = "";
            var sessionDone = done - sessionBase;
            if (sessionDone > 0 && watch.Elapsed.TotalSeconds > 1)
            {
                var rate      = sessionDone / watch.Elapsed.TotalSeconds;
                var remaining = (total.Value - done) / Math.Max(1, rate);
                emaRemaining  = emaRemaining < 0 ? remaining : emaRemaining * 0.7 + remaining * 0.3;
                eta = emaRemaining < 60
                    ? "осталось меньше минуты"
                    : $"осталось ≈ {Math.Ceiling(emaRemaining / 60):0} мин";
            }

            stage?.Invoke($"Обновление «{name}»\nмиграция данных{(eta.Length > 0 ? " · " + eta : "")}", frac);
        }
    }

    // Индексы новой таблицы. CREATE INDEX неделим (при выключении начнется заново), но
    // это короткая часть работы; готовые индексы пропускаются — шаг идемпотентен.
    // Прогресс — тем же гибридным счетчиком, что у EF-миграций (VM-шаги + рост журнала).
    private static void CreateMissingIndexes(AppDbContext db, string path, string name,
        Action<string, double?>? stage, params (string IndexName, string Sql)[] indexes)
    {
        var conn    = db.Database.GetDbConnection();
        var missing = indexes.Where(i =>
            Scalar(conn, $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='{i.IndexName}';") == 0)
            .ToList();
        if (missing.Count == 0) return;

        using (SqliteStageProgress.ForMigration(db, path, name, stage))
            foreach (var i in missing)
                Exec(conn, i.Sql);
    }

    // ── Флаг отложенного сжатия (переживает выключение во время VACUUM) ────────────

    private static bool NeedsCompact(AppDbContext db)
    {
        if (!HasTable(db, "db_meta")) return false;
        db.Database.OpenConnection();
        try
        {
            return Scalar(db.Database.GetDbConnection(),
                "SELECT COUNT(*) FROM db_meta WHERE \"Key\"='compact.vacuum';") > 0;
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    private static void SetNeedsCompact(AppDbContext db)
    {
        if (!HasTable(db, "db_meta")) return;
        db.Database.ExecuteSqlRaw("INSERT INTO db_meta (\"Key\", \"Value\") VALUES ('compact.vacuum', '1') " +
                                  "ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = '1';");
    }

    private static void ClearNeedsCompact(AppDbContext db)
    {
        if (!HasTable(db, "db_meta")) return;
        db.Database.ExecuteSqlRaw("DELETE FROM db_meta WHERE \"Key\" = 'compact.vacuum';");
    }

    // ── Сжатие по просьбе при следующем запуске ─────────────────────────────────────
    //
    // Основную базу при работающей программе не отцепить (на ней держится все окно), поэтому ее
    // «Сжать файл» — флаг в db_meta: следующее монтирование сжимает файл с ходом на заставке, без
    // порогов. Флаг в самом файле — он переживает и выключение компьютера до запуска.

    private const string CompactRequestKey = "compact.requested";

    /// <summary>Сжать файл этой базы при следующем подключении (основная база — при следующем
    /// запуске программы), сколько бы в нем ни было данных.</summary>
    public void RequestCompactOnNextStart()
    {
        if (Provider != "sqlite" || !HasTable(Context, "db_meta")) return;
        Context.Database.ExecuteSqlRaw($"INSERT INTO db_meta (\"Key\", \"Value\") VALUES ('{CompactRequestKey}', '1') " +
                                       "ON CONFLICT(\"Key\") DO UPDATE SET \"Value\" = '1';");
        Log.Information("База «{Name:l}»: сжатие файла заказано на следующий запуск ({Path:l})", Name, Path);
    }

    /// <summary>Заказано ли сжатие при следующем запуске (<see cref="RequestCompactOnNextStart"/>).</summary>
    public bool CompactOnNextStart => Provider == "sqlite" && CompactRequested(Context);

    private static bool CompactRequested(AppDbContext db)
    {
        if (!HasTable(db, "db_meta")) return false;
        db.Database.OpenConnection();
        try
        {
            return Scalar(db.Database.GetDbConnection(),
                $"SELECT COUNT(*) FROM db_meta WHERE \"Key\"='{CompactRequestKey}';") > 0;
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    private static void ClearCompactRequest(AppDbContext db) =>
        db.Database.ExecuteSqlRaw($"DELETE FROM db_meta WHERE \"Key\" = '{CompactRequestKey}';");

    // ── SQL-хелперы на живом соединении ────────────────────────────────────────────

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // Выполнить и вернуть changes() — число строк, вставленных последним оператором.
    private static long ExecCount(DbConnection conn, string sql)
    {
        Exec(conn, sql);
        return Scalar(conn, "SELECT changes();");
    }

    private static long Scalar(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static bool TableExists(DbConnection conn, string table) =>
        Scalar(conn, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';") > 0;

    // Таблица еще в старом (rowid) формате? Новый формат содержит WITHOUT ROWID в DDL.
    private static bool IsRowidTable(DbConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT sql FROM sqlite_master WHERE type='table' AND name='{table}';";
        var sql = Convert.ToString(cmd.ExecuteScalar()) ?? "";
        return sql.Length > 0 && !sql.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase);
    }

    // Максимальный ключ новой таблицы — точка возобновления (мгновенно по PK DESC).
    private static (long R, long F) MaxKey(DbConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"RecordId\", \"FieldId\" FROM \"{table}\" " +
                          "ORDER BY \"RecordId\" DESC, \"FieldId\" DESC LIMIT 1;";
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetInt64(1)) : (0, 0);
    }

    private static long? ReadMetaLong(DbConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"Value\" FROM db_meta WHERE \"Key\" = '{key}';";
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : long.Parse(Convert.ToString(v)!);
    }
}
