using System.Globalization;
using System.IO;
using System.Threading;
using ClosedXML.Excel;

namespace SmartBase.Extensions.AssemblyCalc;

/// <summary>Ошибка формирования с дружелюбным текстом — показывается в статусе окна.</summary>
public sealed class AssemblyCalcException(string message) : Exception(message);

/// <summary>Итог: путь к созданному файлу и число перенесенных строк.</summary>
public sealed record AssemblyCalcResult(string OutputPath, int RowCount);

/// <summary>
/// Формирование файла «Расчет сборочных единиц в изделии»: перенос выбранных столбцов из
/// исходного «Список КД и ТД» (лист «Исходные данные») в .xlsm-шаблон (тот же лист) с
/// раскраской строк по уровню и стилем ГОСТ. Реализация на ClosedXML.
///
/// Логика без UI: окно вызывает <see cref="Run"/>, репортя прогресс и отменяясь по токену.
/// Макросы шаблона сохраняются (запись в копию шаблона через <c>SaveAs</c>).
/// </summary>
public static class AssemblyCalcEngine
{
    private const string SheetName    = "Исходные данные";
    private const int    FirstDataRow = 6;

    // Столбцы источника (1-based) → столбец назначения в шаблоне.
    private static readonly (int Src, int Dst)[] Map =
    [
        (2, 2),     // «Уровень идентификатор»
        (5, 3),     // «Наименование»
        (11, 4),    // «Децимальный номер КД»
        (12, 5),    // «Количество»
        (283, 7),   // «Вид согласно разделу СП»
        (287, 8),   // «Децимальный номер ТД»
        (289, 9),   // «Тип ТД по виду документа»
        (346, 10),  // «Состояние ТД (согласно ОГТ)»
        (362, 11),  // «Состояние ТД (согласно данным различных источников)»
        (284, 12),  // «Не входит в состав МК»
        (248, 13),  // «ВЭ»
    ];

    private const int SrcLevel = 1;                        // «Уровень» → столбец 1 шаблона (целое + цвет)
    private static readonly int LastCol = Map.Max(m => m.Dst);   // последний столбец назначения — по нему чистим и форматируем

    // Цвета заливки по уровню (как в оригинале).
    private static readonly Dictionary<int, XLColor> LevelColors = new()
    {
        [0]  = Hex("0070C0"), [1] = Hex("00B0F0"), [2]  = Hex("A9D08E"), [3] = Hex("E2EFDA"),
        [4]  = Hex("8EA9DB"), [5] = Hex("BDD7EE"), [6]  = Hex("D9E1F2"), [7] = Hex("A6A6A6"),
        [8]  = Hex("BFBFBF"), [9] = Hex("D9D9D9"), [10] = Hex("F2F2F2"),
    };

