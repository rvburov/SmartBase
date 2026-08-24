using System.Threading;
using ClosedXML.Excel;

namespace SmartBase.Extensions.TimeCollector;

/// <summary>
/// Запись свода на лист «Таблица» .xlsm-шаблона — порт того, что макрос делал прямо в книге
/// (<c>ClearWS</c> → <c>SetHeaderData</c> → запись массива → <c>ApplyFastFormatting</c>).
/// Реализация на ClosedXML, как у остальных расширений, работающих с Excel; макросы шаблона
/// переживают <c>SaveAs</c>.
///
/// ClosedXML держит книгу в памяти целиком, поэтому оформление кладется диапазонами, а не
/// по ячейкам: на десятках тысяч строк это разница в разы. Прогресс репортится по фазам,
/// отмена проверяется между ними.
/// </summary>
internal static class TimeTableWriter
{
    // Колонки свода (1-based) — те же константы, что COL_* в модуле TimeCollector.
    internal const int ColRNum = 1;   // номер строки листа «Расшифровка» − 1, колонка скрытая
    internal const int ColName = 2;
    internal const int ColDeno = 3;
    internal const int ColNorm = 4;
    internal const int ColDate = 5;
    internal const int ColEfio = 6;
    internal const int ColProj = 7;
    internal const int ColLink = 8;
    internal const int LastCol = ColLink;

    /// <summary>Шапка занимает строки 1–3, данные начинаются с 4-й (TOP_INDENT макроса).</summary>
    private const int TopIndent = 3;

    private const string NamedRange  = "ALL_DATA";
    private const string SourceSheet = "Расшифровка";
    private const string LinkMark    = ">>>";

    // Прогресс постится не на каждую строку: поток сообщений в UI тормозит сильнее записи.
    private const int ProgressEvery = 500;

    // Цвета макроса в RGB: ColorIndex 19 (светло-желтый) и ColorIndex 3 (красный).
    private static readonly XLColor Yellow = XLColor.FromArgb(255, 255, 204);
    private static readonly XLColor Red    = XLColor.FromArgb(255, 0, 0);

    // Символы, при которых формулу HYPERLINK не собрать: кавычка рвет строку, скобки — адрес.
    private static readonly char[] BadLinkChars = ['"', '[', ']'];

    internal static void Fill(
        XLWorkbook wb, IXLWorksheet ws,
        IReadOnlyList<TimeRow> rows, TimeCollectorEngine.RowPaint[] paints, IReadOnlySet<int> hidden,
        IProgress<CollectProgress>? progress, CancellationToken ct,
        double clearTo, double writeTo, double formatTo)
    {
        progress?.Report(new CollectProgress(null, $"Очистка листа «{ws.Name}»…", ""));
        ClearSheet(ws);
        ct.ThrowIfCancellationRequested();

        WriteHeader(ws);
        progress?.Report(new CollectProgress(clearTo, "Запись строк", $"0 из {rows.Count}"));

        WriteRows(ws, rows, progress, ct, clearTo, writeTo);

        var last = TopIndent + rows.Count;
        Decorate(wb, ws, paints, hidden, TopIndent + 1, last, progress, ct, writeTo, formatTo);
    }

    // ── Очистка (ClearWS) ─────────────────────────────────────────────────────

    private static void ClearSheet(IXLWorksheet ws)
    {
        // Автофильтр прошлого прогона держит свой диапазон и может прятать строки.
        ws.AutoFilter.Clear();

        // Объединения шапки: без снятия Merge новая шапка ляжет поверх старых блоков.
        foreach (var merged in ws.MergedRanges.ToList()) merged.Unmerge();

        // XLCellsUsedOptions.All — вместе с ячейками, где остались только формат и заливка
        // прошлой раскраски: по AllContents они бы не попали в диапазон и цвет бы уцелел.
        ws.RangeUsed(XLCellsUsedOptions.All)?.Clear(XLClearOptions.All);

        for (var col = 1; col <= LastCol; col++) ws.Column(col).Unhide();
    }

    // ── Шапка (SetHeaderData) ─────────────────────────────────────────────────

    private static void WriteHeader(IXLWorksheet ws)
    {
        ws.Cell(1, ColName).Value = "Наименование";
        ws.Cell(1, ColDeno).Value = "Обозначение КД";
        ws.Cell(1, ColNorm).Value = "Тр-ть";
        ws.Cell(1, ColDate).Value = "Дата";
        ws.Cell(1, ColEfio).Value = "ФИО";
        ws.Cell(1, ColProj).Value = "Проект";
        ws.Cell(1, ColLink).Value = "Ссылка";

        // Каждая подпись объединена с ячейкой под ней — как в макросе. Колонка A подписи не
        // получает: она служебная и все равно скрывается.
        for (var col = ColName; col <= ColLink; col++) ws.Range(1, col, 2, col).Merge();

        ws.Row(1).Height = 30;
        ws.Row(2).Height = 100;
        ws.Row(3).Height = 13;

        // Границы — как в макросе; выравнивание с переносом добавлено взамен форматирования,
        // которое снимает ClearSheet (макрос чистил только содержимое шапки, не ее вид).
        var head = ws.Range(1, ColRNum, TopIndent, LastCol);
        head.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        head.Style.Border.InsideBorder  = XLBorderStyleValues.Thin;
        head.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        head.Style.Alignment.Vertical   = XLAlignmentVerticalValues.Center;
        head.Style.Alignment.WrapText   = true;
    }

    // ── Данные ────────────────────────────────────────────────────────────────

