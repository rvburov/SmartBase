using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Составная операция <c>InWriteTransaction</c> на PostgreSQL. Serializable там защищает снимок
/// отказом 40001, когда сосед тронул прочитанное, — и без повтора каждый такой отказ доходил бы
/// до человека сбоем шага вместо ответа по свежим данным. Оба теста ставят соседа ровно между
/// чтением и записью первой попытки. Каждый тест — SkippableFact: без стенда sb-pg (порт 5455)
/// помечается Skipped.
/// </summary>
public sealed class PgWriteTransactionTests
{
    private static void ResetSchema(string connString)
    {
        using var conn = new NpgsqlConnection(connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
        cmd.ExecuteNonQuery();
    }

    private static void Execute(string connString, string sql)
    {
        using var conn = new NpgsqlConnection(connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string PgText(string connString, string sql)
    {
        using var conn = new NpgsqlConnection(connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (string)(cmd.ExecuteScalar() ?? "");
    }

    /// <summary>
    /// Сосед поправил запись после снимка: UPDATE первой попытки получает 40001, повтор читает
    /// значение соседа и пишет поверх него. Журнал изменений — от значения соседа: слияние правок
    /// из откаченной попытки забыто, иначе «до» осталось бы старым.
    /// </summary>
    [SkippableFact]
    public void A_neighbour_update_repeats_the_operation_on_fresh_data()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);

        using var m = DatabaseManager.Open(103, "pg-test", cs, provider: "postgres");
        var repo = m.Constructor;
        var col  = SampleData.AddCollection(repo, "Журнал").Id!.Value;
        var id   = repo.AddRecordAfter(new DbRecord { CollectionId = col, Title = "Было" }, afterRecordId: 0).Id!.Value;

        var attempts = 0;
        var seen = repo.InWriteTransaction(() =>
        {
            attempts++;
            var title = repo.GetRecord(id, refresh: true)!.Title;   // первая команда — снимок
            if (attempts == 1) Execute(cs, $"UPDATE db_records SET title = 'Сосед' WHERE id = {id}");
            repo.SetRecordTitle(id, title + " и мы");
            return title;
        });

        Assert.Equal(2, attempts);
        Assert.Equal("Сосед", seen);
        Assert.Equal("Сосед и мы", PgText(cs, $"SELECT title FROM db_records WHERE id = {id}"));
        Assert.DoesNotContain("Было",
            PgText(cs, "SELECT detail FROM change_log WHERE action = 'update' ORDER BY id DESC LIMIT 1"));
    }

    /// <summary>
    /// Две нумерации одной серии: обе прочли «выдано 0», сосед успел записать «№1». Без
    /// Serializable вторая тоже выдала бы «№1»; с ним она получает отказ и при повторе — «№2».
    /// </summary>
    [SkippableFact]
    public void Two_numberings_of_one_series_never_hand_out_the_same_number()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);

        using var m = DatabaseManager.Open(104, "pg-test", cs, provider: "postgres");
        var repo = m.Constructor;
        var col  = SampleData.AddCollection(repo, "Серия").Id!.Value;

        var attempts = 0;
        var number = repo.InWriteTransaction(() =>
        {
            attempts++;
            var next = Issue(repo, col, write: false);
            if (attempts == 1)
            {
                // Сосед — свое подключение и та же составная операция.
                using var other = new AppDbContext(cs);
                var neighbour = new ConstructorRepository(other);
                neighbour.InWriteTransaction(() => Issue(neighbour, col, write: true));
            }
            repo.AddRecordAfter(new DbRecord { CollectionId = col, Title = $"№{next}" }, afterRecordId: 0);
            return next;
        });

        Assert.Equal(2, attempts);
        Assert.Equal(2, number);
        Assert.Equal("№1,№2",
            PgText(cs, $"SELECT string_agg(title, ',' ORDER BY title) FROM db_records WHERE collectionid = {col}"));
    }

    /// <summary>Следующий номер серии — по числу выданных; write — сразу записать его.</summary>
    private static int Issue(ConstructorRepository repo, int collectionId, bool write)
    {
        var next = repo.CountRecords(collectionId) + 1;
        if (write)
            repo.AddRecordAfter(new DbRecord { CollectionId = collectionId, Title = $"№{next}" }, afterRecordId: 0);
        return next;
    }
}
