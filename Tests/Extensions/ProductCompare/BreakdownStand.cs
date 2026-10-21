using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SmartBase.Tests.Extensions.ProductCompare;

/// <summary>Строка листа «Расшифровка»: с индексом — изделие, без индекса — операция
/// изделия выше (в <paramref name="Deno"/> у нее вид работ).</summary>
internal sealed record BRow(string Index, string Name, string Deno = "", double? Qty = null,
                            double? Norm = null, string Comment = "");

/// <summary>
/// Стенд режима «расшифровка». Расшифровки и шаблон собираются в тесте: ClosedXML строит
/// книги, OpenXML добавляет то, чего ClosedXML не умеет, — макросы и кнопку на листе
/// (VML-фигуру), которые обязаны дойти до результата нетронутыми.
///
/// Раскладка расшифровки — как у макросов: шапка в 1-й строке, A — уровень, B — индекс,
/// C — наименование/операция, D — обозначение КД/вид работ, E — обозначение ТД, F — кол-во,
/// G — Т (1 шт.), H — Т всего, L — комментарий. Строки изделий залиты и с высотой 30 —
/// оформление должно переехать в результат.
/// </summary>
internal static class BreakdownStand
{
    public const string ProductFill = "4472C4";
    public const string ButtonText  = "КНОПКА";
    public const string TitleText   = "заголовок шаблона";
    public static readonly byte[] VbaBytes = [1, 2, 3, 4, 5, 6, 7, 8];

    public const string ButtonVml =
        """
        <xml xmlns:v="urn:schemas-microsoft-com:vml" xmlns:o="urn:schemas-microsoft-com:office:office" xmlns:x="urn:schemas-microsoft-com:office:excel"><v:shape id="_x0000_s1025" type="#_x0000_t201" style="position:absolute;width:96pt;height:20pt"><v:textbox><div>Создать уровни</div></v:textbox><x:ClientData ObjectType="Button"><x:Anchor>0, 2, 0, 2, 2, 0, 0, 18</x:Anchor><x:FmlaMacro>[0]!Levels.LevelsByIndex</x:FmlaMacro></x:ClientData></v:shape></xml>
        """;

    /// <summary>Файл расшифровки. Перед листом «Расшифровка» стоит посторонний лист — читать
    /// положено лист по имени, а не первый.</summary>
    public static void WriteBreakdown(string path, params BRow[] rows)
    {
        using var wb = new XLWorkbook();
        wb.AddWorksheet("Консолидация").Cell(1, 1).Value = "не тот лист";
        var ws = wb.AddWorksheet("Расшифровка");

        string[] header = ["Уровень", "Индекс", "Наименование", "Обозначение КД", "Обозначение ТД",
                           "Кол-во", "Т (1 шт.)", "Т всего", "База", "Расчет", "Зафиксировано", "Комментарий"];
        for (var c = 0; c < header.Length; c++) ws.Cell(1, c + 1).Value = header[c];

        for (var i = 0; i < rows.Length; i++)
        {
            var r   = rows[i];
            var row = i + 2;
            // Пустых клеток в файле нет вовсе, как в настоящих расшифровках: у строки операции нет
            // ни индекса (B), ни, бывает, вида работ (D). Ячейки без элемента в XML читатель
            // обязан понимать как пустые — на этом упал первый прогон на настоящей папке.
            if (r.Index.Length > 0) ws.Cell(row, 2).Value = r.Index;
            if (r.Name.Length  > 0) ws.Cell(row, 3).Value = r.Name;
            if (r.Deno.Length  > 0) ws.Cell(row, 4).Value = r.Deno;
            if (r.Qty  is { } qty)  ws.Cell(row, 6).Value = qty;
            if (r.Norm is { } norm) ws.Cell(row, 7).Value = norm;
            if (r.Comment.Length > 0) ws.Cell(row, 12).Value = r.Comment;
            ws.Cell(row, 9).Value = "база не переносится";

            if (r.Index.Length > 0)
            {
                ws.Cell(row, 1).Value = r.Index == "Изделие" ? 0 : r.Index.TrimEnd('.').Split('.').Length;
                ws.Cell(row, 5).Value = "ТД-" + r.Deno;
                var range = ws.Range(row, 1, row, 12);
                range.Style.Fill.BackgroundColor = XLColor.FromHtml("#" + ProductFill);
                range.Style.Font.Bold = true;
                ws.Row(row).Height = 30;
            }
            else
            {
                ws.Row(row).Height = 15;
            }
        }
        wb.SaveAs(path);
    }

    /// <summary>
    /// Шаблон как на фото пользователя: лист «Лист1», в A1 — кнопка (здесь ее место помечено
    /// текстом), C1:I1 — объединенный заголовок, строки 2–4 — шапка столбцов, 5 — автофильтр,
    /// 6–8 — пустые клетки с рамками. Второй лист и макросы — «обвязка», ее трогать нельзя.
    /// </summary>
    public static void WriteTemplate(string path)
    {
        var plain = Path.ChangeExtension(path, ".tmp.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Лист1");
            ws.Cell(1, 1).Value = ButtonText;
            ws.Range("C1:I1").Merge().Value = TitleText;

            string[] header = ["Уровень", "Уровень идентификатор", "Наименование", "Децимальный номер КД",
                               "Децимальный номер ТД", "Кол-во", "Т (1 шт.)", "Т всего", "Комментарий"];
            for (var c = 0; c < header.Length; c++)
            {
                var range = ws.Range(2, c + 1, 4, c + 1).Merge();
                range.Value = header[c];
                range.Style.Font.Bold = true;
            }
            ws.Range("A5:I5").SetAutoFilter();
            ws.Range("A6:I8").Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            ws.Range("A6:I8").Style.Border.InsideBorder  = XLBorderStyleValues.Thin;

            ws.Column(1).Width  = 5;
            ws.Column(3).Width  = 40;
            ws.Column(10).Width = 2;

            wb.AddWorksheet("Операции").Cell(1, 1).Value = "не трогать";
            wb.SaveAs(plain);
        }

        using (var doc = SpreadsheetDocument.Open(plain, true))
        {
            doc.ChangeDocumentType(SpreadsheetDocumentType.MacroEnabledWorkbook);
            var book = doc.WorkbookPart!;
            using (var vba = new MemoryStream(VbaBytes)) book.AddNewPart<VbaProjectPart>().FeedData(vba);

            var sheetPart = (WorksheetPart)book.GetPartById(book.Workbook.Sheets!.Elements<Sheet>().First().Id!);
            var vml = sheetPart.AddNewPart<VmlDrawingPart>();
            using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ButtonVml))) vml.FeedData(stream);

            // Место legacyDrawing задано схемой: после полей и колонтитулов листа.
            var legacy = new LegacyDrawing { Id = sheetPart.GetIdOfPart(vml) };
            var anchor = sheetPart.Worksheet.ChildElements.Last(e =>
                e is PageMargins or PageSetup or HeaderFooter or RowBreaks or ColumnBreaks or Drawing);
            anchor.InsertAfterSelf(legacy);
        }
        File.Move(plain, path, overwrite: true);
    }
}
