using System.Text.Json;

namespace MyPlanner.Utils;

/// <summary>
/// Аналог config.py — пути к БД, настройки окна, масштаб.
/// </summary>
public static class AppSettings
{
    public const string AppName    = "MyPlanner";
    public const string AppVersion = "1.0.0";

    public const double ScaleMin     = 1.0;
    public const double ScaleMax     = 1.3;
    public const double ScaleStep    = 0.1;
    public const double ScaleDefault = 1.0;

    public const int WindowMinWidth  = 1100;
    public const int WindowMinHeight = 760;

    // ── Пути ──────────────────────────────────────────────────────────────────

    public static string AppDataDir { get; } = ResolveAppDataDir();
    public static string DbPath     { get; } = Path.Combine(AppDataDir, "database.db");
    public static string SettingsPath { get; } = Path.Combine(AppDataDir, "settings.json");

    public static void EnsureCreated() =>
        Directory.CreateDirectory(AppDataDir);

    // ── Масштаб ───────────────────────────────────────────────────────────────

    public static double LoadScale()
    {
        try
        {
            if (!File.Exists(SettingsPath)) { SaveScale(ScaleDefault); return ScaleDefault; }
            var json  = File.ReadAllText(SettingsPath);
            var doc   = JsonDocument.Parse(json);
            var scale = doc.RootElement.TryGetProperty("scale", out var el) ? el.GetDouble() : ScaleDefault;
            if (scale < ScaleMin || scale > ScaleMax) { SaveScale(ScaleDefault); return ScaleDefault; }
            return scale;
        }
        catch { SaveScale(ScaleDefault); return ScaleDefault; }
    }

    public static void SaveScale(double scale)
    {
        try
        {
            Dictionary<string, object> data = [];
            if (File.Exists(SettingsPath))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsPath));
                    if (existing is not null) data = existing;
                }
                catch { }
            }
            data["scale"] = scale;
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ── Приватное ─────────────────────────────────────────────────────────────

    private static string ResolveAppDataDir()
    {
        // В Release (publish) — AppData/Roaming/MyPlanner
        // В Debug — папка Database/ рядом с проектом
#if DEBUG
        return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Database");
#else
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(roaming, AppName);
#endif
    }
}
