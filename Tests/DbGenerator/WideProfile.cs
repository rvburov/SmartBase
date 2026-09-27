using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;

namespace SmartBase.Tests.DbGenerator;

/// <summary>
/// Профиль «wide» — широкая коллекция для проверки раздувания файла и скорости ядра на пределе
/// по обеим осям: много записей И много колонок.
///
///   dotnet run -c Release --project Tests/DbGenerator -- --profile wide --count 10000000 --columns 2000 --fill 20 --out D:\stand\wide.db
///   dotnet run -c Release --project Tests/DbGenerator -- --profile wide --count 100000 --columns 1000 --fill 1000 --variants --out D:\stand\full.db
///
/// Одна коллекция «Широкая»: по одному свойству КАЖДОГО типа конструктора — text, number,
/// date (видима в календаре → строка calendar_index), select, status, multiselect
/// (JSON-массив), checkbox, url, file (абсолютный путь, 3000 разных, как у свода
/// трудоемкости), user (20 имен), relation (ребро db_record_links на более раннюю запись
/// этой же коллекции) и четыре вычисляемых (created_at / updated_at / created_by /
/// updated_by — значений в db_field_values не имеют); остальные колонки — текст «Текст N».
///
/// <c>--variants</c> — еще десять свойств тех же типов с другими настройками и формами значения
/// (<see cref="VariantCount"/>): дата со временем, скрытая в календаре; многодневный период;
/// числа в рублях, в долларах с точкой, в процентах, с разрядами и с точкой; мультисписок со
/// склейкой переносом строки; связь с несколькими записями; многострочный текст. Без ключа
/// состав колонок прежний — стенды wide20/wide1000 пересобираются теми же.
///
/// Заполнение: у записи заполнены все типизированные ячейки (10 значений + ребро связи + строка
/// календаря) и <c>--fill − 11</c> текстовых колонок, выбранных детерминированно от номера
/// записи (у соседних записей — разные колонки, каждая текстовая колонка заполнена примерно у
/// fill/columns записей). <c>--fill</c> не меньше числа колонок — заполнено все. Полное
/// заполнение 10 млн × 2000 = 20 млрд значений (> 1 ТБ) на рабочей машине невозможно, а
/// разреженная матрица — обычный вид «широких» таблиц. В разреженной базе период, связи и
/// многострочный текст из вариантов заполнены у части записей (у каждой 10-й, 5-й и 10-й), в
/// заполненной — у всех.
///
/// Значения — в каноне хранения приложения (DateValue.ToStorage, JSON мультисписка,
/// «1»/«0» у чекбокса, десятичный разделитель числа — как в настройке свойства), аудит записи —
/// как пишет приложение (даты ISO «o», снимки имени). Строка calendar_index — у каждого
/// значения-даты, в том числе скрытой (как пишет приложение). Индексы снимаются на время
/// заливки, multi-row prepared INSERT порциями (<see cref="RowSink"/>), транзакции по 50 тыс.
/// записей, индексы строятся в конце.
///
/// Ожидание при 10 млн / 2000 / 20: ~200 млн значений, файл ~22 ГБ, 40–60 мин; на время
/// построения индексов нужен запас места под temp-файлы сортировки (~размер индексов).
/// Задавайте --out на диске с запасом ≥ 60 ГБ.
/// </summary>
internal static class WideProfile
{
    private const int  TxRecords    = 50_000;   // записей на транзакцию (заполненных — в 10 раз меньше)
    private const int  TypedCount   = 15;       // свойств с типами (11 хранимых + 4 вычисляемых)
    private const int  VariantCount = 10;       // свойств --variants
    private const string CreatedAt = "2026-07-01T00:00:00.0000000+03:00";
    private static readonly DateTime DayBase = new(2020, 1, 1);

