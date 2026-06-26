using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using ClosedXML.Excel;
using UglyToad.PdfPig;

namespace DataBase.Extensions.PdfAnalyzer;

/// <summary>Тип выгрузки (как в выпадающем списке оригинала).</summary>
public enum ExportType { Td, Kd, Inventory, All }

/// <summary>Ошибка анализа с дружелюбным текстом.</summary>
public sealed class PdfAnalyzeException(string message) : Exception(message);

/// <summary>Прогресс: обработано из всего + текущий файл.</summary>
public sealed record AnalyzeProgress(int Current, int Total, string File);

/// <summary>Итог анализа: путь к отчёту + сводка для показа.</summary>
public sealed record AnalyzeResult(string SavePath, int TotalFiles, int Processed, int Errors, string StatsText);

/// <summary>
/// Анализ PDF файлов: рекурсивный/локальный обход папки, разбор имени (КД/ПД/ТД через
/// <see cref="DocumentCodes"/>), MD5, число страниц и форматы листов (PdfPig), сортировка
/// (3 режима), 4 типа выгрузки и запись отчёта в Excel (ClosedXML). Порт PyQt-утилиты.
/// Логика без UI: окно вызывает <see cref="Run"/>, репортя прогресс и отменяясь по токену.
/// </summary>
public static class PdfAnalyzerEngine
{
    private sealed class Row
    {
        public int    NumPp;
        public string Name = "";
        public string DesignationTd = "";
        public string DesignationKd = "";       // КД, иначе ПД
        public string DesignationEn = "";
        public string FileKd = "";
        public string FileTd = "";
        public string Checksum = "";
        public string FileFormat = "pdf";
        public int    PageCount;
        public int    A4, A3, A2, A1, A0;        // 0 ⇒ пусто
        public object? Additional;               // string | int | null
        public int    A4Sheets;                  // 0 ⇒ пусто
        public double SizeMb;
        public string Secrecy = "н/с";
        public string Note = "";
        public string FilePath = "";
        public string Folder = "";
        public string FileName = "";
        public DateTime Mtime;
        public string RawKd = "", RawPd = "";
    }

    public static AnalyzeResult Run(
        string folderPath, string savePath, ExportType exportType, bool recursive, bool preserveOrder,
        IProgress<AnalyzeProgress>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(folderPath)) throw new PdfAnalyzeException($"Папка не существует:\n{folderPath}");

        savePath = ResolveSavePath(folderPath, savePath);

        var files = FindPdfFiles(folderPath, recursive && !preserveOrder);
        if (files.Count == 0) throw new PdfAnalyzeException("В указанной папке не найдено PDF файлов.");

