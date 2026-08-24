using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace SmartBase.Extensions.TimeCollector;

/// <summary>
/// Стили свода, ДОПИСАННЫЕ в таблицу стилей шаблона. В потоковой записи (см.
/// <see cref="TimeSheetWriter"/>) у ячейки нет объекта оформления — есть только номер стиля
/// в атрибуте <c>s</c>, а сами стили лежат в <c>xl/styles.xml</c> общим списком. Поэтому все
/// сочетания, которые встречаются в своде, заводятся заранее: их всего семь.
///
/// Именно ДОПИСАННЫЕ, а не свои: таблица стилей у шаблона уже есть (ей пользуются шапка,
/// прочие листы, условное форматирование), и заменить ее целиком значило бы обесцветить всю
/// книгу. Существующие элементы остаются на своих местах и со своими номерами, новые идут
/// в конец списков.
/// </summary>
internal sealed class TimeSheetStyles
{
    internal const string PartPath = "xl/styles.xml";

    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    // Порядок разделов в styles.xml задан схемой: вставлять новый раздел нужно на его место,
    // иначе Excel считает книгу поврежденной.
    private static readonly string[] SectionOrder =
    [
        "numFmts", "fonts", "fills", "borders", "cellStyleXfs", "cellXfs",
        "cellStyles", "dxfs", "tableStyles", "colors", "extLst",
    ];

    // Цвета макроса в ARGB: ColorIndex 19 (светло-желтый) и ColorIndex 3 (красный).
    private const string YellowArgb = "FFFFFFCC";
    private const string RedArgb    = "FFFF0000";

    private readonly int[] _body;
    private readonly int[] _date;

    private TimeSheetStyles(string xml, int head, int[] body, int[] date)
    {
        Xml   = xml;
        Head  = head;
        _body = body;
        _date = date;
    }

    /// <summary>Готовый текст <c>xl/styles.xml</c> для результата.</summary>
    internal string Xml { get; }

    /// <summary>Стиль ячейки шапки: границы, выравнивание по центру, перенос.</summary>
    internal int Head { get; }

    /// <summary>Обычная ячейка строки свода с заливкой этой строки.</summary>
    internal int Body(TimeCollectorEngine.RowFill fill) => _body[(int)fill];

    /// <summary>Ячейка даты (формат «дд.ММ.гггг») с заливкой этой строки.</summary>
    internal int Date(TimeCollectorEngine.RowFill fill) => _date[(int)fill];

    /// <summary>Дописать стили свода в таблицу стилей шаблона и запомнить их номера.</summary>
    internal static TimeSheetStyles Prepare(ZipArchive template)
    {
        var doc  = Load(template);
        var root = doc.Root!;

        var numFmtId = AddDateFormat(root);
        var borderId = AddThinBorder(root);
        var yellowId = AddSolidFill(root, YellowArgb);
        var redId    = AddSolidFill(root, RedArgb);

        // Белая строка заливки не получает вовсе: «без заливки» и есть заливка №0, и лишний
        // сплошной белый поверх нее только испортил бы вид листа в темах Excel.
        int[] fills = new int[3];
        fills[(int)TimeCollectorEngine.RowFill.White]  = 0;
        fills[(int)TimeCollectorEngine.RowFill.Yellow] = yellowId;
        fills[(int)TimeCollectorEngine.RowFill.Red]    = redId;

        var cellXfs = Section(root, "cellXfs");
        var head    = AddFormat(cellXfs, borderId, fillId: 0, numFmtId: 0, header: true);

        var body = new int[3];
        var date = new int[3];
        for (var i = 0; i < 3; i++)
        {
            body[i] = AddFormat(cellXfs, borderId, fills[i], numFmtId: 0,        header: false);
            date[i] = AddFormat(cellXfs, borderId, fills[i], numFmtId: numFmtId, header: false);
        }

        return new TimeSheetStyles(Serialize(doc), head, body, date);
    }

    // ── Разделы ───────────────────────────────────────────────────────────────

    private static XDocument Load(ZipArchive template)
    {
        var entry = template.GetEntry(PartPath);
        if (entry is not null)
        {
            using var s = entry.Open();
            try
            {
                var doc = XDocument.Load(s);
                if (doc.Root is not null) return doc;
            }
            catch (System.Xml.XmlException) { /* испорченная таблица стилей — соберем свою */ }
        }

        // Шаблона без таблицы стилей быть не должно, но собрать минимальную дешевле, чем
        // ронять прогон: Excel требует в ней хотя бы один шрифт, две заливки и одну границу.
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(XName.Get("styleSheet", Ns),
                new XElement(XName.Get("fonts", Ns), new XAttribute("count", 1),
                    new XElement(XName.Get("font", Ns))),
                new XElement(XName.Get("fills", Ns), new XAttribute("count", 2),
                    Fill("none"), Fill("gray125")),
                new XElement(XName.Get("borders", Ns), new XAttribute("count", 1),
                    new XElement(XName.Get("border", Ns))),
                new XElement(XName.Get("cellStyleXfs", Ns), new XAttribute("count", 1),
                    new XElement(XName.Get("xf", Ns))),
                new XElement(XName.Get("cellXfs", Ns), new XAttribute("count", 1),
                    new XElement(XName.Get("xf", Ns)))));

