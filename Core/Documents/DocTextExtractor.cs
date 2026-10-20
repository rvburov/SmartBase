using System.IO;
using System.Text;
using System.Threading;
using System.Xml;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace SmartBase.Core.Documents;

/// <summary>Чем закончился разбор документа. Число пишется в кеш — порядок менять нельзя.</summary>
public enum DocTextStatus
{
    /// <summary>Текст извлечен.</summary>
    Ok = 0,
    /// <summary>Формат поддержан, но текста в файле нет — обычно скан без текстового слоя.</summary>
    NoText = 1,
    /// <summary>Расширение не из списка поддержанных.</summary>
    Unsupported = 2,
    /// <summary>Файл не читается: битый, занят, нет прав, пропал с диска.</summary>
    Failed = 3,
    /// <summary>Файл тяжелее порога — намеренно не разбирался.</summary>
    TooBig = 4,
}

/// <summary>
/// Результат разбора: статус, текст (пустой для всего, кроме <see cref="DocTextStatus.Ok"/>) и
/// число страниц.
///
/// Страницы считаются ЗАОДНО: документ в этот момент уже открыт, и число страниц лежит рядом с
/// текстом — отдельного прохода оно не стоит. Поэтому у прочитанных документов оно есть, а у
/// непрочитанных не бывает, и карточка обещает ровно то, что знает. У форматов без страничной
/// разбивки (DOCX, XLSX, текст) остается 0.
/// </summary>
public readonly record struct DocText(DocTextStatus Status, string Text, int Pages = 0)
{
    public static DocText Of(DocTextStatus status) => new(status, "");
}

/// <summary>
/// Извлечение текста документа для поиска по содержимому. Чистая логика без UI и без базы:
/// на входе путь, на выходе строка. Кеширование, очередь и прогресс — дело
/// <c>Database/DocIndex/DocIndexService.cs</c>.
///
/// Поддержанные форматы закрыты списком намеренно: разбор чужого файла — это выполнение чужого
/// кода в парсере, и расширять список стоит осознанно, а не «заодно». OCR нет: PDF-скан без
/// текстового слоя честно получает <see cref="DocTextStatus.NoText"/> и больше не тревожится.
///
/// Все ограничители — здесь, и все они про одно: один документ не должен съесть столько памяти
/// и времени, что встанет очередь. Чертеж на 2000 страниц и выгрузка на сто мегабайт —
/// нормальные файлы в инженерной папке, и наткнуться на них программа обязана без последствий.
/// </summary>
public static class DocTextExtractor
{
    /// <summary>Сколько страниц PDF разбираем. Дальше текст для поиска все равно не нужен.</summary>
    public const int MaxPdfPages = 300;

    /// <summary>Потолок извлеченного текста в символах (~4 МБ) — дальше обрезаем.</summary>
    public const int MaxChars = 4_000_000;

    /// <summary>Порог размера файла по умолчанию, МБ: тяжелее — не разбираем вовсе.</summary>
    public const int DefaultMaxFileMb = 64;

    /// <summary>
    /// Тяжелее ли файл порога; 0 и меньше — порога нет. Проверка одна на все три разбора —
    /// индекс, режим «Текст» и таблицу книги: пока ее не было у таблицы, режим «Текст» тяжелую
    /// книгу отклонял, а «Как в документе» разбирал целиком.
    /// </summary>
    public static bool TooBig(long length, int maxFileMb) =>
        maxFileMb > 0 && length > (long)maxFileMb * 1024 * 1024;

    /// <summary>
    /// Текстовые форматы: читаются как есть, без разборщика. Этот же список дает на плитке
    /// ленту «ТЕКСТ» (<see cref="DocFormats"/>): лента обещает, что внутри ищется, и обещание
    /// держится ровно потому, что список один. Раньше их было два, и у INI, JSON, XML, YML и
    /// YAML лента «ТЕКСТ» стояла, а текст не читался.
    /// </summary>
    public static readonly string[] PlainTextExtensions =
        ["txt", "md", "csv", "log", "ini", "json", "xml", "yml", "yaml"];

    /// <summary>Расширения (без точки, в нижнем регистре), текст которых умеем читать.</summary>
    public static readonly string[] SupportedExtensions =
        ["pdf", "docx", "xlsx", "xlsm", .. PlainTextExtensions];

    /// <summary>
    /// Понятная человеку строка поддержанных форматов — для подсказок интерфейса. Собирается из
    /// <see cref="SupportedExtensions"/>, а не пишется руками: написанная руками, она отстала от
    /// списка на XLSM и LOG, и человек читал, что их текст не ищется.
    /// </summary>
    public static string SupportedText => string.Join(", ", SupportedExtensions.Select(e => e.ToUpperInvariant()));

