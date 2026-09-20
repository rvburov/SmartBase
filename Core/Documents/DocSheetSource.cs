using System.Globalization;
using System.IO;
using System.Threading;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SmartBase.Core.Documents;

/// <summary>Куда прижат текст ячейки по горизонтали. «Сам» — по правилу Excel: число вправо,
/// текст влево.</summary>
public enum DocAlign { Auto, Left, Center, Right }

/// <summary>Куда прижат текст ячейки по вертикали. По умолчанию Excel прижимает вниз.</summary>
public enum DocVAlign { Bottom, Top, Middle }

/// <summary>
/// Сторона рамки ячейки: цвет, толщина в ступенях (1 тонкая, 2 средняя, 3 толстая) и пунктир.
/// Нулевая толщина — рамки с этой стороны нет.
/// </summary>
public readonly record struct DocEdge(uint Color = 0, int Weight = 0, bool Dashed = false)
{
    public bool Visible => Weight > 0;
}

/// <summary>
/// Оформление ячейки: заливка, начертание текста и рамка. Живет ОБЩИМ списком на лист, а ячейка
/// ссылается на него номером — ровно так это устроено и в самой книге, и по той же причине: в
/// книге на тысячу строк разного оформления обычно десятки видов, а не тысячи.
/// </summary>
/// <param name="Fill">Цвет заливки ARGB; 0 — заливки нет.</param>
/// <param name="Ink">Цвет текста ARGB; 0 — цвет по умолчанию.</param>
/// <param name="Size">Размер шрифта в ПУНКТАХ, как в книге; 0 — размер по умолчанию.</param>
public readonly record struct DocPaint(uint Fill = 0, uint Ink = 0,
                                       bool Bold = false, bool Italic = false, bool Strike = false,
                                       double Size = 0,
                                       DocEdge Left = default, DocEdge Top = default,
                                       DocEdge Right = default, DocEdge Bottom = default)
{
    /// <summary>Есть ли у ячейки хоть какая-то рамка — по ней решают, рисовать ли ее вовсе.</summary>
    public bool Framed => Left.Visible || Top.Visible || Right.Visible || Bottom.Visible;
}

/// <summary>Ячейка листа: текст, размах объединения и как этот текст показан.</summary>
/// <param name="Column">Номер колонки с нуля — ячейки в строке идут не подряд.</param>
/// <param name="Wrap">Текст переносится по словам внутри ячейки (в книге — «переносить текст»).</param>
/// <param name="Paint">Номер оформления в списке листа.</param>
/// <param name="Red">Формат велит показать это значение красным — так помечают отрицательные.</param>
public readonly record struct DocCell(int Column, string Text, int ColumnSpan = 1, int RowSpan = 1,
                                      bool Wrap = false,
                                      DocAlign Align = DocAlign.Auto,
                                      DocVAlign VAlign = DocVAlign.Bottom,
                                      int Paint = 0,
                                      bool Red = false);

/// <summary>
/// Часть объединения, проходящая через строку: колонки с <paramref name="From"/> по
/// <paramref name="To"/>, оформление головной ячейки и две приметы — головная это строка
/// объединения или накрытая, и продолжается ли объединение ниже.
///
/// Нужна она РИСОВАНИЮ, а не разбору: объединенные клетки — это одна клетка, и линии сетки
/// внутри нее быть не должно. Ячейка про это знать не может: она про свой текст, а линию
/// рисует строка.
/// </summary>
public readonly record struct DocMerge(int From, int To, int Paint = 0,
                                       bool Head = true, bool Open = false)
{
    /// <summary>Граница колонок проходит ВНУТРИ объединения — линии сетки здесь не место.</summary>
    public bool Swallows(int boundary) => boundary > From && boundary <= To;
}

/// <summary>Строка листа: номер (с нуля), ячейки, высота в пунктах (0 — высота листа)
/// и объединения, проходящие через нее.</summary>
public readonly record struct DocSheetRow(int Index, IReadOnlyList<DocCell> Cells, double Height = 0,
                                          IReadOnlyList<DocMerge>? Merges = null)
{
    public IReadOnlyList<DocMerge> Merges { get; } = Merges ?? [];
}

