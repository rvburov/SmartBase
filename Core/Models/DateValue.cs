using System.Globalization;
using System.Text.RegularExpressions;

namespace SmartBase.Core.Models;

/// <summary>
/// Каноническое значение свойства типа «дата». Покрывает все формы, с которыми
/// работает календарь:
///   • день                       01.01.2026
///   • диапазон дней              01.01.2026 - 03.01.2026
///   • дата + время               03.01.2026 14:00
///   • время в дне                03.01.2026 14:00 - 15:00
///   • диапазон с временем        01.01.2026 - 03.01.2026 14:00 - 14:00
///
/// Хранится в db_field_values в ISO-каноне (см. <see cref="ToStorage"/>), а
/// пользователю показывается в дд.MM.yyyy (см. <see cref="ToDisplay"/>).
/// Модель совпадает по смыслу с <see cref="CalendarRecord"/> (Start/End/AllDay),
/// поэтому одно и то же значение проецируется и в таблицу, и в календарь.
/// </summary>
public readonly record struct DateValue(DateTime Start, DateTime End, bool AllDay)
{
    /// <summary>Конец дня для записей «весь день» — как в диалоге календаря.</summary>
    public static readonly TimeSpan EndOfDay = new(23, 59, 0);

    private static readonly Regex DateRx = new(@"\d{1,2}\.\d{1,2}\.\d{2,4}", RegexOptions.Compiled);
    private static readonly Regex TimeRx = new(@"\d{1,2}:\d{2}", RegexOptions.Compiled);

    // ── Канон для хранения (ISO 8601) ────────────────────────────────────────
    // день:                 2026-01-01
    // диапазон дней:         2026-01-01/2026-01-03
    // дата+время (точка):    2026-01-03T14:00
    // время в дне/диапазоне: 2026-01-03T14:00/2026-01-03T15:00
    public string ToStorage()
    {
        if (AllDay)
            return Start.Date == End.Date
                ? Start.ToString("yyyy-MM-dd")
                : $"{Start:yyyy-MM-dd}/{End:yyyy-MM-dd}";

        var s = Start.ToString("yyyy-MM-ddTHH:mm");
        return Start == End ? s : $"{s}/{End:yyyy-MM-ddTHH:mm}";
    }

    // ── Отображение для пользователя ─────────────────────────────────────────
    public string ToDisplay()
    {
        if (AllDay)
            return Start.Date == End.Date
                ? Start.ToString("dd.MM.yyyy")
                : $"{Start:dd.MM.yyyy} - {End:dd.MM.yyyy}";

        var st = Start.ToString("HH:mm");
        var et = End.ToString("HH:mm");

        if (Start.Date == End.Date)
            return st == et ? $"{Start:dd.MM.yyyy} {st}" : $"{Start:dd.MM.yyyy} {st} - {et}";

        return $"{Start:dd.MM.yyyy} - {End:dd.MM.yyyy} {st} - {et}";
    }

    /// <summary>ISO-строки для колонок db_records.StartDt / EndDt (Kind=Unspecified, как пишет календарь).</summary>
    public (string StartIso, string EndIso) ToRecordColumns()
    {
        static string Iso(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Unspecified).ToString("o");
        return (Iso(Start), Iso(End));
    }

    // ── Разбор ───────────────────────────────────────────────────────────────

    /// <summary>Универсальный разбор: сначала ISO-канон, затем человекочитаемый ввод.</summary>
    public static bool TryParse(string? text, out DateValue value) =>
        TryParseStorage(text, out value) || TryParseDisplay(text, out value);

    public static DateValue? Parse(string? text) => TryParse(text, out var v) ? v : null;

    /// <summary>Разбор ISO-канона из хранилища.</summary>
    public static bool TryParseStorage(string? text, out DateValue value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().Split('/', 2);
        if (!TryIsoPart(parts[0], out var s, out var sHasTime)) return false;

        if (parts.Length == 1)
        {
            value = sHasTime
                ? new DateValue(s, s, false)
                : new DateValue(s.Date, s.Date.Add(EndOfDay), true);
            return true;
        }

        if (!TryIsoPart(parts[1], out var e, out var eHasTime)) return false;

        value = (sHasTime || eHasTime)
            ? new DateValue(s, e, false)
            : new DateValue(s.Date, e.Date.Add(EndOfDay), true);
        return true;
    }

    private static bool TryIsoPart(string p, out DateTime dt, out bool hasTime)
    {
        hasTime = p.Contains('T');
        var fmt = hasTime ? "yyyy-MM-ddTHH:mm" : "yyyy-MM-dd";
        return DateTime.TryParseExact(p, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);
    }

    /// <summary>Разбор ручного ввода: до двух дат (дд.MM.гггг) и до двух времен (чч:мм) в любом порядке.</summary>
    public static bool TryParseDisplay(string? text, out DateValue value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var dates = DateRx.Matches(text).Select(m => m.Value).ToList();
        var times = TimeRx.Matches(text).Select(m => m.Value).ToList();

        if (dates.Count == 0 || !TryDate(dates[0], out var d1)) return false;
        var d2 = d1;
        if (dates.Count > 1 && !TryDate(dates[1], out d2)) return false;

        if (times.Count == 0)
        {
            value = new DateValue(d1.Date, d2.Date.Add(EndOfDay), true);
            return true;
        }

        if (!TryTime(times[0], out var t1)) return false;
        var t2 = t1;
        if (times.Count > 1 && !TryTime(times[1], out t2)) return false;

        value = new DateValue(d1.Date + t1, d2.Date + t2, false);
        return true;
    }

    private static bool TryDate(string s, out DateTime dt) =>
        DateTime.TryParseExact(s, ["dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);

    // ── Строгий разбор целой строки ──────────────────────────────────────────

    // Дата в строгом разборе: дд.ММ.гггг и д.М.гг (как показываем сами), дд/ММ/гггг (день
    // первым — так пишут в России и Европе; месяц первым не угадываем) и гггг-ММ-дд.
    private const string ExactDate =
        @"[0-9]{1,2}\.[0-9]{1,2}\.(?:[0-9]{4}|[0-9]{2})|[0-9]{1,2}/[0-9]{1,2}/[0-9]{4}|[0-9]{4}-[0-9]{2}-[0-9]{2}";

    // Время: чч:мм и чч:мм:сс. Секунды принимаются, но отбрасываются — канон хранит минуты.
    private const string ExactTime = @"[0-9]{1,2}:[0-9]{2}(?::[0-9]{2})?";

    // Тире между датами и между временами — любое: дефис, как в ToDisplay, или типографское.
    private const string ExactDash = @"\s*[-–—]\s*";

    // Все формы ToDisplay (день, диапазон дней, дата + время, время в дне, диапазон с временем) и
    // те же формы с другими записями даты. Якоря ^…$ — главное: лишнее слово до или после даты
    // делает строку не датой.
    private static readonly Regex ExactDisplayRx = new(
        "^(?<d1>" + ExactDate + ")(?:" + ExactDash + "(?<d2>" + ExactDate + "))?"
        + @"(?:\s+(?<t1>" + ExactTime + ")(?:" + ExactDash + "(?<t2>" + ExactTime + "))?)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ISO с буквой T: гггг-ММ-ддTчч:мм[:сс] — так даты уходят из баз и программных выгрузок.
    private static readonly Regex ExactIsoRx = new(
        "^(?<d1>[0-9]{4}-[0-9]{2}-[0-9]{2})T(?<t1>" + ExactTime + ")$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Строгий разбор: true, только если ВСЯ строка (без пробелов по краям) — дата или дата со
    /// временем. Понимает все, что дает <see cref="ToDisplay"/>, канон хранения
    /// (<see cref="ToStorage"/>), дд.ММ.гггг, д.М.гггг, дд.ММ.гг, дд/ММ/гггг, гггг-ММ-дд,
    /// гггг-ММ-ддTчч:мм[:сс] и дату с временем через пробел.
    ///
    /// Нужен там, где текст приходит извне и неправильно понятая дата хуже отказа, — во
    /// вставке из буфера. Терпимый <see cref="TryParse"/> вынимает дату из любого текста:
    /// «Отчет от 01.02.2026» для него — 1 февраля, а остальное молча теряется. Здесь это отказ.
    /// <see cref="TryParse"/> остается как был: на нем держится ручной ввод и календарь.
    /// </summary>
    public static bool TryParseExact(string? text, out DateValue value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (TryParseStorage(s, out value)) return true;
        value = default;

        var m = ExactDisplayRx.Match(s);
        if (!m.Success) m = ExactIsoRx.Match(s);
        if (!m.Success) return false;

        if (!TryExactDate(m.Groups["d1"].Value, out var d1)) return false;
        var d2 = d1;
        if (m.Groups["d2"].Success && !TryExactDate(m.Groups["d2"].Value, out d2)) return false;

        if (!m.Groups["t1"].Success)
        {
            value = new DateValue(d1, d2.Add(EndOfDay), true);
            return true;
        }

        if (!TryExactTime(m.Groups["t1"].Value, out var t1)) return false;
        var t2 = t1;
        if (m.Groups["t2"].Success && !TryExactTime(m.Groups["t2"].Value, out t2)) return false;

        // Та же сборка, что у TryParseDisplay: одна дата — время в дне, две — диапазон.
        value = new DateValue(d1 + t1, d2 + t2, false);
        return true;
    }

    // Дата из уже проверенной регуляркой записи. Числа разбираются руками, а не форматами
    // DateTime: так однозначно, какая часть — день, а какая — год, и «31.02» честно не дата.
    private static bool TryExactDate(string s, out DateTime date)
    {
        date = default;
        int day, month, year;

        if (s.Length == 10 && s[4] == '-')
        {
            year  = int.Parse(s.AsSpan(0, 4), CultureInfo.InvariantCulture);
            month = int.Parse(s.AsSpan(5, 2), CultureInfo.InvariantCulture);
            day   = int.Parse(s.AsSpan(8, 2), CultureInfo.InvariantCulture);
        }
        else
        {
            var parts = s.Split(s.Contains('/') ? '/' : '.');
            day   = int.Parse(parts[0], CultureInfo.InvariantCulture);
            month = int.Parse(parts[1], CultureInfo.InvariantCulture);
            year  = int.Parse(parts[2], CultureInfo.InvariantCulture);
            // Двузначный год — по тому же правилу, что и «дд.MM.yy» ручного ввода (до 2049 — XXI век).
            if (parts[2].Length == 2) year = CultureInfo.InvariantCulture.Calendar.ToFourDigitYear(year);
        }

        if (year is < 1 or > 9999 || month is < 1 or > 12) return false;
        if (day < 1 || day > DateTime.DaysInMonth(year, month)) return false;

        date = new DateTime(year, month, day);
        return true;
    }

    private static bool TryExactTime(string s, out TimeSpan time)
    {
        time = default;
        var parts = s.Split(':');
        var h = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var m = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var sec = parts.Length > 2 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
        if (h is < 0 or > 23 || m is < 0 or > 59 || sec is < 0 or > 59) return false;

        time = new TimeSpan(h, m, 0);
        return true;
    }

    private static bool TryTime(string s, out TimeSpan ts)
    {
        ts = default;
        var parts = s.Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return false;
        if (h is < 0 or > 23 || m is < 0 or > 59) return false;
        ts = new TimeSpan(h, m, 0);
        return true;
    }
}
