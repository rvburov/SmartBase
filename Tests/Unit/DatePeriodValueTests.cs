using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>Продолжительность по календарным границам и рабочим окнам. Ожидания
/// заданы в секундах: формат вывода не влияет на сортировку и сохраненное значение.</summary>
public sealed class DatePeriodValueTests
{
    [Fact]
    public void Duration_is_a_computed_value_and_not_an_editable_date()
    {
        Assert.True(FieldTypeInfo.IsComputed("date_period"));
        Assert.False(FieldTypeInfo.IsAuditComputed("date_period"));
        Assert.False(FieldTypeInfo.IsDateLike("date_period"));
        Assert.Equal("Дата период времени", FieldTypeInfo.Label("date_period"));
        Assert.Equal("2 ч", FieldTypeInfo.FormatComputedDisplay("7200", "date_period"));
    }

    [Theory]
    [InlineData("2026-01-01T12:30", "2026-01-02T14:00", "00:00", "24:00", 127, "91800")]
    [InlineData("2026-01-01T10:00", "2026-01-01T17:00", "09:00", "18:00", 127, "25200")]
    [InlineData("2026-01-01T07:00", "2026-01-01T20:00", "09:00", "18:00", 127, "32400")]
    [InlineData("2026-01-02T16:00", "2026-01-05T10:00", "09:00", "18:00", 31, "10800")]
    [InlineData("2026-01-02T12:00", "2026-01-05T12:00", "00:00", "24:00", 96, "172800")]
    [InlineData("2026-01-03T12:00", "2026-01-04T15:00", "09:00", "18:00", 31, "0")]
    [InlineData("2026-01-01T23:59", "2026-01-02T00:00", "00:00", "24:00", 127, "60")]
    [InlineData("2026-01-01T00:00", "2026-01-02T00:00", "00:00", "23:59", 127, "86340")]
    [InlineData("2026-01-01T12:00", "2026-01-01T12:00", "00:00", "24:00", 127, "0")]
    [InlineData("2026-01-02T12:00", "2026-01-01T12:00", "00:00", "24:00", 127, "")]
    public void Counts_only_intersections_with_selected_days_and_hours(string start, string end,
        string dayStart, string dayEnd, int weekdays, string seconds)
    {
        var config = new DatePeriodConfig(1, 2, Weekdays: weekdays, DayStart: dayStart, DayEnd: dayEnd);
        Assert.Equal(seconds, DatePeriodValue.Calculate(Parse(start), Parse(end), DateTime.MinValue, config));
    }

    [Fact]
    public void Date_properties_use_the_start_of_each_selected_date_including_leap_day()
    {
        var config = new DatePeriodConfig(1, 2);
        Assert.Equal("86400", DatePeriodValue.Calculate(Parse("2024-02-28"), Parse("2024-02-29"), DateTime.MinValue, config));
        Assert.Equal("172800", DatePeriodValue.Calculate(Parse("2024-02-28"), Parse("2024-03-01"), DateTime.MinValue, config));
        Assert.Equal("0", DatePeriodValue.Calculate(Parse("2026-01-01"), Parse("2026-01-01"), DateTime.MinValue, config));
        // Диапазоны внутри обоих исходных свойств дают их Start, а не End.
        Assert.Equal("259200", DatePeriodValue.Calculate(Parse("2026-01-01/2026-01-03"),
            Parse("2026-01-04/2026-01-05"), DateTime.MinValue, config));
    }

    [Fact]
    public void Current_time_is_injected_and_seconds_are_not_discarded_by_minute_formatting()
    {
        var start = Parse("2026-01-01T12:00");
        var config = new DatePeriodConfig(1);
        Assert.Equal("90", DatePeriodValue.Calculate(start, null, new DateTime(2026, 1, 1, 12, 1, 30), config));
        Assert.Equal("150", DatePeriodValue.Calculate(start, null, new DateTime(2026, 1, 1, 12, 2, 30), config));
        Assert.Equal("1 мин", DatePeriodValue.Format("90"));
    }

    [Fact]
    public void Missing_end_property_does_not_silently_change_the_mode_to_current_time()
    {
        var start = Parse("2026-10-28");
        var config = new DatePeriodConfig(1, 2);
        Assert.Equal("", DatePeriodValue.Calculate(start, null, new DateTime(2026, 10, 29), config));
        Assert.Equal("", DatePeriodValue.Prepare(config).Calculate(start, null, new DateTime(2026, 10, 29)));
        Assert.Equal("86400", DatePeriodValue.Prepare(config).Calculate(start, Parse("2026-10-29"), DateTime.MinValue));
    }

