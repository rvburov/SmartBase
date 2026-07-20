using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Канон даты (<see cref="DateValue"/>) — чистая логика разбора/сериализации, стоящая за
/// каждым полем-датой и календарём. Проверяем ISO-хранилище, отображение дд.MM.yyyy,
/// круговые преобразования и разбор ручного ввода.
/// </summary>
public sealed class DateValueTests
{
    [Fact]
    public void EndOfDay_is_23_59()
    {
        Assert.Equal(new TimeSpan(23, 59, 0), DateValue.EndOfDay);
    }

    // ── ToStorage ────────────────────────────────────────────────────────────

    [Fact]
    public void ToStorage_allday_single_day()
    {
        var v = new DateValue(new DateTime(2026, 1, 1), new DateTime(2026, 1, 1).Add(DateValue.EndOfDay), true);
        Assert.Equal("2026-01-01", v.ToStorage());
    }

    [Fact]
    public void ToStorage_allday_range()
    {
        var v = new DateValue(new DateTime(2026, 1, 1), new DateTime(2026, 1, 3).Add(DateValue.EndOfDay), true);
        Assert.Equal("2026-01-01/2026-01-03", v.ToStorage());
    }

    [Fact]
    public void ToStorage_timed_instant()
    {
        var dt = new DateTime(2026, 1, 3, 14, 0, 0);
        var v = new DateValue(dt, dt, false);
        Assert.Equal("2026-01-03T14:00", v.ToStorage());
    }

    [Fact]
    public void ToStorage_timed_range()
    {
        var v = new DateValue(new DateTime(2026, 1, 3, 14, 0, 0), new DateTime(2026, 1, 3, 15, 0, 0), false);
        Assert.Equal("2026-01-03T14:00/2026-01-03T15:00", v.ToStorage());
    }

    // ── ToDisplay ────────────────────────────────────────────────────────────

    [Fact]
    public void ToDisplay_allday_single_day()
    {
        var v = new DateValue(new DateTime(2026, 7, 1), new DateTime(2026, 7, 1).Add(DateValue.EndOfDay), true);
        Assert.Equal("01.07.2026", v.ToDisplay());
    }

    [Fact]
    public void ToDisplay_timed_range()
    {
        var v = new DateValue(new DateTime(2026, 7, 1, 14, 0, 0), new DateTime(2026, 7, 1, 15, 0, 0), false);
        Assert.Equal("01.07.2026 14:00 - 15:00", v.ToDisplay());
    }

    // ── Круговые преобразования через ISO-хранилище ──────────────────────────

    public static IEnumerable<object[]> RoundTripCases()
    {
        yield return [new DateValue(new DateTime(2026, 1, 1), new DateTime(2026, 1, 1).Add(DateValue.EndOfDay), true)];
        yield return [new DateValue(new DateTime(2026, 1, 1), new DateTime(2026, 1, 3).Add(DateValue.EndOfDay), true)];
        yield return [new DateValue(new DateTime(2026, 1, 3, 14, 0, 0), new DateTime(2026, 1, 3, 14, 0, 0), false)];
        yield return [new DateValue(new DateTime(2026, 1, 3, 14, 0, 0), new DateTime(2026, 1, 3, 15, 0, 0), false)];
        yield return [new DateValue(new DateTime(2020, 12, 31, 23, 0, 0), new DateTime(2021, 1, 1, 1, 0, 0), false)];
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void Storage_round_trip_preserves_value(DateValue original)
    {
        var stored = original.ToStorage();
        Assert.True(DateValue.TryParseStorage(stored, out var parsed));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void TryParseStorage_date_without_time_is_allday_to_end_of_day()
    {
        Assert.True(DateValue.TryParseStorage("2026-01-01", out var v));
        Assert.True(v.AllDay);
        Assert.Equal(new DateTime(2026, 1, 1), v.Start);
        Assert.Equal(new DateTime(2026, 1, 1).Add(DateValue.EndOfDay), v.End);
    }

    // ── Разбор ручного ввода (дд.MM.yyyy + чч:мм) ─────────────────────────────

    [Fact]
    public void TryParseDisplay_single_date_is_allday()
    {
        Assert.True(DateValue.TryParseDisplay("01.01.2026", out var v));
        Assert.True(v.AllDay);
        Assert.Equal(new DateTime(2026, 1, 1), v.Start);
    }

    [Fact]
    public void TryParseDisplay_date_and_time_range()
    {
        Assert.True(DateValue.TryParseDisplay("01.07.2026 14:00 - 15:00", out var v));
        Assert.False(v.AllDay);
        Assert.Equal(new DateTime(2026, 7, 1, 14, 0, 0), v.Start);
        Assert.Equal(new DateTime(2026, 7, 1, 15, 0, 0), v.End);
    }

    [Fact]
    public void TryParseDisplay_date_range_without_time_is_allday()
    {
        Assert.True(DateValue.TryParseDisplay("01.01.2026 - 03.01.2026", out var v));
        Assert.True(v.AllDay);
        Assert.Equal(new DateTime(2026, 1, 1), v.Start);
        Assert.Equal(new DateTime(2026, 1, 3).Add(DateValue.EndOfDay), v.End);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не дата")]
    [InlineData("01.07.2026 25:00")] // час вне диапазона
    public void TryParse_rejects_invalid_input(string? text)
    {
        Assert.False(DateValue.TryParse(text, out _));
    }

    [Fact]
    public void TryParse_prefers_storage_then_display()
    {
        Assert.True(DateValue.TryParse("2026-01-03T14:00", out var iso));
        Assert.False(iso.AllDay);
        Assert.Equal(new DateTime(2026, 1, 3, 14, 0, 0), iso.Start);

        Assert.True(DateValue.TryParse("03.01.2026 14:00", out var human));
        Assert.Equal(iso.Start, human.Start);
    }

    [Fact]
    public void ToRecordColumns_round_trips_via_iso_o_format()
    {
        var v = new DateValue(new DateTime(2026, 1, 3, 14, 0, 0), new DateTime(2026, 1, 3, 15, 0, 0), false);
        var (startIso, endIso) = v.ToRecordColumns();

        Assert.Equal(v.Start, DateTime.Parse(startIso, null, System.Globalization.DateTimeStyles.RoundtripKind));
        Assert.Equal(v.End, DateTime.Parse(endIso, null, System.Globalization.DateTimeStyles.RoundtripKind));
    }
}
