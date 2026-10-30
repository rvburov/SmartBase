using System.IO.Compression;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>Проверка вложения до записи: книга, реальные номера строк и ограничения разбора.</summary>
public sealed class FileMessageReadTests
{
    [Theory]
    [InlineData("Список.xlsx")]
    [InlineData("Список.XLSX")]
    [InlineData("Список.xlsm")]
    public void The_existing_template_keeps_fields_and_real_sheet_row_numbers(string name)
    {
        var bytes = FileMessageBook.Create(
            new FileMessageBook.Row(2, Name: "Втулка", Accounting: "Учтен", FirstUse: "Изделие", Summary: "Сталь"),
            new FileMessageBook.Row(7, Name: "Вал", Fio: "Петров Петр Петрович", Project: "Второй проект"));

        var rows = RegistryMessageFile.Read(bytes, name);

        Assert.Equal(new[] { 2, 7 }, rows.Select(row => row.Line));
        Assert.Equal("Втулка", rows[0].Name);
        Assert.Equal("АБВГ.301121.ХХХ", rows[0].Designation);
        Assert.Equal("Иванов Иван Иванович", rows[0].Fio);
        Assert.Equal("Учтен", rows[0].Accounting);
        Assert.Equal("Изделие", rows[0].FirstUse);
        Assert.Equal("Сталь", rows[0].Summary);
        Assert.Equal("Петров Петр Петрович", rows[1].Fio);
        Assert.Equal("Второй проект", rows[1].Project);
    }

    [Fact]
    public void Columns_are_resolved_by_name_and_the_registration_date_is_not_parsed()
    {
        var header = RegistrySchema.Columns.Reverse().Append("Своя колонка").ToArray();
        var bytes = FileMessageBook.CreateSheet(sheet =>
        {
            string[] values = ["Кратко", "Применение", "Учтен", "Машина", "не дата",
                "Иванов Иван Иванович", "АБВГ.301121.ХХХ", "Втулка", "Лишнее"];
            for (var i = 0; i < values.Length; i++) sheet.Cell(2, i + 1).Value = values[i];
        }, header);

        var row = Assert.Single(RegistryMessageFile.Read(bytes, "Список.xlsx"));

        Assert.Equal("Втулка", row.Name);
        Assert.Equal("АБВГ.301121.ХХХ", row.Designation);
        Assert.Equal("Иванов Иван Иванович", row.Fio);
        Assert.Equal("Машина", row.Project);
        Assert.Equal("Применение", row.FirstUse);
        Assert.Equal("Кратко", row.Summary);
    }

    [Fact]
    public void Sparse_books_keep_far_sheet_lines_without_treating_blank_rows_as_documents()
    {
        var bytes = FileMessageBook.Create(
            new FileMessageBook.Row(2, Name: "Первый документ"), new FileMessageBook.Row(1000000, Name: "Далекий документ"));

        var rows = RegistryMessageFile.Read(bytes, "Разреженный список.xlsx");

        Assert.Equal(new[] { 2, 1000000 }, rows.Select(row => row.Line));
        Assert.Equal(new[] { "Первый документ", "Далекий документ" }, rows.Select(row => row.Name));
        Assert.All(rows, row => Assert.Equal("АБВГ.301121.ХХХ", row.Designation));
    }

    [Fact]
    public void A_missing_or_duplicate_header_refuses_the_whole_book()
    {
        var missing = FileMessageBook.CreateSheet(sheet => FileMessageBook.Fill(sheet, new FileMessageBook.Row(2)),
            RegistrySchema.Columns.Take(7).ToArray());
        var duplicate = FileMessageBook.CreateSheet(sheet => FileMessageBook.Fill(sheet, new FileMessageBook.Row(2)),
            RegistrySchema.Columns.Append(RegistrySchema.ProjectColumn).ToArray());

        Assert.Contains(RegistrySchema.SummaryColumn,
            Assert.Throws<DocRegistryException>(() => RegistryMessageFile.Read(missing, "Нет колонки.xlsx")).Message);
        Assert.Contains(RegistrySchema.ProjectColumn,
            Assert.Throws<DocRegistryException>(() => RegistryMessageFile.Read(duplicate, "Повтор.xlsx")).Message);
    }

