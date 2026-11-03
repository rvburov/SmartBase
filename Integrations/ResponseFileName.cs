using System.Net.Http.Headers;

namespace SmartBase.Integrations;

/// <summary>Имя вложения из стандартного ответа HTTP, пригодное на всех поддерживаемых ОС.</summary>
public static class ResponseFileName
{
    public static string From(IReadOnlyList<IntegrationHeader> headers)
    {
        var names = headers.Where(h => h.Name.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (names.Length != 1 || !ContentDispositionHeaderValue.TryParse(names[0].Value, out var disposition))
            return "";
        if (disposition.Parameters.Count(p => p.Name.Equals("filename", StringComparison.OrdinalIgnoreCase)) > 1
            || disposition.Parameters.Count(p => p.Name.Equals("filename*", StringComparison.OrdinalIgnoreCase)) > 1)
            return "";

        // filename* содержит кодировку и процентное кодирование по RFC 5987. Готовый разбор
        // HTTP знает их и снимает кавычки у обычного filename; вручную URL всего имени не декодируем.
        return Usable(disposition.FileNameStar) is { } encoded ? encoded
            : Usable(disposition.FileName) ?? "";
    }

    private static string? Usable(string? value)
    {
        if (value is null) return null;
        var name = value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
        if (string.IsNullOrWhiteSpace(name) || name.Length > ScenarioStep.MaxTextLength
            || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
            || name.Any(c => char.IsControl(c) || "/\\<>:\"|?*".Contains(c) || c == '\uFFFD'))
            return null;

        // Имена устройств Windows запрещены также с расширением; проверка одинакова на Unix.
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Length == 4 && stem[3] is >= '1' and <= '9'
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
            return null;
        return name;
    }
}
