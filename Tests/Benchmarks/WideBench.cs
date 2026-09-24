using System.Diagnostics;
using SmartBase.Core.Models;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Цена ШИРОКОЙ таблицы (много свойств) на путях ядра, которыми ходит таблица конструктора:
/// снимок открытия коллекции (ReadOpenSnapshot) с разбором по под-запросам, страница записей
/// (GetRecordsPageAfter), значения страницы (GetFieldValuesBatch), стили ячеек, сборка строк
/// TableRowVm, список значений колонки (панель фильтра), счет записей.
///
/// Меряем время И аллокации (GC.GetTotalAllocatedBytes) — на слабой машине именно аллокации
/// на страницу дают паузы GC. Стенд делает Tests/DbGenerator профилем «wide».
///
/// Запуск: dotnet run -c Release --project Tests/Benchmarks -- wide --db PATH [--pages N] [--rounds N]
///         dotnet run -c Release --project Tests/Benchmarks -- wide --pg ИМЯ_БАЗЫ (стенд sb-pg)
/// </summary>
internal static class WideBench
{
    private const int PageSize = 200;   // как VirtualRowList.PageSize

    public static int Run(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();

        var pgDb   = BenchUtil.ArgStr(args, "--pg");
        var path   = BenchUtil.ArgStr(args, "--db");
        var pages  = BenchUtil.ArgInt(args, "--pages")  ?? 10;
        var rounds = BenchUtil.ArgInt(args, "--rounds") ?? 3;
        var hold   = BenchUtil.ArgInt(args, "--hold")   ?? 50;   // MaxPages кеша VirtualRowList

        if (pgDb is null && path is null)
        {
            Console.WriteLine("Нужен --db PATH (SQLite) или --pg ИМЯ (PostgreSQL-стенд sb-pg).");
            return 1;
        }

        if (pgDb is not null)
        {
            var cs      = $"Host=localhost;Port=5455;Username=postgres;Password=postgres;Database={pgDb};Timeout=5";
            var pgCols  = BenchUtil.ArgInt(args, "--pgfill");
            var pgRows  = BenchUtil.ArgInt(args, "--pgrows") ?? 2000;
            Console.WriteLine($"Стенд PostgreSQL: база {pgDb}");
            if (pgCols is int c)
            {
                Console.WriteLine($"Схема public базы {pgDb} СБРАСЫВАЕТСЯ, заливаем {pgRows:N0} × {c:N0}.");
                ResetSchema(cs);
                using var fill = DatabaseManager.Open(9301, "wide-pg", cs, provider: "postgres");
                Build(fill.Constructor, c, pgRows);
            }
            using var m = DatabaseManager.Open(9302, "wide-pg", cs, provider: "postgres");
            return Measure(m, cs, pages, rounds, hold, isolatedPath: null);
        }

        if (!File.Exists(path!)) { Console.WriteLine($"Нет файла {path}"); return 1; }
        Console.WriteLine($"Стенд SQLite: {path} ({new FileInfo(path!).Length / 1024.0 / 1024:N0} МБ)");
        using var mgr = DatabaseManager.Open(9300, "wide", path!);
        return Measure(mgr, path!, pages, rounds, hold, isolatedPath: path);
    }

