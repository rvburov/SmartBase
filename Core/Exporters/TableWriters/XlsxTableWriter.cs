using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using SmartBase.Core.Models;

namespace SmartBase.Core.Exporters.TableWriters;

/// <summary>
/// Потоковая запись .xlsx/.xlsm прямо в ZIP-архив: строки уходят в файл по мере записи,
/// книга в память не поднимается (ClosedXML — DOM, на 10 млн строк непригоден).
/// Архив собирается вручную (<see cref="ZipArchiveMode.Create"/>), а не через OpenXml SDK:
/// SDK открывает пакет в режиме Update, а он держит КАЖДУЮ часть целиком в памяти до
/// закрытия — на 600 тыс. строк это 1,1 ГБ против 100 МБ у Create (замеры — в
/// docs/developer/architecture/performance/hot-paths.md). Части простые и их шесть штук,
/// так что своя сборка дешевле зависимости от чужого упаковщика.
/// Без shared strings — каждая строка сразу inline (память O(1), файл чуть больше).
/// Пустые ячейки не пишутся вовсе: сетку держат ссылки r="A5" на ячейках и строках.
/// Типизация: number → числовая ячейка, checkbox → булева (ИСТИНА/ЛОЖЬ), «точечные» даты →
/// настоящие даты (OADate + формат дд.мм.гггг[ чч:мм] из минимального styles.xml), диапазоны
/// дат и неразобранное → строки. Переполнение листа (лимит Excel 1 048 576 строк, включая
/// заголовок) — автопродолжение «Имя 2», «Имя 3»… с повтором шапки; лимит инъектируется
/// для тестов. .xlsm — тот же контейнер с macro-enabled типом (макросов внутри нет).
/// </summary>
public sealed class XlsxTableWriter : ITableWriter
{
    /// <summary>Лимит строк листа Excel (2^20).</summary>
    public const int ExcelSheetRowLimit = 1_048_576;

    private const string NsMain    = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string NsDocRel  = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NsPkgRel  = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string NsTypes   = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string NsXml     = "http://www.w3.org/XML/1998/namespace";

    private const string TypeWorkbook = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    private const string TypeMacroBook = "application/vnd.ms-excel.sheet.macroEnabled.main+xml";
    private const string TypeWorksheet = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
    private const string TypeStyles    = "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml";
    private const string TypeRels      = "application/vnd.openxmlformats-package.relationships+xml";

    /// <summary>CheckCharacters=false — как у OpenXmlWriter: выброшенное исключение посреди
    /// экспорта хуже пропущенного символа, чистку берет на себя <see cref="WriterHelpers.SanitizeXml"/>.</summary>
    private static readonly XmlWriterSettings PartSettings = new()
    {
        Encoding        = new UTF8Encoding(false),
        CheckCharacters = false,
        CloseOutput     = true,
    };

    private readonly FileStream _fs;
    private readonly ZipArchive _zip;
    private readonly bool _macroEnabled;
    private readonly int _maxRowsPerSheet;

    private readonly List<string> _sheets = [];   // имена листов книги, индекс+1 = номер части
    private readonly HashSet<string> _sheetNames = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<ExportColumn>? _cols;
    private string[] _colRefs = [];   // буквенные части ссылок ячеек (A, B, …, AA)
    private bool _writeHeader;
    private string _tableBase = "";   // имя таблицы — база для листов-продолжений
    private int _sheetNo;             // номер листа внутри текущей таблицы (1 — первый)
    private XmlWriter? _sheet;
    private int _rowIndex;            // строк в текущем листе (включая заголовок)
    private bool _finished;
    private bool _disposed;

    public XlsxTableWriter(string path, bool macroEnabled, int maxRowsPerSheet = ExcelSheetRowLimit)
    {
        _maxRowsPerSheet = Math.Clamp(maxRowsPerSheet, 2, ExcelSheetRowLimit);
        _macroEnabled    = macroEnabled;
        _fs  = ExportErrors.CreateFile(path);
        _zip = new ZipArchive(_fs, ZipArchiveMode.Create, leaveOpen: true);
    }

    public void BeginTable(string name, IReadOnlyList<ExportColumn> columns, bool writeHeader)
    {
        if (_sheet is not null) throw new InvalidOperationException("Предыдущая таблица не закрыта (EndTable).");
        _cols        = columns;
        _writeHeader = writeHeader;
        _tableBase   = WriterHelpers.SanitizeXml(name);
        _sheetNo     = 0;
        _colRefs     = new string[columns.Count];
        for (var i = 0; i < columns.Count; i++) _colRefs[i] = ColumnLetters(i + 1);
        StartSheet();
    }

