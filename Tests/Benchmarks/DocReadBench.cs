using System.Diagnostics;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml;
using W = DocumentFormat.OpenXml.Wordprocessing;
using X = DocumentFormat.OpenXml.Spreadsheet;
using SmartBase.Core.Documents;
using SmartBase.Database.DocIndex;
using UglyToad.PdfPig.Writer;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Замер чтения документов: сколько времени уходит на разбор одного файла и во что выльется
/// библиотека в тысячи документов.
///
/// Зачем: от этого числа зависят две вещи в разделе — порог, с которого чтение спрашивает
/// подтверждение, и то, стоит ли вообще показывать оценку времени. Оценка «около 20 минут»,
/// взятая с потолка, хуже отсутствия оценки.
///
/// <b>Честная оговорка про синтетику.</b> Набор здесь генерируется: многостраничные PDF с
/// плотным текстом, DOCX и текстовые файлы. Настоящий СП со сканами, таблицами, встроенными
/// шрифтами и кривой структурой тяжелее, поэтому замер дает <b>нижнюю границу</b>, а не правду.
/// Есть папка с настоящими документами — укажите ее через <c>--dir</c>, и генерация пропустится:
/// тогда числа будут про ваши файлы.
/// </summary>
public static class DocReadBench
{
    public static int Run(string[] args)
    {
        var count = BenchUtil.ArgInt(args, "--count") ?? 200;
        var pages = BenchUtil.ArgInt(args, "--pages") ?? 20;
        var dir   = BenchUtil.ArgStr(args, "--dir");
        var keep  = args.Contains("--keep");

        if (count <= 0 || pages <= 0 || (dir is not null && !Directory.Exists(dir)))
        {
            Console.Error.WriteLine("Нужны положительные --count/--pages и существующая папка --dir.");
            return 1;
        }
        var generated = dir is null;
        dir ??= Path.Combine(Path.GetTempPath(), "sb-docbench-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            if (generated)
            {
                Console.WriteLine($"Генерирую набор: {count} документов по {pages} страниц…");
                Generate(dir, count, pages);
            }

            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                                 .Where(f => DocTextExtractor.IsSupported(f))
                                 .OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (files.Count == 0)
            {
                Console.WriteLine($"В {dir} нет документов поддержанных форматов " +
                                  $"({DocTextExtractor.SupportedText}).");
                return 1;
            }

            Console.WriteLine($"Папка: {dir}");
            Console.WriteLine($"Документов: {files.Count}, суммарно {Mb(TotalBytes(files))}");
            Console.WriteLine();

            MeasurePipeline(files, BenchUtil.ArgStr(args, "--csv"));
            Measure(files);
            return 0;
        }
        finally
        {
            if (generated && !keep)
                try { Directory.Delete(dir, recursive: true); } catch { /* уберет система */ }
        }
    }

    // ── Замер ─────────────────────────────────────────────────────────────────

