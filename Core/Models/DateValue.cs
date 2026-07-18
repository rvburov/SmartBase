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

    /// <summary>Разбор ручного ввода: до двух дат (дд.MM.гггг) и до двух времён (чч:мм) в любом порядке.</summary>
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
