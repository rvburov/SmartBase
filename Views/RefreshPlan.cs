namespace SmartBase.Views;

/// <summary>
/// Что перечитать на экране после своего коммита в базу (шина <c>ChangeBus</c>). Чистое правило
/// без Avalonia: главное окно снимает состояние частей экрана в <see cref="Screen"/>, получает
/// набор шагов и выполняет их (<c>MainWindow.ApplyLocalChanges</c>). Вынесено из окна ради
/// тестов (<c>RefreshPlanTests</c>): именно здесь сидит риск регрессии — часть, которая сама
/// правила, не должна перечитываться заново; ввод не должен затираться; скрытое не должно
/// трогаться; открытая таблица должна отличать структуру от строк.
///
/// Правило одно: часть экрана перечитывает данные, если читала их РАНЬШЕ коммита
/// (<see cref="IsStale"/>). Отметки ставят сами части — при чтении и после своей правки, уже
/// отраженной на месте, — поэтому автор правки в план не попадает.
/// </summary>
public static class RefreshPlan
{
    /// <summary>Часть экрана читала данные раньше коммита — ей пора перечитать. Равные отметки
    /// и нулевой коммит (коммитов еще не было) — не устарела.</summary>
    public static bool IsStale(long readStamp, long commitStamp) => readStamp < commitStamp;

    [Flags]
    public enum Steps
    {
        None               = 0,
        CalendarSidebar    = 1 << 0,
        TasksSidebar       = 1 << 1,
        ConstructorSidebar = 1 << 2,
        Calendar           = 1 << 3,   // видимый календарь: SetActiveIds → RefreshCurrent
        Tasks              = 1 << 4,   // видимые задачи: SetActiveIds(force) — тихо
        ConstructorRows    = 1 << 5,   // открытая таблица: строки на месте
        ConstructorFull    = 1 << 6,   // открытая таблица: полная пересборка (структура)
        RecordPanel        = 1 << 7,   // открытая панель записи: ReloadFromDb
        Retry              = 1 << 8,   // идет ввод — зайти снова позже с тем же коммитом
        LibrarySidebar     = 1 << 9,
        Library            = 1 << 10,  // видимая библиотека: SetActiveIds(force) — тихо
        RecordPanelBacklinks = 1 << 11, // блок ссылок открытой панели записи: RefreshBacklinks
    }

    /// <summary>Снимок частей экрана на момент применения коммита ОДНОЙ базы.</summary>
    /// <param name="CommitStamp">Момент коммита (<c>ChangeBus.Now()</c> при его получении).</param>
    /// <param name="Structural">Затронуты группы/коллекции/поля.</param>
    /// <param name="ActiveModule">Показанный раздел: <c>calendar</c>, <c>tasks</c>, <c>constructor</c>, …</param>
    /// <param name="ConstructorShowsDb">Таблица конструктора видна и показывает коллекцию этой базы.</param>
    /// <param name="CellEditing">В таблице открыт редактор ячейки.</param>
    /// <param name="RecordPanelShowsDb">Панель записи открыта и показывает запись этой базы.</param>
    /// <param name="RecordPanelFocused">Фокус ввода в панели записи — человек может печатать.</param>
    /// <param name="RecordPanelBacklinkSource">Коммит этой базы может изменить блок «Ссылаются на эту
    /// запись» открытой панели: это база записи или прочитанная блоком другая база.</param>
    public sealed record Screen(
        long   CommitStamp,
        bool   Structural,
        string ActiveModule,
        long   CalendarSidebarStamp,
        long   TasksSidebarStamp,
        long   ConstructorSidebarStamp,
        long   LibrarySidebarStamp,
        long   CalendarStamp,
        long   TasksStamp,
        long   LibraryStamp,
        bool   ConstructorShowsDb,
        long   ConstructorStamp,
        bool   CellEditing,
        bool   RecordPanelShowsDb,
        long   RecordPanelStamp,
        bool   RecordPanelFocused,
        bool   RecordPanelBacklinkSource = false,
        long   RecordPanelBacklinksStamp = 0);

    public static Steps After(Screen s)
    {
        var steps = Steps.None;
        var stamp = s.CommitStamp;

        // Группы/коллекции/поля — деревья сайдбаров. Свой раздел их уже перестроил: отметка новее.
        if (s.Structural)
        {
            if (IsStale(s.CalendarSidebarStamp,    stamp)) steps |= Steps.CalendarSidebar;
            if (IsStale(s.TasksSidebarStamp,       stamp)) steps |= Steps.TasksSidebar;
            if (IsStale(s.ConstructorSidebarStamp, stamp)) steps |= Steps.ConstructorSidebar;
            if (IsStale(s.LibrarySidebarStamp,     stamp)) steps |= Steps.LibrarySidebar;
        }

        // Видимый агрегатный раздел: календарь и задачи собирают данные всех допущенных баз.
        // Скрытый не трогаем — он перечитает при показе (ShowModule).
        if (s.ActiveModule == "calendar" && IsStale(s.CalendarStamp, stamp)) steps |= Steps.Calendar;
        if (s.ActiveModule == "tasks"    && IsStale(s.TasksStamp,    stamp)) steps |= Steps.Tasks;
        if (s.ActiveModule == "library"  && IsStale(s.LibraryStamp,  stamp)) steps |= Steps.Library;

        // Открытая таблица этой базы: структура → полная пересборка, записи → строки на месте.
        // Открыт редактор ячейки — ввод не затираем, зайдем позже.
        if (s.ConstructorShowsDb && IsStale(s.ConstructorStamp, stamp))
        {
            if (s.CellEditing)     steps |= Steps.Retry;
            else if (s.Structural) steps |= Steps.ConstructorFull;
            else                   steps |= Steps.ConstructorRows;
        }

        // Открытая панель записи этой базы: фокус в ней — человек может печатать, зайдем позже.
        if (s.RecordPanelShowsDb && IsStale(s.RecordPanelStamp, stamp))
        {
            if (s.RecordPanelFocused) steps |= Steps.Retry;
            else                      steps |= Steps.RecordPanel;
        }

        // Ссылки на запись панели: коммит в базе-источнике — своей или другой подключенной.
        // Перечитанная целиком панель читает и их. Повтора не нужно: ответ, пришедший во время
        // ввода, панель сама применит, когда редактор освободится.
        if (s.RecordPanelBacklinkSource && IsStale(s.RecordPanelBacklinksStamp, stamp) && !steps.Has(Steps.RecordPanel))
            steps |= Steps.RecordPanelBacklinks;

        return steps;
    }

    public static bool Has(this Steps steps, Steps step) => (steps & step) != 0;
}
