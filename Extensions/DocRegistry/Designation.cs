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

    /// <summary>Начало обозначений серии — «АБВГ.301121.» (с точкой на конце).</summary>
    public static string Prefix(string org, string eskd) => $"{org}.{eskd}.";

    /// <summary>Готовое обозначение: номер всегда тремя цифрами («2» → «002»).</summary>
    public static string Format(string org, string eskd, int number) =>
        $"{Prefix(org, eskd)}{number:D3}";

    /// <summary>
    /// Номер обозначения, если оно принадлежит серии <paramref name="org"/>.<paramref name="eskd"/>;
    /// иначе <c>null</c> — чужая серия номер не занимает.
    ///
    /// Хвост после приставки читается до первой нецифры, поэтому исполнение (<c>…001-01</c>) и
    /// любая приписка (<c>…001 (копия)</c>) считаются занятым номером 001: это тот же документ,
    /// и выдавать 001 второй раз нельзя. Хвост длиннее трех цифр — не номер по ГОСТ, такую
    /// строку пропускаем целиком, чтобы не занять номер по ошибке разбора.
    /// </summary>
    public static int? NumberIn(string? designation, string org, string eskd)
    {
        var text   = (designation ?? "").Trim();
        var prefix = Prefix(org, eskd);

        // Регистр приставки не учитываем: код организации у людей встречается и строчными.
        if (!text.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase)) return null;

        var tail   = text[prefix.Length..];
        var digits = 0;
        while (digits < tail.Length && char.IsAsciiDigit(tail[digits])) digits++;
        if (digits is 0 or > 3) return null;

        var number = int.Parse(tail[..digits], CultureInfo.InvariantCulture);
        return number is >= MinNumber and <= MaxNumber ? number : null;
    }

    /// <summary>
    /// Первый свободный номер серии среди уже выданных обозначений. Дырки заполняются:
    /// заняты 001 и 003 — вернется 002. <c>null</c> — вся серия 001…999 разобрана.
    /// </summary>
    public static int? FirstFree(IEnumerable<string?> designations, string org, string eskd)
    {
        var taken = new HashSet<int>();
        foreach (var d in designations)
            if (NumberIn(d, org, eskd) is { } number) taken.Add(number);

        for (var n = MinNumber; n <= MaxNumber; n++)
            if (!taken.Contains(n)) return n;

        return null;
    }
}
