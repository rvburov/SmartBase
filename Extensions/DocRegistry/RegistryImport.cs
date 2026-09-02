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
/// <param name="Designation">На входе — ШАБЛОН из файла (<c>АБВГ.301121.ХХХ</c>), на выходе
/// проверки — готовое обозначение с выданным номером (<c>АБВГ.301121.014</c>).</param>
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
/// <param name="Rows">Строки без единой ошибки — с ВЫДАННЫМ обозначением. Записывать их можно,
/// только если <see cref="Ok"/>: файл идет в журнал целиком или никак. Номера в строках файла с
/// ошибками смысла не имеют — такой файл все равно не пишется.</param>
public sealed record ImportCheck(IReadOnlyList<ImportRow> Rows, IReadOnlyList<ImportProblem> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Проверка списка номеров КД, загруженного файлом, и ВЫДАЧА номеров по нему. Чистая логика:
/// чтение книги — в <see cref="RegistryFile"/>, запись — в <see cref="DocRegistrar"/>.
///
/// Обозначение в файле — ШАБЛОН, а не готовый номер: человек пишет только то, что знает сам —
/// код организации и код ЕСКД, — а на месте номера ставит метку <see cref="NumberMark"/> и, если
/// документ с исполнением, метку <see cref="ExecutionMark"/>. Номер выдает расширение — тот же
/// первый свободный в серии, что и при ручной регистрации:
/// <list type="bullet">
/// <item><c>АБВГ.301121.ХХХ</c> — новый документ;</item>
/// <item><c>АБВГ.301121.ХХХ-ХХ</c> — новый документ сразу с исполнением;</item>
/// <item><c>АБВГ.301121.014-ХХ</c> — исполнение уже выданного документа: НОМЕР ЗДЕСЬ НАЗЫВАЮТ,
/// иначе к существующему документу было бы не приписаться, а исполнение все равно выдается —
/// первое свободное у этого номера.</item>
/// </list>
/// Номер сам по себе (<c>АБВГ.301121.014</c>) и готовое исполнение (<c>…014-01</c>) — ошибки:
/// номера выдает журнал, а не тот, кто составлял список.
///
/// Отсюда и разница с ручным окном: там строка одна и номер подбирается перед самой записью,
/// здесь строк много, и номера выдаются подряд — набор занятых номеров серии пополняется по ходу
/// файла, иначе два документа одной серии получили бы один номер.
///
/// Файл записывается ЦЕЛИКОМ ИЛИ НИКАК: одна ошибка — не записывается ни одна строка.
/// Наполовину залитый журнал разобрать потом нечем: часть номеров в нем уже выдана, часть нет, а
/// какая именно — видно только сличением файла с журналом строка за строкой.
/// </summary>
public static class RegistryImport
{
    /// <summary>Метки в шаблоне: три знака на месте номера, два — на месте исполнения.</summary>
    public const string NumberMark    = "ХХХ";
    public const string ExecutionMark = "ХХ";

    /// <summary>Образец обозначения для сообщений об ошибке.</summary>
    public const string Sample = $"АААА.000000.{NumberMark}, АААА.000000.{NumberMark}-{ExecutionMark} " +
                                 $"или АААА.000000.000-{ExecutionMark}";

    // Знак метки: «икс» латинский и кириллический (Х и х), в любом регистре. На
    // клавиатуре они на одной клавише, и человек наберет тот, который у него включен, — счесть
    // это ошибкой значило бы придираться к невидимой глазом разнице.
    private const string Mark = "[XxХх]";

    // Шаблон: четыре буквы кода организации, шесть цифр кода ЕСКД, на месте номера метка ИЛИ
    // настоящий номер, и, если документ с исполнением, метка исполнения. Настоящий номер тут
    // разрешен намеренно — им приписывают исполнение к УЖЕ ВЫДАННОМУ документу; годен он только
    // вместе с меткой исполнения, и это проверяется отдельно (см. Assign).
    private static readonly Regex TemplateRx = new(
        "^(?<org>\\p{L}{4})\\.(?<eskd>\\d{6})\\.(?:" + Mark + "{3}|(?<number>\\d{3}))" +
        "(?<execution>-" + Mark + "{2})?$",
        RegexOptions.Compiled);

    // Готовое обозначение с настоящим исполнением. Разбирается ТОЛЬКО ради сообщения об ошибке:
    // человек написал исполнение сам, и подсказать надо не «не по образцу», а что его выдает
    // расширение.
    private static readonly Regex ReadyRx = new(
        @"^(?<head>\p{L}{4}\.\d{6}\.\d{3})-\d{2}$",
        RegexOptions.Compiled);

    /// <summary>
    /// Проверить строки файла и выдать им номера.
    /// </summary>
    /// <param name="rows">Строки, прочитанные из книги.</param>
    /// <param name="journal">Обозначения, уже выданные в коллекции-журнале: из них считаются
    /// занятые номера серий.</param>
    /// <param name="orgCodes">Коды организации из настроек расширения. Код не из списка —
    /// ошибка: в ручном режиме код берется из этого же списка, и файл не повод заводить в журнале
    /// серию, о которой расширение не знает. Заодно это ловит латиницу, набранную вместо
    /// кириллицы: «АБВГ» с латинскими A и B выглядит так же, а серия получилась бы вторая.</param>
    public static ImportCheck Check(IEnumerable<ImportRow> rows,
                                    IReadOnlyList<string> journal,
                                    IReadOnlyList<string> orgCodes)
    {
        // Занятые номера по сериям и занятые исполнения по номерам: первый раз считаются по
        // журналу, дальше пополняются тем, что выдано выше по файлу. Журнал при этом обходится
        // один раз на серию (и один раз на номер), а не на строку.
        var series     = new Dictionary<string, HashSet<int>>(StringComparer.CurrentCultureIgnoreCase);
        var executions = new Dictionary<string, HashSet<int>>(StringComparer.CurrentCultureIgnoreCase);

        var good     = new List<ImportRow>();
        var problems = new List<ImportProblem>();

        foreach (var row in rows)
        {
            var before = problems.Count;

            var designation = Assign(row, journal, orgCodes, series, executions, problems);

            if (row.Name.Trim().Length == 0)
                problems.Add(new(row.Line, $"не заполнено «{RegistrySchema.TitleColumn}»"));

            if (row.Project.Trim().Length == 0)
                problems.Add(new(row.Line, $"не заполнен «{RegistrySchema.ProjectColumn}»"));

            CheckFio(row, problems);

            // В журнал уходит приведенная строка: выданное обозначение, снятые крайние пробелы.
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
    /// Разобрать шаблон строки и выдать по нему обозначение. <c>null</c> — шаблон не годится,
    /// причина уже дописана в <paramref name="problems"/>.
    ///
    /// Три вида шаблона:
    /// <list type="bullet">
    /// <item><c>АБВГ.301121.ХХХ</c> — новый документ, номер первый свободный в серии;</item>
    /// <item><c>АБВГ.301121.ХХХ-ХХ</c> — то же, и сразу с исполнением. Исполнение выйдет первым:
    /// номер только что выдан, и других исполнений у него нет;</item>
    /// <item><c>АБВГ.301121.014-ХХ</c> — исполнение УЖЕ ВЫДАННОГО документа: номер называет
    /// человек, исполнение выдается первое свободное у этого номера.</item>
    /// </list>
    ///
    /// Каждая строка файла — своя запись, даже когда строки одинаковы. Два
    /// <c>АБВГ.301121.ХХХ-ХХ</c> подряд дадут <c>…014-01</c> и <c>…015-01</c> — два документа с
    /// первым исполнением; два <c>АБВГ.301121.014-ХХ</c> подряд — <c>…014-01</c> и <c>…014-02</c>,
    /// два исполнения одного документа.
    /// </summary>
    private static string? Assign(ImportRow row, IReadOnlyList<string> journal,
                                  IReadOnlyList<string> orgCodes,
                                  Dictionary<string, HashSet<int>> series,
                                  Dictionary<string, HashSet<int>> executions,
                                  List<ImportProblem> problems)
    {
        var text = row.Designation.Trim();

        if (text.Length == 0)
        {
            problems.Add(new(row.Line, $"не заполнено «{RegistrySchema.DesignationColumn}»"));
            return null;
        }

        var match = TemplateRx.Match(text);
        if (!match.Success)
        {
            problems.Add(new(row.Line, WrongShape(text)));
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

        var eskd  = match.Groups["eskd"].Value;
        var named = match.Groups["number"];
        var wants = match.Groups["execution"].Success;

        // Названный номер годится ТОЛЬКО под исполнение. Сам по себе он означал бы, что документу
        // номер назначили в обход журнала, — а номера выдает журнал.
        if (named.Success && !wants)
        {
            problems.Add(new(row.Line,
                $"«{text}» — номер выдает расширение: «{Designation.Prefix(org, eskd)}{NumberMark}». " +
                $"Исполнение этого документа — «{text}-{ExecutionMark}»"));
            return null;
        }

        if (Number(row, text, org, eskd, named, TakenIn(series, journal, org, eskd), problems)
                is not { } number)
            return null;

        if (!wants) return Designation.Format(org, eskd, number);

        var taken = TakenExecutionsIn(executions, journal, org, eskd, number);

        if (Designation.FirstFreeExecutionAmong(taken) is not { } execution)
        {
            problems.Add(new(row.Line,
                $"«{Designation.Format(org, eskd, number)}» — заняты все исполнения " +
                $"{Designation.MinExecution:D2}–{Designation.MaxExecution:D2}"));
            return null;
        }

        taken.Add(execution);

        return Designation.FormatExecution(org, eskd, number, execution);
    }

    /// <summary>
    /// Номер строки: названный человеком или первый свободный в серии. <c>null</c> — не годится.
    ///
    /// Названный номер в журнале может не значиться вовсе — старые документы туда часто не
    /// заносились, и запрет означал бы, что их исполнения внести нельзя. Занятым он после этого
    /// считается в любом случае: строке с меткой он уже не достанется.
    /// </summary>
    private static int? Number(ImportRow row, string text, string org, string eskd,
                               Group named, HashSet<int> taken, List<ImportProblem> problems)
    {
        if (!named.Success)
        {
            if (Designation.FirstFreeAmong(taken) is { } free)
            {
                taken.Add(free);
                return free;
            }

            problems.Add(new(row.Line,
                $"в серии «{Designation.Prefix(org, eskd)}» заняты все номера " +
                $"{Designation.MinNumber:D3}–{Designation.MaxNumber:D3}"));
            return null;
        }

        var number = int.Parse(named.Value, CultureInfo.InvariantCulture);

        if (number < Designation.MinNumber)
        {
            problems.Add(new(row.Line,
                $"«{text}» — {Designation.MinNumber:D3} первый номер серии, меньше не бывает"));
            return null;
        }

        taken.Add(number);
        return number;
    }

    /// <summary>
    /// Объяснение, почему обозначение не подошло. Написанное исполнение разбирается отдельно:
    /// сказать надо не «не по образцу», а что исполнение выдает расширение, и показать ту же
    /// строку правильно — с сохраненным номером документа.
    /// </summary>
    private static string WrongShape(string text)
    {
        var ready = ReadyRx.Match(text);

        return ready.Success
            ? $"«{text}» — исполнение выдает расширение: «{ready.Groups["head"].Value}-{ExecutionMark}»"
            : $"«{text}» — не обозначение по образцу {Sample}";
    }

    /// <summary>Занятые номера серии: считаются по журналу один раз и дальше пополняются тем,
    /// что выдано по ходу файла.</summary>
    private static HashSet<int> TakenIn(Dictionary<string, HashSet<int>> series,
                                        IReadOnlyList<string> journal, string org, string eskd)
    {
        var prefix = Designation.Prefix(org, eskd);

        if (series.TryGetValue(prefix, out var taken)) return taken;

        taken = Designation.TakenNumbers(journal, org, eskd);
        series[prefix] = taken;

        return taken;
    }

    /// <summary>Занятые исполнения одного номера: так же считаются по журналу один раз и дальше
    /// пополняются тем, что выдано по ходу файла.</summary>
    private static HashSet<int> TakenExecutionsIn(Dictionary<string, HashSet<int>> executions,
                                                  IReadOnlyList<string> journal,
                                                  string org, string eskd, int number)
    {
        var key = Designation.Format(org, eskd, number);

        if (executions.TryGetValue(key, out var taken)) return taken;

        taken = Designation.TakenExecutions(journal, org, eskd, number);
        executions[key] = taken;

        return taken;
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
