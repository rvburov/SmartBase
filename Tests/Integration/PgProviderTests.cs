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
        var date = SampleData.AddField(repo, col.Id!.Value, "Дата", "date");
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
        var date = SampleData.AddField(repo, col.Id!.Value, "Дата", "date");
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
        Assert.Equal(0, PgScalar(cs1, $"SELECT COUNT(*) FROM db_records WHERE collectionid = {srcColId}")); // источник удалён
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
}
