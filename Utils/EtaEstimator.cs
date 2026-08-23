namespace SmartBase.Utils;

/// <summary>
/// Оценка оставшегося времени долгой операции: линейная экстраполяция по доле
/// выполненного, сглаженная экспоненциальным средним. Тот же прием и те же формулировки,
/// что у монтирования баз (<c>DatabaseManager.SqliteStageProgress</c> и порционная
/// миграция): пользователь видит одинаковую фразу везде, где приложение чем-то занято.
///
/// Сглаживание нужно из-за рваной скорости: первые страницы читаются из холодного кеша,
/// дальше быстрее — «сырая» экстраполяция скакала бы с «20 минут» на «2 минуты» и обратно.
/// Класс не знает про часы: время передает вызывающий (<see cref="System.Diagnostics.Stopwatch"/>
/// в бою, фиксированные значения в тестах).
/// </summary>
public sealed class EtaEstimator(double smoothing = 0.3)
{
    /// <summary>Пока сделано меньше — оценка мусорная (старт всегда медленнее).</summary>
    private const double MinFraction = 0.02;

    /// <summary>И пока прошло меньше секунды: делить на околонулевое время бессмысленно.</summary>
    private const double MinSeconds = 1.0;

    private double _ema = -1;

    /// <summary>
    /// Обновить оценку. <paramref name="fraction"/> — доля выполненного (0..1).
    /// Возвращает готовую строку («осталось ≈ 3 мин») или null, если оценивать рано
    /// либо работа уже закончена — в этом случае строку просто не показывают.
    /// </summary>
    public string? Update(double fraction, TimeSpan elapsed)
    {
        if (fraction < MinFraction || fraction >= 1 || elapsed.TotalSeconds < MinSeconds) return null;

        var raw = elapsed.TotalSeconds * (1 - fraction) / fraction;
        _ema = _ema < 0 ? raw : _ema * (1 - smoothing) + raw * smoothing;
        return Format(_ema);
    }

    /// <summary>Секунды → фраза для инфо-строки прогресса.</summary>
    public static string Format(double remainingSeconds) =>
        remainingSeconds < 50
            ? "осталось меньше минуты"
            : $"осталось ≈ {Math.Ceiling(remainingSeconds / 60):0} мин";
}