    private static void WriteRows(IXLWorksheet ws, IReadOnlyList<TimeRow> rows,
                                  IProgress<CollectProgress>? progress, CancellationToken ct,
                                  double from, double to)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var source = rows[i];
            var row    = TopIndent + 1 + i;

            ws.Cell(row, ColRNum).Value = source.RNum;
            ws.Cell(row, ColName).Value = source.Name;
            ws.Cell(row, ColDeno).Value = source.Deno;

            var norm = ws.Cell(row, ColNorm);
            if (source.Norm is { } value) norm.Value = value;
            else                          norm.Value = source.NormText;

            ws.Cell(row, ColDate).Value = source.Date;
            ws.Cell(row, ColEfio).Value = source.Efio;
            ws.Cell(row, ColProj).Value = source.Proj;

            WriteLink(ws.Cell(row, ColLink), source);

            if (i % ProgressEvery == 0 || i == rows.Count - 1)
                progress?.Report(new CollectProgress(
                    from + (to - from) * (i + 1) / rows.Count, "Запись строк", $"{i + 1} из {rows.Count}"));
        }
    }

    /// <summary>
    /// Колонка «Ссылка» — оба способа перехода сразу. Текст «&gt;&gt;&gt;» и номер строки в
    /// скрытой колонке A нужны макросу шаблона (<c>WsSelectionChanged</c> → <c>CalculationLink</c>),
    /// формула <c>HYPERLINK</c> открывает тот же расчет на той же строке и без макросов.
    /// </summary>
    private static void WriteLink(IXLCell cell, TimeRow row)
    {
        if (row.SourcePath.Length == 0 || row.SourcePath.IndexOfAny(BadLinkChars) >= 0)
        {
            cell.Value = LinkMark;
            return;
        }

        var target = $"[{row.SourcePath}]{SourceSheet}!A{row.RNum + 1}";
        cell.FormulaA1 = $"HYPERLINK(\"{target}\",\"{LinkMark}\")";
    }

    // ── Оформление (ApplyFastFormatting) ──────────────────────────────────────

    private static void Decorate(XLWorkbook wb, IXLWorksheet ws,
                                 TimeCollectorEngine.RowPaint[] paints, IReadOnlySet<int> hidden,
                                 int first, int last,
                                 IProgress<CollectProgress>? progress, CancellationToken ct,
                                 double from, double to)
    {
        progress?.Report(new CollectProgress(from, "Оформление…", "высота строк и границы"));

        // Высота — ДЕФОЛТОМ листа, а не проходом по строкам: у макроса свод занимал тысячи
        // строк, а тут сотни тысяч, и Rows(first, last).Height материализует каждую. Шапке
        // это не мешает — ее строкам высота проставлена явно (WriteHeader) и дефолт перебивает.
        ws.RowHeight = 15;

        var data = ws.Range(first, ColRNum, last, LastCol);
        data.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        data.Style.Border.InsideBorder  = XLBorderStyleValues.Thin;
        ws.Range(first, ColDate, last, ColDate).Style.DateFormat.Format = "dd.MM.yyyy";

        ct.ThrowIfCancellationRequested();
        progress?.Report(new CollectProgress(from + (to - from) * 0.4, "Оформление…", "раскраска"));
        PaintRows(ws, paints, first);

        ct.ThrowIfCancellationRequested();
        progress?.Report(new CollectProgress(from + (to - from) * 0.8, "Оформление…", "фильтр и диапазоны"));

        foreach (var col in hidden) ws.Column(col).Hide();

        // Именованный диапазон и автофильтр ставятся на строку шапки-фильтра (3-ю) и все данные —
        // те же адреса, что собирал макрос.
        var whole = ws.Range(TopIndent, ColRNum, last, LastCol);
        RemoveName(wb, ws, NamedRange);
        wb.DefinedNames.Add(NamedRange, whole);
        whole.SetAutoFilter();

        progress?.Report(new CollectProgress(to, "Оформление…", ""));
    }

    // Заливка кладется непрерывными кусками одного цвета: вызов на каждую строку в ClosedXML
    // на десятках тысяч строк стоит заметно дороже одного вызова на всю группу обозначения.
    // Белые куски пропускаем — лист очищен, и «нет заливки» выглядит ровно так же.
    private static void PaintRows(IXLWorksheet ws, TimeCollectorEngine.RowPaint[] paints, int firstRow)
    {
        var start = 0;
        for (var i = 1; i <= paints.Length; i++)
        {
            if (i < paints.Length && paints[i].Fill == paints[start].Fill) continue;

            var color = FillColor(paints[start].Fill);
            if (color is not null)
                ws.Range(firstRow + start, ColRNum, firstRow + i - 1, LastCol)
                  .Style.Fill.BackgroundColor = color;

            start = i;
        }

        // Точечная пометка «Тр-ть» (расхождение при разных проекте или дате) — таких немного.
        for (var i = 0; i < paints.Length; i++)
            if (paints[i].NormRed)
                ws.Cell(firstRow + i, ColNorm).Style.Fill.BackgroundColor = Red;
    }

    private static XLColor? FillColor(TimeCollectorEngine.RowFill fill) => fill switch
    {
        TimeCollectorEngine.RowFill.Yellow => Yellow,
        TimeCollectorEngine.RowFill.Red    => Red,
        _                                  => null,   // белый — оставляем «без заливки»
    };

    private static void RemoveName(XLWorkbook wb, IXLWorksheet ws, string name)
    {
        if (wb.DefinedNames.TryGetValue(name, out var book))  book.Delete();
        if (ws.DefinedNames.TryGetValue(name, out var sheet)) sheet.Delete();
    }
}
