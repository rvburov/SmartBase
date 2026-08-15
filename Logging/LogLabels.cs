namespace SmartBase.Logging;

/// <summary>
/// Человеческие подписи строк диагностики: уровень Serilog и время. Один источник
/// для таблицы «Настройки → Диагностика» и для выгрузки в CSV — в файле должно быть
/// написано «Предупреждение», а не «Warning», как и на экране.
/// </summary>
public static class LogLabels
{
    public static string LevelLabel(string level) => level switch
    {
        "Verbose"     => "Подробно",
        "Debug"       => "Отладка",
        "Information" => "Информация",
        "Warning"     => "Предупреждение",
        "Error"       => "Ошибка",
        "Fatal"       => "Критическая",
        _             => level,
    };

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
