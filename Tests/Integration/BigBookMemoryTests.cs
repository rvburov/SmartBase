using System.IO;
using System.Threading;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SmartBase.Core.Documents;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Большая книга (сотни тысяч строк, как свод трудоемкости на 630 тыс.) читается без дерева
/// всего листа в памяти — и индексом поиска, и панелью «Как в документе». Дерево листа на
/// 200 тыс. строк держало 287 МБ живой памяти и там и там (на 630 тыс. — больше гигабайта);
/// потоковое чтение держит одну строку (индекс, 23 МБ — это сам текст) или показываемые две
/// тысячи (панель, 3 МБ). Замер — пик живой кучи во время чтения: интеграционные тесты идут
/// последовательно (AssemblyInfo), чужих выделений в замере нет.
/// </summary>
public sealed class BigBookMemoryTests : IDisposable
{
    private const int Rows = 200_000;
    private readonly string _dir;
    private readonly string _book;

    public BigBookMemoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb-bigbook-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _book = WriteBook(Path.Combine(_dir, "свод.xlsx"), Rows);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* занят — уберет система */ }
    }

    [Fact]
    public void Index_reads_a_big_book_row_by_row()
    {
        DocText text = default;
        var peak = PeakHeapGrowth(() => text = DocTextExtractor.Extract(_book, maxFileMb: 0));

        Assert.Equal(DocTextStatus.Ok, text.Status);
        Assert.StartsWith("д0 д1 ", text.Text);
        Assert.Contains($"д{Rows - 1}", text.Text);
        Assert.Contains($"{Rows * 10}", text.Text);
        Assert.True(peak < 80L * 1024 * 1024, $"пик кучи при чтении книги {peak / 1024 / 1024} МБ");
    }

    [Fact]
    public void Panel_reads_only_the_rows_it_shows()
    {
        DocBookView view = DocBookView.Empty;
        var peak = PeakHeapGrowth(() => view = DocSheetSource.OpenBook(_book, maxFileMb: 0));

        var sheet = Assert.Single(view.Sheets);
        Assert.Equal(DocSheetSource.MaxRows, sheet.Rows.Count);
        Assert.Equal("д0", sheet.Rows[0].Cells[0].Text);
        Assert.True(sheet.Cut.Rows);
        Assert.Equal(Rows, sheet.Cut.RowsTotal);
        Assert.True(peak < 40L * 1024 * 1024, $"пик кучи при разборе книги для панели {peak / 1024 / 1024} МБ");
    }

    // Наибольший прирост ЖИВОЙ кучи во время действия: замер каждые 10 мс с полной сборкой —
    // мусор (строка листа, уже отпущенная) не в счет, в счет то, что чтение держит.
    private static long PeakHeapGrowth(Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var peak = baseline;
        var done = 0;
        var sampler = new Thread(() =>
        {
            while (Volatile.Read(ref done) == 0)
            {
                var now = GC.GetTotalMemory(forceFullCollection: true);
                if (now > Interlocked.Read(ref peak)) Interlocked.Exchange(ref peak, now);
                Thread.Sleep(10);
            }
        }) { IsBackground = true };
        sampler.Start();
        try { action(); }
        finally
        {
            Volatile.Write(ref done, 1);
            sampler.Join();
        }
        return Math.Max(0, Interlocked.Read(ref peak) - baseline);
    }

    // Книга пишется потоком (OpenXmlWriter): в колонке A — общая строка «дN», в B — число.
    private static string WriteBook(string path, int rows)
    {
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var book  = doc.AddWorkbookPart();
        var part  = book.AddNewPart<WorksheetPart>();
        book.Workbook = new Workbook(new Sheets(new Sheet { Id = book.GetIdOfPart(part), SheetId = 1, Name = "Свод" }));

        var strings = book.AddNewPart<SharedStringTablePart>();
        using (var w = OpenXmlWriter.Create(strings))
        {
            w.WriteStartElement(new SharedStringTable());
            for (var i = 0; i < rows; i++) w.WriteElement(new SharedStringItem(new Text($"д{i}")));
            w.WriteEndElement();
        }

        using (var w = OpenXmlWriter.Create(part))
        {
            w.WriteStartElement(new Worksheet());
            w.WriteElement(new SheetDimension { Reference = $"A1:B{rows}" });
            w.WriteStartElement(new SheetData());
            for (var r = 1; r <= rows; r++)
            {
                w.WriteStartElement(new Row { RowIndex = (uint)r });
                w.WriteElement(new Cell { CellReference = $"A{r}", DataType = CellValues.SharedString,
                                          CellValue = new CellValue(r - 1) });
                w.WriteElement(new Cell { CellReference = $"B{r}", CellValue = new CellValue(r * 10) });
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        }
        book.Workbook.Save();
        return path;
    }
}
