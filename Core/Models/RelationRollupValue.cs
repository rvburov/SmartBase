using System.Globalization;

namespace SmartBase.Core.Models;

/// <summary>Итоги по связям. Пустое свойство пропускается; ошибочное значение не превращается
/// в ноль. Decimal сохраняет точность денег и не дает бесконечностей. Самая ранняя и самая поздняя
/// дата возвращают значение цели целиком (с временем и диапазоном) в ISO-каноне.</summary>
public static class RelationRollupValue
{
    public static string Calculate(string aggregate, IEnumerable<string> rawValues)
    {
        var raw = rawValues.ToArray();
        if (aggregate == "count") return raw.Length.ToString(CultureInfo.InvariantCulture);
        if (!RelationComputedConfig.Aggregates.Contains(aggregate, StringComparer.Ordinal))
            return "(неизвестный итог)";
        if (RelationComputedConfig.IsDateAggregate(aggregate)) return CalculateDate(aggregate, raw);
        var numbers = new List<decimal>();
        foreach (var value in raw)
        {
            if (value.Length == 0) continue;
            if (!decimal.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return "(нечисловое значение)";
            numbers.Add(number);
        }
        if (numbers.Count == 0) return aggregate == "sum" ? "0" : "";
        try
        {
            return (aggregate switch
            {
                "sum" => numbers.Sum(), "avg" => numbers.Average(),
                "min" => numbers.Min(), "max" => numbers.Max(), _ => 0m,
            }).ToString("G29", CultureInfo.InvariantCulture);
        }
        catch (OverflowException) { return "(слишком большое значение)"; }
    }

    // Самая ранняя — по началу, самая поздняя — по концу: у диапазона «01.10 - 05.10» поздняя
    // граница 5 октября. Дата аудита (ISO «o») приводится к локальному времени, как в ее показе.
    private static string CalculateDate(string aggregate, IReadOnlyList<string> raw)
    {
        var dates = new List<DateValue>();
        foreach (var value in raw)
        {
            if (value.Length == 0) continue;
            if (DateValue.TryParseStorage(value, out var date)) dates.Add(date);
            else if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment))
            {
                var local = moment.Kind == DateTimeKind.Unspecified ? moment : moment.ToLocalTime();
                local = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0);
                dates.Add(new DateValue(local, local, false));
            }
            else return "(не дата)";
        }
        if (dates.Count == 0) return "";
        var chosen = aggregate == "earliest" ? dates.MinBy(d => d.Start) : dates.MaxBy(d => d.End);
        return chosen.ToStorage();
    }
}
