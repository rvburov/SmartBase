using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

// Смоук-стенд провайдера PostgreSQL: сквозной прогон монтирования, CRUD, гонок,
// PG-веток дубля и generic-переноса между провайдерами. Все проверки — assert-ами:
// стенд падает на первом расхождении с ненулевым кодом выхода.

var pg1 = args.Length > 0 ? args[0] : Conn("smartbase");
var pg2 = args.Length > 1 ? args[1] : Conn("smartbase2");

var scratch = Path.Combine(Path.GetTempPath(), "pgsmoke");
Directory.CreateDirectory(scratch);
var sqlitePath = Path.Combine(scratch, "pgsmoke_local.db");
if (File.Exists(sqlitePath)) File.Delete(sqlitePath);
foreach (var suffix in new[] { "-wal", "-shm" })
    if (File.Exists(sqlitePath + suffix)) File.Delete(sqlitePath + suffix);

DropAllTables(pg1);
DropAllTables(pg2);

var failures = 0;

// ── 1. Монтирование: миграция + сиды + повторный no-op ─────────────────────────
Step("mount: миграция InitialCreatePg + сиды", () =>
{
    using (var m = DatabaseManager.Open(101, "PG-смоук", pg1, "postgres"))
    {
        Check(m.Context.IsPostgres, "контекст должен быть PG");
        Check(m.IsShared, "PG-база многопользовательская (IsShared)");
        Check(m.Categories.GetAll().Count > 0, "сиды категорий применены");
        Check(m.Permissions.IsAdmin, "первый пользователь стал админом");
    }
    using var again = DatabaseManager.Open(101, "PG-смоук", pg1, "postgres");
    Check(again.Permissions.IsAdmin, "повторное монтирование: пользователь узнан");
});

// ── 2. CRUD + календарная проекция + keyset ────────────────────────────────────
int colId = 0, dateFieldId = 0, textFieldId = 0;
Step("crud: группа/коллекция/поля/записи/значения/календарь/keyset", () =>
{
    using var m = DatabaseManager.Open(101, "PG-смоук", pg1, "postgres");
    var repo = m.Constructor;

    var group = repo.AddGroup(new DbGroup { Name = "Смоук-группа" });
    var col   = repo.AddCollection(new DbCollection { Name = "Смоук-коллекция", GroupId = group.Id });
    colId     = col.Id!.Value;

    var text = repo.AddField(new DbField { CollectionId = colId, Name = "Текст", FieldType = "text", Position = 1000 });
    var date = repo.AddField(new DbField { CollectionId = colId, Name = "Срок", FieldType = "date", Position = 2000, IsCalStart = true });
    textFieldId = text.Id!.Value;
    dateFieldId = date.Id!.Value;

    for (var i = 1; i <= 250; i++)
    {
        var rec = repo.AddRecord(new DbRecord { CollectionId = colId, Title = $"Запись {i}", Position = i * 1000 });
        repo.SetFieldValue(rec.Id!.Value, textFieldId, $"значение {i}");
        if (i % 5 == 0)
            repo.SetFieldValue(rec.Id!.Value, dateFieldId, $"2026-07-{(i % 28) + 1:00}");
    }

    Check(repo.CountRecords(colId) == 250, "CountRecords = 250");

    var page1 = repo.GetRecordsPageAfter(colId, null, null, 100);
    Check(page1.Count == 100, "первая keyset-страница = 100");
    var last  = page1[^1];
    var page2 = repo.GetRecordsPageAfter(colId, last.Position, last.Id, 100);
    Check(page2.Count == 100 && page2[0].Id != page1[0].Id, "вторая keyset-страница без перекрытия");

    Check(Scalar(pg1, $"SELECT COUNT(*) FROM calendar_index WHERE collectionid = {colId}") == 50,
          "calendar_index: 50 строк проекции");

    // Правка даты → upsert строки проекции; очистка → строка удаляется.
    var firstDated = repo.GetRecordsPageAfter(colId, null, null, 5)[4];
    repo.SetFieldValue(firstDated.Id!.Value, dateFieldId, "2026-12-31");
    Check(Scalar(pg1, $"SELECT COUNT(*) FROM calendar_index WHERE recordid = {firstDated.Id} AND startiso LIKE '2026-12-31%'") == 1,
          "upsert проекции при правке даты");
    repo.SetFieldValue(firstDated.Id!.Value, dateFieldId, "");
    Check(Scalar(pg1, $"SELECT COUNT(*) FROM calendar_index WHERE recordid = {firstDated.Id}") == 0,
          "очистка даты удаляет строку проекции");
    repo.SetFieldValue(firstDated.Id!.Value, dateFieldId, "2026-12-31");
});

