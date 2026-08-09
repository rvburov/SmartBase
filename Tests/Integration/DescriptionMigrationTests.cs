using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartBase.Database;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Миграция <c>DescriptionAsField</c>: описания, накопленные в системной колонке
/// <c>db_records.Description</c>, переезжают в обычное поле коллекции с ролью
/// <c>IsDescription</c>, после чего колонка удаляется. Проверяем на «старой» базе:
/// накатываем миграции до предыдущей, наливаем данные сырым SQL и доводим до последней.
/// </summary>
public sealed class DescriptionMigrationTests
{
    /// <summary>Последняя миграция ПЕРЕД переездом описания — точка, на которой строим «старую» базу.</summary>
    private const string PreviousMigration = "20260725130231_AddCrossDbLinks";
    private const string PreviousMigrationPg = "20260725130305_AddCrossDbLinksPg";

    // Старая база: две коллекции — у одной описания есть (переносим), у другой пусто (поля быть
    // не должно). Плюс переименованная системная колонка, её метаданные и правило авто-файла.
    private static readonly string[] SeedSql =
    [
        "INSERT INTO db_collections (Id, Name, Position, Icon, Color) VALUES (1, 'События', 1000, '', '#007AFF')",
        "INSERT INTO db_collections (Id, Name, Position, Icon, Color) VALUES (2, 'Пустая', 2000, '', '#007AFF')",
        "INSERT INTO db_fields (Id, CollectionId, Name, FieldType, ConfigJson, Position, IsCalStart, IsCalEnd, IsSystem, IsRequired, IsTaskExec, IsTaskAssigner, IsTaskStatus)" +
        " VALUES (10, 1, 'Файл', 'file', '{\"auto\":{\"source\":\"__desc__\",\"folder\":\"/d\",\"format\":\"pdf\"}}', 1000, 0, 0, 0, 0, 0, 0, 0)",
        "INSERT INTO db_records (Id, CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, AllDay, Title, Description)" +
        " VALUES (100, 1, '2026-01-01', '2026-01-01', 'я', 'я', 1000, 0, 'Планёрка', 'в переговорной')",
        "INSERT INTO db_records (Id, CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, AllDay, Title, Description)" +
        " VALUES (101, 1, '2026-01-01', '2026-01-01', 'я', 'я', 2000, 0, 'Без описания', NULL)",
        "INSERT INTO db_records (Id, CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, AllDay, Title, Description)" +
        " VALUES (200, 2, '2026-01-01', '2026-01-01', 'я', 'я', 1000, 0, 'Чужая', NULL)",
        // Пользователь переименовал системную колонку и подвинул её — имя и позиция переезжают.
        "INSERT INTO column_meta (CollectionId, Key, Position, Visible, Level, DisplayName)" +
        " VALUES (1, '__desc__', 1500, NULL, NULL, 'Комментарий')",
    ];

    private static void Exec(System.Data.Common.DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Scalar(System.Data.Common.DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    [Fact]
    public void Sqlite_moves_descriptions_into_a_field_and_drops_the_column()
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

                // Поле заведено только там, где описания были, и носит прежнюю подпись колонки.
                Assert.Equal("Комментарий", Scalar(conn,
                    "SELECT Name FROM db_fields WHERE CollectionId = 1 AND IsDescription = 1"));
                Assert.Equal("text", Scalar(conn,
                    "SELECT FieldType FROM db_fields WHERE CollectionId = 1 AND IsDescription = 1"));
                Assert.Equal("0", Scalar(conn,
                    "SELECT COUNT(*) FROM db_fields WHERE CollectionId = 2 AND IsDescription = 1"));

                var fid = Scalar(conn, "SELECT Id FROM db_fields WHERE CollectionId = 1 AND IsDescription = 1");

                // Значение переехало ровно одно — пустые описания строк не плодят.
                Assert.Equal("в переговорной", Scalar(conn,
                    $"SELECT Value FROM db_field_values WHERE RecordId = 100 AND FieldId = {fid}"));
                Assert.Equal("1", Scalar(conn,
                    $"SELECT COUNT(*) FROM db_field_values WHERE FieldId = {fid}"));

                // Метаданные колонки переехали на числовой ключ, старый ключ вычищен.
                Assert.Equal("1500", Scalar(conn,
                    $"SELECT Position FROM column_meta WHERE CollectionId = 1 AND Key = '{fid}'"));
                Assert.Equal("0", Scalar(conn, "SELECT COUNT(*) FROM column_meta WHERE Key = '__desc__'"));

                // Правило авто-привязки указывает на новое поле, а не на исчезнувший ключ.
                Assert.Contains($"\"source\":\"{fid}\"", Scalar(conn,
                    "SELECT ConfigJson FROM db_fields WHERE Id = 10"));

                // Колонки описания в схеме больше нет.
                Assert.Equal("0", Scalar(conn,
                    "SELECT COUNT(*) FROM pragma_table_info('db_records') WHERE name = 'Description'"));
            }
        }
        finally { TempDatabase.Cleanup(path); }
    }

    [SkippableFact]
    public void Postgres_moves_descriptions_into_a_field_and_drops_the_column()
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
            // PG-модель — строчные имена колонок (см. ApplyLowercaseNames), регистр в SQL не важен.
            foreach (var sql in SeedSql) Exec(conn, sql);
        }

        using (var ctx = new PgAppDbContext(cs))
        {
            ctx.Database.Migrate();
            var conn = ctx.Database.GetDbConnection();
            conn.Open();

            Assert.Equal("Комментарий", Scalar(conn,
                "SELECT name FROM db_fields WHERE collectionid = 1 AND isdescription = 1"));
            Assert.Equal("0", Scalar(conn,
                "SELECT COUNT(*) FROM db_fields WHERE collectionid = 2 AND isdescription = 1"));

            var fid = Scalar(conn, "SELECT id FROM db_fields WHERE collectionid = 1 AND isdescription = 1");

            Assert.Equal("в переговорной", Scalar(conn,
                $"SELECT value FROM db_field_values WHERE recordid = 100 AND fieldid = {fid}"));
            Assert.Equal("1", Scalar(conn, $"SELECT COUNT(*) FROM db_field_values WHERE fieldid = {fid}"));

            Assert.Equal("1500", Scalar(conn,
                $"SELECT position FROM column_meta WHERE collectionid = 1 AND key = '{fid}'"));
            Assert.Equal("0", Scalar(conn, "SELECT COUNT(*) FROM column_meta WHERE key = '__desc__'"));

            Assert.Contains($"\"source\":\"{fid}\"", Scalar(conn,
                "SELECT configjson FROM db_fields WHERE id = 10"));

            Assert.Equal("0", Scalar(conn,
                "SELECT COUNT(*) FROM information_schema.columns" +
                " WHERE table_name = 'db_records' AND column_name = 'description'"));
        }
    }
}
