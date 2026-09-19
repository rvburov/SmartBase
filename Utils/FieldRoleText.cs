using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Utils;

/// <summary>
/// Человеческие названия ролей полей — общий источник для всех мест, где привязка к разделу
/// показывается пользователю: плашка «привязано к разделу» в окне свойства, окно подготовки
/// коллекции при создании записи и предупреждение при удалении столбца. Тексты живут здесь,
/// а не в трех местах, чтобы «Статус» везде назывался статусом задачи одинаково.
/// </summary>
public static class FieldRoleText
{
    public const string SectionCalendar = "Календарь";
    public const string SectionTasks    = "Задачи";
    public const string SectionLibrary  = "Библиотека документов";

    /// <summary>Чем столбец служит разделу: «статус задачи», «описание записи», …</summary>
    public static string Title(FieldRole role) => role switch
    {
        FieldRole.TaskExec     => "исполнитель задачи",
        FieldRole.TaskAssigner => "назначивший задачу",
        FieldRole.TaskStatus   => "статус задачи",
        FieldRole.CalendarDate => "дата события",
        FieldRole.DocFile      => "документ библиотеки",
        FieldRole.DocType      => "тип документа",
        _                      => "описание записи",
    };

    /// <summary>
    /// Роли поля и разделы, которые их читают. <paramref name="hasTasks"/> — есть ли в коллекции
    /// поле-исполнитель: без него раздел задач не показывает НИ ОДНОЙ записи коллекции, и
    /// «Назначил»/«Статус» читать некому. Флаги при этом остаются на полях — вернули исполнителя,
    /// и прежняя разметка ожила сама (заводить столбцы заново не нужно), — но заявлять привязку,
    /// пока раздел молчит, нельзя: пользователь удалял исполнителя именно чтобы ее снять.
    /// <paramref name="hasCalendar"/> — то же для календаря: без видимого поля-даты коллекции в
    /// нем нет, и «Описание» там никто не читает.
    ///
    /// Роль на несовместимом типе не считается вовсе (<see cref="ConstructorRepository.RoleAllowsType"/>):
    /// текстовый «Статус» списка статусов не дает, и плашка о нем молчит — ровно так же, как
    /// молчит сам раздел.
    /// </summary>
    public static (IReadOnlyList<string> Sections, IReadOnlyList<string> Roles) Of(
        DbField f, bool hasTasks, bool hasCalendar = true, bool hasLibrary = true)
    {
        var roles    = new List<string>();
        var sections = new List<string>();

        void Add(string what, params string[] where)
        {
            roles.Add(what);
            foreach (var s in where) if (!sections.Contains(s)) sections.Add(s);
        }

        bool Carries(FieldRole role, bool flag) =>
            flag && ConstructorRepository.RoleAllowsType(role, f.FieldType);

        // «Дата» и «Описание» — общие для двух разделов: перечисляем только те, что реально читают.
        string[] shared = (hasCalendar, hasTasks) switch
        {
            (true,  true)  => [SectionCalendar, SectionTasks],
            (true,  false) => [SectionCalendar],
            (false, true)  => [SectionTasks],
            _              => [],
        };

        if (Carries(FieldRole.CalendarDate, f.IsCalStart) && shared.Length > 0)
            Add(hasCalendar && hasTasks ? "дата события и срок задачи"
              : hasTasks                ? "срок задачи"
              :                           "дата события", shared);
        if (Carries(FieldRole.TaskExec,     f.IsTaskExec))               Add(Title(FieldRole.TaskExec),     SectionTasks);
        if (Carries(FieldRole.TaskAssigner, f.IsTaskAssigner) && hasTasks) Add(Title(FieldRole.TaskAssigner), SectionTasks);
        if (Carries(FieldRole.TaskStatus,   f.IsTaskStatus)   && hasTasks) Add(Title(FieldRole.TaskStatus),   SectionTasks);
        if (Carries(FieldRole.Description,  f.IsDescription)  && shared.Length > 0)
            Add(Title(FieldRole.Description), shared);
        // Файловый столбец — несущая роль библиотеки (как исполнитель у задач), поэтому о нем
        // говорим всегда. «Тип документа» читает только коллекция с таким столбцом.
        if (Carries(FieldRole.DocFile, f.IsDocFile))                 Add(Title(FieldRole.DocFile), SectionLibrary);
        if (Carries(FieldRole.DocType, f.IsDocType) && hasLibrary)   Add(Title(FieldRole.DocType), SectionLibrary);

        return (sections, roles);
    }

