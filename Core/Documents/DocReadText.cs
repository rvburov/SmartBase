using System.IO;
using System.Text;
using System.Threading;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace SmartBase.Core.Documents;

/// <summary>Кусок документа для чтения: подпись места и его текст.</summary>
/// <param name="Label">«Страница 4», «Лист „Смета“»; пусто — у форматов без такого деления.</param>
public readonly record struct DocPart(string Label, string Text);

/// <summary>Документ, разобранный для чтения: статус, куски по порядку и число страниц.</summary>
public readonly record struct DocReadResult(DocTextStatus Status, IReadOnlyList<DocPart> Parts, int Pages = 0)
{
    public static DocReadResult Of(DocTextStatus status) => new(status, []);

    /// <summary>Общая длина текста — по ней панель решает, что показывать.</summary>
    public int Length => Parts.Sum(p => p.Text.Length);
}

/// <summary>
/// Разбор документа ДЛЯ ЧТЕНИЯ — то, что показывает панель чтения библиотеки.
///
/// Почему это отдельный файл, а не метод <see cref="DocTextExtractor"/>: у них разные цели, и
/// цели эти несовместимы. Тому нужен текст ДЛЯ ПОИСКА, и он не зря схлопывает всякий пробельный
/// знак в один пробел (<c>DocTextExtractor.Normalize</c>) — индексу верстка не нужна, а лишние
/// пробелы раздули бы кеш вдвое. Человеку же нужен текст ДЛЯ ГЛАЗ: убрать переносы строк значит
/// склеить документ в одну бесконечную строку, где не видно ни абзацев, ни строк таблицы.
///
/// Поэтому здесь свое правило: пробелы и табуляции схлопываются, а переносы строк остаются —
/// не больше одной пустой строки подряд. Разборщики при этом те же самые библиотеки и те же
/// ограничители, что у индекса: один документ не должен съесть всю память ради одного открытия
/// панели.
///
/// Исключений метод не бросает НИКОГДА — ровно по той же причине, что и разбор для индекса:
/// битый и занятый файл в ленте это рядовой исход, а не сбой.
/// </summary>
public static class DocReadText
{
    /// <summary>Сколько строк листа XLSX показываем: дальше это уже не чтение, а выгрузка.</summary>
    public const int MaxSheetRows = 2_000;

    /// <summary>Разделитель ячеек в строке листа — видимый, чтобы колонки не слипались.</summary>
    private const string CellGap = "   ";

