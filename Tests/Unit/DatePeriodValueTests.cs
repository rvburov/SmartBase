using System.Globalization;
using System.Text.Json;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>Продолжительность одного диапазона по выбранным дням и рабочим часам.
/// Числовые ожидания заданы в секундах, отдельно от отображения результата.</summary>
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
    [InlineData("2026-01-01T12:30/2026-01-02T14:00", "00:00", "24:00", 127, "91800")]
    [InlineData("2026-01-01T10:00/2026-01-01T17:00", "09:00", "18:00", 127, "25200")]
    [InlineData("2026-01-01T07:00/2026-01-01T20:00", "09:00", "18:00", 127, "32400")]
    [InlineData("2026-01-02T16:00/2026-01-05T10:00", "09:00", "18:00", 31, "10800")]
    [InlineData("2026-01-02T12:00/2026-01-05T12:00", "00:00", "24:00", 96, "172800")]
    [InlineData("2026-01-03T12:00/2026-01-04T15:00", "09:00", "18:00", 31, "0")]
    [InlineData("2026-01-01T23:59/2026-01-02T00:00", "00:00", "24:00", 127, "60")]
    [InlineData("2026-01-01T00:00/2026-01-02T00:00", "00:00", "23:59", 127, "86340")]
    [InlineData("2026-01-01T12:00", "00:00", "24:00", 127, "0")]
    [InlineData("2026-01-02T12:00/2026-01-01T12:00", "00:00", "24:00", 127, "")]
    public void Counts_only_intersections_of_the_stored_range_with_selected_days_and_hours(
        string range, string dayStart, string dayEnd, int weekdays, string seconds)
    {
        var config = new DatePeriodConfig(1, Weekdays: weekdays, DayStart: dayStart, DayEnd: dayEnd);
        Assert.Equal(seconds, DatePeriodValue.Calculate(Parse(range), config));
        Assert.Equal(seconds, DatePeriodValue.Prepare(config).Calculate(Parse(range)));
    }

    [Theory]
    [InlineData("2026-09-01/2026-09-04", "97200", "1 д 3 ч")]
    [InlineData("2026-09-02/2026-09-03", "32400", "9 ч")]
    [InlineData("2026-09-02/2026-09-04", "64800", "18 ч")]
    [InlineData("2026-09-14/2026-09-28", "453600", "5 д 6 ч")]
    [InlineData("2026-09-21/2026-10-27", "1166400", "13 д 12 ч")]
    [InlineData("2026-03-18/2026-03-24", "194400", "2 д 6 ч")]
    public void Photo_date_ranges_count_both_boundaries_of_one_property(
        string range, string seconds, string display)
    {
        var actual = DatePeriodValue.Calculate(Parse(range),
            new DatePeriodConfig(1, DayStart: "09:00", DayEnd: "18:00"));
        Assert.Equal(seconds, actual);
        Assert.Equal(display, DatePeriodValue.Format(actual));
    }

    [Theory]
    [InlineData("2026-09-01T10:15/2026-09-01T13:45", "12600")]
    [InlineData("2026-09-01T07:00/2026-09-01T20:00", "32400")]
    [InlineData("2026-09-01T07:00/2026-09-01T08:30", "0")]
    [InlineData("2026-09-01T18:00/2026-09-01T20:00", "0")]
    [InlineData("2026-09-01T17:00/2026-09-02T10:00", "7200")]
    [InlineData("2026-09-01T09:00/2026-09-02T18:00", "64800")]
    public void Explicit_times_include_only_the_part_inside_each_daily_window(string range, string seconds) =>
        Assert.Equal(seconds, DatePeriodValue.Calculate(Parse(range),
            new DatePeriodConfig(1, DayStart: "09:00", DayEnd: "18:00")));

    [Theory]
    [InlineData(127, "72000")]
    [InlineData(31, "7200")]
    [InlineData(96, "64800")]
    [InlineData(10, "0")]
    public void Friday_to_monday_uses_exactly_the_selected_weekdays(int weekdays, string seconds) =>
        Assert.Equal(seconds, DatePeriodValue.Calculate(Parse("2026-09-04T17:00/2026-09-07T10:00"),
            new DatePeriodConfig(1, Weekdays: weekdays, DayStart: "09:00", DayEnd: "18:00")));

    [Theory]
    [InlineData("2026-09-01/2026-09-04", "259200")]
    [InlineData("2026-09-01T23:00/2026-09-02T02:00", "10800")]
    [InlineData("2024-02-28/2024-03-01", "172800")]
    [InlineData("2024-02-28/2024-02-29", "86400")]
    [InlineData("2026-10-28/2026-10-29", "86400")]
    public void Whole_days_use_an_exclusive_end_date_and_count_leap_day(string range, string seconds) =>
        Assert.Equal(seconds, DatePeriodValue.Calculate(Parse(range), new DatePeriodConfig(1)));

    [Theory]
    [InlineData("2026-09-04")]
    [InlineData("2026-09-04T09:00")]
    [InlineData("2026-09-04T09:00/2026-09-04T09:00")]
    [InlineData("2026-09-04/2026-09-04")]
    public void One_date_or_coincident_boundaries_have_zero_duration(string source) =>
        Assert.Equal("0", DatePeriodValue.Calculate(Parse(source),
            new DatePeriodConfig(1, DayStart: "09:00", DayEnd: "18:00")));

    [Theory]
    [InlineData("2026-09-04/2026-09-01")]
    [InlineData("2026-09-04T12:00/2026-09-04T09:00")]
    public void Reversed_boundaries_have_no_plausible_value(string source) =>
        Assert.Equal("", DatePeriodValue.Calculate(Parse(source), new DatePeriodConfig(1)));

    [Fact]
    public void All_day_values_normalize_both_boundaries_to_the_start_of_their_days()
    {
        var source = new DateValue(new(2026, 10, 28, 12, 0, 0), new(2026, 10, 29, 23, 59, 0), true);
        Assert.Equal("86400", DatePeriodValue.Calculate(source, new DatePeriodConfig(1)));
        Assert.Equal("0", DatePeriodValue.Calculate(
            new DateValue(new(2026, 10, 28, 12, 0, 0), new(2026, 10, 28, 23, 59, 0), true),
            new DatePeriodConfig(1)));
        // Неверный исходный диапазон не превращается в ноль после нормализации.
        Assert.Equal("", DatePeriodValue.Calculate(
            new DateValue(new(2026, 10, 28, 12, 0, 0), new(2026, 10, 28, 11, 0, 0), true),
            new DatePeriodConfig(1)));
    }

    [Fact]
    public void Only_the_range_controls_duration_for_past_and_future_records()
    {
        var config = new DatePeriodConfig(1);
        Assert.Equal("172800", DatePeriodValue.Calculate(Parse("1900-01-01/1900-01-03"), config));
        Assert.Equal("172800", DatePeriodValue.Calculate(Parse("2500-01-01/2500-01-03"), config));
        Assert.Equal("172800", DatePeriodValue.Calculate(Parse("2500-01-01/2500-01-03"), config with { Automatic = false }));
    }

    [Fact]
    public void Seconds_are_preserved_for_sorting_even_when_the_display_shows_minutes()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0);
        var source = new DateValue(start, start.AddSeconds(90), false);
        Assert.Equal("90", DatePeriodValue.Calculate(source, new DatePeriodConfig(1)));
        Assert.Equal("1 мин", DatePeriodValue.Format("90"));
    }

    [Fact]
    public void Selected_weekdays_do_not_implicitly_exclude_public_holidays() =>
        Assert.Equal("32400", DatePeriodValue.Calculate(Parse("2026-01-01T09:00/2026-01-01T18:00"),
            new DatePeriodConfig(1, Weekdays: 31, DayStart: "09:00", DayEnd: "18:00")));

    [Fact]
    public void Prepared_schedule_can_be_reused_without_carrying_previous_record_boundaries()
    {
        var calculator = DatePeriodValue.Prepare(new DatePeriodConfig(1, Weekdays: 31,
            DayStart: "09:15", DayEnd: "17:45"));
        Assert.Equal("30600", calculator.Calculate(Parse("2026-10-28/2026-10-29")));
        Assert.Equal("0", calculator.Calculate(Parse("2026-10-31/2026-11-02")));
        Assert.Equal("1800", calculator.Calculate(Parse("2026-10-28T09:00/2026-10-28T09:45")));
        Assert.Equal("", calculator.Calculate(Parse("2026-10-29/2026-10-28")));
        Assert.Equal("30600", calculator.Calculate(Parse("2026-10-28/2026-10-29")));
        Assert.Equal("", default(DatePeriodValue.Calculator).Calculate(Parse("2026-10-28/2026-10-29")));
    }

    [Fact]
    public void Floating_calendar_time_does_not_depend_on_the_source_datetime_kind()
    {
        var date = new DateTime(2026, 3, 29, 0, 0, 0);
        foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Local, DateTimeKind.Utc })
        {
            var source = new DateValue(DateTime.SpecifyKind(date, kind), DateTime.SpecifyKind(date.AddHours(6), kind), false);
            Assert.Equal("21600", DatePeriodValue.Calculate(source, new DatePeriodConfig(1)));
        }
    }

    [Fact]
    public void Huge_intervals_keep_integer_seconds_without_overflow_or_date_boundary_errors()
    {
        // Григорианский цикл 400 лет — ровно 20871 неделя, по 5 восьмичасовых рабочих дней.
        var source = new DateValue(new(2000, 1, 1), new(2400, 1, 1), false);
        Assert.Equal("3005424000", DatePeriodValue.Calculate(source,
            new DatePeriodConfig(1, Weekdays: 31, DayStart: "09:00", DayEnd: "17:00")));
        source = new DateValue(DateTime.MinValue, DateTime.MaxValue, false);
        var seconds = (DateTime.MaxValue.Ticks - DateTime.MinValue.Ticks) / TimeSpan.TicksPerSecond;
        Assert.Equal(seconds.ToString(CultureInfo.InvariantCulture),
            DatePeriodValue.Calculate(source, new DatePeriodConfig(1)));
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
            var config = new DatePeriodConfig(1, Weekdays: weekdayMask,
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
            var source = new DateValue(start, end, false);
            Assert.Equal(expected, DatePeriodValue.Calculate(source, config));
            Assert.Equal(expected, DatePeriodValue.Prepare(config).Calculate(source));
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
        for (var mask = 1; mask <= 127; mask++)
        {
            var calculator = DatePeriodValue.Prepare(new DatePeriodConfig(1, Weekdays: mask, DayStart: from, DayEnd: to));
            for (var weekday = 0; weekday < 7; weekday++)
            {
                var day = new DateTime(2026, 10, 26).AddDays(weekday); // Понедельник.
                var source = new DateValue(day, day.AddDays(1).Add(DateValue.EndOfDay), true);
                var expected = (mask & (1 << weekday)) == 0 ? 0 : (long)(minuteEnd - minuteStart).TotalSeconds;
                Assert.Equal(expected.ToString(CultureInfo.InvariantCulture), calculator.Calculate(source));
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
    [InlineData(null, "")]
    public void Human_display_does_not_change_the_numeric_storage(string? raw, string display) =>
        Assert.Equal(display, DatePeriodValue.Format(raw));

    [Theory]
    [InlineData(0, 127, "00:00", "24:00")]
    [InlineData(-1, 127, "00:00", "24:00")]
    [InlineData(1, 0, "00:00", "24:00")]
    [InlineData(1, 128, "00:00", "24:00")]
    [InlineData(1, 127, "22:00", "06:00")]
    [InlineData(1, 127, "24:00", "24:00")]
    [InlineData(1, 127, "09:00", "09:00")]
    [InlineData(1, 127, "9:00", "18:00")]
    [InlineData(1, 127, "00:00", "24:01")]
    public void Invalid_configuration_never_returns_a_plausible_duration(int fieldId, int weekdays, string from, string to)
    {
        var config = new DatePeriodConfig(fieldId, Weekdays: weekdays, DayStart: from, DayEnd: to);
        Assert.False(config.IsValid);
        Assert.Equal("", DatePeriodValue.Calculate(Parse("2026-01-01/2026-01-02"), config));
        Assert.Equal("", DatePeriodValue.Prepare(config).Calculate(Parse("2026-01-01/2026-01-02")));
    }

    [Fact]
    public void Config_round_trips_as_version_two_without_discarding_other_settings()
    {
        var expected = new DatePeriodConfig(12, Automatic: false, Weekdays: 31, DayStart: "09:00", DayEnd: "18:00");
        var json = expected.ToConfigJson("{\"color\":\"red\",\"width\":160,\"custom\":{\"keep\":true}}");
        Assert.Equal(expected, DatePeriodConfig.FromConfig(json));
        using var document = JsonDocument.Parse(json);
        Assert.Equal("red", document.RootElement.GetProperty("color").GetString());
        Assert.Equal(160, document.RootElement.GetProperty("width").GetInt32());
        Assert.True(document.RootElement.GetProperty("custom").GetProperty("keep").GetBoolean());
        var rule = document.RootElement.GetProperty("datePeriod");
        Assert.Equal(2, rule.GetProperty("version").GetInt32());
        Assert.Equal(12, rule.GetProperty("dateFieldId").GetInt32());
        Assert.False(rule.TryGetProperty("startFieldId", out _));
        Assert.False(rule.TryGetProperty("endFieldId", out _));
        Assert.Null(DatePeriodConfig.FromConfig("не json"));
        Assert.Null(DatePeriodConfig.FromConfig("{}"));
        Assert.Null(DatePeriodConfig.FromConfig(null));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("12")]
    [InlineData("27")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("\"устаревшее\"")]
    [InlineData("{\"unused\":true}")]
    public void Legacy_version_one_uses_the_original_date_property_range_and_ignores_the_retired_end_field(string oldEnd)
    {
        var json = "{\"color\":\"red\",\"datePeriod\":{\"version\":1,\"startFieldId\":12,\"endFieldId\":"
            + oldEnd + ",\"automatic\":false,\"weekdays\":31,\"dayStart\":\"09:00\",\"dayEnd\":\"18:00\"}}";
        var config = DatePeriodConfig.FromConfig(json);
        Assert.Equal(new DatePeriodConfig(12, Automatic: false, Weekdays: 31, DayStart: "09:00", DayEnd: "18:00"), config);
        Assert.Equal("97200", DatePeriodValue.Calculate(Parse("2026-09-01/2026-09-04"), config!));
        using var written = JsonDocument.Parse(config!.MergeInto(json));
        Assert.Equal("red", written.RootElement.GetProperty("color").GetString());
        var rule = written.RootElement.GetProperty("datePeriod");
        Assert.Equal(2, rule.GetProperty("version").GetInt32());
        Assert.Equal(12, rule.GetProperty("dateFieldId").GetInt32());
        Assert.False(rule.TryGetProperty("endFieldId", out _));
    }

    [Fact]
    public void Legacy_settings_without_a_version_keep_the_original_date_source_and_schedule()
    {
        var config = DatePeriodConfig.FromConfig("""
            {"datePeriod":{"startFieldId":9,"endFieldId":99,"weekdays":96,"dayStart":"10:00","dayEnd":"12:00"}}
            """);
        Assert.Equal(new DatePeriodConfig(9, Weekdays: 96, DayStart: "10:00", DayEnd: "12:00"), config);
        Assert.Equal("14400", DatePeriodValue.Calculate(Parse("2026-09-04/2026-09-07"), config!));
    }

    [Theory]
    [InlineData("{\"datePeriod\":{\"version\":3,\"dateFieldId\":1}}")]
    [InlineData("{\"datePeriod\":{\"version\":0,\"dateFieldId\":1}}")]
    [InlineData("{\"datePeriod\":{\"version\":\"2\",\"dateFieldId\":1}}")]
    [InlineData("{\"datePeriod\":{\"version\":null,\"dateFieldId\":1}}")]
    [InlineData("{\"datePeriod\":{\"version\":2,\"startFieldId\":1}}")]
    [InlineData("{\"datePeriod\":{\"dateFieldId\":1}}")]
    [InlineData("{\"datePeriod\":{\"version\":2,\"dateFieldId\":0}}")]
    [InlineData("{\"datePeriod\":{\"version\":2,\"dateFieldId\":2147483648}}")]
    [InlineData("{\"datePeriod\":{\"version\":2,\"dateFieldId\":\"1\"}}")]
    [InlineData("{\"datePeriod\":{\"version\":2,\"dateFieldId\":1,\"automatic\":\"true\"}}")]
    [InlineData("{\"datePeriod\":{\"version\":2,\"dateFieldId\":1,\"weekdays\":0}}")]
    [InlineData("{\"datePeriod\":{\"version\":2,\"dateFieldId\":1,\"dayStart\":9}}")]
    [InlineData("{\"datePeriod\":{\"version\":2,\"dateFieldId\":1,\"dayEnd\":null}}")]
    public void Broken_or_unknown_configurations_are_not_guessed(string json) =>
        Assert.Null(DatePeriodConfig.FromConfig(json));

    [Fact]
    public void The_version_defines_which_source_key_is_authoritative()
    {
        Assert.Equal(8, DatePeriodConfig.FromConfig("""
            {"datePeriod":{"version":1,"startFieldId":8,"dateFieldId":9}}
            """)!.DateFieldId);
        Assert.Equal(9, DatePeriodConfig.FromConfig("""
            {"datePeriod":{"version":2,"startFieldId":8,"dateFieldId":9,"endFieldId":8}}
            """)!.DateFieldId);
    }

    private static DateValue Parse(string text) => DateValue.Parse(text) ?? throw new InvalidOperationException(text);
}
