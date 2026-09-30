namespace SmartBase.Views.Library;

/// <summary>
/// Повторные попытки загрузить страницу ленты после фоновой ошибки (база занята, сеть
/// моргнула, пропали права).
///
/// Раньше упавшая страница оставалась «Загрузка…» навсегда: она числилась и среди ожидающих,
/// и среди загруженных, а индексатор запрашивает только отсутствующие. Спасало одно вытеснение
/// по очереди, а в библиотеке меньше сорока страниц его не случится никогда.
///
/// Попыток немного и с растущей паузой: занятая база отпускает через секунду, а недоступная не
/// отпустит и через час, и долбить ее каждую секунду незачем. Когда попытки кончились, страница
/// снимается с учета — следующая прокрутка запросит ее заново, как новую.
/// </summary>
public sealed class DocPageRetry
{
    /// <summary>Сколько повторов дается странице после первой неудачи.</summary>
    public const int MaxRetries = 3;

    private readonly Dictionary<int, int> _failures = new();

    /// <summary>
    /// Учесть неудачу страницы: пауза до следующей попытки либо <c>null</c> — попытки
    /// кончились, страницу пора снять с учета.
    /// </summary>
    public TimeSpan? Fail(int page)
    {
        var failures = _failures.GetValueOrDefault(page) + 1;
        if (failures > MaxRetries) { _failures.Remove(page); return null; }
        _failures[page] = failures;
        return Delay(failures);
    }

    /// <summary>Страница загрузилась — ее неудачи забываются.</summary>
    public void Done(int page) => _failures.Remove(page);

    /// <summary>Пауза перед повтором номер <paramref name="retry"/>: 1, 3, 9 секунд.</summary>
    public static TimeSpan Delay(int retry) =>
        TimeSpan.FromSeconds(Math.Pow(3, Math.Max(0, retry - 1)));
}
