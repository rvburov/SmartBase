using System.IO;
using ClosedXML.Excel;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Чтение файла со списком номеров КД: книга Excel → строки <see cref="ImportRow"/>.
/// Проверка содержимого здесь не идет — ею занимается <see cref="RegistryImport"/>; здесь
/// отсеивается только то, из-за чего читать нечего: не тот формат, не тот лист, не те столбцы.
///
/// Столбцы в файле — ТЕ ЖЕ ВОСЕМЬ, что в коллекции-журнале (<see cref="RegistrySchema.Columns"/>),
/// и ищутся так же — по именам, в любом порядке, лишние пропускаются. Правило одно на файл и на
/// журнал намеренно: файл делают выгрузкой из журнала или по его образцу, и выгрузка журнала со
/// своими столбцами (для письма и прочего) обязана читаться без правки шапки.
/// </summary>
public static class RegistryFile
{
    /// <summary>Расширения, которые читаются. Старый .xls не читается ничем из того, что уже
    /// есть в приложении, — про него сказано отдельным сообщением, а не общим «не открылся».</summary>
    public static IReadOnlyList<string> Extensions => [".xlsx", ".xlsm"];

    /// <summary>
    /// Прочитать файл. Работа с диском и разбор книги — вызывать из фона.
    /// </summary>
    /// <exception cref="DocRegistryException">Файла нет, формат не тот, книга не открылась,
    /// лист пуст или столбцы не сошлись с эталоном. Текст годится для показа как есть.</exception>
    public static IReadOnlyList<ImportRow> Read(string path)
    {
        if (!File.Exists(path))
            throw new DocRegistryException($"Файл не найден: {path}");

        var extension = Path.GetExtension(path);

        if (string.Equals(extension, ".xls", StringComparison.OrdinalIgnoreCase))
            throw new DocRegistryException(
                "Старый формат .xls не читается — откройте файл в Excel и сохраните как .xlsx.");

        if (!Extensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
            throw new DocRegistryException(
                $"Нужен файл Excel ({string.Join(", ", Extensions)}), а выбран «{extension}».");

        using var book = Open(path);

        var sheet = book.Workbook.Worksheets.FirstOrDefault()
            ?? throw new DocRegistryException("В книге нет ни одного листа.");

        // Лист берем ПЕРВЫЙ, а не по имени: файл делают выгрузкой или вручную, и имя листа у
        // каждого свое. Если листов несколько, читается первый — о нем и сказано в документации.
        var used = sheet.RangeUsed()
            ?? throw new DocRegistryException($"Лист «{sheet.Name}» пустой.");

        // Номера столбцов эталона в книге — в порядке эталона: 0 — «Наименование», 1 —
        // «Обозначение» и так далее.
        var at = Header(used.FirstRow(), sheet.Name);

        var rows = new List<ImportRow>();

        foreach (var row in used.Rows().Skip(1))
        {
            // Пустая строка — не ошибка, а пробел между блоками: их в файлах ставят для
            // читаемости, и разбирать там нечего.
            if (row.Cells().All(c => c.IsEmpty())) continue;

            // «Дата регистрации» (at[3]) намеренно НЕ читается: дата регистрации это момент,
            // когда строка попала в журнал, и ставится он при записи. В файле столбец быть
            // обязан (правило одно на файл и на журнал), а значение в нем — любое.
            rows.Add(new ImportRow(
                Line:        row.WorksheetRow().RowNumber(),
                Name:        Text(row.Cell(at[0])),
                Designation: Text(row.Cell(at[1])),
                Fio:         Text(row.Cell(at[2])),
                Project:     Text(row.Cell(at[4])),
                Accounting:  Text(row.Cell(at[5])),
                FirstUse:    Text(row.Cell(at[6])),
                Summary:     Text(row.Cell(at[7]))));
        }

        if (rows.Count == 0)
            throw new DocRegistryException($"На листе «{sheet.Name}» только заголовок — строк с номерами нет.");

        return rows;
    }

    /// <summary>
    /// Найти в шапке столбцы эталона и вернуть их номера в строке (с 1, как у ячеек) в порядке
    /// эталона. Сообщения — как у проверки коллекции (<see cref="RegistrySchema.Check"/>):
    /// человек видит один и тот же разбор и там и тут.
    /// </summary>
    private static IReadOnlyList<int> Header(IXLRangeRow header, string sheetName)
    {
        var cells = header.Cells().Select((cell, i) => (Name: Text(cell), Number: i + 1)).ToList();

        var (found, problem) = RegistrySchema.Find(cells, c => c.Name);

        return found?.Select(c => c.Number).ToList()
            ?? throw new DocRegistryException($"На листе «{sheetName}» {problem}");
    }

    /// <summary>Текст ячейки — тот же, что человек видит в Excel (с учетом формата числа).</summary>
    private static string Text(IXLCell cell) => (cell.GetFormattedString() ?? "").Trim();

    private static ExcelBook Open(string path)
    {
        try
        {
            return ExcelBook.Open(path);
        }
        catch (ExcelOpenException ex)
        {
            throw new DocRegistryException(
                $"Не удалось открыть файл {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
