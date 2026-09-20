using System.Globalization;
using System.IO;
using System.Threading;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SmartBase.Core.Documents;

/// <summary>Ячейка листа: текст и сколько клеток она занимает (объединение).</summary>
/// <param name="Column">Номер колонки с нуля — ячейки в строке идут не подряд.</param>
public readonly record struct DocCell(int Column, string Text, int ColumnSpan = 1, int RowSpan = 1);

/// <summary>Строка листа: номер (с нуля) и непустые ячейки.</summary>
public readonly record struct DocSheetRow(int Index, IReadOnlyList<DocCell> Cells);

/// <summary>Лист книги: имя, строки и ширины колонок в знаках.</summary>
public sealed class DocSheet(string name, IReadOnlyList<DocSheetRow> rows, IReadOnlyList<double> widths)
{
    public string Name { get; } = name;
    public IReadOnlyList<DocSheetRow> Rows { get; } = rows;

    /// <summary>Ширины колонок в ЗНАКАХ, как их хранит Excel; пусто — ширина по умолчанию.</summary>
    public IReadOnlyList<double> Widths { get; } = widths;

    /// <summary>Сколько колонок занято — по самой правой ячейке листа.</summary>
    public int Columns { get; } = rows.Count == 0
        ? 0
        : rows.Max(r => r.Cells.Count == 0 ? 0 : r.Cells.Max(c => c.Column + c.ColumnSpan));
}

/// <summary>
/// Книга Excel для панели чтения: листы, ячейки и объединения.
///
/// Это ТРЕТИЙ разбор XLSX в программе, и каждый нужен для своего:
/// <list type="bullet">
/// <item><c>DocTextExtractor</c> вываливает весь текст подряд — поиску нужны слова, а не
///       таблица;</item>
/// <item><c>DocReadText</c> отдает строки листа текстом — так лист читается в режиме «Текст»;</item>
/// <item>здесь книга разбирается в СЕТКУ: где какая ячейка стоит, какой она ширины и что с чем
///       объединено. Без этого таблицу не нарисовать — текстом она превращается в кашу из
///       чисел, потому что колонки в ней держат смысл.</item>
/// </list>
///
/// <b>Числа и даты.</b> Excel хранит дату числом (45678), а как ее показывать — записано в
/// стиле ячейки. Показать «45678» вместо «12.08.2026» значит показать неправду, поэтому формат
/// ячейки здесь разбирается: встроенные форматы дат по их номерам, свои — по буквам в записи
/// формата. Остальные числа печатаются как есть, без попытки повторить все правила Excel:
/// панель читают, а не считают в ней.
///
/// Исключений не бросает — как и остальные разборщики документов.
/// </summary>
public static class DocSheetSource
{
    /// <summary>Сколько строк листа показываем: дальше это уже не чтение, а выгрузка.</summary>
    public const int MaxRows = DocReadText.MaxSheetRows;

    /// <summary>Сколько колонок показываем.</summary>
    public const int MaxColumns = 128;

    /// <summary>Ширина колонки по умолчанию в знаках — та же, что у Excel.</summary>
    public const double DefaultWidth = 8.43;

