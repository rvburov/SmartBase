using System.Diagnostics;
using System.Globalization;
using SmartBase.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace SmartBase.Tests.DbGenerator;

/// <summary>
/// Генератор тестовой БД Tests/test_database.db — стенд для проверки производительности
/// календаря и таблицы (см. docs/testing/test-database.md).
///
/// Схема НЕ дублируется руками: файл создает <see cref="AppDbContext"/> через
/// Database.Migrate() — ровно те же миграции, что применяет само приложение.
/// Для скорости вставки все вторичные индексы (DDL снимается из sqlite_master,
/// поэтому новые миграции подхватываются автоматически) удаляются на время
/// заливки и строятся заново в конце.
///
/// Профиль данных (масштабируется от --count, по умолчанию 20 000 000 записей):
///   • первая половина — «Событие N»: разброс по годам с 2020-01-01 равным шагом
///     (~11 лет диапазона при любом count), длительность 1 час;
///   • вторая половина — «День N»: ГИГАНТСКИЙ день 2026-07-01, старты по секундам
///     суток по кругу (при 10 млн — ~116 записей на каждую секунду), длительность
///     1 час (хвост уходит за полночь — это нормально);
///   • у каждой записи ВСЕ типы свойств конструктора: 3 даты («Дата» — основная,
///     «Напоминание» = −1 день, «Дедлайн» = +7 дней; все видимы в календаре, т.е.
///     каждая запись дает 3 события и 3 строки calendar_index), текст «Заметка N»,
///     число, список «Статус» (4 варианта), чекбокс «Выполнено», ссылка;
///   • db_records несут денормализованные StartDt/EndDt/AllDay + Title по основной
///     «Дате» (как пишет приложение), db_field_values хранит даты в формате
///     DateValue «start/end» с точностью до минут, calendar_index — ISO-строки
///     формата "o" (7 знаков дробных секунд) по всем трем полям-датам.
///
/// Запуск (из корня репозитория):
///   dotnet run -c Release --project Tests/DbGenerator                            → Tests/test_database.db, 20 млн
///   dotnet run -c Release --project Tests/DbGenerator -- --count 2000000
///   dotnet run -c Release --project Tests/DbGenerator -- --out /Volumes/Disk/big.db --force
///
/// ВНИМАНИЕ: 20 млн записей — это ~240 млн строк и файл ~30 ГБ (100 млн — ~1,2 млрд
/// строк и ~150 ГБ). Такому файлу не место в рабочей папке — большие объемы задавайте
/// через --out ВНЕ репозитория (внешний диск /Volumes/… или ~/TestData/).
/// </summary>
internal static class Program
{
    // ── Профиль стенда ─────────────────────────────────────────────────────────
    private const long DefaultTotal       = 20_000_000;
    private const long ScatterSpanSeconds = 350_000_000;          // ~11 лет разброса «Событий»
    private static readonly DateTime ScatterBase = new(2020, 1, 1, 0, 0, 0);
    private static readonly DateTime HugeDay     = new(2026, 7, 1, 0, 0, 0);
    private static readonly TimeSpan Duration      = TimeSpan.FromHours(1);
    private static readonly TimeSpan ReminderShift = TimeSpan.FromDays(-1);
    private static readonly TimeSpan DeadlineShift = TimeSpan.FromDays(7);
    private const string CreatedAt = "2026-07-01T00:00:00.0000000+03:00";
    private static readonly string[] StatusOptions = ["Новая", "В работе", "Готово", "Отложена"];

    private const int TxRecords = 100_000; // записей на транзакцию
    private const int Group     = 8;       // записей на один multi-row INSERT

