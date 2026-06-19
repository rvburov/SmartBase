using System.Text;
using ClosedXML.Excel;

namespace DataBase.Core.Importers;

/// <summary>Ошибка импорта с дружелюбным текстом — показывается пользователю в баннере диалога.</summary>
public sealed class ExcelImportException(string message) : Exception(message);

/// <summary>Готовые к записи данные импорта: подпись названия, имена свойств и записи.</summary>
public sealed record ExcelImportData(
    string? TitleHeader,
    IReadOnlyList<string> FieldNames,
    IReadOnlyList<ExcelImportRow> Rows);

/// <summary>Одна запись: название и значения свойств (в порядке FieldNames). Описание не импортируется.</summary>
public sealed record ExcelImportRow(string Title, IReadOnlyList<string> FieldValues);

/// <summary>
/// Чтение таблицы (.xlsx/.xlsm через ClosedXML, .csv своим парсером) в структуру для
/// создания коллекции. Заданный столбец → название записи, остальные столбцы используемого
/// диапазона → текстовые свойства. Колонка «Описание» не импортируется (создаётся пустой
/// отдельно). Имена свойств берутся из строки заголовков; пустые/повторяющиеся приводятся к
/// уникальным («Без названия N»). Данные — со строки данных, всё сохраняется как текст.
/// </summary>
public static class ExcelImporter
{
    /// <param name="SheetName">Название листа; пусто — первый лист. Для CSV игнорируется.</param>
    /// <param name="HeaderRow">Номер строки заголовков (≥ 1).</param>
    /// <param name="DataRow">Номер строки, с которой начинаются данные (≥ 1).</param>
    /// <param name="TitleColumn">Столбец названия записи — число («1») или буква («A»).</param>
    public sealed record Spec(string FilePath, string? SheetName, int HeaderRow, int DataRow, string TitleColumn);

    public static ExcelImportData Parse(Spec spec, IProgress<double>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(spec.FilePath))
            throw new ExcelImportException("Укажите файл для импорта.");
        if (!File.Exists(spec.FilePath))
            throw new ExcelImportException("Файл не найден.");
        if (spec.HeaderRow < 1)
            throw new ExcelImportException("Строка заголовков должна быть ≥ 1.");
        if (spec.DataRow < 1)
            throw new ExcelImportException("Строка данных должна быть ≥ 1.");

        var titleCol = ParseColumn(spec.TitleColumn, "Столбец названия");

