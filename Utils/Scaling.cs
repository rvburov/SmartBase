namespace DataBase.Utils;

/// <summary>
/// Уровень масштабирования всего интерфейса (хранится в app.db).
/// Применяется как LayoutTransform к корню MainWindow — окно слушает <see cref="ScaleChanged"/>
/// и перекомпоновывает содержимое (без перезапуска). Диапазон/шаг/дефолт — в
/// <see cref="AppSettings"/> (70…130 %, шаг 10 %, стандарт 100 %).
/// </summary>
public static class Scaling
{
    /// <summary>Текущий масштаб (1.0 = 100 %).</summary>
    public static double Current { get; private set; } = AppSettings.ScaleDefault;

    /// <summary>Масштаб изменился — корню окна пора применить трансформацию.</summary>
    public static event Action<double>? ScaleChanged;

    /// <summary>Старт: загрузить сохранённый масштаб. Вызывать до создания MainWindow.</summary>
    public static void Load() => Current = AppSettings.LoadScale();

    public static bool ZoomIn()  => SetScale(Current + AppSettings.ScaleStep);
    public static bool ZoomOut() => SetScale(Current - AppSettings.ScaleStep);

    /// <summary>Установить масштаб (зажим в диапазон), сохранить и применить вживую.</summary>
    public static bool SetScale(double value)
    {
        var clamped = Math.Round(Math.Clamp(value, AppSettings.ScaleMin, AppSettings.ScaleMax), 2);
        if (Math.Abs(clamped - Current) < 0.001) return false;

        Current = clamped;
        AppSettings.SaveScale(clamped);
        ScaleChanged?.Invoke(clamped);
        return true;
    }
}
