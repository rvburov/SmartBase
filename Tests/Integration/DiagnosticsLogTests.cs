using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Logging;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Журнал диагностики отражает работу ядра: монтирование базы — одной записью с режимом
/// журнала и путем, сжатие — «было → стало», допуск — кто стал администратором; отказы
/// чтения при недоступной базе идут одной записью на серию, повреждение файла зовется
/// своим именем, а на здоровой локальной базе предупреждений о прагмах нет. Логгер
/// глобальный (Serilog.Log) — тесты подменяют его на сборщик и восстанавливают.
/// </summary>
public sealed class DiagnosticsLogTests : IDisposable
{
    private readonly List<LogEvent> _events = [];

    public DiagnosticsLogTests()
    {
        Diag.ResetForTests();
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(new ListSink(_events)).CreateLogger();
    }

    public void Dispose()
    {
        Log.Logger = Logger.None;
        Diag.ResetForTests();
    }

    private IEnumerable<string> Messages(LogEventLevel? level = null)
    {
        lock (_events)
            return _events.Where(e => level is null || e.Level == level).Select(e => e.RenderMessage()).ToList();
    }

    [Fact]
    public void Mount_writes_one_record_with_journal_mode_and_path()
    {
        using var db = new TempDatabase(name: "диагностика");

        var open = Assert.Single(Messages(LogEventLevel.Information), m => m.StartsWith("База «диагностика» открыта за"));
        Assert.Contains("sqlite", open);
        Assert.Contains("локальный путь " + db.Path, open);
        Assert.Contains("журнал wal", open);
        Assert.Contains("файл ", open);
        Assert.Equal("wal", db.Manager.JournalMode);
        Assert.True(db.Manager.MountDuration > TimeSpan.Zero);

        // Здоровая локальная база: прагмы применились, предупреждений о них нет.
        Assert.DoesNotContain(Messages(LogEventLevel.Warning), m => m.Contains("не применился"));
    }

    [Fact]
    public void First_user_of_new_database_is_logged_as_administrator()
    {
        using var db = new TempDatabase(name: "новая");
        Assert.Contains(Messages(LogEventLevel.Information), m => m.Contains("открыл базу") && m.Contains("стал ее администратором"));
    }

    [Fact]
    public void Compaction_at_mount_logs_before_and_after_sizes()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            using (DatabaseManager.Open(7, "раздутая", path)) { }
            AppDbContext.ResetPool(path);

            // 80 МБ мусора, тут же удаленного: свободных страниц больше 64 МБ и почти весь файл —
            // порог автосжатия локальной базы при монтировании.
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE junk(b); " +
                    "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 80000) " +
                    "INSERT INTO junk SELECT randomblob(1000) FROM n; " +
                    "DROP TABLE junk; PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            var bloated = new FileInfo(path).Length;

            using (DatabaseManager.Open(7, "раздутая", path)) { }

