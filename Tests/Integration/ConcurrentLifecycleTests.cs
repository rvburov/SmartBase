using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using Xunit.Abstractions;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Небольшие регрессии настоящих команд нескольких пользователей. Каждый участник работает
/// на своем потоке и контексте; барьер выпускает всех после подготовки. Большая нагрузка
/// на 1000 клиентов запускается отдельным стендом, а эти сценарии входят в обычный прогон.
/// </summary>
public sealed class ConcurrentLifecycleTests
{
    private const int Clients = 8;
    private readonly ITestOutputHelper _output;

    public ConcurrentLifecycleTests(ITestOutputHelper output) => _output = output;

    private static string Date(int client) => new DateValue(
        new DateTime(2026, 10, 1).AddDays(client).AddHours(10),
        new DateTime(2026, 10, 1).AddDays(client).AddHours(11), false).ToStorage();

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Concurrent_create_edit_read_duplicate_and_delete_keep_exact_results(string profile)
    {
        using var f = new Fixture(profile);
        var collection = SampleData.AddCollection(f.Repo, "Участники").Id!.Value;
        var text = SampleData.AddField(f.Repo, collection, "Текст").Id!.Value;
        var date = SampleData.AddCalendarDateField(f.Repo, collection).Id!.Value;
        var originals = new int[Clients];
        var copies = new int[Clients];

        RunClients(f, (repo, _, client) =>
        {
            var id = SampleData.AddRecord(repo, collection, $"Участник {client}", (client + 1) * 1000).Id!.Value;
            originals[client] = id;
            repo.SetFieldValue(id, text, $"Первое {client}");
            repo.SetFieldValue(id, text, $"Последнее {client}");
            repo.SetFieldValue(id, date, Date(client));
            Assert.Equal($"Последнее {client}", repo.GetFieldValues(id)[text]);
            var page = repo.GetRecordsPageAfter(collection, null, null, take: Clients * 2);
            Assert.Contains(page, r => r.Id == id);
            var values = repo.GetFieldValuesBatch(page.Select(r => r.Id!.Value).ToArray());
            Assert.Equal(Date(client), values[id][date]);
            copies[client] = repo.DuplicateRecord(id).Id!.Value;
            repo.DeleteRecord(id);
        });

        using var check = f.OpenContext();
        var rows = check.DbRecords.AsNoTracking().Where(r => r.CollectionId == collection).ToList();
        Assert.Equal(Clients, rows.Count);
        Assert.Equal(Clients, copies.Distinct().Count());
        Assert.Equal(copies.Order(), rows.Select(r => r.Id).Order());
        var originalIds = originals.ToHashSet();
        Assert.False(check.DbRecords.Any(r => originalIds.Contains(r.Id)));
        for (var client = 0; client < Clients; client++)
        {
            var id = copies[client];
            var values = check.DbFieldValues.AsNoTracking().Where(v => v.RecordId == id).ToDictionary(v => v.FieldId, v => v.Value);
            Assert.Equal(2, values.Count);
            Assert.Equal($"Последнее {client}", values[text]);
            Assert.Equal(Date(client), values[date]);
            Assert.Single(check.CalendarIndex.AsNoTracking().Where(i => i.RecordId == id && i.FieldId == date));
        }
        AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Concurrent_moves_into_one_collection_keep_values_audit_and_calendar_projection(string profile)
    {
        using var f = new Fixture(profile);
        var source = SampleData.AddCollection(f.Repo, "Источник").Id!.Value;
        var target = SampleData.AddCollection(f.Repo, "Приемник").Id!.Value;
        var sourceText = SampleData.AddField(f.Repo, source, "Текст").Id!.Value;
        var sourceDate = SampleData.AddCalendarDateField(f.Repo, source).Id!.Value;
        var targetText = SampleData.AddField(f.Repo, target, "Текст").Id!.Value;
        var targetDate = SampleData.AddCalendarDateField(f.Repo, target).Id!.Value;
        var originals = new int[Clients];
        var moved = new int[Clients];
        var audits = new (string CreatedAt, string UpdatedAt, string CreatedBy, string UpdatedBy)[Clients];
        for (var client = 0; client < Clients; client++)
        {
            var id = SampleData.AddRecord(f.Repo, source, $"Перенос {client}", (client + 1) * 1000).Id!.Value;
            originals[client] = id;
            f.Repo.SetFieldValue(id, sourceText, $"Значение {client}");
            f.Repo.SetFieldValue(id, sourceDate, Date(client));
            // SetFieldValue уже записал аудит сырым SQL и синхронизировал объект трекера.
            // Отсоединяем объект до исторического штампа ниже, чтобы SaveChanges следующего
            // AddRecord не вернул прежнюю дату правки из трекера поверх этого штампа.
            f.Manager.Context.ChangeTracker.Clear();
            audits[client] = ($"2025-01-{client + 1:D2}T10:00:00.0000000", $"2025-02-{client + 1:D2}T12:00:00.0000000",
                $"Автор {client}", $"Редактор {client}");
            var audit = audits[client];
            f.Manager.Context.Database.ExecuteSqlRaw(
                "UPDATE db_records SET createdat = {0}, updatedat = {1}, createdby = {2}, updatedby = {3} WHERE id = {4}",
                audit.CreatedAt, audit.UpdatedAt, audit.CreatedBy, audit.UpdatedBy, id);
        }

        // Проверяем именно сохраненный исходник ДО гонки. Ошибка подготовки исторического
        // аудита не должна выглядеть как ошибка многопользовательского переноса.
        using (var before = f.OpenContext())
        {
            var sourceRows = before.DbRecords.AsNoTracking().Where(r => r.CollectionId == source).ToDictionary(r => r.Id);
            Assert.Equal(Clients, sourceRows.Count);
            for (var client = 0; client < Clients; client++)
            {
                var record = sourceRows[originals[client]];
                Assert.Equal(audits[client], (record.CreatedAt, record.UpdatedAt, record.CreatedBy, record.UpdatedBy));
            }
        }

        RunClients(f, (repo, _, client) =>
        {
            var ids = repo.MoveRecordsToCollectionIsolated(f.Path, source, [originals[client]], target,
                createMissingFields: false, sourceDbId: 1, targetDbId: 1);
            moved[client] = Assert.Single(ids);
        });

        using var check = f.OpenContext();
        Assert.Empty(check.DbRecords.Where(r => r.CollectionId == source));
        var rows = check.DbRecords.AsNoTracking().Where(r => r.CollectionId == target).ToDictionary(r => r.Id);
        Assert.Equal(Clients, rows.Count);
        Assert.Equal(Clients, moved.Distinct().Count());
        var originalIds = originals.ToHashSet();
        Assert.False(check.DbRecords.Any(r => originalIds.Contains(r.Id)));
        for (var client = 0; client < Clients; client++)
        {
            var id = moved[client];
            var record = rows[id];
            Assert.Equal($"Перенос {client}", record.Title);
            Assert.Equal(audits[client], (record.CreatedAt, record.UpdatedAt, record.CreatedBy, record.UpdatedBy));
            var values = check.DbFieldValues.AsNoTracking().Where(v => v.RecordId == id).ToDictionary(v => v.FieldId, v => v.Value);
            Assert.Equal(2, values.Count);
            Assert.Equal($"Значение {client}", values[targetText]);
            Assert.Equal(Date(client), values[targetDate]);
            var projection = Assert.Single(check.CalendarIndex.AsNoTracking().Where(i => i.RecordId == id));
            Assert.Equal(targetDate, projection.FieldId);
            Assert.Equal(target, projection.CollectionId);
            Assert.StartsWith($"2026-10-{client + 1:D2}T10:00:00", projection.StartIso);
            Assert.StartsWith($"2026-10-{client + 1:D2}T11:00:00", projection.EndIso);
        }
        AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Concurrent_first_writes_to_one_cell_finish_without_duplicates_or_partial_values(string profile)
    {
        using var f = new Fixture(profile);
        var collection = SampleData.AddCollection(f.Repo).Id!.Value;
        var text = SampleData.AddField(f.Repo, collection, "Общая ячейка").Id!.Value;
        var record = SampleData.AddRecord(f.Repo, collection, "Общая запись").Id!.Value;
        var expected = Enumerable.Range(0, Clients).Select(i => $"Участник {i}: " + new string((char)('A' + i), 2048)).ToArray();

        RunClients(f, (repo, _, client) => repo.SetFieldValue(record, text, expected[client]));

        using var check = f.OpenContext();
        var value = Assert.Single(check.DbFieldValues.AsNoTracking().Where(v => v.RecordId == record && v.FieldId == text));
        Assert.Contains(value.Value, expected);
        AssertHealthy(check);
    }

    // Устаревшие редакторы успели прочитать запись и значение. Сосед удаляет записи до их
    // сохранения: ожидаем обычное завершение, а не восстановление данных или ошибку FK.
    // Текст и дата проверяются отдельно: дата дополнительно пишет calendar_index. В одном
    // варианте значение осталось в трекере редактора, в другом прочитано без его сохранения
    // в трекере: это разные пути разрешения конфликта внутри SetFieldValue.
    [SkippableTheory]
    [InlineData("sqlite", "text", true)]
    [InlineData("sqlite-shared", "text", true)]
    [InlineData("postgres", "text", true)]
    [InlineData("sqlite", "date", true)]
    [InlineData("sqlite-shared", "date", true)]
    [InlineData("postgres", "date", true)]
    [InlineData("sqlite", "text", false)]
    [InlineData("sqlite-shared", "text", false)]
    [InlineData("postgres", "text", false)]
    [InlineData("sqlite", "date", false)]
    [InlineData("sqlite-shared", "date", false)]
    [InlineData("postgres", "date", false)]
    public void Deleted_records_are_not_resurrected_or_reported_as_failed_by_stale_editors(string profile, string type, bool trackValue)
    {
        using var f = new Fixture(profile);
        var collection = SampleData.AddCollection(f.Repo).Id!.Value;
        var field = SampleData.AddField(f.Repo, collection, "Правка", type).Id!.Value;
        var records = Enumerable.Range(0, Clients).Select(client =>
        {
            var id = SampleData.AddRecord(f.Repo, collection, $"Удаляемая {client}").Id!.Value;
            f.Repo.SetFieldValue(id, field, type == "date" ? Date(client) : $"До {client}");
            return id;
        }).ToArray();
        var recordIds = records.ToHashSet();

        var apiError = Record.Exception(() => RunClients(f,
            (repo, _, client) => repo.SetFieldValue(records[client], field, type == "date" ? Date(client + 10) : $"После {client}"),
            beforeStart: (repo, ctx, client) =>
            {
                Assert.NotNull(repo.GetRecord(records[client]));
                var value = Assert.Single(ctx.DbFieldValues.Where(v => v.RecordId == records[client] && v.FieldId == field));
                if (!trackValue) ctx.Entry(value).State = EntityState.Detached;
            },
            whenReady: () =>
            {
                using var ctx = f.OpenContext();
                var repo = new ConstructorRepository(ctx);
                foreach (var id in records) repo.DeleteRecord(id);
            }));

        using var check = f.OpenContext();
        Assert.Empty(check.DbRecords.Where(r => r.CollectionId == collection));
        Assert.False(check.DbFieldValues.Any(v => recordIds.Contains(v.RecordId)));
        Assert.False(check.CalendarIndex.Any(i => recordIds.Contains(i.RecordId)));
        AssertHealthy(check);
        _output.WriteLine($"{profile}, {type}, trackValue={trackValue}: после удаления проверены отсутствие записей, значений и проекций, целостность и внешние ключи; данные не восстановлены. Ошибка API: {apiError is not null}.");
        Assert.Null(apiError);
    }

    // Ошибка после записи значения, но до аудита — такая же неопределенность результата,
    // как BUSY в середине составной правки. Пользователь не должен получить отказ при
    // фактически сохраненной части операции. Сбой вызывается настоящим триггером базы.
    [SkippableTheory]
    [InlineData("sqlite", "text")]
    [InlineData("sqlite-shared", "text")]
    [InlineData("postgres", "text")]
    [InlineData("sqlite", "date")]
    [InlineData("sqlite-shared", "date")]
    [InlineData("postgres", "date")]
    public void Failed_field_edit_leaves_value_log_and_calendar_unchanged(string profile, string type)
    {
        using var f = new Fixture(profile);
        const string marker = "multiuser-audit-failure";
        var collection = SampleData.AddCollection(f.Repo, "Атомарность правки").Id!.Value;
        var field = (type == "date"
            ? SampleData.AddCalendarDateField(f.Repo, collection, "Правка")
            : SampleData.AddField(f.Repo, collection, "Правка")).Id!.Value;
        var record = SampleData.AddRecord(f.Repo, collection, "Неуспешная операция").Id!.Value;
        var oldValue = type == "date" ? Date(0) : "До отказа";
        var newValue = type == "date" ? Date(1) : "После отказа";
        f.Repo.SetFieldValue(record, field, oldValue);
        f.Manager.Context.ChangeTracker.Clear();

        string logBefore;
        string calendarBefore;
        using (var before = f.OpenContext())
        {
            Assert.Equal(oldValue, Assert.Single(before.DbFieldValues.AsNoTracking()
                .Where(v => v.RecordId == record && v.FieldId == field)).Value);
            Assert.Equal(type == "date" ? 1 : 0, before.CalendarIndex.Count(i => i.RecordId == record));
            logBefore = LogSnapshot(before);
            calendarBefore = CalendarSnapshot(before, record);
            var trigger = before.IsPostgres
                ? $"""
                    CREATE FUNCTION multiuser_audit_failure() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                        IF NEW.id = {record} THEN RAISE EXCEPTION '{marker}'; END IF;
                        RETURN NEW;
                    END;
                    $$;
                    CREATE TRIGGER multiuser_audit_failure BEFORE UPDATE ON db_records
                    FOR EACH ROW EXECUTE FUNCTION multiuser_audit_failure();
                    """
                : $"""
                    CREATE TRIGGER multiuser_audit_failure BEFORE UPDATE ON db_records
                    WHEN NEW.Id = {record}
                    BEGIN SELECT RAISE(ABORT, '{marker}'); END;
                    """;
            before.Database.ExecuteSqlRaw(trigger);
        }

        Exception? apiError;
        using (var writer = f.OpenContext())
        {
            var repo = new ConstructorRepository(writer);
            apiError = Record.Exception(() => repo.SetFieldValue(record, field, newValue));
        }
        Assert.NotNull(apiError);
        Assert.Contains(marker, apiError.ToString());

        using var check = f.OpenContext();
        var actualValue = Assert.Single(check.DbFieldValues.AsNoTracking()
            .Where(v => v.RecordId == record && v.FieldId == field)).Value;
        var logAfter = LogSnapshot(check);
        var calendarAfter = CalendarSnapshot(check, record);
        _output.WriteLine($"{profile}, {type}: подтвержден отказ триггера {marker}. Значение неизменно: {actualValue == oldValue}; журнал неизменен: {logAfter == logBefore}; проекция неизменна: {calendarAfter == calendarBefore}.");
        AssertHealthy(check);
        Assert.Equal(oldValue, actualValue);
        Assert.Equal(logBefore, logAfter);
        Assert.Equal(calendarBefore, calendarAfter);
    }

    private static string LogSnapshot(AppDbContext ctx) =>
        JsonSerializer.Serialize(ctx.ChangeLog.AsNoTracking().OrderBy(l => l.Id).ToList());

    private static string CalendarSnapshot(AppDbContext ctx, int record) =>
        JsonSerializer.Serialize(ctx.CalendarIndex.AsNoTracking().Where(i => i.RecordId == record)
            .OrderBy(i => i.FieldId)
            .Select(i => new { i.RecordId, i.FieldId, i.CollectionId, i.StartIso, i.EndIso, i.AllDay }).ToList());

    private static void RunClients(Fixture fixture,
        Action<ConstructorRepository, AppDbContext, int> operation,
        Action<ConstructorRepository, AppDbContext, int>? beforeStart = null,
        Action? whenReady = null)
    {
        var ready = new CountdownEvent(Clients);
        var start = new ManualResetEventSlim(false);
        var errors = new ConcurrentQueue<Exception>();
        var threads = Enumerable.Range(0, Clients).Select(client => new Thread(() =>
        {
            var signalled = false;
            try
            {
                using var ctx = fixture.OpenContext();
                var repo = new ConstructorRepository(ctx);
                beforeStart?.Invoke(repo, ctx, client);
                ready.Signal();
                signalled = true;
                if (!start.Wait(TimeSpan.FromSeconds(60)))
                    throw new TimeoutException("Не освобожден барьер начала операций");
                operation(repo, ctx, client);
            }
            catch (Exception ex) { errors.Enqueue(new InvalidOperationException($"Участник {client}", ex)); }
            finally { if (!signalled) ready.Signal(); }
        }) { IsBackground = true, Name = $"concurrent-lifecycle-{client}" }).ToArray();

        var launched = new List<Thread>(Clients);
        var readyInTime = false;
        var joined = true;
        try
        {
            foreach (var thread in threads)
            {
                thread.Start();
                launched.Add(thread);
            }
            readyInTime = ready.Wait(TimeSpan.FromSeconds(45));
            if (readyInTime && errors.IsEmpty) whenReady?.Invoke();
        }
        finally
        {
            // Даже ошибка контроллера не оставляет детей на барьере и не закрывает события
            // под работающим потоком. Команды ограничены таймаутом контекста в 20 секунд.
            start.Set();
            var watch = Stopwatch.StartNew();
            foreach (var thread in launched)
            {
                var left = TimeSpan.FromSeconds(120) - watch.Elapsed;
                joined &= thread.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            }
            // При таймауте поздний участник еще может обратиться к барьеру. Его ошибка
            // должна попасть в отчет теста, а не закрыть весь testhost из finally потока.
            if (joined)
            {
                ready.Dispose();
                start.Dispose();
            }
        }
        Assert.True(readyInTime, "Участники не подготовились за 45 секунд");
        Assert.True(joined, "Операции участников не завершились за 120 секунд");
        Assert.True(errors.IsEmpty, string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
    }

    private static void AssertHealthy(AppDbContext ctx)
    {
        ctx.Database.OpenConnection();
        var connection = ctx.Database.GetDbConnection();
        long Scalar(string sql)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandTimeout = 20;
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        Assert.Equal(0, Scalar(@"
            SELECT COUNT(*) FROM db_field_values v
            LEFT JOIN db_records r ON r.id = v.recordid
            LEFT JOIN db_fields f ON f.id = v.fieldid
            WHERE r.id IS NULL OR f.id IS NULL OR r.collectionid <> f.collectionid"));
        Assert.Equal(0, Scalar(@"
            SELECT COUNT(*) FROM calendar_index i
            LEFT JOIN db_records r ON r.id = i.recordid
            LEFT JOIN db_fields f ON f.id = i.fieldid
            LEFT JOIN db_field_values v ON v.recordid = i.recordid AND v.fieldid = i.fieldid
            WHERE r.id IS NULL OR f.id IS NULL OR v.recordid IS NULL
                OR i.collectionid <> r.collectionid OR i.collectionid <> f.collectionid"));
        Assert.Equal(0, Scalar(@"
            SELECT COUNT(*) FROM (
                SELECT recordid, fieldid FROM db_field_values GROUP BY recordid, fieldid HAVING COUNT(*) > 1
            ) duplicates"));

        if (ctx.IsPostgres)
        {
            Assert.Equal(0, Scalar(@"
                SELECT COUNT(*) FROM (
                    SELECT collectionid, substr(startiso, 1, 10), fieldid, COUNT(*)::int
                    FROM calendar_index GROUP BY 1, 2, 3
                    EXCEPT SELECT collectionid, startday, fieldid, events
                    FROM calendar_day_counts WHERE events > 0
                ) missing"));
            Assert.Equal(0, Scalar(@"
                SELECT COUNT(*) FROM (
                    SELECT collectionid, startday, fieldid, events FROM calendar_day_counts WHERE events > 0
                    EXCEPT SELECT collectionid, substr(startiso, 1, 10), fieldid, COUNT(*)::int
                    FROM calendar_index GROUP BY 1, 2, 3
                ) extra"));
        }
        else
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", cmd.ExecuteScalar());
            cmd.CommandText = "PRAGMA foreign_key_check";
            using var reader = cmd.ExecuteReader();
            Assert.False(reader.Read(), "Нарушены внешние ключи SQLite");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string? _postgresDatabase;
        private bool _databaseCreated;
        private DatabaseManager? _manager;
        public string Path { get; }
        public DatabaseManager Manager => _manager!;
        public ConstructorRepository Repo => Manager.Constructor;

        public Fixture(string profile)
        {
            if (profile == "postgres")
            {
                Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL (порт 5455) не поднят");
                _postgresDatabase = $"sb_concurrent_{Guid.NewGuid():N}";
                Path = new NpgsqlConnectionStringBuilder(PgTestServer.ConnectionString(_postgresDatabase))
                {
                    Pooling = false, Timeout = 5, CommandTimeout = 20,
                }.ConnectionString;
            }
            else
            {
                Path = TempDatabase.NewTempPath();
                if (profile == "sqlite-shared") NetworkPath.ForceNetwork(Path);
            }

            try
            {
                if (_postgresDatabase is not null)
                {
                    AdminSql($"CREATE DATABASE \"{_postgresDatabase}\"");
                    _databaseCreated = true;
                }
                _manager = DatabaseManager.Open(1, "concurrent-lifecycle", Path,
                    provider: _postgresDatabase is null ? "sqlite" : "postgres");
                if (_postgresDatabase is null)
                {
                    using var check = OpenContext();
                    check.Database.OpenConnection();
                    using var cmd = check.Database.GetDbConnection().CreateCommand();
                    cmd.CommandText = "PRAGMA journal_mode";
                    Assert.Equal(profile == "sqlite-shared" ? "delete" : "wal", cmd.ExecuteScalar());
                    if (profile == "sqlite-shared")
                    {
                        cmd.CommandText = "PRAGMA synchronous";
                        Assert.Equal(2L, Convert.ToInt64(cmd.ExecuteScalar()));
                        cmd.CommandText = "PRAGMA mmap_size";
                        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
                    }
                }
            }
            catch { Dispose(); throw; }
        }

        public AppDbContext OpenContext()
        {
            var ctx = new AppDbContext(Path);
            try
            {
                ctx.Database.SetCommandTimeout(20);
                ctx.ApplyPerformancePragmas();
                return ctx;
            }
            catch { ctx.Dispose(); throw; }
        }

        private static void AdminSql(string sql)
        {
            using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(PgTestServer.ConnectionString("postgres"))
            {
                Pooling = false, Timeout = 5, CommandTimeout = 20,
            }.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            try { _manager?.Dispose(); }
            finally
            {
                if (_postgresDatabase is null)
                {
                    TempDatabase.Cleanup(Path);
                    NetworkPath.Forget(Path);
                }
                else if (_databaseCreated)
                {
                    // Имя создано этим экземпляром, чужие базы и общая схема стенда не трогаются.
                    AdminSql($"DROP DATABASE \"{_postgresDatabase}\" WITH (FORCE)");
                    _databaseCreated = false;
                }
            }
        }
    }
}
