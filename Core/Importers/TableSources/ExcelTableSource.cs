using ExcelDataReader;
using Serilog;

namespace SmartBase.Core.Importers.TableSources;

/// <summary>
/// Excel-источник (.xlsx/.xlsm/.xlsb/.xls) на ExcelDataReader: потоковое чтение строк без
/// материализации книги в память. Скрытые строки, столбцы и листы читаются наравне с видимыми.
/// Границы (первая/последняя колонка, последняя строка) — по реально непустым ячейкам: dimension
/// листа может включать пустые отформатированные хвосты (заливка/рамки на весь столбец), и без
/// обрезки импорт раздувается лишними столбцами и строками. Исключения ручного режима
/// (листы/строки/столбцы) не участвуют ни в данных, ни в границах; заголовки читаются даже из
/// исключенной строки (исключения действуют на данные). Проход 2 переоткрывает файл — надежнее,
/// чем Reset() (особенно на .xlsb), и стоит дешево.
/// </summary>
public sealed class ExcelTableSource(string path, string? sheetName, int headerRow, int dataRow, int titleCol,
    RangeSet excludeRows, RangeSet excludeCols, HashSet<string> excludeSheets) : ITableSource
{
    private long      _lastRow;
    private List<int> _fieldCols = [];

    /// <summary>Статистика одного прохода по листу (общая для одиночного и merge-источников).
    /// DataRows — число неисключенных строк в диапазоне данных (для процентов записи).</summary>
    internal sealed record SheetStat(long LastRow, int FirstCol, int LastCol, string[]? Headers,
                                     long DataRows, long EmptyTitles);

    public ImportSchema Analyze(IProgress<double>? progress, CancellationToken ct)
    {
        using var fs = ImportErrors.OpenRead(path);
        using var reader = CreateReader(fs);
        MoveToSheet(reader, sheetName, excludeSheets);

        var stat = ScanSheet(reader, headerRow, dataRow, titleCol, excludeRows, excludeCols,
                             fs, SourceHelpers.ForwardOnly(progress), ct);
        if (stat.LastRow == 0)
            throw new ImportException(string.IsNullOrWhiteSpace(sheetName)
                ? "Лист пустой — нет данных для импорта."
                : $"Лист «{sheetName!.Trim()}» пустой — нет данных для импорта.");

        _lastRow   = stat.LastRow;
        _fieldCols = FieldCols(stat.FirstCol, stat.LastCol, titleCol, excludeCols);

        var nameByCol = NamesFor(stat.Headers, titleCol, _fieldCols);
        return new ImportSchema(nameByCol[titleCol],
                                _fieldCols.Select(c => nameByCol[c]).ToList(),
                                stat.DataRows,
                                stat.DataRows == 0 ? 0 : stat.EmptyTitles);
    }

    public IEnumerable<ImportRow> ReadRows(CancellationToken ct)
    {
        using var fs = ImportErrors.OpenRead(path);
        using var reader = CreateReader(fs);
        MoveToSheet(reader, sheetName, excludeSheets);

        long row = 0;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            row++;
            if (row < dataRow) continue;
            if (row > _lastRow) yield break;
            if (excludeRows.Contains(row)) continue;
            yield return ReadRow(reader, _fieldCols, titleCol);
        }
    }

    /// <summary>Имена листов книги в порядке следования (для «каждый лист — своя коллекция»).
    /// Скрытые листы включаются.</summary>
    public static List<string> SheetNames(string path)
    {
        using var fs = ImportErrors.OpenRead(path);
        using var reader = CreateReader(fs);
        var names = new List<string>();
        do names.Add(reader.Name); while (reader.NextResult());
        return names;
    }

    // ── Общее для одиночного и merge-источников ───────────────────────────────

    internal static IExcelDataReader CreateReader(FileStream fs)
    {
        EncodingSetup.Ensure();
        if (fs.Length == 0) throw new ImportException("Файл пустой — нет данных для импорта.");
        try { return ExcelReaderFactory.CreateReader(fs); }
        catch (Exception ex) when (ex is not ImportException)
        {
            // Сырая ошибка открытия оборачивается в дружелюбную мимо ImportErrors.Map —
            // поэтому логируем здесь, иначе причина (повреждение/формат) нигде не осталась бы.
            Log.Warning(ex, "Не удалось открыть файл Excel для импорта");
            throw new ImportException(
                "Не удалось открыть файл. Проверьте, что это корректный файл Excel " +
                "(.xlsx, .xlsm, .xlsb, .xls) и он не поврежден.");
        }
    }

    internal static void MoveToSheet(IExcelDataReader reader, string? sheetName, HashSet<string> excludeSheets)
    {
        if (string.IsNullOrWhiteSpace(sheetName))
        {
            // Первый НЕисключенный лист (даже скрытый).
            while (excludeSheets.Contains(reader.Name?.Trim() ?? ""))
                if (!reader.NextResult())
                    throw new ImportException("Все листы исключены — нет данных для импорта.");
            return;
        }

        var target = sheetName.Trim();
        if (excludeSheets.Contains(target))
            throw new ImportException($"Лист «{target}» указан и в поле «Лист», и в «Исключить листы» — уберите одно из двух.");
        while (!string.Equals(reader.Name, target, StringComparison.OrdinalIgnoreCase))
            if (!reader.NextResult())
                throw new ImportException($"Лист «{target}» не найден.");
    }

    // Полный проход по текущему листу: границы по непустым НЕисключенным ячейкам, заголовки
    // (даже из исключенной строки — исключения действуют на данные), счетчики строк данных и
    // пустых названий в границах непустого хвоста (строки после последней непустой не считаются).
    // Прогресс — по позиции файла (приблизительно, только вперед).
    internal static SheetStat ScanSheet(IExcelDataReader reader, int headerRow, int dataRow, int titleCol,
        RangeSet excludeRows, RangeSet excludeCols, FileStream fs, IProgress<double>? progress, CancellationToken ct)
    {
        long row = 0, lastRow = 0, dataRows = 0, dataSnap = 0, emptyTitles = 0, emptySnap = 0;
        int firstCol = 0, lastCol = 0;
        string[]? headers = null;
        var total   = Math.Max(1L, fs.Length);
        var lastPct = -1;

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            row++;
            if (row == headerRow) headers = RowValues(reader, reader.FieldCount);

            if (!excludeRows.Contains(row))
            {
                var fc = reader.FieldCount;

                int rightmost = 0, leftmost = 0;
                for (var i = fc - 1; i >= 0; i--)
                    if (!excludeCols.Contains(i + 1) && !IsEmpty(reader, i)) { rightmost = i + 1; break; }
                if (rightmost > 0)
                    for (var i = 0; i < rightmost; i++)
                        if (!excludeCols.Contains(i + 1) && !IsEmpty(reader, i)) { leftmost = i + 1; break; }

                if (row >= dataRow)
                {
                    dataRows++;
                    var title = titleCol <= fc ? SourceHelpers.FormatExcelValue(reader.GetValue(titleCol - 1)) : "";
                    if (string.IsNullOrWhiteSpace(title)) emptyTitles++;
                }

                if (rightmost > 0)
                {
                    lastRow = row;
                    if (rightmost > lastCol) lastCol = rightmost;
                    if (firstCol == 0 || leftmost < firstCol) firstCol = leftmost;
                    dataSnap  = dataRows;
                    emptySnap = emptyTitles;
                }
            }

            if (progress is not null)
            {
                var pct = (int)(fs.Position * 100 / total);
                if (pct != lastPct) { progress.Report(Math.Min(1.0, fs.Position / (double)total)); lastPct = pct; }
            }
        }

        return new SheetStat(lastRow, Math.Max(1, firstCol), lastCol, headers, dataSnap, emptySnap);
    }

    private static bool IsEmpty(IExcelDataReader reader, int index)
    {
        var v = reader.GetValue(index);
        return v is null || SourceHelpers.FormatExcelValue(v).Length == 0;
    }

    internal static string[] RowValues(IExcelDataReader reader, int fc)
    {
        var vals = new string[fc];
        for (var i = 0; i < fc; i++) vals[i] = SourceHelpers.FormatExcelValue(reader.GetValue(i));
        return vals;
    }

    internal static List<int> FieldCols(int firstCol, int lastCol, int titleCol, RangeSet excludeCols)
    {
        var cols = new List<int>();
        for (var c = firstCol; c <= lastCol; c++)
            if (c != titleCol && !excludeCols.Contains(c))
                cols.Add(c);
        return cols;
    }

    internal static Dictionary<int, string> NamesFor(string[]? headers, int titleCol, List<int> fieldCols)
    {
        string Header(int col) => headers is not null && col <= headers.Length ? (headers[col - 1] ?? "").Trim() : "";
        var named = new List<(int Col, string Raw)> { (titleCol, Header(titleCol)) };
        named.AddRange(fieldCols.Select(c => (c, Header(c))));
        named.Sort((a, b) => a.Col.CompareTo(b.Col));
        return SourceHelpers.AssignUniqueNames(named);
    }

    internal static ImportRow ReadRow(IExcelDataReader reader, List<int> fieldCols, int titleCol)
    {
        var fc = reader.FieldCount;
        string Val(int col) => col <= fc ? SourceHelpers.FormatExcelValue(reader.GetValue(col - 1)) : "";

        var vals = new string[fieldCols.Count];
        for (var i = 0; i < fieldCols.Count; i++) vals[i] = Val(fieldCols[i]);
        return new ImportRow(Val(titleCol), vals);
    }
}
