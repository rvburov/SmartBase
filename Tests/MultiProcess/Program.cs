// Стенд многопользовательской работы с одним SQLite-файлом. Режимы — в шапке .csproj.
// «Сетевой» профиль без настоящей шары включается NetworkPath.ForceNetwork(path).
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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
    // Аварийный стенд бэкапа: родитель может завершить настоящий процесс до публикации
    // копии или удалить его .part. Барьер перед проверкой не меняет сам алгоритм записи.
    case "--backup-worker":
    {
        var (source, app, ready, release) = (args[1], args[2], args[3], args[4]);
        using var registry = DatabaseRegistry.Open(source, app);
        SmartBase.Utils.AppSettings.AttachStore(registry);
        using var service = new SmartBase.Database.Backup.BackupService(registry);
        var ok = false;
        service.BackupFinished += (_, success, message) =>
        {
            ok = success;
            Console.WriteLine(success ? "SUCCESS" : "ERROR: " + message);
        };
        service.BackupProgress += (_, message, fraction) =>
        {
            if (fraction is not null || !message.StartsWith("Проверяю копию", StringComparison.Ordinal)) return;
            File.WriteAllText(ready, "verification");
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(release))
            {
                if (timeout.Elapsed > TimeSpan.FromSeconds(90))
                    throw new TimeoutException("Родитель не освободил барьер резервной копии");
                Thread.Sleep(5);
            }
        };
        service.RunNowAsync(registry.GetAll().Single(d => d.IsDefault).Id).GetAwaiter().GetResult();
        return ok ? 0 : 1;
    }

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
        // Дочерний писатель: жесткий цикл правок одной ячейки. Печатает число операций.
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

        // Пишем как «удаленный пользователь»: значение + строка журнала с чужим UserKey —
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

    // ── Нагрузка: N писателей и M читателей одновременно бьют в один файл ─────────
    //   --load <новый.db> [--writers 10] [--readers 10] [--seconds 60] [--rate 1] [--read-rate 2]
    //          [--records 2000] [--journal delete|persist|truncate]
    // Каждый участник — отдельный процесс со своим подключением (как копия приложения на
    // другой машине). Писатель правит случайную ячейку через SetFieldValue с заданной
    // частотой; читатель делает то, что делают таблица, сайдбар и поллер: PRAGMA data_version
    // на постоянном соединении, страница записей + значения, раз в пять итераций — дерево
    // (группы + коллекции). Локальный путь помечается «сетевым» (профиль DELETE + FULL),
    // настоящая шара — как есть. Существующий файл НЕ трогаем: стенд не должен переписать
    // рабочую базу, поэтому путь обязан быть новым.
    case "--load":
    {
        var o = LoadOptions.Parse(args);
        if (o.Db is null) { Console.WriteLine("нужен путь к НОВОМУ .db"); return 1; }
        if (File.Exists(o.Db))
        {
            Console.WriteLine($"файл уже есть: {o.Db} — укажите путь к новому файлу (рабочую базу стенд не трогает)");
            return 1;
        }
        var emulated = !NetworkPath.IsNetwork(o.Db);
        if (emulated) NetworkPath.ForceNetwork(o.Db);
        Console.WriteLine(emulated
            ? "профиль: локальный файл, сетевой профиль (журнал DELETE, synchronous=FULL) эмулируется"
            : "профиль: настоящий сетевой путь");

        // Фикстура: коллекция, пять текстовых полей, записи — одной транзакцией (на шаре
        // каждая отдельная запись стоила бы десятки миллисекунд).
        int colId;
        var fieldIds = new List<int>();
        var sw = Stopwatch.StartNew();
        using (var m = DatabaseManager.Open(1, "load", o.Db))
        {
            var col = m.Constructor.AddCollection(new DbCollection { Name = "Стенд", Position = 1000 });
            colId = col.Id!.Value;
            for (var i = 1; i <= 5; i++)
                fieldIds.Add(m.Constructor.AddField(new DbField
                    { CollectionId = colId, Name = $"Поле {i}", FieldType = "text", Position = i * 1000 }).Id!.Value);
            using (var tx = m.Context.Database.BeginTransaction())
            {
                for (var i = 1; i <= o.Records; i++)
                    m.Constructor.AddRecord(new DbRecord { CollectionId = colId, Title = $"Запись {i}", Position = i * 1000 });
                tx.Commit();
            }
            m.Context.Database.ExecuteSqlRaw("INSERT INTO db_meta (\"Key\", \"Value\") VALUES ('load.stand', '1')");
        }
        SqliteGate.ClearAllPools();
        Console.WriteLine($"фикстура: {o.Records} записей × {fieldIds.Count} полей за {sw.Elapsed.TotalSeconds:F1} с");

        // Режим журнала — уже после DatabaseManager (он ставит DELETE на сетевом профиле);
        // дети открывают файл через AppDbContext и режим не трогают.
        if (o.Journal != "delete")
        {
            using var conn = new SqliteConnection($"Data Source={o.Db};Pooling=False");
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText = $"PRAGMA journal_mode={o.Journal};";
            c.ExecuteScalar();
        }
        Console.WriteLine($"journal_mode={Pragma(o.Db, "journal_mode")}");

        Process Spawn(string role, int idx) => Process.Start(new ProcessStartInfo
        {
            FileName     = Environment.ProcessPath!,
            ArgumentList =
            {
                "--load-child", role, o.Db, colId.ToString(), o.Seconds.ToString(),
                (role == "writer" ? o.Rate : o.ReadRate).ToString(CultureInfo.InvariantCulture),
                $"{role[0]}{idx}", emulated ? "emulated" : "real",
            },
            RedirectStandardOutput = true,
        })!;

        var children = new List<(string Role, Process P)>();
        for (var i = 1; i <= o.Writers; i++) children.Add(("writer", Spawn("writer", i)));
        for (var i = 1; i <= o.Readers; i++) children.Add(("reader", Spawn("reader", i)));
        Console.WriteLine($"запущено: {o.Writers} писателей ({Rate(o.Rate)}), {o.Readers} читателей ({Rate(o.ReadRate)}), {o.Seconds} с…");

        var reports = new List<LoadReport>();
        var crashed = 0;
        foreach (var (role, p) in children)
        {
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
            if (p.ExitCode != 0 || line is null)
            {
                crashed++;
                Console.WriteLine($"  процесс {role} упал (код {p.ExitCode}): {text.Trim()}");
                continue;
            }
            reports.Add(JsonSerializer.Deserialize<LoadReport>(line)!);
        }

        // Сводка: одна строка на вид операции, задержки по всем процессам этой роли вместе.
        Console.WriteLine();
        Console.WriteLine($"{"Операция",-24}{"Проц.",6}{"Всего",8}{"BUSY",6}{"Ошибок",8}{"p50",7}{"p95",7}{"p99",7}{"max",7}  (мс)");
        void Row(string title, string role, Func<LoadReport, int[]> pick, bool countErrors)
        {
            var mine = reports.Where(r => r.Role == role).ToList();
            var all  = mine.SelectMany(pick).OrderBy(x => x).ToArray();
            var busy = countErrors ? mine.Sum(r => r.Busy)   : 0;
            var err  = countErrors ? mine.Sum(r => r.Errors) : 0;
            Console.WriteLine($"{title,-24}{mine.Count,6}{all.Length,8}{busy,6}{err,8}" +
                              $"{Pct(all, 0.50),7}{Pct(all, 0.95),7}{Pct(all, 0.99),7}{(all.Length > 0 ? all[^1] : 0),7}");
        }
        Row("запись ячейки",       "writer", r => r.Lat,     countErrors: true);
        Row("страница таблицы",    "reader", r => r.Lat,     countErrors: true);
        Row("опрос data_version",  "reader", r => r.PollLat, countErrors: false);
        Row("дерево сайдбара",     "reader", r => r.TreeLat, countErrors: false);
        foreach (var r in reports.Where(r => r.FirstError is not null))
            Console.WriteLine($"  первая ошибка {r.Role}: {r.FirstError}");
        Console.WriteLine();

        Check("все процессы завершились без падений", crashed == 0);
        Check("у писателей нет ошибок, кроме BUSY", reports.Where(r => r.Role == "writer").Sum(r => r.Errors) == 0);
        Check("у читателей нет ошибок, кроме BUSY", reports.Where(r => r.Role == "reader").Sum(r => r.Errors) == 0);
        var writes = reports.Where(r => r.Role == "writer").Sum(r => r.Ops);
        Check($"правки доехали ({writes} операций записи)", writes > 0);

        using (var conn = new SqliteConnection($"Data Source={o.Db};Pooling=False"))
        {
            conn.Open();
            string One(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; return Convert.ToString(c.ExecuteScalar()) ?? ""; }
            Check("integrity_check = ok", One("PRAGMA integrity_check;") == "ok");
            // Каждое значение — от одного из писателей целиком (без обрывков): «w3-118».
            var bad = long.Parse(One("SELECT COUNT(*) FROM db_field_values WHERE Value NOT GLOB 'w[0-9]*-[0-9]*';"));
            var cnt = long.Parse(One("SELECT COUNT(*) FROM db_field_values;"));
            Check($"значения ячеек целые ({cnt} ячеек, чужих/битых {bad})", bad == 0 && (writes == 0 || cnt > 0));
            var logCount = long.Parse(One("SELECT COUNT(*) FROM change_log WHERE Action='update';"));
            Check($"журнал изменений пополнялся ({logCount} строк update)", writes == 0 || logCount > 0);
        }
        Console.WriteLine($"файл стенда оставлен: {o.Db}");
        return Done();

        static string Rate(double r) => r > 0 ? $"{r:0.##}/с каждый" : "без пауз";
        static int Pct(int[] sorted, double q) =>
            sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    case "--load-child":
    {
        var (role, db, colId, seconds, rate, tag, profile) =
            (args[1], args[2], int.Parse(args[3]), int.Parse(args[4]),
             double.Parse(args[5], CultureInfo.InvariantCulture), args[6], args[7]);
        if (profile == "emulated") NetworkPath.ForceNetwork(db);

        using var ctx = new AppDbContext(db);
        ctx.ApplyPerformancePragmas();
        var repo = new ConstructorRepository(ctx);   // Permissions == null → Demand no-op

        var fieldIds = repo.GetFields(colId).Where(f => f.Id.HasValue).Select(f => f.Id!.Value).ToList();
        var cursors  = repo.GetRecordsPageAfter(colId, null, null, 1_000_000)
                           .Select(r => (r.Position, Id: r.Id!.Value)).ToList();
        if (fieldIds.Count == 0 || cursors.Count == 0) { Console.WriteLine("фикстура пуста"); return 2; }

        // Постоянное read-only соединение читателя — как у ChangePoller (busy_timeout там же).
        SqliteConnection? watch = null;
        if (role == "reader")
        {
            watch = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
            watch.Open();
            using var p = watch.CreateCommand();
            p.CommandText = "PRAGMA busy_timeout=2000;";
            p.ExecuteNonQuery();
        }

        var rnd  = new Random(tag.GetHashCode(StringComparison.Ordinal));
        var lat  = new List<int>(); var poll = new List<int>(); var tree = new List<int>();
        int busy = 0, errors = 0, n = 0;
        string? firstError = null;
        var period = rate > 0 ? TimeSpan.FromSeconds(1 / rate) : TimeSpan.Zero;
        var clock  = Stopwatch.StartNew();
        var iter   = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            var t0 = Stopwatch.GetTimestamp();
            try
            {
                if (role == "writer")
                {
                    repo.SetFieldValue(cursors[rnd.Next(cursors.Count)].Id, fieldIds[rnd.Next(fieldIds.Count)], $"{tag}-{++n}");
                    lat.Add(Ms(t0));
                }
                else
                {
                    using (var c = watch!.CreateCommand()) { c.CommandText = "PRAGMA data_version;"; c.ExecuteScalar(); }
                    poll.Add(Ms(t0));

                    var t1   = Stopwatch.GetTimestamp();
                    var cur  = cursors[rnd.Next(cursors.Count)];
                    var page = repo.GetRecordsPageAfter(colId, cur.Position, cur.Id, 200);
                    repo.GetFieldValuesBatch(page.Select(r => r.Id!.Value).ToList());
                    lat.Add(Ms(t1));

                    if (++iter % 5 == 0)
                    {
                        var t2 = Stopwatch.GetTimestamp();
                        repo.GetAllGroups();
                        repo.GetAllCollections();
                        tree.Add(Ms(t2));
                    }
                }
            }
            catch (Exception ex) when (DbHealthMonitor.IsBusyError(ex)) { busy++; }
            catch (Exception ex) { errors++; firstError ??= $"{ex.GetType().Name}: {ex.Message}"; }

            var rest = period - Stopwatch.GetElapsedTime(t0);
            if (rest > TimeSpan.Zero) Thread.Sleep(rest);
        }
        watch?.Dispose();

        Console.WriteLine(JsonSerializer.Serialize(new LoadReport(
            role, lat.Count, busy, errors, firstError, lat.ToArray(), poll.ToArray(), tree.ToArray())));
        return 0;

        static int Ms(long from) => (int)Stopwatch.GetElapsedTime(from).TotalMilliseconds;
    }

    default:
        Console.WriteLine("режимы: --check-pragmas | --contend | --writer <db> | --hold-write-lock <db> [сек] | --load <новый.db> [--writers N --readers M --seconds S --rate R --read-rate R --records K --journal delete|persist|truncate]");
        return 1;
}

