using System.Globalization;
using System.Text.RegularExpressions;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Одна строка файла со списком номеров КД — столбцы журнала, кроме даты, плюс номер строки в
/// книге. Номер НАСТОЯЩИЙ (с учетом заголовка и пропущенных строк): по нему человек ищет ошибку
/// у себя в файле, и порядковый номер строки тут не помог бы.
///
/// Даты здесь нет намеренно. Столбец «Дата регистрации» в файле быть обязан — ряд столбцов один
/// на файл и на журнал, — но значение из него не берется: дата регистрации это момент, когда
/// строка попала в журнал, а он наступает при записи. Что бы в ячейке ни стояло, оно не читается
/// и ошибкой не бывает.
/// </summary>
public sealed record ImportRow(
    int Line, string Name, string Designation, string Fio,
    string Project, string Accounting, string FirstUse, string Summary);

/// <summary>Ошибка в строке файла: номер строки в книге и объяснение.</summary>
public sealed record ImportProblem(int Line, string Text)
{
    /// <summary>Готовая строка для списка в окне.</summary>
    public override string ToString() => $"Строка {Line}: {Text}";
}

/// <summary>Итог проверки файла: строки, готовые к записи, и все найденные ошибки.</summary>
/// <param name="Rows">Строки без единой ошибки — с приведенным обозначением и разобранной датой.
/// Записывать их можно, только если <see cref="Ok"/>: файл идет в журнал целиком или никак.</param>
public sealed record ImportCheck(IReadOnlyList<ImportRow> Rows, IReadOnlyList<ImportProblem> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Проверка списка номеров КД, загруженного файлом. Чистая логика: чтение книги — в
/// <see cref="RegistryFile"/>, запись — в <see cref="DocRegistrar"/>.
///
/// Номера при загрузке файлом НЕ ВЫДАЮТСЯ: они уже стоят в столбце «Обозначение», и файлом
/// заносят в журнал то, что выдали раньше. Отсюда и набор проверок — не «свободен ли следующий
/// номер», а «то ли это вообще обозначение и не занято ли оно».
///
/// Файл записывается ЦЕЛИКОМ ИЛИ НИКАК: одна ошибка — не записывается ни одна строка.
/// Наполовину залитый журнал разобрать потом нечем: часть номеров в нем уже есть, часть нет, а
/// какая именно часть — видно только сличением файла с журналом строка за строкой.
/// </summary>
public static class RegistryImport
{
    /// <summary>Образец обозначения для сообщений об ошибке.</summary>
    public const string Sample = "АААА.000000.000 или АААА.000000.000-01";

    // Четыре буквы кода организации, шесть цифр кода ЕСКД, три цифры номера и, если он есть,
    // две цифры исполнения. Ни больше ни меньше: «-1» и «0011» — не обозначение по ГОСТ, и
    // догадываться, что имелось в виду, тут не за кого (тот же довод, что в Designation).
    private static readonly Regex DesignationRx = new(
        @"^(?<org>\p{L}{4})\.(?<eskd>\d{6})\.(?<number>\d{3})(?:-(?<execution>\d{2}))?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Проверить строки файла против журнала и списка кодов организации из настроек.
    /// </summary>
    /// <param name="rows">Строки, прочитанные из книги.</param>
    /// <param name="journal">Обозначения, уже выданные в коллекции-журнале.</param>
    /// <param name="orgCodes">Коды организации из настроек расширения. Обозначение с чужим
    /// кодом — ошибка: в ручном режиме код берется из этого же списка, и файл не повод заводить
    /// в журнале серию, о которой расширение не знает. Заодно это ловит латиницу, набранную
    /// вместо кириллицы: «АБВГ» с латинскими A и B выглядит так же, а серия получилась бы
    /// вторая.</param>
    public static ImportCheck Check(IEnumerable<ImportRow> rows,
                                    IEnumerable<string> journal,
                                    IReadOnlyList<string> orgCodes)
    {
        var taken = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var value in journal)
        {
            var text = (value ?? "").Trim();
            if (text.Length > 0) taken.Add(text);
        }

        // Обозначение → строка файла, где оно встретилось впервые. Повтор ВНУТРИ файла — такая
        // же ошибка, как повтор с журналом: двух документов с одним обозначением не бывает.
        var seen     = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        var good     = new List<ImportRow>();
        var problems = new List<ImportProblem>();

        foreach (var row in rows)
        {
            var before = problems.Count;

            var designation = CheckDesignation(row, orgCodes, taken, seen, problems);

            if (row.Name.Trim().Length == 0)
                problems.Add(new(row.Line, $"не заполнено «{RegistrySchema.TitleColumn}»"));

            if (row.Project.Trim().Length == 0)
                problems.Add(new(row.Line, $"не заполнен «{RegistrySchema.ProjectColumn}»"));

            CheckFio(row, problems);

            // В журнал уходит приведенная строка: код организации в написании из настроек,
            // крайние пробелы сняты.
            if (problems.Count == before && designation is not null)
                good.Add(row with
                {
                    Name        = row.Name.Trim(),
                    Designation = designation,
                    Fio         = row.Fio.Trim(),
                    Project     = row.Project.Trim(),
                    Accounting  = row.Accounting.Trim(),
                    FirstUse    = row.FirstUse.Trim(),
                    Summary     = row.Summary.Trim(),
                });
        }

        return new ImportCheck(good, problems);
    }

    /// <summary>
    /// Разобрать обозначение строки. Отдает приведенное обозначение или <c>null</c>, если оно не
    /// годится, — причина уже дописана в <paramref name="problems"/>.
    ///
    /// Разобранное обозначение попадает в <paramref name="seen"/> даже тогда, когда в строке
    /// есть ДРУГИЕ ошибки: повтор ниже по файлу все равно остается повтором, и молчать о нем
    /// нельзя.
    /// </summary>
    private static string? CheckDesignation(ImportRow row, IReadOnlyList<string> orgCodes,
                                            HashSet<string> taken, Dictionary<string, int> seen,
                                            List<ImportProblem> problems)
    {
        var text = row.Designation.Trim();

        if (text.Length == 0)
        {
            problems.Add(new(row.Line, $"не заполнено «{RegistrySchema.DesignationColumn}»"));
            return null;
        }

        var match = DesignationRx.Match(text);
        if (!match.Success)
        {
            problems.Add(new(row.Line, $"«{text}» — не обозначение по образцу {Sample}"));
            return null;
        }

        var number = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
        if (number < Designation.MinNumber)
        {
            problems.Add(new(row.Line,
                $"«{text}» — {Designation.MinNumber:D3} первый номер серии, меньше не бывает"));
            return null;
        }

        var execution = match.Groups["execution"];
        if (execution.Success &&
            int.Parse(execution.Value, CultureInfo.InvariantCulture) < Designation.MinExecution)
        {
            problems.Add(new(row.Line, $"«{text}» — исполнения 00 не бывает: документ без " +
                                       "исполнения пишется вообще без дефиса"));
            return null;
        }

        // Код организации — в написании из настроек: журнал должен выглядеть одинаково, откуда
        // бы строка в него ни пришла.
        var written = match.Groups["org"].Value;
        var org     = orgCodes.FirstOrDefault(
            code => string.Equals(code, written, StringComparison.CurrentCultureIgnoreCase));

        if (org is null)
        {
            problems.Add(new(row.Line,
                $"«{written}» — код организации не из списка настроек расширения"));
            return null;
        }

        var designation = org + text[written.Length..];

        if (seen.TryGetValue(designation, out var first))
        {
            problems.Add(new(row.Line,
                $"«{designation}» — второй раз в файле, первый раз в строке {first}"));
            return null;
        }

        seen[designation] = row.Line;

        if (taken.Contains(designation))
        {
            problems.Add(new(row.Line, $"«{designation}» — уже есть в журнале"));
            return null;
        }

        return designation;
    }

    private static void CheckFio(ImportRow row, List<ImportProblem> problems)
    {
        var fio = row.Fio.Trim();

        if (fio.Length == 0)
        {
            problems.Add(new(row.Line, $"не заполнено «{RegistrySchema.FioColumn}»"));
            return;
        }

        if (!IsFullName(fio))
            problems.Add(new(row.Line,
                $"«{fio}» — ФИО должно быть написано полностью: Фамилия Имя Отчество"));
    }

    /// <summary>
    /// ФИО написано полностью: ровно три слова, в каждом не меньше двух букв.
    ///
    /// Инициалы («Иванов И.И.», «Иванов И. И.») не проходят намеренно: журнал регистрации читают
    /// люди, которые регистратора лично не знают, а по инициалам однофамильцы неразличимы.
    /// Дефис внутри слова разрешен — двойные фамилии и имена бывают.
    /// </summary>
    public static bool IsFullName(string? fio)
    {
        var parts = (fio ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return false;

        foreach (var part in parts)
        {
            if (part.Length < 2) return false;
            if (part[0] == '-' || part[^1] == '-') return false;

            foreach (var symbol in part)
                if (!char.IsLetter(symbol) && symbol != '-') return false;
        }

        return true;
    }
}