    /// <summary>Расширение файла без точки в нижнем регистре; «» — расширения нет.</summary>
    public static string ExtensionOf(string path)
    {
        var name = path ?? "";
        var cut  = name.LastIndexOfAny(['/', '\\']);
        if (cut >= 0) name = name[(cut + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..].ToLowerInvariant() : "";
    }

    /// <summary>Умеем ли вообще читать текст такого файла (по расширению, файл не трогаем).</summary>
    public static bool IsSupported(string path) =>
        Array.IndexOf(SupportedExtensions, ExtensionOf(path)) >= 0;

    /// <summary>
    /// Редакция разбора формата. Кеш помнит, какой редакцией прочитан документ, и перепись
    /// считает прочитанное прежней редакцией ИЗМЕНЕННЫМ — следующее чтение его перечитает. Без
    /// этого улучшенный разбор доходил бы только до документов, которые с тех пор правили, а
    /// остальные так и искались бы по старому тексту.
    ///
    /// Поднимать — когда разбор начинает давать ДРУГОЙ текст, а не когда он просто стал быстрее:
    /// подъем стоит перечитывания всех документов формата.
    /// <list type="bullet">
    /// <item>книга, 1 — числа и даты так, как их показывает панель («05.03.2024», а не 45356),
    ///       логические значения словами; раньше — как они лежат в файле;</item>
    /// <item>INI, JSON, XML, YML, YAML, 1 — читаются как текст; раньше формат считался
    ///       нечитаемым, и такой файл в кеше помечен «формат не читается».</item>
    /// </list>
    /// </summary>
    public static int RevisionOf(string path) => ExtensionOf(path) switch
    {
        "xlsx" or "xlsm"                           => 1,
        "ini" or "json" or "xml" or "yml" or "yaml" => 1,
        _                                          => 0,
    };

    /// <summary>
    /// Разобрать документ. Ошибки файла становятся статусом; отмена выходит исключением,
    /// чтобы очередь остановилась, а не записала отмененный разбор как испорченный файл.
    /// </summary>
    /// <param name="maxFileMb">Порог размера; 0 и меньше — порога нет.</param>
    public static DocText Extract(string path, int maxFileMb = DefaultMaxFileMb,
                                  CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return DocText.Of(DocTextStatus.Failed);

        var ext = ExtensionOf(path);
        if (Array.IndexOf(SupportedExtensions, ext) < 0) return DocText.Of(DocTextStatus.Unsupported);

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return DocText.Of(DocTextStatus.Failed);
            if (TooBig(info.Length, maxFileMb)) return DocText.Of(DocTextStatus.TooBig);

            var pages = 0;
            var text = ext switch
            {
                "pdf"          => FromPdf(path, ct, out pages),
                "docx"         => FromDocx(path, ct),
                "xlsx" or "xlsm" => FromXlsx(path, ct),
                _              => FromPlainText(path),
            };

            text = Normalize(text);
            // Число страниц сохраняем и у скана без текстового слоя: страницы у него есть, а
            // текста нет, и «12 страниц» в карточке — правда, которая помогает узнать документ.
            return text.Length == 0
                ? new DocText(DocTextStatus.NoText, "", pages)
                : new DocText(DocTextStatus.Ok, text, pages);
        }
        catch (OperationCanceledException) { throw; }
        catch { return DocText.Of(DocTextStatus.Failed); }
    }

    // ── Разборщики ────────────────────────────────────────────────────────────

    /// <param name="pageCount">
    /// Сколько страниц в документе ВСЕГО — не сколько разобрано. Разбор упирается в
    /// <see cref="MaxPdfPages"/>, а карточка показывает настоящий объем документа: «300 страниц»
    /// у тысячестраничного тома было бы неправдой.
    /// </param>
    private static string FromPdf(string path, CancellationToken ct, out int pageCount)
    {
        var sb = new StringBuilder();
        using var doc = PdfDocument.Open(path);
        pageCount = doc.NumberOfPages;
        var pages = Math.Min(doc.NumberOfPages, MaxPdfPages);
        for (var i = 1; i <= pages; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (sb.Length >= MaxChars) break;
            // ContentOrderTextExtractor — тот же разборщик, что в расширении «Анализ PDF»:
            // он собирает текст в порядке содержимого, а не в порядке объектов страницы,
            // иначе куски строк чертежа приезжали бы вперемешку.
            try { sb.Append(ContentOrderTextExtractor.GetText(doc.GetPage(i))).Append('\n'); }
            catch (OperationCanceledException) { throw; }
            catch { /* битая страница не должна отменять весь документ */ }
        }
        return sb.ToString();
    }

    private static string FromDocx(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var doc = WordprocessingDocument.Open(path, false);
        if (doc.MainDocumentPart is not { } main) return "";
        using var stream = main.GetStream(FileMode.Open, FileAccess.Read);
        return ReadDocxText(stream, ct);
    }

