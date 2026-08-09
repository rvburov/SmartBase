using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Utils;

/// <summary>
/// Человеческие названия ролей полей — общий источник для всех мест, где привязка к разделу
/// показывается пользователю: плашка «привязано к разделу» в окне свойства, окно подготовки
/// коллекции при создании записи и предупреждение при удалении столбца. Тексты живут здесь,
/// а не в трёх местах, чтобы «Статус» везде назывался статусом задачи одинаково.
/// </summary>
public static class FieldRoleText
{
    public const string SectionCalendar = "Календарь";
    public const string SectionTasks    = "Задачи";

    /// <summary>Чем столбец служит разделу: «статус задачи», «описание записи», …</summary>
    public static string Title(FieldRole role) => role switch
    {
        FieldRole.TaskExec     => "исполнитель задачи",
        FieldRole.TaskAssigner => "назначивший задачу",
        FieldRole.TaskStatus   => "статус задачи",
        FieldRole.CalendarDate => "дата события",
        _                      => "описание записи",
    };

    /// <summary>
    /// Роли поля и разделы, которые их читают. <paramref name="hasTasks"/> — включён ли в
    /// коллекции раздел задач: тогда поле-дата ещё и срок задачи, а описание читают оба раздела.
    /// </summary>
    public static (IReadOnlyList<string> Sections, IReadOnlyList<string> Roles) Of(DbField f, bool hasTasks)
    {
        var roles    = new List<string>();
        var sections = new List<string>();

        void Add(string what, params string[] where)
        {
            roles.Add(what);
            foreach (var s in where) if (!sections.Contains(s)) sections.Add(s);
        }

        string[] calendarSections = hasTasks ? [SectionCalendar, SectionTasks] : [SectionCalendar];

        if (f.IsCalStart)
            Add(hasTasks ? "дата события и срок задачи" : "дата события", calendarSections);
        if (f.IsTaskExec)      Add(Title(FieldRole.TaskExec),     SectionTasks);
        if (f.IsTaskAssigner)  Add(Title(FieldRole.TaskAssigner), SectionTasks);
        if (f.IsTaskStatus)    Add(Title(FieldRole.TaskStatus),   SectionTasks);
        if (f.IsDescription)   Add(Title(FieldRole.Description),  calendarSections);

        return (sections, roles);
    }

    /// <summary>
    /// «к разделу «Задачи» — статус задачи» либо null, если ролей у поля нет. Готовая середина
    /// фразы: вызывающий сам решает, «это свойство привязано …» или ««Статус» привязан …».
    /// </summary>
    public static string? Binding(DbField f, bool hasTasks)
    {
        var (sections, roles) = Of(f, hasTasks);
        if (roles.Count == 0) return null;

        var where = sections.Count > 1
            ? $"разделам {JoinRu(sections.Select(s => $"«{s}»"))}"
            : $"разделу «{sections[0]}»";
        return $"к {where} — {JoinRu(roles)}";
    }

    /// <summary>Что потеряется вместе со столбцом. null — роли нет, терять нечего.</summary>
    public static string? DeletionImpact(DbField f)
    {
        var losses = new List<string>();
        // Исполнитель — несущая роль: задача это «запись × user-поле с флагом», без него
        // раздел не покажет НИ ОДНОЙ записи коллекции, даже с заполненными статусами.
        if (f.IsTaskExec)     losses.Add("записи коллекции перестанут отображаться в задачах");
        if (f.IsCalStart)     losses.Add("события пропадут из календаря, а у задач исчезнет срок");
        if (f.IsTaskAssigner) losses.Add("раздел задач потеряет, кто ставил задачу");
        if (f.IsTaskStatus)   losses.Add("раздел задач потеряет статусы");
        if (f.IsDescription)  losses.Add("тексты описаний удалятся вместе со столбцом");

        return losses.Count == 0 ? null : JoinRu(losses);
    }

    /// <summary>«а», «а и б», «а, б и в» — перечисление по-русски.</summary>
    public static string JoinRu(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        if (list.Count <= 1) return list.Count == 1 ? list[0] : "";
        return $"{string.Join(", ", list.Take(list.Count - 1))} и {list[^1]}";
    }
}
