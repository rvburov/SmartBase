using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

/// <summary>Роль поля, которую можно повесить на существующий столбец по совпадению имени.</summary>
public enum FieldRole
{
    /// <summary>«Назначил» — user-поле задачи (<c>IsTaskAssigner</c>).</summary>
    TaskAssigner,
    /// <summary>«Статус» — поле-список задачи (<c>IsTaskStatus</c>).</summary>
    TaskStatus,
    /// <summary>«Дата» — источник календаря и срок задачи (<c>IsCalStart</c>).</summary>
    CalendarDate,
    /// <summary>«Описание» — markdown-блок панели записи (<c>IsDescription</c>).</summary>
    Description,
    /// <summary>
    /// «Исполнитель» — user-поле, на котором держится задача (<c>IsTaskExec</c>). В
    /// <see cref="ConstructorRepository.TaskRoles"/> не входит: это само поле с тумблером.
    /// Появляется в планах подготовки коллекции (<c>PlanTaskSetup</c>), где исполнителя ещё нет.
    /// </summary>
    TaskExec,
}

/// <summary>Существующий столбец, который подойдёт под роль, потому что так и называется.</summary>
public sealed record RoleCandidate(FieldRole Role, string RoleName, int FieldId, string FieldName);

/// <summary>
/// Что произойдёт при разметке ролей коллекции: <see cref="Candidates"/> — столбцы, чьи имена
/// совпали с ролями (их предлагается подтянуть, чтобы данные поехали в раздел),
/// <see cref="FieldsToCreateIfDeclined"/> — имена полей, которые заведутся при отказе от
/// подтягивания, <see cref="FieldsToCreateIfAdopted"/> — при согласии (роли, которым не нашлось
/// ни носителя, ни одноимённого столбца). Пустой список кандидатов = спрашивать про
/// подтягивание не о чем, разметка идёт молча.
/// </summary>
public sealed record RoleAdoptionPlan(IReadOnlyList<RoleCandidate> Candidates,
                                      IReadOnlyList<string> FieldsToCreateIfDeclined,
                                      IReadOnlyList<string> FieldsToCreateIfAdopted)
{
    public bool NeedsConfirmation => Candidates.Count > 0;

    /// <summary>
    /// Изменит ли согласие состав или разметку коллекции. Для создания записи из раздела это
    /// и есть повод спросить: ничего не меняется — окно не нужно, запись создаётся молча.
    /// </summary>
    public bool ChangesCollection => Candidates.Count > 0 || FieldsToCreateIfAdopted.Count > 0;

    public static RoleAdoptionPlan Empty { get; } = new([], [], []);
}

// ── Роли полей: подтягивание по имени ─────────────────────────────────────────
//
// Раздел (задачи/календарь) держится не на именах, а на ФЛАГАХ полей. Но пользователь,
// заводя коллекцию руками, называет столбцы ровно так же — «Назначил», «Статус», «Дата»,
// «Описание». Раньше такие столбцы усыновлялись молча; теперь UI сначала показывает, что
// нашлось, и спрашивает: подтянуть их (данные поедут в раздел) или завести отдельные поля.
// Совпадение — ТОЛЬКО по имени (регистр не важен), тип столбца роли не мешает: исключение
// одно — «Дата», где календарная проекция физически строится по полям типа «Дата».
public partial class ConstructorRepository
{
    /// <summary>Роли, которые обеспечивает включённый раздел задач (исполнитель — само поле).</summary>
    public static readonly FieldRole[] TaskRoles =
        [FieldRole.TaskAssigner, FieldRole.TaskStatus, FieldRole.CalendarDate, FieldRole.Description];

    /// <summary>Роли, которые обеспечивает поле-дата, включённое в календарь (дата — само поле).</summary>
    public static readonly FieldRole[] CalendarRoles = [FieldRole.Description];

    /// <summary>Имя роли — оно же имя по умолчанию для авто-созданного поля.</summary>
    public static string RoleName(FieldRole role) => role switch
    {
        FieldRole.TaskAssigner => FieldAssigner,
        FieldRole.TaskStatus   => FieldStatus,
        FieldRole.CalendarDate => FieldDate,
        FieldRole.TaskExec     => FieldExecutor,
        _                      => FieldDescription,
    };