    public void WriteRow(IReadOnlyList<string> values)
    {
        var cols = _cols ?? throw new InvalidOperationException("BeginTable не вызван.");
        if (_rowIndex >= _maxRowsPerSheet)
        {
            CloseSheet();
            StartSheet();   // лист-продолжение с повтором шапки
        }

        BeginRow();
        var n = Math.Min(values.Count, cols.Count);
        for (var i = 0; i < n; i++)
        {
            var raw = values[i];
            if (raw.Length == 0) continue;   // пустое — ячейку не пишем, r= держит сетку
            switch (cols[i].FieldType)
            {
                case "number" or "rollup" when WriterHelpers.TryNumber(raw, out var num):
                    WriteNumberCell(i, num);
                    break;
                case "checkbox":
                    WriteBoolCell(i, WriterHelpers.IsChecked(raw));
                    break;
                case "date":
                    WriteDateCell(i, raw);
                    break;
                case "multiselect":
                    WriteStringCell(i, MultiSelectValue.Display(raw, MultiSelectValue.SeparatorFromConfig(cols[i].ConfigJson)));
                    break;
                default:
                    WriteStringCell(i, raw);
                    break;
            }
        }
        _sheet!.WriteEndElement();   // </row>
    }

    public void EndTable()
    {
        if (_sheet is null) throw new InvalidOperationException("BeginTable не вызван.");
        CloseSheet();
        _cols = null;
    }

