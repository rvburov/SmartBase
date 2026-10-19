using System.IO;
using System.Threading;

namespace SmartBase.Extensions.ProductCompare;

/// <summary>Итог сравнения расшифровок: созданный файл, файлы в порядке дат и счетчики
/// строк общего списка.</summary>
public sealed record BreakdownResult(
    string OutputPath, IReadOnlyList<BreakdownFile> Files, int TotalRows, int Products, int Operations);

/// <summary>
/// Режим «Сравнение составов изделий расшифровка»: все расшифровки одного изделия из папки,
/// по датам в именах, сводятся в общий список входимостей и пишутся в копию шаблона —
/// по блоку на дату, слева направо. Изделие переносится вместе со своими операциями; на дату,
/// где строки не было, клетки блока пустые. Заливки по изменениям нет: что появилось, что
/// пропало и что поменялось, видно по соседним блокам.
///
/// Логика без UI: окно вызывает <see cref="Run"/>, получая прогресс и строки журнала.
/// Режим «заключение» (<see cref="ProductCompareEngine"/>) этим не затронут.
/// </summary>
public static class BreakdownCompareEngine
{
    public static BreakdownResult Run(
        string templatePath, string folder, string outputDir,
        IProgress<int>? progress, IProgress<string>? log, CancellationToken ct)
    {
        if (!File.Exists(templatePath))   throw new ProductCompareException("Файл-шаблон не найден.");
        if (!Directory.Exists(folder))    throw new ProductCompareException("Папка с расшифровками не найдена.");
        if (!Directory.Exists(outputDir)) throw new ProductCompareException("Папка для сохранения не найдена.");

        progress?.Report(2);

        // 1) Файлы по датам: любая неясность с порядком — отказ до чтения.
        var files = BreakdownFiles.Collect(folder, log);
        log?.Report($"📅 Файлов расшифровки: {files.Count}, по датам:");
        foreach (var file in files)
            log?.Report($"   {file.Date:dd.MM.yyyy} — {Path.GetFileName(file.Path)}");
        progress?.Report(5);

        // 2) Чтение.
        var sheets = new List<BreakdownSheet>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sheet = BreakdownReader.Read(files[i], ct);
            sheets.Add(sheet);
            log?.Report($"📖 {files[i].Date:dd.MM.yyyy}: изделий {sheet.Products}, операций {sheet.Operations}");
            progress?.Report(5 + 55 * (i + 1) / files.Count);
        }

        // 3) Общий список входимостей.
        var list = BreakdownMerge.Merge(sheets.Select(s => s.Root).ToList());
        if (list.Rows.Count == 0)
            throw new ProductCompareException($"На листах «{BreakdownReader.SheetName}» нет ни одной строки данных.");
        progress?.Report(62);

        // 4) Запись. Расширение результата — как у шаблона: тип книги внутри файла не меняется.
        var timestamp  = DateTime.Now.ToString("dd.MM.yyyy_HH-mm-ss");
        var extension  = Path.GetExtension(templatePath).ToLowerInvariant();
        var outputPath = Path.Combine(outputDir, $"{BreakdownFiles.ResultPrefix}{timestamp}{extension}");
        BreakdownWriter.Write(templatePath, outputPath, sheets, list, progress, 62, 98, ct);
        progress?.Report(100);

        return new BreakdownResult(outputPath, files, list.Rows.Count, list.Products, list.Operations);
    }
}
