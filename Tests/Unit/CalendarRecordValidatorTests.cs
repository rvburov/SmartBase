using SmartBase.Core.Models;
using SmartBase.Core.Validators;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Бизнес-правила валидации календарной записи: конец позже начала и минимум 30 минут.
/// </summary>
public sealed class CalendarRecordValidatorTests
{
    private static CalendarRecord Record(DateTime start, DateTime end) =>
        new() { StartDt = start, EndDt = end };

    [Fact]
    public void Valid_record_returns_null()
    {
        var r = Record(new DateTime(2026, 1, 1, 10, 0, 0), new DateTime(2026, 1, 1, 10, 30, 0));
        Assert.Null(CalendarRecordValidator.Validate(r));
    }

    [Fact]
    public void End_before_start_is_rejected()
    {
        var r = Record(new DateTime(2026, 1, 1, 10, 0, 0), new DateTime(2026, 1, 1, 9, 0, 0));
        Assert.Equal("Время конца должно быть позже начала", CalendarRecordValidator.Validate(r));
    }

    [Fact]
    public void Equal_start_and_end_is_rejected()
    {
        var t = new DateTime(2026, 1, 1, 10, 0, 0);
        var r = Record(t, t);
        Assert.Equal("Время конца должно быть позже начала", CalendarRecordValidator.Validate(r));
    }

    [Fact]
    public void Duration_below_30_minutes_is_rejected()
    {
        var r = Record(new DateTime(2026, 1, 1, 10, 0, 0), new DateTime(2026, 1, 1, 10, 29, 0));
        Assert.Equal("Минимальная продолжительность записи — 30 минут", CalendarRecordValidator.Validate(r));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_category_name_is_rejected(string? name)
    {
        Assert.Equal("Введите название категории", CalendarRecordValidator.ValidateCategoryName(name!));
    }

    [Fact]
    public void Non_empty_category_name_is_accepted()
    {
        Assert.Null(CalendarRecordValidator.ValidateCategoryName("Работа"));
    }
}
