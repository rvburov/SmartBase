using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Сценарий раздувания файла SQLite — воспроизводит путь «Сводной трудоемкости» (несколько
/// сводов по N строк в одной базе, удаление старых, правки) и меряет файл на каждом шаге:
/// <c>bloat [--count 1000000] [--dir PATH] [--keep]</c>.
///
/// Шаги (каждый — строка таблицы «файл / живые / свободно / -wal / время»):
///  1. свод №1 через <see cref="ConstructorRepository.BulkAddRecords"/> (тот же путь, что у
///     расширения): базовая линия — байт на запись;
///  2. свод №2 в ту же базу, затем удаление свода №1 (<c>DeleteCollectionIsolated</c>) —
///     сколько места остается в freelist и не возвращается;
///  3. правка 10 % ячеек более длинными значениями и очистка 5 % — рост от правок; отдельно —
///     1000 честных <c>SetFieldValue</c>: байт журнала на одну правку;
///  4. свод №3 при ОТКРЫТОМ читателе (снимок чтения держится всю заливку) — рост -wal и
///     усечение после чекпоинта;
///  5. VACUUM: время и итог. Затем тот же прогон 1–2 на файле с <c>auto_vacuum=INCREMENTAL</c>
///     и <c>incremental_vacuum</c> после удаления — накладные на вставку и объем (решение,
///     включать ли INCREMENTAL для общих баз).
///
/// Данные — как у свода: обозначение (~10 тыс. разных), трудоемкость (число), дата (день),
/// ФИО (50), проект (100), путь к файлу расчета (3000 разных путей ~90 байт), название ~40 байт.
/// Файлы стенда — во временной папке, по умолчанию удаляются (<c>--keep</c> оставляет).
/// </summary>
internal static class BloatBench
{
    private sealed record Snap(long File, long Live, long Free, long Wal);
    private sealed record Line(string Step, Snap Snap, double Ms);
    private static readonly List<Line> Lines = [];

    private static readonly string[] Names =
        Enumerable.Range(0, 50).Select(i => $"Сотрудник {i:00} И.О.").ToArray();
    private static readonly string[] Projects =
        Enumerable.Range(0, 100).Select(i => $"Проект «Изделие-{i:000}»").ToArray();
    private static readonly string[] Files =
        Enumerable.Range(0, 3000).Select(i =>
            $@"P:\Расчеты\Изделие-{i % 100:000}\2026\{i / 100:00}\Расчет_{i:0000}_Сотрудник_{i % 50:00}.xlsm").ToArray();

    public static int Run(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        Console.OutputEncoding = Encoding.UTF8;   // вывод в файл (> log) читаем как UTF-8, а не в OEM-кодировке консоли

        var count = BenchUtil.ArgInt(args, "--count") ?? 1_000_000;
        var dir   = BenchUtil.ArgStr(args, "--dir") ?? Path.Combine(Path.GetTempPath(), "bloat-bench");
        var keep  = args.Contains("--keep");
        Directory.CreateDirectory(dir);

        Console.WriteLine($"Стенд раздувания: {count:N0} строк на свод, папка {dir}");
        Console.WriteLine();

        var normal = Path.Combine(dir, "bloat_none.db");
        var incr   = Path.Combine(dir, "bloat_incr.db");
        Cleanup(normal); Cleanup(incr);

        try
        {
            RunScenario(normal, count, incremental: false, full: true);
            Print("auto_vacuum=NONE (как сейчас)");
            Lines.Clear();

            RunScenario(incr, count, incremental: true, full: false);
            Print("auto_vacuum=INCREMENTAL (кандидат для общих баз)");
        }
        finally
        {
            if (!keep) { Cleanup(normal); Cleanup(incr); }
            else Console.WriteLine($"Файлы оставлены: {normal}, {incr}");
        }
        return 0;
    }

    private static void RunScenario(string path, int count, bool incremental, bool full)
    {
        if (incremental) PrepareIncremental(path);

        var sw = Stopwatch.StartNew();
        using var m = DatabaseManager.Open(0, "bloat", path);
        var repo = m.Constructor;
        Add("схема (DatabaseManager.Open)", path, sw);

        // 1. Свод №1.
        sw.Restart();
        var shell1 = Shell(repo, "Трудоемкость (свод 1)");
        Fill(path, shell1, count, seed: 1);
        Add($"свод №1: {count:N0} строк (BulkAddRecords)", path, sw);
        var s1 = Lines[^1].Snap;
        Console.WriteLine($"  байт на запись (файл/строк): {s1.File / (double)count:N0}; живых: {s1.Live / (double)count:N0}");

        // 2. Свод №2, удаление свода №1.
        sw.Restart();
        var shell2 = Shell(repo, "Трудоемкость (свод 2)");
        Fill(path, shell2, count, seed: 2);
        Add($"свод №2: еще {count:N0} строк", path, sw);

        sw.Restart();
        repo.DeleteCollectionIsolated(shell1.CollectionId);
        Add("удален свод №1 (DeleteCollectionIsolated)", path, sw);

        if (incremental)
        {
            sw.Restart();
            AppDbContext.ResetPool(path);   // усечение файла — только без чужих mmap-отображений (см. шаг 5)
            using (var c = Open(path))
            {
                Exec(c, "PRAGMA busy_timeout=15000;");
                // Порциями, как будет делать приложение после пакетного удаления: короткие
                // транзакции, любой клиент, без эксклюзива на минуты. Предохранитель от
                // бесконечного цикла — на случай страниц, которые SQLite освободить не может.
                for (var guard = 0; Freelist(c) > 0 && guard < 100_000; guard++)
                    Exec(c, "PRAGMA incremental_vacuum(2000);");
                // Усечение файла в WAL-режиме видно только после чекпоинта (до него укороченный
                // образ лежит в -wal) — фиксируем честный размер файла.
                Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
            Add("incremental_vacuum порциями по 2000 страниц + чекпоинт", path, sw);
        }

        if (!full) return;

        // 3. Правки: 10 % ячеек длиннее, 5 % очищены (сырым UPDATE — то же, что делает с
        //    хранением SetFieldValue, но без журнала: честные правки ниже отдельно).
        sw.Restart();
        var fields = repo.GetFields(shell2.CollectionId);
        var deno   = fields.First(f => f.Name == "Обозначение КД").Id!.Value;
        var proj   = fields.First(f => f.Name == "Проект").Id!.Value;
        using (var c = Open(path))
        {
            Exec(c, "PRAGMA busy_timeout=15000;");
            Exec(c, $"UPDATE db_field_values SET Value = Value || ' — исправлено после проверки нормировщиком (см. замечания)' " +
                    $"WHERE FieldId = {deno} AND (RecordId % 10) = 0;");
            Exec(c, $"DELETE FROM db_field_values WHERE FieldId = {proj} AND (RecordId % 20) = 0;");
        }
        Add("правка 10 % ячеек (длиннее) + очистка 5 %", path, sw);

        sw.Restart();
        var ids = repo.GetRecordsPageAfter(shell2.CollectionId, null, null, 1000).Select(r => r.Id!.Value).ToList();
        var logBefore = LogBytes(path);
        foreach (var id in ids)
            repo.SetFieldValue(id, proj, "Проект «Изделие-999» (перенос по решению ГК)");
        var logAfter = LogBytes(path);
        Add("1000 правок SetFieldValue (журнал, аудит записи)", path, sw);
        Console.WriteLine($"  журнал изменений: +{(logAfter - logBefore) / 1000.0:N0} байт на правку (строка change_log)");

        // 4. Свод №3 при открытом читателе: снимок держит WAL от чекпоинта.
        sw.Restart();
        var shell3 = Shell(repo, "Трудоемкость (свод 3)");
        using (var reader = Open(path))
        {
            Exec(reader, "BEGIN; SELECT COUNT(*) FROM db_records;");   // открытый снимок чтения
            Fill(path, shell3, count, seed: 3);
            Add($"свод №3 при открытом читателе", path, sw);
            Exec(reader, "COMMIT;");
        }
        sw.Restart();
        using (var c = Open(path))
        {
            Exec(c, "PRAGMA busy_timeout=15000;");
            Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        Add("читатель закрыт, wal_checkpoint(TRUNCATE)", path, sw);

        // 5. VACUUM — как делает приложение (DatabaseManager.Vacuum): пул подключений к файлу
        //    закрывается заранее. Иначе простаивающее в пуле подключение главного контекста с
        //    mmap держит отображение файла, и Windows молча не усекает файл после чекпоинта —
        //    freelist пуст, а размер на диске прежний (см. комментарий в DatabaseManager).
        sw.Restart();
        AppDbContext.ResetPool(path);
        using (var c = Open(path))
        {
            Exec(c, "PRAGMA busy_timeout=15000;");
            Exec(c, "VACUUM;", timeoutSeconds: 3600);
            Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");   // см. выше: размер файла — после чекпоинта
        }
        Add("VACUUM + чекпоинт (пул закрыт)", path, sw);
    }

    // ── Свод: каркас и заливка ────────────────────────────────────────────────

    private sealed record ShellIds(int CollectionId, int[] FieldIds);

    private static ShellIds Shell(ConstructorRepository repo, string name)
    {
        var existing = repo.GetAllCollections();
        var position = existing.Count == 0 ? 1000 : existing.Max(c => c.Position) + 1000;
        var col = repo.AddCollection(new DbCollection { Name = name, Position = position });
        var colId = col.Id!.Value;
        int F(string n, string t, int p) =>
            repo.AddField(new DbField { CollectionId = colId, Name = n, FieldType = t, Position = p }).Id!.Value;
        return new ShellIds(colId,
        [
            F("Обозначение КД", "text",   1000),
            F("Тр-ть",          "number", 2000),
            F("Дата",           "date",   3000),
            F("ФИО",            "text",   4000),
            F("Проект",         "text",   5000),
            F("Файл расчета",   "file",   6000),
        ]);
    }

    private static void Fill(string path, ShellIds shell, int count, int seed)
    {
        using var ctx = new AppDbContext(path);
        ctx.ApplyBulkPerformancePragmas();
        var repo = new ConstructorRepository(ctx);
        repo.BulkAddRecords(shell.CollectionId, shell.FieldIds, Rows(count, seed), count, CancellationToken.None);
    }

    private static IEnumerable<BulkRecordRow> Rows(int count, int seed)
    {
        var rnd  = new Random(seed);
        var day0 = new DateTime(2025, 1, 1);
        for (var i = 0; i < count; i++)
        {
            var d = day0.AddDays(rnd.Next(0, 600));
            yield return new BulkRecordRow(
                $"Сборочная единица, узел крепления №{i % 5000:0000} исп. {i % 7}",
                [
                    $"АБВГ.{rnd.Next(100000, 999999)}.{i % 10000:000}",
                    (rnd.Next(1, 400) / 4.0).ToString(CultureInfo.InvariantCulture).Replace('.', ','),
                    new DateValue(d, d.Add(DateValue.EndOfDay), AllDay: true).ToStorage(),
                    Names[i % Names.Length],
                    Projects[(i / 7) % Projects.Length],
                    Files[(i / 200) % Files.Length],
                ]);
        }
    }

    // ── Замер ─────────────────────────────────────────────────────────────────

    private static void Add(string step, string path, Stopwatch sw)
    {
        var ms = sw.Elapsed.TotalMilliseconds;
        Lines.Add(new Line(step, Measure(path), ms));
        var l = Lines[^1];
        Console.WriteLine($"  {step,-58} файл {Size(l.Snap.File),9}  живые {Size(l.Snap.Live),9}  " +
                          $"свободно {Size(l.Snap.Free),9}  -wal {Size(l.Snap.Wal),9}  {BenchUtil.Fmt(ms)}");
    }

    private static Snap Measure(string path)
    {
        using var c = Open(path);
        var ps = Scalar(c, "PRAGMA page_size;");
        var pc = Scalar(c, "PRAGMA page_count;");
        var fl = Scalar(c, "PRAGMA freelist_count;");
        return new Snap(new FileInfo(path).Length, (pc - fl) * ps, fl * ps, Len(path + "-wal"));
    }

    private static void Print(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"Итог — {title}:");
        Console.WriteLine($"  {"шаг",-58} {"файл",10} {"живые",10} {"свободно",10} {"-wal",10} {"время",12}");
        foreach (var l in Lines)
            Console.WriteLine($"  {l.Step,-58} {Size(l.Snap.File),10} {Size(l.Snap.Live),10} {Size(l.Snap.Free),10} {Size(l.Snap.Wal),10} {BenchUtil.Fmt(l.Ms),12}");
        Console.WriteLine();
    }

    private static long LogBytes(string path)
    {
        using var c = Open(path);
        return Scalar(c, "SELECT COALESCE(SUM(length(CAST(Detail AS BLOB)) + length(CAST(TargetName AS BLOB)) + " +
                         "length(CAST(CollectionName AS BLOB)) + length(CAST(UserName AS BLOB)) + 60), 0) FROM change_log;");
    }

    // ── SQLite-мелочи ─────────────────────────────────────────────────────────

    private static void PrepareIncremental(string path)
    {
        // auto_vacuum записывается в заголовок только при создании файла — ставим на пустом
        // файле до того, как DatabaseManager.Open создаст схему.
        using var c = Open(path);
        Exec(c, "PRAGMA auto_vacuum=INCREMENTAL; CREATE TABLE _bootstrap(x); DROP TABLE _bootstrap;");
        if (Scalar(c, "PRAGMA auto_vacuum;") != 2)
            throw new InvalidOperationException("auto_vacuum=INCREMENTAL не применился");
    }

    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, int timeoutSeconds = 600)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeoutSeconds;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 3600;
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    private static long Freelist(SqliteConnection c) => Scalar(c, "PRAGMA freelist_count;");

    private static long Len(string f) => File.Exists(f) ? new FileInfo(f).Length : 0;

    private static string Size(long bytes) => bytes switch
    {
        < 1024L * 1024        => $"{bytes / 1024.0:N0} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:N1} МБ",
        _                     => $"{bytes / 1024.0 / 1024 / 1024:N2} ГБ",
    };

    private static void Cleanup(string path)
    {
        try { SqliteGate.ClearAllPools(); } catch { /* best-effort */ }
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var p = path + suffix;
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        }
    }
}
