using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using ClosedXML.Parser;

namespace SmartBase.Extensions.TimeCollector;

/// <summary>
/// Запись свода на лист «Таблица» .xlsm-шаблона ПОТОКОМ — порт того, что макрос делал прямо
/// в книге (<c>ClearWS</c> → <c>SetHeaderData</c> → запись массива → <c>ApplyFastFormatting</c>).
///
/// Почему не ClosedXML, как у остальных расширений: он держит книгу в памяти целиком, и свод
/// в 600 тыс. строк — это 4,8 млн объектов ячеек. Замер: 75 секунд сверх чтения и пик памяти
/// процесса ~1,9 ГБ. Здесь строка уходит в файл сразу и в памяти не задерживается.
///
/// Почему не OpenXml SDK поверх готового файла: .xlsm — это zip, а <see cref="ZipArchive"/>
/// в режиме <see cref="ZipArchiveMode.Update"/> держит КАЖДУЮ часть в памяти до закрытия
/// архива, и SDK открывает пакет именно так. Замер на листе в 600 тыс. строк: Update — 663 МБ,
/// Create — 32 МБ. Поэтому результат собирается НОВЫМ архивом: части шаблона копируются в него
/// побайтно, а лист свода пишется на лету.
///
/// Побочный выигрыш побайтного копирования — целость шаблона: <c>vbaProject.bin</c> с макросами,
/// картинки, прочие листы переезжают ровно теми байтами, что были. У самого листа свода
/// сохраняются его «обвязка» (<c>sheetPr</c> с <c>codeName</c> — без него отвалятся обработчики
/// событий листа, настройки вида, ширины колонок, привязки рисунков) и хвост (поля печати,
/// колонтитулы). Заменяются только строки, автофильтр и объединения шапки.
/// </summary>
internal static class TimeSheetWriter
{
    private const string Ns    = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NsPkg = "http://schemas.openxmlformats.org/package/2006/relationships";

    // Колонки свода (1-based) — те же константы, что COL_* в модуле TimeCollector.
    internal const int ColRNum = 1;   // номер строки листа «Расшифровка» − 1, колонка скрытая
    internal const int ColName = 2;
    internal const int ColDeno = 3;
    internal const int ColNorm = 4;
    internal const int ColDate = 5;
    internal const int ColEfio = 6;
    internal const int ColProj = 7;
    internal const int ColLink = 8;
    internal const int LastCol = ColLink;

    /// <summary>Шапка занимает строки 1–3, данные начинаются с 4-й (TOP_INDENT макроса).</summary>
    internal const int TopIndent = 3;

    /// <summary>Строк на листе Excel. Свод должен влезть вместе с тремя строками шапки: файл
    /// с лишними строками записался бы молча, а Excel открыть его уже не смог.</summary>
    internal const int ExcelMaxRows = 1_048_576;

    internal const string NamedRange  = "ALL_DATA";
    private  const string SourceSheet = "Расшифровка";
    private  const string LinkMark    = ">>>";

    // Буквы колонок: их ровно восемь, считать в цикле незачем.
    private static readonly string[] Letters = ["A", "B", "C", "D", "E", "F", "G", "H"];

    // Символы, при которых формулу HYPERLINK не собрать: кавычка рвет строку, скобки — адрес.
    private static readonly char[] BadLinkChars = ['"', '[', ']'];

    // Прогресс постится не на каждую строку: поток сообщений в UI тормозит сильнее записи.
    private const int ProgressEvery = 5_000;

    /// <summary>Подпись колонки в шапке (колонка A служебная и подписи не получает).</summary>
    private static string Header(int col) => col switch
    {
        ColName => "Наименование",
        ColDeno => "Обозначение КД",
        ColNorm => "Тр-ть",
        ColDate => "Дата",
        ColEfio => "ФИО",
        ColProj => "Проект",
        ColLink => "Ссылка",
        _       => "",
    };

