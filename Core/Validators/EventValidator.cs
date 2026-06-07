using MyPlanner.Core.Models;

namespace MyPlanner.Core.Validators;

public static class EventValidator
{
    private const int MinDurationSeconds = 1800; // 30 минут

    public static string? Validate(Event e)
    {
        if (string.IsNullOrWhiteSpace(e.Title))
            return "Введите название записи";

        if (e.EndDt <= e.StartDt)
            return "Время конца должно быть позже начала";

        if ((e.EndDt - e.StartDt).TotalSeconds < MinDurationSeconds)
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
