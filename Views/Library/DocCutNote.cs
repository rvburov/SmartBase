using System.Globalization;
using SmartBase.Core.Documents;
using SmartBase.Utils;

namespace SmartBase.Views.Library;

/// <summary>
/// Строка «показано не все» над содержимым панели чтения — чистая логика без Avalonia, поэтому
/// проверяется тестами, как и строка подвала раздела (<see cref="DocStatus"/>).
///
/// У панели есть потолки, и они нужны: том на девятьсот страниц или выгрузка на сто тысяч строк
/// не должны съесть память ради одного открытия. Плохо было другое — потолки обрывали документ
/// МОЛЧА: том показывал треть, лист — первые две тысячи строк, и ничто не говорило, что дальше
/// есть еще. Человек читал обрезанный документ как целый.
///
/// Здесь же — скрытые листы книги. Их не показывает и сам Excel, а поиск по содержимому ищет и в
/// них: найденное там слово в панели не подсветится, и без этой строки панель выглядела бы
/// сломанной.
/// </summary>
public static class DocCutNote
{
    // Куда идти за остальным: целиком документ показывает программа, в которой он сделан.
    private const string OpenHint = "Целиком документ открывается кнопкой «Открыть» на его карточке.";

    /// <summary>Режим «Текст»: все, чего не показал разбор для чтения.</summary>
    public static string ForText(DocReadResult read)
    {
        var cut  = read.Cut;
        var cuts = new List<string>();

        if (read.Pages > 0 && cut.PagesShown > 0 && cut.PagesShown < read.Pages)
            cuts.Add(Pages(cut.PagesShown, read.Pages));
        else if (cut.Text)
            cuts.Add("Документ показан не целиком: панель читает первые четыре миллиона знаков.");

        if (cut.Sheets.Count == 1)
            cuts.Add(Sheet(cut.Sheets[0].Name, cut.Sheets[0].Cut));
        else if (cut.Sheets.Count > 1)
            cuts.Add($"Не целиком показаны листы {Names(cut.Sheets.Select(s => s.Name))}: панель " +
                     $"читает первые {N(DocSheetSource.MaxRows)} строк и " +
                     $"{N(DocSheetSource.MaxColumns)} {Columns(DocSheetSource.MaxColumns)} листа.");

        return Compose(cuts, cut.HiddenSheets);
    }

    /// <summary>Режим «Как в документе», PDF: страницы за потолком.</summary>
    public static string ForPages(int shown, int total) =>
        Compose(shown > 0 && shown < total ? [Pages(shown, total)] : [], hiddenSheets: 0);

    /// <summary>Режим «Как в документе», книга: открытый лист и скрытые листы книги.</summary>
    public static string ForSheet(DocSheet? sheet, int hiddenSheets) =>
        Compose(sheet is { Cut.Any: true } ? [Sheet(sheet.Name, sheet.Cut)] : [], hiddenSheets);

    // Сначала то, что обрезано, и куда идти за остальным; потом скрытые листы — за ними идти
    // некуда, их не показывает и сам Excel.
    private static string Compose(List<string> cuts, int hiddenSheets)
    {
        var notes = new List<string>(cuts);
        if (cuts.Count > 0)   notes.Add(OpenHint);
        if (hiddenSheets > 0) notes.Add(Hidden(hiddenSheets));
        return string.Join(" ", notes);
    }

    private static string Pages(int shown, int total) => $"Показаны страницы 1–{N(shown)} из {N(total)}.";

    private static string Sheet(string name, DocSheetCut cut)
    {
        var parts = new List<string>();
        if (cut.Rows)
            parts.Add(cut.RowsTotal > cut.RowsShown
                ? $"строки 1–{N(cut.RowsShown)} из {N(cut.RowsTotal)}"
                : $"строки 1–{N(cut.RowsShown)}");
        if (cut.Columns)
            parts.Add(cut.ColumnsTotal > cut.ColumnsShown
                ? $"первые {N(cut.ColumnsShown)} {Columns(cut.ColumnsShown)} из {N(cut.ColumnsTotal)}"
                : $"первые {N(cut.ColumnsShown)} {Columns(cut.ColumnsShown)}");
        return $"Лист «{name}» показан не целиком: {string.Join(", ", parts)}.";
    }

    private static string Hidden(int count)
    {
        var them = count == 1 ? "его" : "их";
        var in_  = count == 1 ? "в нем" : "в них";
        return $"В книге {Plural.Count(count, "скрытый лист", "скрытых листа", "скрытых листов")}: " +
               $"панель {them} не показывает, как и Excel, а поиск по содержимому ищет и {in_}.";
    }

    private static string Columns(int n) => Plural.Of(n, "колонка", "колонки", "колонок");

    // «Смета» · «Смета» и «Итоги» · «Смета», «Ресурсы» и «Итоги» · «Смета», «Ресурсы» и еще 3.
    private static string Names(IEnumerable<string> names)
    {
        var list = names.Select(n => $"«{n}»").ToList();
        if (list.Count > 3) return $"{string.Join(", ", list.Take(2))} и еще {list.Count - 2}";
        return list.Count == 1 ? list[0] : $"{string.Join(", ", list.Take(list.Count - 1))} и {list[^1]}";
    }

    /// <summary>
    /// Число с разделителем разрядов — неразрывным пробелом: «5 400», а не «5400», и строка не
    /// переносится посреди числа. Не через культуру системы: у нее разделитель свой, а строка
    /// обязана выглядеть одинаково везде.
    /// </summary>
    public static string N(int n) =>
        n.ToString("#,0", CultureInfo.InvariantCulture).Replace(",", " ");
}
