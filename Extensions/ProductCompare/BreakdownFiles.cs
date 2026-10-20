using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartBase.Extensions.ProductCompare;

/// <summary>Файл расшифровки, отобранный для сравнения: путь, имя без расширения (оно же
/// идет в заголовок блока), часть имени до даты и дата из имени.</summary>
public sealed record BreakdownFile(string Path, string Title, string Project, DateTime Date);

/// <summary>
/// Папка с расшифровками → список файлов по датам. Имя файла расшифровки устроено как
/// «Проект_ГГГГ.ММ.ДД_ФИО» (так их называет макрос расшифровки и читает «Сводная
/// трудоемкость»); хвоста «_ФИО» может и не быть. Все, что мешает однозначному порядку, —
/// отказ с перечнем файлов: сравнение по датам, у которого порядок угадан, хуже, чем никакое.
///
/// Часть имени до даты в отбор не входит: на деле ее пишут свободно («ТД 7Р21С110 (…)
/// АЦИЕ.461411.123», «Платан-РСО АЦИЕ.461411.123 7Р21С110», «ЧАСТНЫЙ СЛУЧАЙ …»), и по имени одно
/// изделие от другого не отличить. Одно ли это изделие, проверяет движок по содержимому —
/// по обозначению КД в строке «Изделие» (<see cref="BreakdownCompareEngine"/>).
/// </summary>
public static partial class BreakdownFiles
{
    /// <summary>Начало имени результата: прошлые прогоны, сохраненные в ту же папку, в
    /// сравнение не берутся.</summary>
    public const string ResultPrefix = "Расшифровка сравнения ";

    // Дата — отдельная часть имени: перед ней подчеркивание, после — подчеркивание или конец.
    // Основной вид «ГГГГ.ММ.ДД»; «ДД.ММ.ГГГГ» принимается на случай ручного переименования —
    // путаницы нет, год виден по длине части (так же разбирает «Сводная трудоемкость»).
    [GeneratedRegex(@"_(\d{4}\.\d{2}\.\d{2}|\d{2}\.\d{2}\.\d{4})(?=_|$)")]
    private static partial Regex DatePart();

    /// <summary>
    /// Имя файла без расширения → проект и дата; не разобралось — <c>null</c>. Проект — все до
    /// даты, без пробелов по краям; пустой проект (имя начинается с даты) не принимается.
    /// </summary>
    public static (string Project, DateTime Date)? ParseName(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        foreach (Match match in DatePart().Matches(title))
        {
            if (!DateTime.TryParseExact(match.Groups[1].Value, ["yyyy.MM.dd", "dd.MM.yyyy"],
                                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;

            var project = title[..match.Index].Trim();
            if (project.Length == 0) return null;
            return (project, date);
        }
        return null;
    }

    /// <summary>
    /// Файлы расшифровок папки (без подпапок) в порядке дат, от ранней к поздней. Берутся
    /// .xlsx и .xlsm; служебные «~$…» и прошлые результаты пропускаются. Отказ
    /// (<see cref="ProductCompareException"/> с перечнем файлов), если в каком-то имени нет
    /// даты, если дата повторяется или файлов меньше двух.
    /// </summary>
    public static IReadOnlyList<BreakdownFile> Collect(string folder, IProgress<string>? log)
    {
        var files     = new List<BreakdownFile>();
        var undated   = new List<string>();
        var skipped   = 0;

        foreach (var path in Directory.EnumerateFiles(folder).Order(StringComparer.CurrentCultureIgnoreCase))
        {
            var name = Path.GetFileName(path);
            var ext  = Path.GetExtension(path);
            if (!ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("~$", StringComparison.Ordinal)) continue;
            if (name.StartsWith(ResultPrefix, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }

            var title  = Path.GetFileNameWithoutExtension(path);
            var parsed = ParseName(title);
            if (parsed is null) { undated.Add(name); continue; }
            files.Add(new BreakdownFile(path, title, parsed.Value.Project, parsed.Value.Date));
        }

        if (skipped > 0)
            log?.Report($"ℹ️ Пропущены результаты прошлых сравнений: {skipped}");

        if (undated.Count > 0)
            throw new ProductCompareException(
                List("В имени нет даты «_ГГГГ.ММ.ДД» — расставить файлы по порядку нельзя:", undated));

        var sameDate = files.GroupBy(f => f.Date).Where(g => g.Count() > 1).OrderBy(g => g.Key).ToList();
        if (sameDate.Count > 0)
            throw new ProductCompareException(List(
                "У нескольких файлов одна и та же дата — какой из них новее, неизвестно:",
                sameDate.SelectMany(g => g.Select(f => $"{g.Key:dd.MM.yyyy} — {Path.GetFileName(f.Path)}"))));

        if (files.Count < 2)
            throw new ProductCompareException(
                $"Для сравнения нужны хотя бы два файла расшифровки, в папке найдено: {files.Count}.");

        return files.OrderBy(f => f.Date).ToList();
    }

    internal static string List(string title, IEnumerable<string> items)
    {
        var text = new StringBuilder(title);
        foreach (var item in items) text.Append("\n • ").Append(item);
        return text.ToString();
    }
}
