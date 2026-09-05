using System.Globalization;

namespace SmartBase.Database.Backup;

/// <summary>Периодичность резервного копирования.</summary>
public enum BackupFrequency { Daily, Weekly, Monthly, Yearly }

/// <summary>
/// Расписание резервного копирования одной базы. Хранится в app.db одной строкой
/// (ключ «backup.&lt;dbId&gt;.schedule»), формат — человекочитаемые токены через пробел:
///   «daily 03:00» · «weekly mon,thu 03:00» · «monthly 15 03:00» · «yearly 15.01 03:00».
/// Разбор строгий: любой мусор (правка app.db руками, откат версии) молча дает
/// <see cref="Default"/>, а не роняет старт — как у остальных настроек (см. AppSettings).
/// <see cref="ToString"/> дает канонический вид и с <see cref="Parse"/> образует round-trip.
/// </summary>
public sealed record BackupSchedule
{
    public BackupFrequency Frequency { get; init; } = BackupFrequency.Daily;

    /// <summary>Время суток запуска (для всех периодичностей).</summary>
    public TimeOnly Time { get; init; } = new(3, 0);

    /// <summary>Дни недели (только Weekly). После Parse не пуст и отсортирован Пн → Вс.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [DayOfWeek.Monday];

    /// <summary>День месяца 1–31 (только Monthly). В коротком месяце берется его последний день.</summary>
    public int DayOfMonth { get; init; } = 1;

    /// <summary>Месяц 1–12 и день 1–31 (только Yearly). 29.02 в невисокосный год — 28.02.</summary>
    public int Month { get; init; } = 1;
    public int Day   { get; init; } = 1;

    /// <summary>Стандарт: каждый день в 03:00 — ночью база наименее занята.</summary>
    public static BackupSchedule Default => new();

    // Синтезированное равенство record сравнило бы Days по ссылке (у списков нет
    // value-равенства) — два одинаковых расписания считались бы разными. Сравниваем по значению.
    public bool Equals(BackupSchedule? other) =>
        other is not null
        && Frequency == other.Frequency && Time == other.Time
        && DayOfMonth == other.DayOfMonth && Month == other.Month && Day == other.Day
        && Days.SequenceEqual(other.Days);

    public override int GetHashCode()
    {
        var hash = HashCode.Combine(Frequency, Time, DayOfMonth, Month, Day);
        foreach (var d in Days) hash = HashCode.Combine(hash, d);
        return hash;
    }

    // Токены дней недели: неделя с понедельника, как в календаре приложения.
    private static readonly (string Token, DayOfWeek Day)[] DayTokens =
    [
        ("mon", DayOfWeek.Monday), ("tue", DayOfWeek.Tuesday), ("wed", DayOfWeek.Wednesday),
        ("thu", DayOfWeek.Thursday), ("fri", DayOfWeek.Friday), ("sat", DayOfWeek.Saturday),
        ("sun", DayOfWeek.Sunday),
    ];

    /// <summary>Разобрать строку настройки; мусор → <see cref="Default"/>.</summary>
    public static BackupSchedule Parse(string? text)
    {
        var tokens = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return Default;

        return tokens[0] switch
        {
            "daily"   when tokens.Length == 2 && TryTime(tokens[1], out var t) =>
                new BackupSchedule { Frequency = BackupFrequency.Daily, Time = t },

            "weekly"  when tokens.Length == 3 && TryDays(tokens[1], out var days) && TryTime(tokens[2], out var t) =>
                new BackupSchedule { Frequency = BackupFrequency.Weekly, Days = days, Time = t },

            "monthly" when tokens.Length == 3 && TryInt(tokens[1], 1, 31, out var dom) && TryTime(tokens[2], out var t) =>
                new BackupSchedule { Frequency = BackupFrequency.Monthly, DayOfMonth = dom, Time = t },

            "yearly"  when tokens.Length == 3 && TryMonthDay(tokens[1], out var month, out var day) && TryTime(tokens[2], out var t) =>
                new BackupSchedule { Frequency = BackupFrequency.Yearly, Month = month, Day = day, Time = t },

            _ => Default,
        };
    }

    /// <summary>Канонический вид для хранения (round-trip с <see cref="Parse"/>).</summary>
    public override string ToString()
    {
        var time = Time.ToString("HH:mm", CultureInfo.InvariantCulture);
        return Frequency switch
        {
            BackupFrequency.Weekly  => $"weekly {string.Join(',', SortedTokens(Days))} {time}",
            BackupFrequency.Monthly => $"monthly {DayOfMonth.ToString(CultureInfo.InvariantCulture)} {time}",
            BackupFrequency.Yearly  => $"yearly {Day:00}.{Month:00} {time}",
            _                       => $"daily {time}",
        };
    }

    private static IEnumerable<string> SortedTokens(IReadOnlyList<DayOfWeek> days) =>
        DayTokens.Where(t => days.Contains(t.Day)).Select(t => t.Token);

    private static bool TryTime(string s, out TimeOnly time) =>
        TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static bool TryInt(string s, int min, int max, out int value) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
        && value >= min && value <= max;

    // «dd.MM» — как пишут дату по-русски (15.01 = 15 января). Проверка дня — по
    // високосному 2000 году: 29.02 законен (сработает в невисокосный как 28.02), 30.02 — нет.
    private static bool TryMonthDay(string s, out int month, out int day)
    {
        month = day = 0;
        var parts = s.Split('.');
        return parts.Length == 2
            && TryInt(parts[0], 1, 31, out day)
            && TryInt(parts[1], 1, 12, out month)
            && day <= DateTime.DaysInMonth(2000, month);
    }

    // Дни недели: «mon,thu» — без пробелов, дубликаты и мусор отвергаются целиком.
    private static bool TryDays(string s, out IReadOnlyList<DayOfWeek> days)
    {
        days = [];
        var result = new List<DayOfWeek>();
        foreach (var token in s.Split(','))
        {
            var known = DayTokens.FirstOrDefault(t => t.Token == token);
            if (known.Token is null || result.Contains(known.Day)) return false;
            result.Add(known.Day);
        }
        if (result.Count == 0) return false;

        // Канонический порядок Пн → Вс: ToString дает одну и ту же строку для одного набора.
        result.Sort((a, b) => MondayFirst(a).CompareTo(MondayFirst(b)));
        days = result;
        return true;
    }

    private static int MondayFirst(DayOfWeek d) => ((int)d + 6) % 7;
}
