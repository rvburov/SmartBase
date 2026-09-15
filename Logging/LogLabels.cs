namespace SmartBase.Logging;

/// <summary>
/// Человеческие подписи строк диагностики: уровень Serilog и время. Один источник
/// для таблицы «Настройки → Диагностика» и для выгрузки в CSV — в файле должно быть
/// написано «Предупреждение», а не «Warning», как и на экране.
/// </summary>
public static class LogLabels
{
    /// <summary>
    /// Уровни Serilog → подписи. Словарь, а не switch: из него же <c>LogReader</c> собирает
    /// сортировку колонки — «От А до Я» идет по подписи в ячейке, а не по «Error», «Warning».
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Levels = new Dictionary<string, string>
    {
        ["Verbose"]     = "Подробно",
        ["Debug"]       = "Отладка",
        ["Information"] = "Информация",
        ["Warning"]     = "Предупреждение",
        ["Error"]       = "Ошибка",
        ["Fatal"]       = "Критическая",
    };

    /// <summary>Подпись уровня; незнакомый уровень показывается как есть.</summary>
    public static string LevelLabel(string level) =>
        Levels.TryGetValue(level, out var label) ? label : level;

    /// <summary>
    /// Метка времени Serilog (UTC, без суффикса Z) → локальное «дд.MM.гггг ЧЧ:мм:сс».
    /// AssumeUniversal обязателен: иначе строка «2026-08-15 20:39:37» разобралась бы
    /// как местное время и сдвинулась бы на часовой пояс.
    /// </summary>
    public static string FormatTs(string ts) =>
        DateTime.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
                          System.Globalization.DateTimeStyles.AssumeUniversal |
                          System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
            : ts;
}
