using ClosedXML.Excel;

namespace SmartBase.Tests.Extensions.AssemblyCalc;

/// <summary>
/// Стенд «Расчета сборочных единиц»: исходный файл и шаблон собираются ClosedXML прямо в
/// тесте — бинарных фикстур в репозитории нет.
///
/// И источник, и шаблон держат лист «Исходные данные»; данные начинаются с 6-й строки.
/// Столбцы источника разрежены (2, 5, 11, 12, 248, 283, 284, 287, 289, 346, 362), поэтому
/// строка задается парами «столбец → значение», а не позиционно.
/// </summary>
internal static class ExcelStand
{
    public const string SheetName    = "Исходные данные";
    public const int    FirstDataRow = 6;

    /// <summary>Исходный файл: каждая строка — «уровень» плюс произвольные пары столбец→текст.
    /// Уровень (столбец 1) кладется как есть: числом, строкой или ничем — от него зависит и
    /// перенос, и заливка.</summary>
    public static void WriteSource(string path, params (object? Level, (int Col, string Value)[] Cells)[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(SheetName);

        ws.Cell(1, 1).Value = "ШАПКА — читать не положено";
        ws.Cell(5, 5).Value = "подпись столбца";

        for (var i = 0; i < rows.Length; i++)
        {
            var (level, cells) = rows[i];
            var row = FirstDataRow + i;

            switch (level)
            {
                case null:            break;                            // пустой уровень обрывает перенос
                case int n:           ws.Cell(row, 1).Value = n;    break;
                case double d:        ws.Cell(row, 1).Value = d;    break;
                case string s:        ws.Cell(row, 1).Value = s;    break;
            }
            foreach (var (col, value) in cells) ws.Cell(row, col).Value = value;
        }
        wb.SaveAs(path);
    }

    /// <summary>Шаблон: лист «Исходные данные» с данными прошлого прогона — их обязана снести
    /// очистка. Второй лист несет обвязку шаблона (в бою там макросы).</summary>
    public static void WriteTemplate(string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(SheetName);
        ws.Cell(1, 1).Value = "шапка шаблона";

        for (var row = FirstDataRow; row < FirstDataRow + 3; row++)
        {
            ws.Cell(row, 2).Value  = "строка прошлого прогона";
            ws.Cell(row, 13).Value = "старое ВЭ";
            ws.Cell(row, 14).Value = "правее области данных";
        }

        wb.AddWorksheet("Обвязка").Cell(1, 1).Value = "не трогать";
        wb.SaveAs(path);
    }

    /// <summary>Книга без листа «Исходные данные» — для проверки понятной ошибки.</summary>
    public static void WriteWithoutDataSheet(string path)
    {
        using var wb = new XLWorkbook();
        wb.AddWorksheet("Другой лист").Cell(1, 1).Value = "не тот лист";
        wb.SaveAs(path);
    }

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
