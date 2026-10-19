using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SmartBase.Extensions.ProductCompare;

/// <summary>
/// Запись сравнения расшифровок в копию шаблона. Шаблон копируется целиком, а в копии
/// меняется только содержимое ячеек первого листа (OpenXML), поэтому кнопки на листе, макросы
/// и прочие части книги доходят до результата нетронутыми. ClosedXML здесь не годится: при
/// сохранении он теряет элементы управления на листе — кнопку «Создать уровни» шаблона.
///
/// Раскладка шаблона: блок — столбцы A–I (уровень, индекс, наименование, обозначение КД,
/// обозначение ТД, кол-во, Т (1 шт.), Т всего, комментарий), за ним пустой столбец; строки
/// 1–5 — шапка (в C1 — заголовок блока, в A1:B1 — кнопка), данные с 6-й строки. Блоки дат
/// идут вправо через пустой столбец; шапка повторяется над каждым, кроме места кнопки.
/// </summary>
internal static class BreakdownWriter
{
    public const int  BlockColumns = 9;
    public const int  BlockStep    = BlockColumns + 1;
    public const uint HeaderRows   = 5;
    public const uint FirstDataRow = HeaderRows + 1;
    public const int  TitleColumn  = 3;          // C1 — заголовок блока: имя файла
    private const int ButtonColumns = 2;         // A1:B1 — кнопка шаблона, над другими блоками не повторяется

