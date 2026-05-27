namespace MyPlanner.Utils;

/// <summary>
/// Аналог scaling.py — управление масштабом через settings.json.
/// В Avalonia масштаб меняется через RenderScaling без перезапуска.
/// </summary>
public static class Scaling
{
    public static double Current { get; private set; } = AppSettings.ScaleDefault;

    public static void Load()
    {
        Current = AppSettings.LoadScale();
    }

    public static bool ZoomIn()  => Apply(Current + AppSettings.ScaleStep);
    public static bool ZoomOut() => Apply(Current - AppSettings.ScaleStep);

    private static bool Apply(double value)
    {
        var clamped = Math.Round(Math.Clamp(value, AppSettings.ScaleMin, AppSettings.ScaleMax), 1);
        if (Math.Abs(clamped - Current) < 0.001) return false;

        Current = clamped;
        AppSettings.SaveScale(clamped);
        ScaleChanged?.Invoke(clamped);
        return true;
    }

    public static event Action<double>? ScaleChanged;
}