    public void Finish()
    {
        if (_sheet is not null) throw new InvalidOperationException("Таблица не закрыта (EndTable).");
        if (_finished) return;

        // Служебные части идут после листов: список листов известен только теперь, а порядок
        // элементов внутри ZIP роли не играет (Excel читает архив по оглавлению, не подряд).
        WriteStyles();
        WriteWorkbook();
        WritePackageRels();
        WriteWorkbookRels();
        WriteContentTypes();

        _finished = true;
        _zip.Dispose();   // оглавление ZIP
        _fs.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _sheet?.Dispose(); } catch { /* best-effort */ }
        if (!_finished)
        {
            // Аварийное закрытие (ошибка/отмена): файл недописан, его удалит оркестрация.
            try { _zip.Dispose(); } catch { /* best-effort */ }
            try { _fs.Dispose(); } catch { /* best-effort */ }
        }
    }

    // ── Листы ────────────────────────────────────────────────────────────────

    private void StartSheet()
    {
        _sheetNo++;
        _sheets.Add(UniqueSheetName(_tableBase, _sheetNo));

        _sheet = NewPart($"xl/worksheets/sheet{_sheets.Count}.xml");
        _sheet.WriteStartElement("worksheet", NsMain);
        _sheet.WriteStartElement("sheetData", NsMain);
        _rowIndex = 0;

        if (_writeHeader)
        {
            BeginRow();
            for (var i = 0; i < _cols!.Count; i++)
                WriteStringCell(i, _cols[i].Header);
            _sheet.WriteEndElement();   // </row>
        }
    }

    private void CloseSheet()
    {
        _sheet!.WriteEndElement();   // </sheetData>
        _sheet.WriteEndElement();    // </worksheet>
        _sheet.Dispose();            // CloseOutput=true — закрывает и элемент архива
        _sheet = null;
    }

    /// <summary>«Имя», «Имя 2», «Имя 3»… с уникальностью по всей книге (коллекции «Имя» и
    /// «Имя 2» рядом с продолжениями не сталкиваются) и с укорачиванием базы под лимит 31.</summary>
    private string UniqueSheetName(string baseName, int no)
    {
        var n = no;
        while (true)
        {
            var candidate = n <= 1 ? baseName : Fit(baseName, $" {n}");
            if (_sheetNames.Add(candidate)) return candidate;
            n++;
        }
    }

    private static string Fit(string baseName, string suffix) =>
        (baseName.Length + suffix.Length <= 31 ? baseName : baseName[..(31 - suffix.Length)].TrimEnd()) + suffix;

    // ── Ячейки ───────────────────────────────────────────────────────────────

    private void BeginRow()
    {
        _rowIndex++;
        _sheet!.WriteStartElement("row", NsMain);
        _sheet.WriteAttributeString("r", _rowIndex.ToString(CultureInfo.InvariantCulture));
    }

    private string CellRef(int col) => _colRefs[col] + _rowIndex.ToString(CultureInfo.InvariantCulture);

    private void WriteStringCell(int col, string text)
    {
        var w = _sheet!;
        w.WriteStartElement("c", NsMain);
        w.WriteAttributeString("r", CellRef(col));
        w.WriteAttributeString("t", "inlineStr");
        w.WriteStartElement("is", NsMain);
        w.WriteStartElement("t", NsMain);
        w.WriteAttributeString("space", NsXml, "preserve");
        w.WriteString(WriterHelpers.SanitizeXml(text));
        w.WriteEndElement();   // </t>
        w.WriteEndElement();   // </is>
        w.WriteEndElement();   // </c>
    }

    private void WriteNumberCell(int col, double num)
    {
        var w = _sheet!;
        w.WriteStartElement("c", NsMain);
        w.WriteAttributeString("r", CellRef(col));
        w.WriteElementString("v", NsMain, num.ToString(CultureInfo.InvariantCulture));
        w.WriteEndElement();
    }

    private void WriteBoolCell(int col, bool value)
    {
        var w = _sheet!;
        w.WriteStartElement("c", NsMain);
        w.WriteAttributeString("r", CellRef(col));
        w.WriteAttributeString("t", "b");
        w.WriteElementString("v", NsMain, value ? "1" : "0");
        w.WriteEndElement();
    }

    /// <summary>«Точечные» даты — настоящие датные ячейки (OADate + стиль 1/2 из styles.xml),
    /// диапазоны и неразобранное — строками (у Excel нет типа «диапазон дат»).</summary>
    private void WriteDateCell(int col, string raw)
    {
        if (DateValue.TryParse(raw, out var dv))
        {
            if (dv.AllDay && dv.Start.Date == dv.End.Date)
            {
                WriteStyledNumberCell(col, dv.Start.Date.ToOADate(), style: 1);   // дд.мм.гггг
                return;
            }
            if (!dv.AllDay && dv.Start == dv.End)
            {
                WriteStyledNumberCell(col, dv.Start.ToOADate(), style: 2);        // дд.мм.гггг чч:мм
                return;
            }
            WriteStringCell(col, dv.ToDisplay());
            return;
        }
        WriteStringCell(col, raw);
    }

    private void WriteStyledNumberCell(int col, double num, int style)
    {
        var w = _sheet!;
        w.WriteStartElement("c", NsMain);
        w.WriteAttributeString("r", CellRef(col));
        w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        w.WriteElementString("v", NsMain, num.ToString(CultureInfo.InvariantCulture));
        w.WriteEndElement();
    }

    /// <summary>1 → A, 2 → B, 27 → AA …</summary>
    private static string ColumnLetters(int index)
    {
        var s = "";
        while (index > 0)
        {
            index--;
            s = (char)('A' + index % 26) + s;
            index /= 26;
        }
        return s;
    }

    // ── Служебные части пакета ───────────────────────────────────────────────

    /// <summary>Новый элемент архива под запись XML. Поток элемента закрывается вместе
    /// с писателем (CloseOutput), поэтому наружу отдается только XmlWriter.</summary>
    private XmlWriter NewPart(string entry)
    {
        var stream = _zip.CreateEntry(entry, CompressionLevel.Optimal).Open();
        var w = XmlWriter.Create(stream, PartSettings);
        w.WriteStartDocument();
        return w;
    }

    /// <summary>Минимальный styles.xml: только два датных формата. Индексы ячейковых стилей:
    /// 0 — дефолт, 1 — дд.мм.гггг (numFmt 164), 2 — дд.мм.гггг чч:мм (numFmt 165).
    /// Fills обязаны содержать none и gray125 — Excel иначе считает файл поврежденным.</summary>
    private void WriteStyles()
    {
        using var w = NewPart("xl/styles.xml");
        w.WriteStartElement("styleSheet", NsMain);

        w.WriteStartElement("numFmts", NsMain);
        w.WriteAttributeString("count", "2");
        WriteNumFmt(w, 164, "dd.mm.yyyy");
        WriteNumFmt(w, 165, "dd.mm.yyyy hh:mm");
        w.WriteEndElement();

        w.WriteStartElement("fonts", NsMain);
        w.WriteAttributeString("count", "1");
        w.WriteElementString("font", NsMain, "");
        w.WriteEndElement();

        w.WriteStartElement("fills", NsMain);
        w.WriteAttributeString("count", "2");
        WriteFill(w, "none");
        WriteFill(w, "gray125");
        w.WriteEndElement();

        w.WriteStartElement("borders", NsMain);
        w.WriteAttributeString("count", "1");
        w.WriteElementString("border", NsMain, "");
        w.WriteEndElement();

        w.WriteStartElement("cellStyleXfs", NsMain);
        w.WriteAttributeString("count", "1");
        w.WriteElementString("xf", NsMain, "");
        w.WriteEndElement();

        w.WriteStartElement("cellXfs", NsMain);
        w.WriteAttributeString("count", "3");
        w.WriteElementString("xf", NsMain, "");
        WriteDateXf(w, 164);
        WriteDateXf(w, 165);
        w.WriteEndElement();

        w.WriteEndElement();   // </styleSheet>

        static void WriteNumFmt(XmlWriter w, int id, string code)
        {
            w.WriteStartElement("numFmt", NsMain);
            w.WriteAttributeString("numFmtId", id.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("formatCode", code);
            w.WriteEndElement();
        }

        static void WriteFill(XmlWriter w, string pattern)
        {
            w.WriteStartElement("fill", NsMain);
            w.WriteStartElement("patternFill", NsMain);
            w.WriteAttributeString("patternType", pattern);
            w.WriteEndElement();
            w.WriteEndElement();
        }

        static void WriteDateXf(XmlWriter w, int numFmtId)
        {
            w.WriteStartElement("xf", NsMain);
            w.WriteAttributeString("numFmtId", numFmtId.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("applyNumberFormat", "1");
            w.WriteEndElement();
        }
    }

    /// <summary>Связи книги: rId1 — стили, дальше листы в порядке создания.</summary>
    private static string SheetRelId(int index) => $"rId{index + 2}";

    private void WriteWorkbook()
    {
        using var w = NewPart("xl/workbook.xml");
        w.WriteStartElement("workbook", NsMain);
        w.WriteAttributeString("xmlns", "r", null, NsDocRel);
        w.WriteStartElement("sheets", NsMain);
        for (var i = 0; i < _sheets.Count; i++)
        {
            w.WriteStartElement("sheet", NsMain);
            w.WriteAttributeString("name", _sheets[i]);
            w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("id", NsDocRel, SheetRelId(i));
            w.WriteEndElement();
        }
        w.WriteEndElement();   // </sheets>
        w.WriteEndElement();   // </workbook>
    }

    private void WritePackageRels()
    {
        using var w = NewPart("_rels/.rels");
        w.WriteStartElement("Relationships", NsPkgRel);
        WriteRel(w, "rId1", NsDocRel + "/officeDocument", "xl/workbook.xml");
        w.WriteEndElement();
    }

    private void WriteWorkbookRels()
    {
        using var w = NewPart("xl/_rels/workbook.xml.rels");
        w.WriteStartElement("Relationships", NsPkgRel);
        WriteRel(w, "rId1", NsDocRel + "/styles", "styles.xml");
        for (var i = 0; i < _sheets.Count; i++)
            WriteRel(w, SheetRelId(i), NsDocRel + "/worksheet", $"worksheets/sheet{i + 1}.xml");
        w.WriteEndElement();
    }

    private static void WriteRel(XmlWriter w, string id, string type, string target)
    {
        w.WriteStartElement("Relationship", NsPkgRel);
        w.WriteAttributeString("Id", id);
        w.WriteAttributeString("Type", type);
        w.WriteAttributeString("Target", target);
        w.WriteEndElement();
    }

    private void WriteContentTypes()
    {
        using var w = NewPart("[Content_Types].xml");
        w.WriteStartElement("Types", NsTypes);
        WriteDefault(w, "rels", TypeRels);
        WriteDefault(w, "xml", "application/xml");
        WriteOverride(w, "/xl/workbook.xml", _macroEnabled ? TypeMacroBook : TypeWorkbook);
        WriteOverride(w, "/xl/styles.xml", TypeStyles);
        for (var i = 0; i < _sheets.Count; i++)
            WriteOverride(w, $"/xl/worksheets/sheet{i + 1}.xml", TypeWorksheet);
        w.WriteEndElement();

        static void WriteDefault(XmlWriter w, string ext, string type)
        {
            w.WriteStartElement("Default", NsTypes);
            w.WriteAttributeString("Extension", ext);
            w.WriteAttributeString("ContentType", type);
            w.WriteEndElement();
        }

        static void WriteOverride(XmlWriter w, string part, string type)
        {
            w.WriteStartElement("Override", NsTypes);
            w.WriteAttributeString("PartName", part);
            w.WriteAttributeString("ContentType", type);
            w.WriteEndElement();
        }
    }
}
