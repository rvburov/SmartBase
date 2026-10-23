using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SmartBase.Extensions.ProductCompare;

/// <summary>Что лежит в ячейке источника — так же она и уйдет в результат.</summary>
internal enum SourceKind { Empty, Number, Text, Bool, Error, Date }

/// <summary>Ячейка источника: значение в том виде, в каком оно лежит в файле (число — его
/// запись из XML без пересчета), и номер стиля в книге-источнике.</summary>
internal readonly record struct SourceCell(SourceKind Kind, string Value, uint Style)
{
    /// <summary>Ячейки, которой в XML нет вовсе: у пустых клеток настоящих расшифровок элемента
    /// нет. Значение по умолчанию (<c>default</c>) не годится — в нем <see cref="Value"/> равно
    /// <c>null</c>, и на нем упал первый прогон на настоящей папке.</summary>
    public static readonly SourceCell Missing = new(SourceKind.Empty, "", 0);

    public bool IsEmpty => Kind == SourceKind.Empty;
}

/// <summary>Строка источника, урезанная до столбцов блока, и ее высота (если задана).</summary>
internal sealed class SourceRow(SourceCell[] cells, double? height)
{
    public SourceCell[] Cells  { get; } = cells;
    public double?      Height { get; } = height;
}

/// <summary>Изделие в дереве входимости одного файла: строка изделия, его операции (строки
/// без индекса сразу под ним) и входящие изделия. Корень дерева строки не имеет.</summary>
internal sealed class BreakdownNode
{
    public string     Key        { get; init; } = "";
    /// <summary>Индекс из столбца B как он записан («Изделие», «1», «1.2.»). По нему строки
    /// изделий разных дат встают друг напротив друга (<see cref="BreakdownMerge"/>).</summary>
    public string     Index      { get; init; } = "";
    public int        Level      { get; init; } = -1;
    public SourceRow? Row        { get; init; }

    public List<(string Key, int Occurrence, SourceRow Row)> Operations { get; } = [];
    public List<BreakdownNode> Children { get; } = [];
}

/// <summary>Прочитанный файл расшифровки: дерево и стили книги (нужны, чтобы перенести
/// оформление ячеек в результат уже после закрытия файла).</summary>
internal sealed class BreakdownSheet(BreakdownFile file, BreakdownNode root, Stylesheet? styles,
                                     int products, int operations)
{
    /// <summary>Верхнее изделие файла — строка «Изделие» (уровень 0), а без нее первое изделие
    /// листа; <c>null</c> — изделий на листе нет. По нему движок проверяет, что все файлы —
    /// расшифровки одного изделия.</summary>
    public BreakdownNode? Top { get; } =
        root.Children.FirstOrDefault(c => c.Level == 0) ?? root.Children.FirstOrDefault();

    public BreakdownFile File       { get; } = file;
    public BreakdownNode Root       { get; } = root;
    public Stylesheet?   Styles     { get; } = styles;
    public int           Products   { get; } = products;
    public int           Operations { get; } = operations;
}

/// <summary>
/// Чтение листа «Расшифровка» одного файла (OpenXML, без ClosedXML: нужны и значения, и номера
/// стилей как есть). Раскладка — как у макросов расшифровки (<c>Calculation.bas</c>): первая
/// строка — шапка (<c>top_indent = 1</c>), A — уровень, B — индекс («Изделие», «1», «1.2»…),
/// C — наименование или операция, D — обозначение КД (у операции — вид работ), E — обозначение
/// ТД, F — кол-во, G — Т (1 шт.), H — Т всего, L — комментарий.
///
/// Строка с индексом — изделие, без индекса — операция ближайшего изделия выше. Индекс узла
/// (<see cref="BreakdownNode.Index"/>) сводит строки разных дат; ключ (обозначение КД, без него
/// наименование) нужен проверке «все файлы — одно изделие» по верхнему изделию.
/// </summary>
internal static partial class BreakdownReader
{
    public const string SheetName = "Расшифровка";

    /// <summary>Столбцы источника (1-based), которые попадают в блок результата, по порядку
    /// столбцов блока: A–H как есть, комментарий из L — в девятый.</summary>
    public static readonly int[] SourceColumns = [1, 2, 3, 4, 5, 6, 7, 8, 12];

