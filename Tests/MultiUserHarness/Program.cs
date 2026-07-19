// Стенд многопользовательской работы с одним SQLite-файлом. Режимы — в шапке .csproj.
// «Сетевой» профиль без настоящей шары включается NetworkPath.ForceNetwork(path).
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

SQLitePCL.Batteries_V2.Init();

var pass = 0; var fail = 0;
void Check(string name, bool ok)
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {name}"); }
    else    { fail++; Console.WriteLine($"  FAIL  {name}"); }
}

int Done()
{
    Console.WriteLine($"Итого: PASS {pass}, FAIL {fail}");
    return fail == 0 ? 0 : 1;
}

string Pragma(string db, string pragma)
{
    using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"PRAGMA {pragma};";
    return Convert.ToString(cmd.ExecuteScalar()) ?? "";
}

switch (args.FirstOrDefault())
{
    // ── Профиль журнала/прагм: локально WAL+NORMAL+mmap, «сеть» DELETE+FULL+mmap=0 ──
    case "--check-pragmas":
    {
        var dir = Directory.CreateTempSubdirectory("mu_pragmas_").FullName;

        var local = Path.Combine(dir, "local.db");
        using (DatabaseManager.Open(1, "local", local)) { }
        Check($"локально journal_mode={Pragma(local, "journal_mode")}", Pragma(local, "journal_mode") == "wal");

        // Файл, созданный локально (WAL в заголовке), «переносим на шару» и переоткрываем:
        // журнал обязан конвертироваться в DELETE.
        var net = Path.Combine(dir, "net.db");
        using (DatabaseManager.Open(2, "net", net)) { }
        Check("до переноса journal_mode=wal", Pragma(net, "journal_mode") == "wal");
        NetworkPath.ForceNetwork(net);
        using (var m = DatabaseManager.Open(2, "net", net))
        {
            Check("IsShared=true на сетевом пути", m.IsShared);
            var ef = m.Context.Database;
            ef.OpenConnection();
            var conn = ef.GetDbConnection();
            string P(string p) { using var c = conn.CreateCommand(); c.CommandText = $"PRAGMA {p};"; return Convert.ToString(c.ExecuteScalar()) ?? ""; }
            Check($"сеть: journal_mode={P("journal_mode")}", P("journal_mode") == "delete");
            Check($"сеть: synchronous={P("synchronous")} (FULL=2)", P("synchronous") == "2");
            Check($"сеть: mmap_size={P("mmap_size")}", P("mmap_size") == "0");
            Check($"сеть: busy_timeout={P("busy_timeout")}", P("busy_timeout") == "15000");
        }
        Check("после переоткрытия журнал в файле delete", Pragma(net, "journal_mode") == "delete");

        try { Directory.Delete(dir, true); } catch { }
        return Done();
    }

    // ── Конкуренция: два процесса бьют в одну ячейку на «сетевом» профиле ─────────
    case "--contend":
    {
        var dir = Directory.CreateTempSubdirectory("mu_contend_").FullName;
        var db  = Path.Combine(dir, "contend.db");

        // Фикстура: коллекция + текстовое поле + запись (сетевой профиль с самого начала).
        NetworkPath.ForceNetwork(db);
        int recId, fieldId;
        using (var m = DatabaseManager.Open(1, "contend", db))
        {
            var col = m.Constructor.AddCollection(new DbCollection { Name = "Стенд", Position = 1000 });
            var fld = m.Constructor.AddField(new DbField
                { CollectionId = col.Id!.Value, Name = "Ячейка", FieldType = "text", Position = 1000 });
            var rec = m.Constructor.AddRecord(new DbRecord { CollectionId = col.Id!.Value, Title = "R1" });
            recId = rec.Id!.Value; fieldId = fld.Id!.Value;
        }

        const int seconds = 15;
        Process Spawn(string tag) => Process.Start(new ProcessStartInfo
        {
            FileName  = Environment.ProcessPath!,
            ArgumentList = { "--contend-child", db, recId.ToString(), fieldId.ToString(), seconds.ToString(), tag },
            RedirectStandardOutput = true,
        })!;
        var a = Spawn("A");
        var b = Spawn("B");
        a.WaitForExit(); b.WaitForExit();
        var opsA = int.TryParse(a.StandardOutput.ReadToEnd().Trim(), out var oa) ? oa : -1;
        var opsB = int.TryParse(b.StandardOutput.ReadToEnd().Trim(), out var ob) ? ob : -1;

        Check($"писатель A без падений ({opsA} операций)", a.ExitCode == 0 && opsA > 0);
        Check($"писатель B без падений ({opsB} операций)", b.ExitCode == 0 && opsB > 0);

        using (var conn = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            conn.Open();
            string One(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; return Convert.ToString(c.ExecuteScalar()) ?? ""; }
            var final = One($"SELECT Value FROM db_field_values WHERE RecordId={recId} AND FieldId={fieldId};");
            Check($"итоговое значение от одного из писателей: «{final}»",
                  System.Text.RegularExpressions.Regex.IsMatch(final, @"^[AB]-\d+$"));
            Check("integrity_check = ok", One("PRAGMA integrity_check;") == "ok");
            var logCount = long.Parse(One("SELECT COUNT(*) FROM change_log;"));
            var logMax   = long.Parse(One("SELECT COALESCE(MAX(Id),0) FROM change_log;"));
            Check($"журнал монотонен и не пуст ({logCount} строк, max Id {logMax})",
                  logCount > 0 && logMax >= logCount);
        }

        try { Directory.Delete(dir, true); } catch { }
        return Done();
    }

    case "--contend-child":
    {
        // Дочерний писатель: жёсткий цикл правок одной ячейки. Печатает число операций.
        var (db, recId, fieldId, seconds, tag) =
            (args[1], int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), args[5]);
        NetworkPath.ForceNetwork(db);
        using var ctx = new AppDbContext(db);
        ctx.ApplyPerformancePragmas();
        var repo = new ConstructorRepository(ctx);
        var sw = Stopwatch.StartNew();
        var i = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
            repo.SetFieldValue(recId, fieldId, $"{tag}-{++i}");
        Console.WriteLine(i);
        return 0;
    }

    // ── «Второй пользователь» для живой проверки синхронизации в приложении ──────
    case "--writer":
    {
        var db = args.ElementAtOrDefault(1) ?? throw new ArgumentException("нужен путь к .db");
        using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
        conn.Open();
        string One(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; return Convert.ToString(c.ExecuteScalar()) ?? ""; }

        // Первое текстовое поле и первая запись его коллекции.
        var fieldId = One("SELECT Id FROM db_fields WHERE FieldType='text' ORDER BY Id LIMIT 1;");
        var colId   = One($"SELECT CollectionId FROM db_fields WHERE Id={fieldId};");
        var colName = One($"SELECT Name FROM db_collections WHERE Id={colId};");
        var recId   = One($"SELECT Id FROM db_records WHERE CollectionId={colId} ORDER BY Id LIMIT 1;");
        if (recId.Length == 0) { Console.WriteLine("нет записей — создайте коллекцию с записью"); return 1; }
        Console.WriteLine($"пишу в коллекцию «{colName}», запись {recId}, поле {fieldId}; Ctrl+C — стоп");

        // Пишем как «удалённый пользователь»: значение + строка журнала с чужим UserKey —
        // ровно то, что увидит поллер другой копии приложения.
        var n = 0;
        while (true)
        {
            n++;
            using (var c = conn.CreateCommand())
            {
                c.CommandText =
                    "INSERT INTO db_field_values (RecordId, FieldId, Value) VALUES ($r, $f, $v) " +
                    "ON CONFLICT(RecordId, FieldId) DO UPDATE SET Value=$v;" +
                    "INSERT INTO change_log (Ts, UserKey, UserName, Action, TargetType, TargetName, CollectionName) " +
                    "VALUES ($ts, 'harness@remote', 'Стенд', 'update', 'record', 'R', $col);";
                c.Parameters.AddWithValue("$r", long.Parse(recId));
                c.Parameters.AddWithValue("$f", long.Parse(fieldId));
                c.Parameters.AddWithValue("$v", $"писатель {n} ({DateTime.Now:HH:mm:ss})");
                c.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
                c.Parameters.AddWithValue("$col", colName);
                c.ExecuteNonQuery();
            }
            Console.WriteLine($"  записал «писатель {n}»");
            Thread.Sleep(2000);
        }
    }

    // ── Держать write-замок N секунд (проверка тоста «база занята») ───────────────
    case "--hold-write-lock":
    {
        var db = args.ElementAtOrDefault(1) ?? throw new ArgumentException("нужен путь к .db");
        var sec = int.Parse(args.ElementAtOrDefault(2) ?? "20");
        using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
        conn.Open();
        using (var c = conn.CreateCommand()) { c.CommandText = "BEGIN IMMEDIATE; UPDATE db_meta SET Value=Value WHERE 0;"; c.ExecuteNonQuery(); }
        Console.WriteLine($"держу write-замок {sec} с…");
        Thread.Sleep(sec * 1000);
        using (var c = conn.CreateCommand()) { c.CommandText = "COMMIT;"; c.ExecuteNonQuery(); }
        Console.WriteLine("отпустил");
        return 0;
    }

    default:
        Console.WriteLine("режимы: --check-pragmas | --contend | --writer <db> | --hold-write-lock <db> [сек]");
        return 1;
}