/// <summary>Лист книги: имя, строки и ширины колонок в знаках.</summary>
public sealed class DocSheet(string name, IReadOnlyList<DocSheetRow> rows, IReadOnlyList<double> widths,
                             double defaultHeight = DocSheetSource.DefaultHeight,
                             double defaultWidth  = DocSheetSource.DefaultWidth,
                             IReadOnlyList<DocPaint>? paints = null,
                             double fontSize = DocSheetSource.DefaultFontSize)
{
    public string Name { get; } = name;
    public IReadOnlyList<DocSheetRow> Rows { get; } = rows;

    /// <summary>Оформления книги: ячейка ссылается сюда своим номером.</summary>
    public IReadOnlyList<DocPaint> Paints { get; } = paints ?? [];

    /// <summary>Размер основного шрифта книги в ПУНКТАХ — мера для всех прочих размеров.</summary>
    public double FontSize { get; } = fontSize > 0 ? fontSize : DocSheetSource.DefaultFontSize;

    /// <summary>Оформление ячейки; у ячейки без своего — пустое, а не чужое.</summary>
    public DocPaint PaintOf(DocCell cell) =>
        cell.Paint >= 0 && cell.Paint < Paints.Count ? Paints[cell.Paint] : default;

    /// <summary>Ширины колонок в ЗНАКАХ, как их хранит Excel; 0 — колонка скрыта.</summary>
    public IReadOnlyList<double> Widths { get; } = widths;

    /// <summary>Высота строки листа по умолчанию в ПУНКТАХ.</summary>
    public double DefaultHeight { get; } = defaultHeight;

    /// <summary>Ширина колонки листа по умолчанию в ЗНАКАХ.</summary>
    public double DefaultWidth { get; } = defaultWidth;

    /// <summary>Ширина колонки в знаках: своя, если задана в книге, иначе — листа.</summary>
    public double WidthOf(int column) =>
        column >= 0 && column < Widths.Count ? Widths[column] : DefaultWidth;

    /// <summary>Высота строки в пунктах: своя, если задана в книге, иначе — листа.</summary>
    public double HeightOf(DocSheetRow row) => row.Height > 0 ? row.Height : DefaultHeight;

    /// <summary>Сколько колонок занято — по самой правой ячейке листа.</summary>
    public int Columns { get; } = rows.Count == 0
        ? 0
        : rows.Max(r => r.Cells.Count == 0 ? 0 : r.Cells.Max(c => c.Column + c.ColumnSpan));
}

/// <summary>
/// Книга Excel для панели чтения: листы, ячейки и объединения.
///
/// Это ТРЕТИЙ разбор XLSX в программе, и каждый нужен для своего:
/// <list type="bullet">
/// <item><c>DocTextExtractor</c> вываливает весь текст подряд — поиску нужны слова, а не
///       таблица;</item>
/// <item><c>DocReadText</c> отдает строки листа текстом — так лист читается в режиме «Текст»;</item>
/// <item>здесь книга разбирается в СЕТКУ: где какая ячейка стоит, какой она ширины и что с чем
///       объединено. Без этого таблицу не нарисовать — текстом она превращается в кашу из
///       чисел, потому что колонки в ней держат смысл.</item>
/// </list>
///
/// <b>Числа и даты.</b> Excel хранит дату числом (45678), а проценты долей — как их читать,
/// записано в стиле ячейки. Показать «45678» вместо «12.08.2026» значит показать неправду, и
/// неправду убедительную. Разбором записей формата занят <see cref="DocNumberFormat"/>.
///
/// <b>Геометрия.</b> Лист — это не только значения, но и их расстановка: высота строки, ширина
/// колонки, перенос текста в ячейке и выключка. Без них таблица выглядит не как лист, а как
/// сетка одинаковых клеток: ячейка с переносом в три строки не влезает в строку одной высоты и
/// режется, а пустые строки между данными пропадают, и номера строк перестают совпадать с
/// книгой. Поэтому здесь разбираются и они.
///
/// <b>Оформление.</b> Заливка ячейки, цвет и начертание шрифта, рамки. В книге оно лежит НЕ в
/// ячейках, а общей таблицей на всю книгу, и ячейка ссылается на нее номером — так оно устроено
/// и здесь. Цвета при этом почти никогда не лежат цветом: их разворачивает
/// <see cref="DocPalette"/>.
///
/// Чего тут нет: картинок, диаграмм и условного форматирования. Первые два — отдельный рисунок
/// со своими привязками и описанием рядов, третье — правила, которые надо вычислять на каждой
/// ячейке; панель книгу читает, а не считает.
///
/// Исключений не бросает — как и остальные разборщики документов.
/// </summary>
public static class DocSheetSource
{
    /// <summary>Сколько строк листа показываем: дальше это уже не чтение, а выгрузка.</summary>
    public const int MaxRows = DocReadText.MaxSheetRows;