    // Читаем XML потоком: построение всего Body перед обходом не дает остановить большой
    // документ. Пробелы складываем сразу, чтобы потолок относился к тексту, а не к отступам XML.
    internal static string ReadDocxText(Stream stream, CancellationToken ct)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreWhitespace = false,
        });
        var text = new StringBuilder();
        var body = false;
        var textElement = false;
        var space = true;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.LocalName == "body" && reader.NamespaceURI is
                "http://schemas.openxmlformats.org/wordprocessingml/2006/main" or
                "http://purl.oclc.org/ooxml/wordprocessingml/main")
            {
                if (reader.NodeType == XmlNodeType.EndElement) break;
                if (reader.NodeType == XmlNodeType.Element) body = true;
            }
            if (reader.NodeType == XmlNodeType.Element)
                textElement = !reader.IsEmptyElement &&
                              reader.LocalName is "t" or "delText" or "instrText" or "delInstrText";
            else if (reader.NodeType == XmlNodeType.EndElement) textElement = false;
            if (!body) continue;
            if (reader.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA) &&
                !(textElement && reader.NodeType is XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)) continue;
            foreach (var ch in reader.Value)
            {
                if ((text.Length & 1023) == 0) ct.ThrowIfCancellationRequested();
                if (text.Length >= MaxChars) return text.ToString().TrimEnd();
                if (char.IsWhiteSpace(ch) || char.IsControl(ch))
                {
                    if (!space) { text.Append(' '); space = true; }
                }
                else { text.Append(ch); space = false; }
            }
        }
        ct.ThrowIfCancellationRequested();
        return text.ToString().TrimEnd();
    }

    private static string FromXlsx(string path, CancellationToken ct)
    {
        var sb = new StringBuilder();
        using var doc = SpreadsheetDocument.Open(path, false);
        var book = doc.WorkbookPart;
        if (book is null) return "";

        // Общая таблица строк — в книге весь повторяющийся текст лежит именно там, и без нее
        // в ячейках видны одни индексы. Таблица и листы читаются потоком (SheetStream): дерево
        // листа на сотни тысяч строк занимало гигабайты памяти ради текста, который пишется
        // строка за строкой.
        foreach (var text in SheetStream.SharedStrings(book.SharedStringTablePart, ct))
        {
            if (sb.Length >= MaxChars) break;
            sb.Append(text).Append('\n');
        }

        // Плюс то, что лежит в ячейках напрямую: числа, даты, логические значения, строки внутри
        // ячеек. Значение пишется ТАК, КАК ЕГО ПОКАЗЫВАЕТ ПАНЕЛЬ (DocSheetSource.Text): дата —
        // «05.03.2024», а не 45356, деньги — «30 772,48», а не 30772.48. Пока индекс хранил
        // числа как они лежат в файле, по тому, что человек видит, книга не находилась, а
        // найденное по сырому числу панели было нечем подсветить.
        //
        // Листы — в порядке корешков, и скрытые тоже: поиск ищет и в них сознательно, чтобы не
        // терять находки в свернутых группах и служебных листах.
        //
        // Сломанная таблица оформления — не повод терять числа: они запишутся без форматов.
        string[] formats;
        try { formats = DocSheetSource.NumberFormats(book); }
        catch (OperationCanceledException) { throw; }
        catch { formats = []; }

        var date1904 = DocSheetSource.Date1904(book);
        foreach (var (_, sheet) in DocSheetSource.Worksheets(book))
        {
            ct.ThrowIfCancellationRequested();
            if (sb.Length >= MaxChars) break;
            foreach (var row in SheetStream.Rows(sheet, ct))
            {
                if (sb.Length >= MaxChars) break;
                foreach (var cell in row.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>())
                {
                    if (sb.Length >= MaxChars) break;
                    // Ссылки в общую таблицу уже добавлены выше — второй раз не пишем.
                    if (cell.DataType?.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString) continue;
                    var format = cell.StyleIndex?.Value is { } id && id < formats.Length ? formats[id] : null;
                    var value  = DocSheetSource.Text(cell, [], format, date1904).Text;
                    if (value.Length > 0) sb.Append(value).Append(' ');
                }
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string FromPlainText(string path)
    {
        // BOM распознается сам; без BOM пробуем UTF-8 строго, и только если строка не
        // раскодировалась — откатываемся на windows-1251. Русский txt из старых систем без
        // этого отката выглядел бы как набор вопросительных знаков и в поиск бы не попал.
        var bytes = File.ReadAllBytes(path);
        if (HasBom(bytes)) return new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true).ReadToEnd();

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Cp1251().GetString(bytes);
        }
    }

    private static bool HasBom(byte[] b) =>
        (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) ||
        (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF)));

    // .NET Core не знает кодовых страниц без явной регистрации провайдера; делаем это лениво и
    // один раз, а если страница все равно недоступна — читаем как Latin-1, чтобы не упасть.
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
    /// Схлопывает пробелы и переводы строк: в индексе важны слова, а не верстка, а лишние
    /// пробелы PDF-извлечения раздували бы кеш вдвое. Заодно режет по <see cref="MaxChars"/>.
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        var sb    = new StringBuilder(Math.Min(raw.Length, MaxChars));
        var space = true;   // true в начале — ведущие пробелы не пишем
        foreach (var ch in raw)
        {
            if (sb.Length >= MaxChars) break;
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                if (!space) { sb.Append(' '); space = true; }
                continue;
            }
            sb.Append(ch);
            space = false;
        }
        return sb.ToString().TrimEnd();
    }
}