    private static void Measure(List<string> files)
    {
        var byExt = new Dictionary<string, (int Count, double Seconds, long Bytes, long Chars)>(
            StringComparer.Ordinal);

        var total = Stopwatch.StartNew();
        var slowest = ("", 0.0);

        foreach (var file in files)
        {
            var ext  = DocTextExtractor.ExtensionOf(file);
            var size = new FileInfo(file).Length;

            var one  = Stopwatch.StartNew();
            var text = DocTextExtractor.Extract(file);
            one.Stop();

            var seconds = one.Elapsed.TotalSeconds;
            if (seconds > slowest.Item2) slowest = (Path.GetFileName(file), seconds);

            var acc = byExt.GetValueOrDefault(ext);
            byExt[ext] = (acc.Count + 1, acc.Seconds + seconds, acc.Bytes + size,
                          acc.Chars + text.Text.Length);
        }
        total.Stop();

        Console.WriteLine("Формат    Штук   Всего, с   На документ, мс   МБ/с     Текста, тыс. знаков");
        Console.WriteLine("──────────────────────────────────────────────────────────────────────────");
        foreach (var (ext, a) in byExt.OrderByDescending(p => p.Value.Seconds))
        {
            var perDoc = a.Seconds / a.Count * 1000;
            var mbs    = a.Seconds > 0 ? a.Bytes / (1024.0 * 1024) / a.Seconds : 0;
            Console.WriteLine($"{ext,-8}{a.Count,7}{a.Seconds,11:0.00}{perDoc,18:0}{mbs,9:0.0}{a.Chars / 1000.0,22:0}");
        }

        var seconds0 = total.Elapsed.TotalSeconds;
        var perOne   = seconds0 / files.Count;

        Console.WriteLine();
        Console.WriteLine($"Итого: {seconds0:0.0} с на {files.Count} документов " +
                          $"= {perOne * 1000:0} мс на документ.");
        Console.WriteLine($"Самый долгий: {slowest.Item1} — {slowest.Item2 * 1000:0} мс.");
        Console.WriteLine();
        Console.WriteLine("Сколько займет библиотека (одним потоком, без учета диска под нагрузкой):");
        foreach (var n in new[] { 1_000, 5_000, 10_000, 20_000 })
            Console.WriteLine($"  {n,6} документов — {Humanize(TimeSpan.FromSeconds(perOne * n))}");
        Console.WriteLine();
        Console.WriteLine("ВНИМАНИЕ: на сгенерированном наборе это НИЖНЯЯ граница. Настоящие ГОСТы и СП " +
                          "со сканами, таблицами и встроенными шрифтами тяжелее.");
    }

    // Отдельные проходы: первый включает разогрев библиотек, второй использует прогретый процесс.
    // Это не сброс кеша ОС. Put пишет настоящий FTS, как очередь приложения.
    private static void MeasurePipeline(List<string> files, string? csv)
    {
        var lines = new List<string> { "pass,file,bytes,extract_ms,fts_ms,book_ms,status,chars" };
        var root = Path.Combine(Path.GetTempPath(), "sb-docpipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (var pass = 1; pass <= 2; pass++)
            {
                var index = new DocTextIndex(Path.Combine(root, $"index-{pass}.db"));
                double extracted = 0, indexed = 0, books = 0;
                foreach (var file in files)
                {
                    var watch = Stopwatch.StartNew();
                    var text = DocTextExtractor.Extract(file);
                    var read = watch.Elapsed.TotalMilliseconds;
                    watch.Restart();
                    index.Put(file, text);
                    var put = watch.Elapsed.TotalMilliseconds;
                    var book = 0.0;
                    if (DocTextExtractor.ExtensionOf(file) is "xlsx" or "xlsm")
                    {
                        watch.Restart();
                        _ = DocSheetSource.OpenBook(file);
                        book = watch.Elapsed.TotalMilliseconds;
                    }
                    extracted += read;
                    indexed += put;
                    books += book;
                    var name = Path.GetFileName(file).Replace("\"", "\"\"");
                    lines.Add(FormattableString.Invariant($"{pass},\"{name}\",{new FileInfo(file).Length},{read:F3},{put:F3},{book:F3},{text.Status},{text.Text.Length}"));
                }
                Console.WriteLine($"Проход {pass}: разбор {extracted:F1} мс, запись FTS {indexed:F1} мс, " +
                    $"открытие книг {books:F1} мс; разбор + FTS {(extracted + indexed) / files.Count:F1} мс/документ.");
            }
            if (csv is not null) File.WriteAllLines(csv, lines, new UTF8Encoding(false));
        }
        finally { try { Directory.Delete(root, true); } catch { /* временный стенд */ } }
    }

    private static string Humanize(TimeSpan t) =>
        t < TimeSpan.FromMinutes(1) ? $"{t.TotalSeconds:0} с"
        : t < TimeSpan.FromHours(1) ? $"{t.TotalMinutes:0} мин"
        : $"{t.TotalHours:0.0} ч";

    // ── Генерация набора ──────────────────────────────────────────────────────

