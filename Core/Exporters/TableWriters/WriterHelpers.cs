using System.Globalization;
using SmartBase.Core.Models;

namespace SmartBase.Core.Exporters.TableWriters;

/// <summary>Общие правила типизации значений для писателей (см. таблицу в
/// docs/features/import-export.md). Хранение в db_field_values: числа — сырой ввод
/// с «.» или «,», даты — ISO-канон DateValue, checkbox — «1»/«0» (+легаси «true»).</summary>
public static class WriterHelpers
{
    /// <summary>Число из сырого значения поля «number»: канон — запятая → точка + invariant
    /// (как TableView.ParseNum). Неразобранное экспортируется строкой.</summary>
    public static bool TryNumber(string raw, out double value)
    {
        value = 0;
        return double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && double.IsFinite(value);
    }

    /// <summary>Truthy-значения поля «checkbox» — как FieldDisplayConverter таблицы.</summary>
    public static bool IsChecked(string raw) => raw is "1" or "true" or "True";

    /// <summary>Дата для текстовых форматов (CSV/строковая ячейка Excel):
    /// разобранное — человекочитаемое дд.MM.гггг…, неразобранное — как есть.</summary>
    public static string DateDisplay(string raw) =>
        DateValue.TryParse(raw, out var dv) ? dv.ToDisplay() : raw;

    /// <summary>Управляющие символы &lt;0x20 (кроме \t \n \r) в XML запрещены — OpenXml
    /// молча создает битый файл. Вырезаем; чистые строки возвращаются без копии.</summary>
    public static string SanitizeXml(string s)
    {
        var dirty = false;
        foreach (var ch in s)
            if (ch < ' ' && ch is not ('\t' or '\n' or '\r')) { dirty = true; break; }
        if (!dirty) return s;

        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            if (ch >= ' ' || ch is '\t' or '\n' or '\r') sb.Append(ch);
        return sb.ToString();
    }
}