    public static DocReadResult Read(string path, int maxFileMb = DocTextExtractor.DefaultMaxFileMb,
                                     CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return DocReadResult.Of(DocTextStatus.Failed);

        var ext = DocTextExtractor.ExtensionOf(path);
        if (Array.IndexOf(DocTextExtractor.SupportedExtensions, ext) < 0)
            return DocReadResult.Of(DocTextStatus.Unsupported);

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return DocReadResult.Of(DocTextStatus.Failed);
            if (maxFileMb > 0 && info.Length > (long)maxFileMb * 1024 * 1024)
                return DocReadResult.Of(DocTextStatus.TooBig);

            var pages = 0;
            var parts = ext switch
            {
                "pdf"            => FromPdf(path, ct, out pages),
                "docx"           => FromDocx(path, ct),
                "xlsx" or "xlsm" => FromXlsx(path, ct),
                _                => FromPlainText(path),
            };

            // Пустые куски выбрасываем ЗДЕСЬ, а не в разборщиках: пустая страница скана и пустой
            // лист книги — это не текст, и подпись «Страница 7» над пустотой только сбивает.
            var kept = parts.Where(p => p.Text.Length > 0).ToList();
            return kept.Count == 0
                ? new DocReadResult(DocTextStatus.NoText, [], pages)
                : new DocReadResult(DocTextStatus.Ok, kept, pages);
        }
        catch (OperationCanceledException) { throw; }
        catch { return DocReadResult.Of(DocTextStatus.Failed); }
    }

    /// <summary>Фоновое чтение: разбор чужого файла на UI-потоке недопустим.</summary>
    public static Task<DocReadResult> ReadAsync(string path, int maxFileMb = DocTextExtractor.DefaultMaxFileMb,
                                                CancellationToken ct = default) =>
        Task.Run(() => Read(path, maxFileMb, ct), ct);

    // ── Разборщики ────────────────────────────────────────────────────────────

    private static List<DocPart> FromPdf(string path, CancellationToken ct, out int pageCount)
    {
        var parts = new List<DocPart>();
        using var doc = PdfDocument.Open(path);

        // Страниц ВСЕГО, а не сколько разобрали: подпись «Страница 7 из 900» обязана говорить
        // правду об объеме документа, даже когда дальше трехсотой мы не читаем.
        pageCount = doc.NumberOfPages;
        var read  = Math.Min(doc.NumberOfPages, DocTextExtractor.MaxPdfPages);
        var total = 0;

        for (var i = 1; i <= read; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (total >= DocTextExtractor.MaxChars) break;

            string text;
            try { text = ContentOrderTextExtractor.GetText(doc.GetPage(i)); }
            catch (OperationCanceledException) { throw; }
            catch { continue; }   // битая страница не отменяет весь документ

            var body = NormalizeForRead(text);
            total += body.Length;
            parts.Add(new DocPart($"Страница {i}", body));
        }
        return parts;
    }

    /// <summary>
    /// Абзацы документа Word. Именно абзацами, а не <c>InnerText</c> целиком (так берет текст
    /// разбор для индекса): <c>InnerText</c> склеивает весь документ в одну строку — для поиска
    /// это неважно, а для чтения означает стену текста без единого перевода строки.
    /// </summary>
    private static List<DocPart> FromDocx(string path, CancellationToken ct)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return [];

        var sb = new StringBuilder();
        foreach (var p in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
        {
            ct.ThrowIfCancellationRequested();
            if (sb.Length >= DocTextExtractor.MaxChars) break;

            var line = p.InnerText;
            if (line.Length == 0) continue;
            sb.Append(line).Append('\n');
        }
        return [new DocPart("", NormalizeForRead(sb.ToString()))];
    }

    /// <summary>
    /// Книга Excel — по листу на кусок, строка листа на строку текста. Значения берутся из
    /// самих ячеек с разворотом ссылок в общую таблицу строк; разбор для индекса вместо этого
    /// вываливает всю общую таблицу подряд — для поиска этого достаточно, а читать такое нельзя:
    /// там нет ни строк, ни листов, и один и тот же текст идет дважды.
    /// </summary>
    private static List<DocPart> FromXlsx(string path, CancellationToken ct)
    {
        var parts = new List<DocPart>();
        using var doc = SpreadsheetDocument.Open(path, false);
        var book = doc.WorkbookPart;
        if (book is null) return parts;

        var shared = book.SharedStringTablePart?.SharedStringTable
                         ?.Elements<SharedStringItem>().Select(i => i.InnerText).ToArray() ?? [];

        // Имя листа лежит не в самом листе, а в книге — связываются они идентификатором связи.
        var names = book.Workbook?.Sheets?.Elements<Sheet>()
                        .Where(s => s.Id?.Value is not null)
                        .ToDictionary(s => s.Id!.Value!, s => s.Name?.Value ?? "") ?? [];

        var total = 0;
        foreach (var part in book.WorksheetParts)
        {
            ct.ThrowIfCancellationRequested();
            if (total >= DocTextExtractor.MaxChars) break;

            var id   = book.GetIdOfPart(part);
            var name = names.TryGetValue(id, out var n) && n.Length > 0 ? n : "Лист";
            var data = part.Worksheet?.Elements<SheetData>().FirstOrDefault();
            if (data is null) continue;

            var sb   = new StringBuilder();
            var rows = 0;
            foreach (var row in data.Elements<Row>())
            {
                ct.ThrowIfCancellationRequested();
                if (++rows > MaxSheetRows || sb.Length >= DocTextExtractor.MaxChars) break;

                var cells = row.Elements<Cell>().Select(c => CellText(c, shared))
                               .Where(v => v.Length > 0);
                var line = string.Join(CellGap, cells);
                if (line.Length > 0) sb.Append(line).Append('\n');
            }

            var text = NormalizeForRead(sb.ToString());
            total += text.Length;
            parts.Add(new DocPart($"Лист «{name}»", text));
        }
        return parts;
    }

    private static string CellText(Cell cell, string[] shared)
    {
        var raw = cell.CellValue?.InnerText ?? "";
        if (cell.DataType?.Value != CellValues.SharedString) return raw.Length > 0 ? raw : cell.InnerText;

        return int.TryParse(raw, out var idx) && idx >= 0 && idx < shared.Length ? shared[idx] : "";
    }

    private static List<DocPart> FromPlainText(string path) =>
        [new DocPart("", NormalizeForRead(ReadPlain(path)))];

    /// <summary>
    /// Чтение текстового файла с определением кодировки — то же правило, что у разбора для
    /// индекса: BOM, иначе UTF-8 строго, иначе windows-1251 (русский txt из старых систем).
    /// </summary>
    private static string ReadPlain(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (HasBom(bytes)) return new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true).ReadToEnd();

        try   { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return Cp1251().GetString(bytes); }
    }

    private static bool HasBom(byte[] b) =>
        (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) ||
        (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF)));

    private static Encoding Cp1251()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251);
        }
        catch { return Encoding.Latin1; }
    }

    /// <summary>
    /// Приведение текста к читаемому виду: пробелы и табуляции схлопываются, переносы строк
    /// ОСТАЮТСЯ, пустых строк подряд не больше одной, хвостовые пробелы срезаются.
    ///
    /// Отличие от <c>DocTextExtractor.Normalize</c> в одном: тот схлопывает и переносы тоже.
    /// Для индекса это правильно, для чтения — губительно.
    /// </summary>
    public static string NormalizeForRead(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        var sb      = new StringBuilder(Math.Min(raw.Length, DocTextExtractor.MaxChars));
        var spaces  = false;   // был пробел, еще не записанный
        var breaks  = 0;       // переводов строки подряд, уже записанных
        var written = false;   // хоть один видимый знак уже есть

        for (var i = 0; i < raw.Length && sb.Length < DocTextExtractor.MaxChars; i++)
        {
            var ch = raw[i];

            if (ch is '\n' or '\r')
            {
                // \r\n — ОДИН перевод строки, а не два: иначе под каждой строкой документа из
                // Windows появилась бы пустая.
                if (ch == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n') i++;
                // Пустых строк подряд не больше одной (два перевода); ведущие не пишем вовсе.
                if (written && breaks < 2) { sb.Append('\n'); breaks++; }
                spaces = false;
                continue;
            }

            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) { spaces = true; continue; }

            // Пробел переносится в текст только ВНУТРИ строки: после перевода он был бы
            // отступом, которого в исходнике не было.
            if (spaces && written && breaks == 0) sb.Append(' ');
            spaces  = false;
            breaks  = 0;
            written = true;
            sb.Append(ch);
        }

        return sb.ToString().TrimEnd();
    }
}
