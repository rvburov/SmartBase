using ClosedXML.Excel;

namespace DataBase.Core.Importers;

/// <summary>Ошибка импорта с дружелюбным текстом — показывается пользователю в баннере диалога.</summary>
public sealed class ExcelImportException(string message) : Exception(message);

/// <summary>Готовые к записи данные импорта: подписи системных колонок, имена свойств и записи.</summary>
public sealed record ExcelImportData(
    string? TitleHeader,
    string? DescHeader,
    IReadOnlyList<string> FieldNames,
    IReadOnlyList<ExcelImportRow> Rows);

/// <summary>Одна запись: название, описание и значения свойств (в порядке FieldNames).</summary>
public sealed record ExcelImportRow(string Title, string? Description, IReadOnlyList<string> FieldValues);

/// <summary>
/// Чтение листа Excel (.xlsx/.xlsm) в структуру для создания коллекции.
/// Каждый столбец используемого диапазона = свойство: 1-й (заданный) → название записи,
/// 2-й (заданный) → описание, остальные → текстовые свойства. Имена свойств берутся из
/// строки заголовков; пустые/повторяющиеся приводятся к уникальным («Без названия N»).
/// Данные читаются со строки сразу после заголовка; всё сохраняется как текст.
/// </summary>
public static class ExcelImporter
{
    /// <param name="SheetName">Название листа; пусто — первый лист.</param>
    /// <param name="HeaderRow">Номер строки заголовков (≥ 1).</param>
    /// <param name="TitleColumn">Столбец названия записи — число («1») или буква («A»).</param>
    /// <param name="DescColumn">Столбец описания — число или буква.</param>
    public sealed record Spec(string FilePath, string? SheetName, int HeaderRow,
                              string TitleColumn, string DescColumn);

    public static ExcelImportData Parse(Spec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.FilePath))
            throw new ExcelImportException("Укажите файл для импорта.");
        if (!File.Exists(spec.FilePath))
            throw new ExcelImportException("Файл не найден.");
        if (spec.HeaderRow < 1)
            throw new ExcelImportException("Строка заголовков должна быть ≥ 1.");

        var titleCol = ParseColumn(spec.TitleColumn, "Столбец названия");
        var descCol  = ParseColumn(spec.DescColumn,  "Столбец описания");
        if (titleCol == descCol)
            throw new ExcelImportException("Столбец названия и столбец описания не должны совпадать.");

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

            // Столбцы-свойства = весь используемый диапазон, кроме названия и описания.
            var fieldCols = new List<int>();
            for (var c = firstCol; c <= lastCol; c++)
                if (c != titleCol && c != descCol)
                    fieldCols.Add(c);

            // Имена свойств из строки заголовков: пустые → «Без названия N», уникальность гарантируется.
            string Header(int col) => sheet.Cell(spec.HeaderRow, col).GetFormattedString().Trim();

            var named = new List<(int Col, string Raw)> { (titleCol, Header(titleCol)), (descCol, Header(descCol)) };
            named.AddRange(fieldCols.Select(c => (c, Header(c))));
            named.Sort((a, b) => a.Col.CompareTo(b.Col));
            var nameByCol = AssignUniqueNames(named);

            // Данные записей — со строки сразу после заголовка.
            string Cell(int row, int col) => sheet.Cell(row, col).GetFormattedString();

            var rawRows = new List<(string Title, string Desc, List<string> Vals)>();
            for (var r = spec.HeaderRow + 1; r <= lastRow; r++)
            {
                var vals = fieldCols.Select(c => Cell(r, c)).ToList();
                rawRows.Add((Cell(r, titleCol), Cell(r, descCol), vals));
            }

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

                rows.Add(new ExcelImportRow(title, string.IsNullOrEmpty(x.Desc) ? null : x.Desc, x.Vals));
            }

            var fieldNames = fieldCols.Select(c => nameByCol[c]).ToList();
            return new ExcelImportData(nameByCol[titleCol], nameByCol[descCol], fieldNames, rows);
        }
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
