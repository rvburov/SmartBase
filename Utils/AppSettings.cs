using System.Text.Json;

namespace DataBase.Utils;

/// <summary>
/// Аналог config.py — пути к БД, настройки окна, масштаб.
/// </summary>
public static class AppSettings
{
    public const string AppName    = "DataBase";
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
    // Системная БД (реестр БД + настройки приложения) — независимое хранилище, не показывается.
    public static string SystemDbPath { get; } = Path.Combine(AppDataDir, "app.db");
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

    public static void SaveScale(double scale) => SaveValue("scale", scale);

    // ── Панель записи (правая) ────────────────────────────────────────────────

    public const int RecordPanelWidthMin     = 320;
    public const int RecordPanelWidthMax     = 800;
    public const int RecordPanelWidthDefault = 420;

    public static bool LoadRecordPanelOpen()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return doc.RootElement.TryGetProperty("recordPanelOpen", out var el) && el.GetBoolean();
        }
        catch { return false; }
    }

    public static void SaveRecordPanelOpen(bool open) => SaveValue("recordPanelOpen", open);

    public static int LoadRecordPanelWidth()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return RecordPanelWidthDefault;
            var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (!doc.RootElement.TryGetProperty("recordPanelWidth", out var el)) return RecordPanelWidthDefault;
            var w = el.GetInt32();
            if (w < RecordPanelWidthMin || w > RecordPanelWidthMax) return RecordPanelWidthDefault;
            return w;
        }
        catch { return RecordPanelWidthDefault; }
    }

    public static void SaveRecordPanelWidth(int width) => SaveValue("recordPanelWidth", width);

    // ── Универсальный writer для settings.json ────────────────────────────────

    private static void SaveValue(string key, object value)
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
            data[key] = value;
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ── Приватное ─────────────────────────────────────────────────────────────

    private static string ResolveAppDataDir()
    {
        // В Release (publish) — AppData/Roaming/DataBase
        // В Debug — папка Database/ рядом с проектом
#if DEBUG
        return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Database");
#else
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(roaming, AppName);
#endif
    }
}
