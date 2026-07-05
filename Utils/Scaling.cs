using Avalonia;
using Avalonia.Media;

namespace DataBase.Utils;

/// <summary>
/// Уровень масштабирования всего интерфейса (хранится в app.db).
/// Применяется как LayoutTransform: к корню MainWindow (окно слушает
/// <see cref="ScaleChanged"/>), к остальным окнам (диалоги, расширения) — через
/// <see cref="WindowZoom"/>, к попапам (меню, тултипы, выпадашки) — через ресурс
/// приложения "AppZoomTransform" (DynamicResource у LayoutTransformControl-обёрток
/// контента попапов). Диапазон/шаг/дефолт — в <see cref="AppSettings"/>
/// (70…130 %, шаг 10 %, стандарт 100 %).
/// </summary>
public static class Scaling
{
    /// <summary>Текущий масштаб (1.0 = 100 %).</summary>
    public static double Current { get; private set; } = AppSettings.ScaleDefault;

    /// <summary>Масштаб изменился — корню окна пора применить трансформацию.</summary>
    public static event Action<double>? ScaleChanged;

    /// <summary>Старт: загрузить сохранённый масштаб. Вызывать до создания MainWindow.</summary>
    public static void Load()
    {
        Current = AppSettings.LoadScale();
        PublishTransform();
    }

    public static bool ZoomIn()  => SetScale(Current + AppSettings.ScaleStep);
    public static bool ZoomOut() => SetScale(Current - AppSettings.ScaleStep);

    /// <summary>Установить масштаб (зажим в диапазон), сохранить и применить вживую.</summary>
    public static bool SetScale(double value)
    {
        var clamped = Math.Round(Math.Clamp(value, AppSettings.ScaleMin, AppSettings.ScaleMax), 2);
        if (Math.Abs(clamped - Current) < 0.001) return false;

        Current = clamped;
        AppSettings.SaveScale(clamped);
        PublishTransform();
        ScaleChanged?.Invoke(clamped);
        return true;
    }

    // Кладёт трансформ текущего зума в ресурсы приложения: все попап-обёртки
    // (LayoutTransformControl с DynamicResource "AppZoomTransform") обновляются разом.
    private static void PublishTransform()
    {
        if (Application.Current is { } app)
            app.Resources["AppZoomTransform"] = new ScaleTransform(Current, Current);
    }
}
