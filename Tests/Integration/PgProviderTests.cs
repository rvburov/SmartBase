using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Провайдер PostgreSQL (перенос функционала Tests/PgSmoke). Каждый тест — SkippableFact:
/// без поднятого стенда sb-pg (порт 5455) помечается Skipped, а не падает. Перед прогоном
/// схема PG сбрасывается, чтобы миграция InitialCreatePg накатилась начисто.
/// </summary>
public sealed class PgProviderTests
{
    private static void ResetSchema(string connString)
    {
        using var conn = new NpgsqlConnection(connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
        cmd.ExecuteNonQuery();
    }

    [SkippableFact]
    public async Task Library_order_keys_and_batch_fetch_work_on_postgres()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "PostgreSQL не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);
        using var manager = DatabaseManager.Open(101, "pg-library", cs, provider: "postgres");
        var repo = manager.Constructor;
        var col = SampleData.AddCollection(repo, "Документы").Id!.Value;
        var field = SampleData.AddField(repo, col, "Файл", "file").Id!.Value;
        repo.SetFileFieldInLibrary(field, true);
        var type = repo.DocTypeFieldId(col)!.Value;
        var record = SampleData.AddRecord(repo, col, "Название").Id!.Value;
        repo.SetFieldValue(record, field, "/document.pdf");
        var keys = await repo.GetDocOrderKeysAsync(col, type, ConstructorRepository.DocView.All,
            SmartBase.Core.Documents.DocSort.Title);
        Assert.Equal("Название", Assert.Single(keys).Title);
        var docs = await repo.GetDocsByOrderKeysAsync(col, type,
            [new SmartBase.Core.Documents.DocOrderKey(0, record, field)]);
        Assert.Equal("/document.pdf", Assert.Single(docs).Path);
    }

    private static long PgScalar(string connString, string sql)
    {
        using var conn = new NpgsqlConnection(connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    [SkippableFact]
    public void Mount_crud_keyset_and_calendar_projection()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);

        using var m = DatabaseManager.Open(101, "pg-test", cs, provider: "postgres");
        Assert.True(m.Context.IsPostgres);
        Assert.True(m.IsShared);
        Assert.True(m.Permissions.IsAdmin);
        Assert.NotEmpty(m.Categories.GetAll());

        var repo = m.Constructor;
        var col  = SampleData.AddCollection(repo, "PG");
        var date = SampleData.AddCalendarDateField(repo, col.Id!.Value);
        SampleData.AddRecords(repo, col.Id!.Value, count: 10, step: 1000);
        Assert.Equal(10, repo.CountRecords(col.Id!.Value));

        var page = repo.GetRecordsPageAfter(col.Id!.Value, null, null, take: 4);
        Assert.Equal(4, page.Count);

        var recId = page[0].Id!.Value;
        var timed = new DateValue(new DateTime(2026, 7, 1, 10, 0, 0), new DateTime(2026, 7, 1, 11, 0, 0), false).ToStorage();
        repo.SetFieldValue(recId, date.Id!.Value, timed);
        var events = repo.GetCalendarRecordsByRangeCapped(new DateTime(2026, 7, 1), new DateTime(2026, 7, 2), null, 1000);
        Assert.Contains(events, r => r.Id == recId);
    }

