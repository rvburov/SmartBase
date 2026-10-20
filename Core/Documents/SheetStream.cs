using System.Threading;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SmartBase.Core.Documents;

/// <summary>
/// Потоковое чтение книги XLSX: лист и общая таблица строк читаются по одному элементу, а не
/// деревом целиком. Обращение к <c>WorksheetPart.Worksheet</c> строит в памяти ВСЕ дерево листа:
/// книга на 630 тыс. строк распаковывается в XML в разы больше файла, а дерево объектов — еще в
/// разы больше, и чтение библиотеки держало полтора гигабайта на одной книге. Здесь в памяти
/// одна строка листа за раз (индекс поиска) или только показываемые строки (панель).
/// </summary>
internal static class SheetStream
{
    /// <summary>Строки листа по порядку, по одной: прочитанная строка отпускается, как только
    /// зовущий берет следующую.</summary>
    public static IEnumerable<Row> Rows(WorksheetPart part, CancellationToken ct)
    {
        using var reader = OpenXmlReader.Create(part);
        reader.Read();   // до первого Read читатель стоит перед документом
        while (!reader.EOF)
        {
            ct.ThrowIfCancellationRequested();
            // LoadCurrentElement сам переводит чтение за конец строки — Read после него
            // пропустил бы следующую строку.
            if (reader.IsStartElement && reader.ElementType == typeof(Row))
            {
                if (reader.LoadCurrentElement() is Row row) yield return row;
                continue;
            }
            reader.Read();
        }
    }

    /// <summary>
    /// Лист без лишних строк: все части листа (размер, закрепление, ширины колонок, объединения)
    /// — как в книге, а в данных — только первые <paramref name="maxRows"/> строк и еще одна:
    /// по ней панель узнает, что лист дальше потолка. Остальные строки пропускаются, не
    /// становясь объектами.
    /// </summary>
    public static Worksheet LightWorksheet(WorksheetPart part, int maxRows, CancellationToken ct)
    {
        var sheet = new Worksheet();
        SheetData? data = null;
        var rows = 0;

        using var reader = OpenXmlReader.Create(part);
        reader.Read();   // до первого Read читатель стоит перед документом
        while (!reader.EOF)
        {
            ct.ThrowIfCancellationRequested();
            if (reader.IsStartElement && reader.ElementType == typeof(SheetData))
            {
                data = new SheetData();
                sheet.Append(data);
                reader.Read();
                continue;
            }
            if (reader.IsStartElement && data is not null && reader.ElementType == typeof(Row))
            {
                if (rows++ <= maxRows)
                {
                    if (reader.LoadCurrentElement() is { } row) data.Append(row);
                }
                else reader.Skip();
                continue;
            }
            // Прочие части листа — целиком: они малы, а ReadSheet берет из них оформление.
            if (reader.IsStartElement && reader.Depth == 1)
            {
                if (reader.LoadCurrentElement() is { } element) sheet.Append(element);
                continue;
            }
            reader.Read();
        }
        return sheet;
    }

    /// <summary>Тексты общей таблицы строк по порядку номеров, по одной записи.</summary>
    public static IEnumerable<string> SharedStrings(SharedStringTablePart? part, CancellationToken ct)
    {
        if (part is null) yield break;
        using var reader = OpenXmlReader.Create(part);
        reader.Read();   // до первого Read читатель стоит перед документом
        while (!reader.EOF)
        {
            ct.ThrowIfCancellationRequested();
            if (reader.IsStartElement && reader.ElementType == typeof(SharedStringItem))
            {
                yield return reader.LoadCurrentElement()?.InnerText ?? "";
                continue;
            }
            reader.Read();
        }
    }

    /// <summary>
    /// Общая таблица строк только с теми номерами, на которые ссылаются <paramref name="used"/>:
    /// панели нужна пара тысяч строк из книги, где общих строк могут быть миллионы. Остальные
    /// места массива пусты (null) — к ним никто не обращается.
    /// </summary>
    public static string[] SharedStrings(SharedStringTablePart? part, IReadOnlySet<int> used, CancellationToken ct)
    {
        if (part is null || used.Count == 0) return [];
        var result = new string[used.Max() + 1];
        var index  = 0;
        foreach (var text in SharedStrings(part, ct))
        {
            if (index >= result.Length) break;
            if (used.Contains(index)) result[index] = text;
            index++;
        }
        return result;
    }

    /// <summary>Номера общих строк, на которые ссылаются ячейки листа.</summary>
    public static void CollectSharedIndices(Worksheet sheet, HashSet<int> into)
    {
        foreach (var cell in sheet.Descendants<Cell>())
            if (cell.DataType?.Value == CellValues.SharedString &&
                int.TryParse(cell.CellValue?.InnerText, out var idx) && idx >= 0)
                into.Add(idx);
    }
}
