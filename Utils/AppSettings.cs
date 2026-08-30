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

    // Режим тела раздела: "source" — исходный текст, "split" — слева правка, справа живой
    // просмотр, "preview" — только просмотр. Стандарт — разделенный: ближе всего к тому, как
    // markdown показывают редакторы заметок.
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
    private const string KeyTasksView        = "tasksView";
    private const string KeyCalendarView     = "calendarView";
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
    private const string KeyAutostartEnabled     = "autostartEnabled";
    private const string KeyBackgroundEnabled    = "backgroundEnabled";
    private const string KeyIntroSeedDone        = "introSeedDone";
    // Не ключ, а приставка: полный ключ собирает ExtensionKey из id расширения и имени
    // настройки — фиксированного списка тут быть не может, расширения приходят и уходят.
    private const string ExtensionKeyPrefix      = "ext.";

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