    public static int Main(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        Console.OutputEncoding = System.Text.Encoding.UTF8;   // вывод в файл (> log) читаем как UTF-8

        var force  = args.Contains("--force");
        var total  = ArgLong(args, "--count") ?? DefaultTotal;
        var dbPath = ArgStr(args, "--out") ?? Path.Combine("Tests", "test_database.db");

        if (total < 2) { Console.WriteLine("--count должен быть ≥ 2."); return 1; }
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (dir is not null && !Directory.Exists(dir))
        {
            Console.WriteLine($"Папки {dir} нет — запускайте из корня репозитория или задайте --out.");
            return 1;
        }
        if (File.Exists(dbPath))
        {
            if (!force)
            {
                Console.WriteLine($"{dbPath} уже существует — перезапуск с --force пересоздаст файл.");
                return 1;
            }
            File.Delete(dbPath);
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        }

        // Профиль стенда: календарный (по умолчанию, ниже) или широкий разреженный — WideProfile.
        var profile = ArgStr(args, "--profile") ?? "calendar";
        if (profile == "wide") return WideProfile.Run(args, dbPath, total);
        if (profile != "calendar") { Console.WriteLine($"Неизвестный профиль {profile}: calendar | wide."); return 1; }

        var scattered = total / 2;
        var huge      = total - scattered;
        var step      = Math.Max(1, ScatterSpanSeconds / scattered);
        Console.WriteLine($"Стенд: {total:N0} записей → {dbPath}");
        Console.WriteLine($"  «Событие N»: {scattered:N0}, шаг {step} с с {ScatterBase:yyyy-MM-dd} " +
                          $"(по {ScatterBase.AddSeconds(step * (scattered - 1)):yyyy-MM-dd})");
        Console.WriteLine($"  «День N»:    {huge:N0} в дне {HugeDay:yyyy-MM-dd}");

        var sw = Stopwatch.StartNew();

        // 1. Схема — те же миграции EF, что применяет приложение.
        using (var ctx = new AppDbContext(dbPath))
            ctx.Database.Migrate();
        // Закрытый контекст оставляет в пуле ОТКРЫТОЕ подключение, а файл уже в WAL: сырому
        // подключению ниже оно мешает переключить журнал (WAL → OFF требует одиночества), и
        // каждая вставка ждала замок секундами. Пул закрываем (см. WideProfile).
        AppDbContext.ResetPool(dbPath);
        var tSchema = sw.Elapsed;
        Console.WriteLine($"Схема (Database.Migrate): {tSchema.TotalSeconds:0.0} с");

        using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        con.Open();
        Exec(con, "PRAGMA journal_mode=OFF");
        Exec(con, "PRAGMA synchronous=OFF");
        Exec(con, "PRAGMA cache_size=-524288");     // 512 МБ страничного кеша
        Exec(con, "PRAGMA mmap_size=2147483648");   // чтение при построении индексов через mmap
        // temp_store остается FILE: сортировки при построении индексов на сотнях
        // миллионов строк не должны жить в памяти.

        // 2. Коллекция «Задачи» и все 8 полей (все типы конструктора).
        var colId = InsertReturningId(con,
            "INSERT INTO db_collections (Name, GroupId, Position, Icon, Color) " +
            "VALUES ('Задачи', NULL, 1000, 'table', '#007AFF')");
        var fDate = InsertField(con, colId, "Дата",        "date",     "{}", 1000, isCalStart: true, isSystem: true);
        var fRem  = InsertField(con, colId, "Напоминание", "date",     "{}", 1010, isCalStart: true);
        var fDl   = InsertField(con, colId, "Дедлайн",     "date",     "{}", 1020, isCalStart: true);
        var fNote = InsertField(con, colId, "Заметка",     "text",     "{}", 1030);
        var fQty  = InsertField(con, colId, "Количество",  "number",   "{}", 1040);
        var fSt   = InsertField(con, colId, "Статус",      "select",
            """{"options":["Новая","В работе","Готово","Отложена"]}""", 1050);
        var fDone = InsertField(con, colId, "Выполнено",   "checkbox", "{}", 1060);
        var fUrl  = InsertField(con, colId, "Ссылка",      "url",      "{}", 1070);

        // 3. Снять и удалить вторичные индексы (вставка без них в разы быстрее).
        var indexes = new List<(string Name, string Sql)>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='index' AND sql IS NOT NULL ORDER BY name";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) indexes.Add((rd.GetString(0), rd.GetString(1)));
        }
        foreach (var (name, _) in indexes) Exec(con, $"DROP INDEX \"{name}\"");