        var ext = Path.GetExtension(spec.FilePath).ToLowerInvariant();
        return ext == ".csv" ? ParseCsv(spec, titleCol, progress) : ParseExcel(spec, titleCol, progress);
    }

    // ── Excel (.xlsx/.xlsm) ───────────────────────────────────────────────────

    private static ExcelImportData ParseExcel(Spec spec, int titleCol, IProgress<double>? progress)
    {
        XLWorkbook wb;
        try { wb = new XLWorkbook(spec.FilePath); }
        catch
        {
            throw new ExcelImportException(
                "Не удалось открыть файл. Проверьте, что это корректный Excel (.xlsx или .xlsm) " +
                "и он не открыт в другой программе.");
        }

        using (wb)
        {
            IXLWorksheet? sheet;
            if (string.IsNullOrWhiteSpace(spec.SheetName))
                sheet = wb.Worksheets.FirstOrDefault();
            else if (!wb.Worksheets.TryGetWorksheet(spec.SheetName.Trim(), out sheet))
                sheet = null;

            if (sheet is null)
                throw new ExcelImportException(string.IsNullOrWhiteSpace(spec.SheetName)
                    ? "В файле нет ни одного листа."
                    : $"Лист «{spec.SheetName.Trim()}» не найден.");

            var used = sheet.RangeUsed();
            if (used is null)
                throw new ExcelImportException("Лист пустой — нет данных для импорта.");

            var firstCol = used.RangeAddress.FirstAddress.ColumnNumber;
            var lastCol  = used.RangeAddress.LastAddress.ColumnNumber;
            var lastRow  = used.RangeAddress.LastAddress.RowNumber;

            // Читаем заполненные ячейки одним проходом в словарь — это в разы быстрее, чем
            // дёргать sheet.Cell(r,c) в цикле (он к тому же материализует пустые ячейки).
            var values = new Dictionary<(int Row, int Col), string>();
            foreach (var cell in used.CellsUsed())
                values[(cell.Address.RowNumber, cell.Address.ColumnNumber)] = cell.GetFormattedString();

            string Cell(int row, int col) => values.TryGetValue((row, col), out var v) ? v : "";
            return BuildImportData(spec, titleCol, firstCol, lastCol, lastRow, Cell, progress);
        }
    }

    // ── CSV ───────────────────────────────────────────────────────────────────

    private static ExcelImportData ParseCsv(Spec spec, int titleCol, IProgress<double>? progress)
    {
        string text;
        try { text = File.ReadAllText(spec.FilePath); }   // UTF-8 с авто-определением BOM
        catch { throw new ExcelImportException("Не удалось прочитать CSV-файл."); }

        var grid = ParseCsvText(text, SniffDelimiter(text));
        // Хвостовые пустые строки (артефакт конца файла) отбрасываем — как делает Excel.
        while (grid.Count > 0 && grid[^1].All(string.IsNullOrWhiteSpace))
            grid.RemoveAt(grid.Count - 1);

        if (grid.Count == 0)
            throw new ExcelImportException("Файл пустой — нет данных для импорта.");

        var lastRow = grid.Count;
        var lastCol = grid.Max(r => r.Length);

        string Cell(int row, int col)
        {
            if (row < 1 || row > grid.Count) return "";
            var r = grid[row - 1];
            return col >= 1 && col <= r.Length ? r[col - 1] ?? "" : "";
        }

        return BuildImportData(spec, titleCol, firstCol: 1, lastCol, lastRow, Cell, progress);
    }

    // Определяем разделитель по первой непустой строке: ; (частый в RU-Excel), таб или ,.
    private static char SniffDelimiter(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        int Count(char c) => line.Count(ch => ch == c);
        var semicolons = Count(';');
        var tabs       = Count('\t');
        var commas     = Count(',');
        if (semicolons > 0 && semicolons >= commas && semicolons >= tabs) return ';';
        if (tabs > 0 && tabs >= commas) return '\t';
        return ',';
    }

    // Разбор CSV по RFC 4180: кавычки экранируют разделитель/перенос строки, "" → ".
    private static List<string[]> ParseCsvText(string text, char delim)
    {
        var rows     = new List<string[]>();
        var row      = new List<string>();
        var field    = new StringBuilder();
        var inQuotes = false;

        void EndField()      { row.Add(field.ToString()); field.Clear(); }
        void EndRow()        { EndField(); rows.Add(row.ToArray()); row = new List<string>(); }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }

            switch (ch)
            {
                case '"':  inQuotes = true; break;
                case '\r': EndRow(); if (i + 1 < text.Length && text[i + 1] == '\n') i++; break;
                case '\n': EndRow(); break;
                default:
                    if (ch == delim) EndField();
                    else field.Append(ch);
                    break;
            }
        }

        // Последнее поле/строка без завершающего перевода строки.
        if (field.Length > 0 || row.Count > 0) EndRow();

        return rows;
    }

    // ── Общий маппинг сетки в данные импорта ──────────────────────────────────

    private static ExcelImportData BuildImportData(Spec spec, int titleCol,
        int firstCol, int lastCol, int lastRow, Func<int, int, string> cell,
        IProgress<double>? progress)
    {
        // Столбцы-свойства = весь используемый диапазон, кроме столбца названия.
        var fieldCols = new List<int>();
        for (var c = firstCol; c <= lastCol; c++)
            if (c != titleCol)
                fieldCols.Add(c);

        // Имена свойств из строки заголовков: пустые → «Без названия N», уникальность гарантируется.
        string Header(int col) => cell(spec.HeaderRow, col).Trim();

        var named = new List<(int Col, string Raw)> { (titleCol, Header(titleCol)) };
        named.AddRange(fieldCols.Select(c => (c, Header(c))));
        named.Sort((a, b) => a.Col.CompareTo(b.Col));
        var nameByCol = AssignUniqueNames(named);

        // Данные записей — со строки данных. По ходу отчитываемся о прогрессе чтения
        // (троттлинг по целому проценту, чтобы не заваливать UI сообщениями).
        var rawRows  = new List<(string Title, List<string> Vals)>();
        var totalRows = Math.Max(1, lastRow - spec.DataRow + 1);
        var processed = 0;
        var lastPct   = -1;
        for (var r = spec.DataRow; r <= lastRow; r++)
        {
            var vals = fieldCols.Select(c => cell(r, c)).ToList();
            rawRows.Add((cell(r, titleCol), vals));

            processed++;
            var pct = processed * 100 / totalRows;
            if (progress is not null && pct != lastPct) { progress.Report(processed / (double)totalRows); lastPct = pct; }
        }
        progress?.Report(1.0);

        // Пустое название → «Без названия»; если таких несколько — нумеруем.
        var emptyTitles = rawRows.Count(x => string.IsNullOrWhiteSpace(x.Title));
        var rows = new List<ExcelImportRow>(rawRows.Count);
        var ti = 0;
        foreach (var x in rawRows)
        {
            string title;
            if (string.IsNullOrWhiteSpace(x.Title))
            {
                ti++;
                title = emptyTitles == 1 ? "Без названия" : $"Без названия {ti}";
            }
            else title = x.Title;

            rows.Add(new ExcelImportRow(title, x.Vals));
        }

        var fieldNames = fieldCols.Select(c => nameByCol[c]).ToList();
        return new ExcelImportData(nameByCol[titleCol], fieldNames, rows);
    }

    // «1»/«A» → номер столбца (1-based). Бросает дружелюбную ошибку при неверном вводе.
    private static int ParseColumn(string raw, string label)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0)
            throw new ExcelImportException($"{label}: укажите номер (например, 1) или букву (например, A).");

        if (int.TryParse(s, out var num))
        {
            if (num < 1) throw new ExcelImportException($"{label}: номер столбца должен быть ≥ 1.");
            return num;
        }

        if (s.All(char.IsLetter))
        {
            try { return XLHelper.GetColumnNumberFromLetter(s.ToUpperInvariant()); }
            catch { /* провалимся в ошибку ниже */ }
        }

        throw new ExcelImportException($"{label}: укажите номер (например, 1) или букву (например, A).");
    }

    // Имена в порядке столбцов: пустые → «Без названия» (или «Без названия 1/2…» при нескольких),
    // повторы дедуплицируются добавлением номера. Возвращает «номер столбца → итоговое имя».
    private static Dictionary<int, string> AssignUniqueNames(List<(int Col, string Raw)> entries)
    {
        var emptyCount = entries.Count(e => string.IsNullOrEmpty(e.Raw));
        var used   = new HashSet<string>(StringComparer.Ordinal);
        var result = new Dictionary<int, string>();
        var emptyIdx = 0;

        foreach (var (col, raw) in entries)
        {
            string baseName;
            if (string.IsNullOrEmpty(raw))
            {
                emptyIdx++;
                baseName = emptyCount == 1 ? "Без названия" : $"Без названия {emptyIdx}";
            }
            else baseName = raw;

            var name = baseName;
            var n = 2;
            while (!used.Add(name)) name = $"{baseName} {n++}";
            result[col] = name;
        }

        return result;
    }
}
