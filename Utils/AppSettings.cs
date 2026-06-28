using System.Globalization;

namespace DataBase.Utils;

/// <summary>
/// Пути к БД и типизированный доступ к настройкам приложения.
/// Стандарт (дефолты) — это константы ниже в коде. app.db (через <see cref="ISettingsDatabase"/>)
/// хранит ТОЛЬКО отличия от стандарта: нет отличия или хранилища → возвращается дефолт.
/// Поэтому без сохранённых настроек программа работает по стандарту, а сброс настройки =
/// удаление её ключа (Save* удаляет ключ, когда значение равно дефолту).
/// </summary>
public static class AppSettings
{
    public const string AppName    = "DataBase";
    public const string AppVersion = "1.0.0";

    public const double ScaleMin     = 0.7;   // 70 %
    public const double ScaleMax     = 1.3;   // 130 %
    public const double ScaleStep    = 0.1;   // шаг 10 %
    public const double ScaleDefault = 1.0;   // стандарт 100 %

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
    private static ISettingsDatabase? _store;

    /// <summary>Подключить хранилище настроек (app.db). Вызывается на старте после открытия app.db.</summary>
    public static void AttachStore(ISettingsDatabase store) => _store = store;

    // ── Масштаб ───────────────────────────────────────────────────────────────

