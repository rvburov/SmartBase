using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using SmartBase.Core.Exporters;
using SmartBase.Core.Exporters.TableWriters;
using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.ExportPerf;

/// <summary>
/// Функциональная матрица и замер производительности экспорта таблиц.
///
/// Вызывает РОВНО те методы, что и приложение (SidebarTree.RunExport):
/// ExportPlanner.Plan → GetExportColumnsIsolated → CountRecordsIsolated →
/// ReadExportRowsIsolated → ITableWriter → LogExportIsolated. Коллекции-фикстуры
/// создаются нашим же импортом из CSV (типы полей затем правятся напрямую в БД);
/// ключевые проверки — round-trip: экспорт → импорт нашим же конвейером → сравнение.
/// Типы ячеек Excel проверяются чтением назад через ClosedXML.
///
/// Запуск (из корня репозитория):
///   dotnet run -c Release --project Tests/ExportPerf                → селф-тест матрицы
///   dotnet run -c Release --project Tests/ExportPerf -- --perf --rows 1000000
///                                                                   → замер на большой коллекции
///   ... [--dir путь]  — рабочая папка (по умолчанию %TEMP%/export-perf)
/// </summary>
internal static class Program
{
    private static int _pass, _fail;
    private static string _db = "";
    private static string _out = "";   // папка выгрузки (пересоздаётся между секциями по мере надобности)

