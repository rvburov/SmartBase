using Npgsql;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Доступ к базе PostgreSQL устроен так же, как к файлу SQLite: тумблер «Доступ других
/// пользователей по паролю» и повторный запрос доступа после удаления из базы живут в тех же
/// db_meta и db_users. SkippableFact — без стенда sb-pg (порт 5455) тест помечается Skipped.
/// </summary>
public sealed class PgAccessTests
{
    private static void ResetSchema(string connString)
    {
        using var conn = new NpgsqlConnection(connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
        cmd.ExecuteNonQuery();
    }

    [SkippableFact]
    public void Password_access_and_request_again_work_on_postgres()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database2);
        ResetSchema(cs);

        using var db = DatabaseManager.Open(102, "pg-доступ", cs, provider: "postgres");
        db.Permissions.SetPassword("секрет");
        db.Permissions.SetPasswordAccess(false);
        Assert.False(db.Permissions.PasswordAccess);

        // Администратором становится сосед, меня из базы убирают, пока она открыта.
        using (var other = new AppDbContext(cs))
        {
            other.DbUsers.Add(new DbUserEntity
            {
                UserKey     = "boss@other-pc",
                DisplayName = "Чужой Админ",
                IsAdmin     = true,
                IsApproved  = true,
                CreatedAt   = DateTime.UtcNow.ToString("o"),
            });
            other.DbUsers.Remove(other.DbUsers.Single(u => u.UserKey == CurrentUser.Key));
            other.SaveChanges();
        }
        db.Permissions.Reload(notify: false);
        Assert.False(db.Permissions.AccessGranted);

        // Вход паролем выключен — запрос уходит администратору и ждет его решения.
        Assert.Equal("Вход по паролю выключен администратором базы данных", db.Permissions.Unlock("секрет"));
        db.Permissions.RequestAccess();
        using (var check = new AppDbContext(cs))
            Assert.False(check.DbUsers.Single(u => u.UserKey == CurrentUser.Key).IsApproved);

        // Администратор включил вход паролем — тот же пароль открывает базу.
        using (var other = new AppDbContext(cs))
        {
            other.DbMeta.Single(m => m.Key == "access.password.enabled").Value = "1";
            other.SaveChanges();
        }
        Assert.Equal("", db.Permissions.Unlock("секрет"));
        Assert.True(db.Permissions.AccessGranted);
    }
}
