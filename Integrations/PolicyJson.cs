using System.Text.Json;

namespace SmartBase.Integrations;

/// <summary>
/// Разбор файла политики организации. Формат — JSON, один и тот же и для файла, и для строки
/// в реестре: два формата означали бы две реализации разбора и два набора ошибок.
///
/// <code>
/// {
///   "version": 1,
///   "mode": "internal",
///   "hosts":    ["*.zavod.local", "api.1c-obmen.local"],
///   "networks": ["10.0.0.0/8"],
///   "folders":  ["P:\\Обмен"],
///   "maxRowsPerRun":  5000,
///   "maxBytesPerDay": 52428800,
///   "maxRunsPerHour": 60,
///   "validUntil": "2026-12-31",
///   "userMayAddHosts": false
/// }
/// </code>
///
/// Главное правило разбора: **что не разобралось, то запрещено.** Битый файл, незнакомый режим,
/// нечитаемая дата — это отказ целиком, а не «возьмем что поняли». Половина политики опаснее ее
/// отсутствия: отсутствие видно на экране «Безопасность», а тихо потерянная строка белого
/// списка — нет.
/// </summary>
public static class PolicyJson
{
    /// <summary>
    /// Версия формата, которую понимает эта сборка. Файл с большим номером — от более новой
    /// программы: разбирать его наугад нельзя, потому что смысл полей мог смениться.
    /// </summary>
    public const int SupportedVersion = 1;

    /// <summary>
    /// Разобрать текст политики. <c>null</c> — файл не принят; причина возвращается в
    /// <paramref name="error"/> и показывается на экране «Безопасность» как есть.
    /// </summary>
    public static EgressPolicy? Parse(string? text, string source, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "файл политики пуст";
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,   // политику пишут руками, комментарии в ней уместны
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            error = $"файл политики не разобрался: {ex.Message}";
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "в файле политики ожидался объект";
                return null;
            }

            if (!TryLong(root, "version", out var version, out error)) return null;
            if (version is > SupportedVersion)
            {
                error = $"версия формата {version} новее поддерживаемой ({SupportedVersion})";
                return null;
            }

            if (!TryMode(root, out var mode, out error)) return null;
            if (!TryDate(root, "validUntil", out var validUntil, out error)) return null;

            if (!TryStrings(root, "hosts",    out var hosts,    out error)) return null;
            if (!TryStrings(root, "networks", out var networks, out error)) return null;
            if (!TryStrings(root, "folders",  out var folders,  out error)) return null;

            if (!TryLong(root, "maxRowsPerRun",  out var rows,  out error)) return null;
            if (!TryLong(root, "maxBytesPerDay", out var bytes, out error)) return null;
            if (!TryLong(root, "maxRunsPerHour", out var runs,  out error)) return null;
            if (!TryBool(root, "userMayAddHosts", out var mayAdd, out error)) return null;

            return new EgressPolicy(
                mode,
                hosts,
                networks,
                folders,
                (int)Math.Clamp(rows ?? 0, 0, int.MaxValue),
                Math.Max(bytes ?? 0, 0),
                (int)Math.Clamp(runs ?? 0, 0, int.MaxValue),
                validUntil,
                mayAdd ?? false,
                source);
        }
    }

    // ── Разбор отдельных полей ────────────────────────────────────────────────
    //
    // Отсутствующее поле — самое строгое значение (пустой список, ноль, false), а вот поле
    // НЕ ТОГО ТИПА — отказ разбора: «hosts»: "api.zavod.local" вместо массива это опечатка,
    // и молча превращать ее в пустой список значит скрыть от человека, что список не работает.

    private static bool TryMode(JsonElement root, out EgressMode mode, out string error)
    {
        mode  = EgressMode.Isolated;
        error = "";

        if (!root.TryGetProperty("mode", out var value)) return true;   // нет режима — изолированный

        if (value.ValueKind != JsonValueKind.String)
        {
            error = "поле «mode» должно быть строкой";
            return false;
        }

        switch (value.GetString()?.Trim().ToLowerInvariant())
        {
            case "isolated": mode = EgressMode.Isolated; return true;
            case "internal": mode = EgressMode.Internal; return true;
            case "external": mode = EgressMode.External; return true;
            default:
                error = $"незнакомый режим «{value.GetString()}»: ожидались isolated, internal или external";
                return false;
        }
    }

    private static bool TryDate(JsonElement root, string name, out DateOnly? date, out string error)
    {
        date  = null;
        error = "";

        if (!root.TryGetProperty(name, out var value)) return true;
        if (value.ValueKind == JsonValueKind.Null) return true;

        if (value.ValueKind != JsonValueKind.String)
        {
            error = $"поле «{name}» должно быть строкой вида ГГГГ-ММ-ДД";
            return false;
        }

        if (!DateOnly.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"дата «{value.GetString()}» в поле «{name}» не разобралась: ожидался вид ГГГГ-ММ-ДД";
            return false;
        }

        date = parsed;
        return true;
    }

    private static bool TryStrings(JsonElement root, string name, out IReadOnlyList<string> items, out string error)
    {
        items = [];
        error = "";

        if (!root.TryGetProperty(name, out var value)) return true;   // нет поля — пустой список

        if (value.ValueKind != JsonValueKind.Array)
        {
            error = $"поле «{name}» должно быть списком строк";
            return false;
        }

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = $"в списке «{name}» встретилось значение не-строка";
                return false;
            }

            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text)) list.Add(text.Trim());
        }

        items = list;
        return true;
    }

    private static bool TryLong(JsonElement root, string name, out long? number, out string error)
    {
        number = null;
        error  = "";

        if (!root.TryGetProperty(name, out var value)) return true;   // нет поля — самое строгое значение

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var parsed))
        {
            error = $"поле «{name}» должно быть целым числом";
            return false;
        }

        number = parsed;
        return true;
    }

    private static bool TryBool(JsonElement root, string name, out bool? flag, out string error)
    {
        flag  = null;
        error = "";

        if (!root.TryGetProperty(name, out var value)) return true;

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = $"поле «{name}» должно быть true или false";
            return false;
        }

        flag = value.GetBoolean();
        return true;
    }
}