    public static int Main(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        EncodingSetup.Ensure();

        var dir = ArgStr(args, "--dir") ?? Path.Combine(Path.GetTempPath(), "export-perf");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        _db  = Path.Combine(dir, "export_test.db");
        _out = Path.Combine(dir, "out");
        Directory.CreateDirectory(_out);

        using var m = DatabaseManager.Open(0, "export-test", _db);
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
        // Фикстура «Товары»: значения — как их хранит приложение (числа сырым вводом
        // с «.» и «,», даты ISO-каноном DateValue, checkbox «1»/«0»/пусто).
        var goods = BuildCollection(repo, dir, "Товары",
            "Название;Цена;Дата;Готово;Комментарий\n" +
            "Товар 1;10.5;2026-07-06;1;обычный текст\n" +
            "Товар 2;1,25;2026-07-06T15:30;0;\"текст с ; и \"\"кавычками\"\"\"\n" +
            "Товар 3;abc;2026-01-01/2026-01-03;;Ёжик и ёлка\n");
        SetFieldType(goods, "Цена", "number");
        SetFieldType(goods, "Дата", "date");
        SetFieldType(goods, "Готово", "checkbox");

        // 1. Колонки: дефолтный порядок таблицы, свой column_order, скрытые столбцы/записи.
        {
            var cols = repo.GetExportColumnsIsolated(goods, includeHidden: true);
            Check("Колонки: дефолтный порядок (Название … Описание)",
                  cols.Select(c => c.Header).SequenceEqual(
                      ["Название", "Цена", "Дата", "Готово", "Комментарий", "Описание"]));
            Check("Колонки: типы полей на месте",
                  cols[1].FieldType == "number" && cols[2].FieldType == "date"
                  && cols[3].FieldType == "checkbox" && cols[0].FieldType == "text");

            // Свой порядок: Комментарий сразу после Названия.
            var keys = cols.Select(c => c.Key).ToList();
            var reordered = new List<string> { keys[0], keys[4], keys[1], keys[2], keys[3], keys[5] };
            repo.SaveColumnOrder(goods, reordered);
            Check("Колонки: column_order уважается",
                  repo.GetExportColumnsIsolated(goods, true).Select(c => c.Header)
                      .SequenceEqual(["Название", "Комментарий", "Цена", "Дата", "Готово", "Описание"]));
            repo.SaveColumnOrder(goods, keys);   // вернуть

            // Скрытый столбец «Комментарий» и скрытая запись «Товар 2».
            var commentKey = cols[4].Key;
            repo.SaveColumnDisplay(goods, [(commentKey, false, 1)]);
            var rec2 = RecordIdAt(goods, 1);
            repo.SaveRecordDisplay(goods, [(rec2, false, 1)]);

            Check("Скрытое: столбец исключён без includeHidden",
                  !repo.GetExportColumnsIsolated(goods, false).Any(c => c.Header == "Комментарий"));
            Check("Скрытое: столбец включён с includeHidden",
                  repo.GetExportColumnsIsolated(goods, true).Any(c => c.Header == "Комментарий"));
            Check("Скрытое: запись исключена/включена",
                  repo.CountRecordsIsolated(goods, excludeHidden: true) == 2
                  && repo.CountRecordsIsolated(goods, excludeHidden: false) == 3);
            var visible = ReadRows(repo, goods, includeHidden: false);
            Check("Скрытое: поток строк без скрытой записи",
                  visible.Count == 2 && visible[0][0] == "Товар 1" && visible[1][0] == "Товар 3");

            repo.SaveColumnDisplay(goods, []);   // вернуть
            repo.SaveRecordDisplay(goods, []);
        }

        // 2. CSV: BOM, «;», кавычки, дата человекочитаемо, checkbox «1»/пусто; round-trip.
        {
            var files = Export(repo, Spec(goods, "Товары", ExportFormat.Csv));
            var f = files[0];
            Check("CSV: файл «Товары.csv»", Path.GetFileName(f) == "Товары.csv");
            var bytes = File.ReadAllBytes(f);
            Check("CSV: UTF-8 BOM", bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            var lines = File.ReadAllLines(f, Encoding.UTF8);
            Check("CSV: заголовок", lines[0] == "Название;Цена;Дата;Готово;Комментарий;Описание");
            Check("CSV: дата display, checkbox «1», число сырое",
                  lines[1] == "Товар 1;10.5;06.07.2026;1;обычный текст;");
            Check("CSV: кавычки и «;» в значении, checkbox «0» → пусто",
                  lines[2] == "Товар 2;1,25;06.07.2026 15:30;;\"текст с ; и \"\"кавычками\"\"\";");
            Check("CSV: диапазон дат display",
                  lines[3] == "Товар 3;abc;01.01.2026 - 03.01.2026;;Ёжик и ёлка;");

            var back = ImportBack(repo, f, "Круг CSV");
            var (fields, count, titles) = Snapshot(back);
            Check("CSV круг: записи и названия", count == 3 && titles.SequenceEqual(["Товар 1", "Товар 2", "Товар 3"]));
            Check("CSV круг: поля", fields.SequenceEqual(["Цена", "Дата", "Готово", "Комментарий", "Описание"]));
            Check("CSV круг: значения без потерь",
                  Values(back, 1)["Комментарий"] == "текст с ; и \"кавычками\""
                  && Values(back, 0)["Дата"] == "06.07.2026"
                  && Values(back, 2)["Цена"] == "abc");
        }

        // 3. JSON: типы значений, кириллица без \uXXXX; round-trip по ключу «Название».
        {
            var files = Export(repo, Spec(goods, "Товары", ExportFormat.Json));
            var f = files[0];
            var text = File.ReadAllText(f, Encoding.UTF8);
            Check("JSON: кириллица не экранирована", text.Contains("Ёжик и ёлка") && !text.Contains("\\u04"));

            using var doc = JsonDocument.Parse(text);
            var arr = doc.RootElement.EnumerateArray().ToList();
            Check("JSON: 3 объекта", arr.Count == 3);
            Check("JSON: number → число (и «1,25» → 1.25)",
                  arr[0].GetProperty("Цена").ValueKind == JsonValueKind.Number
                  && Math.Abs(arr[1].GetProperty("Цена").GetDouble() - 1.25) < 1e-9);
            Check("JSON: неразобранное число → строка", arr[2].GetProperty("Цена").ValueKind == JsonValueKind.String);
            Check("JSON: checkbox → true/false",
                  arr[0].GetProperty("Готово").ValueKind == JsonValueKind.True
                  && arr[1].GetProperty("Готово").ValueKind == JsonValueKind.False
                  && arr[2].GetProperty("Готово").ValueKind == JsonValueKind.False);
            Check("JSON: дата ISO-каноном",
                  arr[1].GetProperty("Дата").GetString() == "2026-07-06T15:30"
                  && arr[2].GetProperty("Дата").GetString() == "2026-01-01/2026-01-03");

            var back = ImportBack(repo, f, "Круг JSON");
            var (_, count, titles) = Snapshot(back);
            Check("JSON круг: названия по ключу «Название»",
                  count == 3 && titles.SequenceEqual(["Товар 1", "Товар 2", "Товар 3"]));
            Check("JSON круг: bool и число текстом", Values(back, 0)["Готово"] == "true"
                  && Values(back, 0)["Цена"] == "10.5");
        }

        // 4. XLSX: типы ячеек (дата/число/булево/строка), пустое — ячейки нет; round-trip.
        {
            var files = Export(repo, Spec(goods, "Товары", ExportFormat.Xlsx));
            var f = files[0];
            using (var wb = new XLWorkbook(f))
            {
                var ws = wb.Worksheets.First();
                Check("XLSX: лист назван по коллекции", ws.Name == "Товары");
                Check("XLSX: заголовок на месте", ws.Cell(1, 1).GetString() == "Название"
                      && ws.Cell(1, 6).GetString() == "Описание");
                Check("XLSX: number → числовая ячейка",
                      ws.Cell(2, 2).DataType == XLDataType.Number && ws.Cell(3, 2).DataType == XLDataType.Number);
                Check("XLSX: неразобранное число → текст", ws.Cell(4, 2).DataType == XLDataType.Text);
                Check("XLSX: точечные даты → датные ячейки",
                      ws.Cell(2, 3).DataType == XLDataType.DateTime
                      && ws.Cell(3, 3).GetDateTime() == new DateTime(2026, 7, 6, 15, 30, 0));
                Check("XLSX: диапазон дат → строка", ws.Cell(4, 3).DataType == XLDataType.Text
                      && ws.Cell(4, 3).GetString() == "01.01.2026 - 03.01.2026");
                Check("XLSX: checkbox → булева ячейка",
                      ws.Cell(2, 4).DataType == XLDataType.Boolean && ws.Cell(2, 4).GetBoolean()
                      && ws.Cell(3, 4).DataType == XLDataType.Boolean && !ws.Cell(3, 4).GetBoolean());
                Check("XLSX: пустое значение — ячейки нет", ws.Cell(4, 4).IsEmpty());
            }

            var back = ImportBack(repo, f, "Круг XLSX");
            var (fields, count, titles) = Snapshot(back);
            Check("XLSX круг: записи/поля", count == 3
                  && fields.SequenceEqual(["Цена", "Дата", "Готово", "Комментарий", "Описание"])
                  && titles.SequenceEqual(["Товар 1", "Товар 2", "Товар 3"]));
            Check("XLSX круг: дата и число через реальные типы ячеек",
                  Values(back, 0)["Дата"] == "06.07.2026"
                  && Values(back, 1)["Дата"] == "06.07.2026 15:30"
                  && Values(back, 1)["Цена"] == "1.25");
        }

        // 5. XLSM: тот же контейнер, читается назад.
        {
            var files = Export(repo, Spec(goods, "Товары", ExportFormat.Xlsm));
            using var wb = new XLWorkbook(files[0]);
            Check("XLSM: создан и читается", Path.GetExtension(files[0]) == ".xlsm"
                  && wb.Worksheets.First().Cell(2, 1).GetString() == "Товар 1");
        }

        // 6. Ручные параметры: своё имя листа, «Без строки заголовков», исключение столбцов
        //    (в т.ч. несуществующего — лениво).
        {
            var files = Export(repo, Spec(goods, "Ручной", ExportFormat.Xlsx,
                manual: true, sheetName: "Мой лист", noHeader: true,
                excludeColumns: "Комментарий, НетТакого, Описание"));
            using var wb = new XLWorkbook(files[0]);
            var ws = wb.Worksheets.First();
            Check("Ручной: имя листа своё", ws.Name == "Мой лист");
            Check("Ручной: без шапки — первая строка данные", ws.Cell(1, 1).GetString() == "Товар 1");
            // Осталось 4 колонки: Название, Цена, Дата, Готово (без Комментария и Описания).
            Check("Ручной: столбцы исключены (несуществующий — молча)",
                  ws.LastColumnUsed()!.ColumnNumber() == 4
                  && ws.Cell(1, 4).DataType == XLDataType.Boolean);
        }

        // 7. Все столбцы исключены → дружелюбная ошибка, файла не остаётся.
        {
            var err = Fails(() => Export(repo, Spec(goods, "Пусто", ExportFormat.Csv,
                manual: true, excludeColumns: "Название, Цена, Дата, Готово, Комментарий, Описание")));
            Check("Все столбцы исключены → ошибка с именем коллекции",
                  err is not null && err.Contains("исключены") && err.Contains("Товары"));
            Check("Все столбцы исключены → файл удалён", !File.Exists(Path.Combine(_out, "Пусто.csv")));
        }

        // 8. Мульти-коллекция: файл на коллекцию, имена из коллекций, занятое имя → « (2)».
        {
            var pets = BuildCollection(repo, dir, "Звери", "Название;Вид\nБарсик;кот\nШарик;пёс\n");
            File.WriteAllText(Path.Combine(_out, "Звери.csv"), "занято");   // конфликт на диске

            var files = Export(repo, Spec([goods, pets], null, ExportFormat.Csv));
            // «Товары.csv» уже лежит в папке после секции 2 → « (2)»; «Звери.csv» занят файлом выше.
            Check("Мульти: имена файлов из коллекций и уникализация",
                  Path.GetFileName(files[0]) == "Товары (2).csv" && Path.GetFileName(files[1]) == "Звери (2).csv");
            Check("Мульти: содержимое второго файла",
                  File.ReadAllLines(files[1], Encoding.UTF8)[1] == "Барсик;кот;");

            // 9. SingleFile: одна книга, лист на коллекцию; «Название файла» действует.
            var one = Export(repo, Spec([goods, pets], "Всё разом", ExportFormat.Xlsx, singleFile: true));
            using var wb = new XLWorkbook(one[0]);
            Check("SingleFile: один файл с именем из поля",
                  one.Count == 1 && Path.GetFileName(one[0]) == "Всё разом.xlsx");
            Check("SingleFile: лист на коллекцию",
                  wb.Worksheets.Count == 2
                  && wb.Worksheets.First().Name == "Товары" && wb.Worksheets.Last().Name == "Звери");
            Check("SingleFile: данные обоих листов",
                  wb.Worksheet("Товары").Cell(2, 1).GetString() == "Товар 1"
                  && wb.Worksheet("Звери").Cell(3, 1).GetString() == "Шарик");
        }

        // 10. Переполнение листа: инъекция лимита 5 → «Данные», «Данные 2», «Данные 3»,
        //     шапка повторяется, все строки на месте.
        {
            var f = Path.Combine(_out, "overflow.xlsx");
            var cols = new List<ExportColumn> { new("__title__", "Название", null, "text") };
            using (var w = new XlsxTableWriter(f, macroEnabled: false, maxRowsPerSheet: 5))
            {
                w.BeginTable("Данные", cols, writeHeader: true);
                for (var i = 1; i <= 12; i++) w.WriteRow([$"Строка {i}"]);
                w.EndTable();
                w.Finish();
            }
            using var wb = new XLWorkbook(f);
            Check("Переполнение: три листа с номерами",
                  wb.Worksheets.Select(s => s.Name).SequenceEqual(["Данные", "Данные 2", "Данные 3"]));
            Check("Переполнение: шапка на каждом листе",
                  wb.Worksheets.All(s => s.Cell(1, 1).GetString() == "Название"));
            Check("Переполнение: строки без потерь и по порядку",
                  wb.Worksheet("Данные").Cell(2, 1).GetString() == "Строка 1"
                  && wb.Worksheet("Данные 2").Cell(2, 1).GetString() == "Строка 5"
                  && wb.Worksheet("Данные 3").Cell(2, 1).GetString() == "Строка 9"
                  && wb.Worksheets.Sum(s => s.LastRowUsed()!.RowNumber() - 1) == 12);
        }

        // 11. Санитайз и утилиты планировщика.
        {
            Check("Санитайз файла: спецсимволы и резервные имена",
                  ExportPlanner.SanitizeFileName("А/Б:В*") == "А_Б_В_"
                  && ExportPlanner.SanitizeFileName("  точки... ") == "точки"
                  && ExportPlanner.SanitizeFileName("CON") == "_CON"
                  && ExportPlanner.SanitizeFileName("???") == "___"
                  && ExportPlanner.SanitizeFileName("") == "Коллекция");
            Check("Санитайз листа: символы, длина 31, пусто",
                  ExportPlanner.SanitizeSheetName("Ито[го]: *?/\\") == "Ито_го__ ____"
                  && ExportPlanner.SanitizeSheetName(new string('ы', 40)).Length == 31
                  && ExportPlanner.SanitizeSheetName("''") == "Лист1");
            Check("Исключения столбцов: запятая/точка с запятой, регистр",
                  ExportPlanner.ParseColumnExclusions("Цена, комментарий; X").Contains("Комментарий")
                  && ExportPlanner.ParseColumnExclusions(null).Count == 0);

            // Планировщик: листы одноимённых коллекций в одном файле уникализируются.
            var same = new List<ExportCollectionRef> { new(0, 1, "Продажи", "БД"), new(0, 2, "Продажи", "БД") };
            var plan = ExportPlanner.Plan(new ExportSpec(_out, ExportFormat.Xlsx, same,
                null, false, null, "", false, false, SingleFile: true));
            Check("Планировщик: дубликат имени листа → « (2)»",
                  plan[0].Tables[0].TableName == "Продажи" && plan[0].Tables[1].TableName == "Продажи (2)");
        }

        // 12. Отмена посреди второго юнита: первый файл остаётся, недописанный удалён.
        {
            var sb = new StringBuilder("Название;N\n");
            for (var i = 0; i < 5000; i++) sb.Append("Стр ").Append(i).Append(';').Append(i).Append('\n');
            var big = BuildCollection(repo, dir, "Большая", sb.ToString());

            var pets2  = BuildCollection(repo, dir, "Мелкая", "Название\nX\n");
            using var cts = new CancellationTokenSource();
            var written = 0L;
            var cancelled = false;
            List<string> done = [];
            try
            {
                // Отмена после 2100 строк ВТОРОЙ коллекции: читатель проверяет токен на границе
                // страниц (2000 строк) → OCE детерминированно перед третьей страницей.
                Export(repo, Spec([pets2, big], null, ExportFormat.Csv), cts.Token,
                       onRow: _ => { if (++written == 2101) cts.Cancel(); }, completed: done);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check("Отмена: OCE, готовый файл жив, недописанный удалён",
                  cancelled && done.Count == 1
                  && File.Exists(done[0])
                  && !File.Exists(Path.Combine(_out, "Большая.csv")));
        }

        // 13. Карта ошибок записи: тексты по типам исключений.
        {
            static string Msg(Exception ex) => ExportErrors.Map(ex, @"C:\папка\Файл.xlsx").Message;
            Check("Ошибки: папка исчезла", Msg(new DirectoryNotFoundException()).Contains("Папка не найдена"));
            Check("Ошибки: нет прав на запись", Msg(new UnauthorizedAccessException()).Contains("Нет прав на запись"));
            Check("Ошибки: диск полон", Msg(new IOException("x", unchecked((int)0x80070070))).Contains("места на диске"));
            Check("Ошибки: файл занят", Msg(new IOException("x", unchecked((int)0x80070020))).Contains("занят"));
            Check("Ошибки: длинный путь", Msg(new PathTooLongException()).Contains("Слишком длинный"));
            Check("Ошибки: кривое имя", Msg(new NotSupportedException()).Contains("Недопустимое имя"));
            Check("Ошибки: прочее с именем файла", Msg(new Exception("бум")).Contains("«Файл.xlsx»"));

            var err = Fails(() => Export(repo, Spec(goods, "X", ExportFormat.Csv,
                folder: Path.Combine(dir, "нет такой папки"))));
            Check("Ошибки: несуществующая папка при записи", err is not null && err.Contains("Папка не найдена"));
        }

        // 14. Журнал: строка «export» с числом записей на каждую выгруженную коллекцию.
        {
            using var ctx = new AppDbContext(_db);
            var rows = ctx.ChangeLog.Where(e => e.Action == "export").ToList();
            Check("Журнал: строки export пишутся (с Count)",
                  rows.Count > 0 && rows.All(r => r.TargetType == "collection")
                  && rows.Any(r => r.TargetName == "Товары" && r.Count == 3));
        }
    }

    // ── Замер производительности ──────────────────────────────────────────────

    private static void Perf(ConstructorRepository repo, string dir, int rows)
    {
        Console.WriteLine($"Генерация коллекции {rows:N0} строк × 8 столбцов (через импорт CSV)…");
        var f = Path.Combine(dir, $"perf_{rows}.csv");
        using (var w = new StreamWriter(f, false, new UTF8Encoding(false), 1 << 20))
        {
            w.WriteLine("Название,Код,Цена,Количество,Дата,Категория,Статус,Комментарий");
            for (var i = 0; i < rows; i++)
                w.WriteLine($"Позиция {i},K-{i},{i % 1000}.5,{i % 50},2026-0{1 + i % 9:0}-{1 + i % 28:00},Кат {i % 20},OK,строка с текстом {i}");
        }
        var colId = ImportBack(repo, f, $"Перф {rows}");
        SetFieldType(colId, "Цена", "number");
        SetFieldType(colId, "Дата", "date");
        Console.WriteLine($"Коллекция готова, БД: {new FileInfo(_db).Length / 1024.0 / 1024.0:0.0} МБ");
        Console.WriteLine();

        foreach (var fmt in new[] { ExportFormat.Csv, ExportFormat.Json, ExportFormat.Xlsx, ExportFormat.Xlsm })
        {
            var sw = Stopwatch.StartNew();
            var files = Export(repo, Spec(colId, $"перф-{fmt}", fmt));
            sw.Stop();
            var size = new FileInfo(files[0]).Length / 1024.0 / 1024.0;
            Console.WriteLine($"{fmt,-5}: {sw.Elapsed.TotalSeconds,7:0.0} с " +
                              $"({rows / Math.Max(0.001, sw.Elapsed.TotalSeconds),10:N0} строк/с), файл {size,8:0.0} МБ");
        }
        var peakMb = Process.GetCurrentProcess().PeakWorkingSet64 / 1024.0 / 1024.0;
        Console.WriteLine();
        Console.WriteLine($"Пик памяти процесса: {peakMb:0} МБ");
    }

    // ── Экспорт как в приложении (зеркало SidebarTree.RunExport без UI) ───────

    private static ExportSpec Spec(int colId, string? fileName, ExportFormat fmt,
        bool manual = false, string? sheetName = null, string excludeColumns = "",
        bool noHeader = false, bool includeHidden = false, bool singleFile = false,
        string? folder = null) =>
        Spec([colId], fileName, fmt, manual, sheetName, excludeColumns, noHeader,
             includeHidden, singleFile, folder);

    private static ExportSpec Spec(IReadOnlyList<int> colIds, string? fileName, ExportFormat fmt,
        bool manual = false, string? sheetName = null, string excludeColumns = "",
        bool noHeader = false, bool includeHidden = false, bool singleFile = false,
        string? folder = null)
    {
        var refs = colIds.Select(id => new ExportCollectionRef(0, id, CollectionName(id), "БД")).ToList();
        return new ExportSpec(folder ?? _out, fmt, refs, fileName,
            manual || sheetName is not null, sheetName, excludeColumns, noHeader, includeHidden, singleFile);
    }

    // Точная копия цикла SidebarTree.RunExport (без UI): юниты по очереди, ошибка юнита —
    // дружелюбный текст с именем файла и удаление недописанного, готовые файлы остаются.
    private static List<string> Export(ConstructorRepository repo, ExportSpec spec,
        CancellationToken ct = default, Action<long>? onRow = null, List<string>? completed = null)
    {
        completed ??= [];
        var units = ExportPlanner.Plan(spec);
        var excl  = ExportPlanner.ParseColumnExclusions(spec.ExcludeColumns);
        foreach (var unit in units)
        {
            try
            {
                var fileCreated = false;
                try
                {
                    using var writer = unit.OpenWriter();
                    fileCreated = true;
                    foreach (var tbl in unit.Tables)
                    {
                        var cols = repo.GetExportColumnsIsolated(tbl.Collection.CollectionId, spec.IncludeHidden)
                                       .Where(c => !excl.Contains(c.Header)).ToList();
                        if (cols.Count == 0)
                            throw new ExportException(
                                $"Все столбцы коллекции «{tbl.Collection.CollectionName}» исключены — нет данных для экспорта.");
                        var total = repo.CountRecordsIsolated(tbl.Collection.CollectionId, excludeHidden: !spec.IncludeHidden);
                        writer.BeginTable(tbl.TableName, cols, !spec.NoHeaderRow);
                        foreach (var row in repo.ReadExportRowsIsolated(
                                     tbl.Collection.CollectionId, cols, !spec.IncludeHidden, ct))
                        {
                            writer.WriteRow(row);
                            onRow?.Invoke(1);
                        }
                        writer.EndTable();
                        repo.LogExportIsolated(tbl.Collection.CollectionName, total);
                    }
                    writer.Finish();
                    completed.Add(unit.FilePath);
                }
                catch
                {
                    // Писатель уже disposed (using) — недописанный файл можно удалить.
                    // Только свой: если упало само создание (файл появился извне между
                    // Plan и CreateNew) — чужое не трогаем.
                    if (fileCreated)
                        try { File.Delete(unit.FilePath); } catch { /* best-effort */ }
                    throw;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var friendly = ExportErrors.Map(ex, unit.FilePath);
                throw new ExportException($"{Path.GetFileName(unit.FilePath)}: {friendly.Message}");
            }
        }
        return completed;
    }

    // Строки экспорта без записи в файл — для проверок потока.
    private static List<string[]> ReadRows(ConstructorRepository repo, int colId, bool includeHidden)
    {
        var cols = repo.GetExportColumnsIsolated(colId, includeHidden);
        return repo.ReadExportRowsIsolated(colId, cols, !includeHidden, CancellationToken.None).ToList();
    }

    // ── Фикстуры ──────────────────────────────────────────────────────────────

    // Коллекция нашим же импортом из CSV (типы полей правятся потом SetFieldType).
    private static int BuildCollection(ConstructorRepository repo, string dir, string name, string csv)
    {
        var f = Path.Combine(dir, $"fixture_{name}.csv");
        File.WriteAllText(f, csv, new UTF8Encoding(false));
        return ImportBack(repo, f, name);
    }

    // Импорт файла стандартным конвейером приложения (и для фикстур, и для round-trip).
    private static int ImportBack(ConstructorRepository repo, string path, string name)
    {
        var spec   = new ImportSpec(path, false, false, null, 1, 2, "1", SheetMode.Single, name);
        var unit   = ImportPlanner.Plan(spec)[0];
        var src    = unit.OpenSource();
        var schema = src.Analyze(null, CancellationToken.None);
        var rows   = SourceHelpers.WithTitles(src.ReadRows(CancellationToken.None), schema.EmptyTitleCount);
        return repo.ImportStreamedCollectionIsolated(unit.CollectionName, null, schema, rows, CancellationToken.None);
    }

    private static void SetFieldType(int colId, string fieldName, string type)
    {
        using var ctx = new AppDbContext(_db);
        var f = ctx.DbFields.First(x => x.CollectionId == colId && x.Name == fieldName);
        f.FieldType = type;
        ctx.SaveChanges();
    }

    // ── Проверки и чтение результата ──────────────────────────────────────────

    private static void Check(string name, bool ok)
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {name}"); }
        else    { _fail++; Console.WriteLine($"! FAIL  {name}"); }
    }

    private static string? Fails(Action action)
    {
        try { action(); return null; }
        catch (ExportException ex) { return ex.Message; }
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

    private static int RecordIdAt(int colId, int recIndex)
    {
        using var ctx = new AppDbContext(_db);
        return ctx.DbRecords.Where(r => r.CollectionId == colId)
                            .OrderBy(r => r.Position).ThenBy(r => r.Id)
                            .Skip(recIndex).First().Id;
    }

    private static string CollectionName(int colId)
    {
        using var ctx = new AppDbContext(_db);
        return ctx.DbCollections.Find(colId)?.Name ?? "";
    }

    private static string? ArgStr(string[] args, string key)
    {
        var i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int? ArgInt(string[] args, string key) =>
        int.TryParse(ArgStr(args, key), out var v) ? v : null;
}