// ── 3. Гонка записи одной ячейки с двух подключений ────────────────────────────
Step("contend: два подключения бьют в одну ячейку (last write wins)", () =>
{
    using var m = DatabaseManager.Open(101, "PG-смоук", pg1, "postgres");
    var recId = m.Constructor.AddRecord(new DbRecord { CollectionId = colId, Title = "Гонка", Position = 999_000 }).Id!.Value;

    var errors = 0;
    Parallel.For(0, 2, w =>
    {
        using var ctx = new AppDbContext(pg1);
        var repo = new ConstructorRepository(ctx);
        for (var i = 0; i < 200; i++)
        {
            try { repo.SetFieldValue(recId, textFieldId, $"писатель {w} · {i}"); }
            catch { Interlocked.Increment(ref errors); }
        }
    });
    Check(errors == 0, $"без исключений (получено {errors})");
    Check(Scalar(pg1, $"SELECT COUNT(*) FROM db_field_values WHERE recordid = {recId} AND fieldid = {textFieldId}") == 1,
          "ровно одна строка значения после гонки");
});

// ── 4. PG-ветки дубля: записи (nextval-карта) и коллекция целиком ──────────────
Step("dup: дубль записей и коллекции через nextval-карту", () =>
{
    using var m = DatabaseManager.Open(101, "PG-смоук", pg1, "postgres");
    var repo = m.Constructor;

    var before = repo.CountRecords(colId);
    var srcIds = repo.GetRecordsPageAfter(colId, null, null, 10).Select(r => r.Id!.Value).ToList();
    var copies = repo.DuplicateRecordsData(colId, srcIds);
    Check(copies.Count == 10, "10 копий записей");
    Check(repo.CountRecords(colId) == before + 10, "число записей выросло на 10");
    Check(Scalar(pg1, $"SELECT COUNT(*) FROM db_field_values WHERE recordid IN ({string.Join(",", copies)})") > 0,
          "значения копий на месте");

    var dup = repo.DuplicateCollection(colId);
    Check(dup.Id is not null && dup.Id != colId, "дубль коллекции создан");
    var srcCnt = repo.CountRecords(colId);
    var dupCnt = repo.CountRecords(dup.Id!.Value);
    Check(srcCnt == dupCnt, $"дубль: записей поровну ({srcCnt} = {dupCnt})");
    Check(Scalar(pg1, $"SELECT COUNT(*) FROM db_field_values v JOIN db_records r ON r.id = v.recordid WHERE r.collectionid = {dup.Id}")
       == Scalar(pg1, $"SELECT COUNT(*) FROM db_field_values v JOIN db_records r ON r.id = v.recordid WHERE r.collectionid = {colId}"),
          "дубль: значений поровну");
    Check(Scalar(pg1, $"SELECT COUNT(*) FROM calendar_index WHERE collectionid = {dup.Id}")
       == Scalar(pg1, $"SELECT COUNT(*) FROM calendar_index WHERE collectionid = {colId}"),
          "дубль: проекция календаря поровну");
    repo.DeleteCollectionData(dup.Id!.Value, log: false);
});