    private static int Measure(DatabaseManager mgr, string source, int pages, int rounds, int hold, string? isolatedPath)
    {
        var repo = mgr.Constructor;
        var cols = repo.GetAllCollections();
        var col  = cols.FirstOrDefault(c => c.Name == "Широкая") ?? cols.FirstOrDefault();
        if (col?.Id is not int colId) { Console.WriteLine("В базе нет коллекций."); return 1; }

        var fields = repo.GetFields(colId);
        var total  = repo.CountRecords(colId);
        Console.WriteLine($"Коллекция «{col.Name}»: {fields.Count:N0} свойств, {total:N0} записей");
        Console.WriteLine($"Раундов: {rounds}, страниц в скролле: {pages} × {PageSize} строк");
        Console.WriteLine();
        Console.WriteLine($"{"Операция",-52}{"мс",10}{"МБ аллокаций",16}  примечание");
        Console.WriteLine(new string('─', 110));

        // ── 1. Снимок открытия коллекции (то, что таблица читает при переключении) ──
        // Холодный: свежий контекст на каждый прогон — как ReadOpenSnapshotAsync в приложении.
        if (isolatedPath is not null)
        {
            Row("ReadOpenSnapshot (свежий контекст, как в приложении)", rounds, () =>
            {
                using var ctx = new AppDbContext(isolatedPath);
                ctx.ApplyPerformancePragmas();
                return new ConstructorRepository(ctx).ReadOpenSnapshot(colId);
            }, s => $"полей {s.Fields.Count}, записей {s.RecordCount}");
        }
        Row("ReadOpenSnapshot (теплый контекст)", rounds,
            () => repo.ReadOpenSnapshot(colId), s => $"полей {s.Fields.Count}");

        // Разбор снимка по под-запросам — что именно растет от числа колонок.
        Row("  · GetFields",          rounds, () => repo.GetFields(colId),          r => $"{r.Count} полей");
        Row("  · GetColumnOrder",     rounds, () => repo.GetColumnOrder(colId),     r => $"{r.Count} ключей");
        Row("  · GetColumnDisplay",   rounds, () => repo.GetColumnDisplay(colId),   r => $"{r.Count} ключей");
        Row("  · GetColumnStyles",    rounds, () => repo.GetColumnStyles(colId),    r => $"{r.Count} ключей");
        Row("  · GetColumnWidths",    rounds, () => repo.GetColumnWidths(colId),    r => $"{r.Count} ключей");
        Row("  · GetSystemFieldNames", rounds, () => repo.GetSystemFieldNames(colId), r => $"{r.Count} ключей");
        Row("  · GetRecordDisplay",   rounds, () => repo.GetRecordDisplay(colId),   r => $"{r.Count} строк");
        Row("  · GetRowHeights",      rounds, () => repo.GetRowHeights(colId),      r => $"{r.Count} строк");
        Row("  · GetRowStyles",       rounds, () => repo.GetRowStyles(colId),       r => $"{r.Count} строк");
        Row("  · CountRecords",       rounds, () => repo.CountRecords(colId),       n => $"{n:N0} записей");

        // ── 2. Страница таблицы ────────────────────────────────────────────────────
        List<DbRecord> page = [];
        Row($"GetRecordsPageAfter (первая страница, {PageSize})", rounds,
            () => page = repo.GetRecordsPageAfter(colId, null, null, PageSize), r => $"{r.Count} записей");
        var ids = page.Select(r => r.Id!.Value).ToList();

        Dictionary<int, Dictionary<int, string>> valMap = [];
        Row($"GetFieldValuesBatch ({PageSize} записей)", rounds,
            () => valMap = repo.GetFieldValuesBatch(ids),
            r => $"{r.Values.Sum(v => v.Count):N0} значений ({r.Values.Sum(v => v.Count) / Math.Max(1, r.Count)} на запись)");

        // Тот же результат сырым SQL, без материализации сущностей EF — потолок оптимизации
        // GetFieldValuesBatch (для сравнения по времени и аллокациям).
        Row($"То же сырым SQL (потолок оптимизации)", rounds, () => RawValues(mgr, ids),
            r => $"{r.Values.Sum(v => v.Count):N0} значений");

        Row($"GetCellStylesBatch ({PageSize} записей)", rounds,
            () => repo.GetCellStylesBatch(colId, ids), r => $"{r.Count} строк со стилями");

        // Полный путь страницы, как его зовет VirtualRowList (свой контекст в Task.Run).
        Row($"LoadPageAsync (весь путь страницы, все поля)", rounds,
            () => repo.LoadPageAsync(colId, r => new ConstructorRepository.RecordPage(r.GetRecordsPageAfter(colId, null, null, PageSize)), null)
                      .GetAwaiter().GetResult(),
            d => $"{d.Page.Records.Count} записей, {d.Values.Values.Sum(v => v.Count):N0} значений");

        // Окно колонок (этап 1 drawn-grid.md): значения только первых 20 полей страницы.
        var window = fields.Take(20).Select(f => f.Id!.Value).ToList();
        Row($"LoadPageAsync (окно {window.Count} колонок)", rounds,
            () => repo.LoadPageAsync(colId, r => new ConstructorRepository.RecordPage(r.GetRecordsPageAfter(colId, null, null, PageSize)), window)
                      .GetAwaiter().GetResult(),
            d => $"{d.Page.Records.Count} записей, {d.Values.Values.Sum(v => v.Count):N0} значений");

        // Сборка строк вида (без UI-дерева): ровно то, что делает VirtualRowList.FillFromData.
        Row($"Сборка {PageSize} TableRowVm из значений страницы", rounds, () =>
        {
            var rows = new TableRowVm[page.Count];
            for (var i = 0; i < page.Count; i++)
            {
                var id = page[i].Id!.Value;
                rows[i] = new TableRowVm(repo, id, i + 1,
                                         valMap.TryGetValue(id, out var v) ? new Dictionary<int, string>(v) : [],
                                         page[i].Title ?? "", page[i].CreatedAt ?? "", page[i].UpdatedAt ?? "",
                                         page[i].CreatedBy ?? "", page[i].UpdatedBy ?? "");
            }
            return rows;
        }, r => $"{r.Length} строк");

        // Чтение ВСЕХ ячеек страницы через индексатор строки — как делают привязки DataGrid,
        // когда колонки видимы (200 строк × число колонок обращений).
        var vmRows = BuildRows(repo, page, valMap);
        var keys   = fields.Select(f => f.Id!.Value.ToString()).ToArray();
        Row($"Чтение всех ячеек страницы ({PageSize}×{keys.Length} привязок)", rounds, () =>
        {
            var len = 0;
            foreach (var row in vmRows)
                foreach (var k in keys)
                    len += row[k].Length;
            return len;
        }, n => $"суммарно символов: {n:N0}");

        // ── 3. Скролл: pages страниц подряд keyset'ом ─────────────────────────────
        var sw = Stopwatch.StartNew();
        var before = GC.GetTotalAllocatedBytes(true);
        int? ap = null, aid = null;
        long vals = 0;
        var got = 0;
        for (var i = 0; i < pages; i++)
        {
            var p = repo.GetRecordsPageAfter(colId, ap, aid, PageSize);
            if (p.Count == 0) break;
            var pids = p.Select(r => r.Id!.Value).ToList();
            var vm   = repo.GetFieldValuesBatch(pids);
            vals += vm.Values.Sum(v => v.Count);
            got  += p.Count;
            ap = p[^1].Position; aid = p[^1].Id;
        }
        sw.Stop();
        Print($"Скролл {pages} страниц (записи+значения)", sw.Elapsed.TotalMilliseconds,
              (GC.GetTotalAllocatedBytes(true) - before) / 1024.0 / 1024,
              $"{got:N0} строк, {vals:N0} значений, {sw.Elapsed.TotalMilliseconds / Math.Max(1, pages):N0} мс/страница");

        // ── 4. Панель фильтра колонки ─────────────────────────────────────────────
        var textField = fields.FirstOrDefault(f => f.Name == "Текст") ?? fields[0];
        Row($"GetFieldValueCounts (колонка «{textField.Name}»)", rounds,
            () => repo.GetFieldValueCounts(colId, textField.Id!.Value), r => $"{r.Count:N0} значений");
        Row("GetTitleCounts (колонка «Название»)", rounds,
            () => repo.GetTitleCounts(colId), r => $"{r.Count:N0} значений");

        // ── 5. Память, удерживаемая страницами в куче (LRU-кеш VirtualRowList) ────
        Console.WriteLine();
        var maxPages = Math.Min(hold, (total + PageSize - 1) / PageSize);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var heapBefore = GC.GetTotalMemory(true);
        var kept = new List<object>();
        var marks = new HashSet<int> { 1, 5, 10, 25, 50, maxPages };
        long exact = 0, rowsHeld = 0;
        for (var i = 0; i < maxPages; i++)
        {
            var p    = repo.GetRecordsPage(colId, i * PageSize, PageSize);
            if (p.Count == 0) break;
            var pids = p.Select(r => r.Id!.Value).ToList();
            var vm   = repo.GetFieldValuesBatch(pids);
            kept.Add(BuildRows(repo, p, vm));
            kept.Add(vm);
            rowsHeld += p.Count;
            // Точный счет удерживаемого: строка значения = заголовок 22 + 2 байта на символ
            // (округление до 8), запись словаря = 16 байт + ~8 байт корзины, сам TableRowVm ~80.
            foreach (var row in vm.Values)
            {
                exact += 80 + 64;
                foreach (var v in row.Values) exact += (22 + 2L * v.Length + 7) / 8 * 8 + 24;
            }
            if (!marks.Contains(i + 1)) continue;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var bytes = GC.GetTotalMemory(true) - heapBefore;
            Console.WriteLine($"  кеш {i + 1,3} страниц ({rowsHeld,6:N0} строк): " +
                              $"{bytes / 1024.0 / 1024,8:N1} МБ  ({bytes / (double)rowsHeld:N0} байт на строку); " +
                              $"счетом по данным {exact / 1024.0 / 1024:N1} МБ ({exact / (double)rowsHeld:N0} байт на строку)");
        }
        GC.KeepAlive(kept);

        Console.WriteLine($"Процесс: рабочий набор {Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024:N0} МБ, " +
                          $"управляемая куча {GC.GetTotalMemory(false) / 1024.0 / 1024:N0} МБ, " +
                          $"аллокаций за прогон {GC.GetTotalAllocatedBytes(true) / 1024.0 / 1024:N0} МБ");
        Console.WriteLine($"Источник: {source}");
        return 0;
    }

