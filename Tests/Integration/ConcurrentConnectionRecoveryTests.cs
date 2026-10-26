using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>Обрыв настоящих серверных сессий только своей PG-базы и повтор через свежие подключения.</summary>
public sealed class ConcurrentConnectionRecoveryTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lost_server_connections_do_not_prevent_fresh_sessions_from_reading_and_writing(bool write)
    {
        using var database = new MultiUserTestDatabase("postgres");
        var collection = SampleData.AddCollection(database.Repo, "После обрыва").Id!.Value;
        var field = SampleData.AddField(database.Repo, collection, "Значение").Id!.Value;
        var ids = Enumerable.Range(0, 8).Select(i =>
        {
            var id = SampleData.AddRecord(database.Repo, collection, $"Участник {i}").Id!.Value;
            database.Repo.SetFieldValue(id, field, $"До {i}");
            return id;
        }).ToArray();
        var backends = new ConcurrentDictionary<int, int>();
        var serverDatabase = new NpgsqlConnectionStringBuilder(database.Path).Database;
        MultiUserTestDatabase.RunClients(database, ids.Length,
            (repo, _, client) =>
            {
                try
                {
                    if (write) repo.SetFieldValue(ids[client], field, $"После {client}");
                    else repo.GetFieldValues(ids[client]);
                }
                catch (Exception ex) when (ex.GetBaseException() is NpgsqlException)
                {
                    // Разорванное соединение может бросить, а драйвер может уже знать о
                    // разрыве и открыть новое. Проверяем данные и восстановление в обоих случаях.
                }
                using var fresh = database.OpenContext();
                var recovered = new ConstructorRepository(fresh);
                if (write) recovered.SetFieldValue(ids[client], field, $"После {client}");
                Assert.Equal(write ? $"После {client}" : $"До {client}", recovered.GetFieldValues(ids[client])[field]);
            },
            beforeStart: (_, context, client) =>
            {
                context.Database.OpenConnection();
                using var command = context.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT pg_backend_pid()";
                backends[client] = Convert.ToInt32(command.ExecuteScalar());
            },
            whenReady: () =>
            {
                using var admin = new NpgsqlConnection(PgTestServer.ConnectionString("postgres"));
                admin.Open();
                Assert.Equal(ids.Length, backends.Values.Distinct().Count());
                foreach (var backend in backends.Values)
                {
                    using var command = admin.CreateCommand();
                    command.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE pid=@pid AND datname=@database";
                    command.Parameters.AddWithValue("pid", backend);
                    command.Parameters.AddWithValue("database", serverDatabase);
                    Assert.Equal(true, command.ExecuteScalar());
                }
            });
        using var check = database.OpenContext();
        Assert.Equal(ids.Length, check.DbRecords.Count(r => r.CollectionId == collection));
        MultiUserTestDatabase.AssertHealthy(check);
    }
}