    public static void Write(string templatePath, string outputPath,
                             IReadOnlyList<BreakdownSheet> sheets, MergedList list,
                             IProgress<int>? progress, int fromPct, int toPct, CancellationToken ct)
    {
        using var memory = ReadTemplate(templatePath);
        using (var doc = OpenTemplate(memory))
        {
            var book  = doc.WorkbookPart!;
            var sheet = book.Workbook.Sheets?.Elements<Sheet>().FirstOrDefault()
                        ?? throw new ProductCompareException("В файле-шаблоне нет ни одного листа.");
            if (sheet.Id?.Value is not { } id || book.GetPartById(id) is not WorksheetPart part)
                throw new ProductCompareException("Первый лист файла-шаблона — не таблица.");
            var styles = book.WorkbookStylesPart?.Stylesheet
                         ?? throw new ProductCompareException("В файле-шаблоне нет таблицы стилей.");

            var ws     = part.Worksheet;
            var data   = ws.GetFirstChild<SheetData>() ?? ws.AppendChild(new SheetData());
            var grid   = Grid.Load(data);
            var blocks = sheets.Count;

            // Оформление пустых клеток данных берется из 6-й строки шаблона ДО записи.
            var blankStyles = new uint?[BlockColumns];
            for (var c = 0; c < BlockColumns; c++) blankStyles[c] = grid.Get(FirstDataRow, c + 1)?.StyleIndex?.Value;
            var titleStyle = grid.Get(1, TitleColumn)?.StyleIndex?.Value;

            RepeatHeader(grid, blocks);
            for (var k = 0; k < blocks; k++)
                grid.Set(1, TitleColumn + k * BlockStep, TextCell(sheets[k].File.Title, titleStyle));

            var target  = new TargetStyles(styles);
            var copiers = sheets.Select(s => new StyleCopier(s.Styles, target)).ToArray();
            var lastRow = FirstDataRow + (uint)list.Rows.Count - 1;

            for (var i = 0; i < list.Rows.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var r     = FirstDataRow + (uint)i;
                var cells = list.Rows[i];
                for (var k = 0; k < blocks; k++)
                {
                    var source = cells[k];
                    for (var c = 0; c < BlockColumns; c++)
                    {
                        var column = c + 1 + k * BlockStep;
                        if (source is not null)
                            grid.Set(r, column, DataCell(source.Cells[c], copiers[k]));
                        else if (blankStyles[c] is { } blank)
                            grid.Set(r, column, new Cell { StyleIndex = blank });
                        else
                            grid.Remove(r, column);
                    }
                }
                SetHeight(grid.Row(r), cells.LastOrDefault(s => s is not null)?.Height);

                if (i % 200 == 0) progress?.Report(fromPct + (toPct - fromPct) * 9 / 10 * (i + 1) / list.Rows.Count);
            }

            var lastColumn = (blocks - 1) * BlockStep + BlockColumns;
            RepeatMerges(ws, blocks, lastRow, lastColumn);
            RepeatColumns(ws, blocks);
            grid.SaveTo(data);
            ExtendFilter(ws, book, sheet, lastColumn, lastRow);
            if (ws.GetFirstChild<SheetDimension>() is { } dimension)
                dimension.Reference = $"A1:{CellRef.Of(Math.Max(lastColumn, grid.MaxColumn), Math.Max(lastRow, grid.MaxRow))}";

            // Цепочка вычислений шаблона может ссылаться на ячейки, где теперь значения вместо
            // формул, — Excel счел бы такую книгу поврежденной. Без цепочки он строит ее сам.
            if (book.CalculationChainPart is { } chain) book.DeletePart(chain);
        }
        progress?.Report(toPct);

        try
        {
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
            memory.Position = 0;
            memory.CopyTo(output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { /* best-effort */ }
            throw new ProductCompareException(
                $"Не удалось сохранить результат:\n{ex.Message}\nПроверьте права на папку и что файл не открыт.");
        }
    }

    // ── Шаблон ────────────────────────────────────────────────────────────────

    // Шаблон лежит на файловом сервере и может быть открыт у коллеги: читаем с разрешением на
    // чужую запись и правим копию в памяти — сам файл шаблона не меняется.
    private static MemoryStream ReadTemplate(string path)
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
            throw new ProductCompareException($"Не удалось прочитать файл-шаблон:\n{ex.Message}");
        }
    }

    private static SpreadsheetDocument OpenTemplate(MemoryStream memory)
    {
        try
        {
            var doc = SpreadsheetDocument.Open(memory, true);
            if (doc.WorkbookPart is null) throw new InvalidDataException();
            return doc;
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or IOException or FileFormatException)
        {
            throw new ProductCompareException("Не удалось открыть файл-шаблон: это не книга Excel или файл поврежден.");
        }
    }

    // ── Ячейки ────────────────────────────────────────────────────────────────

    private static Cell DataCell(SourceCell source, StyleCopier styles)
    {
        var cell  = new Cell();
        var style = styles.Map(source.Style);
        if (style != 0) cell.StyleIndex = style;

        switch (source.Kind)
        {
            case SourceKind.Number:
                cell.CellValue = new CellValue(source.Value);
                break;
            case SourceKind.Text:
                cell.DataType     = CellValues.InlineString;
                cell.InlineString = new InlineString(new Text(source.Value) { Space = SpaceProcessingModeValues.Preserve });
                break;
            case SourceKind.Bool:
                cell.DataType  = CellValues.Boolean;
                cell.CellValue = new CellValue(source.Value);
                break;
            case SourceKind.Error:
                cell.DataType  = CellValues.Error;
                cell.CellValue = new CellValue(source.Value);
                break;
            case SourceKind.Date:
                cell.DataType  = CellValues.Date;
                cell.CellValue = new CellValue(source.Value);
                break;
        }
        return cell;
    }

    private static Cell TextCell(string text, uint? style)
    {
        var cell = new Cell
        {
            DataType     = CellValues.InlineString,
            InlineString = new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve }),
        };
        if (style is > 0) cell.StyleIndex = style;
        return cell;
    }

    private static void SetHeight(Row row, double? height)
    {
        if (height is { } h) { row.Height = h; row.CustomHeight = true; }
        else                 { row.Height = null; row.CustomHeight = null; }
    }

    // ── Шапка, объединения, ширины, фильтр ────────────────────────────────────

    private static bool InButton(uint row, int column) => row == 1 && column <= ButtonColumns;

    private static void RepeatHeader(Grid grid, int blocks)
    {
        for (uint r = 1; r <= HeaderRows; r++)
            for (var c = 1; c <= BlockColumns; c++)
            {
                if (InButton(r, c) || grid.Get(r, c) is not { } original) continue;
                for (var k = 1; k < blocks; k++)
                {
                    var copy = (Cell)original.CloneNode(true);
                    copy.CellFormula = null;      // формула шапки ссылалась бы на столбцы первого блока
                    grid.Set(r, c + k * BlockStep, copy);
                }
            }
    }

    private static void RepeatMerges(Worksheet ws, int blocks, uint lastRow, int lastColumn)
    {
        if (ws.GetFirstChild<MergeCells>() is not { } merges) return;

        foreach (var merge in merges.Elements<MergeCell>().ToList())
        {
            if (!TryRange(merge.Reference?.Value, out var range)) continue;

            // Объединения в области данных мешали бы строкам — их место занимает сравнение.
            if (range.Row2 >= FirstDataRow && range.Row1 <= lastRow && range.Col1 <= lastColumn)
            {
                merge.Remove();
                continue;
            }

            var inHeader = range.Row2 <= HeaderRows && range.Col2 <= BlockColumns && !InButton(range.Row1, range.Col1);
            if (!inHeader) continue;
            for (var k = 1; k < blocks; k++)
            {
                var shift = k * BlockStep;
                merges.Append(new MergeCell
                {
                    Reference = $"{CellRef.Of(range.Col1 + shift, range.Row1)}:{CellRef.Of(range.Col2 + shift, range.Row2)}",
                });
            }
        }

        if (merges.ChildElements.Count == 0) merges.Remove();
        else merges.Count = (uint)merges.ChildElements.Count;
    }

    // Ширины столбцов блока и пустого столбца за ним повторяются у каждого блока; описания
    // правее последнего блока остаются как были.
    private static void RepeatColumns(Worksheet ws, int blocks)
    {
        if (ws.GetFirstChild<Columns>() is not { } columns) return;

        var original = columns.Elements<Column>().ToList();
        var covered  = (uint)(blocks * BlockStep);
        var rebuilt  = new List<Column>();

        for (uint c = 1; c <= covered; c++)
        {
            var pattern = (uint)((c - 1) % BlockStep + 1);
            var source  = original.FirstOrDefault(d => d.Min?.Value <= pattern && pattern <= d.Max?.Value);
            if (source is null) continue;
            var copy = (Column)source.CloneNode(true);
            copy.Min = c;
            copy.Max = c;
            rebuilt.Add(copy);
        }
        foreach (var source in original.Where(d => d.Max?.Value > covered))
        {
            var copy = (Column)source.CloneNode(true);
            copy.Min = Math.Max(copy.Min?.Value ?? 1, covered + 1);
            rebuilt.Add(copy);
        }

        columns.RemoveAllChildren();
        foreach (var column in rebuilt) columns.Append(column);
        if (columns.ChildElements.Count == 0) columns.Remove();
    }

    // В Excel на листе один автофильтр: он растягивается на все блоки и все строки данных.
    private static void ExtendFilter(Worksheet ws, WorkbookPart book, Sheet sheet, int lastColumn, uint lastRow)
    {
        if (ws.GetFirstChild<AutoFilter>() is not { } filter || !TryRange(filter.Reference?.Value, out var range))
            return;

        var bottom = Math.Max(range.Row2, lastRow);
        filter.Reference = $"{CellRef.Of(range.Col1, range.Row1)}:{CellRef.Of(lastColumn, bottom)}";

        // Скрытое имя фильтра листа Excel держит в паре с самим фильтром.
        var sheetIndex = book.Workbook.Sheets!.Elements<Sheet>().ToList().IndexOf(sheet);
        var name = book.Workbook.DefinedNames?.Elements<DefinedName>().FirstOrDefault(d =>
            d.Name?.Value == "_xlnm._FilterDatabase" && d.LocalSheetId?.Value == (uint)sheetIndex);
        if (name is not null)
            name.Text = $"'{sheet.Name?.Value?.Replace("'", "''")}'!" +
                        $"${CellRef.Name(range.Col1)}${range.Row1}:${CellRef.Name(lastColumn)}${bottom}";
    }

    private readonly record struct Range(int Col1, uint Row1, int Col2, uint Row2);

    private static bool TryRange(string? reference, out Range range)
    {
        range = default;
        if (string.IsNullOrEmpty(reference)) return false;
        var parts = reference.Split(':');
        var a = parts[0];
        var b = parts.Length > 1 ? parts[1] : parts[0];
        range = new Range(CellRef.Column(a), CellRef.Row(a), CellRef.Column(b), CellRef.Row(b));
        return range.Col1 > 0 && range.Row1 > 0 && range.Col2 >= range.Col1 && range.Row2 >= range.Row1;
    }

    // ── Сетка листа ───────────────────────────────────────────────────────────

    /// <summary>
    /// Строки и ячейки листа по номерам. В XML ячейки строки обязаны идти по возрастанию
    /// столбца, а строки — по возрастанию номера; сетка держит порядок сама и собирает
    /// <c>sheetData</c> заново при сохранении. Атрибуты строк шаблона (высота, оформление)
    /// сохраняются.
    /// </summary>
    private sealed class Grid
    {
        private readonly SortedDictionary<uint, (Row Row, SortedDictionary<int, Cell> Cells)> _rows = [];

        public int  MaxColumn => _rows.Values.Select(r => r.Cells.Count > 0 ? r.Cells.Keys.Max() : 0).DefaultIfEmpty(0).Max();
        public uint MaxRow    => _rows.Count > 0 ? _rows.Keys.Max() : 0;

        public static Grid Load(SheetData data)
        {
            var grid = new Grid();
            uint previousRow = 0;
            foreach (var row in data.Elements<Row>())
            {
                var index = row.RowIndex?.Value ?? previousRow + 1;
                previousRow = index;
                var cells = grid.Entry(index, (Row)row.CloneNode(false)).Cells;

                var previousColumn = 0;
                foreach (var cell in row.Elements<Cell>())
                {
                    var column = cell.CellReference?.Value is { } reference ? CellRef.Column(reference) : previousColumn + 1;
                    previousColumn = column;
                    cells[column] = (Cell)cell.CloneNode(true);
                }
            }
            return grid;
        }

        public Row Row(uint index) => Entry(index, null).Row;

        public Cell? Get(uint row, int column) =>
            _rows.TryGetValue(row, out var entry) && entry.Cells.TryGetValue(column, out var cell) ? cell : null;

        public void Set(uint row, int column, Cell cell) => Entry(row, null).Cells[column] = cell;

        public void Remove(uint row, int column)
        {
            if (_rows.TryGetValue(row, out var entry)) entry.Cells.Remove(column);
        }

        public void SaveTo(SheetData data)
        {
            data.RemoveAllChildren();
            foreach (var (index, (row, cells)) in _rows)
            {
                row.RemoveAllChildren();
                row.RowIndex = index;
                row.Spans    = null;              // подсказка диапазона устарела бы
                foreach (var (column, cell) in cells)
                {
                    cell.CellReference = CellRef.Of(column, index);
                    row.Append(cell);
                }
                data.Append(row);
            }
        }

        private (Row Row, SortedDictionary<int, Cell> Cells) Entry(uint index, Row? shell)
        {
            if (_rows.TryGetValue(index, out var entry)) return entry;
            entry = (shell ?? new Row(), new SortedDictionary<int, Cell>());
            _rows[index] = entry;
            return entry;
        }
    }
}