    [Fact]
    public void Current_time_mode_ignores_an_old_end_property_value()
    {
        var start = Parse("2026-10-28");
        var now = new DateTime(2026, 10, 29, 12, 0, 0);
        var calculator = DatePeriodValue.Prepare(new DatePeriodConfig(1));
        Assert.Equal("129600", calculator.Calculate(start, Parse("2026-10-30"), now));
        Assert.Equal("129600", calculator.Calculate(start, Parse("2026-10-27"), now));
        Assert.Equal("129600", calculator.Calculate(start, Parse("2026-10-30/2026-10-27"), now));
        Assert.Equal("129600", calculator.Calculate(start, null, now));
    }

    [Fact]
    public void All_day_values_normalize_both_boundaries_to_the_start_of_the_day()
    {
        var start = new DateValue(new(2026, 10, 28, 12, 0, 0), new(2026, 10, 28, 23, 59, 0), true);
        var end = new DateValue(new(2026, 10, 29, 18, 0, 0), new(2026, 10, 29, 23, 59, 0), true);
        Assert.Equal("86400", DatePeriodValue.Calculate(start, end, DateTime.MinValue, new DatePeriodConfig(1, 2)));
        Assert.Equal("86400", DatePeriodValue.Calculate(Parse("2026-10-28"), Parse("2026-10-29"),
            DateTime.MaxValue, new DatePeriodConfig(1, 2)));
    }

    [Fact]
    public void Prepared_schedule_can_be_reused_without_carrying_the_previous_record_time()
    {
        var calculator = DatePeriodValue.Prepare(new DatePeriodConfig(1, 2, Weekdays: 31,
            DayStart: "09:15", DayEnd: "17:45"));
        Assert.Equal("30600", calculator.Calculate(Parse("2026-10-28"), Parse("2026-10-29"), DateTime.MinValue));
        Assert.Equal("0", calculator.Calculate(Parse("2026-10-31"), Parse("2026-11-02"), DateTime.MinValue));
        Assert.Equal("1800", calculator.Calculate(Parse("2026-10-28T09:00"), Parse("2026-10-28T09:45"), DateTime.MinValue));
        Assert.Equal("", calculator.Calculate(Parse("2026-10-29"), Parse("2026-10-28"), DateTime.MinValue));
        Assert.Equal("30600", calculator.Calculate(Parse("2026-10-28"), Parse("2026-10-29"), DateTime.MinValue));
        Assert.Equal("", default(DatePeriodValue.Calculator).Calculate(Parse("2026-10-28"), Parse("2026-10-29"), DateTime.MinValue));
    }

