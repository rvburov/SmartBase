using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Exporters;
using SmartBase.Core.Exporters.TableWriters;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Database;
using IE = SmartBase.Tests.Integration.ImportExport;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Функциональная матрица экспорта (перенос Tests/ExportPerf): порядок и скрытие столбцов,
/// форматы CSV/JSON/XLSX/XLSM с типами ячеек, круговые преобразования export→import,
/// singleFile, переполнение листа, отмена, карта ошибок и журнал.
/// </summary>
public sealed class ExportMatrixTests
{
    static ExportMatrixTests() => EncodingSetup.Ensure();

    // Фикстура «Товары»: значения как их хранит приложение (числа сырым вводом, даты
    // ISO-каноном, checkbox «1»/«0»/пусто); типы полей правятся напрямую в БД.
    private static int BuildGoods(TempDatabase db, TempDir dir)
    {
        var f = dir.File("goods.csv");
        File.WriteAllText(f,
            "Название;Цена;Дата;Готово;Комментарий\n" +
            "Товар 1;10.5;2026-07-06;1;обычный текст\n" +
            "Товар 2;1,25;2026-07-06T15:30;0;\"текст с ; и \"\"кавычками\"\"\"\n" +
            "Товар 3;abc;2026-01-01/2026-01-03;;Ёжик и ёлка\n", new UTF8Encoding(false));
        var id = IE.Import(db.Constructor, f, "Товары");
        SetFieldType(db.Path, id, "Цена", "number");
        SetFieldType(db.Path, id, "Дата", "date");
        SetFieldType(db.Path, id, "Готово", "checkbox");
        return id;
    }

    private static void SetFieldType(string dbPath, int colId, string name, string type)
    {
        using var ctx = new AppDbContext(dbPath);
        var fld = ctx.DbFields.First(x => x.CollectionId == colId && x.Name == name);
        fld.FieldType = type;
        ctx.SaveChanges();
    }

    [Fact]
    public void Columns_default_order_and_types()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var goods = BuildGoods(db, dir);

