namespace SmartBase.Logging;

/// <summary>
/// Чистая логика сторожа UI-потока (без таймеров и Avalonia — покрывается модульными
/// тестами): UI-поток ставит отметки (<see cref="Beat"/>), фоновый поток проверяет их
/// (<see cref="Check"/>). Отметка не обновлялась дольше порога — интерфейс висит; когда
/// отметки возобновились, <see cref="Check"/> отдает один отчет о зависании: сколько длилось
/// и какие операции шли в момент обнаружения.
///
/// Отчет отдается ПОСЛЕ оттаивания, а не в момент обнаружения: длительность известна только
/// тогда, а запись в журнал из-под живого зависания все равно никто не прочитает раньше.
/// Стек чужого потока в .NET не снять, поэтому «что выполнялось» берется из реестра
/// операций <see cref="Diag"/> — те же точки, что и пороги медленности.
/// </summary>
public sealed class FreezeDetector(TimeSpan threshold)
{
    /// <summary>Зависание, которое уже закончилось.</summary>
    public readonly record struct Report(TimeSpan Duration, string Operations);

    private TimeSpan  _lastBeat;
    private TimeSpan? _freezeStart;
    private string    _operations = "";

    public TimeSpan Threshold { get; } = threshold;

    /// <summary>Отметка UI-потока: «сейчас я свободен».</summary>
    public void Beat(TimeSpan now) => _lastBeat = now;

    /// <summary>Проверка с фонового потока. <paramref name="activeOperations"/> читается один
    /// раз — в момент обнаружения зависания.</summary>
    public Report? Check(TimeSpan now, Func<string> activeOperations)
    {
        if (_freezeStart is null)
        {
            if (now - _lastBeat <= Threshold) return null;
            _freezeStart = _lastBeat;
            _operations  = activeOperations();
            return null;
        }

        // Зависание идет, пока отметка не сдвинулась. Первая отметка после него — оттаивание.
        if (_lastBeat <= _freezeStart.Value) return null;
        var report   = new Report(_lastBeat - _freezeStart.Value, _operations);
        _freezeStart = null;
        _operations  = "";
        return report;
    }
}
