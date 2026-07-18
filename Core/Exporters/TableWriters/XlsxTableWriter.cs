using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SmartBase.Core.Models;

namespace SmartBase.Core.Exporters.TableWriters;

/// <summary>
/// Потоковая запись .xlsx/.xlsm через OpenXmlWriter (SAX): строки уходят в архив по мере
/// записи, книга в память не поднимается (ClosedXML — DOM, на 10 млн строк непригоден).
/// Без shared strings — каждая строка сразу inline (память O(1), файл чуть больше).
/// Пустые ячейки не пишутся вовсе: сетку держат ссылки r="A5" на ячейках и строках.
/// Типизация: number → числовая ячейка, checkbox → булева (ИСТИНА/ЛОЖЬ), «точечные» даты →
/// настоящие даты (OADate + формат дд.мм.гггг[ чч:мм] из минимального Stylesheet), диапазоны
/// дат и неразобранное → строки. Переполнение листа (лимит Excel 1 048 576 строк, включая
/// заголовок) — автопродолжение «Имя 2», «Имя 3»… с повтором шапки; лимит инъектируется
/// для тестов. .xlsm — тот же контейнер с macro-enabled типом (макросов внутри нет).
/// </summary>
public sealed class XlsxTableWriter : ITableWriter
{
    /// <summary>Лимит строк листа Excel (2^20).</summary>
    public const int ExcelSheetRowLimit = 1_048_576;

    private readonly FileStream _fs;
    private readonly SpreadsheetDocument _doc;
    private readonly WorkbookPart _wb;
    private readonly int _maxRowsPerSheet;

    private readonly List<(string Name, string PartId)> _sheets = [];
    private readonly HashSet<string> _sheetNames = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<ExportColumn>? _cols;
    private string[] _colRefs = [];   // буквенные части ссылок ячеек (A, B, …, AA)
    private bool _writeHeader;
    private string _tableBase = "";   // имя таблицы — база для листов-продолжений
    private int _sheetNo;             // номер листа внутри текущей таблицы (1 — первый)
    private OpenXmlWriter? _sheet;
    private int _rowIndex;            // строк в текущем листе (включая заголовок)
    private bool _finished;
    private bool _disposed;

    public XlsxTableWriter(string path, bool macroEnabled, int maxRowsPerSheet = ExcelSheetRowLimit)
    {
        _maxRowsPerSheet = Math.Clamp(maxRowsPerSheet, 2, ExcelSheetRowLimit);
        _fs = ExportErrors.CreateFile(path, readWrite: true);
        _doc = SpreadsheetDocument.Create(_fs, macroEnabled
            ? SpreadsheetDocumentType.MacroEnabledWorkbook
            : SpreadsheetDocumentType.Workbook);
        _wb = _doc.AddWorkbookPart();
        AddStyles(_wb);
    }

    public void BeginTable(string name, IReadOnlyList<ExportColumn> columns, bool writeHeader)
    {
        if (_sheet is not null) throw new InvalidOperationException("Предыдущая таблица не закрыта (EndTable).");
        _cols        = columns;
        _writeHeader = writeHeader;
        _tableBase   = name;
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
                case "number" when WriterHelpers.TryNumber(raw, out var num):
                    WriteNumberCell(i, num);
                    break;
                case "checkbox":
                    WriteBoolCell(i, WriterHelpers.IsChecked(raw));
                    break;
                case "date":
                    WriteDateCell(i, raw);
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

        // workbook.xml собирается в конце — список листов известен только теперь.
        using (var w = OpenXmlWriter.Create(_wb))
        {
            w.WriteStartElement(new Workbook());
            w.WriteStartElement(new Sheets());
            for (var i = 0; i < _sheets.Count; i++)
                w.WriteElement(new Sheet
                {
                    Name    = _sheets[i].Name,
                    SheetId = (uint)(i + 1),
                    Id      = _sheets[i].PartId,
                });
            w.WriteEndElement();
            w.WriteEndElement();
        }

        _finished = true;
        _doc.Dispose();   // упаковка ZIP-архива
        _fs.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sheet?.Close();
        if (!_finished)
        {
            // Аварийное закрытие (ошибка/отмена): файл недописан, его удалит оркестрация.
            try { _doc.Dispose(); } catch { /* best-effort */ }
            try { _fs.Dispose(); } catch { /* best-effort */ }
        }
    }

