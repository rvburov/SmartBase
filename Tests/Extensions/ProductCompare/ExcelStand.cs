using ClosedXML.Excel;

namespace SmartBase.Tests.Extensions.ProductCompare;

/// <summary>Одна строка состава изделия, как ее видит расширение: позиция, иерархический
/// номер, наименование, обозначение (ключ уникальности) и количество.</summary>
internal sealed record CompareRow(string Pos, string Hierarchy, string Name, string Designation, string Qty);

/// <summary>
/// Стенд «Сравнения составов»: источники и шаблон собираются ClosedXML прямо в тесте —
/// бинарных фикстур в репозитории нет. Раскладка колонок повторяет исходную утилиту
/// (1 — позиция, 2 — иерархия, 5 — наименование, 11 — обозначение, 12 — количество),
/// данные начинаются с 6-й строки: строки 1–5 — шапка, ее расширение читать не должно.
/// </summary>
internal static class ExcelStand
{
    public const int FirstDataRow = 6;

    /// <summary>Файл состава изделия. В шапку кладется мусор — он не должен попасть в результат.</summary>
    public static void WriteSource(string path, params CompareRow[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Состав");

        ws.Cell(1, 1).Value = "ШАПКА — читать не положено";
        ws.Cell(5, 2).Value = "1.999.";                     // похоже на иерархию, но выше данных

        for (var i = 0; i < rows.Length; i++)
        {
            var r   = rows[i];
            var row = FirstDataRow + i;
            ws.Cell(row, 1).Value  = r.Pos;
            ws.Cell(row, 2).Value  = r.Hierarchy;
            ws.Cell(row, 5).Value  = r.Name;
            ws.Cell(row, 11).Value = r.Designation;
            ws.Cell(row, 12).Value = r.Qty;
        }
        wb.SaveAs(path);
    }

    /// <summary>Файл-шаблон: первый лист принимает результат, второй несет «обвязку» шаблона —
    /// она обязана пережить прогон (в бою там лежат макросы и настройки).</summary>
    public static void WriteTemplate(string path)
    {
        using var wb = new XLWorkbook();
        wb.AddWorksheet("Результат").Cell(1, 1).Value = "шапка шаблона";
        wb.AddWorksheet("Обвязка").Cell(1, 1).Value   = "не трогать";
        wb.SaveAs(path);
    }

    /// <summary>Значение ячейки результата строкой (пустая ячейка — пустая строка).</summary>
    public static string Text(this IXLWorksheet ws, int row, int col) => ws.Cell(row, col).GetString();

    /// <summary>Цвет заливки ячейки в виде «RRGGBB» или null, если заливки нет.</summary>
    public static string? Fill(this IXLWorksheet ws, int row, int col)
    {
        var color = ws.Cell(row, col).Style.Fill.BackgroundColor;
        return color.ColorType == XLColorType.Color && color.Color.A != 0
            ? $"{color.Color.R:X2}{color.Color.G:X2}{color.Color.B:X2}"
            : null;
    }
}