    private static readonly string[] SelectOptions = ["Новая", "В работе", "Готово", "Отложена"];
    private static readonly string[] StatusOptions = ["К выполнению", "В работе", "Готово"];
    private static readonly string[] MultiOptions  = ["Дизайн", "Backend", "Frontend", "Тесты", "Документы"];
    private static readonly string[] TagOptions    =
        ["Срочно", "Клиент", "Внутреннее", "Отчет", "Смета", "Чертеж", "Согласование", "Архив"];
    private static readonly string[] Users =
        Enumerable.Range(0, 20).Select(i => $"Сотрудник {i:00} Иванович").ToArray();
    private static readonly string[] Files =
        Enumerable.Range(0, 3000).Select(i =>
            $@"P:\Расчеты\Изделие-{i % 100:000}\2026\{i / 100:00}\Расчет_{i:0000}_Сотрудник_{i % 20:00}.xlsm").ToArray();

    // Постоянные значения параметров — упакованы один раз.
    private static readonly object Zero = 0L, One = 1L;

    public static int Run(string[] args, string dbPath, long total)
    {
        var columns  = (int)(ArgLong(args, "--columns") ?? 2000);
        var fill     = (int)(ArgLong(args, "--fill")    ?? 20);
        var variants = args.Contains("--variants");
        var fixedCount = TypedCount + (variants ? VariantCount : 0);
        if (columns < fixedCount + 1) { Console.WriteLine($"--columns должен быть ≥ {fixedCount + 1}."); return 1; }
        var textCount = columns - fixedCount;
        var extras    = Math.Clamp(fill - 11, 0, textCount);   // текстовых ячеек на запись сверх типизированных
        var dense     = extras == textCount;                    // заполнено все

        Console.WriteLine($"Стенд «wide»: {total:N0} записей × {columns:N0} колонок → {dbPath}");
        Console.WriteLine($"  на запись: 10 типизированных значений + {extras:N0} текстовых из {textCount:N0} колонок " +
                          $"+ ребро связи + строка календаря" +
                          (variants ? $"; варианты настроек: {VariantCount} свойств" + (dense ? "" : " (часть — у части записей)") : ""));

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
        object F(string name, string type, string config = "{}", bool calStart = false) =>
            InsertField(con, colId, name, type, config, pos += 10, calStart);

        var typed = new[]
        {
            F("Текст",        "text"),
            F("Число",        "number"),
            F("Дата",         "date", calStart: true),
            F("Список",       "select",      Options(SelectOptions)),
            F("Статус",       "status",      Options(StatusOptions)),
            F("Мультисписок", "multiselect", Options(MultiOptions)),
            F("Чекбокс",      "checkbox"),
            F("Ссылка",       "url"),
            F("Файл",         "file"),
            F("Пользователь", "user"),
        };
        var fDate = typed[2];
        var fRel  = F("Связь", "relation", new RelationConfig((int)colId, Single: true).ToConfigJson());
        F("Создано",  "created_at");
        F("Изменено", "updated_at");
        F("Автор",    "created_by");
        F("Правил",   "updated_by");

        Variants? v = null;
        if (variants)
            v = new Variants(
                Timed:  F("Дата и время",   "date"),                      // скрыта в календаре
                Period: F("Период",         "date", calStart: true),
                Rub:    F("Сумма",          "number", Number(NumberFormatKind.Currency, 2, true, "₽", true, DecimalMark.Comma)),
                Usd:    F("Цена",           "number", Number(NumberFormatKind.Currency, 2, true, "$", false, DecimalMark.Dot)),
                Share:  F("Доля",           "number", Number(NumberFormatKind.Percent, 1, false, "₽", true, DecimalMark.Comma)),
                Qty:    F("Количество",     "number", Number(NumberFormatKind.Grouped, 0, true, "₽", true, DecimalMark.Comma)),
                Dot:    F("Число с точкой", "number", Number(NumberFormatKind.Plain, -1, false, "₽", true, DecimalMark.Dot)),
                Tags:   F("Теги",           "multiselect",
                          JsonSerializer.Serialize(new { options = TagOptions, separator = "newline" })),
                Links:  F("Связи",          "relation",
                          new RelationConfig((int)colId, Single: false, Separator: "semicolon").ToConfigJson()),
                Long:   F("Длинный текст",  "text"));

        var textIds = new object[textCount];
        using (var tx = con.BeginTransaction())
        {
            for (var i = 0; i < textCount; i++)
                textIds[i] = InsertField(con, colId, $"Текст {i + 1}", "text", "{}", pos += 10, false, tx);
            tx.Commit();
        }
        Console.WriteLine($"Поля: {columns:N0} ({sw.Elapsed.TotalSeconds:0.0} с)");

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
        var stride  = Math.Max(1, textCount / Math.Max(1, extras));
        var posStep = PositionStep(total);
        var col     = (object)colId;
        var recs   = new RowSink(con, "db_records",
            ["Id", "CollectionId", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "Position", "AllDay", "Title"], 32);
        var vals   = new RowSink(con, "db_field_values", ["RecordId", "FieldId", "Value"], 128);
        var links  = new RowSink(con, "db_record_links", ["FieldId", "FromRecordId", "ToRecordId", "TargetDbId", "Position"], 64);
        var cal    = new RowSink(con, "calendar_index", ["RecordId", "FieldId", "CollectionId", "StartIso", "EndIso", "AllDay"], 64);
        RowSink[] sinks = [recs, vals, links, cal];

        // Заполненная запись на 1000 колонок — тысяча значений: транзакции и отчеты чаще.
        var txRecords  = dense ? TxRecords / 10 : TxRecords;
        var reportStep = dense ? 10_000 : 1_000_000;
        long n = 1, nextReport = reportStep;
        while (n <= total)
        {
            var txEnd = Math.Min(n + txRecords - 1, total);
            using var tx = con.BeginTransaction();
            foreach (var s in sinks) s.Transaction = tx;

            // Записи транзакции — раньше их значений, связей и календаря: внешние ключи включены,
            // а порции у таблиц разного размера.
            for (var m = n; m <= txEnd; m++)
            {
                var user = Users[(int)(m % Users.Length)];
                recs.Add(m, col, CreatedAt, CreatedAt, user, user, m * posStep, Zero, $"Запись {m}");
            }
            recs.Flush();

            for (; n <= txEnd; n++)
            {
                var id   = (object)n;
                var user = Users[(int)(n % Users.Length)];
                var day  = DayBase.AddDays(n % 4000);
                var date = new DateValue(day, day.Add(DateValue.EndOfDay), AllDay: true);
                vals.Add(id, typed[0], $"Текстовое значение записи {n}");
                vals.Add(id, typed[1], NumberFormatValue.NormalizeStorage(
                    ((n % 10000) / 4.0).ToString(CultureInfo.InvariantCulture), DecimalMark.Comma));
                vals.Add(id, typed[2], date.ToStorage());
                vals.Add(id, typed[3], SelectOptions[(int)(n & 3)]);
                vals.Add(id, typed[4], StatusOptions[(int)(n % 3)]);
                vals.Add(id, typed[5], MultiSelectValue.Serialize(
                    MultiOptions.Where((_, i) => ((n >> i) & 1) == 1).DefaultIfEmpty(MultiOptions[0])));
                vals.Add(id, typed[6], (n & 1) == 0 ? "1" : "0");
                vals.Add(id, typed[7], $"https://example.com/item/{n}");
                vals.Add(id, typed[8], Files[(int)(n % Files.Length)]);
                vals.Add(id, typed[9], user);
                Calendar(cal, id, fDate, col, date);

                // Связь — на более раннюю запись (детерминированный «случайный» выбор); первая — на себя.
                links.Add(fRel, id, n <= 1 ? One : (object)(1 + (long)((ulong)(n * 2654435761L) % (ulong)(n - 1))), Zero, 1000L);

                if (v is not null) v.Fill(vals, links, cal, id, n, col, dense);

                // Текстовые колонки: старт от номера записи, дальше с шагом — у соседних записей
                // разные колонки, у каждой записи колонки различны (шаг ≥ 1, extras ≤ textCount).
                var start = (int)(n % textCount);
                for (var k = 0; k < extras; k++)
                {
                    var c = (start + k * stride) % textCount;
                    vals.Add(id, textIds[c], $"Значение {c + 1}-{n % 997}");
                }
            }
            foreach (var s in sinks) s.Flush();
            tx.Commit();

            if (n > nextReport)
            {
                var rate = (n - 1) / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                var eta  = TimeSpan.FromSeconds((total - n + 1) / rate);
                Console.WriteLine($"  {n - 1,15:N0} / {total:N0}  ({sw.Elapsed.TotalSeconds,6:0} с, " +
                                  $"{rate / 1_000_000 * 60:0.00} млн зап./мин, осталось ~{eta:h\\:mm\\:ss})");
                while (nextReport < n) nextReport += reportStep;
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

        var bytes = new FileInfo(dbPath).Length;
        Console.WriteLine($"Готово: {dbPath}");
        Console.WriteLine($"  записей: {total:N0}, значений: {vals.Rows:N0}, связей: {links.Rows:N0}, строк календаря: {cal.Rows:N0}; " +
                          $"размер: {bytes / 1024.0 / 1024 / 1024:0.00} ГБ");
        Console.WriteLine($"  байт на запись: {bytes / (double)total:N0}; байт на значение (файл / значений): {bytes / (double)vals.Rows:N0}");
        Console.WriteLine($"  время: схема {tSchema.TotalSeconds:0.0} с + вставка {tInsert.TotalMinutes:0.0} мин " +
                          $"+ индексы {tIndex.TotalMinutes:0.0} мин = {(tSchema + tInsert + tIndex).TotalMinutes:0.0} мин");
        return 0;
    }

    // ── Варианты настроек (--variants) ─────────────────────────────────────────

    private sealed record Variants(object Timed, object Period, object Rub, object Usd, object Share,
                                   object Qty, object Dot, object Tags, object Links, object Long)
    {
        /// <summary>Значения записи <paramref name="n"/>. В разреженной базе период, связи и
        /// многострочный текст — у каждой 10-й, 5-й и 10-й записи, в заполненной — у всех.</summary>
        public void Fill(RowSink vals, RowSink links, RowSink cal, object id, long n, object col, bool dense)
        {
            var day   = DayBase.AddDays(n % 4000);
            var start = day.AddMinutes(480 + n % 20 * 30);
            var timed = new DateValue(start, start.AddMinutes(90), AllDay: false);
            vals.Add(id, Timed, timed.ToStorage());
            Calendar(cal, id, Timed, col, timed);

            if (dense || n % 10 == 0)
            {
                var from   = DayBase.AddDays(n * 7 % 4000);
                var period = new DateValue(from, from.AddDays(1 + n % 5).Add(DateValue.EndOfDay), AllDay: true);
                vals.Add(id, Period, period.ToStorage());
                Calendar(cal, id, Period, col, period);
            }

            var rub = n * 7919 % 100_000_000;
            var usd = n * 104_729 % 10_000_000;
            vals.Add(id, Rub,   $"{rub / 100},{rub % 100:00}");
            vals.Add(id, Usd,   $"{usd / 100}.{usd % 100:00}");
            vals.Add(id, Share, NumberFormatValue.NormalizeStorage(
                (n % 1000 / 10.0).ToString(CultureInfo.InvariantCulture), DecimalMark.Comma));
            vals.Add(id, Qty,   (n * 131 % 100_000_000).ToString(CultureInfo.InvariantCulture));
            vals.Add(id, Dot,   (n % 10000 / 4.0).ToString(CultureInfo.InvariantCulture));

            // 1–4 тега; шаг 3 взаимно прост с 8 — теги записи различны.
            var first = (int)(n * 5 % TagOptions.Length);
            vals.Add(id, Tags, MultiSelectValue.Serialize(
                Enumerable.Range(0, 1 + (int)(n % 4)).Select(i => TagOptions[(first + i * 3) % TagOptions.Length])));

            // 1–3 связи на разные более ранние записи: смещения 0, шаг, 2·шаг меньше n − 1.
            if (n > 1 && (dense || n % 5 == 0))
            {
                var span  = (ulong)(n - 1);
                var count = (int)Math.Min(1 + n % 3, n - 1);
                var step  = Math.Max(1UL, span / 3);
                var from  = (ulong)(n * 2654435761L) % span;
                for (var k = 0; k < count; k++)
                    links.Add(Links, id, 1 + (long)((from + (ulong)k * step) % span), Zero, 1000L * (k + 1));
            }

            if (dense || n % 10 == 0)
                vals.Add(id, Long, $"Подробное описание записи {n}: несколько строк текста, как в заметке.\n" +
                                   $"Вторая строка — условия и сроки, вариант {n % 97}.\n" +
                                   $"Третья строка — ответственный {Users[(int)(n % Users.Length)]}.");
        }
    }

    /// <summary>Строка проекции календаря для значения-даты (как SyncCalendarIndexValue).</summary>
    private static void Calendar(RowSink cal, object id, object fieldId, object col, DateValue date)
    {
        var (s, e) = date.ToRecordColumns();
        cal.Add(id, fieldId, col, s, e, date.AllDay ? One : Zero);
    }

    // ── Многострочные prepared-INSERT'ы порциями ──────────────────────────────

    /// <summary>
    /// Multi-row prepared INSERT порциями по <c>rows</c> строк: в команде сотни параметров, не
    /// тысячи. Microsoft.Data.Sqlite ищет каждый параметр по имени перебором, поэтому команда на
    /// 16 тыс. параметров (8 заполненных записей на 1000 колонок) заливала 3000 записей 6 минут.
    /// </summary>
    private sealed class RowSink
    {
        private readonly SqliteCommand _chunk, _one;
        private readonly SqliteParameter[] _chunkP, _oneP;
        private readonly int _width, _rows;
        private int _n;

        /// <summary>Сколько строк добавлено за все время.</summary>
        public long Rows { get; private set; }

        public RowSink(SqliteConnection con, string table, string[] columns, int rows)
        {
            _width = columns.Length;
            _rows  = rows;
            _chunk = con.CreateCommand();
            _one   = con.CreateCommand();
            _chunkP = new SqliteParameter[_width * rows];
            _oneP   = new SqliteParameter[_width];

            var head   = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ";
            var tuples = new List<string>(rows);
            for (var r = 0; r < rows; r++)
            {
                var names = new string[_width];
                for (var c = 0; c < _width; c++)
                    _chunkP[r * _width + c] = Param(_chunk, names[c] = $"$p{r}_{c}");
                tuples.Add("(" + string.Join(", ", names) + ")");
            }
            _chunk.CommandText = head + string.Join(", ", tuples);

            var one = new string[_width];
            for (var c = 0; c < _width; c++) _oneP[c] = Param(_one, one[c] = $"$q{c}");
            _one.CommandText = head + "(" + string.Join(", ", one) + ")";
        }

        public SqliteTransaction? Transaction
        {
            set { _chunk.Transaction = value; _one.Transaction = value; }
        }

        public void Add(params ReadOnlySpan<object> values)
        {
            var at = _n * _width;
            for (var c = 0; c < _width; c++) _chunkP[at + c].Value = values[c];
            Rows++;
            if (++_n < _rows) return;
            _chunk.ExecuteNonQuery();
            _n = 0;
        }

        /// <summary>Дописать неполную порцию — по строке (перед концом транзакции).</summary>
        public void Flush()
        {
            for (var r = 0; r < _n; r++)
            {
                for (var c = 0; c < _width; c++) _oneP[c].Value = _chunkP[r * _width + c].Value;
                _one.ExecuteNonQuery();
            }
            _n = 0;
        }

        private static SqliteParameter Param(SqliteCommand cmd, string name)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            cmd.Parameters.Add(p);
            return p;
        }
    }

    // ── Мелкие утилиты ─────────────────────────────────────────────────────────

    /// <summary>Шаг номеров записей (<c>Position</c>) — как у пакета записей в пустую коллекцию
    /// (AllocatePositions): 1000, на больших коллекциях мельче — чтобы уложиться в int.</summary>
    internal static long PositionStep(long total) => Math.Clamp(int.MaxValue / (total + 1), 1, 1000);

    private static string Options(string[] options) =>
        JsonSerializer.Serialize(new { options });

    private static string Number(NumberFormatKind kind, int decimals, bool thousands, string currency,
                                 bool suffix, DecimalMark mark) =>
        JsonSerializer.Serialize(NumberFormatValue.ToConfigObject(
            new NumberFormat(kind, decimals, thousands, currency, suffix, mark)));

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