// ── Стенд нагрузки: отчет ребенка и параметры ─────────────────────────────────
// Отчет — одна JSON-строка в stdout: задержки целиком (мс), проценты считает родитель по всем
// процессам роли вместе (перцентили детей между собой честно не сливаются).
sealed record LoadReport(string Role, int Ops, int Busy, int Errors, string? FirstError,
                         int[] Lat, int[] PollLat, int[] TreeLat);

sealed class LoadOptions
{
    public string? Db;
    public int     Writers  = 10;
    public int     Readers  = 10;
    public int     Seconds  = 60;
    public double  Rate     = 1;     // правок в секунду на писателя (0 — без пауз)
    public double  ReadRate = 2;     // чтений в секунду на читателя (0 — без пауз)
    public int     Records  = 2000;
    public string  Journal  = "delete";

    public static LoadOptions Parse(string[] args)
    {
        var o = new LoadOptions();
        for (var i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--writers":   o.Writers  = int.Parse(Next()); break;
                case "--readers":   o.Readers  = int.Parse(Next()); break;
                case "--seconds":   o.Seconds  = int.Parse(Next()); break;
                case "--rate":      o.Rate     = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--read-rate": o.ReadRate = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--records":   o.Records  = int.Parse(Next()); break;
                case "--journal":   o.Journal  = Next().ToLowerInvariant(); break;
                default:
                    if (!args[i].StartsWith("--")) o.Db = Path.GetFullPath(args[i]);
                    break;
            }
        }
        if (o.Journal is not ("delete" or "persist" or "truncate")) o.Journal = "delete";
        return o;
    }
}
