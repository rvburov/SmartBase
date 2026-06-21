using System.Globalization;

namespace DataBase.Utils;

/// <summary>
/// Пути к БД и типизированный доступ к настройкам приложения.
/// Настройки хранятся в app.db через <see cref="IAppSettingsStore"/>.
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

    public static void EnsureCreated() =>
        Directory.CreateDirectory(AppDataDir);

    // ── Хранилище настроек (app.db) ────────────────────────────────────────────

    // Подключается на старте после открытия app.db. До подключения чтения возвращают
    // значения по умолчанию, а записи — no-op (живых обращений к настройкам до старта нет).
    private static IAppSettingsStore? _store;

    /// <summary>Подключить хранилище настроек (app.db). Вызывается на старте после открытия app.db.</summary>
    public static void AttachStore(IAppSettingsStore store) => _store = store;

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

    // ── Подгрузка строк таблицы ────────────────────────────────────────────────
    //
    // Виртуализация таблицы (постраничная подгрузка через VirtualCollectionView) — всегда
    // включена, это часть программы, переключателя нет (без неё большие коллекции зависают).

    // Фоновая подгрузка строк таблицы с заглушкой «Загрузка…» при прокрутке. По умолчанию ВКЛ:
    // данные страниц грузятся в фоновом потоке (UI не подвисает даже при очень быстром скролле),
    // строки до прихода данных показывают «Загрузка…». Выкл — синхронная загрузка на UI-потоке
    // (тоже быстро, ~мс).
    public static bool LoadAsyncRowLoad()
    {
        var v = _store?.Get(KeyAsyncRowLoad);
        if (v is null) return true;                       // по умолчанию включено
        return !bool.TryParse(v, out var on) || on;
    }

    public static void SaveAsyncRowLoad(bool on) =>
        _store?.Set(KeyAsyncRowLoad, on ? "true" : "false");

    // ── Ключи настроек ─────────────────────────────────────────────────────────

    private const string KeyScale            = "scale";
    private const string KeyRecordPanelOpen  = "recordPanelOpen";
    private const string KeyRecordPanelWidth = "recordPanelWidth";
    private const string KeyAsyncRowLoad     = "asyncRowLoad";

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
