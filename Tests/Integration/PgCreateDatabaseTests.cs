using Npgsql;
using SmartBase.Database;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Создание базы и роли-владельца на сервере (<see cref="PgDatabaseCreator"/> — логика окна
/// «Создать базу данных PostgreSQL»). Каждый тест — SkippableFact на стенде sb-pg; имена
/// генерируются уникальными (sb_create_test_*/sb_create_role_*) и подчищаются в finally
/// (DROP DATABASE WITH (FORCE) + DROP ROLE), чтобы стенд оставался чистым: в нем должны
/// быть только postgres/smartbase/smartbase2.
/// </summary>
public sealed class PgCreateDatabaseTests
{
    private static PgConnectionInfo Admin => new(
        PgTestServer.Host, PgTestServer.Port, PgDatabaseCreator.MaintenanceDb,
        PgTestServer.User, PgTestServer.Password, "Prefer");

    private static string MaintCs =>
        Admin.ToConnectionString();

    private static (string Db, string Role) FreshNames()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ($"sb_create_test_{suffix}", $"sb_create_role_{suffix}");
    }

    // Имена генерирует сам тест ([a-z0-9_] — см. FreshNames), интерполяция безопасна.
    private static void Exec(string sql)
    {
        using var conn = new NpgsqlConnection(MaintCs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(string sql)
    {
        using var conn = new NpgsqlConnection(MaintCs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static void Cleanup(string db, string role)
    {
        Exec($"DROP DATABASE IF EXISTS {db} WITH (FORCE);");
        Exec($"DROP ROLE IF EXISTS {role};");
    }

    private static bool RoleExists(string role) =>
        Scalar($"SELECT COUNT(*) FROM pg_roles WHERE rolname = '{role}'") == 1;

    private static bool DbExists(string db) =>
        Scalar($"SELECT COUNT(*) FROM pg_database WHERE datname = '{db}'") == 1;

    [SkippableFact]
    public async Task Creates_role_and_database_and_new_role_can_mount()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var (db, role) = FreshNames();
        try
        {
            var outcome = await PgDatabaseCreator.CreateAsync(Admin, db, role, "секрет-1");
            Assert.True(outcome.RoleCreated);
            Assert.False(outcome.RoleReused);
            Assert.True(RoleExists(role));
            Assert.True(DbExists(db));

            // Роль-владелец монтирует базу: схема строится миграциями, первый вход — админ
            // (ровно то, что происходит после окна создания).
            var info = new PgConnectionInfo(PgTestServer.Host, PgTestServer.Port, db, role, "секрет-1", "Prefer");
            using var m = DatabaseManager.Open(9201, "pg-created", info.ToConnectionString(), provider: "postgres");
            Assert.True(m.Context.IsPostgres);
            Assert.True(m.Permissions.IsAdmin);
            var col = SampleData.AddCollection(m.Constructor, "Первая");
            Assert.NotNull(col.Id);
        }
        finally
        {
            Cleanup(db, role);
        }
    }

    [SkippableFact]
    public async Task Existing_database_surfaces_42P04()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var (db, role) = FreshNames();
        try
        {
            Exec($"CREATE DATABASE {db};");   // база занята кем-то раньше

            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => PgDatabaseCreator.CreateAsync(Admin, db, role, "секрет-2"));
            Assert.Equal("42P04", ex.SqlState);
        }
        finally
        {
            Cleanup(db, role);
        }
    }

    [SkippableFact]
    public async Task Existing_role_is_reused_and_password_untouched()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var (db, role) = FreshNames();
        try
        {
            Exec($"CREATE ROLE {role} LOGIN PASSWORD 'старый-пароль';");

            var outcome = await PgDatabaseCreator.CreateAsync(Admin, db, role, "новый-пароль");
            Assert.False(outcome.RoleCreated);
            Assert.True(outcome.RoleReused);
            Assert.True(DbExists(db));

            // Пароль существующей роли не переписан: старый работает, новый — нет
            // (окно поверх этого делает контрольную пробу ролью и объясняет исход).
            var old = new PgConnectionInfo(PgTestServer.Host, PgTestServer.Port, db, role, "старый-пароль", "Prefer");
            using (var conn = new NpgsqlConnection(old.ToConnectionString())) conn.Open();

            var fresh = new PgConnectionInfo(PgTestServer.Host, PgTestServer.Port, db, role, "новый-пароль", "Prefer");
            Assert.ThrowsAny<Exception>(() =>
            {
                using var conn = new NpgsqlConnection(fresh.ToConnectionString());
                conn.Open();
            });
        }
        finally
        {
            Cleanup(db, role);
        }
    }

    [SkippableFact]
    public async Task Failed_database_leaves_no_orphan_role()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var (db, role) = FreshNames();
        try
        {
            Exec($"CREATE DATABASE {db};");   // CREATE DATABASE в CreateAsync упадет (42P04)

            await Assert.ThrowsAsync<PostgresException>(
                () => PgDatabaseCreator.CreateAsync(Admin, db, role, "секрет-3"));

            // Свежесозданная роль подчищена — сироты на сервере не остается.
            Assert.False(RoleExists(role));
        }
        finally
        {
            Cleanup(db, role);
        }
    }
}
