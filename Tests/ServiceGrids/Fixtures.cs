using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Tests.ServiceGrids;

/// <summary>
/// Данные стенда — воспроизводимые: те же строки, те же метки времени при каждом прогоне, иначе
/// кадры не сравнить. Журнал диагностики пишется сырым SQLite в схеме приемника Serilog; журнал
/// изменений — настоящими операциями репозитория (метки времени потом выставляются по порядку);
/// пользователи — строками db_users со всеми состояниями допуска и ролей; журнал безопасности —
/// настоящим <see cref="SecurityJournal"/> с заданными моментами.
/// </summary>
sealed class Fixtures : IDisposable
{
    /// <summary>Три страницы по 200: подгрузка при прокрутке проверяется на второй и третьей.</summary>
    public const int LogRows        = 450;
    public const int HistoryRecords = 230;
    public const int SecurityRows   = 250;

    public string           LogsPath     { get; }
    public DatabaseRegistry Registry     { get; }
    public DatabaseManager  Db           { get; }
    public Workspace        Workspace    { get; }
    public SecurityJournal  Journal      { get; }
    public int              CollectionId { get; }
    public long             HistoryRows  { get; }

    private Fixtures(string logsPath, DatabaseRegistry registry, DatabaseManager db, Workspace ws,
                     SecurityJournal journal, int collectionId, long historyRows)
    {
        LogsPath     = logsPath;
        Registry     = registry;
        Db           = db;
        Workspace    = ws;
        Journal      = journal;
        CollectionId = collectionId;
        HistoryRows  = historyRows;
    }

    public static readonly string[] Users =
    [
        "Иванова Анна Петровна", "Сидоров Петр", "Кузнецова Ольга", "Зайцев Иван", "Обмен учетной системы", "Орлова Мария",
    ];

    public static string LogMessage(int i) => (i % 5) switch
    {
        0 => $"Пользователь открыл базу «Договоры» ({i})",
        1 => $"Импорт файла договоров завершен: {120 + i} строк",
        2 => $"Проверка обновлений: новых версий нет (попытка {i})",
        3 => $"Сторож медленности: раскладка страницы записи заняла {300 + i * 3} мс",
        _ => $"Резервная копия базы сохранена в папку архива ({i})",
    };

    public static string LogLevel(int i) => i % 7 == 0 ? "Error" : i % 5 == 3 ? "Warning" : "Information";

