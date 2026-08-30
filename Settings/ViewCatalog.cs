using SmartBase.Extensions;
using SmartBase.Utils;

namespace SmartBase.Settings;

/// <summary>Пункт переключателя: ключ для настроек и подпись в интерфейсе.</summary>
public sealed record ViewItem(string Key, string Label);

/// <summary>
/// Единый список того, что можно показать или скрыть: модули панели навигации и виды
/// разделов. Один источник на все приложение — по нему строятся и переключатели в навбарах,
/// и обе группы настроек «Оформления» («по умолчанию» и видимость). Раньше эти списки
/// лежали копиями в каждом навбаре, и добавление вида требовало правок в трех местах.
///
/// ПОРЯДОК ПУНКТОВ ЗНАЧИМ: позиция в списке — это глобальный номер вида, на который
/// завязаны потребители (например, <c>CalendarControllerView.SwitchView</c> и сохраненный
/// <c>calendarView</c>). Скрытие пунктов номера НЕ сдвигает: навбар показывает только
/// видимые, но наружу отдает по-прежнему глобальный номер. Поэтому новые виды дописываются
/// в КОНЕЦ списка, иначе сохраненные у людей значения станут значить другое.
/// Исключение — <see cref="ScopeExtensions"/>: там пункты не нумеруются вовсе (см. ниже).
/// </summary>
public static class ViewCatalog
{
    public const string ScopeNavigation  = "navigation";
    public const string ScopeCalendar    = "calendar";
    public const string ScopeTasks       = "tasks";
    public const string ScopeConstructor = "constructor";
    public const string ScopeExtensions  = "extensions";

    /// <summary>
    /// Модули панели навигации, которые разрешено скрывать. «Настройки» и «Профиль» сюда
    /// не входят намеренно: спрятав настройки, человек лишился бы способа вернуть их обратно.
    /// </summary>
    public static IReadOnlyList<ViewItem> Navigation { get; } =
    [
        new("calendar",    "Календарь"),
        new("tasks",       "Задачи"),
        new("constructor", "Конструктор базы данных"),
        new("extensions",  "Расширения"),
        new("notes",       "Заметки"),
    ];

    /// <summary>Виды календаря. Порядок — номера видов навбара (0 Расписание … 6 Диаграмма).</summary>
    public static IReadOnlyList<ViewItem> Calendar { get; } =
    [
        new("schedule", "Расписание"),
        new("journal",  "Журнал"),
        new("day",      "День"),
        new("week",     "Неделя"),
        new("month",    "Месяц"),
        new("year",     "Год"),
        new("diagram",  "Диаграмма"),
    ];

    /// <summary>Виды раздела «Задачи». Пока один; новые дописываются сюда.</summary>
    public static IReadOnlyList<ViewItem> Tasks { get; } =
    [
        new("list", "Список"),
    ];

    /// <summary>Виды коллекции в конструкторе.</summary>
    public static IReadOnlyList<ViewItem> Constructor { get; } =
    [
        new("table", "Таблица"),
        new("list",  "Список"),
    ];

    /// <summary>
    /// Расширения раздела «Расширения» — единственный ДИНАМИЧЕСКИЙ список каталога: пункты
    /// не перечислены здесь, а берутся из <see cref="ExtensionRegistry"/> (ключ — <c>Id</c>
    /// расширения, подпись — его <c>Name</c>). Так удаление расширения убирает его сразу
    /// отовсюду — из сайдбара, из настроек видимости и из списка галочек, — не требуя правок
    /// ни здесь, ни на экране настроек (контракт см. в <c>Extensions/IExtension.cs</c>).
    ///
    /// Правило «позиция = глобальный номер вида» к этому разделу НЕ относится: номеров у
    /// расширений нет, ключ — Id, а «вида по умолчанию» у раздела не бывает. Поэтому порядок
    /// здесь — просто порядок показа, тот же, что в реестре.
    ///
    /// Свойство пересобирается на каждое обращение (реестр статический, список короткий):
    /// кешировать нечего, а лишний снимок жил бы дольше самого реестра.
    /// </summary>
    public static IReadOnlyList<ViewItem> Extensions =>
        [.. ExtensionRegistry.All.Select(e => new ViewItem(e.Id, e.Name))];

    /// <summary>Все пункты раздела; неизвестный scope — пустой список.</summary>
    public static IReadOnlyList<ViewItem> All(string scope) => scope switch
    {
        ScopeNavigation  => Navigation,
        ScopeCalendar    => Calendar,
        ScopeTasks       => Tasks,
        ScopeConstructor => Constructor,
        ScopeExtensions  => Extensions,
        _                => [],
    };

    /// <summary>
    /// Пункты, которые показываем: все минус скрытые настройкой. Пустым результат не бывает —
    /// если настройка почему-то прячет все (испорченный app.db, откат на версию с другим
    /// набором видов), возвращаем первый пункт: раздел без единого вида неработоспособен.
    /// </summary>
    public static IReadOnlyList<ViewItem> Visible(string scope)
    {
        var all = All(scope);
        if (all.Count == 0) return all;

        var hidden  = AppSettings.LoadHiddenViews(scope);
        var visible = all.Where(v => !hidden.Contains(v.Key)).ToList();
        return visible.Count > 0 ? visible : [all[0]];
    }

    /// <summary>Позиция пункта в полном списке — тот самый глобальный номер вида (−1, если нет).</summary>
    public static int IndexOf(string scope, string key)
    {
        var all = All(scope);
        for (var i = 0; i < all.Count; i++)
            if (all[i].Key == key) return i;
        return -1;
    }

    /// <summary>Ключ пункта по глобальному номеру; выход за границы — ключ первого пункта.</summary>
    public static string KeyAt(string scope, int index)
    {
        var all = All(scope);
        if (all.Count == 0) return "";
        return index >= 0 && index < all.Count ? all[index].Key : all[0].Key;
    }

    /// <summary>Виден ли пункт (скрытый последний пункт все равно считается видимым — см. Visible).</summary>
    public static bool IsVisible(string scope, string key) =>
        Visible(scope).Any(v => v.Key == key);

    /// <summary>Первый видимый пункт раздела — к нему переезжает выбор «по умолчанию»,
    /// когда прежний пункт скрыли.</summary>
    public static ViewItem? FirstVisible(string scope) => Visible(scope).FirstOrDefault();
}
