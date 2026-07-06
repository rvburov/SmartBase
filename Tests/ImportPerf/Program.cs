using System.Diagnostics;
using System.Text;
using ClosedXML.Excel;
using DataBase.Core.Importers;
using DataBase.Core.Importers.TableSources;
using DataBase.Database;
using DataBase.Database.Repositories;

namespace DataBase.Tests.ImportPerf;

/// <summary>
/// Функциональная матрица и замер производительности импорта таблиц.
///
/// Вызывает РОВНО те методы, что и приложение (SidebarTree.OpenImportExport):
/// ImportPlanner.Plan → ITableSource.Analyze → SourceHelpers.WithTitles(ReadRows) →
/// ConstructorRepository.ImportStreamedCollectionIsolated. Тестовые файлы (CSV в разных
/// кодировках, JSON/NDJSON, xlsx со скрытыми строками/столбцами/листами) генерирует сам
/// во временную папку; БД — свежая на каждый прогон.
///
/// Запуск (из корня репозитория):
///   dotnet run -c Release --project Tests/ImportPerf                → селф-тест матрицы
///   dotnet run -c Release --project Tests/ImportPerf -- --perf --rows 1000000
///                                                                   → замер большого CSV
///   ... [--dir путь]  — рабочая папка (по умолчанию %TEMP%/import-perf)
/// </summary>
internal static class Program
{
    private static int _pass, _fail;
    private static string _db = "";

