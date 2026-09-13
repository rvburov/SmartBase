namespace SmartBase.Utils;

/// <summary>
/// Живые настройки раздела «Конструктор базы данных» (по образцу <see cref="TaskSettings"/> и
/// <see cref="CalendarSettings"/>): кэш значений из AppSettings + событие <see cref="Changed"/> —
/// открытые таблица и панель записи перечитываются без перезапуска. Set* сохраняют дельту и
/// публикуют изменение. Мусор в хранилище (правка app.db руками, откат версии) читается как
/// стандарт, поэтому раздел никогда не остается с нулевой строкой или опросом раз в сутки.
///
/// Стартовый вид коллекции (<c>constructorView</c>) и видимость видов (<c>hiddenViews.constructor</c>)
/// в разделе тоже показаны, но сюда НЕ входят: их ведет общий механизм видов (ViewCatalog +
/// InitAppearance), одинаковый для всех разделов, и дублировать его кэшем здесь незачем.
///
/// О типах свойств, цветах и геометрии сервис знает только как о примитивах: списки допустимых
/// значений собирает панель настроек, применяют — сами вью. Так Utils не тянет за собой Views.
/// </summary>
public static class ConstructorSettings
{
    /// <summary>Тип свойства, который стоит в окне «Создание свойства» (ключ FieldTypeItem.Type).</summary>
    public static string NewFieldType { get; private set; } = AppSettings.ConstructorNewFieldTypeDefault;

    /// <summary>Открывать созданную запись в панели справа (false — создать и остаться в таблице).</summary>
    public static bool OpenNewRecord { get; private set; } = AppSettings.ConstructorOpenNewRecordDefault;

    /// <summary>Спрашивать подтверждение при удалении записей и свойств. Предупреждение об
    /// обрыве связей этой настройке не подчиняется — оно о потере данных, а не о рутине.</summary>
    public static bool ConfirmDelete { get; private set; } = AppSettings.ConstructorConfirmDeleteDefault;

    /// <summary>Цвет новой коллекции, «#RRGGBB».</summary>
    public static string NewCollectionColor { get; private set; } = AppSettings.ConstructorNewCollectionColorDefault;

    /// <summary>Высота строки таблицы в логических пикселях (28/36/44).</summary>
    public static int RowHeight { get; private set; } = AppSettings.ConstructorRowHeightDefault;

    /// <summary>Стандартная ширина столбца: минимум авто-режима и ширина колонки без ручной.</summary>
    public static int ColWidth { get; private set; } = AppSettings.ConstructorColWidthDefault;

    /// <summary>Ручная ширина столбцов у новых коллекций (у коллекции без своей записи настроек).</summary>
    public static bool ColResize { get; private set; } = AppSettings.ConstructorColResizeDefault;

    /// <summary>Ручная высота строк у новых коллекций.</summary>
    public static bool RowResize { get; private set; } = AppSettings.ConstructorRowResizeDefault;

    /// <summary>Запись открывается двойным кликом по названию (false — одинарным).</summary>
    public static bool OpenByDoubleClick { get; private set; } = AppSettings.ConstructorOpenByDoubleClickDefault;

    /// <summary>Описание записи открывается в динамическом просмотре (false — в редакторе).</summary>
    public static bool DescriptionPreview { get; private set; } = AppSettings.ConstructorDescriptionPreviewDefault;

    /// <summary>Восстанавливать открытую запись и панель при запуске программы.</summary>
    public static bool RestoreRecord { get; private set; } = AppSettings.ConstructorRestoreRecordDefault;

    /// <summary>Запоминать раскладку панели записи между сеансами.</summary>
    public static bool RememberLayout { get; private set; } = AppSettings.ConstructorRememberLayoutDefault;

    /// <summary>Запоминать фильтры и сортировку коллекций между открытиями.</summary>
    public static bool RememberFilters { get; private set; } = AppSettings.ConstructorRememberFiltersDefault;

    /// <summary>Период проверки чужих правок при активном окне, секунды (3/10/30).</summary>
    public static int PollSeconds { get; private set; } = AppSettings.ConstructorPollSecondsDefault;

    /// <summary>Настройки изменились — разделу пора перечитаться.</summary>
    public static event Action? Changed;

    /// <summary>Счетчик изменений — «догоняющая» перерисовка, как у CalendarSettings.</summary>
    public static long Stamp { get; private set; }

    /// <summary>Старт: считать сохраненные значения. Вызывать после AppSettings.AttachStore.</summary>
    public static void Init()
    {
        NewFieldType       = ParseFieldType(AppSettings.LoadConstructorNewFieldType());
        OpenNewRecord      = AppSettings.LoadConstructorOpenNewRecord();
        ConfirmDelete      = AppSettings.LoadConstructorConfirmDelete();
        NewCollectionColor = ParseColor(AppSettings.LoadConstructorNewCollectionColor());
        RowHeight          = AppSettings.LoadConstructorRowHeight();
        ColWidth           = AppSettings.LoadConstructorColWidth();
        ColResize          = AppSettings.LoadConstructorColResize();
        RowResize          = AppSettings.LoadConstructorRowResize();
        OpenByDoubleClick  = AppSettings.LoadConstructorOpenByDoubleClick();
        DescriptionPreview = AppSettings.LoadConstructorDescriptionPreview();
        RestoreRecord      = AppSettings.LoadConstructorRestoreRecord();
        RememberLayout     = AppSettings.LoadConstructorRememberLayout();
        RememberFilters    = AppSettings.LoadConstructorRememberFilters();
        PollSeconds        = AppSettings.LoadConstructorPollSeconds();

        PublishRowHeight();
    }

