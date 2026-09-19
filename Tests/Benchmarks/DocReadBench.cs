using System.Diagnostics;
using System.Text;
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
                                 .ToList();
            if (files.Count == 0)
            {
                Console.WriteLine($"В {dir} нет документов поддержанных форматов " +
                                  $"({DocTextExtractor.SupportedText}).");
                return 1;
            }

            Console.WriteLine($"Папка: {dir}");
            Console.WriteLine($"Документов: {files.Count}, суммарно {Mb(TotalBytes(files))}");
            Console.WriteLine();

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
            else if (kind < 9) WriteText(Path.Combine(dir, $"note-{i:0000}.txt"), pages, rnd);
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