    /// <summary>
    /// Собрать результат: копия <paramref name="templatePath"/>, в которой лист
    /// <see cref="TimeCollectorEngine.TargetSheet"/> заменен сводом. Оригинал не трогается.
    /// </summary>
    /// <exception cref="TimeCollectException">Шаблон не читается или в нем нет нужного листа.</exception>
    internal static void Write(
        string templatePath, string outputPath,
        IReadOnlyList<TimeRow> rows, TimeCollectorEngine.RowPaint[] paints, IReadOnlySet<int> hidden,
        IProgress<CollectProgress>? progress, CancellationToken ct,
        double from, double to)
    {
        EnsureFits(rows.Count);

        progress?.Report(new CollectProgress(from, "Открываю шаблон…", Path.GetFileName(templatePath)));

        using var source = OpenTemplate(templatePath);
        var sheetPath = FindSheetPath(source, TimeCollectorEngine.TargetSheet)
            ?? throw new TimeCollectException(
                $"Лист «{TimeCollectorEngine.TargetSheet}» не найден в файле-шаблоне.");

        // Обвязку листа читаем ДО начала записи, и строки прошлого свода при этом пропускаются
        // не читая: шаблоном вполне может оказаться прошлый результат на те же сотни тысяч строк.
        var shell  = ReadSheetShell(source, sheetPath);
        var styles = TimeSheetStyles.Prepare(source);

        using var outFile = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var target  = new ZipArchive(outFile, ZipArchiveMode.Create);

        foreach (var entry in source.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.FullName == sheetPath)
                WriteSheet(target, entry.FullName, shell, styles, rows, paints, hidden, progress, ct, from, to);
            else if (entry.FullName == TimeSheetStyles.PartPath)
                CopyText(target, entry.FullName, styles.Xml);
            else if (entry.FullName == "xl/workbook.xml")
                CopyText(target, entry.FullName, RewriteWorkbook(entry, rows.Count));
            else
                CopyRaw(target, entry);
        }
    }

    /// <summary>Влезает ли свод на лист. Проверяем ДО записи: узнать про предел из отказа
    /// Excel открыть готовый файл — худший способ.</summary>
    internal static void EnsureFits(int rowCount)
    {
        var room = ExcelMaxRows - TopIndent;
        if (rowCount <= room) return;

        throw new TimeCollectException(
            $"В своде {rowCount:N0} строк, а лист Excel вмещает {room:N0}.\n" +
            "Сузьте папку с расчетами или выгрузите свод в коллекцию программы — у нее предела нет.");
    }

    private static ZipArchive OpenTemplate(string path)
    {
        try { return ZipFile.OpenRead(path); }
        catch (Exception ex)
        {
            throw new TimeCollectException(
                $"Не удалось открыть файл-шаблон:\n{Path.GetFileName(path)}\n{ex.Message}\n" +
                "Проверьте, что это корректный Excel (.xlsm) и он не открыт в другой программе.");
        }
    }

    // ── Копирование частей ────────────────────────────────────────────────────

    private static void CopyRaw(ZipArchive target, ZipArchiveEntry entry)
    {
        var copy = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
        using var from = entry.Open();
        using var to   = copy.Open();
        from.CopyTo(to);
    }

    private static void CopyText(ZipArchive target, string path, string xml)
    {
        var copy = target.CreateEntry(path, CompressionLevel.Optimal);
        using var to = copy.Open();
        using var w  = new StreamWriter(to, new UTF8Encoding(false));
        w.Write(xml);
    }

    // ── Где лежит лист ────────────────────────────────────────────────────────

    /// <summary>Путь части листа по его ИМЕНИ: имя → <c>r:id</c> в <c>xl/workbook.xml</c> →
    /// цель связи в <c>xl/_rels/workbook.xml.rels</c>.</summary>
    private static string? FindSheetPath(ZipArchive zip, string sheetName)
    {
        var book = ReadXml(zip, "xl/workbook.xml");
        var rels = ReadXml(zip, "xl/_rels/workbook.xml.rels");
        if (book is null || rels is null) return null;

        var id = book.Descendants(XName.Get("sheet", Ns))
            .FirstOrDefault(s => string.Equals((string?)s.Attribute("name"), sheetName,
                                               StringComparison.OrdinalIgnoreCase))
            ?.Attribute(XName.Get("id", NsRel))?.Value;
        if (id is null) return null;

        var target = rels.Descendants(XName.Get("Relationship", NsPkg))
            .FirstOrDefault(r => (string?)r.Attribute("Id") == id)
            ?.Attribute("Target")?.Value;
        if (target is null) return null;

        // Цель связи задана относительно папки xl/, но может прийти и абсолютной («/xl/…»):
        // обе формы встречаются у разных генераторов книг.
        target = target.Replace('\\', '/').TrimStart('/');
        return target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : "xl/" + target;
    }

    private static XDocument? ReadXml(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        using var s = entry.Open();
        try { return XDocument.Load(s); }
        catch (XmlException) { return null; }
    }

    // ── Обвязка листа ─────────────────────────────────────────────────────────

    /// <summary>Все, что на листе НЕ строки: элементы до <c>sheetData</c> и после него, плюс
    /// отдельно ширины колонок — их мы не выбрасываем, а лишь помечаем скрытые.</summary>
    private sealed record SheetShell(List<(string Name, string Xml)> Head, XElement? Cols, List<string> Tail);

    /// <summary>Порядок элементов листа ДО строк — задан схемой, и Excel считает книгу битой,
    /// если его нарушить. Из этого списка мы формируем заново все, кроме первых двух: их берем
    /// из шаблона и должны вернуть на свои места между нашими.</summary>
    private static readonly string[] HeadOrder = ["sheetPr", "dimension", "sheetViews", "sheetFormatPr", "cols"];

    private static int HeadRank(string name)
    {
        var i = Array.IndexOf(HeadOrder, name);
        return i < 0 ? HeadOrder.Length : i;   // незнакомое — в конец, ближе к sheetData
    }

    // Элементы, которые формируются заново: из шаблона их брать не нужно.
    private static readonly HashSet<string> Rebuilt =
        new(StringComparer.Ordinal) { "dimension", "sheetFormatPr", "cols", "sheetData", "autoFilter", "mergeCells" };

    private static SheetShell ReadSheetShell(ZipArchive zip, string sheetPath)
    {
        var head = new List<(string Name, string Xml)>();
        var tail = new List<string>();
        XElement? cols = null;

        var entry = zip.GetEntry(sheetPath);
        if (entry is null) return new SheetShell(head, cols, tail);

        using var s = entry.Open();
        using var r = XmlReader.Create(s, new XmlReaderSettings { IgnoreWhitespace = true });
        if (!r.ReadToDescendant("worksheet", Ns)) return new SheetShell(head, cols, tail);
        if (r.IsEmptyElement) return new SheetShell(head, cols, tail);
        r.ReadStartElement();

        // Читатель двигают САМИ операции: и Skip, и ReadOuterXml оставляют его на следующем
        // узле. Поэтому в цикле нет «лишнего» Read — с ним терялся бы каждый второй элемент
        // (наступали на это: printOptions уезжал в начало листа, и Excel считал файл битым).
        var seenData = false;
        while (!r.EOF && r.NodeType != XmlNodeType.EndElement)
        {
            if (r.NodeType != XmlNodeType.Element) { r.Read(); continue; }

            switch (r.LocalName)
            {
                case "sheetData":
                    // Строки прошлого свода не читаем вовсе — их может быть сотни тысяч.
                    seenData = true;
                    r.Skip();
                    break;

                case "cols":
                    cols = XElement.Parse(r.ReadOuterXml());
                    break;

                default:
                    if (Rebuilt.Contains(r.LocalName)) { r.Skip(); break; }
                    var name = r.LocalName;
                    var xml  = r.ReadOuterXml();
                    if (seenData) tail.Add(xml);
                    else          head.Add((name, xml));
                    break;
            }
        }

        return new SheetShell(head, cols, tail);
    }

    // ── Лист ──────────────────────────────────────────────────────────────────

    private static void WriteSheet(
        ZipArchive target, string path, SheetShell shell, TimeSheetStyles styles,
        IReadOnlyList<TimeRow> rows, TimeCollectorEngine.RowPaint[] paints, IReadOnlySet<int> hidden,
        IProgress<CollectProgress>? progress, CancellationToken ct, double from, double to)
    {
        var entry = target.CreateEntry(path, CompressionLevel.Optimal);
        using var s = entry.Open();
        using var w = XmlWriter.Create(s, new XmlWriterSettings
        {
            Encoding    = new UTF8Encoding(false),
            CloseOutput = false,
        });

        var last = TopIndent + rows.Count;

        w.WriteStartDocument(standalone: true);
        w.WriteStartElement("worksheet", Ns);
        w.WriteAttributeString("xmlns", "r", null, NsRel);

        // Элементы шаблона расставляются вокруг наших по схемному порядку: то, что идет до
        // dimension (это sheetPr с codeName), — раньше него, остальное (настройки вида) — после.
        var dimension = HeadRank("dimension");
        foreach (var (name, xml) in shell.Head) if (HeadRank(name) < dimension) w.WriteRaw(xml);

        w.WriteStartElement("dimension", Ns);
        w.WriteAttributeString("ref", $"A1:{Letters[^1]}{last}");
        w.WriteEndElement();

        foreach (var (name, xml) in shell.Head) if (HeadRank(name) >= dimension) w.WriteRaw(xml);

        // Высота строк — ДЕФОЛТОМ листа: проход по сотням тысяч строк ради одинаковой высоты
        // не нужен, а шапке высота проставлена явно и дефолт перебивает.
        w.WriteStartElement("sheetFormatPr", Ns);
        w.WriteAttributeString("defaultRowHeight", "15");
        w.WriteEndElement();

        WriteCols(w, shell.Cols, hidden);

        w.WriteStartElement("sheetData", Ns);
        WriteHeaderRows(w, styles);
        WriteDataRows(w, styles, rows, paints, progress, ct, from, to);
        w.WriteEndElement();

        // Порядок после sheetData задан схемой: autoFilter раньше mergeCells, и оба раньше
        // хвоста шаблона (поля печати, колонтитулы, рисунки).
        w.WriteStartElement("autoFilter", Ns);
        w.WriteAttributeString("ref", $"A{TopIndent}:{Letters[^1]}{last}");
        w.WriteEndElement();

        WriteMerges(w);

        foreach (var xml in shell.Tail) w.WriteRaw(xml);

        w.WriteEndElement();
        w.WriteEndDocument();
    }

    /// <summary>
    /// Ширины колонок берутся ИЗ ШАБЛОНА — их настраивал тот, кто шаблон делал. Мы только
    /// расставляем скрытие: колонка A служебная (номер строки источника нужен макросу) и
    /// скрыта всегда, остальные прячутся, если во всем своде пусты, а прежнее скрытие с них
    /// снимается — иначе колонка, пустовавшая в прошлый раз, осталась бы невидимой.
    /// </summary>
    private static void WriteCols(XmlWriter w, XElement? template, IReadOnlySet<int> hidden)
    {
        var kept = new List<XElement>();
        var seen = new HashSet<int>();

        foreach (var col in template?.Elements(XName.Get("col", Ns)) ?? [])
        {
            var min = (int?)col.Attribute("min") ?? 0;
            var max = (int?)col.Attribute("max") ?? min;
            if (min <= 0) continue;

            // Диапазон шаблона может накрывать сразу несколько наших колонок — режем его на
            // отдельные, чтобы скрыть ровно нужные и не тронуть соседей.
            if (min <= LastCol && max >= 1)
            {
                for (var c = min; c <= max; c++)
                {
                    var one = new XElement(col);
                    one.SetAttributeValue("min", c);
                    one.SetAttributeValue("max", c);
                    if (c <= LastCol)
                    {
                        seen.Add(c);
                        SetHidden(one, c == ColRNum || hidden.Contains(c));
                    }
                    kept.Add(one);
                }
            }
            else
            {
                kept.Add(new XElement(col));
            }
        }

        // Колонки, которых в шаблоне не было: ширина остается дефолтной, важен только флаг.
        for (var c = 1; c <= LastCol; c++)
        {
            if (seen.Contains(c)) continue;
            var one = new XElement(XName.Get("col", Ns),
                new XAttribute("min", c), new XAttribute("max", c));
            SetHidden(one, c == ColRNum || hidden.Contains(c));
            kept.Add(one);
        }

        if (kept.Count == 0) return;

        w.WriteStartElement("cols", Ns);
        foreach (var col in kept.OrderBy(e => (int?)e.Attribute("min") ?? 0)) w.WriteRaw(col.ToString());
        w.WriteEndElement();

        static void SetHidden(XElement col, bool hide)
        {
            if (hide) col.SetAttributeValue("hidden", "1");
            else      col.Attribute("hidden")?.Remove();
        }
    }

    private static void WriteHeaderRows(XmlWriter w, TimeSheetStyles styles)
    {
        // Строка 1 — подписи; 2-я объединена с ней (подпись занимает две строки), 3-я — строка
        // автофильтра. Высоты те же, что ставил макрос.
        w.WriteStartElement("row", Ns);
        w.WriteAttributeString("r", "1");
        w.WriteAttributeString("ht", "30");
        w.WriteAttributeString("customHeight", "1");
        for (var col = ColName; col <= ColLink; col++)
            WriteInlineString(w, col, 1, Header(col), styles.Head);
        w.WriteEndElement();

        WriteEmptyRow(w, 2, "100");
        WriteEmptyRow(w, 3, "13");
    }

    private static void WriteEmptyRow(XmlWriter w, int row, string height)
    {
        w.WriteStartElement("row", Ns);
        w.WriteAttributeString("r", row.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("ht", height);
        w.WriteAttributeString("customHeight", "1");
        w.WriteEndElement();
    }

    private static void WriteDataRows(
        XmlWriter w, TimeSheetStyles styles,
        IReadOnlyList<TimeRow> rows, TimeCollectorEngine.RowPaint[] paints,
        IProgress<CollectProgress>? progress, CancellationToken ct, double from, double to)
    {
        var plain = new TimeCollectorEngine.RowPaint(TimeCollectorEngine.RowFill.White, false);
        var i     = 0;

        try
        {
            for (; i < rows.Count; i++)
            {
                // Отмена проверяется не на каждой строке: сам по себе токен дешев, но на
                // 600 тыс. строк лишние проверки заметны, а реакция раз в тысячу строк
                // мгновенна и так.
                if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();

                WriteRow(w, styles, rows[i], i < paints.Length ? paints[i] : plain, TopIndent + 1 + i);

                if (i % ProgressEvery == 0 || i == rows.Count - 1)
                    progress?.Report(new CollectProgress(
                        from + (to - from) * (i + 1) / rows.Count, "Запись строк", $"{i + 1} из {rows.Count}"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TimeCollectException)
        {
            // Свод склеен из тысяч расчетов, и сбой на записи почти всегда про данные одной
            // ячейки. Без имени файла и номера строки искать виновника в такой куче нечем.
            var file = i < rows.Count ? Path.GetFileName(rows[i].SourcePath) : "";
            throw new TimeCollectException(
                $"Сбой при записи строки {i + 1} из {rows.Count}" +
                (file.Length > 0 ? $" (расчет: {file})" : "") + $":\n{ex.Message}", ex);
        }
    }

    private static void WriteRow(XmlWriter w, TimeSheetStyles styles, TimeRow source,
                                 TimeCollectorEngine.RowPaint paint, int row)
    {
        var body = styles.Body(paint.Fill);
        var date = styles.Date(paint.Fill);
        var norm = paint.NormRed ? styles.Body(TimeCollectorEngine.RowFill.Red) : body;

        w.WriteStartElement("row", Ns);
        w.WriteAttributeString("r", row.ToString(CultureInfo.InvariantCulture));

        WriteNumber(w, ColRNum, row, source.RNum, body);
        WriteInlineString(w, ColName, row, source.Name, body);
        WriteInlineString(w, ColDeno, row, source.Deno, body);

        if (source.Norm is { } value) WriteNumber(w, ColNorm, row, value, norm);
        else                          WriteInlineString(w, ColNorm, row, source.NormText, norm);

        // Дата в Excel — число с датным форматом; серийный номер дает ToOADate.
        WriteNumber(w, ColDate, row, source.Date.ToOADate(), date);
        WriteInlineString(w, ColEfio, row, source.Efio, body);
        WriteInlineString(w, ColProj, row, source.Proj, body);
        WriteLink(w, row, source, body);

        w.WriteEndElement();
    }

    /// <summary>
    /// Колонка «Ссылка» — оба способа перехода сразу. Текст «&gt;&gt;&gt;» и номер строки в
    /// скрытой колонке A нужны макросу шаблона (<c>WsSelectionChanged</c> → <c>CalculationLink</c>),
    /// формула <c>HYPERLINK</c> открывает тот же расчет на той же строке и без макросов.
    /// </summary>
    private static void WriteLink(XmlWriter w, int row, TimeRow source, int style)
    {
        if (source.SourcePath.Length == 0 || source.SourcePath.IndexOfAny(BadLinkChars) >= 0)
        {
            WriteInlineString(w, ColLink, row, LinkMark, style);
            return;
        }

        var target = XmlSafe($"[{source.SourcePath}]{SourceSheet}!A{source.RNum + 1}");

        w.WriteStartElement("c", Ns);
        w.WriteAttributeString("r", Ref(ColLink, row));
        w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("t", "str");
        w.WriteElementString("f", Ns, $"HYPERLINK(\"{target}\",\"{LinkMark}\")");
        w.WriteEndElement();
    }

    private static void WriteInlineString(XmlWriter w, int col, int row, string text, int style)
    {
        if (text.Length == 0) { WriteBlank(w, col, row, style); return; }

        w.WriteStartElement("c", Ns);
        w.WriteAttributeString("r", Ref(col, row));
        w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        // Строки пишутся ВСТРОЕННЫМИ: общая таблица строк (sharedStrings) потребовала бы
        // держать в памяти словарь на весь свод — ровно то, от чего мы здесь уходим.
        w.WriteAttributeString("t", "inlineStr");
        w.WriteStartElement("is", Ns);
        w.WriteElementString("t", Ns, XmlSafe(text));
        w.WriteEndElement();
        w.WriteEndElement();
    }

    /// <summary>
    /// Текст, пригодный для XML. В XML 1.0 нет управляющих символов (кроме табуляции и
    /// переводов строки), одиночных суррогатов и FFFE/FFFF, а в расчетах они попадаются:
    /// выгрузки CAD оставляют в наименованиях остатки форматирования, и такой знак не виден
    /// ни в ячейке Excel, ни в глаза. <see cref="XmlWriter"/> на нем бросает
    /// <see cref="ArgumentException"/> — и весь прогон падал уже на записи, потеряв все
    /// прочитанное. Меняем на пробел: свод важнее одного невидимого знака.
    ///
    /// Быстрый путь без единой аллокации: обычная строка проходит <see cref="FirstBad"/>
    /// и возвращается собой — на 600 тыс. строк это важнее краткости.
    /// </summary>
    private static string XmlSafe(string text)
    {
        var bad = FirstBad(text);
        if (bad < 0) return text;

        var buf = new StringBuilder(text.Length).Append(text, 0, bad);
        for (var i = bad; i < text.Length; i++)
        {
            var c = text[i];
            if (IsPair(text, i)) { buf.Append(c).Append(text[i + 1]); i++; continue; }
            buf.Append(Allowed(c) ? c : ' ');
        }
        return buf.ToString();
    }

    private static int FirstBad(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (IsPair(text, i)) { i++; continue; }
            if (!Allowed(text[i])) return i;
        }
        return -1;
    }

    // Пара суррогатов — один символ вне BMP (эмодзи, редкие иероглифы): она допустима,
    // а вот половина пары сама по себе — нет.
    private static bool IsPair(string text, int i) =>
        char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);

    private static bool Allowed(char c) =>
        c >= 0x20
            ? !char.IsSurrogate(c) && c is not ('\uFFFE' or '\uFFFF')
            : c is '\t' or '\n' or '\r';

    private static void WriteNumber(XmlWriter w, int col, int row, double value, int style)
    {
        // Бесконечность и NaN числом не записать: «Infinity» в <v> оставляет XML синтаксически
        // целым, но Excel объявляет книгу нечитаемой и выбрасывает лист при «восстановлении».
        // Отсеивает такие значения уже читатель (CalcFileReader.ReadNorm) — здесь страховка
        // на случай другого источника: пустая ячейка со стилем безопаснее битого листа.
        if (!double.IsFinite(value)) { WriteBlank(w, col, row, style); return; }

        w.WriteStartElement("c", Ns);
        w.WriteAttributeString("r", Ref(col, row));
        w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        w.WriteElementString("v", Ns, value.ToString("R", CultureInfo.InvariantCulture));
        w.WriteEndElement();
    }

    // Пустая ячейка со стилем: значения нет, но границы и заливка строки должны быть.
    private static void WriteBlank(XmlWriter w, int col, int row, int style)
    {
        w.WriteStartElement("c", Ns);
        w.WriteAttributeString("r", Ref(col, row));
        w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        w.WriteEndElement();
    }

    private static string Ref(int col, int row) =>
        Letters[col - 1] + row.ToString(CultureInfo.InvariantCulture);

    private static void WriteMerges(XmlWriter w)
    {
        var count = ColLink - ColName + 1;
        w.WriteStartElement("mergeCells", Ns);
        w.WriteAttributeString("count", count.ToString(CultureInfo.InvariantCulture));
        for (var col = ColName; col <= ColLink; col++)
        {
            w.WriteStartElement("mergeCell", Ns);
            w.WriteAttributeString("ref", $"{Letters[col - 1]}1:{Letters[col - 1]}2");
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    // ── Книга ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Правка <c>xl/workbook.xml</c>: имя ALL_DATA переставляется на новый свод — по нему
    /// ходит макрос шаблона. Заодно выбрасываются определенные имена, ссылки которых не
    /// разбираются: обычно это остатки связей с другими книгами (см. <see cref="ExcelBook"/>),
    /// и результат с таким именем не открылся бы уже нашими же средствами.
    /// </summary>
    private static string RewriteWorkbook(ZipArchiveEntry entry, int rowCount)
    {
        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);

        var root = doc.Root;
        if (root is null) return doc.ToString();

        var names = root.Element(XName.Get("definedNames", Ns));
        if (names is null)
        {
            // Порядок элементов книги задан схемой: definedNames идут сразу после sheets.
            var sheets = root.Element(XName.Get("sheets", Ns));
            if (sheets is null) return Serialize(doc);
            names = new XElement(XName.Get("definedNames", Ns));
            sheets.AddAfterSelf(names);
        }

        foreach (var name in names.Elements(XName.Get("definedName", Ns)).ToList())
        {
            var key = (string?)name.Attribute("name");
            if (string.Equals(key, NamedRange, StringComparison.OrdinalIgnoreCase) || !Parses(name.Value))
                name.Remove();
        }

        var last = TopIndent + rowCount;
        names.Add(new XElement(XName.Get("definedName", Ns),
            new XAttribute("name", NamedRange),
            $"'{TimeCollectorEngine.TargetSheet}'!$A${TopIndent}:${Letters[^1]}${last}"));

        return Serialize(doc);
    }

    /// <summary>Разбирается ли ссылка тем же парсером, что читает книгу на нашей стороне
    /// (см. <see cref="ExcelBook"/>). Пустую ссылку он встречает не разбором, а
    /// <see cref="ArgumentException"/>.</summary>
    private static bool Parses(string refersTo)
    {
        if (string.IsNullOrWhiteSpace(refersTo)) return false;
        try { FormulaConverter.ToR1C1(refersTo, 1, 1); return true; }
        catch (ParsingException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static string Serialize(XDocument doc) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        doc.ToString(SaveOptions.DisableFormatting);
}