    /// <summary>Ключ ресурса высоты строки таблицы (Assets/Styles/Controls.axaml).</summary>
    public const string RowHeightResourceKey = "RowHeightTable";

    // Высота строки едет к экрану ресурсом приложения, а не через кэш: ее читают и таблица,
    // и дерево коллекций (они выровнены построчно), и разметка, и код — ровно тот же прием,
    // что у Scaling с AppZoomTransform. Приложения может не быть в тестах — тогда просто
    // некуда публиковать, значение остается в свойстве.
    private static void PublishRowHeight()
    {
        if (Avalonia.Application.Current is { } app)
            app.Resources[RowHeightResourceKey] = (double)RowHeight;
    }

    /// <summary>Период опроса чужих правок для окна в фоне: тот же шаг, растянутый в пять раз
    /// (3 с → 15 с, как было зашито в поллере до настройки).</summary>
    public static int BackgroundPollSeconds => PollSeconds * 5;

    public static void SetNewFieldType(string type)
    {
        type = ParseFieldType(type);
        if (type == NewFieldType) return;
        NewFieldType = type;
        AppSettings.SaveConstructorNewFieldType(type);
        Publish();
    }

    public static void SetOpenNewRecord(bool on)
    {
        if (on == OpenNewRecord) return;
        OpenNewRecord = on;
        AppSettings.SaveConstructorOpenNewRecord(on);
        Publish();
    }

    public static void SetConfirmDelete(bool on)
    {
        if (on == ConfirmDelete) return;
        ConfirmDelete = on;
        AppSettings.SaveConstructorConfirmDelete(on);
        Publish();
    }

    public static void SetNewCollectionColor(string hex)
    {
        hex = ParseColor(hex);
        if (hex == NewCollectionColor) return;
        NewCollectionColor = hex;
        AppSettings.SaveConstructorNewCollectionColor(hex);
        Publish();
    }

    public static void SetRowHeight(int height)
    {
        if (Array.IndexOf(AppSettings.ConstructorRowHeights, height) < 0) return;
        if (height == RowHeight) return;
        RowHeight = height;
        AppSettings.SaveConstructorRowHeight(height);
        PublishRowHeight();
        Publish();
    }

    public static void SetColWidth(int width)
    {
        if (Array.IndexOf(AppSettings.ConstructorColWidths, width) < 0) return;
        if (width == ColWidth) return;
        ColWidth = width;
        AppSettings.SaveConstructorColWidth(width);
        Publish();
    }

    public static void SetColResize(bool on)
    {
        if (on == ColResize) return;
        ColResize = on;
        AppSettings.SaveConstructorColResize(on);
        Publish();
    }

    public static void SetRowResize(bool on)
    {
        if (on == RowResize) return;
        RowResize = on;
        AppSettings.SaveConstructorRowResize(on);
        Publish();
    }

    public static void SetOpenByDoubleClick(bool on)
    {
        if (on == OpenByDoubleClick) return;
        OpenByDoubleClick = on;
        AppSettings.SaveConstructorOpenByDoubleClick(on);
        Publish();
    }

    public static void SetDescriptionPreview(bool on)
    {
        if (on == DescriptionPreview) return;
        DescriptionPreview = on;
        AppSettings.SaveConstructorDescriptionPreview(on);
        Publish();
    }

    public static void SetRestoreRecord(bool on)
    {
        if (on == RestoreRecord) return;
        RestoreRecord = on;
        AppSettings.SaveConstructorRestoreRecord(on);
        Publish();
    }

    /// <summary>Выключение «запоминать раскладку» заодно СТИРАЕТ запомненное: иначе повторное
    /// включение через месяц вернуло бы забытую раскладку вместо текущей.</summary>
    public static void SetRememberLayout(bool on)
    {
        if (on == RememberLayout) return;
        RememberLayout = on;
        AppSettings.SaveConstructorRememberLayout(on);
        if (!on)
        {
            AppSettings.SaveConstructorPropsHeight(0);
            AppSettings.SaveConstructorNameColWidth(0);
        }
        Publish();
    }

    /// <summary>Выключение стирает запомненное — как и у раскладки панели записи: включив
    /// настройку снова, человек ждет текущий отбор, а не забытый месяц назад.</summary>
    public static void SetRememberFilters(bool on)
    {
        if (on == RememberFilters) return;
        RememberFilters = on;
        AppSettings.SaveConstructorRememberFilters(on);
        if (!on) ConstructorFilterMemory.Clear();
        Publish();
    }

    public static void SetPollSeconds(int seconds)
    {
        if (Array.IndexOf(AppSettings.ConstructorPollSeconds, seconds) < 0) return;
        if (seconds == PollSeconds) return;
        PollSeconds = seconds;
        AppSettings.SaveConstructorPollSeconds(seconds);
        Publish();
    }

    private static void Publish()
    {
        Stamp++;
        Changed?.Invoke();
    }

    // Тип свойства проверяем только на пустоту: полный список типов знает окно создания
    // свойства (Controls/Dialogs), и тянуть его в Utils ради валидации не нужно — неизвестный
    // ключ там и так отдаст первый пункт списка.
    private static string ParseFieldType(string? type) =>
        string.IsNullOrWhiteSpace(type) ? AppSettings.ConstructorNewFieldTypeDefault : type.Trim();

    private static string ParseColor(string? hex) =>
        Core.Models.AppPalette.IsHex(hex) ? hex!.ToUpperInvariant() : AppSettings.ConstructorNewCollectionColorDefault;
}