    [Theory]
    [InlineData("Список.xls")]
    [InlineData("Список.csv")]
    [InlineData("Список.zip")]
    [InlineData("")]
    public void Unsupported_or_missing_names_are_refused_before_reading(string name)
    {
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2));
        Assert.Throws<DocRegistryException>(() => RegistryMessageFile.Read(bytes, name));
    }

    [Fact]
    public void Empty_or_damaged_input_does_not_reach_the_workbook_reader()
    {
        Assert.Throws<DocRegistryException>(() => RegistryMessageFile.Read([], "Пустой.xlsx"));
        Assert.Throws<DocRegistryException>(() => RegistryMessageFile.Read([1, 2, 3, 4], "Поврежден.xlsx"));
        Assert.Throws<DocRegistryException>(() => RegistryMessageFile.Read(
            FileMessageBook.Archive(("other.txt", "это не книга")), "Не книга.xlsx"));
        Assert.Throws<DocRegistryException>(() => RegistryMessageFile.Read(
            FileMessageBook.CreateSheet(_ => { }), "Только шапка.xlsx"));
    }

    [Fact]
    public void The_compressed_file_size_is_limited_before_zip_parsing()
    {
        var bytes = new byte[RegistryMessageFile.MaxFileBytes + 1];
        Assert.Contains("МБ", Assert.Throws<DocRegistryException>(
            () => RegistryMessageFile.Read(bytes, "Большой.xlsx")).Message);
    }

    [Fact]
    public void A_large_expanded_part_is_refused_even_when_the_archive_is_small()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("[Content_Types].xml");
            zip.CreateEntry("xl/workbook.xml");
            using var content = zip.CreateEntry("padding.bin", CompressionLevel.Fastest).Open();
            var block = new byte[64 * 1024];
            for (long written = 0; written <= RegistryMessageFile.MaxExpandedBytes; written += block.Length)
                content.Write(block);
        }
        var archive = bytes.ToArray();
        Assert.True(archive.Length < RegistryMessageFile.MaxFileBytes);
        Assert.Contains("Распакованная", Assert.Throws<DocRegistryException>(
            () => RegistryMessageFile.Read(archive, "Сжатый.xlsx")).Message);
    }

    [Fact]
    public void A_large_sheet_is_refused_before_closed_workbook_loading()
    {
        var cells = string.Concat(Enumerable.Repeat("<c/>", RegistryMessageFile.MaxCells + 1));
        var archive = FileMessageBook.Archive(("[Content_Types].xml", ""), ("xl/workbook.xml", ""),
            ("xl/worksheets/sheet1.xml", "<worksheet><sheetData><row>" + cells + "</row></sheetData></worksheet>"));

        Assert.Contains("ячеек", Assert.Throws<DocRegistryException>(
            () => RegistryMessageFile.Read(archive, "Много ячеек.xlsx")).Message);
    }

    [Fact]
    public void Duplicate_internal_parts_and_external_entities_are_refused()
    {
        var duplicate = FileMessageBook.Archive(("[Content_Types].xml", ""), ("xl/workbook.xml", ""),
            ("xl/workbook.xml", "повтор"));
        var dtd = FileMessageBook.Archive(("[Content_Types].xml", ""), ("xl/workbook.xml", ""),
            ("xl/worksheets/sheet1.xml", "<!DOCTYPE worksheet [<!ENTITY item 'значение'>]><worksheet>&item;</worksheet>"));

        Assert.Contains("повторяются", Assert.Throws<DocRegistryException>(
            () => RegistryMessageFile.Read(duplicate, "Повтор частей.xlsx")).Message);
        Assert.Throws<DocRegistryException>(() => RegistryMessageFile.Read(dtd, "DTD.xlsx"));
    }

    [Fact]
    public void A_file_with_too_many_data_rows_is_refused_instead_of_partly_imported()
    {
        var bytes = FileMessageBook.Create(Enumerable.Range(2, RegistryMessageFile.MaxRows + 1)
            .Select(line => new FileMessageBook.Row(line)).ToArray());

        Assert.Contains(RegistryMessageFile.MaxRows.ToString(), Assert.Throws<DocRegistryException>(
            () => RegistryMessageFile.Read(bytes, "Много строк.xlsx")).Message);
    }

    [Fact]
    public void A_cancelled_read_does_not_begin_file_processing()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => RegistryMessageFile.Read(
            FileMessageBook.Create(new FileMessageBook.Row(2)), "Список.xlsx", cancelled.Token));
    }
}
