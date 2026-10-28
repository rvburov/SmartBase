using System.Globalization;
using System.Numerics;

namespace SmartBase.Core.Models;

/// <summary>Пересечение диапазона дат с выбранными днями недели и часами каждого дня.
/// Результат — целые секунды в invariant-каноне; отображение отдельно от значения сортировки.</summary>
public static class DatePeriodValue
{
    public static string Calculate(DateValue start, DateValue? end, DateTime now, DatePeriodConfig config)
    {
        if (!config.IsValid || start.End < start.Start || end is { } invalid && invalid.End < invalid.Start)
            return "";
        config.TryGetDayMinutes(out var dayStart, out var dayEnd);
        var from = start.AllDay ? start.Start.Date.Ticks : start.Start.Ticks;
        var to = end is { } value ? value.Start.Ticks : now.Ticks;
        if (to < from) return "";
        if (to == from) return "0";

        var first = from / TimeSpan.TicksPerDay;
        var last = (to - 1) / TimeSpan.TicksPerDay;
        long Intersection(long day)
        {
            // 01.01.0001 — понедельник, поэтому номер дня сразу соответствует битовой маске.
            if ((config.Weekdays & (1 << (int)(day % 7))) == 0) return 0;
            var origin = day * TimeSpan.TicksPerDay;
            return Math.Max(0, Math.Min(to, origin + dayEnd * TimeSpan.TicksPerMinute)
                             - Math.Max(from, origin + dayStart * TimeSpan.TicksPerMinute));
        }
        var ticks = Intersection(first);
        if (last != first)
        {
            ticks += Intersection(last);
            var count = last - first - 1;
            var selected = count / 7 * BitOperations.PopCount((uint)config.Weekdays);
            for (var i = 0; i < count % 7; i++)
                if ((config.Weekdays & (1 << (int)((first + 1 + i) % 7))) != 0) selected++;
            ticks += selected * (dayEnd - dayStart) * TimeSpan.TicksPerMinute;
        }
        return (ticks / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>День в подписи — 24 часа накопленного времени, а не рабочий день по расписанию.</summary>
    public static string Format(string? raw)
    {
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
            return "";
        var days = seconds / 86400;
        var hours = seconds / 3600 % 24;
        var minutes = seconds / 60 % 60;
        var parts = new List<string>(3);
        if (days > 0) parts.Add($"{days.ToString(CultureInfo.InvariantCulture)} д");
        if (hours > 0) parts.Add($"{hours.ToString(CultureInfo.InvariantCulture)} ч");
        if (minutes > 0 || parts.Count == 0) parts.Add($"{minutes.ToString(CultureInfo.InvariantCulture)} мин");
        return string.Join(" ", parts);
    }
}