        static XElement Fill(string pattern) =>
            new(XName.Get("fill", Ns),
                new XElement(XName.Get("patternFill", Ns), new XAttribute("patternType", pattern)));
    }

    /// <summary>Раздел по имени; нет — создается на своем месте по схеме.</summary>
    private static XElement Section(XElement root, string name)
    {
        var section = root.Element(XName.Get(name, Ns));
        if (section is not null) return section;

        section = new XElement(XName.Get(name, Ns), new XAttribute("count", 0));

        var index = Array.IndexOf(SectionOrder, name);
        var after = root.Elements()
            .LastOrDefault(e => Array.IndexOf(SectionOrder, e.Name.LocalName) is var i && i >= 0 && i < index);

        if (after is not null) after.AddAfterSelf(section);
        else                   root.AddFirst(section);

        return section;
    }

    // Дописать элемент в раздел и вернуть его номер (нумерация с нуля, как ее видит ячейка).
    private static int Append(XElement section, XElement item)
    {
        var index = section.Elements().Count();
        section.Add(item);
        section.SetAttributeValue("count", index + 1);
        return index;
    }

    // ── Элементы оформления ───────────────────────────────────────────────────

    /// <summary>Формат даты. Идентификаторы ниже 164 закреплены за встроенными форматами,
    /// поэтому свой берем следующим свободным начиная с 164.</summary>
    private static int AddDateFormat(XElement root)
    {
        var numFmts = Section(root, "numFmts");

        const string code = "dd\\.mm\\.yyyy";
        var existing = numFmts.Elements(XName.Get("numFmt", Ns))
            .FirstOrDefault(e => (string?)e.Attribute("formatCode") == code);
        if (existing is not null) return (int)existing.Attribute("numFmtId")!;

        var used = numFmts.Elements(XName.Get("numFmt", Ns))
            .Select(e => (int?)e.Attribute("numFmtId") ?? 0)
            .ToHashSet();
        var id = 164;
        while (used.Contains(id)) id++;

        numFmts.Add(new XElement(XName.Get("numFmt", Ns),
            new XAttribute("numFmtId", id),
            new XAttribute("formatCode", code)));
        numFmts.SetAttributeValue("count", numFmts.Elements().Count());
        return id;
    }

    private static int AddThinBorder(XElement root) =>
        Append(Section(root, "borders"),
            new XElement(XName.Get("border", Ns),
                Side("left"), Side("right"), Side("top"), Side("bottom"),
                new XElement(XName.Get("diagonal", Ns))));

    private static XElement Side(string name) =>
        new(XName.Get(name, Ns),
            new XAttribute("style", "thin"),
            new XElement(XName.Get("color", Ns), new XAttribute("indexed", 64)));

    private static int AddSolidFill(XElement root, string argb) =>
        Append(Section(root, "fills"),
            new XElement(XName.Get("fill", Ns),
                new XElement(XName.Get("patternFill", Ns),
                    new XAttribute("patternType", "solid"),
                    new XElement(XName.Get("fgColor", Ns), new XAttribute("rgb", argb)),
                    new XElement(XName.Get("bgColor", Ns), new XAttribute("indexed", 64)))));

    private static int AddFormat(XElement cellXfs, int borderId, int fillId, int numFmtId, bool header)
    {
        var xf = new XElement(XName.Get("xf", Ns),
            new XAttribute("numFmtId", numFmtId.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("fontId", 0),
            new XAttribute("fillId", fillId.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("borderId", borderId.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("xfId", 0),
            new XAttribute("applyBorder", 1));

        if (numFmtId != 0) xf.Add(new XAttribute("applyNumberFormat", 1));
        if (fillId   != 0) xf.Add(new XAttribute("applyFill", 1));

        if (header)
            xf.Add(new XAttribute("applyAlignment", 1),
                   new XElement(XName.Get("alignment", Ns),
                       new XAttribute("horizontal", "center"),
                       new XAttribute("vertical", "center"),
                       new XAttribute("wrapText", 1)));

        return Append(cellXfs, xf);
    }

    private static string Serialize(XDocument doc)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>").Append('\n');
        sb.Append(doc.ToString(SaveOptions.DisableFormatting));
        return sb.ToString();
    }
}