    public static int Main(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        EncodingSetup.Ensure();

        var dir = ArgStr(args, "--dir") ?? Path.Combine(Path.GetTempPath(), "import-perf");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        _db = Path.Combine(dir, "import_test.db");

        using var m = DatabaseManager.Open(0, "import-test", _db);
        var repo = m.Constructor;

        if (args.Contains("--perf"))
        {
            Perf(repo, dir, ArgInt(args, "--rows") ?? 1_000_000);
            return 0;
        }

        Console.WriteLine($"Рабочая папка: {dir}");
        Console.WriteLine();

        RunMatrix(repo, dir);

        Console.WriteLine();
        Console.WriteLine($"Итого: PASS {_pass}, FAIL {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // ── Матрица ───────────────────────────────────────────────────────────────

    private static void RunMatrix(ConstructorRepository repo, string dir)
    {
        // 1. CSV UTF-8: кавычки, перенос строки в поле, пустое название (одно → без номера).
        {
            var f = Path.Combine(dir, "utf8.csv");
            File.WriteAllText(f,
                "Название,Цена,Комментарий\n" +
                "Товар 1,10.5,\"многострочный\nтекст, с запятой\"\n" +
                ",20,\"кавычки \"\"внутри\"\"\"\n" +
                "Товар 3,30,\n", new UTF8Encoding(false));
            var ids = Import(repo, Std(f, "CSV UTF-8"));
            var (fields, count, titles) = Snapshot(ids[0]);
            Check("CSV UTF-8: 3 записи",                count == 3);
            Check("CSV UTF-8: поля [Цена, Комментарий]", fields.SequenceEqual(["Цена", "Комментарий"]));
            Check("CSV UTF-8: названия и «Без названия»",
                  titles.SequenceEqual(["Товар 1", "Без названия", "Товар 3"]));
            Check("CSV UTF-8: кавычки/переносы в значениях",
                  Values(ids[0], 0)["Комментарий"] == "многострочный\nтекст, с запятой"
                  && Values(ids[0], 1)["Комментарий"] == "кавычки \"внутри\"");
        }

        // 2. CSV: хвост пустых столбцов (экспорт Excel) и хвостовые пустые строки отбрасываются.
        {
            var f = Path.Combine(dir, "tails.csv");
            File.WriteAllText(f, "Название,A,B,,,\nX,1,2,,,\nY,3,4,,,\n,,,,,\n,,,,,\n", new UTF8Encoding(false));
            var ids = Import(repo, Std(f, "CSV хвосты"));
            var (fields, count, _) = Snapshot(ids[0]);
            Check("CSV хвосты: пустые столбцы обрезаны", fields.SequenceEqual(["A", "B"]));
            Check("CSV хвосты: пустые строки в конце отброшены", count == 2);
        }

        // 3. CSV Windows-1251 с «;»: кодировка и разделитель определяются автоматически.
        {
            var f = Path.Combine(dir, "cp1251.csv");
            File.WriteAllBytes(f, Encoding.GetEncoding(1251).GetBytes(
                "Название;Описание\nЁжик;Съешь ещё этих мягких французских булок\n"));
            var ids = Import(repo, Std(f, "CSV 1251"));
            var (fields, count, titles) = Snapshot(ids[0]);
            Check("CSV 1251: кириллица не побилась",
                  count == 1 && titles[0] == "Ёжик"
                  && Values(ids[0], 0)["Описание"] == "Съешь ещё этих мягких французских булок");
            Check("CSV 1251: разделитель «;»", fields.SequenceEqual(["Описание"]));
        }

        // 4. JSON-массив: union ключей, title по ключу name, null → пусто, вложенное → сырой JSON.
        {
            var f = Path.Combine(dir, "arr.json");
            File.WriteAllText(f,
                """[{"name":"Первый","price":1.5,"nested":{"a":1}},{"price":null,"extra":true}]""",
                new UTF8Encoding(false));
            var ids = Import(repo, Std(f, "JSON массив"));
            var (fields, count, titles) = Snapshot(ids[0]);
            Check("JSON: 2 записи, union полей", count == 2 && fields.SequenceEqual(["price", "nested", "extra"]));
            Check("JSON: title из ключа name", titles.SequenceEqual(["Первый", "Без названия"]));
            Check("JSON: вложенный объект сырым текстом", Values(ids[0], 0)["nested"] == """{"a":1}""");
            Check("JSON: null → пусто, bool как в файле",
                  Values(ids[0], 1)["price"] == "" && Values(ids[0], 1)["extra"] == "true");
        }

        // 5. NDJSON: объект в строке.
        {
            var f = Path.Combine(dir, "nd.json");
            File.WriteAllText(f,
                """{"Название":"Один","x":1}""" + "\n" + """{"Название":"Два","x":2.25}""" + "\n",
                new UTF8Encoding(false));
            var ids = Import(repo, Std(f, "NDJSON"));
            var (_, count, titles) = Snapshot(ids[0]);
            Check("NDJSON: 2 записи, приоритет ключа «Название»",
                  count == 2 && titles.SequenceEqual(["Один", "Два"])
                  && Values(ids[0], 1)["x"] == "2.25");
        }

        // 6. XLSX стандартный: даты фиксированным форматом, числа без двоичного шума,
        //    скрытые строка и столбец импортируются, пустой заголовок → «Без названия».
        {
            var f = Path.Combine(dir, "std.xlsx");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.AddWorksheet("Данные");
                ws.Cell(1, 1).Value = "Название"; ws.Cell(1, 2).Value = "Дата";
                ws.Cell(1, 3).Value = "Число";                                  // 4-й заголовок пуст
                ws.Cell(2, 1).Value = "Стр 1"; ws.Cell(2, 2).Value = new DateTime(2026, 7, 6);
                ws.Cell(2, 3).Value = 0.1 + 0.2; ws.Cell(2, 4).Value = "скрытый столбец";
                ws.Cell(3, 1).Value = "Скрытая строка"; ws.Cell(3, 2).Value = new DateTime(2026, 7, 6, 15, 30, 0);
                ws.Cell(3, 3).Value = 1234.5;
                ws.Row(3).Hide();
                ws.Column(4).Hide();
                var hidden = wb.AddWorksheet("Скрытый");
                hidden.Cell(1, 1).Value = "Название"; hidden.Cell(2, 1).Value = "С листа 2";
                hidden.Visibility = XLWorksheetVisibility.Hidden;
                wb.SaveAs(f);
            }
            var ids = Import(repo, Std(f, "XLSX стандартный"));
            var (fields, count, titles) = Snapshot(ids[0]);
            Check("XLSX: скрытая строка импортирована", count == 2 && titles[1] == "Скрытая строка");
            Check("XLSX: пустой заголовок → «Без названия»", fields.SequenceEqual(["Дата", "Число", "Без названия"]));
            Check("XLSX: дата дд.MM.гггг и дата-время",
                  Values(ids[0], 0)["Дата"] == "06.07.2026" && Values(ids[0], 1)["Дата"] == "06.07.2026 15:30");
            Check("XLSX: числа без двоичного шума",
                  Values(ids[0], 0)["Число"] == "0.3" && Values(ids[0], 1)["Число"] == "1234.5");
            Check("XLSX: скрытый столбец импортирован", Values(ids[0], 0)["Без названия"] == "скрытый столбец");

            // 7. Тот же файл: все листы в разные коллекции (включая скрытый лист).
            var split = Import(repo, Std(f, "Книга", sheets: SheetMode.SplitAll));
            Check("XLSX split: 2 коллекции (со скрытым листом)",
                  split.Count == 2
                  && CollectionName(split[0]) == "Книга — Данные"
                  && CollectionName(split[1]) == "Книга — Скрытый");
            Check("XLSX split: данные скрытого листа", Snapshot(split[1]).Titles.SequenceEqual(["С листа 2"]));

            // 8. Все листы в одну коллекцию: union столбцов по заголовкам.
            var merged = Import(repo, Std(f, "Слитая", sheets: SheetMode.MergeAll));
            var ms = Snapshot(merged[0]);
            Check("XLSX merge: строки всех листов", ms.Count == 3 && ms.Titles[2] == "С листа 2");
            Check("XLSX merge: union столбцов", ms.Fields.SequenceEqual(["Дата", "Число", "Без названия"]));
        }

        // 9. Ручной режим: заголовки во 2-й строке, данные с 3-й, столбец названия — «B».
        {
            var f = Path.Combine(dir, "manual.xlsx");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.AddWorksheet("Лист1");
                ws.Cell(1, 1).Value = "мусорная строка";
                ws.Cell(2, 1).Value = "Код"; ws.Cell(2, 2).Value = "Имя";
                ws.Cell(3, 1).Value = "K-1"; ws.Cell(3, 2).Value = "Альфа";
                ws.Cell(4, 1).Value = "K-2"; ws.Cell(4, 2).Value = "Бета";
                wb.SaveAs(f);
            }
            var ids = Import(repo, Std(f, "Ручной", manual: true, headerRow: 2, dataRow: 3, titleCol: "B"));
            var (fields, count, titles) = Snapshot(ids[0]);
            Check("XLSX ручной: строки/столбец по параметрам",
                  count == 2 && titles.SequenceEqual(["Альфа", "Бета"])
                  && fields.SequenceEqual(["Код"]) && Values(ids[0], 1)["Код"] == "K-2");
        }

        // 9а. Исключения: строки (диапазон и перечисление), столбцы (буква), листы; и защита
        //     столбца названия.
        {
            var f = Path.Combine(dir, "exclude.xlsx");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.AddWorksheet("Главный");
                ws.Cell(1, 1).Value = "Название"; ws.Cell(1, 2).Value = "A";
                ws.Cell(1, 3).Value = "B";        ws.Cell(1, 4).Value = "C";
                for (var r = 2; r <= 7; r++)
                {
                    ws.Cell(r, 1).Value = $"Строка {r}";
                    ws.Cell(r, 2).Value = r; ws.Cell(r, 3).Value = r * 10; ws.Cell(r, 4).Value = r * 100;
                }
                var extra = wb.AddWorksheet("Лишний");
                extra.Cell(1, 1).Value = "Название"; extra.Cell(2, 1).Value = "не должен попасть";
                wb.SaveAs(f);
            }
            // Исключаем строки 3-4 и 6, столбец C (3-й) и лист «Лишний»; листы → разные коллекции.
            var ids = Import(repo, Std(f, "Исключения", manual: true, sheets: SheetMode.SplitAll,
                                       excludeSheets: "Лишний", excludeRows: "3-4, 6", excludeCols: "C"));
            var (fields, count, titles) = Snapshot(ids[0]);
            Check("Исключения: лист исключён (одна коллекция)", ids.Count == 1 && CollectionName(ids[0]) == "Исключения");
            Check("Исключения: строки 3-4, 6 пропущены",
                  count == 3 && titles.SequenceEqual(["Строка 2", "Строка 5", "Строка 7"]));
            // Буква «C» = 3-й столбец листа, его заголовок — «B»: в полях остаются «A» и «C».
            Check("Исключения: столбец (буква C, заголовок B) убран из полей",
                  fields.SequenceEqual(["A", "C"]));
            Check("Исключения: остальные значения на месте", Values(ids[0], 1)["C"] == "500");

            Check("Исключения: столбец названия исключать нельзя",
                  Fails(() => Import(repo, Std(f, "X", manual: true, excludeCols: "A-B")))
                      ?.Contains("нельзя исключать") == true);
        }