    public static double LoadScale()
    {
        if (double.TryParse(_store?.Get(KeyScale), NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
            && scale >= ScaleMin && scale <= ScaleMax)
            return scale;
        return ScaleDefault;
    }

    public static void SaveScale(double scale)
    {
        if (scale == ScaleDefault) _store?.Remove(KeyScale);
        else                       _store?.Set(KeyScale, scale.ToString(CultureInfo.InvariantCulture));
    }

    // ── Панель записи (правая) ────────────────────────────────────────────────

    public const bool RecordPanelOpenDefault = false;   // стандарт: панель записи закрыта
    public const int RecordPanelWidthMin     = 320;
    public const int RecordPanelWidthMax     = 800;
    public const int RecordPanelWidthDefault = 420;

    public static bool LoadRecordPanelOpen() =>
        bool.TryParse(_store?.Get(KeyRecordPanelOpen), out var open) ? open : RecordPanelOpenDefault;

    public static void SaveRecordPanelOpen(bool open)
    {
        if (open == RecordPanelOpenDefault) _store?.Remove(KeyRecordPanelOpen);
        else                                _store?.Set(KeyRecordPanelOpen, open ? "true" : "false");
    }

    public static int LoadRecordPanelWidth()
    {
        if (int.TryParse(_store?.Get(KeyRecordPanelWidth), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            && w >= RecordPanelWidthMin && w <= RecordPanelWidthMax)
            return w;
        return RecordPanelWidthDefault;
    }

    public static void SaveRecordPanelWidth(int width)
    {
        if (width == RecordPanelWidthDefault) _store?.Remove(KeyRecordPanelWidth);
        else                                  _store?.Set(KeyRecordPanelWidth, width.ToString(CultureInfo.InvariantCulture));
    }

    // ── Подгрузка строк таблицы ────────────────────────────────────────────────
    //
    // Виртуализация таблицы (постраничная подгрузка через VirtualCollectionView) — всегда
    // включена, это часть программы, переключателя нет (без неё большие коллекции зависают).

    // Фоновая подгрузка строк таблицы с заглушкой «Загрузка…» при прокрутке. По умолчанию ВКЛ:
    // данные страниц грузятся в фоновом потоке (UI не подвисает даже при очень быстром скролле),
    // строки до прихода данных показывают «Загрузка…». Выкл — синхронная загрузка на UI-потоке
    // (тоже быстро, ~мс).
    public const bool AsyncRowLoadDefault = true;   // стандарт: фоновая подгрузка включена

    public static bool LoadAsyncRowLoad()
    {
        var v = _store?.Get(KeyAsyncRowLoad);
        if (v is null) return AsyncRowLoadDefault;
        return !bool.TryParse(v, out var on) || on;
    }

    public static void SaveAsyncRowLoad(bool on)
    {
        if (on == AsyncRowLoadDefault) _store?.Remove(KeyAsyncRowLoad);
        else                           _store?.Set(KeyAsyncRowLoad, on ? "true" : "false");
    }

    // ── Размер шрифта ──────────────────────────────────────────────────────────
    //
    // Дельта в пикселях к стандартным размерам из Typography.axaml (FontSizeSmall/Base/Large).
    // 0 = стандарт; диапазон -3…+3, шаг 1. Применяется ко всем трём размерам сразу.
    public const int FontDeltaMin     = -3;
    public const int FontDeltaMax     = 3;
    public const int FontDeltaDefault = 0;

    public static int LoadFontDelta()
    {
        if (int.TryParse(_store?.Get(KeyFontDelta), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
            && d >= FontDeltaMin && d <= FontDeltaMax)
            return d;
        return FontDeltaDefault;
    }

    public static void SaveFontDelta(int delta)
    {
        if (delta == FontDeltaDefault) _store?.Remove(KeyFontDelta);
        else                           _store?.Set(KeyFontDelta, delta.ToString(CultureInfo.InvariantCulture));
    }

    // ── Стартовый раздел ───────────────────────────────────────────────────────
    //
    // Раздел навигации (ActivityBar), открываемый при запуске. Значения совпадают с
    // ключами модулей: "calendar", "constructor", "extensions". Стандарт — конструктор.
    public const string StartupModuleDefault = "constructor";

    public static string LoadStartupModule()
    {
        var v = _store?.Get(KeyStartupModule);
        return v is "calendar" or "constructor" or "extensions" ? v : StartupModuleDefault;
    }

    public static void SaveStartupModule(string module)
    {
        if (module == StartupModuleDefault) _store?.Remove(KeyStartupModule);
        else                                _store?.Set(KeyStartupModule, module);
    }

    // ── Стартовый вид календаря ──────────────────────────────────────────────────
    //
    // Какой вид открывается при переходе в Календарь. Индексы совпадают с ViewSelector
    // навбара: 0 Расписание, 1 Журнал, 2 День, 3 Неделя, 4 Месяц, 5 Год. Стандарт — Месяц.
    public const int CalendarViewDefault = 4;

    public static int LoadCalendarView()
    {
        if (int.TryParse(_store?.Get(KeyCalendarView), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            && v is >= 0 and <= 5)
            return v;
        return CalendarViewDefault;
    }

    public static void SaveCalendarView(int index)
    {
        if (index == CalendarViewDefault) _store?.Remove(KeyCalendarView);
        else                              _store?.Set(KeyCalendarView, index.ToString(CultureInfo.InvariantCulture));
    }

    // ── Стартовый вид конструктора ───────────────────────────────────────────────
    //
    // Вид коллекции в Конструкторе для коллекций без собственной сохранённой настройки:
    // "table" (таблица) или "list" (список). Стандарт — таблица. Если у коллекции уже
    // сохранён свой режим (переключали вручную), он имеет приоритет над этим значением.
    public const string ConstructorViewDefault = "table";

    public static string LoadConstructorView()
    {
        var v = _store?.Get(KeyConstructorView);
        return v is "table" or "list" ? v : ConstructorViewDefault;
    }

    public static void SaveConstructorView(string mode)
    {
        if (mode == ConstructorViewDefault) _store?.Remove(KeyConstructorView);
        else                                _store?.Set(KeyConstructorView, mode);
    }

    // ── Ключи настроек ─────────────────────────────────────────────────────────

    private const string KeyScale            = "scale";
    private const string KeyRecordPanelOpen  = "recordPanelOpen";
    private const string KeyRecordPanelWidth = "recordPanelWidth";
    private const string KeyAsyncRowLoad     = "asyncRowLoad";
    private const string KeyFontDelta        = "fontDelta";
    private const string KeyStartupModule    = "startupModule";
    private const string KeyCalendarView     = "calendarView";
    private const string KeyConstructorView  = "constructorView";

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