    /// <summary>
    /// «к разделу «Задачи» — статус задачи» либо null, если ролей у поля нет. Готовая середина
    /// фразы: вызывающий сам решает, «это свойство привязано …» или ««Статус» привязан …».
    /// </summary>
    public static string? Binding(DbField f, bool hasTasks, bool hasCalendar = true, bool hasLibrary = true)
    {
        var (sections, roles) = Of(f, hasTasks, hasCalendar, hasLibrary);
        if (roles.Count == 0) return null;

        var where = sections.Count > 1
            ? $"разделам {JoinRu(sections.Select(s => $"«{s}»"))}"
            : $"разделу «{sections[0]}»";
        return $"к {where} — {JoinRu(roles)}";
    }

    /// <summary>Что потеряется вместе со столбцом. null — роли нет, терять нечего.</summary>
    public static string? DeletionImpact(DbField f, bool hasTasks = true, bool hasCalendar = true,
                                         bool hasLibrary = true)
    {
        var losses = RoleLosses(EffectiveRoles(f, hasTasks, hasCalendar, hasLibrary), deleting: true);
        return losses.Count == 0 ? null : JoinRu(losses);
    }

    /// <summary>
    /// Что потеряется при смене типа свойства. Для раздела это то же самое, что удаление
    /// столбца — данные ему станут недоступны, — поэтому и формулировки те же; отличается
    /// только «Описание»: его тексты остаются в столбце, просто раздел перестает их читать.
    /// </summary>
    public static string TypeChangeImpact(IReadOnlyList<FieldRole> lost) =>
        JoinRu(RoleLosses(lost, deleting: false));

    // Роли, которые поле реально несет: флаг + подходящий тип + раздел, который их читает.
    private static List<FieldRole> EffectiveRoles(DbField f, bool hasTasks, bool hasCalendar,
                                                  bool hasLibrary = true)
    {
        var roles = new List<FieldRole>();
        bool Carries(FieldRole role, bool flag) =>
            flag && ConstructorRepository.RoleAllowsType(role, f.FieldType);

        if (Carries(FieldRole.CalendarDate, f.IsCalStart)   && (hasCalendar || hasTasks)) roles.Add(FieldRole.CalendarDate);
        if (Carries(FieldRole.TaskExec,     f.IsTaskExec))                                roles.Add(FieldRole.TaskExec);
        if (Carries(FieldRole.TaskAssigner, f.IsTaskAssigner) && hasTasks)                roles.Add(FieldRole.TaskAssigner);
        if (Carries(FieldRole.TaskStatus,   f.IsTaskStatus)   && hasTasks)                roles.Add(FieldRole.TaskStatus);
        if (Carries(FieldRole.Description,  f.IsDescription)  && (hasCalendar || hasTasks)) roles.Add(FieldRole.Description);
        if (Carries(FieldRole.DocFile,      f.IsDocFile))                                 roles.Add(FieldRole.DocFile);
        if (Carries(FieldRole.DocType,      f.IsDocType)      && hasLibrary)              roles.Add(FieldRole.DocType);
        return roles;
    }

    private static List<string> RoleLosses(IReadOnlyList<FieldRole> roles, bool deleting)
    {
        var losses = new List<string>();
        // Исполнитель — несущая роль: задача это «запись × user-поле с флагом», без него
        // раздел не покажет НИ ОДНОЙ записи коллекции, даже с заполненными статусами.
        if (roles.Contains(FieldRole.TaskExec))     losses.Add("записи коллекции перестанут отображаться в задачах");
        if (roles.Contains(FieldRole.CalendarDate)) losses.Add("события пропадут из календаря, а у задач исчезнет срок");
        if (roles.Contains(FieldRole.TaskAssigner)) losses.Add("раздел задач потеряет, кто ставил задачу");
        if (roles.Contains(FieldRole.TaskStatus))   losses.Add("раздел задач потеряет статусы");
        if (roles.Contains(FieldRole.Description))
            losses.Add(deleting ? "тексты описаний удалятся вместе со столбцом"
                                : "описания перестанут показываться в календаре и задачах");
        // Файловый столбец — несущая роль библиотеки: без него документов этой записи в разделе
        // не будет. Но ролей таких у коллекции бывает несколько, поэтому говорим про столбец,
        // а не про всю коллекцию (в отличие от исполнителя задач).
        if (roles.Contains(FieldRole.DocFile))
            losses.Add(deleting ? "документы этого столбца пропадут из библиотеки, а ссылки на файлы удалятся"
                                : "документы этого столбца пропадут из библиотеки");
        if (roles.Contains(FieldRole.DocType)) losses.Add("библиотека документов потеряет типы документов");
        return losses;
    }

    /// <summary>«а», «а и б», «а, б и в» — перечисление по-русски.</summary>
    public static string JoinRu(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        if (list.Count <= 1) return list.Count == 1 ? list[0] : "";
        return $"{string.Join(", ", list.Take(list.Count - 1))} и {list[^1]}";
    }
}