        // 4. Заливка.
        sw.Restart();
        var giant = huge > 0 ? new GiantCache() : null;
        var batch = new Writer(con, Group, colId, fDate, fRem, fDl, fNote, fQty, fSt, fDone, fUrl);
        var single = new Writer(con, 1, colId, fDate, fRem, fDl, fNote, fQty, fSt, fDone, fUrl);
        var row = new Row();

        long n = 1, nextReport = 1_000_000;
        while (n <= total)
        {
            var txEnd = Math.Min(n + TxRecords - 1, total);
            using var tx = con.BeginTransaction();
            batch.Transaction = tx;
            single.Transaction = tx;

            while (n + Group - 1 <= txEnd)
            {
                for (var g = 0; g < Group; g++, n++)
                {
                    Fill(ref row, n, scattered, step, giant);
                    batch.Set(g, in row);
                }
                batch.Exec();
            }
            for (; n <= txEnd; n++)
            {
                Fill(ref row, n, scattered, step, giant);
                single.Set(0, in row);
                single.Exec();
            }
            tx.Commit();

            if (n > nextReport)
            {
                var rate = (n - 1) / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                var eta  = TimeSpan.FromSeconds((total - n + 1) / rate);
                Console.WriteLine($"  {n - 1,15:N0} / {total:N0}  ({sw.Elapsed.TotalSeconds,6:0} с, " +
                                  $"{rate / 1_000_000 * 60:0.0} млн/мин, осталось ~{eta:h\\:mm\\:ss})");
                while (nextReport < n) nextReport += 1_000_000;
            }
        }
        var tInsert = sw.Elapsed;
        Console.WriteLine($"Вставка: {tInsert.TotalMinutes:0.0} мин");

        // 5. Индексы заново — DDL из sqlite_master, снятый на шаге 3.
        sw.Restart();
        foreach (var (name, sql) in indexes)
        {
            var t = Stopwatch.StartNew();
            Exec(con, sql, timeoutSeconds: 24 * 3600);
            Console.WriteLine($"  индекс {name}: {t.Elapsed.TotalSeconds:0.0} с");
        }
        var tIndex = sw.Elapsed;
        Console.WriteLine($"Индексы: {tIndex.TotalMinutes:0.0} мин");

        Exec(con, "PRAGMA journal_mode=DELETE");
        con.Close();