    public static Fixtures Build(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { "logs.db", "app.db", "stand.db", "security.db" })
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                var f = Path.Combine(dir, name + suffix);
                if (File.Exists(f)) File.Delete(f);
            }

        var logsPath = Path.Combine(dir, "logs.db");
        CreateLogsDb(logsPath);

        // Реестр и база стенда — как в интеграционных тестах: реестр служит и хранилищем настроек
        // процесса (масштаб, ФИО), чтобы стенд не читал настройки с машины.
        var appDb   = Path.Combine(dir, "app.db");
        var defPath = Path.Combine(dir, "stand.db");
        var registry = DatabaseRegistry.Open(defPath, appDb);
        AppSettings.AttachStore(registry);
        CurrentUser.AttachStore(registry);
        var entry = registry.GetAll().First(d => d.IsDefault);
        var db    = DatabaseManager.Open(entry.Id, "Стенд служебных таблиц", defPath);
        var ws    = Workspace.Attach(registry, db);

        var cid = FillHistory(db);
        FillUsers(db);
        var historyRows = FixTimestamps(defPath);

        var journal = new SecurityJournal(Path.Combine(dir, "security.db"));
        SecurityJournal.Attach(journal);
        FillSecurity(journal, db.DbId);

        return new Fixtures(logsPath, registry, db, ws, journal, cid, historyRows);
    }

    // Схема — как у Serilog.Sinks.SQLite (Logging/LogReader читает id, Timestamp, Level,
    // RenderedMessage, Exception); метка времени — UTC без суффикса, как пишет приемник.
    private static void CreateLogsDb(string path)
    {
        using var con = new SqliteConnection($"Data Source={path}");
        con.Open();
        using (var create = con.CreateCommand())
        {
            create.CommandText = "CREATE TABLE logs (id INTEGER PRIMARY KEY AUTOINCREMENT, Timestamp TEXT, Level VARCHAR(10), " +
                                 "Exception TEXT, RenderedMessage TEXT, Properties TEXT)";
            create.ExecuteNonQuery();
        }
        using var tx  = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO logs (Timestamp, Level, Exception, RenderedMessage, Properties) VALUES ($ts, $lv, $ex, $msg, '{}')";
        var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Text);
        var pLv  = cmd.Parameters.Add("$lv",  SqliteType.Text);
        var pEx  = cmd.Parameters.Add("$ex",  SqliteType.Text);
        var pMsg = cmd.Parameters.Add("$msg", SqliteType.Text);
        var t0 = new DateTime(2026, 9, 18, 5, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < LogRows; i++)
        {
            pTs.Value  = t0.AddMinutes(i * 37 + (i % 3) * 7).ToString("yyyy-MM-dd HH:mm:ss.fff");
            pLv.Value  = LogLevel(i);
            pEx.Value  = i % 7 == 0
                ? $"System.IO.IOException: Файл занят другим процессом (строка {i})\n   at SmartBase.Database.DatabaseManager.Open(Int32 dbId, String name, String path)\n   at SmartBase.App.Start()"
                : DBNull.Value;
            pMsg.Value = LogMessage(i);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // Коллекция с тремя полями и записями; правки дают строки «до → после», дубль и удаление —
    // свои действия, переименование поля — строку про свойство.
    private static int FillHistory(DatabaseManager db)
    {
        var repo = db.Constructor;
        var col  = repo.AddCollection(new DbCollection { Name = "Договоры" });
        var cid  = col.Id!.Value;
        var fNum = repo.AddField(new DbField { CollectionId = cid, Name = "Номер",   FieldType = "text",   Position = 1 });
        var fSum = repo.AddField(new DbField { CollectionId = cid, Name = "Сумма",   FieldType = "number", Position = 2 });
        var fTel = repo.AddField(new DbField { CollectionId = cid, Name = "Телефон", FieldType = "text",   Position = 3 });

        var ids = new List<int>();
        for (var i = 1; i <= HistoryRecords; i++)
            ids.Add(repo.AddRecord(new DbRecord { CollectionId = cid, Title = $"Договор {i}" }).Id!.Value);

        repo.SetFieldValue(ids[0], fNum.Id!.Value, "Д-101");
        repo.SetFieldValue(ids[0], fSum.Id!.Value, "125000");
        repo.SetFieldValue(ids[1], fTel.Id!.Value, "123");
        repo.SetFieldValue(ids[1], fTel.Id!.Value, "456");
        repo.SetRecordTitle(ids[2], "Договор 3 (продлен)");
        repo.DuplicateRecord(ids[3]);
        repo.DeleteRecord(ids[^1]);
        fTel.Name = "Телефон офиса";
        repo.UpdateField(fTel);
        return cid;
    }

    // Все состояния таблицы «Пользователи»: второй администратор, обычный пользователь с частью прав,
    // ожидающий допуска, с отозванным допуском, робот и еще один пользователь.
    private static void FillUsers(DatabaseManager db)
    {
        var ctx = db.Context;
        DbUserEntity U(string key, string name, bool admin, bool approved, bool decided, int day, int seenDay) => new()
        {
            UserKey = key, DisplayName = name, IsAdmin = admin, IsApproved = approved, AccessDecided = decided,
            CreatedAt  = new DateTime(2026, 9, day,     7, 12, 0, DateTimeKind.Utc).ToString("o"),
            LastSeenAt = new DateTime(2026, 9, seenDay, 15, 40, 0, DateTimeKind.Utc).ToString("o"),
        };
        ctx.DbUsers.AddRange(
            U("anna.ivanova@pc-01",    Users[0], admin: true,  approved: true,  decided: true,  day: 1,  seenDay: 24),
            U("petr.sidorov@pc-02",    Users[1], admin: false, approved: true,  decided: true,  day: 3,  seenDay: 23),
            U("olga.kuznetsova@pc-03", Users[2], admin: false, approved: false, decided: false, day: 22, seenDay: 22),
            U("ivan.zaytsev@pc-04",    Users[3], admin: false, approved: false, decided: true,  day: 5,  seenDay: 19),
            U("robot.exchange",        Users[4], admin: false, approved: true,  decided: true,  day: 10, seenDay: 24),
            U("maria.orlova@pc-06",    Users[5], admin: false, approved: true,  decided: true,  day: 12, seenDay: 21));
        ctx.SaveChanges();

        var petr  = ctx.DbUsers.First(u => u.UserKey == "petr.sidorov@pc-02");
        var maria = ctx.DbUsers.First(u => u.UserKey == "maria.orlova@pc-06");
        db.Permissions.SetMask(petr.Id,  null, (int)(PermAction.ViewCollection | PermAction.CreateRecord | PermAction.EditRecord));
        db.Permissions.SetMask(maria.Id, null, (int)(PermAction.ViewCollection | PermAction.EditCollection | PermAction.CreateField | PermAction.DeleteRecord));
    }

    /// <summary>Маска прав пользователя — для проверки клика по галочке.</summary>
    public int MaskOf(string displayName)
    {
        var user = Db.Context.DbUsers.AsNoTracking().First(u => u.DisplayName == displayName);
        return Db.Permissions.GetMask(user.Id, null);
    }

    // Метки времени журнала и текущего пользователя — по порядку и фиксированные: настоящие
    // операции проставили «сейчас», а кадр обязан совпадать с эталоном день за днем.
    private static long FixTimestamps(string defPath)
    {
        using var con = new SqliteConnection($"Data Source={defPath}");
        con.Open();
        var ids = new List<long>();
        using (var read = con.CreateCommand())
        {
            read.CommandText = "SELECT Id FROM change_log ORDER BY Id";
            using var r = read.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        var t0 = new DateTime(2026, 9, 20, 6, 0, 0, DateTimeKind.Utc);
        using var tx = con.BeginTransaction();
        using (var upd = con.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = "UPDATE change_log SET Ts = $ts WHERE Id = $id";
            var pTs = upd.Parameters.Add("$ts", SqliteType.Text);
            var pId = upd.Parameters.Add("$id", SqliteType.Integer);
            for (var i = 0; i < ids.Count; i++)
            {
                pTs.Value = t0.AddMinutes(i * 13).ToString("o");
                pId.Value = ids[i];
                upd.ExecuteNonQuery();
            }
        }
        using (var me = con.CreateCommand())
        {
            me.Transaction = tx;
            me.CommandText = "UPDATE db_users SET CreatedAt = $c, LastSeenAt = $s WHERE UserKey = $k";
            me.Parameters.AddWithValue("$c", new DateTime(2026, 8, 30, 9, 5, 0, DateTimeKind.Utc).ToString("o"));
            me.Parameters.AddWithValue("$s", new DateTime(2026, 9, 25, 8, 15, 0, DateTimeKind.Utc).ToString("o"));
            me.Parameters.AddWithValue("$k", CurrentUser.Key);
            me.ExecuteNonQuery();
        }
        tx.Commit();
        return ids.Count;
    }

    private static void FillSecurity(SecurityJournal journal, int dbId)
    {
        var t0 = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < SecurityRows; i++)
        {
            var kind    = (i % 9) switch { 3 => SecurityEvent.Confirm, 6 => SecurityEvent.Revoke, _ => SecurityEvent.Send };
            var send    = kind == SecurityEvent.Send;
            var allowed = i % 5 != 2;
            var noDb    = i % 6 == 5;
            journal.Write(SecurityEntry.New(
                kind, allowed, Users[i % 3],
                scenario:    i % 4 == 0 ? "" : $"Сценарий {i % 4}",
                address:     send ? $"https://api.example.local/v1/orders/{i}" : "",
                fields:      send ? "Номер, Сумма, Телефон" : "",
                rows:        send ? 10 + i : 0,
                bytes:       send ? 2048 + i * 17 : 0,
                bodyHash:    send ? $"{i:x8}{i * 7919:x8}{i * 104729:x8}" : "",
                explanation: allowed ? "" : "Направление не подтверждено мастер-паролем",
                dbId:        noDb ? 0 : dbId,
                at:          t0.AddMinutes(i * 11),
                confirmed:   kind == SecurityEvent.Confirm ? Users[0] : "",
                collection:  noDb ? "" : "Договоры"));
        }
    }

    public void Dispose()
    {
        SecurityJournal.Attach(null);
        Workspace.Dispose();
        Registry.Dispose();
    }
}
