using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;

namespace SmartBase.Tests.DbGenerator;

/// <summary>
/// Профиль «wide» — широкая разреженная коллекция для проверки раздувания файла и скорости
/// ядра на пределе по обеим осям: много записей И много колонок.
///
///   dotnet run -c Release --project Tests/DbGenerator -- --profile wide --count 10000000 --columns 2000 --fill 20 --out D:\stand\wide.db
///
/// Одна коллекция «Широкая»: по одному свойству КАЖДОГО типа конструктора — text, number,
/// date (видима в календаре → строка calendar_index), select, status, multiselect
/// (JSON-массив), checkbox, url, file (абсолютный путь, 3000 разных, как у свода
/// трудоемкости), user (20 имен), relation (ребро db_record_links на более раннюю запись
/// этой же коллекции) и четыре вычисляемых (created_at / updated_at / created_by /
/// updated_by — значений в db_field_values не имеют); остальные <c>--columns − 15</c>
/// колонок — текст «Текст N».
///
/// Заполнение РАЗРЕЖЕННОЕ: у записи заполнены все типизированные ячейки (10 значений +
/// ребро связи + строка календаря) и <c>--fill − 11</c> текстовых колонок, выбранных
/// детерминированно от номера записи (у соседних записей — разные колонки, каждая
/// текстовая колонка заполнена примерно у fill/columns записей). Полное заполнение
/// 10 млн × 2000 = 20 млрд значений (> 1 ТБ) на рабочей машине невозможно, а разреженная
/// матрица — обычный вид «широких» таблиц.
///
/// Значения — в каноне хранения приложения (DateValue.ToStorage, JSON мультисписка,
/// «1»/«0» у чекбокса), аудит записи — как пишет приложение (даты ISO «o», снимки имени).
/// Техника — как у календарного профиля: индексы снимаются на время заливки, multi-row
/// prepared INSERT, транзакции по 50 тыс. записей, индексы строятся в конце.
///
/// Ожидание при 10 млн / 2000 / 20: ~200 млн значений, файл ~15–20 ГБ, 30–45 мин; на время
/// построения индексов нужен запас места под temp-файлы сортировки (~размер индексов).
/// Задавайте --out на диске с запасом ≥ 60 ГБ.
/// </summary>
internal static class WideProfile
{
    private const int  TxRecords = 50_000;   // записей на транзакцию
    private const int  Group     = 8;        // записей на один multi-row INSERT
    private const int  TypedCount = 15;      // свойств с типами (11 хранимых + 4 вычисляемых)
    private const string CreatedAt = "2026-07-01T00:00:00.0000000+03:00";
    private static readonly DateTime DayBase = new(2020, 1, 1);

    private static readonly string[] SelectOptions = ["Новая", "В работе", "Готово", "Отложена"];
    private static readonly string[] StatusOptions = ["К выполнению", "В работе", "Готово"];
    private static readonly string[] MultiOptions  = ["Дизайн", "Backend", "Frontend", "Тесты", "Документы"];
    private static readonly string[] Users =
        Enumerable.Range(0, 20).Select(i => $"Сотрудник {i:00} Иванович").ToArray();
    private static readonly string[] Files =
        Enumerable.Range(0, 3000).Select(i =>
            $@"P:\Расчеты\Изделие-{i % 100:000}\2026\{i / 100:00}\Расчет_{i:0000}_Сотрудник_{i % 20:00}.xlsm").ToArray();