        var sizeGb = new FileInfo(dbPath).Length / 1024.0 / 1024.0 / 1024.0;
        Console.WriteLine($"Готово: {dbPath}");
        Console.WriteLine($"  записей: {total:N0} (строк в БД: ~{total * 12:N0}), размер: {sizeGb:0.00} ГБ");
        Console.WriteLine($"  время: схема {tSchema.TotalSeconds:0.0} с + вставка {tInsert.TotalMinutes:0.0} мин " +
                          $"+ индексы {tIndex.TotalMinutes:0.0} мин = {(tSchema + tInsert + tIndex).TotalMinutes:0.0} мин");
        return 0;
    }

    // ── Данные одной записи ────────────────────────────────────────────────────

    private struct Row
    {
        public long Id;
        public string S, E, Rs, Re, Ds, De;          // ISO "o" — StartDt/EndDt и calendar_index
        public string V1, V2, V3;                    // значения полей-дат (DateValue.ToStorage)
        public string Title, Note, Qty, Status, Done, Url;
    }

    private static void Fill(ref Row r, long n, long scattered, long step, GiantCache? giant)
    {
        r.Id = n;
        if (n <= scattered)
        {
            var s = ScatterBase.AddSeconds((n - 1) * step);
            var e = s + Duration;
            r.S  = Iso(s);                    r.E  = Iso(e);
            r.Rs = Iso(s + ReminderShift);    r.Re = Iso(e + ReminderShift);
            r.Ds = Iso(s + DeadlineShift);    r.De = Iso(e + DeadlineShift);
            r.V1 = Storage(s, e);
            r.V2 = Storage(s + ReminderShift, e + ReminderShift);
            r.V3 = Storage(s + DeadlineShift, e + DeadlineShift);
            r.Title = $"Событие {n}";
        }
        else
        {
            var sec = (int)((n - scattered - 1) % 86_400);
            var g = giant!;
            r.S  = g.S[sec];  r.E  = g.E[sec];
            r.Rs = g.Rs[sec]; r.Re = g.Re[sec];
            r.Ds = g.Ds[sec]; r.De = g.De[sec];
            r.V1 = g.V1[sec]; r.V2 = g.V2[sec]; r.V3 = g.V3[sec];
            r.Title = $"День {n}";
        }
        r.Note   = $"Заметка {n}";
        r.Qty    = (n % 100_000).ToString(CultureInfo.InvariantCulture);
        r.Status = StatusOptions[(int)(n & 3)];
        r.Done   = (n & 1) == 0 ? "1" : "0";
        r.Url    = $"https://example.com/task/{n}";
    }

    /// <summary>Старты гигантского дня повторяются каждые 86 400 записей — все строки кешируются.</summary>
    private sealed class GiantCache
    {
        public readonly string[] S  = new string[86_400], E  = new string[86_400];
        public readonly string[] Rs = new string[86_400], Re = new string[86_400];
        public readonly string[] Ds = new string[86_400], De = new string[86_400];
        public readonly string[] V1 = new string[86_400], V2 = new string[86_400], V3 = new string[86_400];

        public GiantCache()
        {
            for (var sec = 0; sec < 86_400; sec++)
            {
                var s = HugeDay.AddSeconds(sec);
                var e = s + Duration;
                S[sec]  = Iso(s);                  E[sec]  = Iso(e);
                Rs[sec] = Iso(s + ReminderShift);  Re[sec] = Iso(e + ReminderShift);
                Ds[sec] = Iso(s + DeadlineShift);  De[sec] = Iso(e + DeadlineShift);
                V1[sec] = Storage(s, e);
                V2[sec] = Storage(s + ReminderShift, e + ReminderShift);
                V3[sec] = Storage(s + DeadlineShift, e + DeadlineShift);
            }
        }
    }

    /// <summary>ISO-строка формата DateTime.ToString("o") без смещения (Kind=Unspecified).</summary>
    private static string Iso(DateTime dt) =>
        dt.ToString("yyyy-MM-ddTHH:mm:ss'.0000000'", CultureInfo.InvariantCulture);

    /// <summary>Значение поля-даты (DateValue.ToStorage): минутная точность, «start/end».</summary>
    private static string Storage(DateTime s, DateTime e) =>
        $"{s:yyyy-MM-ddTHH:mm}/{e:yyyy-MM-ddTHH:mm}";

    // ── Многострочные prepared-INSERT'ы на группу записей ─────────────────────

    private sealed class Writer
    {
        private readonly SqliteCommand _rec, _val, _idx;
        private readonly SqliteParameter[][] _recP, _valP, _idxP; // [слот][параметры]

        public SqliteTransaction? Transaction
        {
            set { _rec.Transaction = value; _val.Transaction = value; _idx.Transaction = value; }
        }

        public Writer(SqliteConnection con, int group, long col,
                      long fDate, long fRem, long fDl, long fNote, long fQty, long fSt, long fDone, long fUrl)
        {
            var rec = new List<string>(); var val = new List<string>(); var idx = new List<string>();
            _recP = new SqliteParameter[group][];
            _valP = new SqliteParameter[group][];
            _idxP = new SqliteParameter[group][];
            _rec = con.CreateCommand(); _val = con.CreateCommand(); _idx = con.CreateCommand();

            for (var g = 0; g < group; g++)
            {
                // Аудит записи — как пишет приложение (UpdatedAt = CreatedAt, снимки имени); колонки
                // Description у db_records больше нет (миграция DescriptionAsField).
                rec.Add($"($id{g}, {col}, '{CreatedAt}', '{CreatedAt}', 'Стенд', 'Стенд', $id{g}, $s{g}, $e{g}, 0, $t{g})");
                val.Add($"($id{g}, {fDate}, $v1_{g}), ($id{g}, {fRem}, $v2_{g}), ($id{g}, {fDl}, $v3_{g}), " +
                        $"($id{g}, {fNote}, $v4_{g}), ($id{g}, {fQty}, $v5_{g}), ($id{g}, {fSt}, $v6_{g}), " +
                        $"($id{g}, {fDone}, $v7_{g}), ($id{g}, {fUrl}, $v8_{g})");
                idx.Add($"($id{g}, {fDate}, {col}, $s{g}, $e{g}, 0), " +
                        $"($id{g}, {fRem}, {col}, $rs{g}, $re{g}, 0), " +
                        $"($id{g}, {fDl}, {col}, $ds{g}, $de{g}, 0)");

                _recP[g] = Params(_rec, $"$id{g}", $"$s{g}", $"$e{g}", $"$t{g}");
                _valP[g] = Params(_val, $"$id{g}", $"$v1_{g}", $"$v2_{g}", $"$v3_{g}", $"$v4_{g}",
                                        $"$v5_{g}", $"$v6_{g}", $"$v7_{g}", $"$v8_{g}");
                _idxP[g] = Params(_idx, $"$id{g}", $"$s{g}", $"$e{g}", $"$rs{g}", $"$re{g}", $"$ds{g}", $"$de{g}");
            }

            _rec.CommandText = "INSERT INTO db_records (Id, CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, " +
                               "Position, StartDt, EndDt, AllDay, Title) VALUES " + string.Join(", ", rec);
            _val.CommandText = "INSERT INTO db_field_values (RecordId, FieldId, Value) VALUES " + string.Join(", ", val);
            _idx.CommandText = "INSERT INTO calendar_index (RecordId, FieldId, CollectionId, StartIso, EndIso, " +
                               "AllDay) VALUES " + string.Join(", ", idx);
        }

        private static SqliteParameter[] Params(SqliteCommand cmd, params string[] names)
        {
            var arr = new SqliteParameter[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                arr[i] = cmd.CreateParameter();
                arr[i].ParameterName = names[i];
                cmd.Parameters.Add(arr[i]);
            }
            return arr;
        }

        public void Set(int g, in Row r)
        {
            var p = _recP[g];
            p[0].Value = r.Id; p[1].Value = r.S; p[2].Value = r.E; p[3].Value = r.Title;

            p = _valP[g];
            p[0].Value = r.Id;
            p[1].Value = r.V1; p[2].Value = r.V2; p[3].Value = r.V3; p[4].Value = r.Note;
            p[5].Value = r.Qty; p[6].Value = r.Status; p[7].Value = r.Done; p[8].Value = r.Url;

            p = _idxP[g];
            p[0].Value = r.Id;
            p[1].Value = r.S;  p[2].Value = r.E;
            p[3].Value = r.Rs; p[4].Value = r.Re;
            p[5].Value = r.Ds; p[6].Value = r.De;
        }

        public void Exec()
        {
            _rec.ExecuteNonQuery();
            _val.ExecuteNonQuery();
            _idx.ExecuteNonQuery();
        }
    }

    // ── Мелкие утилиты ─────────────────────────────────────────────────────────

    private static void Exec(SqliteConnection con, string sql, int timeoutSeconds = 600)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeoutSeconds;
        cmd.ExecuteNonQuery();
    }

    private static long InsertReturningId(SqliteConnection con, string insertSql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = insertSql + "; SELECT last_insert_rowid();";
        return (long)cmd.ExecuteScalar()!;
    }

    private static long InsertField(SqliteConnection con, long colId, string name, string type,
                                    string configJson, int position,
                                    bool isCalStart = false, bool isSystem = false)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO db_fields (CollectionId, Name, FieldType, ConfigJson, Position, " +
                          "IsCalStart, IsCalEnd, IsSystem, IsRequired) " +
                          "VALUES ($c, $n, $t, $j, $p, $cs, 0, $sys, 0); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$c", colId);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$j", configJson);
        cmd.Parameters.AddWithValue("$p", position);
        cmd.Parameters.AddWithValue("$cs", isCalStart ? 1 : 0);
        cmd.Parameters.AddWithValue("$sys", isSystem ? 1 : 0);
        return (long)cmd.ExecuteScalar()!;
    }

    private static long? ArgLong(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length &&
               long.TryParse(args[i + 1].Replace("_", ""), out var v) ? v : null;
    }

    private static string? ArgStr(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
