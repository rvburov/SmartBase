using System.IO;
using System.Threading;
using ClosedXML.Excel;

namespace DataBase.Extensions.ProductCompare;

/// <summary>Ошибка сравнения с дружелюбным текстом — показывается пользователю в статусе окна.</summary>
public sealed class ProductCompareException(string message) : Exception(message);

/// <summary>Итог сравнения: путь к созданному файлу и счётчики строк.</summary>
public sealed record CompareResult(string OutputPath, int OldRows, int NewRows, int TotalRows);

/// <summary>
/// Сравнение составов изделий из двух Excel-файлов (старый/новый) с записью результата в
/// .xlsm-шаблон. Порт PyQt-утилиты на ClosedXML (тот же стек, что и импорт в приложении).
///
/// Алгоритм: строки сопоставляются по <b>иерархическому номеру</b> (столбец 2), уникальность
/// определяется по <b>обозначению</b> (столбец 11). В шаблон с 6-й строки пишутся старые данные
/// слева (столбцы 1–5) и новые справа (столбцы 7–11); уникальные в старом — красным, уникальные
/// в новом — зелёным; стиль ГОСТ (шрифт, границы, выравнивание), макросы шаблона сохраняются.
///
/// Логика без UI: окно вызывает <see cref="Run"/>, репортя прогресс и отменяясь по токену.
/// </summary>
public static class ProductCompareEngine
{
    // Колонки источника (1-based) — как в исходной утилите.
    private const int ColPos         = 1;    // позиция
    private const int ColHierarchy   = 2;    // иерархический номер «1.2.3.»
    private const int ColName        = 5;    // наименование
    private const int ColDesignation = 11;   // обозначение (ключ уникальности)
    private const int ColQty         = 12;   // количество
    private const int FirstDataRow   = 6;    // данные начинаются с 6-й строки

    private static readonly XLColor RedFill   = XLColor.FromArgb(255, 0, 0);     // уникально в старом
    private static readonly XLColor GreenFill = XLColor.FromArgb(0, 255, 0);     // уникально в новом

    public static CompareResult Run(
        string templatePath, string oldPath, string newPath, string outputDir,
        IProgress<int>? progress, CancellationToken ct)
    {
        if (!File.Exists(templatePath))   throw new ProductCompareException("Файл-шаблон не найден.");
        if (!File.Exists(oldPath))        throw new ProductCompareException("Старый файл не найден.");
        if (!File.Exists(newPath))        throw new ProductCompareException("Новый файл не найден.");
        if (!Directory.Exists(outputDir)) throw new ProductCompareException("Папка для сохранения не найдена.");

        progress?.Report(2);

        // 1) Читаем источники (с прогрессом по строкам).
        var oldData = ReadRows(oldPath, ct, progress, 2, 25);
        var newData = ReadRows(newPath, ct, progress, 25, 45);

        // 2) Уникальные обозначения в каждом файле.
        var oldOnly = new HashSet<string>(oldData.Designations); oldOnly.ExceptWith(newData.Designations);
        var newOnly = new HashSet<string>(newData.Designations); newOnly.ExceptWith(oldData.Designations);

        // 3) Открываем шаблон (сохраняя макросы) и пишем результат.
        var timestamp  = DateTime.Now.ToString("dd.MM.yyyy_HH-mm-ss");
        var outputPath = Path.Combine(outputDir, $"Результат сравнения {timestamp}.xlsm");

        using var twb = Open(templatePath);
        var tws = twb.Worksheets.FirstOrDefault()
                  ?? throw new ProductCompareException("В файле-шаблоне нет ни одного листа.");

        var allKeys = oldData.ByKey.Keys.Union(newData.ByKey.Keys).ToList();
        allKeys.Sort();

        int row = FirstDataRow;
        int total = Math.Max(1, allKeys.Count);
        int lastPct = -1;
        for (int i = 0; i < allKeys.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var key = allKeys[i];
            oldData.ByKey.TryGetValue(key, out var oldItem);
            newData.ByKey.TryGetValue(key, out var newItem);

            if (oldItem is not null)                                   // старые данные слева (1–5)
                WriteValues(tws, row, startCol: 1, oldItem.Values,
                            IsUnique(oldItem, oldOnly) ? RedFill : null);
            else
                WriteBlank(tws, row, fromCol: 1, toCol: 5);            // нет старого — пустые слева

            if (newItem is not null)                                   // новые данные справа (7–11)
                WriteValues(tws, row, startCol: 7, newItem.Values,
                            IsUnique(newItem, newOnly) ? GreenFill : null);

            tws.Row(row).Height = 30;
            row++;

            int pct = 45 + 50 * (i + 1) / total;
            if (pct != lastPct) { progress?.Report(pct); lastPct = pct; }
        }

        // 4) Сохраняем.
        try { twb.SaveAs(outputPath); }
        catch (Exception ex)
        {
            throw new ProductCompareException(
                $"Не удалось сохранить результат:\n{ex.Message}\nПроверьте права на папку и что файл не открыт.");
        }
        progress?.Report(100);

        return new CompareResult(outputPath, oldData.RowCount, newData.RowCount, allKeys.Count);
    }

    // ── Чтение источника ──────────────────────────────────────────────────────

    private sealed class FileData
    {
        public Dictionary<Hierarchy, RowData> ByKey { get; } = new();
        public HashSet<string> Designations { get; } = new(StringComparer.Ordinal);
        public int RowCount { get; set; }
    }