    [Fact]
    public void Floating_calendar_time_does_not_depend_on_the_machine_time_zone()
    {
        var config = new DatePeriodConfig(1);
        var date = new DateTime(2026, 3, 29, 0, 0, 0);
        var end = date.AddHours(6);
        foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Local, DateTimeKind.Utc })
        {
            var start = new DateValue(DateTime.SpecifyKind(date, kind), DateTime.SpecifyKind(date, kind), false);
            Assert.Equal("21600", DatePeriodValue.Calculate(start, null, DateTime.SpecifyKind(end, kind), config));
        }
    }

    [Fact]
    public void Huge_intervals_keep_integer_seconds_without_overflow_or_date_boundary_errors()
    {
        // Григорианский цикл 400 лет — ровно 20871 неделя, по 5 восьмичасовых рабочих дней.
        var start = new DateValue(new DateTime(2000, 1, 1), new DateTime(2000, 1, 1), false);
        var end = new DateValue(new DateTime(2400, 1, 1), new DateTime(2400, 1, 1), false);
        Assert.Equal("3005424000", DatePeriodValue.Calculate(start, end, DateTime.MinValue,
            new DatePeriodConfig(1, 2, Weekdays: 31, DayStart: "09:00", DayEnd: "17:00")));
        var first = new DateValue(DateTime.MinValue, DateTime.MinValue, false);
        var last = new DateValue(DateTime.MaxValue, DateTime.MaxValue, false);
        var seconds = (DateTime.MaxValue.Ticks - DateTime.MinValue.Ticks) / TimeSpan.TicksPerSecond;
        Assert.Equal(seconds.ToString(CultureInfo.InvariantCulture),
            DatePeriodValue.Calculate(first, last, DateTime.MinValue, new DatePeriodConfig(1, 2)));
    }

    [Fact]
    public void Optimized_week_calculation_agrees_with_a_day_by_day_oracle()
    {
        var random = new Random(714);
        for (var i = 0; i < 250; i++)
        {
            var start = new DateTime(2024, 1, 1).AddDays(random.Next(365)).AddMinutes(random.Next(1440));
            var end = start.AddDays(random.Next(40)).AddSeconds(random.Next(86400));
            var weekdayMask = random.Next(1, 128);
            var hourStart = random.Next(0, 23);
            var hourEnd = random.Next(hourStart + 1, 25);
            var config = new DatePeriodConfig(1, 2, Weekdays: weekdayMask,
                DayStart: $"{hourStart:00}:00", DayEnd: $"{hourEnd:00}:00");
            long expectedTicks = 0;
            for (var day = start.Date; day <= end.Date; day = day.AddDays(1))
            {
                var bit = ((int)day.DayOfWeek + 6) % 7;
                if ((weekdayMask & (1 << bit)) == 0) continue;
                var from = day.AddHours(hourStart);
                var to = day.AddHours(hourEnd);
                if (from < start) from = start;
                if (to > end) to = end;
                if (to > from) expectedTicks += to.Ticks - from.Ticks;
            }
            var expected = (expectedTicks / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture);
            Assert.Equal(expected, DatePeriodValue.Calculate(new(start, start, false), new(end, end, false), DateTime.MinValue, config));
            Assert.Equal(expected, DatePeriodValue.Prepare(config).Calculate(new(start, start, false), new(end, end, false), DateTime.MinValue));
        }
    }

    [Theory]
    [InlineData("00:00", "24:00")]
    [InlineData("09:15", "17:45")]
    [InlineData("23:59", "24:00")]
    public void Every_weekday_mask_counts_only_its_selected_minutes(string from, string to)
    {
        var minuteStart = TimeOnly.ParseExact(from, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan();
        var minuteEnd = to == "24:00" ? TimeSpan.FromDays(1)
            : TimeOnly.ParseExact(to, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan();
        // Семь отдельных односуточных диапазонов задают независимый эталон для всех масок.
        for (var mask = 1; mask <= 127; mask++)
        {
            var calculator = DatePeriodValue.Prepare(new DatePeriodConfig(1, 2, Weekdays: mask, DayStart: from, DayEnd: to));
            for (var weekday = 0; weekday < 7; weekday++)
            {
                var date = new DateTime(2026, 10, 26).AddDays(weekday); // Понедельник.
                var start = new DateValue(date, date.Add(DateValue.EndOfDay), true);
                var end = new DateValue(date.AddDays(1), date.AddDays(1).Add(DateValue.EndOfDay), true);
                var expected = (mask & (1 << weekday)) == 0 ? 0 : (long)(minuteEnd - minuteStart).TotalSeconds;
                Assert.Equal(expected.ToString(CultureInfo.InvariantCulture), calculator.Calculate(start, end, DateTime.MinValue));
            }
        }
    }

    [Theory]
    [InlineData("0", "0 мин")]
    [InlineData("59", "0 мин")]
    [InlineData("60", "1 мин")]
    [InlineData("3600", "1 ч")]
    [InlineData("90060", "1 д 1 ч 1 мин")]
    [InlineData("172800", "2 д")]
    [InlineData("", "")]
    [InlineData("-1", "")]
    [InlineData("не число", "")]
    public void Human_display_does_not_change_the_numeric_storage(string raw, string display) =>
        Assert.Equal(display, DatePeriodValue.Format(raw));

    [Theory]
    [InlineData(0, 127, "00:00", "24:00")]
    [InlineData(1, 0, "00:00", "24:00")]
    [InlineData(1, 128, "00:00", "24:00")]
    [InlineData(1, 127, "22:00", "06:00")]
    [InlineData(1, 127, "24:00", "24:00")]
    [InlineData(1, 127, "09:00", "09:00")]
    [InlineData(1, 127, "9:00", "18:00")]
    [InlineData(1, 127, "00:00", "24:01")]
    public void Invalid_configuration_never_returns_a_plausible_duration(int fieldId, int weekdays, string from, string to)
    {
        var config = new DatePeriodConfig(fieldId, 2, Weekdays: weekdays, DayStart: from, DayEnd: to);
        Assert.False(config.IsValid);
        Assert.Equal("", DatePeriodValue.Calculate(Parse("2026-01-01"), Parse("2026-01-02"), DateTime.MinValue, config));
        Assert.Equal("", DatePeriodValue.Prepare(config).Calculate(Parse("2026-01-01"), Parse("2026-01-02"), DateTime.MinValue));
    }

    [Fact]
    public void Config_round_trips_without_discarding_unrelated_property_settings()
    {
        var expected = new DatePeriodConfig(12, 27, Automatic: false, Weekdays: 31, DayStart: "09:00", DayEnd: "18:00");
        var json = expected.ToConfigJson("{\"color\":\"red\",\"width\":160}");
        Assert.Equal(expected, DatePeriodConfig.FromConfig(json));
        using var document = JsonDocument.Parse(json);
        Assert.Equal("red", document.RootElement.GetProperty("color").GetString());
        Assert.Equal(160, document.RootElement.GetProperty("width").GetInt32());
        var node = JsonNode.Parse(json)!;
        node["datePeriod"]!["version"] = 2;
        Assert.Null(DatePeriodConfig.FromConfig(node.ToJsonString()));
        Assert.Null(DatePeriodConfig.FromConfig("не json"));
        Assert.Null(DatePeriodConfig.FromConfig("{}"));
        Assert.Null(DatePeriodConfig.FromConfig(null));
    }

    private static DateValue Parse(string text) => DateValue.Parse(text) ?? throw new InvalidOperationException(text);
}