    [SkippableFact]
    public void Concurrent_writes_last_write_wins()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);

        int recId, fieldId;
        using (var m = DatabaseManager.Open(101, "pg-test", cs, provider: "postgres"))
        {
            var col = SampleData.AddCollection(m.Constructor, "PG");
            var f = SampleData.AddField(m.Constructor, col.Id!.Value, "V");
            recId = SampleData.AddRecord(m.Constructor, col.Id!.Value, "R").Id!.Value;
            fieldId = f.Id!.Value;
        }

        var errors = 0;
        Parallel.For(0, 2, w =>
        {
            using var ctx = new AppDbContext(cs);
            var repo = new ConstructorRepository(ctx);
            for (var i = 0; i < 100; i++)
                try { repo.SetFieldValue(recId, fieldId, $"писатель {w}·{i}"); }
                catch { Interlocked.Increment(ref errors); }
        });

        Assert.Equal(0, errors);
        Assert.Equal(1, PgScalar(cs, $"SELECT COUNT(*) FROM db_field_values WHERE recordid = {recId} AND fieldid = {fieldId}"));
    }

    [SkippableFact]
    public void Duplicate_collection_copies_records_values_and_projection()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);

        using var m = DatabaseManager.Open(101, "pg-test", cs, provider: "postgres");
        var repo = m.Constructor;
        var col = SampleData.AddCollection(repo, "PG");
        var date = SampleData.AddCalendarDateField(repo, col.Id!.Value);
        var recs = SampleData.AddRecords(repo, col.Id!.Value, count: 12, step: 1000);
        foreach (var r in recs.Where((_, i) => i % 3 == 0))
            repo.SetFieldValue(r.Id!.Value, date.Id!.Value, "2026-08-15");

        var dup = repo.DuplicateCollection(col.Id!.Value);
        Assert.NotNull(dup.Id);
        Assert.NotEqual(col.Id!.Value, dup.Id!.Value);
        Assert.Equal(repo.CountRecords(col.Id!.Value), repo.CountRecords(dup.Id!.Value));
        Assert.Equal(
            PgScalar(cs, $"SELECT COUNT(*) FROM calendar_index WHERE collectionid = {col.Id!.Value}"),
            PgScalar(cs, $"SELECT COUNT(*) FROM calendar_index WHERE collectionid = {dup.Id!.Value}"));
    }

    [SkippableFact]
    public void Move_pg_to_pg_between_two_databases()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs1 = PgTestServer.ConnectionString(PgTestServer.Database1);
        var cs2 = PgTestServer.ConnectionString(PgTestServer.Database2);
        ResetSchema(cs1);
        ResetSchema(cs2);

        int srcColId;
        using (var m1 = DatabaseManager.Open(101, "pg1", cs1, provider: "postgres"))
        {
            var col = SampleData.AddCollection(m1.Constructor, "Переносимая");
            var a = SampleData.AddField(m1.Constructor, col.Id!.Value, "A", "text", position: 10);
            var recs = SampleData.AddRecords(m1.Constructor, col.Id!.Value, count: 15, step: 1000);
            foreach (var r in recs) m1.Constructor.SetFieldValue(r.Id!.Value, a.Id!.Value, $"v{r.Id}");
            srcColId = col.Id!.Value;
        }

        using var m2 = DatabaseManager.Open(103, "pg2", cs2, provider: "postgres");
        var movedId = m2.Constructor.MoveCollectionIsolated(cs1, srcColId, targetGroupId: null);

        Assert.NotEqual(0, movedId);
        Assert.Equal(15, m2.Constructor.CountRecords(movedId));
        Assert.Equal(0, PgScalar(cs1, $"SELECT COUNT(*) FROM db_records WHERE collectionid = {srcColId}")); // источник удален
    }

    /// <summary>Перенос SQLite → PostgreSQL и обратно несколькими пакетами: на PG записи и значения
    /// идут потоком COPY, и значение со служебными символами текстового формата (табуляция,
    /// переводы строк, обратная косая, буквальное «\N») обязано доехать до байта в обе стороны;
    /// название, порядок записей и проекция календаря — тоже.</summary>
    [SkippableFact]
    public void Move_sqlite_to_pg_and_back_keeps_every_value_exactly()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);

        string[] tricky = ["таб\tвнутри", "строка\nвторая", "возврат\rкаретки", @"косая \ и \N буквально", "\\N",
                           "кавычки ' и \"", "  пробелы по краям  ", "эмодзи 🙂 и юникод"];
        using var lite = new TempDatabase(dbId: 1, name: "lite");
        var repo = lite.Constructor;
        var col  = SampleData.AddCollection(repo, "Переносимая");
        var text = SampleData.AddField(repo, col.Id!.Value, "Текст", "text", position: 10);
        var date = SampleData.AddCalendarDateField(repo, col.Id!.Value, position: 20);
        for (var i = 0; i < 40; i++)
        {
            var rec = SampleData.AddRecord(repo, col.Id!.Value, tricky[i % tricky.Length] + $" {i}", position: (40 - i) * 10);
            repo.SetFieldValue(rec.Id!.Value, text.Id!.Value, tricky[(i + 3) % tricky.Length]);
            if (i % 2 == 0) repo.SetFieldValue(rec.Id!.Value, date.Id!.Value, new DateTime(2026, 3, 1).AddDays(i).ToString("yyyy-MM-dd"));
        }
        // Оформление строки и ячейки и высота строки: у этих таблиц половина столбцов пустая (\N).
        var styled = repo.GetRecordsPage(col.Id!.Value, 0, 3).Select(r => r.Id!.Value).ToList();
        repo.SetRowStyle(col.Id!.Value, [styled[0]], StyleAspect.Background, "#FF0000");
        repo.SetCellStyle(col.Id!.Value, [(styled[1], text.Id!.Value.ToString())], StyleAspect.Text, "#00AA00");
        repo.SaveRowHeights(col.Id!.Value, new Dictionary<int, int> { [styled[2]] = 48 });
        var expected = Snapshot(repo, col.Id!.Value);
        var calendar = Convert.ToInt64(lite.Context.CalendarIndex.Count(c => c.CollectionId == col.Id!.Value));

        using var pg = DatabaseManager.Open(102, "pg", cs, provider: "postgres");
        var pgCol = pg.Constructor.MoveCollectionIsolated(lite.Path, col.Id!.Value, targetGroupId: null, batchSize: 7,
                                                          sourceDbId: 1, targetDbId: 102);
        Assert.Equal(expected, Snapshot(pg.Constructor, pgCol));
        Assert.Equal(calendar, PgScalar(cs, $"SELECT COUNT(*) FROM calendar_index WHERE collectionid = {pgCol}"));
        Assert.Equal(1, PgScalar(cs, $"SELECT COUNT(*) FROM record_display WHERE collectionid = {pgCol} AND bg = '#FF0000'"));
        Assert.Equal(1, PgScalar(cs, $"SELECT COUNT(*) FROM cell_style WHERE collectionid = {pgCol} AND fg = '#00AA00'"));
        Assert.Equal(1, PgScalar(cs, $"SELECT COUNT(*) FROM ui_row_heights WHERE collectionid = {pgCol} AND height = 48"));

        var back = repo.MoveCollectionIsolated(cs, pgCol, targetGroupId: null, batchSize: 7, sourceDbId: 102, targetDbId: 1);
        Assert.Equal(expected, Snapshot(repo, back));
        Assert.Equal(0, PgScalar(cs, $"SELECT COUNT(*) FROM db_records WHERE collectionid = {pgCol}"));
        Assert.Equal("#FF0000", Assert.Single(repo.GetRowStyles(back)).Value.Bg);
        Assert.Equal([48], repo.GetRowHeights(back).Values);
    }

    // Записи коллекции в порядке (Position, Id): название и значение каждого поля по имени поля.
    private static List<string> Snapshot(ConstructorRepository repo, int colId)
    {
        var fields = repo.GetFields(colId).ToDictionary(f => f.Id!.Value, f => f.Name);
        var recs   = repo.GetRecordsPage(colId, 0, 1000);
        var values = repo.GetFieldValuesBatch(recs.Select(r => r.Id!.Value).ToList());
        return recs.Select(r => r.Title + " | " + string.Join(" | ",
                       values.GetValueOrDefault(r.Id!.Value, []).OrderBy(v => fields[v.Key])
                             .Select(v => $"{fields[v.Key]}={v.Value}")))
                   .ToList();
    }

    [SkippableFact]
    public void Unfinished_crossmove_is_swept_on_mount()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);

        // Поднимаем схему, затем симулируем kill посреди заливки: «частичная» коллекция + маркер.
        int orphanId;
        using (var m = DatabaseManager.Open(101, "pg-test", cs, provider: "postgres"))
        {
            orphanId = SampleData.AddCollection(m.Constructor, "Частичная копия").Id!.Value;
            m.Context.DbMeta.Add(new DbMetaEntity { Key = "crossmove.pending.collection", Value = orphanId.ToString() });
            m.Context.SaveChanges();
        }

        using var again = DatabaseManager.Open(101, "pg-test", cs, provider: "postgres"); // монтирование → свип
        Assert.Equal(0, PgScalar(cs, $"SELECT COUNT(*) FROM db_collections WHERE id = {orphanId}"));
        Assert.Equal(0, PgScalar(cs, "SELECT COUNT(*) FROM db_meta WHERE key = 'crossmove.pending.collection'"));
    }

    [SkippableFact]
    public void Audit_metadata_stamped_on_create_and_edit()
    {
        // Аудит-метаданные записи на PG: миграция AddRecordAuditMetaPg (колонки lowercase) +
        // сырой UPDATE db_records в SetFieldValue должен матчить lowercase-идентификаторы обоих
        // провайдеров (кавычки-lowercase). Проверяем стамп при создании и сдвиг при правке.
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);

        using var m = DatabaseManager.Open(101, "pg-test", cs, provider: "postgres");
        var repo  = m.Constructor;
        var col   = SampleData.AddCollection(repo, "PG");
        var field = SampleData.AddField(repo, col.Id!.Value, "Текст", "text");
        var rec   = SampleData.AddRecord(repo, col.Id!.Value, "R");

        var created = repo.GetRecord(rec.Id!.Value)!;
        Assert.False(string.IsNullOrEmpty(created.CreatedBy));
        Assert.Equal(created.CreatedAt, created.UpdatedAt);

        System.Threading.Thread.Sleep(10);
        repo.SetFieldValue(rec.Id!.Value, field.Id!.Value, "v");   // raw UPDATE db_records по lowercase-колонкам

        var after = repo.GetRecord(rec.Id!.Value)!;
        Assert.Equal(created.CreatedAt, after.CreatedAt);                              // «создано» не трогаем
        Assert.True(string.CompareOrdinal(after.UpdatedAt, created.UpdatedAt) > 0);    // «изменено» подвинулось
        Assert.Equal(created.CreatedBy, after.UpdatedBy);                             // один пользователь на стенде
    }

    [SkippableTheory]
    [MemberData(nameof(FieldTypeMatrixTests.TypeValues), MemberType = typeof(FieldTypeMatrixTests))]
    public void Value_round_trips_for_every_type_pg(string fieldType, string value)
    {
        // Зеркало FieldTypeMatrixTests.Value_round_trips_for_every_type на PostgreSQL.
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);

        using var m = DatabaseManager.Open(101, "pg-test", cs, provider: "postgres");
        var repo  = m.Constructor;
        var col   = SampleData.AddCollection(repo, "PG");
        var field = SampleData.AddField(repo, col.Id!.Value, "F", fieldType);
        var rec   = SampleData.AddRecord(repo, col.Id!.Value, "R");

        repo.SetFieldValue(rec.Id!.Value, field.Id!.Value, value);

        var stored = repo.GetFieldValuesBatch([rec.Id!.Value])[rec.Id!.Value][field.Id!.Value];
        Assert.Equal(value, stored);
    }

    [SkippableFact]
    public void Duplicate_and_delete_records_batch_pg()
    {
        // PG-путь дубля записей — ОТДЕЛЬНЫЙ код (DuplicateRecordsDataPg: карта id через nextval,
        // непрерывного блока rowid нет). Проверяем дубль пакета (со значениями) и пакетное удаление.
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);

        using var m = DatabaseManager.Open(101, "pg-test", cs, provider: "postgres");
        var repo  = m.Constructor;
        var col   = SampleData.AddCollection(repo, "PG");
        var field = SampleData.AddField(repo, col.Id!.Value, "A", "text");
        var recs  = SampleData.AddRecords(repo, col.Id!.Value, count: 3, step: 1000);
        foreach (var (r, i) in recs.Select((r, i) => (r, i + 1)))
            repo.SetFieldValue(r.Id!.Value, field.Id!.Value, $"v{i}");

        var newIds = repo.DuplicateRecordsData(col.Id!.Value, [recs[0].Id!.Value, recs[1].Id!.Value]);
        Assert.Equal(2, newIds.Count);
        Assert.Equal(5, repo.CountRecords(col.Id!.Value));
        var copied = repo.GetFieldValuesBatch(newIds);
        Assert.Equal(["v1", "v2"], newIds.Select(id => copied[id][field.Id!.Value]).OrderBy(v => v).ToList());

        repo.DeleteRecordsData(newIds);
        Assert.Equal(3, repo.CountRecords(col.Id!.Value));
        Assert.Empty(repo.GetFieldValuesBatch(newIds));   // значения копий ушли каскадом
    }

    [SkippableFact]
    public void Duplicate_and_delete_field_pg()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);

        using var m = DatabaseManager.Open(101, "pg-test", cs, provider: "postgres");
        var repo  = m.Constructor;
        var col   = SampleData.AddCollection(repo, "PG");
        var field = SampleData.AddField(repo, col.Id!.Value, "Поле", "text");
        var rec   = SampleData.AddRecord(repo, col.Id!.Value, "R");
        repo.SetFieldValue(rec.Id!.Value, field.Id!.Value, "a");

        var copy = repo.DuplicateField(field.Id!.Value);   // bulk INSERT…SELECT значений
        Assert.Equal("a", repo.GetFieldValuesBatch([rec.Id!.Value])[rec.Id!.Value][copy.Id!.Value]);

        repo.DeleteField(field.Id!.Value);                 // каскад значений исходного поля
        var fields = repo.GetFields(col.Id!.Value);
        Assert.DoesNotContain(fields, f => f.Id == field.Id);
        Assert.Contains(fields, f => f.Id == copy.Id);
        Assert.False(repo.GetFieldValuesBatch([rec.Id!.Value])[rec.Id!.Value].ContainsKey(field.Id!.Value));
    }

    /// <summary>
    /// Раздел задач на PG: чтение идет общим raw SQL (LEFT JOIN значения исполнителя + три
    /// коррелированных подзапроса, флаги сравниваются с 1 — в PG они integer, см.
    /// <c>ApplyLowercaseNames</c>). Проверяем обе половины нового правила: запись с исполнителем
    /// и запись без него — в разделе должны быть обе.
    /// </summary>
    [SkippableFact]
    public void Task_records_include_unassigned_rows_pg()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);

        using var m = DatabaseManager.Open(101, "pg-test", cs, provider: "postgres");
        var repo = m.Constructor;
        var col  = SampleData.AddCollection(repo, "Поручения").Id!.Value;
        var exec = SampleData.AddField(repo, col, "Исполнитель", "user", position: 1000).Id!.Value;
        repo.SetUserFieldTaskExec(exec, true);

        var roles = repo.TaskRoleFields(col);
        var assigned = SampleData.AddRecord(repo, col, "Заменить насос").Id!.Value;
        repo.SetFieldValue(assigned, exec, "Иванов Иван");
        if (roles.StatusId is int sid) repo.SetFieldValue(assigned, sid, ConstructorRepository.TaskStatusActive);

        var free = SampleData.AddRecord(repo, col, "Разобрать архив").Id!.Value;
        repo.SetFieldValue(free, exec, "");
        if (roles.AssignerId is int aid) repo.SetFieldValue(free, aid, "");

        var tasks = repo.GetTaskRecords();
        Assert.Equal(2, tasks.Count);
        Assert.Equal("Иванов Иван", tasks.Single(t => t.Id == assigned).Executor);
        Assert.Equal(ConstructorRepository.TaskStatusActive, tasks.Single(t => t.Id == assigned).Status);
        Assert.Equal("", tasks.Single(t => t.Id == free).Executor);
        Assert.Equal(exec, tasks.Single(t => t.Id == free).ExecFieldId);
    }
}
