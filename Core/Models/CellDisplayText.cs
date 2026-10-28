namespace SmartBase.Core.Models;

/// <summary>
/// Текст ячейки таблицы в том виде, в каком его видит человек, — именно он уходит в буфер обмена.
/// Повторяет правила конвертеров таблицы: дата человекочитаемо, галочка значком, число в формате
/// столбца, мультисписок через свой разделитель, файл — именем. Копировать сырое хранение нельзя:
/// человек ждет в буфере то, что видит на экране.
///
/// Живет в Core, а не в таблице, чтобы круг «скопировал → вставил» проверялся тестом на тех же
/// функциях, что работают в программе: показ — здесь, разбор — <see cref="TablePastePlanner.Convert"/>.
/// Аудит-столбцы и связи сюда не входят: их текст берется не из значения ячейки.
/// </summary>
public static class CellDisplayText
{
    /// <param name="fieldType">Тип свойства столбца.</param>
    /// <param name="raw">Значение в формате хранения.</param>
    /// <param name="numberFormat">Формат показа числа у столбца.</param>
    /// <param name="separator">Разделитель показа мультисписка у столбца.</param>
    public static string Of(string fieldType, string? raw, NumberFormat numberFormat, string separator)
    {
        var value = raw ?? "";
        return fieldType switch
        {
            "checkbox"    => value is "1" or "true" or "True" ? "✓" : "",
            "date"        => DateValue.TryParse(value, out var date) ? date.ToDisplay() : value,
            "multiselect" => MultiSelectValue.Display(value, separator),
            "number"      => NumberFormatValue.Format(value, numberFormat),
            "date_period" => DatePeriodValue.Format(value),
            "file"        => FileValue.DisplayName(value),
            _             => value,
        };
    }
}
