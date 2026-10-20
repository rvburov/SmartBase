using Microsoft.Data.Sqlite;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Сводки календаря на PostgreSQL (<c>calendar_day_counts</c>, <c>calendar_spans</c>) держат триггеры
/// проекции на целый запрос (миграция CalendarStatementTriggersPg): одна сводка по всем строкам
/// запроса вместо вызова функции на каждую строку. На любом пути записи — правка даты, очистка,
/// удаление записи и поля (каскад), дубль коллекции, пакетное удаление, перенос между базами в обе
/// стороны — сводка дней равна проекции, а граница длительности не меньше настоящей. Без стенда
/// PostgreSQL (порт 5455) тесты помечаются Skipped.
/// </summary>
public sealed class PgCalendarSummaryTests
{
    private static string Timed(DateTime start, DateTime end) => new DateValue(start, end, false).ToStorage();
    private static string AllDay(DateTime start, DateTime end) => new DateValue(start, end, true).ToStorage();

    [SkippableFact]
    public void Summaries_follow_every_write_path_on_postgres()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);
        using var pg = DatabaseManager.Open(101, "pg-summary", cs, provider: "postgres");
        var repo = pg.Constructor;
        var d    = new DateTime(2026, 7, 1);

        var a  = SampleData.AddCollection(repo, "Встречи").Id!.Value;
        var fa = SampleData.AddCalendarDateField(repo, a, name: "Начало").Id!.Value;
        var fx = SampleData.AddCalendarDateField(repo, a, name: "Напоминание").Id!.Value;
        var b  = SampleData.AddCollection(repo, "Отпуска").Id!.Value;
        var fb = SampleData.AddCalendarDateField(repo, b, name: "Начало").Id!.Value;

        int Event(int col, int field, string value)
        {
            var id = SampleData.AddRecord(repo, col, "Событие").Id!.Value;
            repo.SetFieldValue(id, field, value);
            return id;
        }

        var moved   = Event(a, fa, Timed(d.AddHours(10), d.AddHours(11)));
        var retimed = Event(a, fa, Timed(d.AddHours(12), d.AddHours(13)));
        var cleared = Event(a, fa, Timed(d.AddHours(14), d.AddHours(15)));
        var removed = Event(a, fa, Timed(d.AddDays(1).AddHours(9), d.AddDays(1).AddHours(10)));
        Event(a, fx, Timed(d.AddHours(8), d.AddHours(9)));
        for (var i = 0; i < 4; i++) Event(b, fb, AllDay(d.AddDays(i), d.AddDays(i + 1)));
        for (var i = 0; i < 30; i++) Event(b, fb, Timed(d.AddDays(i % 9).AddHours(9), d.AddDays(i % 9).AddHours(9 + i % 5)));

        Check(cs, "заполнение");
        Assert.InRange(Spans(cs)[(a, 0)], 3600, 3602);

        repo.SetFieldValue(moved, fa, Timed(d.AddDays(3).AddHours(10), d.AddDays(40)));
        Check(cs, "перенос на другой день и растяжение");
        repo.SetFieldValue(retimed, fa, Timed(d.AddHours(16), d.AddHours(17)));
        Check(cs, "другое время того же дня");
        repo.SetFieldValue(cleared, fa, "");
        Check(cs, "очистка даты");
        repo.DeleteRecord(removed);
        Check(cs, "удаление записи");
        repo.DeleteField(fx);
        Check(cs, "удаление поля");

        var copy = repo.DuplicateCollection(b).Id!.Value;
        Check(cs, "дубль коллекции");
        Assert.Equal(Spans(cs)[(b, 0)], Spans(cs)[(copy, 0)]);   // своя граница копии — по ее же событиям

        repo.DeleteCollectionData(b, batchSize: 7);
        Check(cs, "пакетное удаление коллекции");

        // Перенос в SQLite и обратно: на PG сводки уходят вместе с удалением и приходят с копией.
        using var lite = new TempDatabase(dbId: 1, name: "lite");
        var inLite = lite.Constructor.MoveCollectionIsolated(cs, copy, targetGroupId: null, batchSize: 7,
                                                             sourceDbId: 101, targetDbId: 1);
        Check(cs, "перенос из PG");
        var back = repo.MoveCollectionIsolated(lite.Path, inLite, targetGroupId: null, batchSize: 7,
                                               sourceDbId: 1, targetDbId: 101);
        Check(cs, "перенос в PG");
        Assert.Equal(34, repo.CountRecords(back));
        Assert.Equal(0, PgScalar(cs, $"SELECT COUNT(*) FROM db_meta WHERE key LIKE '{ConstructorRepository.PendingPrefix}%'"));

        repo.TidyCalendarDayCounts();
        Check(cs, "уборка нулевых строк");
    }

    /// <summary>Перенос из SQLite в PG откатывается, если исходник не удалить: копии на PG нет,
    /// источник цел, маркеров нет ни в одной базе (маркер приемника снимается сравнением на PG).</summary>
    [SkippableFact]
    public void Move_to_postgres_is_rolled_back_when_the_source_cannot_be_deleted()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);
        using var pg   = DatabaseManager.Open(101, "pg-move", cs, provider: "postgres");
        using var lite = new TempDatabase(dbId: 1, name: "lite");
        var col = SampleData.AddCollection(lite.Constructor, "Переносимая").Id!.Value;
        SampleData.AddRecords(lite.Constructor, col, count: 12);
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = lite.Path, Pooling = false }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER test_boom BEFORE DELETE ON db_records BEGIN SELECT RAISE(ABORT, 'test boom'); END;";
            cmd.ExecuteNonQuery();
        }
        var before = PgScalar(cs, "SELECT COUNT(*) FROM db_collections");

        Assert.ThrowsAny<Exception>(() => pg.Constructor.MoveCollectionIsolated(lite.Path, col, targetGroupId: null,
                                                                               batchSize: 5, sourceDbId: 1, targetDbId: 101));

        Assert.Equal(before, PgScalar(cs, "SELECT COUNT(*) FROM db_collections"));
        Assert.Equal(12, lite.Constructor.CountRecords(col));
        Assert.Equal(0, PgScalar(cs, $"SELECT COUNT(*) FROM db_meta WHERE key LIKE '{ConstructorRepository.PendingPrefix}%'"));
    }

    // Сводка дней равна проекции, граница длительности у каждой пары (коллекция, вид) не меньше
    // настоящей наибольшей длительности.
    private static void Check(string cs, string step)
    {
        Assert.True(PgScalar(cs, @"
            SELECT COUNT(*) FROM (
                SELECT collectionid, substr(startiso, 1, 10), fieldid, COUNT(*)::int FROM calendar_index GROUP BY 1, 2, 3
                EXCEPT SELECT collectionid, startday, fieldid, events FROM calendar_day_counts WHERE events > 0) x") == 0
                    && PgScalar(cs, @"
            SELECT COUNT(*) FROM (
                SELECT collectionid, startday, fieldid, events FROM calendar_day_counts WHERE events > 0
                EXCEPT SELECT collectionid, substr(startiso, 1, 10), fieldid, COUNT(*)::int FROM calendar_index GROUP BY 1, 2, 3) x") == 0,
                    $"{step}: сводка дней разошлась с проекцией");
        Assert.True(PgScalar(cs, @"
            SELECT COUNT(*) FROM (
                SELECT collectionid, allday, MAX(calendar_span_seconds(startiso, endiso)) AS actual
                FROM calendar_index GROUP BY collectionid, allday) p
            LEFT JOIN calendar_spans s USING (collectionid, allday)
            WHERE s.maxseconds IS NULL OR s.maxseconds < p.actual") == 0,
                    $"{step}: граница длительности меньше настоящей");
    }

    private static Dictionary<(int, int), long> Spans(string cs)
    {
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT collectionid, allday, maxseconds FROM calendar_spans";
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<(int, int), long>();
        while (r.Read()) map[(r.GetInt32(0), r.GetInt32(1))] = r.GetInt64(2);
        return map;
    }

    private static void ResetSchema(string cs)
    {
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
        cmd.ExecuteNonQuery();
    }

    private static long PgScalar(string cs, string sql)
    {
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }
}