    private const int HeaderRows = 1;
    private const int ColIndex   = 1;   // B — в нумерации столбцов блока (0-based)
    private const int ColName    = 2;   // C
    private const int ColDeno    = 3;   // D

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static BreakdownSheet Read(BreakdownFile file, CancellationToken ct)
    {
        using var stream = OpenShared(file.Path);
        SpreadsheetDocument doc;
        try { doc = SpreadsheetDocument.Open(stream, false); }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or IOException or FileFormatException)
        {
            throw new ProductCompareException(
                $"Не удалось открыть файл:\n{Path.GetFileName(file.Path)}\nЭто не книга Excel или файл поврежден.");
        }

        using (doc)
        {
            var book  = doc.WorkbookPart
                        ?? throw new ProductCompareException($"В файле «{Path.GetFileName(file.Path)}» нет книги Excel.");
            var sheet = FindSheet(book)
                        ?? throw new ProductCompareException(
                            $"В файле «{Path.GetFileName(file.Path)}» нет листа «{SheetName}».");

            var strings = SharedStrings(book);
            var styles  = book.WorkbookStylesPart?.Stylesheet?.CloneNode(true) as Stylesheet;

            var root  = new BreakdownNode();
            var stack = new Stack<BreakdownNode>();
            stack.Push(root);
            int products = 0, operations = 0;

            foreach (var (rowIndex, row) in Rows(sheet))
            {
                ct.ThrowIfCancellationRequested();
                if (rowIndex <= HeaderRows) continue;
                try
                {
                    var cells = ReadCells(row, strings);
                    if (cells.All(c => c.IsEmpty)) continue;              // пустая строка-разделитель
                    var source = new SourceRow(cells, row.CustomHeight?.Value == true ? row.Height?.Value : (double?)null);

                    var index = cells[ColIndex].Value.Trim();
                    if (index.Length == 0)
                    {
                        var current = stack.Peek();
                        var opKey   = Normalize(cells[ColName].Value);
                        var opOcc   = current.Operations.Count(o => o.Key == opKey);
                        current.Operations.Add((opKey, opOcc, source));
                        operations++;
                        continue;
                    }

                    var level = Level(index);
                    while (stack.Peek().Level >= level) stack.Pop();
                    var parent = stack.Peek();

                    var deno = Normalize(cells[ColDeno].Value);
                    var key  = deno.Length > 0 ? "КД:" + deno : "Имя:" + Normalize(cells[ColName].Value);
                    var node = new BreakdownNode
                    {
                        Key        = key,
                        Index      = index,
                        Level      = level,
                        Row        = source,
                    };
                    parent.Children.Add(node);
                    stack.Push(node);
                    products++;
                }
                catch (Exception ex) when (ex is not ProductCompareException and not OperationCanceledException)
                {
                    // Непредвиденное в данных — назвать файл и строку, а не одно сообщение .NET:
                    // по нему пользователь не поймет, где искать.
                    throw new ProductCompareException(
                        $"Не удалось разобрать файл «{Path.GetFileName(file.Path)}», лист «{SheetName}», " +
                        $"строка {rowIndex}:\n{ex.Message}");
                }
            }

            return new BreakdownSheet(file, root, styles, products, operations);
        }
    }

    /// <summary>Уровень по индексу — как <c>Calculation.GetLevel</c>: «Изделие» — 0, иначе число
    /// частей через точку («1» и «1.» — 1, «1.2» — 2).</summary>
    public static int Level(string index)
    {
        if (index == "Изделие") return 0;
        var dots = index.Count(ch => ch == '.');
        return index.EndsWith('.') ? dots : dots + 1;
    }

    private static string Normalize(string text) => Spaces().Replace(text, " ").Trim();

    // Файл может быть открыт в Excel у коллеги: читаем с разрешением на чужую запись и целиком
    // в память — пакет OpenXML ходит по архиву вразнобой, по сетевому диску это медленно.
    private static MemoryStream OpenShared(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                                            FileShare.ReadWrite | FileShare.Delete);
            var memory = new MemoryStream();
            file.CopyTo(memory);
            memory.Position = 0;
            return memory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProductCompareException($"Не удалось прочитать файл:\n{Path.GetFileName(path)}\n{ex.Message}");
        }
    }

    private static Worksheet? FindSheet(WorkbookPart book)
    {
        var sheet = book.Workbook.Sheets?.Elements<Sheet>()
            .FirstOrDefault(s => string.Equals(s.Name?.Value?.Trim(), SheetName, StringComparison.OrdinalIgnoreCase));
        if (sheet?.Id?.Value is not { } id) return null;
        return (book.GetPartById(id) as WorksheetPart)?.Worksheet;
    }

    private static List<string> SharedStrings(WorkbookPart book)
    {
        var table = book.SharedStringTablePart?.SharedStringTable;
        return table is null ? [] : table.Elements<SharedStringItem>().Select(PlainText).ToList();
    }

    /// <summary>Текст строки без фонетических подсказок (<c>rPh</c>): прямой <c>t</c> и <c>t</c>
    /// внутри кусков форматированного текста.</summary>
    internal static string PlainText(OpenXmlElement item)
    {
        var text = new StringBuilder();
        foreach (var child in item.ChildElements)
        {
            if (child is Text t) text.Append(t.Text);
            else if (child is Run run)
                foreach (var part in run.Elements<Text>()) text.Append(part.Text);
        }
        return text.ToString();
    }

    // Номер строки и столбца в XML необязателен: без него элемент стоит следом за предыдущим.
    private static IEnumerable<(uint Index, Row Row)> Rows(Worksheet sheet)
    {
        uint previous = 0;
        foreach (var row in sheet.GetFirstChild<SheetData>()?.Elements<Row>() ?? [])
        {
            previous = row.RowIndex?.Value ?? previous + 1;
            yield return (previous, row);
        }
    }

    private static SourceCell[] ReadCells(Row row, List<string> strings)
    {
        var cells = new SourceCell[SourceColumns.Length];
        Array.Fill(cells, SourceCell.Missing);
        var maxColumn = SourceColumns[^1];
        int previous = 0;
        foreach (var cell in row.Elements<Cell>())
        {
            var column = cell.CellReference?.Value is { } reference ? CellRef.Column(reference) : previous + 1;
            previous = column;
            if (column > maxColumn) break;

            var slot = Array.IndexOf(SourceColumns, column);
            if (slot >= 0) cells[slot] = ReadCell(cell, strings);
        }
        return cells;
    }

    private static SourceCell ReadCell(Cell cell, List<string> strings)
    {
        var style = cell.StyleIndex?.Value ?? 0;
        var type  = cell.DataType?.Value;
        var raw   = cell.CellValue?.Text ?? "";

        if (type == CellValues.InlineString)
            return Text(cell.InlineString is { } inline ? PlainText(inline) : "", style);
        if (type == CellValues.SharedString)
            return Text(int.TryParse(raw, out var i) && i >= 0 && i < strings.Count ? strings[i] : "", style);
        if (raw.Length == 0)
            return new SourceCell(SourceKind.Empty, "", style);
        if (type == CellValues.String)  return Text(raw, style);       // результат формулы-строки
        if (type == CellValues.Boolean) return new SourceCell(SourceKind.Bool,  raw, style);
        if (type == CellValues.Error)   return new SourceCell(SourceKind.Error, raw, style);
        if (type == CellValues.Date)    return new SourceCell(SourceKind.Date,  raw, style);
        return new SourceCell(SourceKind.Number, raw, style);
    }

    private static SourceCell Text(string text, uint style) =>
        new(text.Length == 0 ? SourceKind.Empty : SourceKind.Text, text, style);
}

/// <summary>Адреса ячеек: «AB12» ↔ номер столбца.</summary>
internal static class CellRef
{
    /// <summary>Номер столбца (1-based) по адресу ячейки или столбца: «C7» → 3, «AB» → 28.</summary>
    public static int Column(string reference)
    {
        var column = 0;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z')      column = column * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') column = column * 26 + (ch - 'a' + 1);
            else break;
        }
        return column;
    }

    /// <summary>Номер строки по адресу: «C7» → 7 (без цифр — 0).</summary>
    public static uint Row(string reference)
    {
        var digits = reference.SkipWhile(char.IsLetter).TakeWhile(char.IsDigit).ToArray();
        return uint.TryParse(new string(digits), out var row) ? row : 0;
    }

    /// <summary>Буквы столбца: 3 → «C», 28 → «AB».</summary>
    public static string Name(int column)
    {
        var name = new StringBuilder();
        while (column > 0)
        {
            var rest = (column - 1) % 26;
            name.Insert(0, (char)('A' + rest));
            column = (column - 1) / 26;
        }
        return name.ToString();
    }

    public static string Of(int column, uint row) => Name(column) + row;
}
