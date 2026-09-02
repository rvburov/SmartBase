using System.Globalization;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Обозначение документа по ГОСТ 2.201-80: <c>&lt;код организации&gt;.&lt;код ЕСКД&gt;.&lt;порядковый
/// номер&gt;</c> — например <c>АБВГ.301121.001</c>. Чистая логика без UI и без базы: сборка строки,
/// разбор чужого обозначения и подбор первого свободного номера.
///
/// Номер ищется В ПРЕДЕЛАХ СЕРИИ, то есть пары «код организации + код ЕСКД»: у каждого
/// классификатора своя нумерация 001…999, и занятый АБВГ.301121.001 никак не мешает выдать
/// АБВГ.723611.001. Поэтому все методы разбора принимают серию отдельным параметром, а не
/// пытаются угадать ее из строки: код организации — произвольные символы (в том числе с точкой
/// у некоторых предприятий), и деление обозначения по точкам было бы гаданием.
/// </summary>
public static class Designation
{
    /// <summary>Первый и последний номер серии. Три цифры — формат ГОСТ, дальше номеров нет.</summary>
    public const int MinNumber = 1;
    public const int MaxNumber = 999;

    /// <summary>Первое и последнее исполнение документа. Две цифры, «-00» не бывает: документ
    /// без исполнения пишется вообще без дефиса.</summary>
    public const int MinExecution = 1;
    public const int MaxExecution = 99;

    /// <summary>Начало обозначений серии — «АБВГ.301121.» (с точкой на конце).</summary>
    public static string Prefix(string org, string eskd) => $"{org}.{eskd}.";

    /// <summary>Готовое обозначение: номер всегда тремя цифрами («2» → «002»).</summary>
    public static string Format(string org, string eskd, int number) =>
        $"{Prefix(org, eskd)}{number:D3}";

    /// <summary>Обозначение исполнения: «АБВГ.301121.002-04». Исполнение всегда двумя цифрами.</summary>
    public static string FormatExecution(string org, string eskd, int number, int execution) =>
        $"{Format(org, eskd, number)}-{execution:D2}";

    /// <summary>
    /// Номер обозначения, если оно принадлежит серии <paramref name="org"/>.<paramref name="eskd"/>;
    /// иначе <c>null</c> — чужая серия номер не занимает.
    ///
    /// Хвост после приставки читается до первой нецифры, поэтому исполнение (<c>…001-01</c>) и
    /// любая приписка (<c>…001 (копия)</c>) считаются занятым номером 001: это тот же документ,
    /// и выдавать 001 второй раз нельзя.
    ///
    /// Цифр должно быть РОВНО ТРИ. И четыре цифры (<c>…0011</c>), и две (<c>…01</c>) — не номер по
    /// ГОСТ, такую строку пропускаем целиком. Две цифры особенно: «01» с равным правом читается
    /// и как 001, и как обрубок 011 или 014, а гадать за человека нельзя — угадаешь неверно, и
    /// номер уйдет повторно молча. Пропущенная строка хотя бы видна как незанятая.
    /// </summary>
    public static int? NumberIn(string? designation, string org, string eskd) =>
        ParseInSeries(designation, org, eskd)?.Number;

    /// <summary>
    /// Разбор обозначения серии: номер документа и, если он записан, номер исполнения.
    /// <c>null</c> — обозначение не из этой серии или номер записан не по ГОСТ.
    ///
    /// Исполнение читается только в каноне «-NN»: ровно две цифры сразу за номером. «-1» и
    /// «-005» исполнением не считаются — строка остается занятым НОМЕРОМ без исполнения, как и
    /// «…002СБ». Тот же довод, что и у трех цифр номера: угадывать за человека нельзя.
    /// </summary>
    public static (int Number, int? Execution)? ParseInSeries(string? designation, string org, string eskd)
    {
        var text   = (designation ?? "").Trim();
        var prefix = Prefix(org, eskd);

        // Регистр приставки не учитываем: код организации у людей встречается и строчными.
        if (!text.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase)) return null;

        var tail   = text[prefix.Length..];
        var digits = 0;
        while (digits < tail.Length && char.IsAsciiDigit(tail[digits])) digits++;
        if (digits != 3) return null;

        var number = int.Parse(tail[..digits], CultureInfo.InvariantCulture);
        if (number is < MinNumber or > MaxNumber) return null;

