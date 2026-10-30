using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>Одновременная доставка одного вложения и откат квитанции на настоящих провайдерах.</summary>
public sealed class FileMessageDurabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sqlite_concurrent_deliveries_register_one_file_and_replay_the_same_receipt() =>
        ConcurrentDeliveries(postgres: false);

    [PgBatchFact]
    public void Postgres_concurrent_deliveries_register_one_file_and_replay_the_same_receipt() =>
        ConcurrentDeliveries(postgres: true);

    [PgBatchFact]
    public void Postgres_receipt_failure_rolls_back_documents_and_a_later_delivery_can_retry()
    {
        using var database = new Fixture(postgres: true);
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Name: "Первый"), new FileMessageBook.Row(3, Name: "Второй"));
        string before;
        using (var context = database.OpenContext())
        {
            before = Snapshot(context);
            context.Database.ExecuteSqlRaw("""
                CREATE FUNCTION sb_file_reject_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.key LIKE 'extension-state:%:file:%' THEN
                        RAISE EXCEPTION 'file receipt fixture failure';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER sb_file_reject_receipt BEFORE INSERT ON db_meta
                    FOR EACH ROW EXECUTE FUNCTION sb_file_reject_receipt();
                """);
            ActionResult? result = null;
            var error = Record.Exception(() => result = Call(new ConstructorRepository(context), database.Shape, bytes));
            Assert.True(error is not null || result is { Ok: false });
        }
        using (var context = database.OpenContext())
        {
            Assert.Equal(before, Snapshot(context));
            context.Database.ExecuteSqlRaw("DROP TRIGGER sb_file_reject_receipt ON db_meta; DROP FUNCTION sb_file_reject_receipt()");
            var retry = Call(new ConstructorRepository(context), database.Shape, bytes);
            Assert.True(retry.Ok, retry.Explanation);
            Assert.Equal(2, retry.Rows);
        }
        using var reopened = database.OpenContext();
        var beforeReplay = Snapshot(reopened);
        var replay = Call(new ConstructorRepository(reopened), database.Shape, bytes);
        Assert.True(replay.Ok, replay.Explanation);
        Assert.Equal(0, replay.Rows);
        Assert.Equal(beforeReplay, Snapshot(reopened));
    }

    private static void ConcurrentDeliveries(bool postgres)
    {
        var database = new Fixture(postgres);
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Name: "Первый"), new FileMessageBook.Row(5, Name: "Второй"));
        ConcurrentTasks.Run(4, index => new Client(index), (_, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            using var context = database.OpenContext();
            return Call(new ConstructorRepository(context), database.Shape, bytes);
        }, verify: results =>
        {
            Assert.All(results, result => Assert.True(result.Ok, result.Explanation));
            Assert.Single(results, result => result.Rows == 2);
            Assert.Equal(3, results.Count(result => result.Rows == 0));
            Assert.Single(results.Select(result => result.Answer).Distinct(StringComparer.Ordinal));
            using var context = database.OpenContext();
            var repo = new ConstructorRepository(context);
            Assert.Equal(new[] { "АБВГ.301121.001", "АБВГ.301121.002", "АБВГ.301121.003" },
                DocRegistrar.Designations(repo, database.Shape).Order(StringComparer.Ordinal));
            Assert.Single(repo.GetRecords(database.Shape.CollectionId), row => row.Title == "Первый");
            Assert.Single(repo.GetRecords(database.Shape.CollectionId), row => row.Title == "Второй");
            var before = Snapshot(context);
            var replay = Call(repo, database.Shape, bytes);
            Assert.Equal(0, replay.Rows);
            Assert.Equal(results[0].Answer, replay.Answer);
            Assert.Equal(before, Snapshot(context));
        }, shared: database);
    }

    private static ActionResult Call(ConstructorRepository repo, RegistryShape shape, byte[] bytes) =>
        new RegisterMessageAction().Run(new ActionContext(repo, 1, shape.CollectionId, Now,
            new Dictionary<string, string> { [DocRegistryExtension.OrgCodesKey] = "АБВГ" },
            Session: new object(), Channel: "durability-channel"), new ActionInput(new Dictionary<string, string>
            {
                [RegisterMessageAction.SenderField] = "sender-1",
                [RegisterMessageAction.MessageIdField] = "event-1",
            }, Bytes: bytes, FileName: "Список.xlsx"));

    private static string Snapshot(AppDbContext context) => JsonSerializer.Serialize(new
    {
        Records = context.DbRecords.AsNoTracking().OrderBy(record => record.Id).Select(record => new
            { record.Id, record.CollectionId, record.Title, record.Position, record.CreatedAt, record.UpdatedAt,
              record.CreatedBy, record.UpdatedBy }).ToArray(),
        Values = context.DbFieldValues.AsNoTracking().OrderBy(value => value.RecordId).ThenBy(value => value.FieldId)
            .Select(value => new { value.RecordId, value.FieldId, value.Value }).ToArray(),
        Log = context.ChangeLog.AsNoTracking().OrderBy(change => change.Id).ToArray(),
        Meta = context.DbMeta.AsNoTracking().OrderBy(item => item.Key).Select(item => new { item.Key, item.Value }).ToArray(),
    });

    private sealed record Client(int Index) : IDisposable
    {
        public void Dispose() { }
    }

    /// <summary>Каждый тест получает свой файл или отдельную БД; общие базы стенда не меняются.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly TempDatabase? _sqlite;
        private readonly string? _postgres;
        private DatabaseManager? _manager;
        private bool _created;
        private readonly string _path;
        internal RegistryShape Shape { get; }

        internal Fixture(bool postgres)
        {
            if (postgres)
            {
                _postgres = "sb_file_" + Guid.NewGuid().ToString("N");
                _path = PgBatchFactAttribute.ConnectionString(_postgres);
            }
            else
            {
                _sqlite = new TempDatabase("file-message-concurrency");
                _path = _sqlite.Path;
                _manager = _sqlite.Manager;
            }
            try
            {
                if (_postgres is not null)
                {
                    Admin($"CREATE DATABASE \"{_postgres}\"");
                    _created = true;
                    _manager = DatabaseManager.Open(1, "file-durability", _path, provider: "postgres");
                }
                var repo = _manager!.Constructor;
                Shape = JournalStand.Shape(repo, JournalStand.Create(repo));
                JournalStand.AddRow(repo, Shape, "Существующий", "АБВГ.301121.001");
            }
            catch { Dispose(); throw; }
        }

        internal AppDbContext OpenContext()
        {
            var context = new AppDbContext(_path);
            try { context.Database.SetCommandTimeout(20); context.ApplyPerformancePragmas(); return context; }
            catch { context.Dispose(); throw; }
        }

        private static void Admin(string sql)
        {
            using var connection = new NpgsqlConnection(PgBatchFactAttribute.ConnectionString("postgres"));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            if (_sqlite is not null) { _sqlite.Dispose(); return; }
            try { _manager?.Dispose(); }
            finally
            {
                if (_created)
                {
                    Admin($"DROP DATABASE \"{_postgres}\" WITH (FORCE)");
                    _created = false;
                }
            }
        }
    }
}