    private static void Generate(string dir, int count, int pages)
    {
        Directory.CreateDirectory(dir);

        // Текст берем детерминированный: два прогона подряд должны мерить одно и то же, иначе
        // сравнивать их между собой бессмысленно.
        var rnd = new Random(20260919);

        for (var i = 0; i < count; i++)
        {
            // Пропорции набора прикинуты по обычной инженерной библиотеке: PDF большинство,
            // Word заметная часть, текстовые файлы — остаток.
            var kind = i % 10;
            if      (kind < 7) WritePdf(Path.Combine(dir, $"doc-{i:0000}.pdf"), pages, rnd);
            else if (kind == 7) WriteDocx(Path.Combine(dir, $"word-{i:0000}.docx"), pages, rnd);
            else if (kind == 8) WriteBook(Path.Combine(dir, $"book-{i:0000}.xlsx"), pages, rnd);
            else               WriteText(Path.Combine(dir, $"list-{i:0000}.md"),  pages, rnd);
        }
    }

    private static void WritePdf(string path, int pages, Random rnd)
    {
        var builder = new PdfDocumentBuilder();
        var font    = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);

        for (var p = 0; p < pages; p++)
        {
            var page = builder.AddPage(595, 842);   // A4 в пунктах
            var y    = 800;
            for (var line = 0; line < 40 && y > 40; line++, y -= 18)
            {
                // Standard14 не умеет кириллицу, поэтому строки латинские. На скорость разбора
                // это не влияет — PdfPig достает текст одинаково.
                var text = Line(rnd, ascii: true);
                try { page.AddText(text, 10, new UglyToad.PdfPig.Core.PdfPoint(40, y), font); }
                catch { /* пропущенный глиф не должен ронять генерацию */ }
            }
        }

        File.WriteAllBytes(path, builder.Build());
    }

    private static void WriteDocx(string path, int pages, Random rnd)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new W.Body();
        for (var i = 0; i < pages * 40; i++)
            body.Append(new W.Paragraph(new W.Run(new W.Text(Line(rnd, false)))));
        main.Document = new W.Document(body);
        main.Document.Save();
    }

    private static void WriteBook(string path, int pages, Random rnd)
    {
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var book = doc.AddWorkbookPart();
        book.Workbook = new X.Workbook();
        var sheet = book.AddNewPart<WorksheetPart>();
        var data = new X.SheetData();
        for (var i = 1; i <= pages * 40; i++)
            data.Append(new X.Row(new X.Cell { CellReference = $"A{i}", DataType = X.CellValues.String,
                CellValue = new X.CellValue(Line(rnd, false)) }) { RowIndex = (uint)i });
        sheet.Worksheet = new X.Worksheet(data);
        sheet.Worksheet.Save();
        book.Workbook.Append(new X.Sheets(new X.Sheet { Id = book.GetIdOfPart(sheet), SheetId = 1, Name = "Смета" }));
        book.Workbook.Save();
    }

    private static void WriteText(string path, int pages, Random rnd)
    {
        var sb = new StringBuilder();
        for (var line = 0; line < pages * 40; line++) sb.AppendLine(Line(rnd, ascii: false));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static readonly string[] WordsRu =
    [
        "входной", "контроль", "проектной", "документации", "выполняет", "застройщик",
        "организация", "строительства", "требования", "безопасности", "конструкции",
        "основания", "фундаменты", "бетонные", "работы", "приемка", "объекта",
    ];

    private static readonly string[] WordsEn =
    [
        "incoming", "inspection", "project", "documentation", "performed", "developer",
        "organization", "construction", "requirements", "safety", "structures",
        "foundations", "concrete", "works", "acceptance", "facility",
    ];

    private static string Line(Random rnd, bool ascii)
    {
        var words = ascii ? WordsEn : WordsRu;
        var sb = new StringBuilder();
        for (var w = 0; w < 10; w++)
        {
            if (w > 0) sb.Append(' ');
            sb.Append(words[rnd.Next(words.Length)]);
        }
        return sb.ToString();
    }

    private static long TotalBytes(IEnumerable<string> files) =>
        files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });

    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024):0.#} МБ";
}
