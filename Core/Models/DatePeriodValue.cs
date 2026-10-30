using System.Globalization;
using System.Numerics;

namespace SmartBase.Core.Models;

/// <summary>Пересечение диапазона дат с выбранными днями недели и часами каждого дня.
/// Результат — целые секунды в invariant-каноне; отображение отдельно от значения сортировки.</summary>
public static class DatePeriodValue
{
    /// <summary>Готовит расписание один раз на свойство для расчета всех строк пакета.</summary>
    public static Calculator Prepare(DatePeriodConfig config) => new(config);

    public static string Calculate(DateValue source, DatePeriodConfig config) =>
        Prepare(config).Calculate(source);

    /// <summary>Не хранит состояние строки: часы разбираются один раз, недели считаются целиком.
    /// Начало и конец берутся из одного диапазона; у дат без времени это начало каждого дня.</summary>
    public readonly struct Calculator
    {
        private readonly bool _valid;
        private readonly int _weekdays, _daysPerWeek;
        private readonly long _dayStart, _dayEnd;

        internal Calculator(DatePeriodConfig config)
        {
            _valid = config.TryValidate(out var dayStart, out var dayEnd);
            _weekdays = config.Weekdays;
            _daysPerWeek = BitOperations.PopCount((uint)config.Weekdays);
            _dayStart = dayStart * TimeSpan.TicksPerMinute;
            _dayEnd = dayEnd * TimeSpan.TicksPerMinute;
        }

        public string Calculate(DateValue source)
        {
            if (!_valid || source.End < source.Start) return "";
            // Для дат без времени конец исключается целиком: 28–29 октября — одни сутки.
            var from = source.AllDay ? source.Start.Date.Ticks : source.Start.Ticks;
            var to = source.AllDay ? source.End.Date.Ticks : source.End.Ticks;
            if (to < from) return "";
            if (to == from) return "0";
            if (_weekdays == 127 && _dayStart == 0 && _dayEnd == TimeSpan.TicksPerDay)
                return ((to - from) / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture);

            var first = from / TimeSpan.TicksPerDay;
            var last = (to - 1) / TimeSpan.TicksPerDay;
            var ticks = Intersection(first, from, to, _weekdays, _dayStart, _dayEnd);
            if (last != first)
            {
                ticks += Intersection(last, from, to, _weekdays, _dayStart, _dayEnd);
                var count = last - first - 1;
                var selected = count / 7 * _daysPerWeek;
                for (var i = 0; i < count % 7; i++)
                    if ((_weekdays & (1 << (int)((first + 1 + i) % 7))) != 0) selected++;
                ticks += selected * (_dayEnd - _dayStart);
            }
            return (ticks / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture);
        }

        private static long Intersection(long day, long from, long to, int weekdays, long dayStart, long dayEnd)
        {
            // 01.01.0001 — понедельник, поэтому номер дня сразу соответствует битовой маске.
            if ((weekdays & (1 << (int)(day % 7))) == 0) return 0;
            var origin = day * TimeSpan.TicksPerDay;
            return Math.Max(0, Math.Min(to, origin + dayEnd) - Math.Max(from, origin + dayStart));
        }
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
