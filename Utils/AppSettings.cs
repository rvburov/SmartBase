using System.Globalization;
using System.Reflection;

namespace SmartBase.Utils;

/// <summary>
/// Пути к БД и типизированный доступ к настройкам приложения.
/// Стандарт (дефолты) — это константы ниже в коде. app.db (через <see cref="ISettingsDatabase"/>)
/// хранит ТОЛЬКО отличия от стандарта: нет отличия или хранилища → возвращается дефолт.
/// Поэтому без сохраненных настроек программа работает по стандарту, а сброс настройки =
/// удаление ее ключа (Save* удаляет ключ, когда значение равно дефолту).
/// </summary>
public static class AppSettings
{
    // Технический идентификатор: имя папки данных (%AppData%\…\SmartBase), сборки, бандла.
    // НЕ МЕНЯТЬ — иначе программа станет искать данные в другой папке и «потеряет» базы.
    public const string AppName     = "SmartBase";
    // Отображаемое имя (бренд) — то, что видит пользователь: заголовки окон, сплэш, «О программе».
    public const string DisplayName = "SmartBase";
    // Версия — из самой сборки, а НЕ константой: скрипты сборки правят <Version> в
    // SmartBase.csproj, и константа неизбежно отставала (в логах годами значилось 1.0.0
    // при 1.0.3 в проекте). Теперь одно место правды — csproj.
    public static string AppVersion { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        // InformationalVersion — это <Version> дословно; SDK может дописать «+<хеш коммита>»,
        // в логах и «О программе» он лишний.
        var info = typeof(AppSettings).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        // Атрибута нет (сборка без версии) — берем числовую, без служебного 4-го компонента.
        return typeof(AppSettings).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

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
    // Диагностический лог (Serilog → SQLite): единый файл на приложение, не per-database.
    public static string LogsDbPath { get; } = Path.Combine(AppDataDir, "logs.db");
    // Журнал запусков обмена: тоже единый на приложение и намеренно НЕ внутри файла базы —
    // файл базы ездит по сетевой папке, и числа обмена раздули бы его на всех машинах разом
    // (docs/ROADMAP-API.md, 10.10).
    public static string RunJournalPath { get; } = Path.Combine(AppDataDir, "integrations.db");

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

    // ── Панель «Содержание» инструкции пользователя ───────────────────────────
    //
    // Панель открыта все время, пока выбран раздел (у «Деталей» журнала ширина живет
    // только в пределах сессии — там панель появляется по клику), поэтому ширина
    // сохраняется: подобранное один раз не приходится подбирать заново.

    public const int GuideTocWidthMin     = 200;
    public const int GuideTocWidthMax     = 600;
    public const int GuideTocWidthDefault = 280;

    public static int LoadGuideTocWidth()
    {
        if (int.TryParse(_store?.Get(KeyGuideTocWidth), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            && w >= GuideTocWidthMin && w <= GuideTocWidthMax)
            return w;
        return GuideTocWidthDefault;
    }

    public static void SaveGuideTocWidth(int width)
    {
        if (width == GuideTocWidthDefault) _store?.Remove(KeyGuideTocWidth);
        else                               _store?.Set(KeyGuideTocWidth, width.ToString(CultureInfo.InvariantCulture));
    }

    // Виртуализация таблицы (постраничная подгрузка через VirtualCollectionView) и фоновая
    // подгрузка строк с заглушкой «Загрузка…» — всегда включены, это часть программы,
    // переключателей нет (без них большие коллекции зависают). Бывший ключ asyncRowLoad
    // удален: оставшиеся в app.db значения игнорируются.

    // ── Размер шрифта ──────────────────────────────────────────────────────────
    //
    // Дельта в пикселях к стандартным размерам из Typography.axaml (FontSizeSmall/Base/Large).
    // 0 = стандарт; диапазон -3…+3, шаг 1. Применяется ко всем трем размерам сразу.
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
    // ключами модулей: "calendar", "tasks", "constructor", "extensions", "notes". Стандарт —
    // конструктор. Список закрытый: незнакомое значение (правка app.db руками, откат на
    // версию без этого модуля) молча заменяется стандартом, а не роняет старт. Добавили
    // модуль в ActivityBar — дописать его и сюда, иначе выбор в настройках не сохранится.
    public const string StartupModuleDefault = "constructor";

    public static string LoadStartupModule()
    {
        var v = _store?.Get(KeyStartupModule);
        return v is "calendar" or "tasks" or "constructor" or "extensions" or "notes"
            ? v : StartupModuleDefault;
    }

    public static void SaveStartupModule(string module)
    {
        if (module == StartupModuleDefault) _store?.Remove(KeyStartupModule);
        else                                _store?.Set(KeyStartupModule, module);
    }

    // ── Стартовый вид задач ──────────────────────────────────────────────────────
    //
    // Какой вид открывается в разделе «Задачи». Пока вид один — "list" (Список), поэтому
    // список значений закрытый ровно на нем; дальше сюда добавятся остальные, как это
    // сделано у календаря и конструктора. Незнакомое значение (правка app.db руками, откат
    // на версию с другим набором видов) молча заменяется стандартом.
    public const string TasksViewDefault = "list";

    public static string LoadTasksView()
    {
        var v = _store?.Get(KeyTasksView);
        return v is "list" ? v : TasksViewDefault;
    }

    public static void SaveTasksView(string mode)
    {
        if (mode == TasksViewDefault) _store?.Remove(KeyTasksView);
        else                          _store?.Set(KeyTasksView, mode);
    }

    // ── Стартовый вид календаря ──────────────────────────────────────────────────
    //
    // Какой вид открывается при переходе в Календарь. Индексы совпадают с ViewSelector
    // навбара: 0 Расписание, 1 Журнал, 2 День, 3 Неделя, 4 Месяц, 5 Год, 6 Диаграмма.
    // Стандарт — Месяц.
    public const int CalendarViewDefault = 4;

    public static int LoadCalendarView()
    {
        if (int.TryParse(_store?.Get(KeyCalendarView), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            && v is >= 0 and <= 6)
            return v;
        return CalendarViewDefault;
    }

    public static void SaveCalendarView(int index)
    {
        if (index == CalendarViewDefault) _store?.Remove(KeyCalendarView);
        else                              _store?.Set(KeyCalendarView, index.ToString(CultureInfo.InvariantCulture));
    }

    // ── Настройки календаря ──────────────────────────────────────────────────────
    //
    // Раздел настроек «Календарь». Живой доступ с кэшем и событием изменения —
    // CalendarSettings (по образцу FontScale); здесь только хранение дельт.
    // Дней в неделе: 7 (Пн-Вс) или 5 (Пн-Пт) — влияет на виды «День» и «Неделя»
    // и на перетягивание/растягивание событий через выходные.
    public const int CalendarDaysInWeekDefault = 7;

    public static int LoadCalendarDaysInWeek()
    {
        if (int.TryParse(_store?.Get(KeyCalendarDaysInWeek), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            && v is 5 or 7)
            return v;
        return CalendarDaysInWeekDefault;
    }

    public static void SaveCalendarDaysInWeek(int days)
    {
        if (days == CalendarDaysInWeekDefault) _store?.Remove(KeyCalendarDaysInWeek);
        else _store?.Set(KeyCalendarDaysInWeek, days.ToString(CultureInfo.InvariantCulture));
    }

    // Рабочие часы дня в минутах от полуночи. Стандарт 0/1440 — «без ограничений»:
    // перетягивание, растягивание и создание событий работают как раньше.
    // Кросс-инвариант start + 60 <= end проверяет CalendarSettings, не Load*.
    public const int CalendarDayStartDefault = 0;
    public const int CalendarDayEndDefault   = 1440;

    public static int LoadCalendarDayStart()
    {
        if (int.TryParse(_store?.Get(KeyCalendarDayStart), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            && v is >= 0 and <= 1425)
            return v;
        return CalendarDayStartDefault;
    }

    public static void SaveCalendarDayStart(int minutes)
    {
        if (minutes == CalendarDayStartDefault) _store?.Remove(KeyCalendarDayStart);
        else _store?.Set(KeyCalendarDayStart, minutes.ToString(CultureInfo.InvariantCulture));
    }

    public static int LoadCalendarDayEnd()
    {
        if (int.TryParse(_store?.Get(KeyCalendarDayEnd), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            && v is >= 15 and <= 1440)
            return v;
        return CalendarDayEndDefault;
    }

    public static void SaveCalendarDayEnd(int minutes)
    {
        if (minutes == CalendarDayEndDefault) _store?.Remove(KeyCalendarDayEnd);
        else _store?.Set(KeyCalendarDayEnd, minutes.ToString(CultureInfo.InvariantCulture));
    }

    // Прокрутка вида «Неделя» кнопками Назад/Вперед: по неделе (стандарт), по дню,
    // по неделе с остановкой на «Сегодня» (сегодня посередине окна).
    public const string CalendarWeekScrollDefault = "week";

    public static string LoadCalendarWeekScroll()
    {
        var v = _store?.Get(KeyCalendarWeekScroll);
        return v is "week" or "day" or "weekStopToday" ? v : CalendarWeekScrollDefault;
    }

    public static void SaveCalendarWeekScroll(string mode)
    {
        if (mode == CalendarWeekScrollDefault) _store?.Remove(KeyCalendarWeekScroll);
        else _store?.Set(KeyCalendarWeekScroll, mode);
    }

    // Длинные события в часовой сетке. Стандарт (false) — событие длиннее 24 часов
    // видимой длительности уходит в полосу «весь день», как всегда работало. Включено —
    // событие любой длины остается в сетке блоками по дням (drag/resize растягивают
    // через несколько дней), в полосу уходят только записи «весь день».
    public const bool CalendarMultiDayGridDefault = false;

    public static bool LoadCalendarMultiDayGrid() =>
        bool.TryParse(_store?.Get(KeyCalendarMultiDayGrid), out var on) ? on : CalendarMultiDayGridDefault;

    public static void SaveCalendarMultiDayGrid(bool on)
    {
        if (on == CalendarMultiDayGridDefault) _store?.Remove(KeyCalendarMultiDayGrid);
        else _store?.Set(KeyCalendarMultiDayGrid, on.ToString());
    }

    // Длительность нового события при создании из сетки календаря. Стандарт — час.
    public const int CalendarNewEventDurationDefault = 60;

    public static int LoadCalendarNewEventDuration()
    {
        if (int.TryParse(_store?.Get(KeyCalendarNewEventDuration), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            && v is 30 or 60 or 90 or 120)
            return v;
        return CalendarNewEventDurationDefault;
    }

    public static void SaveCalendarNewEventDuration(int minutes)
    {
        if (minutes == CalendarNewEventDurationDefault) _store?.Remove(KeyCalendarNewEventDuration);
        else _store?.Set(KeyCalendarNewEventDuration, minutes.ToString(CultureInfo.InvariantCulture));
    }

    // ── Масштаб Диаграммы ────────────────────────────────────────────────────────
    //
    // Шкала таймлайна вида «Диаграмма»: месяц, год, 5 или 10 лет.
    // Переключается кнопками «−»/«+» в шапке самой Диаграммы. Стандарт — месяц.
    public const string DiagramScaleDefault = "month";

    public static string LoadDiagramScale()
    {
        var v = _store?.Get(KeyDiagramScale);
        return v is "month" or "year" or "5years" or "10years" ? v : DiagramScaleDefault;
    }

    public static void SaveDiagramScale(string scale)
    {
        if (scale == DiagramScaleDefault) _store?.Remove(KeyDiagramScale);
        else                              _store?.Set(KeyDiagramScale, scale);
    }

    // ── Стартовый вид конструктора ───────────────────────────────────────────────
    //
    // Вид коллекции в Конструкторе для коллекций без собственной сохраненной настройки:
    // "table" (таблица) или "list" (список). Стандарт — таблица. Если у коллекции уже
    // сохранен свой режим (переключали вручную), он имеет приоритет над этим значением.
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

    // ── Путь обновления ──────────────────────────────────────────────────────────
    //
    // Папка (сетевая/файловая), где лежит свежий SmartBase.exe для обновления. Пусто по
    // умолчанию — пользователь задает сам в «О программе → Обновления»; значение хранится
    // в app.db. Логика проверки/замены — см. docs/guides/auto-update.md.
    public const string UpdatePathDefault = "";

    public static string LoadUpdatePath() => _store?.Get(KeyUpdatePath) ?? UpdatePathDefault;

    public static void SaveUpdatePath(string path)
    {
        var trimmed = (path ?? "").Trim();
        if (trimmed.Length == 0) _store?.Remove(KeyUpdatePath);
        else                     _store?.Set(KeyUpdatePath, trimmed);
    }

    // ── Заметки ──────────────────────────────────────────────────────────────────
    //
    // Папка-хранилище раздела «Заметки»: читается рекурсивно и показывается деревом. Пусто по
    // умолчанию — до выбора папки раздел показывает подсказку и в диск не лезет вовсе.
    // Остальные ключи раздела осмысленны только при заданном хранилище.
    public const string NotesVaultDefault = "";

    public static string LoadNotesVault() => _store?.Get(KeyNotesVault) ?? NotesVaultDefault;

    public static void SaveNotesVault(string path)
    {
        var trimmed = (path ?? "").Trim();
        // Смена хранилища обесценивает «последнюю открытую заметку»: путь относительный, в
        // новой папке он указал бы на другой файл или в пустоту.
        if (trimmed != LoadNotesVault()) _store?.Remove(KeyNotesLastFile);

        if (trimmed.Length == 0) _store?.Remove(KeyNotesVault);
        else                     _store?.Set(KeyNotesVault, trimmed);
    }

    // Последняя открытая заметка — путь ОТНОСИТЕЛЬНО хранилища, разделитель '/'. Абсолютный
    // сломался бы при переносе хранилища на другой диск или другую машину.
    public const string NotesLastFileDefault = "";

    public static string LoadNotesLastFile() => _store?.Get(KeyNotesLastFile) ?? NotesLastFileDefault;

    public static void SaveNotesLastFile(string relativePath)
    {
        var trimmed = (relativePath ?? "").Trim();
        if (trimmed.Length == 0) _store?.Remove(KeyNotesLastFile);
        else                     _store?.Set(KeyNotesLastFile, trimmed);
    }

    // Режим тела раздела: "source" — просмотр исходного кода, "split" — динамический просмотр
    // (слева правка, справа живой вид), "preview" — просмотр. Стандарт — динамический: ближе
    // всего к тому, как markdown показывают редакторы заметок.
    public const string NotesViewModeDefault = "split";

    public static string LoadNotesViewMode()
    {
        var v = _store?.Get(KeyNotesViewMode);
        return v is "source" or "split" or "preview" ? v : NotesViewModeDefault;
    }

    public static void SaveNotesViewMode(string mode)
    {
        if (mode == NotesViewModeDefault) _store?.Remove(KeyNotesViewMode);
        else                              _store?.Set(KeyNotesViewMode, mode);
    }

    // Порядок дерева заметок. У файлов нет собственной позиции (как Position у коллекций), а
    // порядок файловой системы произволен, — поэтому положений два, а не три, как в
    // конструкторе: "asc" (А → Я, стандарт) и "desc".
    public const string NotesSortDefault = "asc";

    public static string LoadNotesSort()
    {
        var v = _store?.Get(KeyNotesSort);
        return v is "asc" or "desc" ? v : NotesSortDefault;
    }

    public static void SaveNotesSort(string sort)
    {
        if (sort == NotesSortDefault) _store?.Remove(KeyNotesSort);
        else                          _store?.Set(KeyNotesSort, sort);
    }

    // ── Автообновление ───────────────────────────────────────────────────────────
    //
    // Если включено — при запуске приложение тихо проверяет папку обновления и при
    // наличии новой версии показывает ту же модалку «Обновить/Отмена», что и кнопка
    // «Проверить» в «О программе → Обновления».
    public const bool AutoUpdateDefault = false;

    public static bool LoadAutoUpdate() =>
        bool.TryParse(_store?.Get(KeyAutoUpdate), out var on) ? on : AutoUpdateDefault;

    public static void SaveAutoUpdate(bool on)
    {
        if (on == AutoUpdateDefault) _store?.Remove(KeyAutoUpdate);
        else                         _store?.Set(KeyAutoUpdate, on.ToString());
    }

    // ── Уведомления ──────────────────────────────────────────────────────────────
    //
    // Тумблер «Общее → Уведомления»: показывать нативные баннеры ОС о наступивших
    // событиях календаря (в момент StartDt, пока приложение открыто; пропущенные —
    // при следующем запуске) и значок-счетчик на иконке приложения. Стандарт — включено.
    public const bool NotificationsEnabledDefault = true;

    public static bool LoadNotificationsEnabled() =>
        bool.TryParse(_store?.Get(KeyNotificationsEnabled), out var on) ? on : NotificationsEnabledDefault;

    public static void SaveNotificationsEnabled(bool on)
    {
        if (on == NotificationsEnabledDefault) _store?.Remove(KeyNotificationsEnabled);
        else                                   _store?.Set(KeyNotificationsEnabled, on.ToString());
    }

    // Водяная метка службы уведомлений: инстант, до которого события уже обработаны
    // (ISO-8601, круговой формат "o"). Это СОСТОЯНИЕ, а не пользовательская настройка,
    // но живет в том же key-value app.db. null → служба еще не запускалась (первый старт),
    // тогда метку инициализируем текущим временем, чтобы не показать историю разом.
    public static DateTime? LoadNotifyWatermark() =>
        DateTime.TryParse(_store?.Get(KeyNotifyWatermark), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var dt) ? dt : null;

    public static void SaveNotifyWatermark(DateTime instant) =>
        _store?.Set(KeyNotifyWatermark, instant.ToString("o", CultureInfo.InvariantCulture));

    // ── Уведомления по задачам ────────────────────────────────────────────────────
    //
    // Два независимых тумблера «Общее», оба независимы и от календарного:
    //   • назначение — баннер новому исполнителю, когда его поставил КТО-ТО ДРУГОЙ;
    //   • смена стадии — баннер тому, кто указан в свойстве «Назначил».
    // Стандарт — ВЫКЛЮЧЕНО: уведомления по задачам осмысленны только в общей базе с
    // несколькими людьми, поэтому включает их тот, кому они нужны, а не обновление
    // приложения. Календарный тумблер по той же логике включен: он полезен и одному.
    public const bool NotifyTaskAssignedDefault = false;
    public const bool NotifyTaskStatusDefault   = false;

    public static bool LoadNotifyTaskAssigned() =>
        bool.TryParse(_store?.Get(KeyNotifyTaskAssigned), out var on) ? on : NotifyTaskAssignedDefault;

    public static void SaveNotifyTaskAssigned(bool on)
    {
        if (on == NotifyTaskAssignedDefault) _store?.Remove(KeyNotifyTaskAssigned);
        else                                 _store?.Set(KeyNotifyTaskAssigned, on.ToString());
    }

    public static bool LoadNotifyTaskStatus() =>
        bool.TryParse(_store?.Get(KeyNotifyTaskStatus), out var on) ? on : NotifyTaskStatusDefault;

    public static void SaveNotifyTaskStatus(bool on)
    {
        if (on == NotifyTaskStatusDefault) _store?.Remove(KeyNotifyTaskStatus);
        else                               _store?.Set(KeyNotifyTaskStatus, on.ToString());
    }

    // ── Раздел «Настройки → Задачи» ──────────────────────────────────────────────
    //
    // Хранятся дельтами (стандарт = отсутствие ключа); живой кэш, разбор мусора и событие —
    // TaskSettings. Строковые значения (стартовый фильтр, срок, стадия) здесь отдаются как
    // есть: что из них допустимо, решает сервис.
    public const bool   TasksOverdueNextDayDefault  = false;   // просрочка с момента срока
    public const bool   TasksShowDoneDefault        = true;
    public const string TasksStartFilterDefault     = "All";
    public const bool   TasksQuickExecutorMeDefault = true;
    public const string TasksQuickDueDefault        = "none";
    public static readonly string TasksDefaultStageDefault =
        SmartBase.Database.Repositories.ConstructorRepository.TaskStatusPlanned;

    public static bool LoadTasksOverdueNextDay() =>
        bool.TryParse(_store?.Get(KeyTasksOverdueNextDay), out var on) ? on : TasksOverdueNextDayDefault;

    public static void SaveTasksOverdueNextDay(bool on)
    {
        if (on == TasksOverdueNextDayDefault) _store?.Remove(KeyTasksOverdueNextDay);
        else                                  _store?.Set(KeyTasksOverdueNextDay, on.ToString());
    }

    public static bool LoadTasksShowDone() =>
        bool.TryParse(_store?.Get(KeyTasksShowDone), out var on) ? on : TasksShowDoneDefault;

    public static void SaveTasksShowDone(bool on)
    {
        if (on == TasksShowDoneDefault) _store?.Remove(KeyTasksShowDone);
        else                            _store?.Set(KeyTasksShowDone, on.ToString());
    }

    public static string LoadTasksStartFilter() => _store?.Get(KeyTasksStartFilter) ?? TasksStartFilterDefault;

    public static void SaveTasksStartFilter(string key)
    {
        if (key == TasksStartFilterDefault) _store?.Remove(KeyTasksStartFilter);
        else                                _store?.Set(KeyTasksStartFilter, key);
    }

    // Последний выбранный фильтр — СОСТОЯНИЕ для режима «последний выбранный», не настройка:
    // пишется всегда, пустое значение снимает ключ.
    public static string LoadTasksLastFilter() => _store?.Get(KeyTasksLastFilter) ?? "";

    public static void SaveTasksLastFilter(string key)
    {
        if (key.Length == 0) _store?.Remove(KeyTasksLastFilter);
        else                 _store?.Set(KeyTasksLastFilter, key);
    }

    public static bool LoadTasksQuickExecutorMe() =>
        bool.TryParse(_store?.Get(KeyTasksQuickExecutorMe), out var me) ? me : TasksQuickExecutorMeDefault;

    public static void SaveTasksQuickExecutorMe(bool me)
    {
        if (me == TasksQuickExecutorMeDefault) _store?.Remove(KeyTasksQuickExecutorMe);
        else                                   _store?.Set(KeyTasksQuickExecutorMe, me.ToString());
    }

    public static string LoadTasksQuickDue() => _store?.Get(KeyTasksQuickDue) ?? TasksQuickDueDefault;

    public static void SaveTasksQuickDue(string key)
    {
        if (key == TasksQuickDueDefault) _store?.Remove(KeyTasksQuickDue);
        else                             _store?.Set(KeyTasksQuickDue, key);
    }

    public static string LoadTasksDefaultStage() => _store?.Get(KeyTasksDefaultStage) ?? TasksDefaultStageDefault;

    public static void SaveTasksDefaultStage(string stage)
    {
        if (stage == TasksDefaultStageDefault) _store?.Remove(KeyTasksDefaultStage);
        else                                   _store?.Set(KeyTasksDefaultStage, stage);
    }

    // Курсоры прочитанной очереди событий по базам — СОСТОЯНИЕ, а не настройка (как водяная
    // метка): пишутся всегда, дефолта у них нет. Формат «db:id;db:id» — как у галочек
    // коллекций: один ключ вместо ключа на базу, а курсоры размонтированных баз отпадают
    // сами при следующей записи.
    public static Dictionary<int, long> LoadTaskEventCursors()
    {
        var result = new Dictionary<int, long>();
        var packed = _store?.Get(KeyTaskEventCursors);
        if (string.IsNullOrEmpty(packed)) return result;

        // Мусор молча пропускаем: испорченный app.db не должен ронять старт — худшее
        // последствие тут «курсор посеется заново».
        foreach (var pair in packed.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var db) &&
                long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                result[db] = id;
        }
        return result;
    }

    public static void SaveTaskEventCursors(IReadOnlyDictionary<int, long> cursors)
    {
        if (cursors.Count == 0) { _store?.Remove(KeyTaskEventCursors); return; }

        // Порядок фиксированный: без сортировки один и тот же набор писался бы в базу
        // каждый раз новой строкой.
        _store?.Set(KeyTaskEventCursors, string.Join(';', cursors
            .OrderBy(c => c.Key)
            .Select(c => $"{c.Key}:{c.Value.ToString(CultureInfo.InvariantCulture)}")));
    }

    // ── Автозапуск и фоновый режим ────────────────────────────────────────────────
    //
    // Оба служат уведомлениям, но независимы от тумблера «Уведомления». Стандарт — выключено
    // (без них поведение приложения не меняется). Автозапуск дополнительно требует установленного
    // приложения (.app/.exe) — в dev-режиме (dotnet run) недоступен, см. Utils/Autostart.
    public const bool AutostartEnabledDefault  = false;
    public const bool BackgroundEnabledDefault = false;

    public static bool LoadAutostartEnabled() =>
        bool.TryParse(_store?.Get(KeyAutostartEnabled), out var on) ? on : AutostartEnabledDefault;

    public static void SaveAutostartEnabled(bool on)
    {
        if (on == AutostartEnabledDefault) _store?.Remove(KeyAutostartEnabled);
        else                               _store?.Set(KeyAutostartEnabled, on.ToString());
    }

    public static bool LoadBackgroundEnabled() =>
        bool.TryParse(_store?.Get(KeyBackgroundEnabled), out var on) ? on : BackgroundEnabledDefault;

    public static void SaveBackgroundEnabled(bool on)
    {
        if (on == BackgroundEnabledDefault) _store?.Remove(KeyBackgroundEnabled);
        else                                _store?.Set(KeyBackgroundEnabled, on.ToString());
    }

    // ── Резервное копирование ─────────────────────────────────────────────────────
    //
    // Раздел «Настройки → Резервное копирование»: копии баз (SQLite — файл через VACUUM INTO,
    // PostgreSQL — дамп pg_dump) складываются в одну папку, расписание у каждой базы свое.
    // Общие настройки — папка и глубина ротации; на базу — тумблер, строка расписания
    // (разбирает Database/Backup/BackupSchedule, мусор → стандарт) и отметка последнего
    // выполнения (состояние службы, как водяная метка уведомлений). Ключи на базу собираются
    // приставкой «backup.<dbId>.» — фиксированного списка нет, базы приходят и уходят.

    // Пусто = стандартная папка Backups в папке данных приложения (см. ResolveBackupFolder).
    public const string BackupFolderDefault = "";

    public const int BackupKeepMin     = 1;
    public const int BackupKeepMax     = 100;
    public const int BackupKeepDefault = 10;

    public const bool   BackupEnabledDefault  = false;
    public const string BackupScheduleDefault = "daily 03:00";

    public static string LoadBackupFolder() => _store?.Get(KeyBackupFolder) ?? BackupFolderDefault;

    public static void SaveBackupFolder(string path)
    {
        var trimmed = (path ?? "").Trim();
        if (trimmed.Length == 0) _store?.Remove(KeyBackupFolder);
        else                     _store?.Set(KeyBackupFolder, trimmed);
    }

    /// <summary>Папка, куда фактически писать копии: настроенная, а пусто — стандартная
    /// «Backups» в папке данных (рядом с базами — на том же диске, всегда доступна на запись).</summary>
    public static string ResolveBackupFolder()
    {
        var folder = LoadBackupFolder();
        return folder.Length > 0 ? folder : Path.Combine(AppDataDir, "Backups");
    }

    public static int LoadBackupKeep()
    {
        if (int.TryParse(_store?.Get(KeyBackupKeep), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            && n >= BackupKeepMin && n <= BackupKeepMax)
            return n;
        return BackupKeepDefault;
    }

    public static void SaveBackupKeep(int keep)
    {
        if (keep == BackupKeepDefault) _store?.Remove(KeyBackupKeep);
        else                           _store?.Set(KeyBackupKeep, keep.ToString(CultureInfo.InvariantCulture));
    }

    public static bool LoadBackupEnabled(int dbId) =>
        bool.TryParse(_store?.Get(BackupKey(dbId, "enabled")), out var on) ? on : BackupEnabledDefault;

    public static void SaveBackupEnabled(int dbId, bool on)
    {
        if (on == BackupEnabledDefault) _store?.Remove(BackupKey(dbId, "enabled"));
        else                            _store?.Set(BackupKey(dbId, "enabled"), on.ToString());
    }

    // Строка расписания хранится как есть: валидация — в BackupSchedule.Parse (мусор дает
    // стандарт), самому хранилищу настроек о форматах расписаний знать незачем.
    public static string LoadBackupSchedule(int dbId) =>
        _store?.Get(BackupKey(dbId, "schedule")) ?? BackupScheduleDefault;

    public static void SaveBackupSchedule(int dbId, string schedule)
    {
        if (schedule == BackupScheduleDefault) _store?.Remove(BackupKey(dbId, "schedule"));
        else                                   _store?.Set(BackupKey(dbId, "schedule"), schedule);
    }

    // Отметка последнего выполненного бэкапа — СОСТОЯНИЕ службы, а не настройка (как
    // водяная метка уведомлений): пишется всегда, дефолта нет. null — копий еще не было
    // (служба поставит якорь «сейчас» при первом тике включенного расписания).
    public static DateTime? LoadBackupLastRun(int dbId) =>
        DateTime.TryParse(_store?.Get(BackupKey(dbId, "lastRun")), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var dt) ? dt : null;

    public static void SaveBackupLastRun(int dbId, DateTime instant) =>
        _store?.Set(BackupKey(dbId, "lastRun"), instant.ToString("o", CultureInfo.InvariantCulture));

    /// <summary>
    /// Убрать настройки бэкапа баз, которых больше нет в реестре: отключили базу — ее
    /// расписание и отметки не живут в app.db вечно. Вызывается на старте после AttachStore
    /// (рядом с уборкой настроек расширений); возвращает число убранных ключей для журнала.
    /// Сверка — по полной приставке «backup.&lt;id&gt;.»: id — число, «backup.1.» не заденет
    /// «backup.12.…».
    /// </summary>
    public static int RemoveOrphanBackupSettings(IEnumerable<int> liveDbIds)
    {
        if (_store is null) return 0;

        // Общие ключи раздела (backupFolder, backupKeep) под приставку «backup.» не попадают:
        // в них нет точки после слова backup.
        var live = liveDbIds.Select(id => $"{BackupKeyPrefix}{id.ToString(CultureInfo.InvariantCulture)}.").ToList();
        var orphans = _store.KeysWithPrefix(BackupKeyPrefix)
                            .Where(key => !live.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
                            .ToList();

        foreach (var key in orphans) _store.Remove(key);
        return orphans.Count;
    }

    // ── Вводный чек-лист первого запуска ──────────────────────────────────────────
    //
    // Не настройка, а отметка «вводные задачи уже заводились» (см. Database/IntroSeed.cs).
    // Ставится ОДИН раз — на чистой установке, до наполнения базы: даже прерванный на
    // середине сидинг не должен повториться. Пользователь, удаливший вводные задачи или
    // всю коллекцию, сделал это осознанно — возвращать их следующим запуском нельзя.
    public const bool IntroSeedDoneDefault = false;

    public static bool LoadIntroSeedDone() =>
        bool.TryParse(_store?.Get(KeyIntroSeedDone), out var done) ? done : IntroSeedDoneDefault;

    public static void SaveIntroSeedDone(bool done)
    {
        if (done == IntroSeedDoneDefault) _store?.Remove(KeyIntroSeedDone);
        else                              _store?.Set(KeyIntroSeedDone, done.ToString());
    }

    // ── Настройки расширений ──────────────────────────────────────────────────
    //
    // Значения полей, которые расширение объявило в IExtension.Settings (например, путь к
    // файлу-шаблону). Ключ строки — «ext.<id расширения>.<имя настройки>».
    //
    // Ядро хранит здесь ПРОСТО СТРОКУ и не знает ни названий расширений, ни смысла их
    // настроек: иначе удаление расширения требовало бы правок в этом файле и на экране
    // настроек, а по контракту (см. Extensions/IExtension.cs) достаточно убрать папку и
    // строку из ExtensionRegistry. Настройка удаленного расширения остается в app.db
    // осиротевшей строкой — ее больше никто не читает, вреда нет, а при возврате
    // расширения прежнее значение подхватится само.
    //
    // Пусто = «не задано»: пустое значение стирает ключ, как у пути обновления.
    public static string LoadExtensionSetting(string extensionId, string key) =>
        _store?.Get(ExtensionKey(extensionId, key)) ?? "";

    public static void SaveExtensionSetting(string extensionId, string key, string value)
    {
        var storeKey = ExtensionKey(extensionId, key);
        var trimmed  = (value ?? "").Trim();
        if (trimmed.Length == 0) _store?.Remove(storeKey);
        else                     _store?.Set(storeKey, trimmed);
    }

    private static string ExtensionKey(string extensionId, string key) =>
        $"{ExtensionKeyPrefix}{extensionId}.{key}";

    // ── Открытая вкладка раздела «Интеграции» ──────────────────────────────────
    //
    // Какая вкладка открывается при входе в раздел. Настройки как таковой нет — это память
    // о последнем выборе, как у видов календаря: человек настраивает обмен в несколько
    // заходов, и каждый раз возвращаться к первой вкладке было бы утомительно.
    //
    // Список значений задает Settings/IntegrationTabs.cs; проверка там же, поэтому
    // незнакомое значение (правка app.db руками, откат на версию с другим набором вкладок)
    // молча заменяется первой вкладкой, а не роняет раздел.
    public static string LoadIntegrationsTab() => _store?.Get(KeyIntegrationsTab) ?? "";

    public static void SaveIntegrationsTab(string key) => _store?.Set(KeyIntegrationsTab, key);

    // ── Папка-приемник ─────────────────────────────────────────────────────────
    //
    // Корень обмена и база, в которую ложатся входящие файлы. Настройка МАШИННАЯ, и это не
    // упущение: одну и ту же папку намеренно смотрят несколько компьютеров разом (захват идет
    // переименованием, см. Integrations/InboxFolder.cs). Держи ее в общей базе — и включение
    // приемника на одной машине включало бы его у всех.
    //
    // Разрешен ли сам корень, решает политика организации, а не эта настройка: сохраненный
    // путь — всего лишь память о выборе, и запрещенная папка просто не откроется.
    public static string LoadInboxFolder() => _store?.Get(KeyInboxFolder) ?? "";

    public static void SaveInboxFolder(string path) => _store?.Set(KeyInboxFolder, path ?? "");

    /// <summary>База, в которую ложатся входящие файлы; 0 — не выбрана.</summary>
    public static int LoadInboxDb() =>
        int.TryParse(_store?.Get(KeyInboxDb), out var id) && id > 0 ? id : 0;

    public static void SaveInboxDb(int dbId) => _store?.Set(KeyInboxDb, dbId.ToString());

    // ── Последняя известная политика интеграций ────────────────────────────────
    //
    // Снимок текста политики по каждому источнику. Нужен ровно для одного случая: сетевая
    // папка раздачи пропала (выключили сервер, отвалился диск P:), а ограничения обязаны
    // остаться прежними. Без снимка пропажа источника меняла бы права — в ту или другую
    // сторону, и обе одинаково плохи: либо обмен встает на ровном месте, либо, что хуже,
    // исчезнувший строгий источник перестает сужать список разрешенного.
    //
    // Хранится СЫРОЙ текст, а не разобранная политика: разбор меняется вместе с программой,
    // а снимок должен переживать обновление. Подпись хранится рядом своим ключом — источник,
    // которому подпись обязательна, обязан пройти проверку и из снимка тоже.
    //
    // Ключ строки — «integrations.policy.<источник>»; имена источников задает
    // Integrations/PolicySources.cs, ядро их не знает.
    public static string LoadPolicySnapshot(string sourceKey) =>
        _store?.Get(PolicySnapshotKey(sourceKey)) ?? "";

    public static void SavePolicySnapshot(string sourceKey, string text)
    {
        var storeKey = PolicySnapshotKey(sourceKey);
        if (string.IsNullOrWhiteSpace(text)) _store?.Remove(storeKey);
        else                                 _store?.Set(storeKey, text);
    }

    /// <summary>
    /// Забыть снимок источника. Зовется, когда источник пропал НЕ из-за недоступности, а
    /// по-настоящему: папка на месте, файла в ней больше нет. Иначе отмененная политика
    /// действовала бы вечно, и снять ее у службы ИТ не вышло бы никак.
    /// </summary>
    public static void ForgetPolicySnapshot(string sourceKey) =>
        _store?.Remove(PolicySnapshotKey(sourceKey));

    private static string PolicySnapshotKey(string sourceKey) =>
        $"{PolicySnapshotPrefix}{sourceKey}";

    /// <summary>
    /// Убрать настройки расширений, которых в сборке больше нет: удалено расширение —
    /// удалены и его строки, лишнего в app.db не остается. Вызывается на старте после
    /// AttachStore; возвращает число убранных ключей (для записи в журнал).
    ///
    /// Живым считается расширение, чей Id пришел в <paramref name="liveExtensionIds"/> —
    /// список дает реестр расширений. Сверяем по полной приставке «ext.&lt;id&gt;.», а не
    /// разбором ключа: id с точкой внутри не сломает уборку и не заденет соседа с похожим
    /// началом имени (pdf-rename и pdf-rename-v2 — разные расширения).
    ///
    /// Обратная сторона: расширение, временно убранное из реестра, теряет свои настройки
    /// при первом же запуске — вернув его, значения придется ввести заново.
    /// </summary>
    public static int RemoveOrphanExtensionSettings(IEnumerable<string> liveExtensionIds)
    {
        if (_store is null) return 0;

        var live = liveExtensionIds.Select(id => $"{ExtensionKeyPrefix}{id}.").ToList();
        var orphans = _store.KeysWithPrefix(ExtensionKeyPrefix)
                            .Where(key => !live.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
                            .ToList();

        foreach (var key in orphans) _store.Remove(key);
        return orphans.Count;
    }

    // ── Галочки коллекций в боковой панели (календарь и задачи) ───────────────
    //
    // Хранятся СНЯТЫЕ галочки, а не отмеченные: по умолчанию видно все, поэтому пустая
    // настройка = стандарт, а новая коллекция появляется отмеченной сама собой (ее ключа
    // в списке снятых нет). Обратное хранение («какие отмечены») требовало бы дописывать
    // каждую созданную коллекцию, и любая пропущенная запись прятала бы ее от человека.
    //
    // Пара — (DbId реестра, id коллекции внутри БД): id коллекций между базами пересекаются.
    // Формат строки — «db:col;db:col».

    /// <param name="scope">Раздел: <c>calendar</c> или <c>tasks</c> — наборы у них свои.</param>
    public static HashSet<(int DbId, int CollectionId)> LoadHiddenCollections(string scope)
    {
        var result = new HashSet<(int DbId, int CollectionId)>();
        var packed = _store?.Get(KeyHiddenCollections(scope));
        if (string.IsNullOrEmpty(packed)) return result;

        // Мусор молча пропускаем: настройка правится только этим кодом, но испорченный
        // app.db не должен ронять старт — хуже всего будет лишняя отмеченная коллекция.
        foreach (var pair in packed.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var db) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var col))
                result.Add((db, col));
        }
        return result;
    }

    public static void SaveHiddenCollections(string scope,
                                             IReadOnlyCollection<(int DbId, int CollectionId)> hidden)
    {
        var key = KeyHiddenCollections(scope);
        // Ничего не снято — это и есть стандарт: ключ удаляем, как остальные настройки.
        if (hidden.Count == 0) { _store?.Remove(key); return; }

        // Порядок фиксированный: множество неупорядочено, и без сортировки один и тот же
        // набор писался бы в базу каждый раз новой строкой.
        _store?.Set(key, string.Join(';', hidden
            .OrderBy(h => h.DbId).ThenBy(h => h.CollectionId)
            .Select(h => $"{h.DbId}:{h.CollectionId}")));
    }

    // ── Скрытые виды разделов и модули панели навигации ───────────────────────
    //
    // Что НЕ показывать: пункты переключателя вида в навбаре («Оформление → Отображение
    // раздела …») и кнопки модулей панели навигации. Как и у галочек коллекций, хранятся
    // СКРЫТЫЕ, а не показываемые: пустая настройка = стандарт «видно все», и новый вид
    // появляется у всех сам собой, без дописывания в каждую установку.
    //
    // Ключи пунктов дает ViewCatalog, формат строки — «key;key». Правило «хотя бы один
    // пункт остается» держит экран настроек: сюда набор приходит уже проверенным, а
    // навбар на всякий случай не дает списку опустеть (см. ViewCatalog.Visible).
    public static HashSet<string> LoadHiddenViews(string scope)
    {
        var packed = _store?.Get(KeyHiddenViews(scope));
        if (string.IsNullOrEmpty(packed)) return [];

        return [.. packed.Split(';', StringSplitOptions.RemoveEmptyEntries)];
    }

    public static void SaveHiddenViews(string scope, IReadOnlyCollection<string> hidden)
    {
        var key = KeyHiddenViews(scope);
        if (hidden.Count == 0) { _store?.Remove(key); return; }

        // Порядок фиксированный: множество неупорядочено, и без сортировки один и тот же
        // набор писался бы в базу каждый раз новой строкой.
        _store?.Set(key, string.Join(';', hidden.OrderBy(h => h, StringComparer.Ordinal)));
    }

    /// <summary>
    /// Выбросить из «скрытых» ключи, которых больше нет среди пунктов раздела. Нужно разделам
    /// с ДИНАМИЧЕСКИМ составом — сейчас это расширения (<c>ViewCatalog.ScopeExtensions</c>):
    /// удалили расширение, и его Id уходит из настройки при первом же запуске, как и группа
    /// «ext.&lt;Id&gt;.…» (см. <see cref="RemoveOrphanExtensionSettings"/>). Иначе строка жила бы
    /// в app.db вечно, а вернувшееся расширение оказалось бы скрытым без видимой причины.
    ///
    /// Возвращает число убранных ключей (для записи в журнал). Разделы со статическим списком
    /// звать не нужно: их пункты не исчезают между запусками.
    /// </summary>
    public static int PruneHiddenViews(string scope, IEnumerable<string> liveKeys)
    {
        var hidden = LoadHiddenViews(scope);
        if (hidden.Count == 0) return 0;

        var live    = liveKeys.ToHashSet(StringComparer.Ordinal);
        var orphans = hidden.Where(k => !live.Contains(k)).ToList();
        if (orphans.Count == 0) return 0;

        hidden.ExceptWith(orphans);
        SaveHiddenViews(scope, hidden);
        return orphans.Count;
    }

    // ── Ключи настроек ─────────────────────────────────────────────────────────

    private static string KeyHiddenCollections(string scope) => $"hiddenCollections.{scope}";
    private static string KeyHiddenViews(string scope)       => $"hiddenViews.{scope}";

    private const string KeyScale            = "scale";
    private const string KeyRecordPanelOpen  = "recordPanelOpen";
    private const string KeyRecordPanelWidth = "recordPanelWidth";
    private const string KeyGuideTocWidth    = "guideTocWidth";
    private const string KeyFontDelta        = "fontDelta";
    private const string KeyStartupModule    = "startupModule";
    private const string KeyIntegrationsTab  = "integrations.tab";
    private const string KeyInboxFolder      = "integrations.inbox.folder";
    private const string KeyInboxDb          = "integrations.inbox.db";
    private const string KeyTasksView        = "tasksView";
    private const string KeyCalendarView     = "calendarView";
    private const string KeyCalendarDaysInWeek        = "calendarDaysInWeek";
    private const string KeyCalendarDayStart          = "calendarDayStartMin";
    private const string KeyCalendarDayEnd            = "calendarDayEndMin";
    private const string KeyCalendarWeekScroll        = "calendarWeekScroll";
    private const string KeyCalendarMultiDayGrid      = "calendarMultiDayGrid";
    private const string KeyCalendarNewEventDuration  = "calendarNewEventDuration";
    private const string KeyDiagramScale     = "diagramScale";
    private const string KeyConstructorView  = "constructorView";
    private const string KeyUpdatePath       = "updatePath";
    private const string KeyNotesVault       = "notesVault";
    private const string KeyNotesLastFile    = "notesLastFile";
    private const string KeyNotesViewMode    = "notesViewMode";
    private const string KeyNotesSort        = "notesSort";
    private const string KeyAutoUpdate       = "autoUpdate";
    private const string KeyNotificationsEnabled = "notificationsEnabled";
    private const string KeyNotifyWatermark      = "notifyWatermark";
    private const string KeyNotifyTaskAssigned   = "notifyTaskAssigned";
    private const string KeyNotifyTaskStatus     = "notifyTaskStatus";
    private const string KeyTaskEventCursors     = "notifyTaskCursors";
    private const string KeyTasksOverdueNextDay  = "tasksOverdueNextDay";
    private const string KeyTasksShowDone        = "tasksShowDone";
    private const string KeyTasksStartFilter     = "tasksStartFilter";
    private const string KeyTasksLastFilter      = "tasksLastFilter";
    private const string KeyTasksQuickExecutorMe = "tasksQuickExecutorMe";
    private const string KeyTasksQuickDue        = "tasksQuickDue";
    private const string KeyTasksDefaultStage    = "tasksDefaultStage";
    private const string KeyAutostartEnabled     = "autostartEnabled";
    private const string KeyBackgroundEnabled    = "backgroundEnabled";
    private const string KeyIntroSeedDone        = "introSeedDone";
    private const string KeyBackupFolder         = "backupFolder";
    private const string KeyBackupKeep           = "backupKeep";
    // Не ключ, а приставка: полный ключ на базу собирает BackupKey — «backup.<dbId>.<имя>».
    private const string BackupKeyPrefix         = "backup.";

    private static string BackupKey(int dbId, string name) =>
        $"{BackupKeyPrefix}{dbId.ToString(CultureInfo.InvariantCulture)}.{name}";
    // Не ключ, а приставка: полный ключ собирает ExtensionKey из id расширения и имени
    // настройки — фиксированного списка тут быть не может, расширения приходят и уходят.
    private const string ExtensionKeyPrefix      = "ext.";
    // Тоже приставка: имена источников политики задает Integrations/PolicySources.cs, и
    // перечислять их здесь значило бы держать список в двух местах.
    private const string PolicySnapshotPrefix    = "integrations.policy.";

    // ── Приватное ─────────────────────────────────────────────────────────────

    private static string ResolveAppDataDir()
    {
        // Release (publish) — стабильно в %AppData%\Roaming\SmartBase.
        // Debug — локальная папка .localdata в корне проекта (рядом с SmartBase.csproj):
        // изолировано «как venv», переживает dotnet clean и снос artifacts\, не мешается
        // с исходниками. Корень ищем по маркеру, а не счетом «..» — не зависит от
        // раскладки вывода (classic bin\Debug или artifacts\bin\...).
#if DEBUG
        var root = FindProjectRoot() ?? AppContext.BaseDirectory;
        return Path.Combine(root, ".localdata");
#else
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(roaming, AppName);
        MigrateLegacyDataDir(roaming, dir);
        return dir;
#endif
    }

#if !DEBUG
    // Одноразовый переезд данных со старого имени приложения (DataBase) на новое (SmartBase):
    // если новой папки еще нет, а старая есть — переносим ее целиком, чтобы не потерять базы.
    private static void MigrateLegacyDataDir(string roaming, string newDir)
    {
        try
        {
            if (Directory.Exists(newDir)) return;
            var legacy = Path.Combine(roaming, "DataBase");
            if (Directory.Exists(legacy))
                Directory.Move(legacy, newDir);
        }
        catch { /* переезд не критичен: при неудаче создастся чистая папка данных */ }
    }
#endif

#if DEBUG
    // Каталог с SmartBase.csproj вверх от каталога сборки (корень проекта).
    private static string? FindProjectRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (dir.GetFiles("SmartBase.csproj").Length > 0)
                return dir.FullName;
        return null;
    }
#endif
}
