using System.IO.Compression;
using ClosedXML.Excel;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>Настоящие байты книги: те же восемь колонок, что у загрузки через окно.</summary>
internal static class FileMessageBook
{
    internal sealed record Row(int Line, string Number = "АБВГ.301121.ХХХ", string Name = "Документ",
        string Fio = "Иванов Иван Иванович", string Project = "Проект",
        string Accounting = "", string FirstUse = "", string Summary = "");

    internal static byte[] Create(params Row[] rows) => CreateSheet(sheet =>
    {
        foreach (var row in rows) Fill(sheet, row);
    });

    internal static byte[] CreateSheet(Action<IXLWorksheet> fill, IReadOnlyList<string>? header = null)
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Журнал");
        var columns = header ?? RegistrySchema.Columns;
        for (var i = 0; i < columns.Count; i++) sheet.Cell(1, i + 1).Value = columns[i];
        fill(sheet);
        using var bytes = new MemoryStream();
        book.SaveAs(bytes);
        return bytes.ToArray();
    }

    internal static void Fill(IXLWorksheet sheet, Row row)
    {
        string[] values = [row.Name, row.Number, row.Fio, "дата из файла не используется",
            row.Project, row.Accounting, row.FirstUse, row.Summary];
        for (var i = 0; i < values.Length; i++) sheet.Cell(row.Line, i + 1).Value = values[i];
    }

    internal static byte[] Archive(params (string Name, string Text)[] entries)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        return bytes.ToArray();
    }
}
