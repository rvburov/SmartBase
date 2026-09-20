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
/// Это третий разбор XLSX в программе, и проверки здесь про то, чего нет у двух других: место
/// ячейки, объединения и ширины колонок. Текстовые разборы такого не знают и знать не должны —
/// поиску и режиму «Текст» колонки не нужны.
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

    // ── Листы и ячейки ────────────────────────────────────────────────────────

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
