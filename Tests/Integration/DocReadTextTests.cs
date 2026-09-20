using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SmartBase.Core.Documents;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Paragraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using Run       = DocumentFormat.OpenXml.Wordprocessing.Run;
using Text      = DocumentFormat.OpenXml.Wordprocessing.Text;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Разбор документа ДЛЯ ЧТЕНИЯ (<c>Core/Documents/DocReadText.cs</c>) — то, что показывает
/// панель чтения библиотеки.
///
/// Тесты интеграционные: открываются настоящие файлы настоящими библиотеками, как и у разбора
/// для индекса. Отдельный набор нужен потому, что цель у этого разбора ДРУГАЯ — человеку нужны
/// строки, абзацы и деление на страницы и листы, а индексу они не нужны вовсе. Сторожим именно
/// это отличие: схлопни кто-нибудь переводы строк «для единообразия» — панель превратится в одну
/// бесконечную строку, и тест обязан упасть раньше, чем это увидит человек.
/// </summary>
public sealed class DocReadTextTests : IDisposable
{
    private readonly string _dir;

    public DocReadTextTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb-docread-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* занят — уберет система */ }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    private string WritePdf(string name, int pages)
    {
        var path = Path_(name);
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        for (var p = 1; p <= pages; p++)
        {
            var page = builder.AddPage(595, 842);
            page.AddText($"Page {p} line one", 12, new PdfPoint(50, 700), font);
            page.AddText($"Page {p} line two", 12, new PdfPoint(50, 680), font);
        }
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private string WriteDocx(string name, params string[] paragraphs)
    {
        var path = Path_(name);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new DocumentFormat.OpenXml.Wordprocessing.Body();
        foreach (var p in paragraphs) body.Append(new Paragraph(new Run(new Text(p))));
        main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(body);
        main.Document.Save();
        return path;
    }

    /// <summary>Книга с одним листом заданного имени и строками из значений ячеек.</summary>
    private string WriteXlsx(string name, string sheetName, params string[][] rows)
    {
        var path = Path_(name);
        using var doc  = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var book       = doc.AddWorkbookPart();
        book.Workbook  = new Workbook();
        var sheetPart  = book.AddNewPart<WorksheetPart>();
        var data       = new SheetData();

        foreach (var cells in rows)
        {
            var row = new Row();
            foreach (var value in cells)
                row.Append(new Cell { DataType = CellValues.String, CellValue = new CellValue(value) });
            data.Append(row);
        }

        sheetPart.Worksheet = new Worksheet(data);
        book.Workbook.AppendChild(new Sheets()).Append(new Sheet
        {
            Id      = book.GetIdOfPart(sheetPart),
            SheetId = 1,
            Name    = sheetName,
        });
        book.Workbook.Save();
        return path;
    }

    private string WriteText(string name, string text)
    {
        var path = Path_(name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    // ── Главное отличие от разбора для индекса ────────────────────────────────

    /// <summary>
    /// Переводы строк ОСТАЮТСЯ. Разбор для индекса схлопывает их в пробелы намеренно, и если
    /// когда-нибудь эти два разбора «объединят», панель чтения станет одной строкой на весь
    /// документ.
    /// </summary>
    [Fact]
    public void Line_breaks_survive_reading()
    {
        var path   = WriteText("lines.txt", "первая строка\nвторая строка\nтретья строка");
        var result = DocReadText.Read(path);

        Assert.Equal(DocTextStatus.Ok, result.Status);
        Assert.Equal(3, result.Parts[0].Text.Split('\n').Length);
        // А разбор для индекса на том же файле строк не оставляет — это не ошибка, это его цель.
        Assert.DoesNotContain('\n', DocTextExtractor.Extract(path).Text);
    }

    [Theory]
    [InlineData("одна\r\nдве",        2)]   // перевод из Windows — ОДИН, а не два
    [InlineData("одна\n\n\n\nдве",    3)]   // пустых строк подряд не больше одной
    [InlineData("\n\n\nодна",         1)]   // ведущих пустых строк не бывает
    public void Blank_lines_are_tidied(string raw, int lines)
    {
        Assert.Equal(lines, DocReadText.NormalizeForRead(raw).Split('\n').Length);
    }

    [Fact]
    public void Spaces_inside_a_line_are_collapsed()
    {
        Assert.Equal("слово другое", DocReadText.NormalizeForRead("слово    \t  другое   "));
    }

    // ── Деление на части ──────────────────────────────────────────────────────

    [Fact]
    public void Pdf_comes_page_by_page_with_labels()
    {
        var result = DocReadText.Read(WritePdf("doc.pdf", 3));

        Assert.Equal(DocTextStatus.Ok, result.Status);
        Assert.Equal(3, result.Parts.Count);
        Assert.Equal(3, result.Pages);
        Assert.Equal("Страница 1", result.Parts[0].Label);
        Assert.Equal("Страница 3", result.Parts[2].Label);
        Assert.Contains("Page 2", result.Parts[1].Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Word отдается абзацами. Разбор для индекса берет тут <c>InnerText</c> целиком — для
    /// поиска этого хватает, а для чтения это стена текста без единого перевода строки.
    /// </summary>
    [Fact]
    public void Docx_keeps_paragraphs()
    {
        var result = DocReadText.Read(WriteDocx("doc.docx", "Первый абзац", "Второй абзац", "Третий"));

        Assert.Equal(DocTextStatus.Ok, result.Status);
        Assert.Equal(["Первый абзац", "Второй абзац", "Третий"], result.Parts[0].Text.Split('\n'));
    }

    [Fact]
    public void Xlsx_comes_sheet_by_sheet_with_rows()
    {
        var path = WriteXlsx("book.xlsx", "Смета",
            ["Наименование", "Количество"],
            ["Труба",        "12"]);

        var result = DocReadText.Read(path);

        Assert.Equal(DocTextStatus.Ok, result.Status);
        Assert.Equal("Лист «Смета»", result.Parts[0].Label);

        var rows = result.Parts[0].Text.Split('\n');
        Assert.Equal(2, rows.Length);
        Assert.Contains("Наименование", rows[0], StringComparison.Ordinal);
        Assert.Contains("Количество",   rows[0], StringComparison.Ordinal);
        Assert.Contains("Труба",        rows[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_has_no_part_label()
    {
        var result = DocReadText.Read(WriteText("note.md", "# Заголовок\n\nтекст"));

        Assert.Equal(DocTextStatus.Ok, result.Status);
        Assert.Single(result.Parts);
        Assert.Equal("", result.Parts[0].Label);
    }

    // ── Отказы ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Исключений разбор не бросает НИКОГДА — панель чтения открывают на чужих файлах, и любой
    /// из них может оказаться битым, занятым или пропавшим.
    /// </summary>
    [Theory]
    [InlineData("нет-такого.pdf",   DocTextStatus.Failed)]
    [InlineData("картинка.png",     DocTextStatus.Unsupported)]
    [InlineData("",                 DocTextStatus.Failed)]
    public void Bad_input_gives_a_status_and_not_an_exception(string name, DocTextStatus expected)
    {
        var path = name.Length == 0 ? "" : Path_(name);
        Assert.Equal(expected, DocReadText.Read(path).Status);
    }

    [Fact]
    public void Broken_pdf_is_a_failure_not_a_crash()
    {
        var path = Path_("broken.pdf");
        File.WriteAllText(path, "%PDF-1.4 это не документ");

        Assert.Equal(DocTextStatus.Failed, DocReadText.Read(path).Status);
    }

    [Fact]
    public void Empty_text_reads_as_no_text()
    {
        // Пустой txt — то же, что скан без текстового слоя: показывать нечего, и панель
        // говорит об этом словами, а не пустотой.
        Assert.Equal(DocTextStatus.NoText, DocReadText.Read(WriteText("empty.txt", "   \n\n ")).Status);
    }

    [Fact]
    public void File_heavier_than_the_limit_is_not_parsed()
    {
        // Полтора мегабайта: порог в один мегабайт такой файл обязан отсечь.
        var path = WriteText("big.txt", new string('я', 800_000));

        Assert.Equal(DocTextStatus.TooBig, DocReadText.Read(path, maxFileMb: 1).Status);
        // Порог 0 означает «без порога» — тот же уговор, что у разбора для индекса.
        Assert.Equal(DocTextStatus.Ok, DocReadText.Read(path, maxFileMb: 0).Status);
    }
}
