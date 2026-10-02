using System.IO;
using System.Text;
using System.Threading;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace SmartBase.Core.Documents;

/// <summary>Кусок документа для чтения: подпись места и его текст.</summary>
/// <param name="Label">«Страница 4», «Лист „Смета“»; пусто — у форматов без такого деления.</param>
public readonly record struct DocPart(string Label, string Text);

/// <summary>
/// Что разбор для чтения показал не целиком — чтобы панель сказала об этом, а не оборвала
/// документ молча.
/// </summary>
/// <param name="PagesShown">Сколько страниц PDF прочитано; всего их — <c>DocReadResult.Pages</c>.</param>
/// <param name="Text">Текст оборван потолком знаков (<see cref="DocTextExtractor.MaxChars"/>).</param>
/// <param name="HiddenSheets">Скрытых листов книги — их не показывает и сам Excel.</param>
/// <param name="Sheets">Листы книги, показанные не целиком.</param>
public readonly record struct DocReadCut(int PagesShown = 0, bool Text = false, int HiddenSheets = 0,
                                         IReadOnlyList<(string Name, DocSheetCut Cut)>? Sheets = null)
{
    // Поле, а не свойство с инициализатором: у значения по умолчанию (а им панель сбрасывает
    // разбор) инициализатор не выполняется, и свойство осталось бы null.
    private readonly IReadOnlyList<(string Name, DocSheetCut Cut)>? _sheets = Sheets;

    public IReadOnlyList<(string Name, DocSheetCut Cut)> Sheets => _sheets ?? [];
}