    public static int Run(string[] args, string dbPath, long total)
    {
        var columns = (int)(ArgLong(args, "--columns") ?? 2000);
        var fill    = (int)(ArgLong(args, "--fill")    ?? 20);
        if (columns < TypedCount + 1) { Console.WriteLine($"--columns должен быть ≥ {TypedCount + 1}."); return 1; }
        var textCount = columns - TypedCount;
        var extras    = Math.Clamp(fill - 11, 0, textCount);   // текстовых ячеек на запись сверх типизированных
        var perRecord = 10 + extras;                            // строк db_field_values на запись

        Console.WriteLine($"Стенд «wide»: {total:N0} записей × {columns:N0} колонок → {dbPath}");
        Console.WriteLine($"  на запись: 10 типизированных значений + {extras} текстовых из {textCount:N0} колонок " +
                          $"+ ребро связи + строка календаря; всего значений ≈ {total * perRecord:N0}");

        var sw = Stopwatch.StartNew();

        // 1. Схема — те же миграции EF, что применяет приложение.
        using (var ctx = new AppDbContext(dbPath))
            ctx.Database.Migrate();
        // Закрытый контекст оставляет в пуле ОТКРЫТОЕ подключение, а файл уже в WAL: сырому
        // подключению ниже оно мешает переключить журнал (WAL → OFF требует одиночества), и
        // каждая вставка ждала замок секундами (2000 полей — 6 минут). Пул закрываем.
        AppDbContext.ResetPool(dbPath);
        var tSchema = sw.Elapsed;
        Console.WriteLine($"Схема (Database.Migrate): {tSchema.TotalSeconds:0.0} с");

        using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        con.Open();
        Exec(con, "PRAGMA journal_mode=OFF");
        Exec(con, "PRAGMA synchronous=OFF");
        Exec(con, "PRAGMA cache_size=-524288");
        Exec(con, "PRAGMA mmap_size=2147483648");

        // 2. Коллекция и поля.
        var colId = InsertReturningId(con,
            "INSERT INTO db_collections (Name, GroupId, Position, Icon, Color) " +
            "VALUES ('Широкая', NULL, 1000, 'table', '#007AFF')");
        var pos = 1000;
        long F(string name, string type, string config = "{}", bool calStart = false) =>
            InsertField(con, colId, name, type, config, pos += 10, calStart);

        var fText   = F("Текст",        "text");
        var fNum    = F("Число",        "number");
        var fDate   = F("Дата",         "date", calStart: true);
        var fSel    = F("Список",       "select",      Options(SelectOptions));
        var fStatus = F("Статус",       "status",      Options(StatusOptions));
        var fMulti  = F("Мультисписок", "multiselect", Options(MultiOptions));
        var fCheck  = F("Чекбокс",      "checkbox");
        var fUrl    = F("Ссылка",       "url");
        var fFile   = F("Файл",         "file");
        var fUser   = F("Пользователь", "user");
        var fRel    = F("Связь",        "relation", new RelationConfig((int)colId, Single: true).ToConfigJson());
        F("Создано",  "created_at");
        F("Изменено", "updated_at");
        F("Автор",    "created_by");
        F("Правил",   "updated_by");
        var textIds = new long[textCount];
        using (var tx = con.BeginTransaction())
        {
            for (var i = 0; i < textCount; i++)
                textIds[i] = InsertField(con, colId, $"Текст {i + 1}", "text", "{}", pos += 10, false, tx);
            tx.Commit();
        }
        Console.WriteLine($"Поля: {TypedCount + textCount:N0} ({sw.Elapsed.TotalSeconds:0.0} с)");

        // 3. Снять вторичные индексы на время заливки.
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
        var typed  = new long[] { fText, fNum, fDate, fSel, fStatus, fMulti, fCheck, fUrl, fFile, fUser };
        var stride = Math.Max(1, textCount / Math.Max(1, extras));
        var batch  = new Writer(con, Group, colId, typed, fRel, fDate, extras);
        var single = new Writer(con, 1,     colId, typed, fRel, fDate, extras);
        var row    = new Row(extras);

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
                    Fill(row, n, textIds, stride);
                    batch.Set(g, row);
                }
                batch.Exec();
            }
            for (; n <= txEnd; n++)
            {
                Fill(row, n, textIds, stride);
                single.Set(0, row);
                single.Exec();
            }
            tx.Commit();

            if (n > nextReport)
            {
                var rate = (n - 1) / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                var eta  = TimeSpan.FromSeconds((total - n + 1) / rate);
                Console.WriteLine($"  {n - 1,15:N0} / {total:N0}  ({sw.Elapsed.TotalSeconds,6:0} с, " +
                                  $"{rate / 1_000_000 * 60:0.00} млн зап./мин, осталось ~{eta:h\\:mm\\:ss})");
                while (nextReport < n) nextReport += 1_000_000;
            }
        }
        var tInsert = sw.Elapsed;
        Console.WriteLine($"Вставка: {tInsert.TotalMinutes:0.0} мин");

        // 5. Индексы заново.
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

        var bytes  = new FileInfo(dbPath).Length;
        var values = total * perRecord;
        Console.WriteLine($"Готово: {dbPath}");
        Console.WriteLine($"  записей: {total:N0}, значений: {values:N0}, связей: {total:N0}, строк календаря: {total:N0}; " +
                          $"размер: {bytes / 1024.0 / 1024 / 1024:0.00} ГБ");
        Console.WriteLine($"  байт на запись: {bytes / (double)total:N0}; байт на значение (файл / значений): {bytes / (double)values:N0}");
        Console.WriteLine($"  время: схема {tSchema.TotalSeconds:0.0} с + вставка {tInsert.TotalMinutes:0.0} мин " +
                          $"+ индексы {tIndex.TotalMinutes:0.0} мин = {(tSchema + tInsert + tIndex).TotalMinutes:0.0} мин");
        return 0;
    }

    // ── Данные одной записи ────────────────────────────────────────────────────

    private sealed class Row(int extras)
    {
        public long   Id;
        public string Title = "", User = "";
        public string Text = "", Num = "", Date = "", Sel = "", Status = "", Multi = "", Check = "", Url = "", File = "";
        public string StartIso = "", EndIso = "";
        public long   LinkTo;
        public readonly long[]   ExtraIds  = new long[extras];
        public readonly string[] ExtraVals = new string[extras];
    }

    private static void Fill(Row r, long n, long[] textIds, int stride)
    {
        r.Id    = n;
        r.Title = $"Запись {n}";
        r.User  = Users[(int)(n % Users.Length)];
        r.Text  = $"Текстовое значение записи {n}";
        r.Num   = ((n % 10000) / 4.0).ToString(CultureInfo.InvariantCulture);
        var day = DayBase.AddDays(n % 4000);
        var dv  = new DateValue(day, day.Add(DateValue.EndOfDay), AllDay: true);
        r.Date  = dv.ToStorage();
        (r.StartIso, r.EndIso) = dv.ToRecordColumns();
        r.Sel    = SelectOptions[(int)(n & 3)];
        r.Status = StatusOptions[(int)(n % 3)];
        r.Multi  = MultiSelectValue.Serialize(MultiOptions.Where((_, i) => ((n >> i) & 1) == 1).DefaultIfEmpty(MultiOptions[0]));
        r.Check  = (n & 1) == 0 ? "1" : "0";
        r.Url    = $"https://example.com/item/{n}";
        r.File   = Files[(int)(n % Files.Length)];
        // Связь — на более раннюю запись (детерминированный «случайный» выбор); первая — на себя.
        r.LinkTo = n <= 1 ? 1 : 1 + (long)((ulong)(n * 2654435761L) % (ulong)(n - 1));
        // Текстовые колонки: старт от номера записи, дальше с шагом — у соседних записей
        // разные колонки, у каждой записи колонки различны (шаг ≥ 1, extras ≤ textCount).
        var start = (int)(n % textIds.Length);
        for (var k = 0; k < r.ExtraIds.Length; k++)
        {
            var col = (start + k * stride) % textIds.Length;
            r.ExtraIds[k]  = textIds[col];
            r.ExtraVals[k] = $"Значение {col + 1}-{n % 997}";
        }
    }

    // ── Многострочные prepared-INSERT'ы на группу записей ─────────────────────

    private sealed class Writer
    {
        private readonly SqliteCommand _rec, _val, _link, _cal;
        private readonly SqliteParameter[][] _recP, _valP, _linkP, _calP;
        private readonly int _extras;

        public SqliteTransaction? Transaction
        {
            set { _rec.Transaction = value; _val.Transaction = value; _link.Transaction = value; _cal.Transaction = value; }
        }

        public Writer(SqliteConnection con, int group, long col, long[] typed, long fRel, long fDate, int extras)
        {
            _extras = extras;
            var rec = new List<string>(); var val = new List<string>(); var link = new List<string>(); var cal = new List<string>();
            _recP = new SqliteParameter[group][]; _valP = new SqliteParameter[group][];
            _linkP = new SqliteParameter[group][]; _calP = new SqliteParameter[group][];
            _rec = con.CreateCommand(); _val = con.CreateCommand(); _link = con.CreateCommand(); _cal = con.CreateCommand();

            for (var g = 0; g < group; g++)
            {
                rec.Add($"($id{g}, {col}, '{CreatedAt}', '{CreatedAt}', $u{g}, $u{g}, $id{g}, NULL, NULL, 0, $t{g}, NULL)");

                var vs = new List<string>();
                for (var i = 0; i < typed.Length; i++) vs.Add($"($id{g}, {typed[i]}, $v{i}_{g})");
                for (var k = 0; k < extras; k++)       vs.Add($"($id{g}, $xf{k}_{g}, $xv{k}_{g})");
                val.Add(string.Join(", ", vs));

                link.Add($"({fRel}, $id{g}, $to{g}, 0, 1000)");
                cal.Add($"($id{g}, {fDate}, {col}, $s{g}, $e{g}, 1)");

                var recNames = new[] { $"$id{g}", $"$u{g}", $"$t{g}" };
                var valNames = Enumerable.Range(0, typed.Length).Select(i => $"$v{i}_{g}")
                    .Concat(Enumerable.Range(0, extras).SelectMany(k => new[] { $"$xf{k}_{g}", $"$xv{k}_{g}" }))
                    .Prepend($"$id{g}").ToArray();
                _recP[g]  = Params(_rec, recNames);
                _valP[g]  = Params(_val, valNames);
                _linkP[g] = Params(_link, $"$id{g}", $"$to{g}");
                _calP[g]  = Params(_cal, $"$id{g}", $"$s{g}", $"$e{g}");
            }

            _rec.CommandText = "INSERT INTO db_records (Id, CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, " +
                               "Position, StartDt, EndDt, AllDay, Title, Color) VALUES " + string.Join(", ", rec);
            _val.CommandText = "INSERT INTO db_field_values (RecordId, FieldId, Value) VALUES " + string.Join(", ", val);
            _link.CommandText = "INSERT INTO db_record_links (FieldId, FromRecordId, ToRecordId, TargetDbId, Position) VALUES " +
                                string.Join(", ", link);
            _cal.CommandText = "INSERT INTO calendar_index (RecordId, FieldId, CollectionId, StartIso, EndIso, AllDay) VALUES " +
                               string.Join(", ", cal);
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

        public void Set(int g, Row r)
        {
            var p = _recP[g];
            p[0].Value = r.Id; p[1].Value = r.User; p[2].Value = r.Title;

            p = _valP[g];
            p[0].Value = r.Id;
            p[1].Value = r.Text;  p[2].Value = r.Num;   p[3].Value = r.Date;  p[4].Value = r.Sel;  p[5].Value = r.Status;
            p[6].Value = r.Multi; p[7].Value = r.Check; p[8].Value = r.Url;   p[9].Value = r.File; p[10].Value = r.User;
            for (var k = 0; k < _extras; k++)
            {
                p[11 + 2 * k].Value = r.ExtraIds[k];
                p[12 + 2 * k].Value = r.ExtraVals[k];
            }

            p = _linkP[g];
            p[0].Value = r.Id; p[1].Value = r.LinkTo;

            p = _calP[g];
            p[0].Value = r.Id; p[1].Value = r.StartIso; p[2].Value = r.EndIso;
        }

        public void Exec()
        {
            _rec.ExecuteNonQuery();
            _val.ExecuteNonQuery();
            _link.ExecuteNonQuery();
            _cal.ExecuteNonQuery();
        }
    }

    // ── Мелкие утилиты ─────────────────────────────────────────────────────────

    private static string Options(string[] options) =>
        JsonSerializer.Serialize(new { options });

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

    private static long InsertField(SqliteConnection con, long colId, string name, string type, string configJson,
                                    int position, bool isCalStart, SqliteTransaction? tx = null)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO db_fields (CollectionId, Name, FieldType, ConfigJson, Position, " +
                          "IsCalStart, IsCalEnd, IsSystem, IsRequired, IsTaskExec, IsTaskAssigner, IsTaskStatus, IsDescription) " +
                          "VALUES ($c, $n, $t, $j, $p, $cs, 0, 0, 0, 0, 0, 0, 0); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$c", colId);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$j", configJson);
        cmd.Parameters.AddWithValue("$p", position);
        cmd.Parameters.AddWithValue("$cs", isCalStart ? 1 : 0);
        return (long)cmd.ExecuteScalar()!;
    }

    private static long? ArgLong(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length &&
               long.TryParse(args[i + 1].Replace("_", ""), out var v) ? v : null;
    }
}