    // Значения страницы сырым SQL: тот же словарь, что у GetFieldValuesBatch, но без сущностей EF.
    private static Dictionary<int, Dictionary<int, string>> RawValues(DatabaseManager mgr, List<int> ids)
    {
        var res  = new Dictionary<int, Dictionary<int, string>>(ids.Count);
        var conn = mgr.Context.Database.GetDbConnection();
        var open = conn.State != System.Data.ConnectionState.Open;
        if (open) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT RecordId, FieldId, Value FROM db_field_values WHERE RecordId IN (" +
                              string.Join(",", ids) + ")";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var rid = rd.GetInt32(0);
                if (!res.TryGetValue(rid, out var map)) res[rid] = map = new Dictionary<int, string>();
                map[rd.GetInt32(1)] = rd.IsDBNull(2) ? "" : rd.GetString(2);
            }
        }
        finally { if (open) conn.Close(); }
        return res;
    }

    // Стенд на PostgreSQL путями приложения: коллекция «Широкая» + columns текстовых свойств
    // + rows записей, ВСЕ ячейки заполнены (плотная матрица, как dense-профиль SQLite).
    private static void Build(ConstructorRepository repo, int columns, int rows)
    {
        var sw  = Stopwatch.StartNew();
        var col = repo.AddCollection(new DbCollection { Name = "Широкая" }).Id!.Value;
        var ids = new List<int>(columns);
        for (var i = 0; i < columns; i++)
            ids.Add(repo.AddField(new DbField
            { CollectionId = col, Name = i == 0 ? "Текст" : $"Текст {i}", FieldType = "text", Position = (i + 1) * 10 }).Id!.Value);
        Console.WriteLine($"  поля: {columns:N0} за {BenchUtil.Fmt(sw.Elapsed.TotalMilliseconds)}");

        sw.Restart();
        repo.BulkAddRecords(col, ids, Rows(columns, rows), rows, CancellationToken.None);
        Console.WriteLine($"  записи: {rows:N0} × {columns:N0} значений за {BenchUtil.Fmt(sw.Elapsed.TotalMilliseconds)}");
        Console.WriteLine();
    }

    private static IEnumerable<BulkRecordRow> Rows(int columns, int rows)
    {
        for (var n = 1; n <= rows; n++)
        {
            var vals = new string[columns];
            for (var i = 0; i < columns; i++) vals[i] = $"Значение {i + 1}-{n % 997}";
            yield return new BulkRecordRow($"Запись {n}", vals);
        }
    }

    private static void ResetSchema(string cs)
    {
        using var conn = new Npgsql.NpgsqlConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
        cmd.ExecuteNonQuery();
    }

    private static TableRowVm[] BuildRows(ConstructorRepository repo, List<DbRecord> page,
                                          Dictionary<int, Dictionary<int, string>> valMap)
    {
        var rows = new TableRowVm[page.Count];
        for (var i = 0; i < page.Count; i++)
        {
            var id = page[i].Id!.Value;
            rows[i] = new TableRowVm(repo, id, i + 1, valMap.TryGetValue(id, out var v) ? v : [],
                                     page[i].Title ?? "", page[i].CreatedAt ?? "", page[i].UpdatedAt ?? "",
                                     page[i].CreatedBy ?? "", page[i].UpdatedBy ?? "");
        }
        return rows;
    }

    // Прогоняет операцию rounds раз, печатает МЕДИАНУ времени и аллокации последнего прогона.
    private static void Row<T>(string label, int rounds, Func<T> op, Func<T, string>? note = null)
    {
        var times = new List<double>(rounds);
        T last = default!;
        double mb = 0;
        for (var i = 0; i < rounds; i++)
        {
            var a0 = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            last = op();
            sw.Stop();
            mb = (GC.GetTotalAllocatedBytes(true) - a0) / 1024.0 / 1024;
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        Print(label, times[times.Count / 2], mb, note is null ? "" : note(last));
    }

    private static void Print(string label, double ms, double mb, string note) =>
        Console.WriteLine($"{label,-52}{ms,10:N1}{mb,16:N1}  {note}");
}
