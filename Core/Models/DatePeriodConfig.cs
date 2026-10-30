using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartBase.Core.Models;

/// <summary>Правило длительности по диапазону одного свойства даты. Дни недели: бит 0 — понедельник,
/// бит 6 — воскресенье; часы — локальное календарное время без перевода в часовой пояс сервера.</summary>
public sealed record DatePeriodConfig(int DateFieldId, bool Automatic = true, int Weekdays = 127,
                                     string DayStart = "00:00", string DayEnd = "24:00")
{
    public bool IsValid => TryValidate(out _, out _);

    internal bool TryValidate(out int start, out int end)
    {
        start = end = 0;
        return DateFieldId > 0 && Weekdays is > 0 and <= 127 && TryGetDayMinutes(out start, out end);
    }

    public bool TryGetDayMinutes(out int start, out int end)
    {
        start = end = 0;
        return TryMinutes(DayStart, false, out start) && TryMinutes(DayEnd, true, out end) && end > start;
    }

    private static bool TryMinutes(string? text, bool allowMidnightEnd, out int minutes)
    {
        minutes = 0;
        if (allowMidnightEnd && text == "24:00") { minutes = 1440; return true; }
        if (!TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return false;
        minutes = time.Hour * 60 + time.Minute;
        return true;
    }

    public static DatePeriodConfig? FromConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("datePeriod", out var node)
                || node.ValueKind != JsonValueKind.Object) return null;
            var version = 1;
            if (node.TryGetProperty("version", out var storedVersion)
                && (storedVersion.ValueKind != JsonValueKind.Number || !storedVersion.TryGetInt32(out version))) return null;
            if (version is not (1 or 2)) return null;

            // В прежней настройке выбранное свойство хранилось как начало. Его диапазон
            // становится единственным источником; второй источник и режим «сейчас» сняты.
            var fieldKey = version == 1 ? "startFieldId" : "dateFieldId";
            if (!node.TryGetProperty(fieldKey, out var field) || field.ValueKind != JsonValueKind.Number
                || !field.TryGetInt32(out var dateFieldId)) return null;
            var automatic = true;
            if (node.TryGetProperty("automatic", out var a))
            {
                if (a.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                automatic = a.GetBoolean();
            }
            var weekdays = 127;
            if (node.TryGetProperty("weekdays", out var w)
                && (w.ValueKind != JsonValueKind.Number || !w.TryGetInt32(out weekdays))) return null;
            var dayStart = "00:00";
            var dayEnd = "24:00";
            if (node.TryGetProperty("dayStart", out var ds))
            {
                if (ds.ValueKind != JsonValueKind.String) return null;
                dayStart = ds.GetString()!;
            }
            if (node.TryGetProperty("dayEnd", out var de))
            {
                if (de.ValueKind != JsonValueKind.String) return null;
                dayEnd = de.GetString()!;
            }
            var config = new DatePeriodConfig(dateFieldId, automatic, weekdays, dayStart, dayEnd);
            return config.IsValid ? config : null;
        }
        catch (JsonException ex)
        {
            ConfigJsonLog.Broken("период времени", json, ex);
            return null;
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Записывает правило диапазона версии 2, сохраняя прочие настройки свойства.</summary>
    public string ToConfigJson(string? originalJson = null)
    {
        JsonObject root;
        try { root = JsonNode.Parse(originalJson ?? "{}") as JsonObject ?? new(); }
        catch (JsonException) { root = new(); }
        root["datePeriod"] = JsonSerializer.SerializeToNode(new
        {
            version = 2, dateFieldId = DateFieldId,
            automatic = Automatic, weekdays = Weekdays, dayStart = DayStart, dayEnd = DayEnd,
        });
        return root.ToJsonString();
    }

    public string MergeInto(string? json) => ToConfigJson(json);
}
