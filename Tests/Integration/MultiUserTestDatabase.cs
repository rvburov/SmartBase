using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>Собственная база для конкурентных проверок модулей. Общие базы стенда не сбрасывает.</summary>
internal sealed class MultiUserTestDatabase : IDisposable
{
    private readonly string? _postgresDatabase;
    private DatabaseManager? _manager;
    private bool _created;
    internal string Path { get; }
    internal DatabaseManager Manager => _manager!;
    internal ConstructorRepository Repo => Manager.Constructor;

    internal MultiUserTestDatabase(string profile)
    {
        if (profile == "postgres")
        {
            Skip.IfNot(PgTestServer.IsAvailable, "Локальный тестовый PostgreSQL на 5455 недоступен");
            _postgresDatabase = $"sb_modules_{Guid.NewGuid():N}";
            Path = new NpgsqlConnectionStringBuilder(PgTestServer.ConnectionString(_postgresDatabase))
            {
                Pooling = false, Timeout = 5, CommandTimeout = 20,
            }.ConnectionString;
        }
        else
        {
            if (profile is not ("sqlite" or "sqlite-shared")) throw new ArgumentException("Неизвестный профиль", nameof(profile));
            Path = TempDatabase.NewTempPath();
            if (profile == "sqlite-shared") NetworkPath.ForceNetwork(Path);
        }
        try
        {
            if (_postgresDatabase is not null)
            {
                Admin($"CREATE DATABASE \"{_postgresDatabase}\"");
                _created = true;
            }
            _manager = DatabaseManager.Open(1, "multiuser-module", Path,
                provider: _postgresDatabase is null ? "sqlite" : "postgres");
        }
        catch { Dispose(); throw; }
    }

    internal AppDbContext OpenContext()
    {
        var context = new AppDbContext(Path);
        try { context.Database.SetCommandTimeout(20); context.ApplyPerformancePragmas(); return context; }
        catch { context.Dispose(); throw; }
    }

    internal static void RunClients(MultiUserTestDatabase fixture, int count,
        Action<ConstructorRepository, AppDbContext, int> action,
        Action<ConstructorRepository, AppDbContext, int>? beforeStart = null, Action? whenReady = null)
    {
        var ready = new CountdownEvent(count);
        var start = new ManualResetEventSlim(false);
        var errors = new ConcurrentQueue<Exception>();
        var threads = Enumerable.Range(0, count).Select(client => new Thread(() =>
        {
            var announced = false;
            try
            {
                using var context = fixture.OpenContext();
                var repo = new ConstructorRepository(context);
                beforeStart?.Invoke(repo, context, client);
                ready.Signal();
                announced = true;
                if (!start.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("Барьер старта не освобожден");
                action(repo, context, client);
            }
            catch (Exception ex) { errors.Enqueue(new InvalidOperationException($"Участник {client}", ex)); }
            finally { if (!announced) ready.Signal(); }
        }) { IsBackground = true, Name = $"module-client-{client}" }).ToArray();
        var launched = new List<Thread>();
        var prepared = false;
        var joined = true;
        try
        {
            foreach (var thread in threads) { thread.Start(); launched.Add(thread); }
            prepared = ready.Wait(TimeSpan.FromSeconds(45));
            if (prepared && errors.IsEmpty) whenReady?.Invoke();
        }
        finally
        {
            start.Set();
            var watch = Stopwatch.StartNew();
            foreach (var thread in launched)
            {
                var remaining = TimeSpan.FromSeconds(120) - watch.Elapsed;
                joined &= thread.Join(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            }
            if (joined) { ready.Dispose(); start.Dispose(); }
        }
        Assert.True(prepared, "Подготовка участников заняла больше 45 секунд");
        Assert.True(joined, "Участники не завершили команды за 120 секунд");
        Assert.True(errors.IsEmpty, string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
    }

    internal static void AssertHealthy(AppDbContext context)
    {
        context.Database.OpenConnection();
        long Scalar(string sql)
        {
            using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandTimeout = 20;
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        Assert.Equal(0, Scalar(@"SELECT COUNT(*) FROM db_field_values v
            LEFT JOIN db_records r ON r.id=v.recordid LEFT JOIN db_fields f ON f.id=v.fieldid
            WHERE r.id IS NULL OR f.id IS NULL OR r.collectionid<>f.collectionid"));
        Assert.Equal(0, Scalar(@"SELECT COUNT(*) FROM calendar_index i
            LEFT JOIN db_records r ON r.id=i.recordid LEFT JOIN db_fields f ON f.id=i.fieldid
            LEFT JOIN db_field_values v ON v.recordid=i.recordid AND v.fieldid=i.fieldid
            WHERE r.id IS NULL OR f.id IS NULL OR v.recordid IS NULL
            OR i.collectionid<>r.collectionid OR i.collectionid<>f.collectionid"));
        Assert.Equal(0, Scalar(@"SELECT COUNT(*) FROM (
            SELECT recordid,fieldid FROM db_field_values GROUP BY recordid,fieldid HAVING COUNT(*)>1) duplicates"));
        Assert.Equal(0, Scalar(@"SELECT COUNT(*) FROM (
            SELECT collectionid,substr(startiso,1,10),fieldid,COUNT(*) FROM calendar_index GROUP BY 1,2,3
            EXCEPT SELECT collectionid,startday,fieldid,events FROM calendar_day_counts WHERE events>0) missing"));
        Assert.Equal(0, Scalar(@"SELECT COUNT(*) FROM (
            SELECT collectionid,startday,fieldid,events FROM calendar_day_counts WHERE events>0
            EXCEPT SELECT collectionid,substr(startiso,1,10),fieldid,COUNT(*) FROM calendar_index GROUP BY 1,2,3) extra"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM calendar_day_counts WHERE events<0"));
        if (!context.IsPostgres)
        {
            using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", command.ExecuteScalar());
            command.CommandText = "PRAGMA foreign_key_check";
            using var reader = command.ExecuteReader();
            Assert.False(reader.Read(), "Нарушены внешние ключи SQLite");
        }
    }

    private static void Admin(string sql)
    {
        using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(PgTestServer.ConnectionString("postgres"))
        { Pooling = false, Timeout = 5, CommandTimeout = 20 }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { _manager?.Dispose(); }
        finally
        {
            if (_postgresDatabase is null) { TempDatabase.Cleanup(Path); NetworkPath.Forget(Path); }
            else if (_created) { Admin($"DROP DATABASE \"{_postgresDatabase}\" WITH (FORCE)"); _created = false; }
        }
    }
}