    /// <summary>Сколько колонок показываем.</summary>
    public const int MaxColumns = 128;

    /// <summary>Ширина колонки по умолчанию в знаках — та же, что у Excel.</summary>
    public const double DefaultWidth = 8.43;

    /// <summary>Высота строки по умолчанию в пунктах — та же, что у Excel.</summary>
    public const double DefaultHeight = 15;

    /// <summary>Размер основного шрифта книги по умолчанию в пунктах — тот же, что у Excel.</summary>
    public const double DefaultFontSize = 11;

    /// <summary>
    /// Разобрать книгу. Пустой список — файл не XLSX, не открылся или в нем нет ни одного
    /// листа с данными; для панели это не сбой, а повод остаться в режиме текста.
    /// </summary>
    public static IReadOnlyList<DocSheet> Open(string path, CancellationToken ct = default)
    {
        var ext = DocTextExtractor.ExtensionOf(path);
        if (ext is not ("xlsx" or "xlsm")) return [];

        try
        {
            if (!File.Exists(path)) return [];

            using var doc = SpreadsheetDocument.Open(path, false);
            var book = doc.WorkbookPart;
            if (book is null) return [];

            var shared  = book.SharedStringTablePart?.SharedStringTable
                              ?.Elements<SharedStringItem>().Select(i => i.InnerText).ToArray() ?? [];
            var styles  = Styles(book);

            var sheets = new List<DocSheet>();
            foreach (var entry in book.Workbook?.Sheets?.Elements<Sheet>() ?? [])
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Id?.Value is not { } id) continue;
                if (book.GetPartById(id) is not WorksheetPart part) continue;

                var sheet = ReadSheet(entry.Name?.Value ?? "Лист", part, shared, styles, ct);
                if (sheet.Rows.Count > 0) sheets.Add(sheet);
            }
            return sheets;
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    /// <summary>Фоновый разбор: книга на тысячи строк не должна держать UI-поток.</summary>
    public static Task<IReadOnlyList<DocSheet>> OpenAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => Open(path, ct), ct);

    // ── Разбор листа ──────────────────────────────────────────────────────────

    private static DocSheet ReadSheet(string name, WorksheetPart part, string[] shared,
                                      BookStyles styles, CancellationToken ct)
    {
        var sheet  = part.Worksheet;
        var merges = Merges(sheet);
        var format = Format(sheet);
        var widths = Widths(sheet, format.Width);

        var data = sheet?.Elements<SheetData>().FirstOrDefault();
        if (data is null)
            return new DocSheet(name, [], widths, format.Height, format.Width,
                                styles.Paints, styles.FontSize);

        // Строки собираются ПО НОМЕРАМ, а не подряд: между заполненными строками в книге бывают
        // пустые, и без них таблица сжалась бы, а номера строк слева перестали бы совпадать с
        // книгой. Скрытые строки не показываются вовсе — как и в самом Excel.
        var parsed = new Dictionary<int, (List<DocCell> Cells, double Height)>();
        var hidden = new HashSet<int>();

        // Оформление головных ячеек объединений — им красятся и накрытые строки. Запоминается
        // ДО проверки на пустоту: объединенная шапка сплошь и рядом пустая, а заливка у нее есть.
        var heads = new Dictionary<(int Row, int Col), int>();
        var last  = -1;
        var seen  = 0;

        foreach (var row in data.Elements<Row>())
        {
            ct.ThrowIfCancellationRequested();
            if (++seen > MaxRows) break;

            var index = (int)(row.RowIndex?.Value ?? (uint)seen) - 1;
            if (index < 0) continue;
            if (row.Hidden?.Value == true) { hidden.Add(index); continue; }

            var cells = new List<DocCell>();

            foreach (var cell in row.Elements<Cell>())
            {
                var column = ColumnOf(cell.CellReference?.Value);
                if (column < 0 || column >= MaxColumns) continue;

                // Ячейка, накрытая чужим объединением, своего места на экране не имеет —
                // Excel хранит ее пустой, но показывать ее нельзя даже если текст в ней есть.
                if (merges.Covered.Contains((index, column))) continue;

                var style = cell.StyleIndex?.Value is { } id && styles.Cells.TryGetValue(id, out var s)
                    ? s : default;

                if (merges.Spans.ContainsKey((index, column))) heads[(index, column)] = style.Paint;

                var (text, red) = Text(cell, shared, style.Format);
                if (text.Length == 0) continue;

                var span = merges.Spans.TryGetValue((index, column), out var m) ? m : (Cols: 1, Rows: 1);
                cells.Add(new DocCell(column, text, span.Cols, span.Rows,
                                      style.Wrap, style.Align, style.VAlign, style.Paint, red));
            }

            var height = row.Height?.Value ?? 0;
            if (cells.Count == 0 && height <= 0) continue;

            parsed[index] = (cells, height);
            if (cells.Count > 0) last = index;
        }

        // Объединение вниз тянет лист за собой: строки под ним своих ячеек не имеют (Excel
        // хранит их пустыми), но слитый кусок идет через них, и оборвись лист на последней
        // заполненной строке — кусок оборвался бы вместе с ним.
        foreach (var area in merges.Areas)
            if (area.Top <= last) last = Math.Max(last, area.Bottom);

        // Объединения раскладываются ПО СТРОКАМ, через которые проходят: строка рисует линии
        // сетки, и только она может их не нарисовать там, где клетки слиты в одну.
        var crossing = new Dictionary<int, List<DocMerge>>();
        foreach (var area in merges.Areas)
            for (var r = area.Top; r <= area.Bottom && r < MaxRows; r++)
            {
                if (!crossing.TryGetValue(r, out var list)) crossing[r] = list = [];
                list.Add(new DocMerge(area.Left, area.Right,
                                      heads.GetValueOrDefault((area.Top, area.Left)),
                                      Head: r == area.Top, Open: r < area.Bottom));
            }

        var rows = new List<DocSheetRow>();
        for (var i = 0; i <= last && rows.Count < MaxRows; i++)
        {
            if (hidden.Contains(i)) continue;

            var spans = crossing.GetValueOrDefault(i);
            rows.Add(parsed.TryGetValue(i, out var row)
                ? new DocSheetRow(i, row.Cells, row.Height, spans)
                : new DocSheetRow(i, [], 0, spans));
        }

        return new DocSheet(name, rows, widths, format.Height, format.Width,
                            styles.Paints, styles.FontSize);
    }

    /// <summary>
    /// Высота строки и ширина колонки листа по умолчанию. Excel хранит их один раз на лист, и
    /// без них таблица на всяком листе выглядела бы одинаково — а листы бывают и мелкие, и
    /// разреженные.
    /// </summary>
    private static (double Height, double Width) Format(Worksheet? sheet)
    {
        var pr = sheet?.Elements<SheetFormatProperties>().FirstOrDefault();

        var height = pr?.DefaultRowHeight?.Value ?? DefaultHeight;

        // DefaultColumnWidth пишут не всегда: чаще в файле лежит BaseColumnWidth — ширина в
        // знаках БЕЗ полей ячейки, и Excel добавляет к ней поля сам.
        var width = pr?.DefaultColumnWidth?.Value
                    ?? (pr?.BaseColumnWidth?.Value is { } b && b > 0 ? b + 0.71 : DefaultWidth);

        return (height > 0 ? height : DefaultHeight, width > 0 ? width : DefaultWidth);
    }

    /// <summary>
    /// Объединения: для головной ячейки — ее размах, для накрытых — отметка «не показывать».
    /// Без второго объединенная шапка рисовалась бы поверх собственных пустых клеток, и линии
    /// сетки лезли бы сквозь нее.
    /// </summary>
    private static (Dictionary<(int Row, int Col), (int Cols, int Rows)> Spans,
                    HashSet<(int Row, int Col)> Covered,
                    List<(int Top, int Left, int Bottom, int Right)> Areas) Merges(Worksheet? sheet)
    {
        var spans   = new Dictionary<(int, int), (int, int)>();
        var covered = new HashSet<(int, int)>();
        var areas   = new List<(int, int, int, int)>();

        foreach (var merge in sheet?.Elements<MergeCells>().FirstOrDefault()?.Elements<MergeCell>() ?? [])
        {
            var reference = merge.Reference?.Value ?? "";
            var parts     = reference.Split(':');
            if (parts.Length != 2) continue;

            var (r1, c1) = CellOf(parts[0]);
            var (r2, c2) = CellOf(parts[1]);
            if (r1 < 0 || r2 < 0 || c1 < 0 || c2 < 0) continue;

            var (top, bottom) = (Math.Min(r1, r2), Math.Max(r1, r2));
            var (left, right) = (Math.Min(c1, c2), Math.Max(c1, c2));

            spans[(top, left)] = (right - left + 1, bottom - top + 1);
            areas.Add((top, left, bottom, right));

            for (var r = top; r <= bottom; r++)
            for (var c = left; c <= right; c++)
                if (r != top || c != left) covered.Add((r, c));
        }
        return (spans, covered, areas);
    }

    /// <summary>Ширины колонок в знаках; 0 — колонка скрыта в книге и не показывается.</summary>
    private static IReadOnlyList<double> Widths(Worksheet? sheet, double fallback)
    {
        var widths = new List<double>();
        foreach (var column in sheet?.Elements<Columns>().FirstOrDefault()?.Elements<Column>() ?? [])
        {
            var from  = (int)(column.Min?.Value ?? 1) - 1;
            var to    = (int)(column.Max?.Value ?? 1) - 1;
            var width = column.Hidden?.Value == true ? 0 : column.Width?.Value ?? fallback;
            if (from < 0) continue;

            for (var i = from; i <= Math.Min(to, MaxColumns - 1); i++)
            {
                while (widths.Count <= i) widths.Add(fallback);
                widths[i] = width;
            }
        }
        return widths;
    }

    // ── Значение ячейки ───────────────────────────────────────────────────────

    private static (string Text, bool Red) Text(Cell cell, string[] shared, string? format)
    {
        var raw = cell.CellValue?.InnerText ?? "";

        if (cell.DataType?.Value == CellValues.SharedString)
            return (int.TryParse(raw, out var idx) && idx >= 0 && idx < shared.Length ? shared[idx] : "", false);

        if (cell.DataType?.Value == CellValues.InlineString) return (cell.InnerText, false);
        if (cell.DataType?.Value == CellValues.Boolean)      return (raw == "1" ? "ИСТИНА" : "ЛОЖЬ", false);
        if (raw.Length == 0)                                 return (cell.InnerText, false);

        // Число: как его читать — дата, проценты, деньги — решает запись формата ячейки.
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return (raw, false);

        var shown = DocNumberFormat.Apply(number, format);
        return (shown.Text, shown.Red);
    }

    /// <summary>
    /// Что стиль ячейки говорит о ее показе: запись формата, перенос, выключка и номер
    /// оформления. У ячейки без стиля берется <c>default</c>, и запись формата в нем пустая —
    /// поэтому она и объявлена допускающей null, а не «всегда строкой».
    /// </summary>
    private readonly record struct CellStyle(string? Format, bool Wrap, DocAlign Align,
                                             DocVAlign VAlign, int Paint = 0);

    /// <summary>Разобранные стили книги: по ячейкам, по оформлению и размер основного шрифта.</summary>
    private readonly record struct BookStyles(Dictionary<uint, CellStyle> Cells, DocPaint[] Paints,
                                              double FontSize);

    /// <summary>
    /// Стили ячеек по их номерам. Оформление в книге лежит НЕ в ячейках, а в общей таблице на
    /// всю книгу — ячейка ссылается на нее номером, и записей этих обычно десятки даже в
    /// большой книге. Поэтому оформление разбирается один раз при открытии, а в ячейке остается
    /// номер: копировать цвета и шрифты в каждую ячейку значило бы раздуть лист на ровном месте.
    ///
    /// Встроенные форматы Excel в файле не лежат — у них только номера, поэтому нужные
    /// подставляем сами.
    /// </summary>
    private static BookStyles Styles(WorkbookPart book)
    {
        var cells  = new Dictionary<uint, CellStyle>();
        var styles = book.WorkbookStylesPart?.Stylesheet;
        if (styles?.CellFormats is not { } formats) return new BookStyles(cells, [], DefaultFontSize);

        var custom = styles.NumberingFormats?.Elements<NumberingFormat>()
                           .Where(f => f.NumberFormatId?.Value is not null)
                           .ToDictionary(f => f.NumberFormatId!.Value!, f => f.FormatCode?.Value ?? "")
                     ?? [];

        var palette = DocPalette.Read(book);
        var fonts   = styles.Fonts?.Elements<Font>().ToArray()     ?? [];
        var fills   = styles.Fills?.Elements<Fill>().ToArray()     ?? [];
        var borders = styles.Borders?.Elements<Border>().ToArray() ?? [];

        // Размер основного шрифта книги — мера, от которой считаются все прочие размеры.
        var basis = fonts.Length > 0 ? fonts[0].FontSize?.Val?.Value ?? DefaultFontSize : DefaultFontSize;

        var paints = new List<DocPaint>();
        var index  = 0u;

        foreach (var format in formats.Elements<CellFormat>())
        {
            var id   = format.NumberFormatId?.Value ?? 0;
            var code = custom.TryGetValue(id, out var own) ? own : DocNumberFormat.Builtin(id);

            var alignment = format.Alignment;
            cells[index++] = new CellStyle(code,
                                           alignment?.WrapText?.Value == true,
                                           Horizontal(alignment),
                                           Vertical(alignment),
                                           paints.Count);

            paints.Add(Paint(format, fonts, fills, borders, palette));
        }

        return new BookStyles(cells, [.. paints], basis > 0 ? basis : DefaultFontSize);
    }

    // ── Оформление ячейки ─────────────────────────────────────────────────────

    private static DocPaint Paint(CellFormat format, Font[] fonts, Fill[] fills, Border[] borders,
                                  DocPalette palette)
    {
        var font   = At(fonts,   format.FontId?.Value);
        var fill   = At(fills,   format.FillId?.Value);
        var border = At(borders, format.BorderId?.Value);

        return new DocPaint(
            Fill:   FillOf(fill, palette),
            Ink:    palette.Resolve(font?.Color),
            Bold:   On(font?.Bold), Italic: On(font?.Italic), Strike: On(font?.Strike),
            Size:   font?.FontSize?.Val?.Value ?? 0,
            Left:   EdgeOf(border?.LeftBorder,   palette),
            Top:    EdgeOf(border?.TopBorder,    palette),
            Right:  EdgeOf(border?.RightBorder,  palette),
            Bottom: EdgeOf(border?.BottomBorder, palette));
    }

    private static T? At<T>(T[] list, uint? index) where T : class =>
        index is { } i && i < list.Length ? list[(int)i] : null;

    /// <summary>
    /// Признак шрифта. В книге он включается самим своим присутствием: <c>&lt;b/&gt;</c> — это
    /// «жирный», и значения у него может не быть вовсе.
    /// </summary>
    private static bool On<T>(T? property) where T : BooleanPropertyType =>
        property is not null && property.Val?.Value != false;

    /// <summary>
    /// Заливка ячейки. Берется только сплошная: узорные заливки («в клеточку», «штриховка») —
    /// это рисунок, а не цвет, и подменять их сплошным цветом значило бы соврать сильнее, чем
    /// не показать вовсе. <c>gray125</c> вдобавок стоит в каждой книге вторым по счету и заливкой
    /// не является — им Excel помечает «оформления нет».
    /// </summary>
    private static uint FillOf(Fill? fill, DocPalette palette)
    {
        if (fill?.PatternFill is not { } pattern) return DocPalette.None;
        if (pattern.PatternType?.Value != PatternValues.Solid) return DocPalette.None;

        var color = palette.Resolve(pattern.ForegroundColor);
        return color != DocPalette.None ? color : palette.Resolve(pattern.BackgroundColor);
    }

    /// <summary>
    /// Сторона рамки. Толщина сведена к трем ступеням: тонкая, средняя, толстая — рисовать
    /// двойную линию и «наклонный штрих-пунктир» в панели чтения не за чем, а вот есть рамка
    /// или нет — видно сразу.
    /// </summary>
    private static DocEdge EdgeOf(BorderPropertiesType? side, DocPalette palette)
    {
        if (side?.Style?.Value is not { } style || style == BorderStyleValues.None) return default;

        var weight = style == BorderStyleValues.Thick  || style == BorderStyleValues.Double ? 3
                   : style == BorderStyleValues.Medium || style == BorderStyleValues.MediumDashed
                     || style == BorderStyleValues.MediumDashDot
                     || style == BorderStyleValues.MediumDashDotDot
                     || style == BorderStyleValues.SlantDashDot ? 2
                   : 1;

        var dashed = style == BorderStyleValues.Dashed  || style == BorderStyleValues.Dotted
                  || style == BorderStyleValues.DashDot || style == BorderStyleValues.DashDotDot
                  || style == BorderStyleValues.MediumDashed
                  || style == BorderStyleValues.MediumDashDot
                  || style == BorderStyleValues.MediumDashDotDot;

        // Цвет у рамки может быть не записан — в книге это значит «автоматический», то есть
        // цвет текста. Свой черный сюда ставить нельзя: в темной теме он пропадет.
        return new DocEdge(palette.Resolve(side.Color), weight, dashed);
    }

    // Значения выключки сравниваются, а не разбираются switch'ем: в OpenXml это не перечисление
    // C#, а свой тип со сравнением, и константы в образцах switch он не дает.
    private static DocAlign Horizontal(Alignment? alignment)
    {
        if (alignment?.Horizontal?.Value is not { } value) return DocAlign.Auto;
        if (value == HorizontalAlignmentValues.Left)   return DocAlign.Left;
        if (value == HorizontalAlignmentValues.Right)  return DocAlign.Right;
        if (value == HorizontalAlignmentValues.Center ||
            value == HorizontalAlignmentValues.CenterContinuous) return DocAlign.Center;
        return DocAlign.Auto;
    }

    private static DocVAlign Vertical(Alignment? alignment)
    {
        if (alignment?.Vertical?.Value is not { } value) return DocVAlign.Bottom;
        if (value == VerticalAlignmentValues.Top)    return DocVAlign.Top;
        if (value == VerticalAlignmentValues.Center) return DocVAlign.Middle;
        return DocVAlign.Bottom;
    }



    // ── Ссылки на ячейки ──────────────────────────────────────────────────────

    /// <summary>Номер колонки с нуля по ссылке вида «BC7»; -1 — ссылка не разобрана.</summary>
    public static int ColumnOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return -1;

        var column = 0;
        var seen   = false;
        foreach (var ch in reference)
        {
            if (char.IsAsciiLetter(ch))
            {
                if (seen) return -1;   // буквы после цифр — это не ссылка
                column = column * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
                continue;
            }
            if (!char.IsAsciiDigit(ch)) return -1;
            seen = true;
        }
        return column > 0 ? column - 1 : -1;
    }

    /// <summary>Имя колонки по ее номеру с нуля: 0 → «A», 26 → «AA», 54 → «BC».</summary>
    public static string NameOf(int column)
    {
        if (column < 0) return "";

        var name = "";
        for (var left = column + 1; left > 0; left = (left - 1) / 26)
            name = (char)('A' + (left - 1) % 26) + name;
        return name;
    }

    /// <summary>Строка и колонка с нуля по ссылке вида «BC7».</summary>
    public static (int Row, int Column) CellOf(string? reference)
    {
        var column = ColumnOf(reference);
        if (column < 0) return (-1, -1);

        var digits = new string((reference ?? "").Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var row) && row > 0 ? (row - 1, column) : (-1, -1);
    }
}
