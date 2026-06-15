using DataBase.Core.Models;

namespace DataBase.Core.Validators;

public static class CalendarRecordValidator
{
    private const int MinDurationSeconds = 1800; // 30 минут

    public static string? Validate(CalendarRecord r)
    {
        if (r.EndDt <= r.StartDt)
            return "Время конца должно быть позже начала";

        if ((r.EndDt - r.StartDt).TotalSeconds < MinDurationSeconds)
            return "Минимальная продолжительность записи — 30 минут";

        return null;
    }

    public static string? ValidateCategoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Введите название категории";

        return null;
    }
}
