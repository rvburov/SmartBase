using SmartBase.Database.Repositories;

namespace SmartBase.Utils;

/// <summary>Какая дата подставляется в строку быстрого добавления задачи.</summary>
public enum TaskQuickDue { None, Today, Tomorrow }

/// <summary>
/// Живые настройки раздела «Задачи» (по образцу <see cref="CalendarSettings"/>): кэш значений
/// из AppSettings + событие <see cref="Changed"/> — раздел слушает и перечитывается без
/// перезапуска. Set* сохраняют дельту и публикуют изменение. Мусор в хранилище (правка app.db
/// руками, откат версии) читается как стандарт.
///
/// О типах раздела показа (фильтр, правила) сервис не знает: он отдает примитивы, а
/// <c>TasksControllerView</c> собирает из них <c>TaskRules</c> и <c>TaskFilter</c> — так Utils
/// не тянет за собой Views.
/// </summary>
public static class TaskSettings
{
    /// <summary>Значение <see cref="StartFilterKey"/>: при входе ставится последний выбранный фильтр.</summary>
    public const string StartFilterLast = "Last";

    /// <summary>Допустимые значения стартового фильтра — ключи стандартных пунктов и «последний».</summary>
    public static readonly string[] StartFilterKeys =
        ["All", "Today", "AssignedToMe", "NoDue", "Overdue", StartFilterLast];

    /// <summary>Просрочка со следующего дня после срока (false — с момента срока).</summary>
    public static bool OverdueNextDay { get; private set; } = AppSettings.TasksOverdueNextDayDefault;

    /// <summary>Показывать выполненные во всех фильтрах (false — только в стадии «Выполнено»).</summary>
    public static bool ShowDone { get; private set; } = AppSettings.TasksShowDoneDefault;

    /// <summary>Ключ фильтра при входе в раздел (<see cref="StartFilterKeys"/>).</summary>
    public static string StartFilterKey { get; private set; } = AppSettings.TasksStartFilterDefault;

    /// <summary>Ключ последнего выбранного фильтра — состояние для режима «последний», не настройка.</summary>
    public static string LastFilterKey { get; private set; } = "";

    /// <summary>Исполнитель в строке быстрого добавления — текущий пользователь (false — не назначен).</summary>
    public static bool QuickExecutorMe { get; private set; } = AppSettings.TasksQuickExecutorMeDefault;

    /// <summary>Дата в строке быстрого добавления.</summary>
    public static TaskQuickDue QuickDue { get; private set; } = TaskQuickDue.None;

    /// <summary>Стадия новой задачи; у коллекции без такой стадии берется первая из ее списка.</summary>
    public static string DefaultStage { get; private set; } = AppSettings.TasksDefaultStageDefault;

    /// <summary>Настройки изменились — разделу пора перечитаться.</summary>
    public static event Action? Changed;

    /// <summary>Счетчик изменений — «догоняющая» перерисовка, как у CalendarSettings.</summary>
    public static long Stamp { get; private set; }

    /// <summary>Старт: считать сохраненные значения. Вызывать после AppSettings.AttachStore.</summary>
    public static void Init()
    {
        OverdueNextDay  = AppSettings.LoadTasksOverdueNextDay();
        ShowDone        = AppSettings.LoadTasksShowDone();
        StartFilterKey  = ParseStartFilter(AppSettings.LoadTasksStartFilter());
        LastFilterKey   = AppSettings.LoadTasksLastFilter();
        QuickExecutorMe = AppSettings.LoadTasksQuickExecutorMe();
        QuickDue        = ParseQuickDue(AppSettings.LoadTasksQuickDue());
        DefaultStage    = ParseStage(AppSettings.LoadTasksDefaultStage());
    }

    /// <summary>Ключ фильтра, который стоит поставить при входе: сама настройка либо, в режиме
    /// «последний», запомненный ключ (пустой — «Все задачи»).</summary>
    public static string InitialFilterKey =>
        StartFilterKey == StartFilterLast
            ? (LastFilterKey.Length > 0 ? LastFilterKey : "All")
            : StartFilterKey;

    /// <summary>Дата для строки быстрого добавления по настройке; null — без срока.</summary>
    public static DateTime? QuickDueDate(DateTime today) => QuickDue switch
    {
        TaskQuickDue.Today    => today.Date,
        TaskQuickDue.Tomorrow => today.Date.AddDays(1),
        _                     => null,
    };

    /// <summary>Запомнить выбранный фильтр (состояние для режима «последний»). Событие не
    /// поднимает: показ раздела от этого не меняется.</summary>
    public static void RememberFilter(string key)
    {
        if (key == LastFilterKey) return;
        LastFilterKey = key;
        AppSettings.SaveTasksLastFilter(key);
    }

    public static void SetOverdueNextDay(bool on)
    {
        if (on == OverdueNextDay) return;
        OverdueNextDay = on;
        AppSettings.SaveTasksOverdueNextDay(on);
        Publish();
    }

    public static void SetShowDone(bool on)
    {
        if (on == ShowDone) return;
        ShowDone = on;
        AppSettings.SaveTasksShowDone(on);
        Publish();
    }

    public static void SetStartFilter(string key)
    {
        key = ParseStartFilter(key);
        if (key == StartFilterKey) return;
        StartFilterKey = key;
        AppSettings.SaveTasksStartFilter(key);
        Publish();
    }

    public static void SetQuickExecutorMe(bool me)
    {
        if (me == QuickExecutorMe) return;
        QuickExecutorMe = me;
        AppSettings.SaveTasksQuickExecutorMe(me);
        Publish();
    }

    public static void SetQuickDue(TaskQuickDue due)
    {
        if (due == QuickDue) return;
        QuickDue = due;
        AppSettings.SaveTasksQuickDue(QuickDueKey(due));
        Publish();
    }

    public static void SetDefaultStage(string stage)
    {
        stage = ParseStage(stage);
        if (stage == DefaultStage) return;
        DefaultStage = stage;
        AppSettings.SaveTasksDefaultStage(stage);
        Publish();
    }

    private static void Publish()
    {
        Stamp++;
        Changed?.Invoke();
    }

    private static string ParseStartFilter(string? key) =>
        key is not null && Array.IndexOf(StartFilterKeys, key) >= 0 ? key : AppSettings.TasksStartFilterDefault;

    private static string ParseStage(string? stage) =>
        string.IsNullOrWhiteSpace(stage) ? ConstructorRepository.TaskStatusPlanned : stage.Trim();

    private static TaskQuickDue ParseQuickDue(string? key) => key switch
    {
        "today"    => TaskQuickDue.Today,
        "tomorrow" => TaskQuickDue.Tomorrow,
        _          => TaskQuickDue.None,
    };

    private static string QuickDueKey(TaskQuickDue due) => due switch
    {
        TaskQuickDue.Today    => "today",
        TaskQuickDue.Tomorrow => "tomorrow",
        _                     => "none",
    };
}
