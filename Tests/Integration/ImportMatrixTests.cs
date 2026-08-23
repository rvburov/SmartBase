using System.Text;
using ClosedXML.Excel;
using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using IE = SmartBase.Tests.Integration.ImportExport;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Функциональная матрица импорта (перенос Tests/ImportPerf): CSV в разных кодировках,
/// JSON/NDJSON, XLSX (скрытые строки/столбцы/листы, ручной режим, исключения), режим папки,
/// отмена, уникализация имени и дружелюбные ошибки. Идет тем же конвейером, что и приложение.
/// </summary>
public sealed class ImportMatrixTests
{
    static ImportMatrixTests() => EncodingSetup.Ensure(); // кодовые страницы для Windows-1251

    [Fact]
    public void Csv_utf8_quotes_multiline_and_empty_title()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var f = dir.File("utf8.csv");
        File.WriteAllText(f,
            "Название,Цена,Комментарий\n" +
            "Товар 1,10.5,\"многострочный\nтекст, с запятой\"\n" +
            ",20,\"кавычки \"\"внутри\"\"\"\n" +
            "Товар 3,30,\n", new UTF8Encoding(false));

        var id = IE.Import(db.Constructor, f, "CSV");
        var (fields, count, titles) = IE.Snapshot(db.Path, id);
        Assert.Equal(3, count);
        Assert.Equal(["Цена", "Комментарий"], fields);
        Assert.Equal(["Товар 1", "Без названия", "Товар 3"], titles);
        Assert.Equal("многострочный\nтекст, с запятой", IE.Values(db.Path, id, 0)["Комментарий"]);
        Assert.Equal("кавычки \"внутри\"", IE.Values(db.Path, id, 1)["Комментарий"]);
    }

    [Fact]
    public void Csv_trailing_empty_columns_and_rows_are_trimmed()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var f = dir.File("tails.csv");
        File.WriteAllText(f, "Название,A,B,,,\nX,1,2,,,\nY,3,4,,,\n,,,,,\n,,,,,\n", new UTF8Encoding(false));

        var id = IE.Import(db.Constructor, f, "Tails");
        var (fields, count, _) = IE.Snapshot(db.Path, id);
        Assert.Equal(["A", "B"], fields);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Csv_windows1251_with_semicolon()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var f = dir.File("cp1251.csv");
        File.WriteAllBytes(f, Encoding.GetEncoding(1251).GetBytes(
            "Название;Описание\nЁжик;Съешь ещё этих мягких французских булок\n"));

        var id = IE.Import(db.Constructor, f, "CP1251");
        var (fields, count, titles) = IE.Snapshot(db.Path, id);
        Assert.Equal(1, count);
        Assert.Equal(["Описание"], fields);
        Assert.Equal("Ёжик", titles[0]);
        Assert.Equal("Съешь ещё этих мягких французских булок", IE.Values(db.Path, id, 0)["Описание"]);
    }

    [Fact]
    public void Json_array_union_keys_null_and_nested()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var f = dir.File("arr.json");
        File.WriteAllText(f,
            """[{"name":"Первый","price":1.5,"nested":{"a":1}},{"price":null,"extra":true}]""",
            new UTF8Encoding(false));

        var id = IE.Import(db.Constructor, f, "JSON");
        var (fields, count, titles) = IE.Snapshot(db.Path, id);
        Assert.Equal(2, count);
        Assert.Equal(["price", "nested", "extra"], fields);
        Assert.Equal(["Первый", "Без названия"], titles);
        Assert.Equal("""{"a":1}""", IE.Values(db.Path, id, 0)["nested"]);
        Assert.Equal("", IE.Values(db.Path, id, 1)["price"]);
        Assert.Equal("true", IE.Values(db.Path, id, 1)["extra"]);
    }

    [Fact]
    public void Ndjson_object_per_line()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var f = dir.File("nd.json");
        File.WriteAllText(f,
            """{"Название":"Один","x":1}""" + "\n" + """{"Название":"Два","x":2.25}""" + "\n",
            new UTF8Encoding(false));

        var id = IE.Import(db.Constructor, f, "ND");
        var (_, count, titles) = IE.Snapshot(db.Path, id);
        Assert.Equal(2, count);
        Assert.Equal(["Один", "Два"], titles);
        Assert.Equal("2.25", IE.Values(db.Path, id, 1)["x"]);
    }

    [Fact]
    public void Xlsx_standard_hidden_row_and_column_dates_and_numbers()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var f = dir.File("std.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Данные");
            ws.Cell(1, 1).Value = "Название"; ws.Cell(1, 2).Value = "Дата"; ws.Cell(1, 3).Value = "Число";
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

        var id = IE.Import(db.Constructor, f, "XLSX");
        var (fields, count, titles) = IE.Snapshot(db.Path, id);
        Assert.Equal(2, count);
        Assert.Equal("Скрытая строка", titles[1]);
        Assert.Equal(["Дата", "Число", "Без названия"], fields);
        Assert.Equal("06.07.2026", IE.Values(db.Path, id, 0)["Дата"]);
        Assert.Equal("06.07.2026 15:30", IE.Values(db.Path, id, 1)["Дата"]);
        Assert.Equal("0.3", IE.Values(db.Path, id, 0)["Число"]);
        Assert.Equal("1234.5", IE.Values(db.Path, id, 1)["Число"]);
        Assert.Equal("скрытый столбец", IE.Values(db.Path, id, 0)["Без названия"]);
    }

    [Fact]
    public void Xlsx_split_and_merge_sheets()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var f = dir.File("book.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Данные");
            ws.Cell(1, 1).Value = "Название"; ws.Cell(1, 2).Value = "Дата"; ws.Cell(1, 3).Value = "Число";
            ws.Cell(2, 1).Value = "Стр 1"; ws.Cell(2, 3).Value = 1;
            ws.Cell(3, 1).Value = "Стр 2"; ws.Cell(3, 3).Value = 2;
            var hidden = wb.AddWorksheet("Скрытый");
            hidden.Cell(1, 1).Value = "Название"; hidden.Cell(2, 1).Value = "С листа 2";
            hidden.Visibility = XLWorksheetVisibility.Hidden;
            wb.SaveAs(f);
        }

        var split = IE.Run(db.Constructor, IE.Std(f, "Книга", sheets: SheetMode.SplitAll));
        Assert.Equal(2, split.Count);
        Assert.Equal("Книга — Данные", IE.CollectionName(db.Path, split[0]));
        Assert.Equal("Книга — Скрытый", IE.CollectionName(db.Path, split[1]));
        Assert.Equal(["С листа 2"], IE.Snapshot(db.Path, split[1]).Titles);

        var merged = IE.Run(db.Constructor, IE.Std(f, "Слитая", sheets: SheetMode.MergeAll));
        var ms = IE.Snapshot(db.Path, merged[0]);
        Assert.Equal(3, ms.Count);
        Assert.Equal("С листа 2", ms.Titles[2]);
    }

    [Fact]
    public void Xlsx_manual_header_and_title_column()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var f = dir.File("manual.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Лист1");
            ws.Cell(1, 1).Value = "мусорная строка";
            ws.Cell(2, 1).Value = "Код"; ws.Cell(2, 2).Value = "Имя";
            ws.Cell(3, 1).Value = "K-1"; ws.Cell(3, 2).Value = "Альфа";
            ws.Cell(4, 1).Value = "K-2"; ws.Cell(4, 2).Value = "Бета";
            wb.SaveAs(f);
        }

        var id = IE.Run(db.Constructor, IE.Std(f, "Ручной", manual: true, headerRow: 2, dataRow: 3, titleCol: "B"))[0];
        var (fields, count, titles) = IE.Snapshot(db.Path, id);
        Assert.Equal(2, count);
        Assert.Equal(["Альфа", "Бета"], titles);
        Assert.Equal(["Код"], fields);
        Assert.Equal("K-2", IE.Values(db.Path, id, 1)["Код"]);
    }

    [Fact]
    public void Xlsx_exclusions_rows_columns_sheets_and_title_guard()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var f = dir.File("exclude.xlsx");
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

        var ids = IE.Run(db.Constructor, IE.Std(f, "Исключения", manual: true, sheets: SheetMode.SplitAll,
                                                excludeSheets: "Лишний", excludeRows: "3-4, 6", excludeCols: "C"));
        Assert.Single(ids);
        Assert.Equal("Исключения", IE.CollectionName(db.Path, ids[0]));
        var (fields, count, titles) = IE.Snapshot(db.Path, ids[0]);
        Assert.Equal(3, count);
        Assert.Equal(["Строка 2", "Строка 5", "Строка 7"], titles);
        Assert.Equal(["A", "C"], fields); // «C» = 3-й столбец (заголовок B) убран
        Assert.Equal("500", IE.Values(db.Path, ids[0], 1)["C"]);

        // Столбец названия исключать нельзя.
        var err = IE.ImportFails(() => IE.Run(db.Constructor, IE.Std(f, "X", manual: true, excludeCols: "A-B")));
        Assert.Contains("нельзя исключать", err);
    }

    [Fact]
    public void Folder_mode_imports_until_broken_file()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "folder");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "a1.csv"), "Название,V\nX,1\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder, "a2.json"), """[{"name":"Y"}]""", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder, "sub", "a3.csv"), "Название\nZ\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder, "junk.txt"), "не таблица");
        File.WriteAllText(Path.Combine(folder, "zbad.xlsx"), "это не Excel");

        var created = new List<int>();
        var err = IE.ImportFails(() => IE.Run(db.Constructor, IE.Std(folder, "", folder: true), created));
        Assert.Equal(3, created.Count);
        Assert.Equal("a1", IE.CollectionName(db.Path, created[0]));
        Assert.Equal("a2", IE.CollectionName(db.Path, created[1]));
        Assert.Equal("a3", IE.CollectionName(db.Path, created[2]));
        Assert.Contains("zbad.xlsx", err);
    }

    [Fact]
    public void Cancel_mid_import_leaves_no_partial_collection()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var f = dir.File("cancel.csv");
        using (var w = new StreamWriter(f, false, new UTF8Encoding(false)))
        {
            w.WriteLine("Название,A,B");
            for (var i = 0; i < 200_000; i++) w.WriteLine($"Строка {i},{i},{i * 2}");
        }

        var before = IE.CollectionCount(db.Path);
        using var cts = new CancellationTokenSource();
        var cancelled = false;
        try
        {
            var unit = ImportPlanner.Plan(IE.Std(f, "Отменяемая"))[0];
            var src = unit.OpenSource();
            var schema = src.Analyze(null, cts.Token);
            var rows = SourceHelpers.WithTitles(src.ReadRows(cts.Token), schema.EmptyTitleCount);
            db.Constructor.ImportStreamedCollectionIsolated(unit.CollectionName, null, schema,
                CancelAfter(rows, 30_000, cts), cts.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }

        Assert.True(cancelled);
        Assert.Equal(before, IE.CollectionCount(db.Path));
    }

    [Fact]
    public void Duplicate_name_gets_copy_suffix()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var f = dir.File("dup.csv");
        File.WriteAllText(f, "Название\nX\n", new UTF8Encoding(false));

        IE.Import(db.Constructor, f, "Дубль");
        var second = IE.Run(db.Constructor, IE.Std(f, "Дубль"))[0];
        Assert.Equal("Дубль (копия)", IE.CollectionName(db.Path, second));
    }

    [Fact]
    public void Friendly_errors_for_bad_input()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var repo = db.Constructor;

        Assert.Contains("не найден",
            IE.ImportFails(() => IE.Run(repo, IE.Std(dir.File("нет.csv"), "X"))));

        var empty = dir.File("empty.csv");
        File.WriteAllText(empty, "");
        Assert.Contains("пустой", IE.ImportFails(() => IE.Run(repo, IE.Std(empty, "X"))));

        var oneSheet = dir.File("one.xlsx");
        using (var wb = new XLWorkbook()) { wb.AddWorksheet("Лист1").Cell(1, 1).Value = "Название"; wb.SaveAs(oneSheet); }
        Assert.Contains("не найден",
            IE.ImportFails(() => IE.Run(repo, IE.Std(oneSheet, "X", manual: true, sheetName: "Нет такого"))));

        var junk = dir.File("junk.txt");
        File.WriteAllText(junk, "не таблица");
        Assert.Contains("не поддерживается", IE.ImportFails(() => IE.Run(repo, IE.Std(junk, "X"))));

        var broken = dir.File("broken.json");
        File.WriteAllText(broken, """[{"a":1},{"b": """, new UTF8Encoding(false));
        Assert.Contains("JSON", IE.ImportFails(() => IE.Run(repo, IE.Std(broken, "X"))));

        // Битые входы не оставили коллекций-сирот.
        Assert.DoesNotContain("X", IE.AllCollectionNames(db.Path));
    }

    // Дергает Cancel после n-й строки источника — детерминированная отмена посреди импорта.
    private static IEnumerable<ImportRow> CancelAfter(IEnumerable<ImportRow> rows, int n, CancellationTokenSource cts)
    {
        var i = 0;
        foreach (var row in rows)
        {
            if (++i == n) cts.Cancel();
            yield return row;
        }
    }
}