    // Носитель роли. У «Даты» флаг проверяется ВМЕСТЕ с типом: календарная проекция строится
    // только по полям типа «Дата», поэтому текстовое поле с оставшимся от прежнего типа
    // IsCalStart источником не считается (иначе роль выглядела бы занятой, а срок и события
    // не появлялись бы вовсе).
    private static bool HasRole(DbFieldEntity f, FieldRole role) => role switch
    {
        FieldRole.TaskAssigner => f.IsTaskAssigner,
        FieldRole.TaskStatus   => f.IsTaskStatus,
        FieldRole.CalendarDate => f.IsCalStart && f.FieldType == "date",
        FieldRole.TaskExec     => f.IsTaskExec && f.FieldType == "user",
        _                      => f.IsDescription,
    };

    private static void SetRole(DbFieldEntity f, FieldRole role, bool on)
    {
        switch (role)
        {
            case FieldRole.TaskAssigner: f.IsTaskAssigner = on; break;
            case FieldRole.TaskStatus:   f.IsTaskStatus   = on; break;
            case FieldRole.CalendarDate: f.IsCalStart     = on; break;
            case FieldRole.TaskExec:     f.IsTaskExec     = on; break;
            default:                     f.IsDescription  = on; break;
        }
    }

    // Роли «Дата» и «Исполнитель» не эксклюзивны: несколько полей-дат дают свои события,
    // несколько user-полей — свои задачи. Остальные роли — ровно одна на коллекцию, поэтому
    // при переезде снимаются с прежнего носителя.
    private static bool IsExclusiveRole(FieldRole role) =>
        role is not (FieldRole.CalendarDate or FieldRole.TaskExec);

    // Поле уже занято ДРУГОЙ ролью — вторую не вешаем: оба значения писались бы в один
    // FieldId и затирали друг друга (тот же запрет, что чинит FixDualRoleFields).
    private static bool CarriesOtherRole(DbFieldEntity f, FieldRole role) =>
        (role != FieldRole.TaskAssigner && f.IsTaskAssigner) ||
        (role != FieldRole.TaskStatus   && f.IsTaskStatus)   ||
        (role != FieldRole.Description  && f.IsDescription)  ||
        (role != FieldRole.TaskExec     && f.IsTaskExec);

    private List<DbFieldEntity> CollectionFields(int collectionId) =>
        RoleOrder(db.DbFields.Where(f => f.CollectionId == collectionId).ToList()).ToList();

    // Носитель роли (первый по RoleOrder — то же правило, что и у читателей раздела).
    private static DbFieldEntity? RoleHolder(List<DbFieldEntity> fields, FieldRole role) =>
        fields.FirstOrDefault(f => HasRole(f, role));

    // Столбец, который называется как роль и может её принять. Тип не важен — кроме «Даты» и
    // «Исполнителя»: calendar_index строится только по полям типа «Дата» (флаг на тексте дал бы
    // пустой срок), а задача = запись × user-поле (на тексте её просто не видно).
    private static DbFieldEntity? RoleCandidateField(List<DbFieldEntity> fields, FieldRole role, int excludeFieldId) =>
        fields.FirstOrDefault(f =>
            f.Id != excludeFieldId &&
            IsRoleName(f.Name, RoleName(role)) &&
            !HasRole(f, role) &&
            !CarriesOtherRole(f, role) &&
            (role != FieldRole.CalendarDate || f.FieldType == "date") &&
            (role != FieldRole.TaskExec     || f.FieldType == "user"));

    /// <summary>
    /// Что даст разметка ролей: какие существующие столбцы можно подтянуть по имени и какие
    /// поля появятся, если от подтягивания отказаться. Ничего не меняет — только считает.
    /// <paramref name="excludeFieldId"/> — поле, на котором сейчас включают тумблер (оно уже
    /// несёт свою роль: исполнитель у задач, дата у календаря).
    /// </summary>
    public RoleAdoptionPlan PlanRoles(int collectionId, IReadOnlyList<FieldRole> roles, int excludeFieldId = 0)
    {
        var fields          = CollectionFields(collectionId);
        var candidates      = new List<RoleCandidate>();
        var createDeclined  = new List<string>();
        var createAdopted   = new List<string>();

        foreach (var role in roles)
        {
            var holder    = RoleHolder(fields, role);
            var candidate = RoleCandidateField(fields, role, excludeFieldId);

            if (candidate is not null)
                candidates.Add(new RoleCandidate(role, RoleName(role), candidate.Id, candidate.Name));

            // При отказе роль без носителя заводится новым полем; имя занято — с суффиксом.
            if (holder is null) createDeclined.Add(FreeFieldName(collectionId, RoleName(role)));
            // При согласии новое поле нужно только там, где некого подтянуть.
            if (holder is null && candidate is null) createAdopted.Add(FreeFieldName(collectionId, RoleName(role)));
        }

        return candidates.Count == 0 && createDeclined.Count == 0
            ? RoleAdoptionPlan.Empty
            : new RoleAdoptionPlan(candidates, createDeclined, createAdopted);
    }

