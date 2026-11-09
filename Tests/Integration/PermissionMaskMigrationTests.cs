using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartBase.Database;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Бэкфилл масок при появлении новых битов <see cref="SmartBase.Core.Models.PermAction"/>.
/// Схема при этом не меняется — меняются УЖЕ СОХРАНЕННЫЕ маски: нового бита в них нет
/// физически, и без бэкфилла каждый, кому администратор что-то урезал, молча потерял бы
/// операцию, которая до этого ему была разрешена.
///
/// Проверяются обе такие миграции разом, потому что обе правят одни и те же строки:
///
/// <list type="bullet">
///   <item><c>AddDuplicateFieldRight</c> — дубль свойства получил свой бит (4096), а раньше
///   шел под правом создания (512): разрешение ПЕРЕНОСИТСЯ тем, у кого было создание.</item>
///   <item><c>AddIntegrationRights</c> — настройка обмена и исходящие направления (8192 и
///   16384) до этого не спрашивали права вовсе, и получают их ВСЕ: миграция заводит
///   администратору ручку, а не отбирает у людей то, что они уже делали.</item>
/// </list>
///
/// Проверяем на «старой» базе: накатываем миграции до предыдущей, наливаем маски сырым
/// SQL и доводим до последней.
/// </summary>
public sealed class PermissionMaskMigrationTests
{
    /// <summary>Последняя миграция ПЕРЕД появлением права — точка, на которой строим «старую» базу.</summary>
    private const string PreviousMigration   = "20260815145920_AddUserLastSeen";
    private const string PreviousMigrationPg = "20260815145953_AddUserLastSeenPg";

    private const int CreateField    = 512;
    private const int DuplicateField = 4096;
    private const int OldAll         = 4095;   // PermAction.All до появления нового бита

    // Права интеграций: настройка (8192) и исходящие (16384).
    private const int Integrations = 8192 | 16384;

    // Три пользователя: с полной старой маской, с урезанной (создание свойств снято)
    // и с переопределением на коллекцию — бэкфилл обязан разобрать все три случая.
    private static readonly string[] SeedSql =
    [
        "INSERT INTO db_collections (Id, Name, Position, Icon, Color) VALUES (1, 'События', 1000, '', '#007AFF')",
        "INSERT INTO db_users (Id, UserKey, DisplayName, IsAdmin, IsApproved, CreatedAt, LastSeenAt)" +
        " VALUES (1, 'полный@пк', 'Полный', 0, 1, '2026-01-01', '2026-01-01')",
        "INSERT INTO db_users (Id, UserKey, DisplayName, IsAdmin, IsApproved, CreatedAt, LastSeenAt)" +
        " VALUES (2, 'урезанный@пк', 'Урезанный', 0, 1, '2026-01-01', '2026-01-01')",
        // Полная старая маска — человек мог все, включая дубль свойства.
        "INSERT INTO db_permissions (Id, UserId, CollectionId, ActionsMask) VALUES (10, 1, NULL, 4095)",
        // Создание свойств снято — дубль был запрещен и должен остаться запрещенным.
        "INSERT INTO db_permissions (Id, UserId, CollectionId, ActionsMask) VALUES (11, 2, NULL, 3583)",
        // Переопределение на коллекцию — обрабатывается наравне с масками базы.
        "INSERT INTO db_permissions (Id, UserId, CollectionId, ActionsMask) VALUES (12, 2, 1, 4095)",
    ];

    private static void Exec(System.Data.Common.DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int Mask(System.Data.Common.DbConnection conn, string column, int id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM db_permissions WHERE Id = {id}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void AssertBackfilled(System.Data.Common.DbConnection conn, string column)
    {
        // Мог создавать свойства → получил и новое право: поведение до миграции сохранено.
        Assert.Equal(OldAll | DuplicateField | Integrations, Mask(conn, column, 10));
        Assert.Equal(OldAll | DuplicateField | Integrations, Mask(conn, column, 12));

        // Не мог создавать → дубль по-прежнему закрыт, лишних прав миграция не раздает.
        var trimmed = Mask(conn, column, 11);
        Assert.Equal(0, trimmed & DuplicateField);
        Assert.Equal(0, trimmed & CreateField);

        // А права интеграций достаются и ему: до этой миграции настройка обмена не
        // спрашивала ничего, и урезанная маска не означала запрета на нее.
        Assert.Equal(Integrations, trimmed & Integrations);
    }

    [Fact]
    public void Sqlite_grants_the_new_right_to_everyone_who_could_create_fields()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            using (var old = new AppDbContext(path))
            {
                old.GetService<IMigrator>().Migrate(PreviousMigration);
                var conn = old.Database.GetDbConnection();
                conn.Open();
                foreach (var sql in SeedSql) Exec(conn, sql);
            }

            using (var ctx = new AppDbContext(path))
            {
                ctx.Database.Migrate();
                var conn = ctx.Database.GetDbConnection();
                conn.Open();
                AssertBackfilled(conn, "ActionsMask");
            }
        }
        finally { TempDatabase.Cleanup(path); }
    }

    [SkippableFact]
    public void Postgres_grants_the_new_right_to_everyone_who_could_create_fields()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database2);

        using (var reset = new NpgsqlConnection(cs))
        {
            reset.Open();
            Exec(reset, "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;");
        }

        using (var old = new PgAppDbContext(cs))
        {
            old.GetService<IMigrator>().Migrate(PreviousMigrationPg);
            var conn = old.Database.GetDbConnection();
            conn.Open();
            // PG-модель — строчные имена колонок (ApplyLowercaseNames), регистр в SQL не важен.
            foreach (var sql in SeedSql) Exec(conn, sql);
        }

        using (var ctx = new PgAppDbContext(cs))
        {
            ctx.Database.Migrate();
            var conn = ctx.Database.GetDbConnection();
            conn.Open();
            AssertBackfilled(conn, "actionsmask");
        }
    }
}