            var compact = Assert.Single(Messages(LogEventLevel.Information), m => m.StartsWith("База «раздутая» сжата:"));
            Assert.Contains("→", compact);
            Assert.True(new FileInfo(path).Length < bloated / 2, "файл на диске должен уменьшиться");
        }
        finally
        {
            TempDatabase.Cleanup(path);
        }
    }

    [Fact]
    public void Read_failures_are_logged_once_per_series()
    {
        var path = @"C:\нет\такой\базы.db";
        var ex   = new IOException("сеть отвалилась");
        DbErrors.HandleRead(ex, path);
        DbErrors.HandleRead(ex, path);
        DbErrors.HandleRead(ex, path);

        var one = Assert.Single(Messages(LogEventLevel.Warning), m => m.Contains("недоступна при чтении"));
        Assert.Contains(path, one);
    }

    [Fact]
    public void Corrupted_file_is_named_as_such()
    {
        var corrupt = new SqliteException("database disk image is malformed", 11);
        Assert.True(DbHealthMonitor.IsCorruptionError(corrupt));
        Assert.True(DbHealthMonitor.IsUnavailableError(corrupt));

        // Путь записи (DbErrors.Handle) сюда не берем: он шлет карточку через Dispatcher, а
        // в тестах без платформы Avalonia его нет; путь чтения — та же классификация.
        DbErrors.HandleRead(corrupt, @"C:\битая.db");
        var e = Assert.Single(Messages(LogEventLevel.Error), m => m.Contains("Файл базы поврежден (чтение)"));
        Assert.Contains(@"C:\битая.db", e);
    }

    [Fact]
    public void PostgreSQL_connection_string_never_reaches_the_log()
    {
        var conn = "Host=srv;Port=5455;Database=sb;Username=u;Password=secret";
        Assert.True(PgConnectionInfo.IsPostgresConnectionString(conn));
        DbErrors.HandleRead(new IOException("x"), conn);
        Assert.Equal("PostgreSQL", DbErrors.Display(conn));
        Assert.All(Messages(), m => Assert.DoesNotContain("secret", m));
        Assert.Contains(Messages(), m => m.Contains("PostgreSQL"));
    }

    /// <summary>
    /// Починка разметки ролей при подключении базы его не срывает, но и не молчит: отказ по
    /// правам остается сведением — с разделом, коллекцией и тем, какого права не хватило. Раньше
    /// он глотался, и коллекция с поломанной разметкой жила дальше без единого следа. То, на что
    /// прав хватило (снять чужую роль), при этом сделано.
    /// </summary>
    [Fact]
    public void A_role_repair_refused_by_rights_leaves_a_trace()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Архив чертежей").Id!.Value;
        var file = SampleData.AddField(repo, col, "Скан", "file").Id!.Value;

        // Состояние старой базы: на источнике документов висит еще и роль «Тип документа».
        var entity = db.Context.DbFields.Single(f => f.Id == file);
        entity.IsDocFile = true;
        entity.IsDocType = true;
        db.Context.SaveChanges();

        // Базу подключает пользователь без права заводить свойства: чужую роль снять он может,
        // а завести «Тип документа» заново — нет.
        SetRights(db, PermAction.All & ~PermAction.CreateField);

        Assert.Equal(0, repo.RepairDocFieldRoles());

        var trace = Assert.Single(Messages(LogEventLevel.Information),
                                  m => m.Contains("Разметка ролей раздела «Библиотека документов»"));
        Assert.Contains("«Архив чертежей»", trace);
        Assert.Contains("Недостаточно прав", trace);
        Assert.False(repo.GetFields(col).Single(f => f.Id == file).IsDocType);
    }

    /// <summary>То же у близнеца — починки ролей задач: исполнитель с чужой ролью «Назначил».</summary>
    [Fact]
    public void A_task_role_repair_refused_by_rights_leaves_a_trace()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Поручения").Id!.Value;
        var exec = SampleData.AddField(repo, col, "Исполнитель", "user").Id!.Value;

        var entity = db.Context.DbFields.Single(f => f.Id == exec);
        entity.IsTaskExec     = true;
        entity.IsTaskAssigner = true;
        db.Context.SaveChanges();

        SetRights(db, PermAction.All & ~PermAction.CreateField);

        Assert.Equal(0, repo.RepairTaskFieldRoles());

        var trace = Assert.Single(Messages(LogEventLevel.Information),
                                  m => m.Contains("Разметка ролей раздела «Задачи»"));
        Assert.Contains("«Поручения»", trace);
        Assert.Contains("Недостаточно прав", trace);
    }

    // Понижает текущего пользователя до допущенного не-админа с заданной маской на всю базу.
    private static void SetRights(TempDatabase db, PermAction mask)
    {
        var me = db.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key);
        me.IsAdmin = true;    // маски пишет только администратор
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);
        db.Permissions.SetMask(db.Permissions.CurrentUserId, null, (int)mask);

        me.IsAdmin    = false;   // теперь маска действует: администратору можно все
        me.IsApproved = true;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);
    }

    private sealed class ListSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) { lock (events) events.Add(logEvent); }
    }
}
