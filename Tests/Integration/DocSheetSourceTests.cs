using System.Globalization;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SmartBase.Core.Documents;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Разбор книги Excel в СЕТКУ (<see cref="DocSheetSource"/>) — то, из чего панель чтения рисует
/// настоящую таблицу.
///
/// Проверки здесь про то, чего нет у разбора для поиска: место ячейки, объединения и ширины
/// колонок. Режим «Текст» своего разбора книги не имеет — он берет строки из этой же сетки,
/// поэтому все, что сказано здесь о листах и строках, верно и для него.
///
/// Отдельная тема — даты. Excel хранит дату числом, а как ее показывать, записано в стиле
/// ячейки; показать «45356» вместо «05.03.2024» значит показать неправду. Тихая это ошибка
/// ровно потому, что число выглядит настоящим значением ячейки.
/// </summary>
public sealed class DocSheetSourceTests : IDisposable
{
    private readonly string _dir;

    public DocSheetSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb-docsheet-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* занят — уберет система */ }
    }

    /// <summary>
    /// Книга со всем, что нас интересует: объединенная шапка, общая строка, число, дата со
    /// своим форматом, заданная ширина колонки и второй лист.
    /// </summary>
    private string WriteBook(string name = "book.xlsx")
    {
        var path = Path.Combine(_dir, name);
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);

        var book      = doc.AddWorkbookPart();
        book.Workbook = new Workbook();

        // Общие строки: так Excel хранит повторяющийся текст.
        var sharedPart = book.AddNewPart<SharedStringTablePart>();
        sharedPart.SharedStringTable = new SharedStringTable(
            new SharedStringItem(new Text("Смета работ")),
            new SharedStringItem(new Text("Труба стальная")));
        sharedPart.SharedStringTable.Save();

        // Стили: формат 165 — дата; ячейка со StyleIndex=1 показывается датой.
        var stylesPart = book.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = new Stylesheet(
            new NumberingFormats(new NumberingFormat { NumberFormatId = 165, FormatCode = "dd.mm.yyyy" }),
            new Fonts(new Font()), new Fills(new Fill()), new Borders(new Border()),
            new CellStyleFormats(new CellFormat()),
            new CellFormats(
                new CellFormat(),
                new CellFormat { NumberFormatId = 165, ApplyNumberFormat = true }));
        stylesPart.Stylesheet.Save();

        var first = book.AddNewPart<WorksheetPart>();
        first.Worksheet = new Worksheet(
            new Columns(new Column { Min = 1, Max = 1, Width = 30, CustomWidth = true }),
            new SheetData(
                // Шапка объединена на три колонки.
                new Row(new Cell { CellReference = "A1", DataType = CellValues.SharedString, CellValue = new CellValue("0") })
                    { RowIndex = 1 },
                new Row(
                    new Cell { CellReference = "A2", DataType = CellValues.String, CellValue = new CellValue("Наименование") },
                    new Cell { CellReference = "B2", DataType = CellValues.String, CellValue = new CellValue("Количество") },
                    new Cell { CellReference = "C2", DataType = CellValues.String, CellValue = new CellValue("Дата") })
                    { RowIndex = 2 },
                new Row(
                    new Cell { CellReference = "A3", DataType = CellValues.SharedString, CellValue = new CellValue("1") },
                    new Cell { CellReference = "B3", CellValue = new CellValue("12.5") },
                    new Cell { CellReference = "C3", StyleIndex = 1,
                               CellValue = new CellValue(new DateTime(2024, 3, 5).ToOADate()
                                                         .ToString(CultureInfo.InvariantCulture)) })
                    { RowIndex = 3 }),
            new MergeCells(new MergeCell { Reference = "A1:C1" }));
        first.Worksheet.Save();

        var second = book.AddNewPart<WorksheetPart>();
        second.Worksheet = new Worksheet(new SheetData(
            new Row(new Cell { CellReference = "A1", DataType = CellValues.String, CellValue = new CellValue("Примечания") })
                { RowIndex = 1 }));
        second.Worksheet.Save();

        book.Workbook.AppendChild(new Sheets(
            new Sheet { Id = book.GetIdOfPart(first),  SheetId = 1, Name = "Смета" },
            new Sheet { Id = book.GetIdOfPart(second), SheetId = 2, Name = "Примечания" }));
        book.Workbook.Save();
        return path;
    }

    /// <summary>
    /// Книга про РАССТАНОВКУ: своя высота строки и высота листа по умолчанию, пустая строка
    /// между данными, скрытая строка, скрытая колонка, перенос текста и выключка.
    /// </summary>
    private string WriteGeometryBook()
    {
        var path = Path.Combine(_dir, "geometry.xlsx");
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);

        var book      = doc.AddWorkbookPart();
        book.Workbook = new Workbook();

        var stylesPart = book.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = new Stylesheet(
            new Fonts(new Font()), new Fills(new Fill()), new Borders(new Border()),
            new CellStyleFormats(new CellFormat()),
            new CellFormats(
                new CellFormat(),
                new CellFormat(new Alignment
                {
                    WrapText   = true,
                    Horizontal = HorizontalAlignmentValues.Center,
                    Vertical   = VerticalAlignmentValues.Center,
                })
                { ApplyAlignment = true }));
        stylesPart.Stylesheet.Save();

        var part = book.AddNewPart<WorksheetPart>();
        part.Worksheet = new Worksheet(
            new SheetFormatProperties { DefaultRowHeight = 20, DefaultColumnWidth = 12 },
            new Columns(
                new Column { Min = 1, Max = 1, Width = 30, CustomWidth = true },
                new Column { Min = 2, Max = 2, Width = 9, Hidden = true }),
            new SheetData(
                new Row(new Cell { CellReference = "A1", StyleIndex = 1,
                                   DataType = CellValues.String, CellValue = new CellValue("Шапка") })
                    { RowIndex = 1, Height = 40, CustomHeight = true },
                // Строки 2 в книге нет вовсе — это пустая строка между данными.
                new Row(new Cell { CellReference = "A3", DataType = CellValues.String,
                                   CellValue = new CellValue("Значение") })
                    { RowIndex = 3 },
                new Row(new Cell { CellReference = "A4", DataType = CellValues.String,
                                   CellValue = new CellValue("Скрытая") })
                    { RowIndex = 4, Hidden = true },
                new Row(new Cell { CellReference = "A5", DataType = CellValues.String,
                                   CellValue = new CellValue("Хвост") })
                    { RowIndex = 5 }));
        part.Worksheet.Save();

        book.Workbook.AppendChild(new Sheets(
            new Sheet { Id = book.GetIdOfPart(part), SheetId = 1, Name = "Лист" }));
        book.Workbook.Save();
        return path;
    }

    /// <summary>
    /// Книга про ОФОРМЛЕНИЕ: заливка, шрифт с цветом и размером, рамки, своя палитра книги и
    /// встроенный формат числа.
    /// </summary>
    private string WritePaintBook()
    {
        var path = Path.Combine(_dir, "paint.xlsx");
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);

        var book      = doc.AddWorkbookPart();
        book.Workbook = new Workbook();

        // Своя палитра книги: под номером 10 в ней серый, а в стандартной — ярко-красный.
        var own = new IndexedColors();
        foreach (var rgb in new[] { "FF000000", "FFFFFFFF", "FFFF0000", "FF00FF00", "FF0000FF",
                                    "FFFFFF00", "FFFF00FF", "FF00FFFF", "FF000000", "FFFFFFFF",
                                    "FFAAAAAA" })
            own.Append(new RgbColor { Rgb = rgb });

        var stylesPart = book.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = new Stylesheet(
            new Fonts(
                new Font(new FontSize { Val = 11 }),
                new Font(new Bold(), new FontSize { Val = 14 }, new Color { Rgb = "FF0000FF" })),
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(new ForegroundColor { Rgb = "FFEEEEEE" })
                    { PatternType = PatternValues.Solid })),
            new Borders(
                new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder()),
                new Border(
                    new LeftBorder(new Color { Indexed = 10 }) { Style = BorderStyleValues.Thin },
                    new RightBorder(),
                    new TopBorder(),
                    new BottomBorder(new Color { Rgb = "FF333333" }) { Style = BorderStyleValues.Medium })),
            new CellStyleFormats(new CellFormat()),
            new CellFormats(
                new CellFormat(),
                new CellFormat { FontId = 1, FillId = 2, BorderId = 1 },
                new CellFormat { NumberFormatId = 4, ApplyNumberFormat = true }),
            new DocumentFormat.OpenXml.Spreadsheet.Colors(own));
        stylesPart.Stylesheet.Save();

        var part = book.AddNewPart<WorksheetPart>();
        part.Worksheet = new Worksheet(new SheetData(
            new Row(
                new Cell { CellReference = "A1", StyleIndex = 1,
                           DataType = CellValues.String, CellValue = new CellValue("Шапка") },
                new Cell { CellReference = "B1", StyleIndex = 2, CellValue = new CellValue("30772.48") },
                new Cell { CellReference = "C1",
                           DataType = CellValues.String, CellValue = new CellValue("Простая") })
                { RowIndex = 1 }));
        part.Worksheet.Save();

        book.Workbook.AppendChild(new Sheets(
            new Sheet { Id = book.GetIdOfPart(part), SheetId = 1, Name = "Лист" }));
        book.Workbook.Save();
        return path;
    }

    /// <summary>
    /// Книга, у которой порядок частей пакета и порядок корешков РАЗНЫЙ, плюс два скрытых
    /// листа. Так выглядит книга, где лист перетащили мышью: Excel переставляет корешок, а часть
    /// пакета остается на прежнем месте.
    /// </summary>
    private string WriteShuffledBook()
    {
        var path = Path.Combine(_dir, "shuffled.xlsx");
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);

        var book      = doc.AddWorkbookPart();
        book.Workbook = new Workbook();

        WorksheetPart Part(string text)
        {
            var part = book.AddNewPart<WorksheetPart>();
            part.Worksheet = new Worksheet(new SheetData(TextRow(1, ("A1", text))));
            part.Worksheet.Save();
            return part;
        }

        var estimate = Part("смета");
        var totals   = Part("итоги");
        var lookup   = Part("справочник");
        var deep     = Part("расчет");

        book.Workbook.AppendChild(new Sheets(
            new Sheet { Id = book.GetIdOfPart(totals),   SheetId = 2, Name = "Итоги" },
            new Sheet { Id = book.GetIdOfPart(lookup),   SheetId = 3, Name = "Справочник",
                        State = SheetStateValues.Hidden },
            new Sheet { Id = book.GetIdOfPart(estimate), SheetId = 1, Name = "Смета" },
            new Sheet { Id = book.GetIdOfPart(deep),     SheetId = 4, Name = "Расчет",
                        State = SheetStateValues.VeryHidden }));
        book.Workbook.Save();
        return path;
    }

    // Книга с одним листом из готовых строк и, по желанию, с записанным размером листа.
    private string WriteRows(string name, string? dimension, params Row[] rows)
    {
        var path = Path.Combine(_dir, name);
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);

        var book      = doc.AddWorkbookPart();
        book.Workbook = new Workbook();

        var part  = book.AddNewPart<WorksheetPart>();
        var sheet = new Worksheet();
        if (dimension is not null) sheet.Append(new SheetDimension { Reference = dimension });
        sheet.Append(new SheetData(rows));
        part.Worksheet = sheet;
        part.Worksheet.Save();

        book.Workbook.AppendChild(new Sheets(
            new Sheet { Id = book.GetIdOfPart(part), SheetId = 1, Name = "Лист" }));
        book.Workbook.Save();
        return path;
    }

    // Строка с текстовыми ячейками; пустая ссылка — ячейка без ссылки.
    private static Row TextRow(uint index, params (string Reference, string Text)[] cells)
    {
        var row = new Row { RowIndex = index };
        foreach (var (reference, text) in cells)
        {
            var cell = new Cell { DataType = CellValues.String, CellValue = new CellValue(text) };
            if (reference.Length > 0) cell.CellReference = reference;
            row.Append(cell);
        }
        return row;
    }

    private static Row[] NumberedRows(int count) =>
        [.. Enumerable.Range(1, count).Select(i => TextRow((uint)i, ($"A{i}", $"строка {i}")))];

    // ── Листы и ячейки ────────────────────────────────────────────────────────

    /// <summary>
    /// Листы идут в порядке КОРЕШКОВ, а не частей пакета: перетащенный мышью лист Excel
    /// переставляет только корешком, и по частям пакета книга читалась бы в чужом порядке.
    /// </summary>
    [Fact]
    public void Sheets_follow_the_tab_order_not_the_package_order()
    {
        Assert.Equal(["Итоги", "Смета"], DocSheetSource.Open(WriteShuffledBook()).Select(s => s.Name));
    }

    /// <summary>
    /// Скрытый лист не показывается — ни обычный скрытый, ни «очень скрытый», которого не вернуть
    /// даже из меню Excel: их не показывает и сам Excel. Но они считаются — панель о них скажет.
    /// </summary>
    [Fact]
    public void Hidden_sheets_are_skipped_and_counted()
    {
        var book = DocSheetSource.OpenBook(WriteShuffledBook());

        Assert.DoesNotContain(book.Sheets, s => s.Name is "Справочник" or "Расчет");
        Assert.Equal(2, book.HiddenSheets);
    }

    /// <summary>
    /// Ссылка у ячейки по формату необязательна: без нее ячейка стоит следом за предыдущей.
    /// Так пишут книги иные выгрузки — и такой лист сетка раньше показывала пустым.
    /// </summary>
    [Fact]
    public void Cells_without_a_reference_follow_their_neighbour()
    {
        var path = WriteRows("bare.xlsx", null,
            TextRow(1, ("", "Наименование"), ("", "Количество"), ("D1", "Цена"), ("", "Сумма")));

        Assert.Equal([(0, "Наименование"), (1, "Количество"), (3, "Цена"), (4, "Сумма")],
                     DocSheetSource.Open(path)[0].Rows[0].Cells.Select(c => (c.Column, c.Text)));
    }

    // ── Что осталось за потолками ─────────────────────────────────────────────
    //
    // Потолки строк и колонок нужны, но обрывать лист молча нельзя: человек прочитал бы
    // обрезанную таблицу как целую. Лист обязан знать, что показан не целиком, и по возможности
    // — насколько.

    [Fact]
    public void A_whole_sheet_reports_no_cut()
    {
        Assert.All(DocSheetSource.Open(WriteBook()), s => Assert.False(s.Cut.Any));
    }

    /// <summary>Сколько строк в листе всего, книга записывает сама — этим числом и говорим.</summary>
    [Fact]
    public void Rows_past_the_cap_are_reported_with_the_size_the_book_wrote()
    {
        var count = DocSheetSource.MaxRows + 5;
        var sheet = DocSheetSource.Open(WriteRows("long.xlsx", $"A1:A{count}", NumberedRows(count)))[0];

        Assert.True(sheet.Cut.Rows);
        Assert.Equal(DocSheetSource.MaxRows, sheet.Cut.RowsShown);
        Assert.Equal(count, sheet.Cut.RowsTotal);
        Assert.False(sheet.Cut.Columns);
    }

    /// <summary>Книга не записала свой размер — сколько строк всего, неизвестно, и выдумывать нельзя.</summary>
    [Fact]
    public void Without_the_size_of_the_sheet_the_total_stays_unknown()
    {
        var sheet = DocSheetSource.Open(WriteRows("long-bare.xlsx", null,
                                                  NumberedRows(DocSheetSource.MaxRows + 5)))[0];

        Assert.True(sheet.Cut.Rows);
        Assert.Equal(DocSheetSource.MaxRows, sheet.Cut.RowsShown);
        Assert.Equal(0, sheet.Cut.RowsTotal);
    }

    /// <summary>
    /// Разреженный лист: строк в файле всего две, но вторая — пятитысячная. Прочитано мало, а
    /// показать до нее нельзя: потолок показа кончается раньше. Это тоже обрез.
    /// </summary>
    [Fact]
    public void A_sparse_sheet_reports_rows_beyond_the_cap()
    {
        var sheet = DocSheetSource.Open(WriteRows("sparse.xlsx", null,
            TextRow(1, ("A1", "начало")), TextRow(5000, ("A5000", "конец"))))[0];

        Assert.True(sheet.Cut.Rows);
        Assert.Equal(DocSheetSource.MaxRows, sheet.Cut.RowsShown);
        Assert.Equal(5000, sheet.Cut.RowsTotal);
    }

    [Fact]
    public void Columns_past_the_cap_are_reported()
    {
        var far   = DocSheetSource.NameOf(DocSheetSource.MaxColumns + 2) + "1";
        var sheet = DocSheetSource.Open(WriteRows("wide.xlsx", null,
            TextRow(1, ("A1", "начало"), (far, "далеко"))))[0];

        Assert.True(sheet.Cut.Columns);
        Assert.Equal(DocSheetSource.MaxColumns, sheet.Cut.ColumnsShown);
        Assert.Equal(DocSheetSource.MaxColumns + 3, sheet.Cut.ColumnsTotal);
        Assert.DoesNotContain(sheet.Rows[0].Cells, c => c.Text == "далеко");
    }

    [Fact]
    public void Sheets_come_with_their_names_in_order()
    {
        var sheets = DocSheetSource.Open(WriteBook());

        Assert.Equal(2, sheets.Count);
        Assert.Equal("Смета",      sheets[0].Name);
        Assert.Equal("Примечания", sheets[1].Name);
    }

    [Fact]
    public void Cells_keep_their_column()
    {
        var sheet = DocSheetSource.Open(WriteBook())[0];
        var head  = sheet.Rows.First(r => r.Index == 1);   // вторая строка листа

        Assert.Equal([0, 1, 2], head.Cells.Select(c => c.Column));
        Assert.Equal("Наименование", head.Cells[0].Text);
        Assert.Equal("Дата",         head.Cells[2].Text);
    }

    [Fact]
    public void Shared_strings_are_resolved()
    {
        var sheet = DocSheetSource.Open(WriteBook())[0];

        Assert.Equal("Смета работ",    sheet.Rows[0].Cells[0].Text);
        Assert.Equal("Труба стальная", sheet.Rows.First(r => r.Index == 2).Cells[0].Text);
    }

    // ── Объединения ───────────────────────────────────────────────────────────

    /// <summary>
    /// У головной ячейки объединения — ее размах. Без него шапка нарисовалась бы в одну клетку
    /// и оборвалась многоточием, хотя в документе она идет во всю ширину таблицы.
    /// </summary>
    [Fact]
    public void Merged_head_knows_its_span()
    {
        var sheet = DocSheetSource.Open(WriteBook())[0];
        var head  = sheet.Rows[0].Cells[0];

        Assert.Equal(3, head.ColumnSpan);
        Assert.Equal(1, head.RowSpan);
    }

    [Fact]
    public void Cells_covered_by_a_merge_are_not_shown()
    {
        var sheet = DocSheetSource.Open(WriteBook())[0];

        // В первой строке ровно одна показываемая ячейка — головная.
        Assert.Single(sheet.Rows[0].Cells);
    }

    /// <summary>
    /// Про объединение знает и СТРОКА, а не только головная ячейка. Знать обязана именно она:
    /// линии сетки ведет строка, и только она может не нарисовать линию там, где клетки слиты в
    /// одну. Без этого объединение видно насквозь — текст идет через две клетки, а между ними
    /// стоит линия, которой в книге нет.
    /// </summary>
    [Fact]
    public void A_merge_is_known_to_the_row_it_crosses()
    {
        var sheet = DocSheetSource.Open(WriteBook())[0];
        var merge = Assert.Single(sheet.Rows[0].Merges);

        Assert.Equal(0, merge.From);
        Assert.Equal(2, merge.To);
        Assert.True(merge.Head);
        Assert.False(merge.Open);
        Assert.Empty(sheet.Rows[1].Merges);
    }

    /// <summary>
    /// Линия нужна по КРАЯМ объединения и не нужна внутри. Ошибись на единицу — и либо линия
    /// перечеркнет слитые клетки, либо пропадет там, где соседняя колонка начинается.
    /// </summary>
    [Fact]
    public void A_merge_swallows_only_the_boundaries_inside_it()
    {
        var merge = new DocMerge(1, 3);

        Assert.False(merge.Swallows(1));   // левый край — линия на месте
        Assert.True(merge.Swallows(2));
        Assert.True(merge.Swallows(3));
        Assert.False(merge.Swallows(4));   // правый край — линия на месте
    }

    /// <summary>
    /// Объединение ВНИЗ проходит через каждую свою строку, а не только через первую: у накрытых
    /// строк своей ячейки нет, но и нижней линии между ними быть не должно.
    /// </summary>
    [Fact]
    public void A_merge_down_crosses_every_row_it_covers()
    {
        var path = Path.Combine(_dir, "down.xlsx");
        using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var book      = doc.AddWorkbookPart();
            book.Workbook = new Workbook();

            var stylesPart = book.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = new Stylesheet(
                new Fonts(new Font()),
                new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }),
                          new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                          new Fill(new PatternFill(new ForegroundColor { Rgb = "FFCCDDEE" })
                              { PatternType = PatternValues.Solid })),
                new Borders(new Border()),
                new CellStyleFormats(new CellFormat()),
                new CellFormats(new CellFormat(), new CellFormat { FillId = 2 }));
            stylesPart.Stylesheet.Save();

            var part = book.AddNewPart<WorksheetPart>();
            part.Worksheet = new Worksheet(
                new SheetData(
                    new Row(new Cell { CellReference = "A1", StyleIndex = 1,
                                       DataType = CellValues.String, CellValue = new CellValue("Шапка") })
                        { RowIndex = 1 },
                    new Row(new Cell { CellReference = "B2", DataType = CellValues.String,
                                       CellValue = new CellValue("Строка 2") })
                        { RowIndex = 2 },
                    new Row(new Cell { CellReference = "B3", DataType = CellValues.String,
                                       CellValue = new CellValue("Строка 3") })
                        { RowIndex = 3 }),
                new MergeCells(new MergeCell { Reference = "A1:A3" }));
            part.Worksheet.Save();

            book.Workbook.AppendChild(new Sheets(
                new Sheet { Id = book.GetIdOfPart(part), SheetId = 1, Name = "Лист" }));
            book.Workbook.Save();
        }

        var sheet = DocSheetSource.Open(path)[0];
        Assert.Equal(3, sheet.Rows.Count);

        var crossings = sheet.Rows.Select(r => Assert.Single(r.Merges)).ToList();
        Assert.Equal([true, false, false], crossings.Select(m => m.Head));
        Assert.Equal([true, true, false],  crossings.Select(m => m.Open));
        Assert.All(crossings, m => Assert.Equal((0, 0), (m.From, m.To)));

        // Оформление головной ячейки достается и накрытым строкам — иначе заливка объединения
        // обрывалась бы на первой строке.
        Assert.All(crossings, m => Assert.Equal(0xFFCCDDEEu, sheet.Paints[m.Paint].Fill));
    }

    /// <summary>
    /// Объединение вниз ТЯНЕТ лист за собой. Строки под ним своих ячеек не имеют — Excel хранит
    /// их пустыми, — и оборвись лист на последней заполненной строке, слитый кусок оборвался бы
    /// вместе с ним: вместо клетки на пять строк вышла бы клетка на одну.
    /// </summary>
    [Fact]
    public void A_merge_down_stretches_the_sheet_past_the_last_filled_row()
    {
        var path = Path.Combine(_dir, "stretch.xlsx");
        using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var book      = doc.AddWorkbookPart();
            book.Workbook = new Workbook();

            var part = book.AddNewPart<WorksheetPart>();
            part.Worksheet = new Worksheet(
                new SheetData(
                    new Row(new Cell { CellReference = "A1", DataType = CellValues.String,
                                       CellValue = new CellValue("Мультисписок") })
                        { RowIndex = 1 }),
                new MergeCells(new MergeCell { Reference = "A1:B5" }));
            part.Worksheet.Save();

            book.Workbook.AppendChild(new Sheets(
                new Sheet { Id = book.GetIdOfPart(part), SheetId = 1, Name = "Лист" }));
            book.Workbook.Save();
        }

        var sheet = DocSheetSource.Open(path)[0];

        Assert.Equal(5, sheet.Rows.Count);
        Assert.All(sheet.Rows, r => Assert.Single(r.Merges));
        Assert.Equal([true, true, true, true, false], sheet.Rows.Select(r => r.Merges[0].Open));

        // Текст остался у одной головной ячейки — накрытые своих не завели.
        Assert.Single(sheet.Rows[0].Cells);
        Assert.All(sheet.Rows.Skip(1), r => Assert.Empty(r.Cells));
    }

    // ── Числа и даты ──────────────────────────────────────────────────────────

    [Fact]
    public void Date_is_shown_as_a_date_and_not_as_a_number()
    {
        var sheet = DocSheetSource.Open(WriteBook())[0];
        var row   = sheet.Rows.First(r => r.Index == 2);

        Assert.Equal("05.03.2024", row.Cells.First(c => c.Column == 2).Text);
    }

    [Fact]
    public void Numbers_lose_their_tail_of_zeros()
    {
        var sheet = DocSheetSource.Open(WriteBook())[0];
        var row   = sheet.Rows.First(r => r.Index == 2);
        var value = row.Cells.First(c => c.Column == 1).Text;

        // Разделитель дробной части зависит от языка системы — проверяем цифры.
        Assert.StartsWith("12", value, StringComparison.Ordinal);
        Assert.EndsWith("5",   value, StringComparison.Ordinal);
        Assert.DoesNotContain("0000", value, StringComparison.Ordinal);
    }

    // ── Ширины колонок ────────────────────────────────────────────────────────

    [Fact]
    public void Column_width_comes_from_the_file()
    {
        var sheet = DocSheetSource.Open(WriteBook())[0];

        Assert.NotEmpty(sheet.Widths);
        Assert.Equal(30, sheet.Widths[0], 1);
    }

    [Fact]
    public void Columns_are_counted_by_the_rightmost_cell()
    {
        Assert.Equal(3, DocSheetSource.Open(WriteBook())[0].Columns);
    }

    // ── Расстановка: высоты, пропуски и скрытое ───────────────────────────────

    /// <summary>
    /// Высота строки берется из книги. Без нее ячейка с переносом на три строки не влезает в
    /// строку одной высоты и режется ровно посередине букв — это и видно на экране.
    /// </summary>
    [Fact]
    public void Row_keeps_its_height_from_the_file()
    {
        var sheet = DocSheetSource.Open(WriteGeometryBook())[0];

        Assert.Equal(40, sheet.Rows[0].Height, 1);
        Assert.Equal(40, sheet.HeightOf(sheet.Rows[0]), 1);
    }

    /// <summary>Строке без своей высоты достается высота листа, а не наша выдумка.</summary>
    [Fact]
    public void Row_without_its_own_height_takes_the_sheet_one()
    {
        var sheet = DocSheetSource.Open(WriteGeometryBook())[0];
        var row   = sheet.Rows.First(r => r.Index == 2);

        Assert.Equal(20, sheet.DefaultHeight, 1);
        Assert.Equal(0,  row.Height, 1);
        Assert.Equal(20, sheet.HeightOf(row), 1);
    }

    /// <summary>
    /// Пустая строка между данными остается пустой строкой. Выброси ее — таблица сожмется, и
    /// номера строк перестанут совпадать с книгой, то есть станут враньем.
    /// </summary>
    [Fact]
    public void Empty_row_between_data_keeps_its_place()
    {
        var sheet = DocSheetSource.Open(WriteGeometryBook())[0];

        Assert.Equal([0, 1, 2, 4], sheet.Rows.Select(r => r.Index));
        Assert.Empty(sheet.Rows[1].Cells);
    }

    /// <summary>Скрытая в книге строка не показывается — как и в самом Excel.</summary>
    [Fact]
    public void Hidden_row_is_not_shown()
    {
        var sheet = DocSheetSource.Open(WriteGeometryBook())[0];

        Assert.DoesNotContain(sheet.Rows, r => r.Index == 3);
        Assert.DoesNotContain(sheet.Rows, r => r.Cells.Any(c => c.Text == "Скрытая"));
    }

    /// <summary>Скрытая колонка получает нулевую ширину — места на экране она не занимает.</summary>
    [Fact]
    public void Hidden_column_has_no_width()
    {
        var sheet = DocSheetSource.Open(WriteGeometryBook())[0];

        Assert.Equal(30, sheet.WidthOf(0), 1);
        Assert.Equal(0,  sheet.WidthOf(1), 1);
    }

    /// <summary>Колонке без своей ширины достается ширина листа.</summary>
    [Fact]
    public void Column_without_its_own_width_takes_the_sheet_one()
    {
        var sheet = DocSheetSource.Open(WriteGeometryBook())[0];

        Assert.Equal(12, sheet.DefaultWidth, 1);
        Assert.Equal(12, sheet.WidthOf(9),   1);
    }

    // ── Показ ячейки: перенос и выключка ──────────────────────────────────────

    [Fact]
    public void Wrap_and_alignment_come_from_the_cell_style()
    {
        var head = DocSheetSource.Open(WriteGeometryBook())[0].Rows[0].Cells[0];

        Assert.True(head.Wrap);
        Assert.Equal(DocAlign.Center,   head.Align);
        Assert.Equal(DocVAlign.Middle, head.VAlign);
    }

    /// <summary>
    /// У ячейки без своего оформления выключки нет: как ее показать, решается по значению —
    /// число вправо, текст влево. Вниз Excel прижимает по умолчанию.
    /// </summary>
    [Fact]
    public void Cell_without_a_style_has_no_alignment_of_its_own()
    {
        var cell = DocSheetSource.Open(WriteGeometryBook())[0].Rows.First(r => r.Index == 2).Cells[0];

        Assert.False(cell.Wrap);
        Assert.Equal(DocAlign.Auto,    cell.Align);
        Assert.Equal(DocVAlign.Bottom, cell.VAlign);
    }

    /// <summary>
    /// Перевод строки внутри ячейки доживает до сетки. Это не редкость: так набирают адреса и
    /// перечисления прямо в клетке, и без переводов они слипаются в одну строку.
    /// </summary>
    [Fact]
    public void Line_breaks_inside_a_cell_survive()
    {
        var path = Path.Combine(_dir, "breaks.xlsx");
        using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var book      = doc.AddWorkbookPart();
            book.Workbook = new Workbook();

            var part = book.AddNewPart<WorksheetPart>();
            part.Worksheet = new Worksheet(new SheetData(
                new Row(new Cell { CellReference = "A1", DataType = CellValues.String,
                                   CellValue = new CellValue("Первая\nВторая") })
                    { RowIndex = 1 }));
            part.Worksheet.Save();

            book.Workbook.AppendChild(new Sheets(
                new Sheet { Id = book.GetIdOfPart(part), SheetId = 1, Name = "Лист" }));
            book.Workbook.Save();
        }

        Assert.Equal("Первая\nВторая", DocSheetSource.Open(path)[0].Rows[0].Cells[0].Text);
    }

    // ── Оформление ────────────────────────────────────────────────────────────

    [Fact]
    public void Cell_fill_comes_from_the_book()
    {
        var sheet = DocSheetSource.Open(WritePaintBook())[0];
        var paint = sheet.PaintOf(sheet.Rows[0].Cells[0]);

        Assert.Equal(0xFFEEEEEEu, paint.Fill);
    }

    [Fact]
    public void Font_of_the_cell_comes_from_the_book()
    {
        var sheet = DocSheetSource.Open(WritePaintBook())[0];
        var paint = sheet.PaintOf(sheet.Rows[0].Cells[0]);

        Assert.True(paint.Bold);
        Assert.False(paint.Italic);
        Assert.Equal(14, paint.Size, 1);
        Assert.Equal(0xFF0000FFu, paint.Ink);
        Assert.Equal(11, sheet.FontSize, 1);
    }

    /// <summary>
    /// Цвет рамки берется из палитры КНИГИ, а не из стандартной. Книга вправе переопределить
    /// палитру своей, и это не редкость: у книги из Numbers номер 10 — серый, а в стандартной
    /// палитре под ним ярко-красный. Прочитай мы только стандартную — аккуратная серая таблица
    /// вышла бы в красную полоску, причем молча.
    /// </summary>
    [Fact]
    public void Border_color_comes_from_the_palette_of_this_book()
    {
        var sheet = DocSheetSource.Open(WritePaintBook())[0];
        var paint = sheet.PaintOf(sheet.Rows[0].Cells[0]);

        Assert.True(paint.Framed);
        Assert.Equal(0xFFAAAAAAu, paint.Left.Color);
        Assert.Equal(1, paint.Left.Weight);
        Assert.Equal(0xFF333333u, paint.Bottom.Color);
        Assert.Equal(2, paint.Bottom.Weight);
        Assert.False(paint.Right.Visible);
        Assert.False(paint.Top.Visible);
    }

    [Fact]
    public void Cell_without_a_style_has_no_paint_of_its_own()
    {
        var sheet = DocSheetSource.Open(WritePaintBook())[0];
        var paint = sheet.PaintOf(sheet.Rows[0].Cells.First(c => c.Text == "Простая"));

        Assert.Equal(0u, paint.Fill);
        Assert.Equal(0u, paint.Ink);
        Assert.False(paint.Bold);
        Assert.False(paint.Framed);
    }

    /// <summary>
    /// Встроенный формат числа — тот, записи которого в файле нет, только номер. Без него
    /// денежная колонка показалась бы голым числом без разрядов.
    /// </summary>
    [Fact]
    public void Builtin_number_format_is_applied()
    {
        var sheet = DocSheetSource.Open(WritePaintBook())[0];
        var text  = sheet.Rows[0].Cells.First(c => c.Column == 1).Text;

        var group = CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator;
        var point = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        Assert.Equal($"30{group}772{point}48", text);
    }

    // ── Отказы ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("текст.txt")]
    [InlineData("документ.pdf")]
    public void Anything_but_a_workbook_opens_as_empty(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "не книга");

        Assert.Empty(DocSheetSource.Open(path));
    }

    [Fact]
    public void Broken_workbook_opens_as_empty()
    {
        var path = Path.Combine(_dir, "broken.xlsx");
        File.WriteAllText(path, "PK это не книга");

        Assert.Empty(DocSheetSource.Open(path));
    }

    [Fact]
    public void Missing_file_opens_as_empty()
    {
        Assert.Empty(DocSheetSource.Open(Path.Combine(_dir, "нет-такого.xlsx")));
    }

    // ── Ссылки на ячейки ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("A1",   0)]
    [InlineData("B7",   1)]
    [InlineData("Z1",  25)]
    [InlineData("AA1", 26)]
    [InlineData("BC7", 54)]
    [InlineData("",    -1)]
    [InlineData("1A",  -1)]
    public void Column_is_read_from_the_reference(string reference, int column)
    {
        Assert.Equal(column, DocSheetSource.ColumnOf(reference));
    }

    /// <summary>Имя колонки — то самое, что стоит над ней в Excel.</summary>
    [Theory]
    [InlineData(0,  "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(54, "BC")]
    [InlineData(-1, "")]
    public void Column_name_is_the_one_excel_shows(int column, string name)
    {
        Assert.Equal(name, DocSheetSource.NameOf(column));
    }

    /// <summary>Имя и номер колонки обязаны сходиться — иначе буква встанет не над своей.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(54)]
    [InlineData(127)]
    public void Column_name_and_number_agree(int column)
    {
        Assert.Equal(column, DocSheetSource.ColumnOf(DocSheetSource.NameOf(column) + "1"));
    }

    [Fact]
    public void Row_and_column_are_read_from_the_reference()
    {
        Assert.Equal((6, 1),  DocSheetSource.CellOf("B7"));
        Assert.Equal((0, 0),  DocSheetSource.CellOf("A1"));
        Assert.Equal((-1, -1), DocSheetSource.CellOf("не ссылка"));
    }
}
