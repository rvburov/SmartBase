using System.Globalization;
using System.Text.Json;

namespace DataBase.Utils;

/// <summary>
/// Пути к БД и типизированный доступ к настройкам приложения.
/// Настройки хранятся в app.db через <see cref="IAppSettingsStore"/>; прежний
/// settings.json упразднён — при наличии он один раз переносится в app.db и удаляется.
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
    // Устаревший файл настроек: больше не используется — переносится в app.db и удаляется.
    public static string LegacySettingsPath { get; } = Path.Combine(AppDataDir, "settings.json");

    public static void EnsureCreated() =>
        Directory.CreateDirectory(AppDataDir);

    // ── Хранилище настроек (app.db) ────────────────────────────────────────────

    // Подключается на старте после открытия app.db. До подключения чтения возвращают
    // значения по умолчанию, а записи — no-op (живых обращений к настройкам до старта нет).
    private static IAppSettingsStore? _store;

    /// <summary>Подключить хранилище настроек (app.db) и перенести старый settings.json, если он есть.</summary>
    public static void AttachStore(IAppSettingsStore store)
    {
        _store = store;
        MigrateLegacyJson();
    }

    // ── Масштаб ───────────────────────────────────────────────────────────────

    public static double LoadScale()
    {
        if (double.TryParse(_store?.Get(KeyScale), NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
            && scale >= ScaleMin && scale <= ScaleMax)
            return scale;
        return ScaleDefault;
    }

    public static void SaveScale(double scale) =>
        _store?.Set(KeyScale, scale.ToString(CultureInfo.InvariantCulture));

    // ── Панель записи (правая) ────────────────────────────────────────────────

    public const int RecordPanelWidthMin     = 320;
    public const int RecordPanelWidthMax     = 800;
    public const int RecordPanelWidthDefault = 420;

    public static bool LoadRecordPanelOpen() =>
        bool.TryParse(_store?.Get(KeyRecordPanelOpen), out var open) && open;

    public static void SaveRecordPanelOpen(bool open) =>
        _store?.Set(KeyRecordPanelOpen, open ? "true" : "false");

    public static int LoadRecordPanelWidth()
    {
        if (int.TryParse(_store?.Get(KeyRecordPanelWidth), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            && w >= RecordPanelWidthMin && w <= RecordPanelWidthMax)
            return w;
        return RecordPanelWidthDefault;
    }

    public static void SaveRecordPanelWidth(int width) =>
        _store?.Set(KeyRecordPanelWidth, width.ToString(CultureInfo.InvariantCulture));

    // ── Ключи настроек ─────────────────────────────────────────────────────────

    private const string KeyScale            = "scale";
    private const string KeyRecordPanelOpen  = "recordPanelOpen";
    private const string KeyRecordPanelWidth = "recordPanelWidth";

    // ── Перенос со старого settings.json ───────────────────────────────────────

    // Однократно: если рядом лежит старый settings.json — переносим известные ключи в
    // app.db и удаляем файл. Битый/частичный JSON просто игнорируем (значения вернутся
    // к умолчаниям). Вызывается из AttachStore, т.е. когда _store уже доступно.
    private static void MigrateLegacyJson()
    {
        if (_store is null || !File.Exists(LegacySettingsPath)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(LegacySettingsPath));
            var root = doc.RootElement;

            if (root.TryGetProperty(KeyScale, out var sc) && sc.ValueKind == JsonValueKind.Number)
                _store.Set(KeyScale, sc.GetDouble().ToString(CultureInfo.InvariantCulture));
            if (root.TryGetProperty(KeyRecordPanelOpen, out var po)
                && po.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _store.Set(KeyRecordPanelOpen, po.GetBoolean() ? "true" : "false");
            if (root.TryGetProperty(KeyRecordPanelWidth, out var pw) && pw.ValueKind == JsonValueKind.Number)
                _store.Set(KeyRecordPanelWidth, pw.GetInt32().ToString(CultureInfo.InvariantCulture));

            File.Delete(LegacySettingsPath);
        }
        catch { /* не критично: либо файл уже удалён, либо повторим при следующем старте */ }
    }

    // ── Приватное ─────────────────────────────────────────────────────────────

    private static string ResolveAppDataDir()
    {
        // Release (publish) — стабильно в %AppData%\Roaming\DataBase.
        // Debug — локальная папка .localdata в корне проекта (рядом с DataBase.csproj):
        // изолировано «как venv», переживает dotnet clean и снос artifacts\, не мешается
        // с исходниками. Корень ищем по маркеру, а не счётом «..» — не зависит от
        // раскладки вывода (classic bin\Debug или artifacts\bin\...).
#if DEBUG
        var root = FindProjectRoot() ?? AppContext.BaseDirectory;
        return Path.Combine(root, ".localdata");
#else
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(roaming, AppName);
#endif
    }

#if DEBUG
    // Каталог с DataBase.csproj вверх от каталога сборки (корень проекта).
    private static string? FindProjectRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (dir.GetFiles("DataBase.csproj").Length > 0)
                return dir.FullName;
        return null;
    }
#endif
}