    /// <summary>
    /// Что изменится в коллекции, если создать в ней СОБЫТИЕ календаря: поле-дата (когда
    /// источника нет и в окне его не выбрали) и «Описание» — их заводит
    /// <see cref="AddCalendarRecord"/>. Ничего не меняет — только считает, чтобы UI успел
    /// спросить до записи.
    /// </summary>
    public RoleAdoptionPlan PlanCalendarSetup(int collectionId, int preferredDateFieldId = 0)
    {
        var roles = new List<FieldRole>();
        if (ExistingCalendarDateField(collectionId, preferredDateFieldId) is null)
            roles.Add(FieldRole.CalendarDate);
        roles.Add(FieldRole.Description);
        return PlanRoles(collectionId, roles);
    }

    /// <summary>
    /// Что изменится в коллекции, если создать в ней ЗАДАЧУ: исполнитель (существующее
    /// user-поле помечается ролью либо заводится «Исполнитель» — правило
    /// <see cref="EnsureTaskCollection"/>) плюс полный набор <see cref="TaskRoles"/>.
    /// Ничего не меняет — только считает.
    /// </summary>
    /// <param name="adoptExec">
    /// Можно ли отдать роль исполнителя существующему user-полю. Повторяет параметр
    /// <c>adoptExisting</c> у <see cref="EnsureTaskCollection"/>: создание задачи в открытой
    /// коллекции — да, ПЕРЕНОС задачи в постороннюю — нет (там заводится своё «Исполнитель»,
    /// иначе чужое user-поле залило бы раздел своими значениями).
    /// </param>
    public RoleAdoptionPlan PlanTaskSetup(int collectionId, bool adoptExec = true)
    {
        var fields = CollectionFields(collectionId);
        var exec   = fields.FirstOrDefault(f => f.FieldType == "user" && f.IsTaskExec);

        var candidates     = new List<RoleCandidate>();
        var createDeclined = new List<string>();
        var createAdopted  = new List<string>();
        var execId         = exec?.Id ?? 0;

        if (exec is null)
        {
            // Повторяет выбор EnsureTaskCollection: любое свободное user-поле (не чужая роль и
            // не «Назначил» по имени) становится исполнителем — план обязан это показать,
            // иначе окно обещало бы новое поле, а раздел забрал бы «Менеджера».
            var candidate = adoptExec
                ? fields.FirstOrDefault(f => f.FieldType == "user" && !f.IsTaskStatus &&
                                             !f.IsTaskAssigner && !IsRoleName(f.Name, FieldAssigner))
                : null;
            if (candidate is not null)
            {
                candidates.Add(new RoleCandidate(FieldRole.TaskExec, FieldExecutor, candidate.Id, candidate.Name));
                execId = candidate.Id;
            }
            else
            {
                var name = FreeFieldName(collectionId, FieldExecutor);
                createDeclined.Add(name);
                createAdopted.Add(name);
            }
        }

        var roles = PlanRoles(collectionId, TaskRoles, execId);
        return candidates.Count == 0 && createDeclined.Count == 0 && !roles.ChangesCollection &&
               roles.FieldsToCreateIfDeclined.Count == 0
            ? RoleAdoptionPlan.Empty
            : new RoleAdoptionPlan([..candidates, ..roles.Candidates],
                                   [..createDeclined, ..roles.FieldsToCreateIfDeclined],
                                   [..createAdopted,  ..roles.FieldsToCreateIfAdopted]);
    }

    // Поле-источник календаря, которое УЖЕ есть: выбранное в окне события (если оно живо и
    // типа «Дата») либо первое поле-дата коллекции, видимое в календаре. Тот же выбор, что
    // делает ResolveCalendarDateField перед записью.
    private int? ExistingCalendarDateField(int collectionId, int preferredFieldId)
    {
        if (preferredFieldId > 0 &&
            db.DbFields.Any(f => f.Id == preferredFieldId && f.CollectionId == collectionId &&
                                 f.FieldType == "date"))
            return preferredFieldId;
        return CalendarDateFieldId(collectionId);
    }

