using System.Globalization;
using System.IO;
using ClosedXML.Excel;

namespace SmartBase.Extensions.Automations;

/// <summary>
/// Действие «Выгрузить строки в книгу Excel» — строки предыдущего шага становятся листом книги
/// (docs/ROADMAP-API.md, шаг 13).
///
/// <b>Файл оно СОБИРАЕТ, но никуда не кладет.</b> Книга возвращается байтами
/// (<see cref="ActionResult.MadeFile"/>), а на диск ее пишет оболочка: папку разрешает политика
/// организации, след в журнале безопасности обязателен. Правило то же, что с сетью, и по той же
/// причине — файл наружу это тоже транспорт, и дверь у него одна. Дай расширению писать самому —
/// и белый список папок пришлось бы проверять в каждом расширении по-своему.
///
/// Почему это отдельное действие, а не формат шага «экспорт выборки»: тот читает КОЛЛЕКЦИЮ и
/// пишет ее готовыми писателями экспорта, а здесь на входе строки, которых в базе может не быть
/// вовсе, — ответ сторонней программы, разобранное сообщение, свод автоматизации.
/// </summary>
public sealed class SheetAction : IExtensionAction
{
    /// <summary>Ключ действия внутри расширения.</summary>
    public const string ActionKey = "excel-sheet";

    /// <summary>Ключи настроек шага.</summary>
    public const string FileSetting = "file";

    /// <inheritdoc cref="FileSetting"/>
    public const string SheetSetting = "sheet";

    /// <summary>Как называются файл и лист, если человек не назвал их сам.</summary>
    public const string DefaultName = "Строки";

    /// <summary>Сколько знаков умещается в имени листа Excel — ограничение самого формата.</summary>
    private const int MaxSheetName = 31;

    public string Key  => ActionKey;
    public string Name => "Выгрузить строки в книгу Excel";

    public string Description =>
        "Строки предыдущего шага становятся листом книги. Файл кладет программа — в папку, "
      + "разрешенную политикой, и со строкой в журнале безопасности.";

    public ExtensionActionKind Kind => ExtensionActionKind.Rows;

    public bool MakesFile => true;

    public IReadOnlyList<ExtensionActionField> Fields =>
    [
        new(FileSetting,  "Имя файла", Required: false, Watermark: DefaultName),
        new(SheetSetting, "Имя листа", Required: false, Watermark: DefaultName),
    ];

    public ActionResult Run(ActionContext context, ActionInput input)
    {
        var rows = input.Table;

        if (rows.Count == 0)
            return ActionResult.Failed("выгружать нечего: предыдущий шаг не передал ни одной строки");

        // Колонки — объединение ключей в порядке появления: строки одной выгрузки бывают разного
        // состава, и колонка, встретившаяся только в последней, теряться не должна.
        var columns = new List<string>();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
            foreach (var column in row.Keys)
                if (seen.Add(column)) columns.Add(column);

        if (columns.Count == 0)
            return ActionResult.Failed("выгружать нечего: в строках нет ни одной колонки");

        var bytes = Build(rows, columns, Sheet(input.Value(SheetSetting)));

        var name = input.Value(FileSetting);
        if (name.Length == 0) name = DefaultName;

        return ActionResult.MadeFile(bytes, name + ".xlsx",
                                     $"строк {rows.Count}, колонок {columns.Count}", rows.Count);
    }

    /// <summary>
    /// Собрать книгу в памяти. На диск не ходим вовсе — ни временным файлом: место для файла
    /// выбирает оболочка, и обходить ее временной папкой значило бы завести вторую дверь.
    /// </summary>
    private static byte[] Build(IReadOnlyList<IReadOnlyDictionary<string, string>> rows,
                                IReadOnlyList<string>                             columns,
                                string                                            sheetName)
    {
        using var book  = new XLWorkbook();
        var       sheet = book.Worksheets.Add(sheetName);

        for (var column = 0; column < columns.Count; column++)
        {
            var cell = sheet.Cell(1, column + 1);

            cell.Value           = columns[column];
            cell.Style.Font.Bold = true;
        }

        for (var line = 0; line < rows.Count; line++)
        {
            for (var column = 0; column < columns.Count; column++)
            {
                var value = Value(rows[line], columns[column]);
                var cell  = sheet.Cell(line + 2, column + 1);

                // Число ложится числом, а не текстом: иначе в книге не сложить итог, ради
                // которого выгрузку чаще всего и делают. Канон разбора тот же, что у экспорта:
                // запятая — десятичный разделитель.
                if (Number(value, out var number)) cell.Value = number;
                else                               cell.Value = value;
            }
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, 200);

        using var stream = new MemoryStream();
        book.SaveAs(stream);

        return stream.ToArray();
    }

    private static string Value(IReadOnlyDictionary<string, string> row, string column) =>
        row.TryGetValue(column, out var value) ? value ?? "" : "";

    private static bool Number(string raw, out double value)
    {
        value = 0;

        var text = (raw ?? "").Trim().Replace(',', '.');
        if (text.Length == 0) return false;

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    /// <summary>
    /// Имя листа, которое примет Excel: без запрещенных знаков и не длиннее 31 знака. Отказывать
    /// человеку из-за двоеточия в названии незачем — поправить его дешевле, чем объяснять
    /// ограничение чужого формата.
    /// </summary>
    private static string Sheet(string wanted)
    {
        var name = (wanted ?? "").Trim();
        if (name.Length == 0) name = DefaultName;

        foreach (var forbidden in new[] { ':', '\\', '/', '?', '*', '[', ']' })
            name = name.Replace(forbidden, ' ');

        name = name.Trim();
        if (name.Length == 0) name = DefaultName;

        return name.Length <= MaxSheetName ? name : name[..MaxSheetName];
    }
}