// ── 5. Generic-перенос: SQLite→PG, PG→SQLite, PG→PG ────────────────────────────
Step("move: SQLite→PG → PG→SQLite → PG→PG", () =>
{
    long srcRecs, srcVals, srcCal;
    int localColId;

    // Локальная SQLite-база с коллекцией.
    using (var local = DatabaseManager.Open(102, "Локальная", sqlitePath))
    {
        var lr  = local.Constructor;
        var col = lr.AddCollection(new DbCollection { Name = "Переносимая" });
        localColId = col.Id!.Value;
        var t = lr.AddField(new DbField { CollectionId = localColId, Name = "Текст", FieldType = "text", Position = 1000 });
        var d = lr.AddField(new DbField { CollectionId = localColId, Name = "Дата", FieldType = "date", Position = 2000, IsCalStart = true });
        for (var i = 1; i <= 120; i++)
        {
            var rec = lr.AddRecord(new DbRecord { CollectionId = localColId, Title = $"Строка {i}", Position = i * 1000 });
            lr.SetFieldValue(rec.Id!.Value, t.Id!.Value, $"т{i}");
            if (i % 3 == 0) lr.SetFieldValue(rec.Id!.Value, d.Id!.Value, $"2026-08-{(i % 28) + 1:00}");
        }
        srcRecs = lr.CountRecords(localColId);
        srcVals = SqliteScalar(sqlitePath, $"SELECT COUNT(*) FROM db_field_values v JOIN db_records r ON r.Id = v.RecordId WHERE r.CollectionId = {localColId}");
        srcCal  = SqliteScalar(sqlitePath, $"SELECT COUNT(*) FROM calendar_index WHERE CollectionId = {localColId}");
    }

    // SQLite→PG: this — приёмник (PG).
    int pgColId;
    using (var m = DatabaseManager.Open(101, "PG-смоук", pg1, "postgres"))
    {
        pgColId = m.Constructor.MoveCollectionIsolated(sqlitePath, localColId, null);
        Check(pgColId != 0, "перенос вернул id новой коллекции");
        Check(m.Constructor.CountRecords(pgColId) == srcRecs, $"SQLite→PG: записей {srcRecs}");
        Check(Scalar(pg1, $"SELECT COUNT(*) FROM db_field_values v JOIN db_records r ON r.id = v.recordid WHERE r.collectionid = {pgColId}") == srcVals,
              $"SQLite→PG: значений {srcVals}");
        Check(Scalar(pg1, $"SELECT COUNT(*) FROM calendar_index WHERE collectionid = {pgColId}") == srcCal,
              $"SQLite→PG: проекция {srcCal}");
        Check(SqliteScalar(sqlitePath, $"SELECT COUNT(*) FROM db_records WHERE CollectionId = {localColId}") == 0,
              "SQLite→PG: источник удалён");
    }

    // PG→SQLite обратно: this — приёмник (SQLite-файл).
    int backColId;
    using (var local = DatabaseManager.Open(102, "Локальная", sqlitePath))
    {
        backColId = local.Constructor.MoveCollectionIsolated(pg1, pgColId, null);
        Check(backColId != 0, "обратный перенос вернул id");
        Check(local.Constructor.CountRecords(backColId) == srcRecs, $"PG→SQLite: записей {srcRecs}");
        Check(SqliteScalar(sqlitePath, $"SELECT COUNT(*) FROM db_field_values v JOIN db_records r ON r.Id = v.RecordId WHERE r.CollectionId = {backColId}") == srcVals,
              $"PG→SQLite: значений {srcVals}");
        Check(SqliteScalar(sqlitePath, $"SELECT COUNT(*) FROM calendar_index WHERE CollectionId = {backColId}") == srcCal,
              $"PG→SQLite: проекция {srcCal}");
        Check(Scalar(pg1, $"SELECT COUNT(*) FROM db_records WHERE collectionid = {pgColId}") == 0,
              "PG→SQLite: источник в PG удалён");
    }

    // PG→PG между двумя серверными базами: снова закинуть на pg1, затем pg1→pg2.
    using (var m1 = DatabaseManager.Open(101, "PG-смоук", pg1, "postgres"))
        pgColId = m1.Constructor.MoveCollectionIsolated(sqlitePath, backColId, null);
    using (var m2 = DatabaseManager.Open(103, "PG-смоук-2", pg2, "postgres"))
    {
        var movedId = m2.Constructor.MoveCollectionIsolated(pg1, pgColId, null);
        Check(movedId != 0, "PG→PG: перенос вернул id");
        Check(m2.Constructor.CountRecords(movedId) == srcRecs, $"PG→PG: записей {srcRecs}");
        Check(Scalar(pg2, $"SELECT COUNT(*) FROM calendar_index WHERE collectionid = {movedId}") == srcCal,
              $"PG→PG: проекция {srcCal}");
        Check(Scalar(pg1, $"SELECT COUNT(*) FROM db_records WHERE collectionid = {pgColId}") == 0,
              "PG→PG: источник удалён");
    }
});

