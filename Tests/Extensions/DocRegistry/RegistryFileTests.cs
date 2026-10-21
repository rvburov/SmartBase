using ClosedXML.Excel;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Чтение файла со списком номеров КД: настоящая книга Excel на диске → строки для проверки.
/// Здесь проверяется только то, из-за чего читать нечего (не тот формат, не те столбцы) и то,
/// как строки ложатся по столбцам; содержимое разбирает <see cref="RegistryImport"/>.
/// </summary>
public sealed class RegistryFileTests
{
    private static readonly string[] Values =
    [
        "Втулка", "АБВГ.301121.ХХХ", "Иванов Иван Иванович", "27.08.2026",
        "Проект", "Учтено", "Изделие 1", "Кратко",
    ];

    /// <summary>Собрать книгу: заголовок эталонный, если не сказано иное.</summary>
    private static string Book(TempDir dir, Action<IXLWorksheet> fill,
                               IReadOnlyList<string>? header = null, string name = "Список.xlsx")
    {
        var path = Path.Combine(dir.Path, name);

        using var book  = new XLWorkbook();
        var       sheet = book.AddWorksheet("Журнал");

        var columns = header ?? RegistrySchema.Columns;
        for (var i = 0; i < columns.Count; i++) sheet.Cell(1, i + 1).Value = columns[i];

        fill(sheet);
        book.SaveAs(path);

        return path;
    }

    private static void Fill(IXLWorksheet sheet, int line, IReadOnlyList<string>? values = null)
    {
        var row = values ?? Values;
        for (var i = 0; i < row.Count; i++) sheet.Cell(line, i + 1).Value = row[i];
    }

    private static string Refuse(TempDir dir, Action<IXLWorksheet> fill,
                                 IReadOnlyList<string>? header = null) =>
        Assert.Throws<DocRegistryException>(() => RegistryFile.Read(Book(dir, fill, header))).Message;

    // ── Что читается ──────────────────────────────────────────────────────────

    [Fact]
    public void Row_is_read_column_by_column()
    {
        using var dir = new TempDir("registry-file");

        var rows = RegistryFile.Read(Book(dir, sheet => Fill(sheet, 2)));
        var row  = Assert.Single(rows);

        Assert.Equal(2,                       row.Line);
        Assert.Equal("Втулка",                row.Name);
        Assert.Equal("АБВГ.301121.ХХХ",       row.Designation);
        Assert.Equal("Иванов Иван Иванович",  row.Fio);
        Assert.Equal("Проект",                row.Project);
        Assert.Equal("Учтено",                row.Accounting);
        Assert.Equal("Изделие 1",             row.FirstUse);
        Assert.Equal("Кратко",                row.Summary);
    }

    /// <summary>Номер строки — НАСТОЯЩИЙ номер в книге: по нему человек ищет ошибку у себя в
    /// файле, а пустые строки между блоками сдвигают порядковый счет.</summary>
    [Fact]
    public void Line_is_the_row_number_in_the_book()
    {
        using var dir = new TempDir("registry-file");

        var rows = RegistryFile.Read(Book(dir, sheet =>
        {
            Fill(sheet, 2);
            Fill(sheet, 5, ["Вал", "АБВГ.301121.ХХХ", "Иванов Иван Иванович", "", "Проект", "", "", ""]);
        }));

        Assert.Equal([2, 5], rows.Select(r => r.Line));
    }

    /// <summary>
    /// Столбец даты не читается вовсе: дата регистрации — момент записи в журнал. Что бы в
    /// ячейке ни стояло, строка от этого не портится и ошибкой не становится.
    /// </summary>
    [Fact]
    public void Date_column_is_not_read()
    {
        using var dir = new TempDir("registry-file");

        var rows = RegistryFile.Read(Book(dir, sheet =>
        {
            Fill(sheet, 2);
            sheet.Cell(2, 4).Value = "в августе";
        }));

        var row = Assert.Single(rows);
        Assert.Equal("Втулка",          row.Name);
        Assert.Equal("АБВГ.301121.ХХХ", row.Designation);
    }

    // ── Шапка ─────────────────────────────────────────────────────────────────

    /// <summary>Имена столбцов сверяются так же, как у коллекции: с точностью до регистра,
    /// крайних пробелов и написания буквы «е».</summary>
    [Fact]
    public void Header_forgives_case_and_the_letter_e()
    {
        using var dir = new TempDir("registry-file");

        var header = RegistrySchema.Columns
            .Select(c => c.ToUpperInvariant().Replace('Е', 'Ё'))
            .ToList();

        Assert.Single(RegistryFile.Read(Book(dir, sheet => Fill(sheet, 2), header)));
    }

