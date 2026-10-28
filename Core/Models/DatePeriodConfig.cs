using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartBase.Core.Models;

/// <summary>Правило длительности по датам этой коллекции. Дни недели: бит 0 — понедельник,
/// бит 6 — воскресенье; часы — локальное календарное время без перевода в часовой пояс сервера.</summary>
public sealed record DatePeriodConfig(int StartFieldId, int? EndFieldId = null, bool Automatic = true,
                                     int Weekdays = 127, string DayStart = "00:00", string DayEnd = "24:00")
{
    public bool IsValid => StartFieldId > 0 && (EndFieldId is null or > 0)
        && Weekdays is > 0 and <= 127 && TryGetDayMinutes(out _, out _);

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
                || node.ValueKind != JsonValueKind.Object
                || !node.TryGetProperty("startFieldId", out var s) || !s.TryGetInt32(out var start)) return null;
            if (node.TryGetProperty("version", out var version)
                && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1)) return null;
            int? end = null;
            if (node.TryGetProperty("endFieldId", out var e) && e.ValueKind != JsonValueKind.Null)
            {
                if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out var id)) return null;
                end = id;
            }
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
            var config = new DatePeriodConfig(start, end, automatic, weekdays, dayStart, dayEnd);
            return config.IsValid ? config : null;
        }
        catch (JsonException ex)
        {
            ConfigJsonLog.Broken("период времени", json, ex);
            return null;
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Сохраняет прочие настройки свойства (в том числе метки доступа).</summary>
    public string ToConfigJson(string? originalJson = null)
    {
        JsonObject root;
        try { root = JsonNode.Parse(originalJson ?? "{}") as JsonObject ?? new(); }
        catch (JsonException) { root = new(); }
        root["datePeriod"] = JsonSerializer.SerializeToNode(new
        {
            version = 1, startFieldId = StartFieldId, endFieldId = EndFieldId,
            automatic = Automatic, weekdays = Weekdays, dayStart = DayStart, dayEnd = DayEnd,
        });
        return root.ToJsonString();
    }

    public string MergeInto(string? json) => ToConfigJson(json);
}