// ── 6. Свип незавершённого переноса ────────────────────────────────────────────
Step("sweep: незавершённая копия убирается при монтировании", () =>
{
    // Симуляция kill посреди заливки: «частичная» коллекция + маркер в db_meta.
    int orphanId;
    using (var ctx = new AppDbContext(pg1))
    {
        var repo = new ConstructorRepository(ctx);
        orphanId = repo.AddCollection(new DbCollection { Name = "Частичная копия" }).Id!.Value;
        ctx.DbMeta.Add(new SmartBase.Database.Entities.DbMetaEntity
            { Key = "crossmove.pending.collection", Value = orphanId.ToString() });
        ctx.SaveChanges();
    }

    using var m = DatabaseManager.Open(101, "PG-смоук", pg1, "postgres");   // монтирование → свип
    Check(Scalar(pg1, $"SELECT COUNT(*) FROM db_collections WHERE id = {orphanId}") == 0,
          "частичная копия удалена свипом");
    Check(Scalar(pg1, "SELECT COUNT(*) FROM db_meta WHERE key = 'crossmove.pending.collection'") == 0,
          "маркер снят");
});

// ── 7. Детектор чужих изменений (запросы ChangePoller-а на PG) ─────────────────
Step("poller: курсор MAX(id) по change_log", () =>
{
    var before = Scalar(pg1, "SELECT COALESCE(MAX(id), 0) FROM change_log");
    using (var m = DatabaseManager.Open(101, "PG-смоук", pg1, "postgres"))
        m.Constructor.AddRecord(new DbRecord { CollectionId = colId, Title = "Для журнала", Position = 1_500_000 });
    var after = Scalar(pg1, "SELECT COALESCE(MAX(id), 0) FROM change_log");
    Check(after > before, "журнал вырос — детектор увидит чужую правку");

    using var conn = new NpgsqlConnection(pg1);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id, UserKey, Action, TargetType, CollectionName " +
                      "FROM change_log WHERE Id > @last ORDER BY Id LIMIT 500;";
    var p = cmd.CreateParameter(); p.ParameterName = "@last"; p.Value = before; cmd.Parameters.Add(p);
    using var r = cmd.ExecuteReader();
    Check(r.Read(), "хвост журнала читается тем же SQL, что ChangePoller");
});

Console.WriteLine(failures == 0 ? "\nВСЕ ПРОВЕРКИ ПРОЙДЕНЫ" : $"\nПРОВАЛОВ: {failures}");
return failures == 0 ? 0 : 1;

// ── Вспомогательные ────────────────────────────────────────────────────────────

static string Conn(string database) => new PgConnectionInfo(
    "localhost", 5455, database, "postgres", "postgres", "Disable").ToConnectionString();

void Step(string title, Action body)
{
    Console.WriteLine($"\n── {title}");
    try { body(); }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"   СБОЙ: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
    }
}

void Check(bool ok, string what)
{
    if (ok) { Console.WriteLine($"   ok: {what}"); return; }
    failures++;
    Console.WriteLine($"   ПРОВАЛ: {what}");
}

static long Scalar(string connString, string sql)
{
    using var conn = new NpgsqlConnection(connString);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
}

static long SqliteScalar(string dbPath, string sql)
{
    using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
}

// Полный сброс тестовой PG-базы: смоук идемпотентен между запусками.
static void DropAllTables(string connString)
{
    using var conn = new NpgsqlConnection(connString);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "DROP SCHEMA public CASCADE; CREATE SCHEMA public;";
    cmd.ExecuteNonQuery();
}