        var cols = db.Constructor.GetExportColumnsIsolated(goods, includeHidden: true);
        // «Описание» в списке нет: импорт его не заводит — поле появляется только у записей,
        // созданных через календарь или задачу.
        Assert.Equal(["Название", "Цена", "Дата", "Готово", "Комментарий"],
                     cols.Select(c => c.Header).ToArray());
        Assert.Equal("number", cols[1].FieldType);
        Assert.Equal("date", cols[2].FieldType);
        Assert.Equal("checkbox", cols[3].FieldType);
        Assert.Equal("text", cols[0].FieldType);
    }

    [Fact]
    public void Column_order_is_respected()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var goods = BuildGoods(db, dir);
        var keys = db.Constructor.GetExportColumnsIsolated(goods, true).Select(c => c.Key).ToList();

        // Комментарий (индекс 4) сразу после Названия.
        var reordered = new List<string> { keys[0], keys[4], keys[1], keys[2], keys[3] };
        db.Constructor.SaveColumnOrder(goods, reordered);

        Assert.Equal(["Название", "Комментарий", "Цена", "Дата", "Готово"],
                     db.Constructor.GetExportColumnsIsolated(goods, true).Select(c => c.Header).ToArray());
    }

    [Fact]
    public void Hidden_column_and_record_are_excluded_unless_included()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var goods = BuildGoods(db, dir);
        var cols = db.Constructor.GetExportColumnsIsolated(goods, true);
        var commentKey = cols[4].Key;
        var rec2 = IE.RecordIdAt(db.Path, goods, 1);

        db.Constructor.SaveColumnDisplay(goods, [(commentKey, false, 1)]);
        db.Constructor.SaveRecordDisplay(goods, [(rec2, false, 1)]);

        Assert.DoesNotContain(db.Constructor.GetExportColumnsIsolated(goods, false), c => c.Header == "Комментарий");
        Assert.Contains(db.Constructor.GetExportColumnsIsolated(goods, true), c => c.Header == "Комментарий");
        Assert.Equal(2, db.Constructor.CountRecordsIsolated(goods, excludeHidden: true));
        Assert.Equal(3, db.Constructor.CountRecordsIsolated(goods, excludeHidden: false));

        var visible = IE.ReadExportRows(db.Constructor, goods, includeHidden: false);
        Assert.Equal(2, visible.Count);
        Assert.Equal("Товар 1", visible[0][0]);
        Assert.Equal("Товар 3", visible[1][0]);
    }

    [Fact]
    public void Csv_format_and_round_trip()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var goods = BuildGoods(db, dir);

        var f = IE.Export(db.Constructor, db.Path, goods, "Товары", ExportFormat.Csv, dir.Path)[0];
        var bytes = File.ReadAllBytes(f);
        Assert.True(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF); // UTF-8 BOM
        var lines = File.ReadAllLines(f, Encoding.UTF8);
        Assert.Equal("Название;Цена;Дата;Готово;Комментарий", lines[0]);
        Assert.Equal("Товар 1;10.5;06.07.2026;1;обычный текст", lines[1]);
        Assert.Equal("Товар 2;1,25;06.07.2026 15:30;;\"текст с ; и \"\"кавычками\"\"\"", lines[2]);
        Assert.Equal("Товар 3;abc;01.01.2026 - 03.01.2026;;Ёжик и ёлка", lines[3]);

        var back = IE.Import(db.Constructor, f, "Круг CSV");
        var (fields, count, titles) = IE.Snapshot(db.Path, back);
        Assert.Equal(3, count);
        Assert.Equal(["Товар 1", "Товар 2", "Товар 3"], titles);
        Assert.Equal(["Цена", "Дата", "Готово", "Комментарий"], fields);
        Assert.Equal("текст с ; и \"кавычками\"", IE.Values(db.Path, back, 1)["Комментарий"]);
        Assert.Equal("abc", IE.Values(db.Path, back, 2)["Цена"]);
    }

    [Fact]
    public void Json_types_cyrillic_and_round_trip()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var goods = BuildGoods(db, dir);

        var f = IE.Export(db.Constructor, db.Path, goods, "Товары", ExportFormat.Json, dir.Path)[0];
        var text = File.ReadAllText(f, Encoding.UTF8);
        Assert.Contains("Ёжик и ёлка", text);
        Assert.DoesNotContain("\\u04", text);

        using var doc = JsonDocument.Parse(text);
        var arr = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, arr.Count);
        Assert.Equal(JsonValueKind.Number, arr[0].GetProperty("Цена").ValueKind);
        Assert.True(Math.Abs(arr[1].GetProperty("Цена").GetDouble() - 1.25) < 1e-9);
        Assert.Equal(JsonValueKind.String, arr[2].GetProperty("Цена").ValueKind);
        Assert.Equal(JsonValueKind.True, arr[0].GetProperty("Готово").ValueKind);
        Assert.Equal(JsonValueKind.False, arr[1].GetProperty("Готово").ValueKind);
        Assert.Equal("2026-07-06T15:30", arr[1].GetProperty("Дата").GetString());
        Assert.Equal("2026-01-01/2026-01-03", arr[2].GetProperty("Дата").GetString());

        var back = IE.Import(db.Constructor, f, "Круг JSON");
        var (_, count, titles) = IE.Snapshot(db.Path, back);
        Assert.Equal(3, count);
        Assert.Equal(["Товар 1", "Товар 2", "Товар 3"], titles);
        Assert.Equal("true", IE.Values(db.Path, back, 0)["Готово"]);
    }

    [Fact]
    public void Xlsx_cell_types_and_round_trip()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var goods = BuildGoods(db, dir);

        var f = IE.Export(db.Constructor, db.Path, goods, "Товары", ExportFormat.Xlsx, dir.Path)[0];
        using (var wb = new XLWorkbook(f))
        {
            var ws = wb.Worksheets.First();
            Assert.Equal("Товары", ws.Name);
            Assert.Equal("Название", ws.Cell(1, 1).GetString());
            Assert.Equal(XLDataType.Number, ws.Cell(2, 2).DataType);
            Assert.Equal(XLDataType.Text, ws.Cell(4, 2).DataType);   // «abc» — не число
            Assert.Equal(XLDataType.DateTime, ws.Cell(2, 3).DataType);
            Assert.Equal(new DateTime(2026, 7, 6, 15, 30, 0), ws.Cell(3, 3).GetDateTime());
            Assert.Equal(XLDataType.Text, ws.Cell(4, 3).DataType);   // диапазон дат — строка
            Assert.Equal(XLDataType.Boolean, ws.Cell(2, 4).DataType);
            Assert.True(ws.Cell(2, 4).GetBoolean());
            Assert.False(ws.Cell(3, 4).GetBoolean());
            Assert.True(ws.Cell(4, 4).IsEmpty());                    // пусто — ячейки нет
        }

        var back = IE.Import(db.Constructor, f, "Круг XLSX");
        var (fields, count, titles) = IE.Snapshot(db.Path, back);
        Assert.Equal(3, count);
        Assert.Equal(["Цена", "Дата", "Готово", "Комментарий"], fields);
        Assert.Equal(["Товар 1", "Товар 2", "Товар 3"], titles);
        Assert.Equal("06.07.2026 15:30", IE.Values(db.Path, back, 1)["Дата"]);
        Assert.Equal("1.25", IE.Values(db.Path, back, 1)["Цена"]);
    }

    [Fact]
    public void Xlsm_is_created_and_readable()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var goods = BuildGoods(db, dir);

        var f = IE.Export(db.Constructor, db.Path, goods, "Товары", ExportFormat.Xlsm, dir.Path)[0];
        Assert.Equal(".xlsm", Path.GetExtension(f));
        using var wb = new XLWorkbook(f);
        Assert.Equal("Товар 1", wb.Worksheets.First().Cell(2, 1).GetString());
    }

    [Fact]
    public void Manual_sheet_name_no_header_and_exclusions()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var goods = BuildGoods(db, dir);

        var spec = IE.ExportSpecFor(db.Path, [goods], "Ручной", ExportFormat.Xlsx, dir.Path,
            manual: true, sheetName: "Мой лист", noHeader: true,
            excludeColumns: "Комментарий, НетТакого");
        var f = IE.Export(db.Constructor, spec)[0];

        using var wb = new XLWorkbook(f);
        var ws = wb.Worksheets.First();
        Assert.Equal("Мой лист", ws.Name);
        Assert.Equal("Товар 1", ws.Cell(1, 1).GetString());          // без шапки — сразу данные
        Assert.Equal(4, ws.LastColumnUsed()!.ColumnNumber());        // Название, Цена, Дата, Готово
        Assert.Equal(XLDataType.Boolean, ws.Cell(1, 4).DataType);
    }

    [Fact]
    public void All_columns_excluded_is_friendly_error_and_no_file()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var goods = BuildGoods(db, dir);

        var spec = IE.ExportSpecFor(db.Path, [goods], "Пусто", ExportFormat.Csv, dir.Path,
            manual: true, excludeColumns: "Название, Цена, Дата, Готово, Комментарий");
        var err = IE.ExportFails(() => IE.Export(db.Constructor, spec));
        Assert.Contains("исключены", err);
        Assert.Contains("Товары", err);
        Assert.False(File.Exists(Path.Combine(dir.Path, "Пусто.csv")));
    }

    [Fact]
    public void Multi_collection_and_single_file()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var goods = BuildGoods(db, dir);

        var petsCsv = dir.File("pets.csv");
        File.WriteAllText(petsCsv, "Название;Вид\nБарсик;кот\nШарик;пёс\n", new UTF8Encoding(false));
        var pets = IE.Import(db.Constructor, petsCsv, "Звери");

        // Файл на коллекцию.
        var multi = IE.Export(db.Constructor, IE.ExportSpecFor(db.Path, [goods, pets], null, ExportFormat.Csv, dir.Path));
        Assert.Equal(2, multi.Count);
        Assert.Equal("Товары.csv", Path.GetFileName(multi[0]));
        Assert.Equal("Звери.csv", Path.GetFileName(multi[1]));
        Assert.Equal("Барсик;кот", File.ReadAllLines(multi[1], Encoding.UTF8)[1]);

        // Одна книга, лист на коллекцию.
        var one = IE.Export(db.Constructor,
            IE.ExportSpecFor(db.Path, [goods, pets], "Всё разом", ExportFormat.Xlsx, dir.Path, singleFile: true));
        Assert.Single(one);
        Assert.Equal("Всё разом.xlsx", Path.GetFileName(one[0]));
        using var wb = new XLWorkbook(one[0]);
        Assert.Equal(2, wb.Worksheets.Count);
        Assert.Equal("Товары", wb.Worksheets.First().Name);
        Assert.Equal("Звери", wb.Worksheets.Last().Name);
    }

    [Fact]
    public void Xlsx_sheet_overflow_splits_into_numbered_sheets()
    {
        using var dir = new TempDir();
        var f = dir.File("overflow.xlsx");
        var cols = new List<ExportColumn> { new("__title__", "Название", null, "text") };
        using (var w = new XlsxTableWriter(f, macroEnabled: false, maxRowsPerSheet: 5))
        {
            w.BeginTable("Данные", cols, writeHeader: true);
            for (var i = 1; i <= 12; i++) w.WriteRow([$"Строка {i}"]);
            w.EndTable();
            w.Finish();
        }

        using var wb = new XLWorkbook(f);
        Assert.Equal(["Данные", "Данные 2", "Данные 3"], wb.Worksheets.Select(s => s.Name).ToArray());
        Assert.All(wb.Worksheets, s => Assert.Equal("Название", s.Cell(1, 1).GetString()));
        Assert.Equal(12, wb.Worksheets.Sum(s => s.LastRowUsed()!.RowNumber() - 1));
    }

    [Fact]
    public void Cancel_mid_export_keeps_done_file_and_removes_partial()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();

        var sb = new StringBuilder("Название;N\n");
        for (var i = 0; i < 5000; i++) sb.Append("Стр ").Append(i).Append(';').Append(i).Append('\n');
        var bigCsv = dir.File("big.csv");
        File.WriteAllText(bigCsv, sb.ToString(), new UTF8Encoding(false));
        var big = IE.Import(db.Constructor, bigCsv, "Большая");

        var smallCsv = dir.File("small.csv");
        File.WriteAllText(smallCsv, "Название\nX\n", new UTF8Encoding(false));
        var small = IE.Import(db.Constructor, smallCsv, "Мелкая");

        using var cts = new CancellationTokenSource();
        var written = 0L;
        var cancelled = false;
        var done = new List<string>();
        try
        {
            IE.Export(db.Constructor, IE.ExportSpecFor(db.Path, [small, big], null, ExportFormat.Csv, dir.Path),
                      cts.Token, onRow: _ => { if (++written == 2101) cts.Cancel(); }, completed: done);
        }
        catch (OperationCanceledException) { cancelled = true; }

        Assert.True(cancelled);
        Assert.Single(done);                                   // «Мелкая» дописана
        Assert.True(File.Exists(done[0]));
        Assert.False(File.Exists(Path.Combine(dir.Path, "Большая.csv")));  // недописанный удалён
    }

    [Fact]
    public void Export_error_map_texts()
    {
        static string Msg(Exception ex) => ExportErrors.Map(ex, "/папка/Файл.xlsx").Message;
        Assert.Contains("Папка не найдена", Msg(new DirectoryNotFoundException()));
        Assert.Contains("Нет прав на запись", Msg(new UnauthorizedAccessException()));
        Assert.Contains("места на диске", Msg(new IOException("x", unchecked((int)0x80070070))));
        Assert.Contains("занят", Msg(new IOException("x", unchecked((int)0x80070020))));
        Assert.Contains("Файл.xlsx", Msg(new Exception("бум")));
    }

    [Fact]
    public void Export_writes_changelog_rows()
    {
        using var db = new TempDatabase();
        using var dir = new TempDir();
        var goods = BuildGoods(db, dir);
        IE.Export(db.Constructor, db.Path, goods, "Товары", ExportFormat.Csv, dir.Path);

        using var ctx = new AppDbContext(db.Path);
        var rows = ctx.ChangeLog.AsNoTracking().Where(e => e.Action == "export").ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal("collection", r.TargetType));
        Assert.Contains(rows, r => r.TargetName == "Товары" && r.Count == 3);
    }
}