        // 10. Папка с подпапкой: юниты в порядке путей, битый .xlsx валит СВОЙ юнит,
        //     готовые коллекции остаются, имена — из имён файлов.
        {
            var folder = Path.Combine(dir, "folder");
            Directory.CreateDirectory(Path.Combine(folder, "sub"));
            File.WriteAllText(Path.Combine(folder, "a1.csv"), "Название,V\nX,1\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "a2.json"), """[{"name":"Y"}]""", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "sub", "a3.csv"), "Название\nZ\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "junk.txt"), "не таблица");
            File.WriteAllText(Path.Combine(folder, "zbad.xlsx"), "это не Excel");

            var created = new List<int>();
            var err = Fails(() => Import(repo, Std(folder, "", folder: true), created));
            Check("Папка: 3 файла импортированы до битого (с подпапкой)",
                  created.Count == 3
                  && CollectionName(created[0]) == "a1" && CollectionName(created[1]) == "a2"
                  && CollectionName(created[2]) == "a3");
            Check("Папка: ошибка называет битый файл", err is not null && err.Contains("zbad.xlsx"));
        }

        // 11. Отмена между пакетами: недолитая коллекция удалена целиком.
        {
            var f = Path.Combine(dir, "cancel.csv");
            using (var w = new StreamWriter(f, false, new UTF8Encoding(false)))
            {
                w.WriteLine("Название,A,B");
                for (var i = 0; i < 200_000; i++) w.WriteLine($"Строка {i},{i},{i * 2}");
            }
            var before = CollectionCount();
            using var cts = new CancellationTokenSource();
            var cancelled = false;
            try
            {
                var units = ImportPlanner.Plan(Std(f, "Отменяемая"));
                var src = units[0].OpenSource();
                var schema = src.Analyze(null, cts.Token);
                var rows = SourceHelpers.WithTitles(src.ReadRows(cts.Token), schema.EmptyTitleCount);
                // Отмена детерминированно посреди второго пакета (пакет = 20k строк):
                // токен проверяется перед следующим пакетом → недолитая коллекция удаляется.
                repo.ImportStreamedCollectionIsolated(units[0].CollectionName, null, schema,
                    CancelAfter(rows, 30_000, cts), cts.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check("Отмена: OperationCanceledException и БД без следов",
                  cancelled && CollectionCount() == before);
        }

        // 12. Повторный импорт с занятым именем → «(копия)» (мульти-файловый путь репозитория).
        {
            var f = Path.Combine(dir, "dup.csv");
            File.WriteAllText(f, "Название\nX\n", new UTF8Encoding(false));
            Import(repo, Std(f, "Дубль-имя"));
            var second = Import(repo, Std(f, "Дубль-имя"));
            Check("Имя занято → «(копия)»", CollectionName(second[0]) == "Дубль-имя (копия)");
        }

        // 13. Дружелюбные ошибки: нет файла, пустой файл, нет листа, чужое расширение, битый JSON.
        {
            Check("Ошибка: файл не найден",
                  Fails(() => Import(repo, Std(Path.Combine(dir, "нет.csv"), "X")))?.Contains("не найден") == true);

            var empty = Path.Combine(dir, "empty.csv");
            File.WriteAllText(empty, "");
            Check("Ошибка: пустой файл",
                  Fails(() => Import(repo, Std(empty, "X")))?.Contains("пустой") == true);

            var oneSheet = Path.Combine(dir, "one.xlsx");
            using (var wb = new XLWorkbook()) { wb.AddWorksheet("Лист1").Cell(1, 1).Value = "Название"; wb.SaveAs(oneSheet); }
            Check("Ошибка: лист не найден",
                  Fails(() => Import(repo, Std(oneSheet, "X", manual: true, sheetName: "Нет такого")))
                      ?.Contains("не найден") == true);

            Check("Ошибка: чужое расширение",
                  Fails(() => Import(repo, Std(Path.Combine(dir, "folder", "junk.txt"), "X")))
                      ?.Contains("не поддерживается") == true);

            var broken = Path.Combine(dir, "broken.json");
            File.WriteAllText(broken, """[{"a":1},{"b": """, new UTF8Encoding(false));
            Check("Ошибка: оборванный JSON",
                  Fails(() => Import(repo, Std(broken, "X")))?.Contains("JSON") == true);

            Check("Ошибка: битый файл не оставил коллекций-сирот",
                  AllCollectionNames().All(n => n != "X"));
        }
    }

    // ── Замер производительности ──────────────────────────────────────────────

    private static void Perf(ConstructorRepository repo, string dir, int rows)
    {
        var f = Path.Combine(dir, $"perf_{rows}.csv");
        Console.WriteLine($"Генерация CSV {rows:N0} строк × 8 столбцов…");
        using (var w = new StreamWriter(f, false, new UTF8Encoding(false), 1 << 20))
        {
            w.WriteLine("Название,Код,Цена,Количество,Дата,Категория,Статус,Комментарий");
            for (var i = 0; i < rows; i++)
                w.WriteLine($"Позиция {i},K-{i},{i % 1000}.5,{i % 50},{1 + i % 28:00}.0{1 + i % 9}.2026,Кат {i % 20},OK,строка с текстом {i}");
        }
        Console.WriteLine($"Файл: {new FileInfo(f).Length / 1024.0 / 1024.0:0.0} МБ");

        var sw = Stopwatch.StartNew();
        var units  = ImportPlanner.Plan(Std(f, $"Перф {rows}"));
        var src    = units[0].OpenSource();
        var schema = src.Analyze(null, CancellationToken.None);
        var tAnalyze = sw.Elapsed.TotalSeconds;

        sw.Restart();
        var data  = SourceHelpers.WithTitles(src.ReadRows(CancellationToken.None), schema.EmptyTitleCount);
        var colId = repo.ImportStreamedCollectionIsolated(units[0].CollectionName, null, schema, data, CancellationToken.None);
        var tWrite = sw.Elapsed.TotalSeconds;

        var (_, count, _) = Snapshot(colId);
        var peakMb = Process.GetCurrentProcess().PeakWorkingSet64 / 1024.0 / 1024.0;
        Console.WriteLine($"Analyze: {tAnalyze:0.0} с; чтение+запись: {tWrite:0.0} с " +
                          $"({rows / Math.Max(0.001, tWrite):N0} строк/с); всего {tAnalyze + tWrite:0.0} с");
        Console.WriteLine($"Записей в БД: {count:N0}; пик памяти процесса: {peakMb:0} МБ");
        Console.WriteLine($"БД: {new FileInfo(_db).Length / 1024.0 / 1024.0:0.0} МБ");
    }

    // ── Импорт как в приложении ───────────────────────────────────────────────

    private static ImportSpec Std(string path, string name, bool folder = false, bool manual = false,
        string? sheetName = null, int headerRow = 1, int dataRow = 2, string titleCol = "1",
        SheetMode sheets = SheetMode.Single,
        string? excludeSheets = null, string? excludeRows = null, string? excludeCols = null) =>
        new(path, folder, manual || sheetName is not null, sheetName,
            headerRow, dataRow, titleCol, sheets, name,
            excludeSheets, excludeRows, excludeCols);

    // Точная копия цикла SidebarTree.OpenImportExport (без UI): юниты по очереди,
    // ошибка юнита — дружелюбный текст с именем файла, готовые коллекции остаются.
    private static List<int> Import(ConstructorRepository repo, ImportSpec spec, List<int>? created = null)
    {
        created ??= [];
        var units = ImportPlanner.Plan(spec);
        foreach (var unit in units)
        {
            try
            {
                var src    = unit.OpenSource();
                var schema = src.Analyze(null, CancellationToken.None);
                var rows   = SourceHelpers.WithTitles(src.ReadRows(CancellationToken.None), schema.EmptyTitleCount);
                created.Add(repo.ImportStreamedCollectionIsolated(
                    unit.CollectionName, null, schema, rows, CancellationToken.None));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var friendly = ImportErrors.Map(ex, unit.FilePath);
                throw new ImportException($"{Path.GetFileName(unit.FilePath)}: {friendly.Message}");
            }
        }
        return created;
    }

    // ── Проверки и чтение результата ──────────────────────────────────────────

    // Дёргает Cancel после n-й строки источника — детерминированная отмена посреди импорта.
    private static IEnumerable<ImportRow> CancelAfter(IEnumerable<ImportRow> rows, int n, CancellationTokenSource cts)
    {
        var i = 0;
        foreach (var row in rows)
        {
            if (++i == n) cts.Cancel();
            yield return row;
        }
    }

    private static void Check(string name, bool ok)
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {name}"); }
        else    { _fail++; Console.WriteLine($"! FAIL  {name}"); }
    }

    private static string? Fails(Action action)
    {
        try { action(); return null; }
        catch (ImportException ex) { return ex.Message; }
    }

    private static (List<string> Fields, long Count, List<string> Titles) Snapshot(int colId)
    {
        using var ctx = new AppDbContext(_db);
        var fields = ctx.DbFields.Where(f => f.CollectionId == colId)
                                 .OrderBy(f => f.Position).Select(f => f.Name).ToList();
        var titles = ctx.DbRecords.Where(r => r.CollectionId == colId)
                                  .OrderBy(r => r.Position).ThenBy(r => r.Id)
                                  .Select(r => r.Title).ToList();
        return (fields, titles.Count, titles);
    }

    private static Dictionary<string, string> Values(int colId, int recIndex)
    {
        using var ctx = new AppDbContext(_db);
        var fields = ctx.DbFields.Where(f => f.CollectionId == colId).OrderBy(f => f.Position).ToList();
        var rec = ctx.DbRecords.Where(r => r.CollectionId == colId)
                               .OrderBy(r => r.Position).ThenBy(r => r.Id)
                               .Skip(recIndex).First();
        var vals = ctx.DbFieldValues.Where(v => v.RecordId == rec.Id).ToList();
        return fields.ToDictionary(f => f.Name, f => vals.FirstOrDefault(v => v.FieldId == f.Id)?.Value ?? "");
    }

    private static string CollectionName(int colId)
    {
        using var ctx = new AppDbContext(_db);
        return ctx.DbCollections.Find(colId)?.Name ?? "";
    }

    private static int CollectionCount()
    {
        using var ctx = new AppDbContext(_db);
        return ctx.DbCollections.Count();
    }

    private static List<string> AllCollectionNames()
    {
        using var ctx = new AppDbContext(_db);
        return ctx.DbCollections.Select(c => c.Name).ToList();
    }

    private static string? ArgStr(string[] args, string key)
    {
        var i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int? ArgInt(string[] args, string key) =>
        int.TryParse(ArgStr(args, key), out var v) ? v : null;
}
