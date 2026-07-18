using System.Globalization;

namespace SmartBase.Utils;

/// <summary>
/// Разбор/формат времени и список вариантов с шагом 15 минут. Общий код для
/// диалога календаря (RecordDialog) и контролов TimeField / DateEditor.
/// </summary>
public static class TimeText
{
    /// <summary>Варианты времени с шагом 15 минут: 00:00, 00:15, … 23:45.</summary>
    public static readonly IReadOnlyList<string> Options = BuildOptions();

    public static string Format(TimeSpan t) => $"{t.Hours:D2}:{t.Minutes:D2}";

    public static bool TryParse(string? text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim().Replace('.', ':').Replace(',', ':').Replace(' ', ':');

        if (!text.Contains(':') && int.TryParse(text, out var digits))
        {
            text = digits switch
            {
                <= 23  => $"{digits}:00",
                >= 100 => $"{digits / 100}:{digits % 100:D2}",
                _      => text,
            };
        }

        var parts = text.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
            return false;
        var m = 0;
        if (parts.Length > 1 &&
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m))
            return false;

        if (h is < 0 or > 23 || m is < 0 or > 59) return false;

        result = new TimeSpan(h, m, 0);
        return true;
    }

    private static List<string> BuildOptions()
    {
        var list = new List<string>(96);
        for (var t = TimeSpan.Zero; t < TimeSpan.FromDays(1); t += TimeSpan.FromMinutes(15))
            list.Add(Format(t));
        return list;
    }
}
