using System.Globalization;

namespace SmartBase.Core.Models;

/// <summary>Числовые итоги по связям. Пустое числовое свойство пропускается; ошибочное значение
/// не превращается в ноль. Decimal сохраняет точность денег и не дает бесконечностей.</summary>
public static class RelationRollupValue
{
    public static string Calculate(string aggregate, IEnumerable<string> rawValues)
    {
        var raw = rawValues.ToArray();
        if (aggregate == "count") return raw.Length.ToString(CultureInfo.InvariantCulture);
        if (!RelationComputedConfig.Aggregates.Contains(aggregate, StringComparer.Ordinal))
            return "(неизвестный итог)";
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
}