    /// <summary>
    /// Разобрать книгу. Пустой список — файл не XLSX, не открылся или в нем нет ни одного
    /// листа с данными; для панели это не сбой, а повод остаться в режиме текста.
    /// </summary>
    public static IReadOnlyList<DocSheet> Open(string path, CancellationToken ct = default)
    {
        var ext = DocTextExtractor.ExtensionOf(path);
        if (ext is not ("xlsx" or "xlsm")) return [];

        try
        {
            if (!File.Exists(path)) return [];

            using var doc = SpreadsheetDocument.Open(path, false);
            var book = doc.WorkbookPart;
            if (book is null) return [];

            var shared  = book.SharedStringTablePart?.SharedStringTable
                              ?.Elements<SharedStringItem>().Select(i => i.InnerText).ToArray() ?? [];
            var formats = NumberFormats(book);

            var sheets = new List<DocSheet>();
            foreach (var entry in book.Workbook?.Sheets?.Elements<Sheet>() ?? [])
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Id?.Value is not { } id) continue;
                if (book.GetPartById(id) is not WorksheetPart part) continue;

                var sheet = ReadSheet(entry.Name?.Value ?? "Лист", part, shared, formats, ct);
                if (sheet.Rows.Count > 0) sheets.Add(sheet);
            }
            return sheets;
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    /// <summary>Фоновый разбор: книга на тысячи строк не должна держать UI-поток.</summary>
    public static Task<IReadOnlyList<DocSheet>> OpenAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => Open(path, ct), ct);

    // ── Разбор листа ──────────────────────────────────────────────────────────

    private static DocSheet ReadSheet(string name, WorksheetPart part, string[] shared,
                                      Dictionary<uint, string> formats, CancellationToken ct)
    {
        var sheet  = part.Worksheet;
        var merges = Merges(sheet);
        var widths = Widths(sheet);
        var rows   = new List<DocSheetRow>();

        var data = sheet?.Elements<SheetData>().FirstOrDefault();
        if (data is null) return new DocSheet(name, rows, widths);

        var seen = 0;
        foreach (var row in data.Elements<Row>())
        {
            ct.ThrowIfCancellationRequested();
            if (++seen > MaxRows) break;

            var index = (int)(row.RowIndex?.Value ?? (uint)seen) - 1;
            var cells = new List<DocCell>();

            foreach (var cell in row.Elements<Cell>())
            {
                var column = ColumnOf(cell.CellReference?.Value);
                if (column < 0 || column >= MaxColumns) continue;

                // Ячейка, накрытая чужим объединением, своего места на экране не имеет —
                // Excel хранит ее пустой, но показывать ее нельзя даже если текст в ней есть.
                if (merges.Covered.Contains((index, column))) continue;

                var text = Text(cell, shared, formats);
                if (text.Length == 0) continue;

                var span = merges.Spans.TryGetValue((index, column), out var s) ? s : (Cols: 1, Rows: 1);
                cells.Add(new DocCell(column, text, span.Cols, span.Rows));
            }

            if (cells.Count > 0) rows.Add(new DocSheetRow(index, cells));
        }

        return new DocSheet(name, rows, widths);
    }

    /// <summary>
    /// Объединения: для головной ячейки — ее размах, для накрытых — отметка «не показывать».
    /// Без второго объединенная шапка рисовалась бы поверх собственных пустых клеток, и линии
    /// сетки лезли бы сквозь нее.
    /// </summary>
    private static (Dictionary<(int Row, int Col), (int Cols, int Rows)> Spans,
                    HashSet<(int Row, int Col)> Covered) Merges(Worksheet? sheet)
    {
        var spans   = new Dictionary<(int, int), (int, int)>();
        var covered = new HashSet<(int, int)>();

        foreach (var merge in sheet?.Elements<MergeCells>().FirstOrDefault()?.Elements<MergeCell>() ?? [])
        {
            var reference = merge.Reference?.Value ?? "";
            var parts     = reference.Split(':');
            if (parts.Length != 2) continue;

            var (r1, c1) = CellOf(parts[0]);
            var (r2, c2) = CellOf(parts[1]);
            if (r1 < 0 || r2 < 0 || c1 < 0 || c2 < 0) continue;

            var (top, bottom) = (Math.Min(r1, r2), Math.Max(r1, r2));
            var (left, right) = (Math.Min(c1, c2), Math.Max(c1, c2));

            spans[(top, left)] = (right - left + 1, bottom - top + 1);
            for (var r = top; r <= bottom; r++)
            for (var c = left; c <= right; c++)
                if (r != top || c != left) covered.Add((r, c));
        }
        return (spans, covered);
    }

    private static IReadOnlyList<double> Widths(Worksheet? sheet)
    {
        var widths = new List<double>();
        foreach (var column in sheet?.Elements<Columns>().FirstOrDefault()?.Elements<Column>() ?? [])
        {
            var from  = (int)(column.Min?.Value ?? 1) - 1;
            var to    = (int)(column.Max?.Value ?? 1) - 1;
            var width = column.Width?.Value ?? DefaultWidth;

            for (var i = from; i <= Math.Min(to, MaxColumns - 1); i++)
            {
                while (widths.Count <= i) widths.Add(DefaultWidth);
                widths[i] = width;
            }
        }
        return widths;
    }

    // ── Значение ячейки ───────────────────────────────────────────────────────

    private static string Text(Cell cell, string[] shared, Dictionary<uint, string> formats)
    {
        var raw = cell.CellValue?.InnerText ?? "";

        if (cell.DataType?.Value == CellValues.SharedString)
            return int.TryParse(raw, out var idx) && idx >= 0 && idx < shared.Length ? shared[idx] : "";

        if (cell.DataType?.Value == CellValues.InlineString) return cell.InnerText;
        if (cell.DataType?.Value == CellValues.Boolean)      return raw == "1" ? "ИСТИНА" : "ЛОЖЬ";
        if (raw.Length == 0)                                 return cell.InnerText;

        // Число: дата это или нет, решает формат ячейки.
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return raw;

        var format = cell.StyleIndex?.Value is { } style && formats.TryGetValue(style, out var code) ? code : "";
        if (IsDate(format)) return AsDate(number, format);

        // Печатаем без хвоста нулей: 12 вместо 12,00000001 и 0,5 вместо 0,50000000000000011.
        return number.ToString("0.##########", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Запись формата по номеру стиля ячейки. Встроенные форматы Excel в файле не лежат —
    /// у них только номера, поэтому нужные подставляем сами.
    /// </summary>
    private static Dictionary<uint, string> NumberFormats(WorkbookPart book)
    {
        var map = new Dictionary<uint, string>();
        var styles = book.WorkbookStylesPart?.Stylesheet;
        if (styles?.CellFormats is not { } formats) return map;

        var custom = styles.NumberingFormats?.Elements<NumberingFormat>()
                           .Where(f => f.NumberFormatId?.Value is not null)
                           .ToDictionary(f => f.NumberFormatId!.Value!, f => f.FormatCode?.Value ?? "")
                     ?? [];

        var index = 0u;
        foreach (var format in formats.Elements<CellFormat>())
        {
            var id = format.NumberFormatId?.Value ?? 0;
            map[index++] = custom.TryGetValue(id, out var code) ? code : Builtin(id);
        }
        return map;
    }

    /// <summary>Встроенные форматы, которые нас интересуют, — все про дату и время.</summary>
    private static string Builtin(uint id) => id switch
    {
        14 => "dd.mm.yyyy",
        15 => "d-mmm-yy",
        16 => "d-mmm",
        17 => "mmm-yy",
        18 or 19 or 20 or 21 => "h:mm",
        22 => "dd.mm.yyyy h:mm",
        45 or 46 or 47 => "h:mm",
        _  => "",
    };

    /// <summary>
    /// Формат про дату или время. Смотрим только на буквы ВНЕ кавычек: в записи вроде
    /// «0.00 "дней"» буква «д» относится к подписи, а не к дате.
    /// </summary>
    private static bool IsDate(string format)
    {
        var quoted = false;
        foreach (var ch in format)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (ch is 'y' or 'Y' or 'd' or 'D' or 'm' or 'M' or 'h' or 'H' or 's' or 'S') return true;
        }
        return false;
    }

    private static string AsDate(double number, string format)
    {
        try
        {
            var date = DateTime.FromOADate(number);
            var time = format.Contains('h', StringComparison.OrdinalIgnoreCase);
            var day  = format.Contains('d', StringComparison.OrdinalIgnoreCase) ||
                       format.Contains('y', StringComparison.OrdinalIgnoreCase);

            if (day && time) return date.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
            if (time)        return date.ToString("HH:mm", CultureInfo.CurrentCulture);
            return date.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);
        }
        catch
        {
            // Число вне диапазона дат — показываем как число, а не как ошибку.
            return number.ToString("0.##########", CultureInfo.CurrentCulture);
        }
    }

    // ── Ссылки на ячейки ──────────────────────────────────────────────────────

    /// <summary>Номер колонки с нуля по ссылке вида «BC7»; -1 — ссылка не разобрана.</summary>
    public static int ColumnOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return -1;

        var column = 0;
        var seen   = false;
        foreach (var ch in reference)
        {
            if (char.IsAsciiLetter(ch))
            {
                if (seen) return -1;   // буквы после цифр — это не ссылка
                column = column * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
                continue;
            }
            if (!char.IsAsciiDigit(ch)) return -1;
            seen = true;
        }
        return column > 0 ? column - 1 : -1;
    }

    /// <summary>Строка и колонка с нуля по ссылке вида «BC7».</summary>
    public static (int Row, int Column) CellOf(string? reference)
    {
        var column = ColumnOf(reference);
        if (column < 0) return (-1, -1);

        var digits = new string((reference ?? "").Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var row) && row > 0 ? (row - 1, column) : (-1, -1);
    }
}