    public static AssemblyCalcResult Run(
        string sourcePath, string templatePath, string outputDir,
        IProgress<int>? progress, CancellationToken ct)
    {
        if (!File.Exists(sourcePath))     throw new AssemblyCalcException("Исходный файл не найден.");
        if (!File.Exists(templatePath))   throw new AssemblyCalcException("Файл-шаблон не найден.");
        if (!Directory.Exists(outputDir)) throw new AssemblyCalcException("Папка для сохранения не найдена.");

        progress?.Report(5);

        using var swb = Open(sourcePath, "исходный файл");
        var sws = Sheet(swb, "исходном файле");
        progress?.Report(15);

        using var twb = Open(templatePath, "файл-шаблон");
        var tws = Sheet(twb, "шаблоне");
        progress?.Report(30);

        // Очистка прежних данных шаблона (с 6-й строки, столбцы 1–LastCol) — только содержимое.
        int tlast = tws.LastRowUsed()?.RowNumber() ?? 0;
        for (int row = FirstDataRow; row <= tlast; row++)
            for (int col = 1; col <= LastCol; col++)
                tws.Cell(row, col).Clear(XLClearOptions.Contents);
        progress?.Report(40);

        // Перенос + форматирование строк, пока в источнике заполнен столбец «Уровень».
        int est = Math.Max(1, (sws.LastRowUsed()?.RowNumber() ?? FirstDataRow) - FirstDataRow + 1);
        int target = FirstDataRow;
        int count = 0;
        int lastPct = -1;

        for (int srow = FirstDataRow; ; srow++)
        {
            ct.ThrowIfCancellationRequested();

            var levelCell = sws.Cell(srow, SrcLevel);
            if (levelCell.IsEmpty()) break;      // while col1 not None

            // Уровень: число → целое (усечение), строка-целое → целое, иначе сырое значение без цвета.
            XLCellValue levelOut;
            int? colorLevel = null;
            if (levelCell.Value.IsNumber)
            {
                int lvl = (int)Math.Truncate(levelCell.Value.GetNumber());
                levelOut = lvl; colorLevel = lvl;
            }
            else if (int.TryParse(levelCell.GetString().Trim(),
                                  NumberStyles.Integer, CultureInfo.InvariantCulture, out var lvl))
            {
                levelOut = lvl; colorLevel = lvl;
            }
            else
            {
                levelOut = levelCell.Value;       // как есть, без раскраски
            }

            tws.Cell(target, 1).Value = levelOut;
            foreach (var (src, dst) in Map)
                tws.Cell(target, dst).Value = sws.Cell(srow, src).Value;

            // Форматирование строки (столбцы 1–LastCol, включая пустой 6 — как в оригинале).
            XLColor? fill = colorLevel is int cl && LevelColors.TryGetValue(cl, out var c) ? c : null;
            for (int col = 1; col <= LastCol; col++)
            {
                var cell = tws.Cell(target, col);
                ApplyGost(cell);
                if (fill is not null) cell.Style.Fill.BackgroundColor = fill;
            }
            tws.Row(target).Height = 30;

            target++; count++;

            int pct = 40 + 50 * Math.Min(count, est) / est;
            if (pct != lastPct) { progress?.Report(pct); lastPct = pct; }
        }

        progress?.Report(92);

        var timestamp  = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
        var outputPath = Path.Combine(outputDir, $"Расчет сборочных единиц ({timestamp}).xlsm");
        try { twb.SaveAs(outputPath); }
        catch (Exception ex)
        {
            throw new AssemblyCalcException(
                $"Не удалось сохранить результат:\n{ex.Message}\nПроверьте права на папку и что файл не открыт.");
        }
        progress?.Report(100);

        return new AssemblyCalcResult(outputPath, count);
    }

    // Стиль ГОСТ: шрифт «GOST type A» 12, центр, тонкие границы, текстовый формат «@».
    private static void ApplyGost(IXLCell cell)
    {
        var s = cell.Style;
        s.Font.FontName        = "GOST type A";
        s.Font.FontSize        = 12;
        s.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        s.Alignment.Vertical   = XLAlignmentVerticalValues.Center;
        s.Border.TopBorder     = XLBorderStyleValues.Thin;
        s.Border.BottomBorder  = XLBorderStyleValues.Thin;
        s.Border.LeftBorder    = XLBorderStyleValues.Thin;
        s.Border.RightBorder   = XLBorderStyleValues.Thin;
        s.NumberFormat.Format   = "@";
    }

    private static XLWorkbook Open(string path, string what)
    {
        try { return new XLWorkbook(path); }
        catch
        {
            throw new AssemblyCalcException(
                $"Не удалось открыть {what}:\n{Path.GetFileName(path)}\n" +
                "Проверьте, что это корректный Excel (.xlsx/.xlsm) и он не открыт в другой программе.");
        }
    }

    private static IXLWorksheet Sheet(XLWorkbook wb, string where) =>
        wb.Worksheets.TryGetWorksheet(SheetName, out var ws)
            ? ws
            : throw new AssemblyCalcException($"Лист «{SheetName}» не найден в {where}.");

    private static XLColor Hex(string h) => XLColor.FromArgb(
        255,
        Convert.ToInt32(h.Substring(0, 2), 16),
        Convert.ToInt32(h.Substring(2, 2), 16),
        Convert.ToInt32(h.Substring(4, 2), 16));
}