        return (number, ExecutionAfter(tail[digits..]));
    }

    /// <summary>Номер исполнения в хвосте после номера документа; <c>null</c> — исполнения нет.</summary>
    private static int? ExecutionAfter(string rest)
    {
        if (rest.Length < 3 || rest[0] != '-') return null;

        var digits = 0;
        while (1 + digits < rest.Length && char.IsAsciiDigit(rest[1 + digits])) digits++;
        if (digits != 2) return null;

        var execution = int.Parse(rest.Substring(1, 2), CultureInfo.InvariantCulture);
        return execution is >= MinExecution and <= MaxExecution ? execution : null;
    }

    /// <summary>
    /// Первый свободный номер серии среди уже выданных обозначений. Дырки заполняются:
    /// заняты 001 и 003 — вернется 002. <c>null</c> — вся серия 001…999 разобрана.
    /// </summary>
    public static int? FirstFree(IEnumerable<string?> designations, string org, string eskd) =>
        FirstFreeAmong(TakenNumbers(designations, org, eskd));

    /// <summary>
    /// Занятые номера серии — НАБОРОМ. Нужно там, где номера выдаются подряд нескольким
    /// документам (загрузка файлом): набор пополняется выданным номером, и следующий документ
    /// получает следующий свободный, а журнал перечитывать не приходится.
    /// </summary>
    public static HashSet<int> TakenNumbers(IEnumerable<string?> designations, string org, string eskd)
    {
        var taken = new HashSet<int>();

        foreach (var d in designations)
            if (NumberIn(d, org, eskd) is { } number) taken.Add(number);

        return taken;
    }

    /// <summary>Первый свободный номер среди занятых; <c>null</c> — вся серия 001…999 разобрана.</summary>
    public static int? FirstFreeAmong(IReadOnlySet<int> taken)
    {
        for (var n = MinNumber; n <= MaxNumber; n++)
            if (!taken.Contains(n)) return n;

        return null;
    }

    // ── Исполнения ────────────────────────────────────────────────────────────

    /// <summary>
    /// Занятые исполнения ОДНОГО номера серии, по возрастанию. Пусто — исполнений нет ни одного;
    /// сам документ при этом может быть и зарегистрирован, и нет.
    /// </summary>
    public static IReadOnlyList<int> ExecutionsOf(IEnumerable<string?> designations,
                                                  string org, string eskd, int number) =>
        [.. new SortedSet<int>(TakenExecutions(designations, org, eskd, number))];

    /// <summary>
    /// Занятые исполнения одного номера — НАБОРОМ. Нужно там же, где и набор номеров: когда
    /// исполнения выдаются подряд нескольким строкам файла, набор пополняется выданным, и
    /// следующая строка получает следующее свободное.
    /// </summary>
    public static HashSet<int> TakenExecutions(IEnumerable<string?> designations,
                                               string org, string eskd, int number)
    {
        var taken = new HashSet<int>();

        foreach (var d in designations)
            if (ParseInSeries(d, org, eskd) is { Execution: { } execution } parsed && parsed.Number == number)
                taken.Add(execution);

        return taken;
    }

    /// <summary>Первое свободное исполнение номера; <c>null</c> — заняты все 01…99.
    /// Дырки заполняются так же, как у номеров: заняты 01 и 03 — вернется 02.</summary>
    public static int? FirstFreeExecution(IEnumerable<string?> designations,
                                          string org, string eskd, int number) =>
        FirstFreeExecutionAmong(TakenExecutions(designations, org, eskd, number));

    /// <summary>Первое свободное исполнение среди занятых; <c>null</c> — заняты все 01…99.</summary>
    public static int? FirstFreeExecutionAmong(IReadOnlySet<int> taken)
    {
        for (var e = MinExecution; e <= MaxExecution; e++)
            if (!taken.Contains(e)) return e;

        return null;
    }

    /// <summary>
    /// Последний выданный номер серии — наибольший из занятых; <c>null</c> — в серии не выдано
    /// ни одного номера.
    ///
    /// Это ДЕЙСТВУЮЩИЙ документ серии: к нему приписывают исполнение, когда его заводят не сразу
    /// за регистрацией самого документа. Первый свободный тут не годится — он означал бы новый
    /// документ, а исполнение приписывают к существующему.
    /// </summary>
    public static int? LastIssued(IEnumerable<string?> designations, string org, string eskd)
    {
        int? last = null;

        foreach (var d in designations)
            if (NumberIn(d, org, eskd) is { } number && (last is null || number > last))
                last = number;

        return last;
    }
}
