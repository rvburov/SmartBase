using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Профиль прагм локальной SQLite-БД (перенос Tests/MultiUserHarness --check-pragmas):
/// журнал WAL (персистится в файле) и настроенные на подключение busy_timeout / synchronous /
/// mmap_size. Соединение держим открытым — иначе прагмы уровня подключения сбросятся.
/// </summary>
public sealed class PragmaTests
{
    [Fact]
    public void Local_sqlite_uses_wal_and_tuned_connection_pragmas()
    {
        using var db = new TempDatabase();               // локальный temp-путь → WAL, локальный профиль
        using var ctx = new AppDbContext(db.Path);
        var conn = ctx.Database.GetDbConnection();
        conn.Open();                                     // держим открытым
        ctx.ApplyPerformancePragmas();

        Assert.Equal("wal", Scalar(conn, "PRAGMA journal_mode;"));
        Assert.Equal("10000", Scalar(conn, "PRAGMA busy_timeout;"));
        Assert.Equal("1", Scalar(conn, "PRAGMA synchronous;"));   // NORMAL
        Assert.True(long.Parse(Scalar(conn, "PRAGMA mmap_size;")) > 0);
        // Предел -wal после чекпоинта: без него журнал локальной базы после свода/импорта
        // оставался сотнями мегабайт (см. AppDbContext.WalSizeLimitBytes).
        Assert.Equal(AppDbContext.WalSizeLimitBytes.ToString(), Scalar(conn, "PRAGMA journal_size_limit;"));
    }

    [Fact]
    public void Shared_sqlite_has_no_wal_size_limit_and_full_sync()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            NetworkPath.ForceNetwork(path);
            using var m   = DatabaseManager.Open(2, "shared", path);
            using var ctx = new AppDbContext(path);
            var conn = ctx.Database.GetDbConnection();
            conn.Open();
            ctx.ApplyPerformancePragmas();

            Assert.Equal("delete", Scalar(conn, "PRAGMA journal_mode;"));
            Assert.Equal("15000",  Scalar(conn, "PRAGMA busy_timeout;"));
            Assert.Equal("2",      Scalar(conn, "PRAGMA synchronous;"));   // FULL
            Assert.Equal("-1",     Scalar(conn, "PRAGMA journal_size_limit;"));   // журнал DELETE — лимит не ставим
        }
        finally
        {
            TempDatabase.Cleanup(path);
        }
    }

    private static string Scalar(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }
}