    private sealed class RowData
    {
        public required XLCellValue[] Values { get; init; }   // [позиция, иерархия, наименование, обозначение, кол-во]
        public string? Designation { get; init; }             // обрезанное обозначение или null
    }

    private static FileData ReadRows(
        string path, CancellationToken ct, IProgress<int>? progress, int fromPct, int toPct)
    {
        using var wb = Open(path);
        var ws = wb.Worksheets.FirstOrDefault()
                 ?? throw new ProductCompareException($"В файле «{Path.GetFileName(path)}» нет ни одного листа.");

        int last  = ws.LastRowUsed()?.RowNumber() ?? 0;
        int total = Math.Max(1, last - FirstDataRow + 1);
        var data  = new FileData();
        int lastPct = -1;

        for (int row = FirstDataRow, idx = 0; row <= last; row++, idx++)
        {
            ct.ThrowIfCancellationRequested();

            var hierarchyCell = ws.Cell(row, ColHierarchy);
            // Как в Python (hierarchy_to_tuple + `if hierarchy`): ключ строится ТОЛЬКО из
            // строкового значения иерархии. Число/пусто/иной тип → пустой ключ.
            var key = hierarchyCell.Value.IsText
                ? Hierarchy.Parse(hierarchyCell.GetString())
                : Hierarchy.Parse(null);

            var desCell = ws.Cell(row, ColDesignation);
            string? designation = null;
            if (!desCell.IsEmpty())
            {
                var s = desCell.GetString().Trim();
                if (s.Length > 0) { designation = s; data.Designations.Add(s); }
            }

            data.ByKey[key] = new RowData
            {
                Designation = designation,
                Values =
                [
                    ws.Cell(row, ColPos).Value,
                    hierarchyCell.Value,
                    ws.Cell(row, ColName).Value,
                    desCell.Value,
                    ws.Cell(row, ColQty).Value,
                ],
            };
            data.RowCount++;

            int pct = fromPct + (toPct - fromPct) * (idx + 1) / total;
            if (pct != lastPct) { progress?.Report(pct); lastPct = pct; }
        }

        return data;
    }

    // ── Запись в шаблон ───────────────────────────────────────────────────────

    private static bool IsUnique(RowData item, HashSet<string> uniqueSet) =>
        item.Designation is not null && uniqueSet.Contains(item.Designation);

    private static void WriteValues(IXLWorksheet ws, int row, int startCol, XLCellValue[] values, XLColor? fill)
    {
        for (int j = 0; j < values.Length; j++)
        {
            var cell = ws.Cell(row, startCol + j);
            cell.Value = values[j];
            ApplyGost(cell, fill);
        }
    }

    private static void WriteBlank(IXLWorksheet ws, int row, int fromCol, int toCol)
    {
        for (int c = fromCol; c <= toCol; c++)
        {
            var cell = ws.Cell(row, c);
            cell.Value = "";
            ApplyGost(cell, null);
        }
    }

    // Стиль ГОСТ: шрифт «GOST type A» 12, выравнивание по центру с переносом, тонкие границы,
    // опциональная заливка (красная/зелёная для уникальных).
    private static void ApplyGost(IXLCell cell, XLColor? fill)
    {
        var s = cell.Style;
        s.Font.FontName             = "GOST type A";
        s.Font.FontSize             = 12;
        s.Alignment.Horizontal      = XLAlignmentHorizontalValues.Center;
        s.Alignment.Vertical        = XLAlignmentVerticalValues.Center;
        s.Alignment.WrapText        = true;
        s.Border.TopBorder          = XLBorderStyleValues.Thin;
        s.Border.BottomBorder       = XLBorderStyleValues.Thin;
        s.Border.LeftBorder         = XLBorderStyleValues.Thin;
        s.Border.RightBorder        = XLBorderStyleValues.Thin;
        if (fill is not null) s.Fill.BackgroundColor = fill;
    }

    private static XLWorkbook Open(string path)
    {
        try { return new XLWorkbook(path); }
        catch
        {
            throw new ProductCompareException(
                $"Не удалось открыть файл:\n{Path.GetFileName(path)}\n" +
                "Проверьте, что это корректный Excel (.xlsx/.xlsm) и он не открыт в другой программе.");
        }
    }

    // ── Иерархический номер «1.2.3.» → последовательность чисел (ключ + сортировка) ──

    private sealed class Hierarchy : IEquatable<Hierarchy>, IComparable<Hierarchy>
    {
        private readonly int[] _parts;
        private Hierarchy(int[] parts) => _parts = parts;

        public static Hierarchy Parse(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new Hierarchy([]);
            var parts = s.Trim().TrimEnd('.').Split('.')
                         .Where(p => p.Length > 0 && p.All(char.IsDigit))
                         .Select(int.Parse)
                         .ToArray();
            return new Hierarchy(parts);
        }

        public bool Equals(Hierarchy? other) =>
            other is not null && _parts.AsSpan().SequenceEqual(other._parts);

        public override bool Equals(object? o) => Equals(o as Hierarchy);

        public override int GetHashCode()
        {
            var h = new HashCode();
            foreach (var p in _parts) h.Add(p);
            return h.ToHashCode();
        }

        // Лексикографически, как сортировка кортежей в Python (короткий префикс — раньше).
        public int CompareTo(Hierarchy? other)
        {
            if (other is null) return 1;
            int n = Math.Min(_parts.Length, other._parts.Length);
            for (int i = 0; i < n; i++)
            {
                int c = _parts[i].CompareTo(other._parts[i]);
                if (c != 0) return c;
            }
            return _parts.Length.CompareTo(other._parts.Length);
        }
    }
}