    // ── Листы ────────────────────────────────────────────────────────────────

    private void StartSheet()
    {
        _sheetNo++;
        var name = UniqueSheetName(_tableBase, _sheetNo);
        var part = _wb.AddNewPart<WorksheetPart>();
        _sheets.Add((name, _wb.GetIdOfPart(part)));

        _sheet = OpenXmlWriter.Create(part);
        _sheet.WriteStartElement(new Worksheet());
        _sheet.WriteStartElement(new SheetData());
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
        _sheet.Close();
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
        _sheet!.WriteStartElement(new Row(),
            [new OpenXmlAttribute("r", "", _rowIndex.ToString(CultureInfo.InvariantCulture))]);
    }

    private string CellRef(int col) => _colRefs[col] + _rowIndex.ToString(CultureInfo.InvariantCulture);

    private void WriteStringCell(int col, string text)
    {
        _sheet!.WriteStartElement(new Cell(),
            [new OpenXmlAttribute("r", "", CellRef(col)), new OpenXmlAttribute("t", "", "inlineStr")]);
        _sheet.WriteStartElement(new InlineString());
        _sheet.WriteElement(new Text(WriterHelpers.SanitizeXml(text)) { Space = SpaceProcessingModeValues.Preserve });
        _sheet.WriteEndElement();   // </is>
        _sheet.WriteEndElement();   // </c>
    }

    private void WriteNumberCell(int col, double num)
    {
        _sheet!.WriteStartElement(new Cell(), [new OpenXmlAttribute("r", "", CellRef(col))]);
        _sheet.WriteElement(new CellValue(num));
        _sheet.WriteEndElement();
    }

    private void WriteBoolCell(int col, bool value)
    {
        _sheet!.WriteStartElement(new Cell(),
            [new OpenXmlAttribute("r", "", CellRef(col)), new OpenXmlAttribute("t", "", "b")]);
        _sheet.WriteElement(new CellValue(value ? "1" : "0"));
        _sheet.WriteEndElement();
    }

    /// <summary>«Точечные» даты — настоящие датные ячейки (OADate + стиль 1/2 из Stylesheet),
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
        _sheet!.WriteStartElement(new Cell(),
        [
            new OpenXmlAttribute("r", "", CellRef(col)),
            new OpenXmlAttribute("s", "", style.ToString(CultureInfo.InvariantCulture)),
        ]);
        _sheet.WriteElement(new CellValue(num));
        _sheet.WriteEndElement();
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

    // ── Стили ────────────────────────────────────────────────────────────────

    /// <summary>Минимальный Stylesheet: только два датных формата. Индексы ячейковых стилей:
    /// 0 — дефолт, 1 — дд.мм.гггг (numFmt 164), 2 — дд.мм.гггг чч:мм (numFmt 165).
    /// Fills обязаны содержать None и Gray125 — Excel иначе считает файл повреждённым.</summary>
    private static void AddStyles(WorkbookPart wb)
    {
        var styles = wb.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = new Stylesheet(
            new NumberingFormats(
                new NumberingFormat { NumberFormatId = 164, FormatCode = "dd.mm.yyyy" },
                new NumberingFormat { NumberFormatId = 165, FormatCode = "dd.mm.yyyy hh:mm" })
            { Count = 2 },
            new Fonts(new Font()) { Count = 1 },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }))
            { Count = 2 },
            new Borders(new Border()) { Count = 1 },
            new CellStyleFormats(new CellFormat()) { Count = 1 },
            new CellFormats(
                new CellFormat(),
                new CellFormat { NumberFormatId = 164, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 165, ApplyNumberFormat = true })
            { Count = 3 });
        styles.Stylesheet.Save();
    }
}