    /// <summary>Столбцы ищутся по именам: переставленные «ФИО» и «Проект» отдают каждый свое
    /// значение, а не соседнее.</summary>
    [Fact]
    public void Swapped_columns_are_read_by_name()
    {
        using var dir = new TempDir("registry-file");

        var header = RegistrySchema.Columns.ToList();
        (header[2], header[4]) = (header[4], header[2]);
        var values = Values.ToList();
        (values[2], values[4]) = (values[4], values[2]);

        var row = Assert.Single(RegistryFile.Read(Book(dir, sheet => Fill(sheet, 2, values), header)));

        Assert.Equal("Иванов Иван Иванович", row.Fio);
        Assert.Equal("Проект",               row.Project);
    }

    /// <summary>
    /// Свои столбцы журнала (для письма и прочего) не мешают: выгрузка такого журнала читается
    /// без правки шапки, а лишние значения пропускаются. Столбец вставлен В СЕРЕДИНУ, чтобы
    /// сдвинуть все следующие за ним.
    /// </summary>
    [Fact]
    public void Extra_columns_are_skipped()
    {
        using var dir = new TempDir("registry-file");

        List<string> header = [.. RegistrySchema.Columns.Take(2), "Письмо", .. RegistrySchema.Columns.Skip(2), "Ответ"];
        List<string> values = [.. Values.Take(2), "Исх. 15", .. Values.Skip(2), "Получен"];

        var row = Assert.Single(RegistryFile.Read(Book(dir, sheet => Fill(sheet, 2, values), header)));

        Assert.Equal("Втулка",               row.Name);
        Assert.Equal("АБВГ.301121.ХХХ",      row.Designation);
        Assert.Equal("Иванов Иван Иванович", row.Fio);
        Assert.Equal("Проект",               row.Project);
        Assert.Equal("Учтено",               row.Accounting);
        Assert.Equal("Изделие 1",            row.FirstUse);
        Assert.Equal("Кратко",               row.Summary);
    }

    [Fact]
    public void Missing_column_is_refused()
    {
        using var dir = new TempDir("registry-file");

        var header = RegistrySchema.Columns.Take(7).ToList();

        Assert.Contains($"нет столбца «{RegistrySchema.SummaryColumn}»",
                        Refuse(dir, sheet => Fill(sheet, 2, [.. Values.Take(7)]), header));
    }

    /// <summary>Два столбца с одним именем эталона — отказ: какой из них читать, не понять.</summary>
    [Fact]
    public void Duplicate_column_is_refused()
    {
        using var dir = new TempDir("registry-file");

        var header = RegistrySchema.Columns.Append(RegistrySchema.ProjectColumn).ToList();

        Assert.Contains($"«{RegistrySchema.ProjectColumn}» встречается несколько раз",
                        Refuse(dir, sheet => Fill(sheet, 2), header));
    }

    // ── Читать нечего ─────────────────────────────────────────────────────────

    [Fact]
    public void Header_without_rows_is_refused()
    {
        using var dir = new TempDir("registry-file");

        Assert.Contains("только заголовок", Refuse(dir, _ => { }));
    }

    [Fact]
    public void Missing_file_is_refused()
    {
        using var dir = new TempDir("registry-file");

        var ex = Assert.Throws<DocRegistryException>(
            () => RegistryFile.Read(Path.Combine(dir.Path, "нет.xlsx")));

        Assert.Contains("Файл не найден", ex.Message);
    }

    /// <summary>Старый .xls не читает ничем из того, что есть в приложении, — про него сказано
    /// отдельно, иначе человек получил бы невнятное «не открылся».</summary>
    [Fact]
    public void Old_xls_is_named_as_the_reason()
    {
        using var dir = new TempDir("registry-file");

        var path = dir.Touch("Список.xls");
        var ex   = Assert.Throws<DocRegistryException>(() => RegistryFile.Read(path));

        Assert.Contains(".xls", ex.Message);
        Assert.Contains(".xlsx", ex.Message);
    }

    [Fact]
    public void Other_formats_are_refused()
    {
        using var dir = new TempDir("registry-file");

        var path = dir.Touch("Список.csv");

        Assert.Contains("Нужен файл Excel",
                        Assert.Throws<DocRegistryException>(() => RegistryFile.Read(path)).Message);
    }
}
