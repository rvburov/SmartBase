using System.Globalization;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Книга с числами разных записей формата — для проверок «что показывает панель, по тому поиск и
/// находит». На видимом листе «Смета»: общие строки, деньги с разрядами, дата двумя записями,
/// проценты, логическое значение и строка «00123», которую вернула формула. На скрытом листе —
/// еще одна сумма: поиск ищет и там.
/// </summary>
internal static class DocNumbersBook
{
    /// <summary>Дата в книге — 5 марта 2024 года.</summary>
    public static readonly DateTime Day = new(2024, 3, 5);

    /// <summary>Сумма на видимом листе.</summary>
    public const double Money = 30772.48;

    /// <summary>Сумма на скрытом листе.</summary>
    public const double HiddenMoney = 98765.43;

    /// <param name="date1904">Книга считает дни от 1904 года — номер дня в ней на 1462 меньше.</param>
    public static string Write(string dir, string name = "числа.xlsx", bool date1904 = false)
    {
        var path = Path.Combine(dir, name);
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);

        var book      = doc.AddWorkbookPart();
        book.Workbook = new Workbook();
        if (date1904) book.Workbook.WorkbookProperties = new WorkbookProperties { Date1904 = true };

        var sharedPart = book.AddNewPart<SharedStringTablePart>();
        sharedPart.SharedStringTable = new SharedStringTable(
            new SharedStringItem(new Text("Смета работ")),
            new SharedStringItem(new Text("Итого")));
        sharedPart.SharedStringTable.Save();

        // Стили по номерам: 1 — дата «dd.mm.yyyy», 2 — деньги (встроенная 4, «#,##0.00»),
        // 3 — проценты (встроенная 9), 4 — дата словами «d mmmm yyyy», 5 — «0.00».
        var stylesPart = book.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = new Stylesheet(
            new NumberingFormats(
                new NumberingFormat { NumberFormatId = 165, FormatCode = "dd.mm.yyyy" },
                new NumberingFormat { NumberFormatId = 166, FormatCode = "d mmmm yyyy" }),
            new Fonts(new Font()), new Fills(new Fill()), new Borders(new Border()),
            new CellStyleFormats(new CellFormat()),
            new CellFormats(
                new CellFormat(),
                new CellFormat { NumberFormatId = 165, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 4,   ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 9,   ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 166, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 2,   ApplyNumberFormat = true }));
        stylesPart.Stylesheet.Save();

        var serial = Number(Day.ToOADate() - (date1904 ? 1462 : 0));

        var visible = book.AddNewPart<WorksheetPart>();
        visible.Worksheet = new Worksheet(new SheetData(
            new Row(Shared("A1", 0)) { RowIndex = 1 },
            new Row(
                Shared("A2", 1),
                new Cell { CellReference = "B2", StyleIndex = 2, CellValue = new CellValue(Number(Money)) },
                new Cell { CellReference = "C2", StyleIndex = 1, CellValue = new CellValue(serial) },
                new Cell { CellReference = "D2", StyleIndex = 3, CellValue = new CellValue("0.15") })
                { RowIndex = 2 },
            new Row(
                new Cell { CellReference = "A3", DataType = CellValues.Boolean, CellValue = new CellValue("1") },
                new Cell { CellReference = "B3", StyleIndex = 5, DataType = CellValues.String,
                           CellFormula = new CellFormula("TEXT(123,\"00000\")"), CellValue = new CellValue("00123") },
                new Cell { CellReference = "C3", StyleIndex = 4, CellValue = new CellValue(serial) })
                { RowIndex = 3 }));
        visible.Worksheet.Save();

        var hidden = book.AddNewPart<WorksheetPart>();
        hidden.Worksheet = new Worksheet(new SheetData(
            new Row(new Cell { CellReference = "A1", StyleIndex = 2, CellValue = new CellValue(Number(HiddenMoney)) })
                { RowIndex = 1 }));
        hidden.Worksheet.Save();

        book.Workbook.AppendChild(new Sheets(
            new Sheet { Id = book.GetIdOfPart(visible), SheetId = 1, Name = "Смета" },
            new Sheet { Id = book.GetIdOfPart(hidden),  SheetId = 2, Name = "Служебный",
                        State = SheetStateValues.Hidden }));
        book.Workbook.Save();
        return path;
    }

    /// <summary>Как сумму показывает книга по записи «#,##0.00» — с разрядами языка системы.</summary>
    public static string Shown(double money) => money.ToString("#,##0.00", CultureInfo.CurrentCulture);

    private static Cell Shared(string reference, int index) =>
        new() { CellReference = reference, DataType = CellValues.SharedString,
                CellValue = new CellValue(index.ToString(CultureInfo.InvariantCulture)) };

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