        int total = files.Count;
        var rows = new List<Row>(total);
        var errors = new List<string>();
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);   // кэш названий изделий
        int processed = 0;

        foreach (var (idx, fi) in files.Select((f, i) => (i + 1, f)))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new AnalyzeProgress(idx, total, fi.FileName));

            try
            {
                var sizeMb = Math.Round(new FileInfo(fi.FullPath).Length / (1024.0 * 1024.0), 2);
                var checksum = CalculateMd5(fi.FullPath);
                var (kd, pd, td, name) = DocumentCodes.ParseFilename(fi.FileName);

                int pageCount;
                Dictionary<string, int> fmt;
                string nonStandard;
                using (var doc = PdfDocument.Open(fi.FullPath))
                {
                    pageCount = doc.NumberOfPages;
                    (fmt, nonStandard) = CountPageFormats(doc);
                }

                var row = new Row
                {
                    Name = name,
                    DesignationTd = td,
                    DesignationKd = kd.Length > 0 ? kd : pd,
                    FileKd = kd.Length > 0 ? $"{kd}.pdf" : "",
                    FileTd = td.Length > 0 ? $"{td}.pdf" : "",
                    Checksum = checksum,
                    PageCount = pageCount,
                    SizeMb = sizeMb,
                    FilePath = fi.FullPath,
                    Folder = fi.Folder,
                    FileName = fi.FileName,
                    Mtime = fi.Mtime,
                    RawKd = kd,
                    RawPd = pd,
                    A4 = fmt["A4"], A3 = fmt["A3"], A2 = fmt["A2"], A1 = fmt["A1"], A0 = fmt["A0"],
                    Additional = AdditionalValue(fmt, nonStandard),
                    A4Sheets = A4Sheets(fmt, nonStandard),
                };
                rows.Add(row);
                processed++;
                row.NumPp = processed;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{fi.FileName}: {ex.Message}");
                processed++;
                rows.Add(new Row
                {
                    Checksum = "Ошибка", FileName = fi.FileName, FilePath = fi.FullPath,
                    Folder = fi.Folder, Mtime = fi.Mtime, NumPp = processed,
                });
            }
        }

        if (processed == 0)
            throw new PdfAnalyzeException($"Не удалось обработать ни один файл. Всего: {total}, ошибок: {errors.Count}.");

        BuildDesignationCache(rows, cache);

        var ordered = SortRows(rows, exportType, recursive, preserveOrder);
        for (int i = 0; i < ordered.Count; i++) ordered[i].NumPp = i + 1;

        var columns = BuildColumns(ordered, exportType, cache);
        for (int i = 0; i < ordered.Count; i++) ordered[i].NumPp = i + 1;

        WriteExcel(savePath, ordered, columns, ct);

        var stats = BuildStats(savePath, total, processed, errors, exportType, ordered);
        return new AnalyzeResult(savePath, total, processed, errors.Count, stats);
    }

    // ── Поиск файлов ──────────────────────────────────────────────────────────

    private sealed record PdfFile(string FullPath, string FileName, string Folder, DateTime Mtime);

    private static List<PdfFile> FindPdfFiles(string folder, bool recursive)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var result = new List<PdfFile>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", option))
        {
            if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
            var dir = Path.GetDirectoryName(path) ?? folder;
            var rel = Path.GetRelativePath(folder, dir);
            if (rel == ".") rel = "";
            result.Add(new PdfFile(path, Path.GetFileName(path), rel, File.GetLastWriteTimeUtc(path)));
        }
        return result;
    }

    // ── MD5 ───────────────────────────────────────────────────────────────────

    private static string CalculateMd5(string path)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(md5.ComputeHash(stream)).ToUpperInvariant();
    }

    // ── Форматы листов ────────────────────────────────────────────────────────

    // A5 намеренно НЕ в списке: страница A5 (148×210) не совпадёт ни с базовым, ни с производным
    // форматом и попадёт в «Доп. форматы» как «148×210» (в оригинале A5 распознавался, но столбца
    // под него не было — и страница «терялась» в разбивке).
    private static readonly (string Fmt, double W, double H)[] BaseFormats =
    [
        ("A0", 841, 1189), ("A1", 594, 841), ("A2", 420, 594), ("A3", 297, 420), ("A4", 210, 297),
    ];

    private static readonly (string Fmt, double W, double H)[] AdditionalFormats =
    [
        ("A0×2", 1189, 1682), ("A0×3", 1189, 2523), ("A1×3", 841, 1783), ("A1×4", 841, 2378),
        ("A2×3", 594, 1261), ("A2×4", 594, 1682), ("A2×5", 594, 2102),
        ("A3×3", 420, 891), ("A3×4", 420, 1189), ("A3×5", 420, 1486), ("A3×6", 420, 1783), ("A3×7", 420, 2080),
        ("A4×3", 297, 630), ("A4×4", 297, 841), ("A4×5", 297, 1051), ("A4×6", 297, 1261), ("A4×7", 297, 1471),
        ("A4×8", 297, 1682), ("A4×9", 297, 1982),
    ];

    // (формат, нестандартный размер|null) по размерам в мм, допуск 30 мм.
    private static (string Fmt, string? NonStandard) DeterminePageFormat(double width, double height)
    {
        double w = Math.Min(width, height), h = Math.Max(width, height);
        const double tol = 30.0;

        foreach (var (fmt, sw, sh) in BaseFormats)
            if ((Math.Abs(w - sw) <= tol && Math.Abs(h - sh) <= tol) || (Math.Abs(w - sh) <= tol && Math.Abs(h - sw) <= tol))
                return (fmt, null);

        foreach (var (fmt, sw, sh) in AdditionalFormats)
            if ((Math.Abs(w - sw) <= tol && Math.Abs(h - sh) <= tol) || (Math.Abs(w - sh) <= tol && Math.Abs(h - sw) <= tol))
                return ("Доп. форматы", fmt);

        return ("Доп. форматы", $"{(int)w}×{(int)h}");
    }

    private static (Dictionary<string, int> Counts, string NonStandard) CountPageFormats(PdfDocument doc)
    {
        var counts = new Dictionary<string, int> { ["A4"] = 0, ["A3"] = 0, ["A2"] = 0, ["A1"] = 0, ["A0"] = 0, ["Доп. форматы"] = 0 };
        var nonStd = new Dictionary<string, int>();

        foreach (var page in doc.GetPages())
        {
            // PdfPig отдаёт размеры в пунктах (1/72"). Переводим в мм и округляем.
            var wMm = (int)Math.Round(page.Width * 25.4 / 72.0);
            var hMm = (int)Math.Round(page.Height * 25.4 / 72.0);

            var (fmt, nonStandard) = DeterminePageFormat(wMm, hMm);
            if (counts.ContainsKey(fmt)) counts[fmt]++;
            if (nonStandard is not null) nonStd[nonStandard] = nonStd.GetValueOrDefault(nonStandard) + 1;
        }

        var parts = nonStd.Select(kv => kv.Value > 1 ? $"{kv.Key} ({kv.Value} шт.)" : kv.Key);
        return (counts, string.Join(", ", parts));
    }

    // Значение столбца «Доп. форматы»: строка нестандартных размеров, либо счётчик, либо пусто.
    private static object? AdditionalValue(Dictionary<string, int> fmt, string nonStandard)
    {
        if (nonStandard.Length > 0)
        {
            if (nonStandard.Contains('×') && !nonStandard.StartsWith("A"))
                return nonStandard;
            return fmt["Доп. форматы"] > 0 ? fmt["Доп. форматы"] : "";
        }
        return fmt["Доп. форматы"] > 0 ? fmt["Доп. форматы"] : "";
    }

    private static readonly Dictionary<string, int> A4Multiplier = new()
    {
        ["A4×3"] = 3, ["A4×4"] = 4, ["A4×5"] = 5, ["A4×6"] = 6, ["A4×7"] = 7, ["A4×8"] = 8, ["A4×9"] = 9,
        ["A3×3"] = 6, ["A3×4"] = 8, ["A3×5"] = 10, ["A3×6"] = 12, ["A3×7"] = 14,
        ["A2×3"] = 12, ["A2×4"] = 16, ["A2×5"] = 20, ["A1×3"] = 24, ["A1×4"] = 32, ["A0×2"] = 32, ["A0×3"] = 48,
    };

    private static int A4Sheets(Dictionary<string, int> fmt, string nonStandard)
    {
        int s = fmt["A4"] + fmt["A3"] * 2 + fmt["A2"] * 4 + fmt["A1"] * 8 + fmt["A0"] * 16;

        if (fmt["Доп. форматы"] > 0 && nonStandard.Length > 0)
            foreach (var part in nonStandard.Split(", "))
            {
                int count = 1;
                var fmtName = part;
                if (part.Contains('(') && part.Contains("шт."))
                {
                    var inside = part.Split('(')[1].Split(' ')[0];
                    if (int.TryParse(inside, out var c)) { count = c; fmtName = part.Split('(')[0].Trim(); }
                }
                if (A4Multiplier.TryGetValue(fmtName, out var m)) s += count * m;
            }
        return s;
    }

    // ── Кэш названий изделий (designations_map) ───────────────────────────────

    private static void BuildDesignationCache(List<Row> rows, Dictionary<string, string> cache)
    {
        var known = new HashSet<string>(DocumentCodes.KdNameMapping.Keys, StringComparer.Ordinal);
        foreach (var pattern in DocumentCodes.PdNameMapping.Keys)
        {
            var code = pattern.Replace("XXXXX", "");
            if (code.Length > 0) known.Add(code);
        }
        foreach (var c in new[] { "-ЛУ", "-УЛ", "ЛУ", "УЛ", "НДЗ", "НД", "НДЗ-УЛ", "НД-УЛ", "-01" }) known.Add(c);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var designation = r.RawKd.Length > 0 ? r.RawKd : r.RawPd;
            if (designation.Length == 0) continue;

            var cleanName = r.Name;
            if (cleanName.Length == 0)
            {
                var t = r.FileName.Replace(designation, "").Replace(".pdf", "").Trim();
                cleanName = Regex.Replace(t, @"[()\[\]_\-]+$", "").Trim();
            }

            var hasCode = known.Any(code => code.Length > 0 && (designation.Contains($"-{code}") || designation.EndsWith(code)));
            if (Regex.IsMatch(designation, @"[A-ZА-Я]{4}\.\d{5}-\d{2}\s+\d{2}\s+\d{2}")) hasCode = true;

            if (!hasCode && cleanName.Length > 0)
                map[designation] = cleanName;
            else if (hasCode && cleanName.Length > 0)
            {
                var baseDes = DocumentCodes.GetBaseDesignation(designation);
                if (!map.ContainsKey(baseDes)) map[baseDes] = cleanName;
            }
        }
        foreach (var (k, v) in map) cache[k] = v;
    }

    // ── Сортировка ────────────────────────────────────────────────────────────

    private static List<Row> SortRows(List<Row> rows, ExportType exportType, bool recursive, bool preserveOrder)
    {
        if (preserveOrder)
            return rows.OrderBy(r => r.Mtime).ToList();

        if (recursive)
        {
            if (exportType is ExportType.Kd or ExportType.Inventory or ExportType.All)
            {
                return rows
                    .Select(r => (Row: r, Key: SortKey(r)))
                    .OrderBy(x => x.Key.Folder, StringComparer.Ordinal)
                    .ThenBy(x => x.Key.Base, StringComparer.Ordinal)
                    .ThenBy(x => x.Key.Exec)
                    .ThenBy(x => x.Key.Order)
                    .ThenBy(x => x.Key.Suffix)
                    .ThenBy(x => x.Key.Designation, StringComparer.Ordinal)
                    .Select(x => x.Row).ToList();
            }
            return rows.OrderBy(r => r.Folder, StringComparer.Ordinal)
                       .ThenBy(r => r.FileName, StringComparer.Ordinal).ToList();
        }

        return rows.OrderBy(r => r.FileName, StringComparer.Ordinal).ToList();
    }

    private sealed record SortKeyData(string Folder, string Base, int Exec, double Order, int Suffix, string Designation);

    private static SortKeyData SortKey(Row row)
    {
        var folder = row.Folder;
        var designation = row.DesignationKd;
        if (designation.Length == 0) return new SortKeyData(folder, "", 999, 0, 0, "");

        var baseNumber = DocumentCodes.GetBaseDesignation(designation);
        int exec = 0;
        var em = Regex.Match(baseNumber, @"-(\d+)(?=[A-ZА-Я]|$)");
        if (em.Success) { exec = int.Parse(em.Groups[1].Value); baseNumber = Regex.Replace(baseNumber, @"-\d+", ""); }

        var cleanForCode = designation;
        int hasSuffix = 0, suffixType = 0;
        if (designation.EndsWith("-ЛУ")) { cleanForCode = designation[..^3]; hasSuffix = 1; suffixType = 1; }
        else if (designation.EndsWith("-УЛ")) { cleanForCode = designation[..^3]; hasSuffix = 1; suffixType = 2; }

        string? docCode = null;
        foreach (var code in DocumentCodes.KdNameMapping.Keys
                     .Where(c => c is not ("-ЛУ" or "-УЛ" or "ЛУ" or "УЛ")).OrderByDescending(c => c.Length))
            if (cleanForCode.Contains($"-{code}") || cleanForCode.EndsWith(code)) { docCode = code; break; }

        double order; int sortSuffix;
        if (docCode is null) { order = 0; sortSuffix = 0; }
        else
        {
            var baseOrder = DocumentCodes.KdOrder.GetValueOrDefault(docCode, 999);
            if (hasSuffix == 1) { order = baseOrder + 0.5; sortSuffix = suffixType; }
            else { order = baseOrder; sortSuffix = 0; }
        }

        return new SortKeyData(folder, baseNumber, exec, order, sortSuffix, designation);
    }

    // ── Колонки по типу выгрузки ──────────────────────────────────────────────

    private static List<(string Header, Func<Row, object?> Value)> BuildColumns(
        List<Row> rows, ExportType exportType, Dictionary<string, string> cache)
    {
        // Для КД/Описи/Всех — пересчёт «Наименование документа» из спецификаций.
        if (exportType is ExportType.Kd or ExportType.Inventory or ExportType.All)
        {
            var allDes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in rows)
                if (r.DesignationKd.Length > 0 && r.Name.Length > 0) allDes[r.DesignationKd] = r.Name;

            foreach (var r in rows)
            {
                if (exportType == ExportType.All && r.DesignationKd.Length == 0) continue;   // оставляем как есть
                r.Name = DocumentCodes.GetDocumentFullName(r.DesignationKd, r.FileName, allDes, cache);
            }
        }

        object? Count(int v) => v > 0 ? v : "";

        return exportType switch
        {
            ExportType.Td =>
            [
                ("№ п/п", r => r.NumPp), ("Наименование документа", r => r.Name),
                ("Обозначение документа ТД", r => r.DesignationTd), ("Обозначение ЭН", r => r.DesignationEn),
                ("Обозначение файла ТД", r => r.FileTd), ("Контрольная сумма", r => r.Checksum),
                ("Формат файла", r => r.FileFormat), ("Физических листов", r => r.PageCount),
                ("А4", r => Count(r.A4)), ("А3", r => Count(r.A3)), ("А2", r => Count(r.A2)),
                ("А1", r => Count(r.A1)), ("А0", r => Count(r.A0)),
                ("Доп. форматы", r => r.Additional), ("Листов формата А4", r => Count(r.A4Sheets)),
                ("Объем, МБ", r => r.SizeMb), ("Гриф секретности", r => r.Secrecy), ("Примечание", r => r.Note),
            ],
            ExportType.Kd =>
            [
                ("№ п/п", r => r.NumPp), ("Наименование документа", r => r.Name),
                ("Обозначение документа КД", r => r.DesignationKd), ("Обозначение ЭН", r => r.DesignationEn),
                ("Обозначение файла КД", r => r.FileKd), ("Контрольная сумма", r => r.Checksum),
                ("Формат файла", r => r.FileFormat), ("Физических листов", r => r.PageCount),
                ("А4", r => Count(r.A4)), ("А3", r => Count(r.A3)), ("А2", r => Count(r.A2)),
                ("А1", r => Count(r.A1)), ("А0", r => Count(r.A0)),
                ("Доп. форматы", r => r.Additional), ("Листов формата А4", r => Count(r.A4Sheets)),
                ("Объем, МБ", r => r.SizeMb), ("Гриф секретности", r => r.Secrecy), ("Примечание", r => r.Note),
            ],
            ExportType.Inventory =>
            [
                ("№ п/п", r => r.NumPp), ("Обозначение", r => r.DesignationKd), ("Наименование", r => r.Name),
                ("Количество листов в документе", r => r.PageCount), ("Примечание", r => r.Note),
            ],
            _ =>   // All
            [
                ("№ п/п", r => r.NumPp), ("Наименование документа", r => r.Name),
                ("Обозначение документа ТД", r => r.DesignationTd), ("Обозначение документа КД", r => r.DesignationKd),
                ("Обозначение ЭН", r => r.DesignationEn), ("Обозначение файла КД", r => r.FileKd),
                ("Обозначение файла ТД", r => r.FileTd), ("Контрольная сумма", r => r.Checksum),
                ("Формат файла", r => r.FileFormat), ("Физических листов", r => r.PageCount),
                ("А4", r => Count(r.A4)), ("А3", r => Count(r.A3)), ("А2", r => Count(r.A2)),
                ("А1", r => Count(r.A1)), ("А0", r => Count(r.A0)),
                ("Доп. форматы", r => r.Additional), ("Листов формата А4", r => Count(r.A4Sheets)),
                ("Объем, МБ", r => r.SizeMb), ("Гриф секретности", r => r.Secrecy), ("Примечание", r => r.Note),
                ("Путь к файлу", r => r.FilePath), ("Папка", r => r.Folder), ("Имя файла", r => r.FileName),
            ],
        };
    }

    // ── Запись в Excel ────────────────────────────────────────────────────────

    private static void WriteExcel(string savePath, List<Row> rows,
        List<(string Header, Func<Row, object?> Value)> columns, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("ОПИСЬ");

        for (int c = 0; c < columns.Count; c++)
            ws.Cell(1, c + 1).Value = columns[c].Header;

        for (int i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            for (int c = 0; c < columns.Count; c++)
            {
                var cell = ws.Cell(i + 2, c + 1);
                switch (columns[c].Value(rows[i]))
                {
                    case null: break;
                    case int n: cell.Value = n; break;
                    case double d: cell.Value = d; break;
                    case string s: cell.Value = s; break;
                }
            }
        }

        FormatSheet(ws, columns);

        try { wb.SaveAs(savePath); }
        catch (IOException)
        {
            throw new PdfAnalyzeException(
                $"Не удалось сохранить отчёт. Возможно, файл «{Path.GetFileName(savePath)}» открыт в Excel. Закройте его и повторите.");
        }
    }

    private static void FormatSheet(IXLWorksheet ws, List<(string Header, Func<Row, object?> Value)> columns)
    {
        var used = ws.RangeUsed();
        if (used is not null)
        {
            used.Style.Font.FontName     = "Times New Roman";
            used.Style.Font.FontSize     = 12;
            used.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            used.Style.Alignment.Vertical   = XLAlignmentVerticalValues.Center;
            used.Style.Alignment.WrapText   = true;
            used.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            used.Style.Border.InsideBorder  = XLBorderStyleValues.Thin;
        }

        for (int c = 0; c < columns.Count; c++)
            ws.Column(c + 1).Width = ColumnWidth(ws, c + 1, columns[c].Header);

        ws.Row(1).Height = 60;
        int last = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (int r = 2; r <= last; r++) ws.Row(r).Height = 30;
    }

    private static double ColumnWidth(IXLWorksheet ws, int col, string header) => header switch
    {
        "№ п/п" => 6,
        "Наименование документа" => 27,
        "Формат файла" => 10,
        "Контрольная сумма" => 44,
        "Объем, МБ" => 9,
        "Физических листов" => 14,
        "Обозначение ЭН" => 19,
        "Гриф секретности" => 14,
        "Примечание" => 14,
        "Обозначение файла КД" or "Обозначение файла ТД" => 27,
        "Обозначение документа ТД" or "Обозначение документа КД" => 29,
        "А4" or "А3" or "А2" or "А1" or "А0" => 4,
        "Доп. форматы" => 11,
        "Листов формата А4" => 12,
        _ => AutoWidth(ws, col),
    };

    private static double AutoWidth(IXLWorksheet ws, int col)
    {
        int max = 0;
        foreach (var cell in ws.Column(col).CellsUsed())
            max = Math.Max(max, cell.GetFormattedString().Length);
        return Math.Min(max + 2, 50);
    }

    // ── Прочее ────────────────────────────────────────────────────────────────

    private static string ResolveSavePath(string folder, string savePath)
    {
        if (string.IsNullOrWhiteSpace(savePath)) return Path.Combine(folder, "pdf_info.xlsx");
        if (Directory.Exists(savePath)) return Path.Combine(savePath, "pdf_info.xlsx");
        return savePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? savePath : savePath + ".xlsx";
    }

    private static string BuildStats(
        string savePath, int total, int processed, List<string> errors, ExportType exportType, List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Отчёт успешно создан!").AppendLine();
        sb.AppendLine($"Сохранён в: {savePath}").AppendLine();
        sb.AppendLine("Статистика:");
        sb.AppendLine($"- Всего найдено файлов: {total}");
        sb.AppendLine($"- Успешно обработано: {processed}");

        switch (exportType)
        {
            case ExportType.Td:
                sb.AppendLine($"- Файлов с обозначением ТД: {rows.Count(r => r.DesignationTd.Length > 0)}");
                sb.AppendLine("Выгружен отчёт по технологической документации (ТД).");
                break;
            case ExportType.Kd:
                sb.AppendLine($"- Файлов с обозначением КД/ПД: {rows.Count(r => r.DesignationKd.Length > 0)}");
                sb.AppendLine("Выгружен отчёт по конструкторской/программной документации (КД/ПД).");
                break;
            case ExportType.Inventory:
                sb.AppendLine($"- Файлов в описи КД/ПД: {rows.Count}");
                sb.AppendLine("Выгружена опись конструкторской/программной документации (КД/ПД) — сокращённый формат.");
                break;
            default:
                sb.AppendLine($"- Файлов с обозначением КД/ПД: {rows.Count(r => r.DesignationKd.Length > 0)}");
                sb.AppendLine($"- Файлов с обозначением ТД: {rows.Count(r => r.DesignationTd.Length > 0)}");
                sb.AppendLine("Выгружен полный отчёт со всеми данными.");
                break;
        }

        if (errors.Count > 0)
        {
            sb.AppendLine().AppendLine($"Файлы с ошибками ({errors.Count}):");
            foreach (var e in errors.Take(5)) sb.AppendLine($"- {e}");
            if (errors.Count > 5) sb.AppendLine($"… и ещё {errors.Count - 5} файлов с ошибками");
        }

        return sb.ToString().TrimEnd();
    }
}
