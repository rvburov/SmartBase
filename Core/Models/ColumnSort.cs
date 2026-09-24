using System.Globalization;

namespace SmartBase.Core.Models;

/// <summary>
/// Порядок сортировки колонки таблицы: какой тип сортируется прямо по индексу значений, а какому
/// нужен типизированный ключ; прежний компаратор значений (текст — по культуре без учета регистра,
/// число — как число, дата — по началу; пустые всегда в конце) — для списков, которые сортируются в
/// памяти (панель фильтра, связи, отобранные записи).
/// </summary>
public static class ColumnSort
{
    /// <summary>
    /// Тип, чей порядок показа совпадает с байтовым порядком индекса <c>(FieldId, Value)</c> — такую
    /// колонку таблица сортирует keyset'ом по индексу без материализации (см. drawn-grid.md, этап 1).
    /// Дата хранится в ISO-каноне, поэтому байтовый порядок для нее хронологический. Число хранится
    /// без выравнивания разрядов («10» раньше «9») — ему нужен типизированный ключ.
    /// </summary>
    public static bool IsIndexSortable(string fieldType) => fieldType is
        "text" or "url" or "select" or "status" or "multiselect" or "checkbox" or "user" or "file" or "date";

    /// <summary>Компаратор значений колонки: пустые всегда в конце независимо от направления.</summary>
    public static Func<string, string, int> BuildComparer(string fieldType, bool asc)
    {
        int Core(string a, string b) => fieldType switch
        {
            "number" => NumberKey(a).CompareTo(NumberKey(b)),
            "date"   => DateKey(a).CompareTo(DateKey(b)),
            _        => string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase),
        };

        return (a, b) =>
        {
            var aEmpty = string.IsNullOrEmpty(a);
            var bEmpty = string.IsNullOrEmpty(b);
            if (aEmpty || bEmpty) return aEmpty == bEmpty ? 0 : (aEmpty ? 1 : -1);
            var r = Core(a, b);
            return asc ? r : -r;
        };
    }

    /// <summary>Числовой ключ значения: запятая как разделитель допускается; пустое и нечисловое —
    /// плюс бесконечность (в конец списка).</summary>
    public static double NumberKey(string s) =>
        s.Length > 0 && double.TryParse(s.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d
            : double.PositiveInfinity;

    /// <summary>Ключ даты: начало значения; нечитаемое — максимум (в конец списка).</summary>
    public static DateTime DateKey(string s) =>
        DateValue.TryParse(s, out var dv) ? dv.Start : DateTime.MaxValue;
}