    /// <summary>
    /// Доводит набор ролей коллекции до полного. <paramref name="adoptByName"/> = true —
    /// одноимённые столбцы получают роль (данные из них поедут в раздел), false — они
    /// остаются нетронутыми, а для ролей заводятся отдельные поля. Роль без носителя
    /// создаётся в любом случае: раздел без «Статуса» или без срока работать не должен.
    /// Возвращает true, если состав или разметка полей изменились.
    ///
    /// Права спрашиваем ЗДЕСЬ, до первой записи: пометка чужого поля ролью и правка его
    /// опций — изменение схемы коллекции, а приходят сюда двумя путями (тумблер в диалоге
    /// свойства и «между делом» из AddTaskRecord, где проверен только CreateRecord). Отказ
    /// не должен оставить коллекцию наполовину размеченной, поэтому всё — одной транзакцией.
    /// </summary>
    public bool EnsureRoles(int collectionId, IReadOnlyList<FieldRole> roles,
                            bool adoptByName, int excludeFieldId = 0)
    {
        var fields  = CollectionFields(collectionId);
        var adopt   = new List<(DbFieldEntity Field, FieldRole Role)>();
        var create  = new List<FieldRole>();

        foreach (var role in roles)
        {
            var candidate = adoptByName ? RoleCandidateField(fields, role, excludeFieldId) : null;
            if (candidate is not null) { adopt.Add((candidate, role)); continue; }
            if (RoleHolder(fields, role) is null) create.Add(role);
        }

        if (adopt.Count == 0 && create.Count == 0) return false;

        if (adopt.Count   > 0) Demand(PermAction.EditField,   collectionId);
        if (create.Count  > 0) Demand(PermAction.CreateField, collectionId);

        using var tx = db.Database.BeginTransaction();
        try
        {
            foreach (var (field, role) in adopt)
            {
                // Эксклюзивную роль снимаем с прежнего носителя: пользователь именно
                // переключает раздел на другой столбец (мог переименовать поля местами).
                if (IsExclusiveRole(role))
                    foreach (var other in fields.Where(f => f.Id != field.Id && HasRole(f, role)))
                        SetRole(other, role, false);

                SetRole(field, role, true);
                // Одноимённый список-«Статус» дополняем тремя статусами; поле другого типа
                // (имя решает, тип нет) остаётся со своим конфигом.
                if (role == FieldRole.TaskStatus && field.FieldType is "select" or "status")
                    field.ConfigJson = MergeSelectOptions(field.ConfigJson, TaskStatusOptions);

                Log("update", "field", field.Name, CollectionNameOf(collectionId));
            }
            if (adopt.Count > 0) db.SaveChanges();

            foreach (var role in create) AddField(NewRoleField(collectionId, role));

            tx.Commit();
        }
        catch
        {
            // Откат вернул флаги в БД, но трекер держит их выставленными: следующее чтение
            // отдало бы «размеченную» роль, которой в базе нет. Сбрасываем — перечитается.
            tx.Rollback();
            db.ChangeTracker.Clear();
            throw;
        }
        return true;
    }

    // Новое поле под роль: тип и настройки предопределены самой ролью.
    private DbField NewRoleField(int collectionId, FieldRole role) => new()
    {
        CollectionId   = collectionId,
        Name           = FreeFieldName(collectionId, RoleName(role)),
        Position       = NextFieldPosition(collectionId),
        FieldType      = role switch
        {
            FieldRole.TaskAssigner => "user",
            FieldRole.TaskExec     => "user",
            FieldRole.TaskStatus   => "status",
            FieldRole.CalendarDate => "date",
            _                      => "text",
        },
        ConfigJson     = role == FieldRole.TaskStatus ? TaskStatusConfigJson : "{}",
        IsTaskAssigner = role == FieldRole.TaskAssigner,
        IsTaskStatus   = role == FieldRole.TaskStatus,
        IsTaskExec     = role == FieldRole.TaskExec,
        IsCalStart     = role == FieldRole.CalendarDate,
        IsDescription  = role == FieldRole.Description,
        // Поле-дата раздела остаётся системным (как было у авто-«Даты»), остальные роли —
        // обычные поля: их можно переименовать, сменить тип и удалить.
        IsSystem       = role == FieldRole.CalendarDate,
    };
}