/// <summary>Документ, разобранный для чтения: статус, куски по порядку, число страниц и обрез.</summary>
public readonly record struct DocReadResult(DocTextStatus Status, IReadOnlyList<DocPart> Parts, int Pages = 0,
                                            DocReadCut Cut = default)
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
            DocReadCut cut;
            var parts = ext switch
            {
                "pdf"            => FromPdf(path, ct, out pages, out cut),
                "docx"           => FromDocx(path, ct, out cut),
                "xlsx" or "xlsm" => FromXlsx(path, ct, out cut),
                _                => FromPlainText(path, out cut),
            };

            // Пустые куски выбрасываем ЗДЕСЬ, а не в разборщиках: пустая страница скана и пустой
            // лист книги — это не текст, и подпись «Страница 7» над пустотой только сбивает.
            var kept = parts.Where(p => p.Text.Length > 0).ToList();
            return kept.Count == 0
                ? new DocReadResult(DocTextStatus.NoText, [], pages, cut)
                : new DocReadResult(DocTextStatus.Ok, kept, pages, cut);
        }
        catch (OperationCanceledException) { throw; }
        catch { return DocReadResult.Of(DocTextStatus.Failed); }
    }

    /// <summary>Фоновое чтение: разбор чужого файла на UI-потоке недопустим.</summary>
    public static Task<DocReadResult> ReadAsync(string path, int maxFileMb = DocTextExtractor.DefaultMaxFileMb,
                                                CancellationToken ct = default) =>
        Task.Run(() => Read(path, maxFileMb, ct), ct);

    // ── Разборщики ────────────────────────────────────────────────────────────

    private static List<DocPart> FromPdf(string path, CancellationToken ct, out int pageCount,
                                         out DocReadCut cut)
    {
        var parts = new List<DocPart>();
        using var doc = PdfDocument.Open(path);

        // Страниц ВСЕГО, а не сколько разобрали: подпись «Страница 7 из 900» обязана говорить
        // правду об объеме документа, даже когда дальше трехсотой мы не читаем.
        pageCount = doc.NumberOfPages;
        var read  = Math.Min(doc.NumberOfPages, DocTextExtractor.MaxPdfPages);
        var total = 0;
        var shown = 0;
        var full  = false;

        for (var i = 1; i <= read; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (total >= DocTextExtractor.MaxChars) { full = true; break; }
            shown = i;

            string text;
            try { text = ContentOrderTextExtractor.GetText(doc.GetPage(i)); }
            catch (OperationCanceledException) { throw; }
            catch { continue; }   // битая страница не отменяет весь документ

            var body = NormalizeForRead(text);
            total += body.Length;
            parts.Add(new DocPart($"Страница {i}", body));
        }

        cut = new DocReadCut(PagesShown: shown, Text: full);
        return parts;
    }

    /// <summary>
    /// Абзацы документа Word. Именно абзацами, а не <c>InnerText</c> целиком (так берет текст
    /// разбор для индекса): <c>InnerText</c> склеивает весь документ в одну строку — для поиска
    /// это неважно, а для чтения означает стену текста без единого перевода строки.
    /// </summary>
    private static List<DocPart> FromDocx(string path, CancellationToken ct, out DocReadCut cut)
    {
        cut = default;
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return [];

        var sb = new StringBuilder();
        foreach (var p in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
        {
            ct.ThrowIfCancellationRequested();
            if (sb.Length >= DocTextExtractor.MaxChars) { cut = new DocReadCut(Text: true); break; }

            var line = p.InnerText;
            if (line.Length == 0) continue;
            sb.Append(line).Append('\n');
        }
        return [new DocPart("", NormalizeForRead(sb.ToString()))];
    }

    /// <summary>
    /// Книга Excel — по листу на кусок, строка листа на строку текста.
    ///
    /// Листы, строки и значения берутся из ТОЙ ЖЕ модели листа, по которой рисуется сетка
    /// режима «Как в документе» (<see cref="DocSheetSource"/>). Раньше у режима был свой обход
    /// книги — по частям пакета, а не по корешкам, без пропуска скрытых листов и строк и с
    /// сырыми числами вместо дат, — и, переключив режим, человек видел другие листы в другом
    /// порядке. Теперь правило одно: листы в порядке корешков, скрытые листы, строки и колонки
    /// не показываются (как и в самом Excel), значения — в формате своей ячейки.
    ///
    /// Разбор для индекса устроен иначе — он вываливает всю общую таблицу строк подряд. Для
    /// поиска этого достаточно, а читать такое нельзя: там нет ни строк, ни листов.
    /// </summary>
    private static List<DocPart> FromXlsx(string path, CancellationToken ct, out DocReadCut cut)
    {
        var book  = DocSheetSource.Read(path, ct);
        var parts = new List<DocPart>();
        var cuts  = new List<(string, DocSheetCut)>();
        var total = 0;
        var full  = false;

        foreach (var sheet in book.Sheets)
        {
            ct.ThrowIfCancellationRequested();
            if (total >= DocTextExtractor.MaxChars) { full = true; break; }

            var sb = new StringBuilder();
            foreach (var row in sheet.Rows)
            {
                // Ячейка скрытой колонки места на листе не имеет — в сетке у нее нулевая
                // ширина, и текстом ее тоже не показываем.
                var line = string.Join(CellGap, row.Cells.Where(c => sheet.WidthOf(c.Column) > 0)
                                                         .Select(c => c.Text));
                if (line.Length > 0) sb.Append(line).Append('\n');
                if (sb.Length >= DocTextExtractor.MaxChars) { full = true; break; }
            }

            var text = NormalizeForRead(sb.ToString());
            total += text.Length;
            parts.Add(new DocPart($"Лист «{sheet.Name}»", text));
            if (sheet.Cut.Any) cuts.Add((sheet.Name, sheet.Cut));
        }

        cut = new DocReadCut(Text: full, HiddenSheets: book.HiddenSheets, Sheets: cuts);
        return parts;
    }

    private static List<DocPart> FromPlainText(string path, out DocReadCut cut)
    {
        var text = NormalizeForRead(ReadPlain(path), out var full);
        cut = new DocReadCut(Text: full);
        return [new DocPart("", text)];
    }

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
    public static string NormalizeForRead(string? raw) => NormalizeForRead(raw, out _);

    /// <summary>То же с признаком, что текст уперся в потолок знаков и обрезан.</summary>
    public static string NormalizeForRead(string? raw, out bool cut)
    {
        cut = false;
        if (string.IsNullOrEmpty(raw)) return "";

        var sb      = new StringBuilder(Math.Min(raw.Length, DocTextExtractor.MaxChars));
        var spaces  = false;   // был пробел, еще не записанный
        var breaks  = 0;       // переводов строки подряд, уже записанных
        var written = false;   // хоть один видимый знак уже есть

        var i = 0;
        for (; i < raw.Length && sb.Length < DocTextExtractor.MaxChars; i++)
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

        // Остановились на потолке, а дальше есть видимый текст — значит, обрезали.
        cut = i < raw.Length && raw.AsSpan(i).ContainsAnyExcept(" \t\r\n");
        return sb.ToString().TrimEnd();
    }
}
