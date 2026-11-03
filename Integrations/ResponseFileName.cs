using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace SmartBase.Integrations;

/// <summary>Имя вложения из ответа HTTP, пригодное на всех поддерживаемых ОС.</summary>
public static class ResponseFileName
{
    public static string From(IReadOnlyList<IntegrationHeader> headers)
    {
        var names = headers.Where(h => h.Name.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (names.Length != 1) return "";
        if (!ContentDispositionHeaderValue.TryParse(names[0].Value, out var disposition))
            return BareName(names[0].Value) ?? "";
        if (disposition.Parameters.Count(p => p.Name.Equals("filename", StringComparison.OrdinalIgnoreCase)) > 1
            || disposition.Parameters.Count(p => p.Name.Equals("filename*", StringComparison.OrdinalIgnoreCase)) > 1)
            return "";

        // filename* содержит кодировку и процентное кодирование по RFC 5987. Готовый разбор
        // HTTP знает их и снимает кавычки у обычного filename; вручную URL всего имени не декодируем.
        var name = Usable(disposition.FileNameStar) ?? Usable(disposition.FileName);
        if (name is not null) return name;
        return disposition.Parameters.Count == 0 ? BareName(names[0].Value) ?? "" : "";
    }

    /// <summary>Совместимость с ответом, где весь заголовок — имя, в том числе с \uHHHH.</summary>
    private static string? BareName(string value)
    {
        // Один знак UTF-16 занимает не больше шести знаков escape; еще два — внешние кавычки.
        if (value.Length > ScenarioStep.MaxTextLength * 6 + 2) return null;
        var quoted = value.Length >= 2 && value[0] == '"' && value[^1] == '"';
        var raw = quoted ? value[1..^1] : value;
        if (!quoted && (raw.Contains(',') || DispositionPrefix(raw))) return null;

        var decoded = new StringBuilder(raw.Length);
        var escaped = false;
        for (var index = 0; index < raw.Length; index++)
        {
            if (raw[index] == '\\')
            {
                if (index + 5 >= raw.Length || raw[index + 1] != 'u'
                    || !ushort.TryParse(raw.AsSpan(index + 2, 4), NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out var code))
                    return null;
                decoded.Append((char)code);
                index += 5;
                escaped = true;
            }
            else decoded.Append(raw[index]);
            if (decoded.Length > ScenarioStep.MaxTextLength) return null;
        }

        var name = decoded.ToString();
        if (!escaped)
        {
            // ASCII-имя узнается по расширению: одиночный тип «attachment» именем не станет.
            // Неудачный разбор параметров после «;» также не превращается в имя.
            var dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1 || name.Any(c => c > 127)
                || !quoted && name.Contains(';'))
                return null;
        }

        // Кавычки снимаются до разбора: декодированная кавычка относится уже к самому имени.
        return Usable(name, unquote: false);
    }

    private static bool DispositionPrefix(string value)
    {
        var semicolon = value.IndexOf(';');
        if (semicolon < 0) return false;
        var kind = value[..semicolon].Trim();
        return kind.Equals("attachment", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("inline", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("form-data", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Usable(string? value, bool unquote = true)
    {
        if (value is null) return null;
        var name = unquote && value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
        if (string.IsNullOrWhiteSpace(name) || name.Length > ScenarioStep.MaxTextLength
            || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
            || name.Any(c => char.IsControl(c) || "/\\<>:\"|?*".Contains(c) || c == '\uFFFD'))
            return null;

        // Непарные суррогаты не задают знак Unicode и не должны превращаться в подмену имени.
        for (var index = 0; index < name.Length; index++)
        {
            if (char.IsHighSurrogate(name[index]))
            {
                if (++index >= name.Length || !char.IsLowSurrogate(name[index])) return null;
            }
            else if (char.IsLowSurrogate(name[index])) return null;
        }

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
