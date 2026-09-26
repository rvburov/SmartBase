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
    /// Появляется в планах подготовки коллекции (<c>PlanTaskSetup</c>), где исполнителя еще нет.
    /// </summary>
    TaskExec,
    /// <summary>
    /// «Файл» — file-поле, чьи документы показывает библиотека (<c>IsDocFile</c>). В
    /// <see cref="ConstructorRepository.DocRoles"/> не входит: это само поле с тумблером.
    /// Как и «Дата», роль НЕ эксклюзивна — файловых столбцов в коллекции может быть несколько,
    /// и каждый дает разделу свои документы.
    /// </summary>
    DocFile,
    /// <summary>«Тип документа» — поле-список библиотеки (<c>IsDocType</c>).</summary>
    DocType,
}

/// <summary>Существующий столбец, который подойдет под роль, потому что так и называется.</summary>
public sealed record RoleCandidate(FieldRole Role, string RoleName, int FieldId, string FieldName);

/// <summary>
/// Что произойдет при разметке ролей коллекции: <see cref="Candidates"/> — столбцы, чьи имена
/// совпали с ролями (их предлагается подтянуть, чтобы данные поехали в раздел),
/// <see cref="FieldsToCreateIfDeclined"/> — имена полей, которые заведутся при отказе от
/// подтягивания, <see cref="FieldsToCreateIfAdopted"/> — при согласии (роли, которым не нашлось
/// ни носителя, ни одноименного столбца). Пустой список кандидатов = спрашивать про
/// подтягивание не о чем, разметка идет молча.
/// </summary>
public sealed record RoleAdoptionPlan(IReadOnlyList<RoleCandidate> Candidates,
                                      IReadOnlyList<string> FieldsToCreateIfDeclined,
                                      IReadOnlyList<string> FieldsToCreateIfAdopted)
{
    public bool NeedsConfirmation => Candidates.Count > 0;

    /// <summary>
    /// Изменит ли согласие состав или разметку коллекции. Для создания записи из раздела это
    /// и есть повод спросить: ничего не меняется — окно не нужно, запись создается молча.
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
    /// <summary>Роли, которые обеспечивает включенный раздел задач (исполнитель — само поле).</summary>
    public static readonly FieldRole[] TaskRoles =
        [FieldRole.TaskAssigner, FieldRole.TaskStatus, FieldRole.CalendarDate, FieldRole.Description];

    /// <summary>Все роли — для обходов «что несет это поле» (плашка, смена типа, починка).</summary>
    public static readonly FieldRole[] AllRoles =
        [FieldRole.CalendarDate, FieldRole.TaskExec, FieldRole.TaskAssigner,
         FieldRole.TaskStatus, FieldRole.Description, FieldRole.DocFile, FieldRole.DocType];

    /// <summary>Роли, которые обеспечивает поле-дата, включенное в календарь (дата — само поле).</summary>
    public static readonly FieldRole[] CalendarRoles = [FieldRole.Description];

    /// <summary>
    /// Роли, которые обеспечивает включенный тумблер библиотеки документов (сам файл — это поле
    /// с тумблером, как исполнитель у задач). «Описание» сюда не входит: карточка библиотеки
    /// показывает не описание записи, а найденный внутри документа фрагмент.
    /// </summary>
    public static readonly FieldRole[] DocRoles = [FieldRole.DocType];

    /// <summary>Имя роли — оно же имя по умолчанию для авто-созданного поля.</summary>
    public static string RoleName(FieldRole role) => role switch
    {
        FieldRole.TaskAssigner => FieldAssigner,
        FieldRole.TaskStatus   => FieldStatus,
        FieldRole.CalendarDate => FieldDate,
        FieldRole.TaskExec     => FieldExecutor,
        FieldRole.DocFile      => FieldFile,
        FieldRole.DocType      => FieldDocType,
        _                      => FieldDescription,
    };

    /// <summary>
    /// Тип свойства, без которого роль работать не может. Раньше тип проверялся только у «Даты»
    /// и «Исполнителя» (их читают физически типизированные пути — <c>calendar_index</c> и
    /// «запись × user-поле»), а «Статус»/«Назначил»/«Описание» роль несли при любом типе. На
    /// практике это давало молчаливо сломанную привязку: столбец «Статус» типа «Текст» показывал
    /// плашку раздела, но списка статусов в нем не было и выбрать «Выполнено» было негде.
    /// Теперь правило одно на все роли: <b>тип = роль</b>. Оно же ограничивает подтягивание по
    /// имени (<see cref="RoleCandidateField"/>) — иначе окно снова предложило бы текстовый
    /// «Статус», и пользователь получил бы ровно то состояние, которое чинит смена типа.
    /// </summary>
    public static bool RoleAllowsType(FieldRole role, string fieldType) => role switch
    {
        FieldRole.CalendarDate => fieldType == "date",
        FieldRole.TaskExec     => fieldType == "user",
        FieldRole.TaskAssigner => fieldType == "user",
        FieldRole.TaskStatus   => fieldType is "status" or "select",
        FieldRole.DocFile      => fieldType == "file",
        // Та же поблажка, что у «Статуса»: «Список» и «Статус» — одно семейство виджетов,
        // и одноименный столбец любого из них показывает библиотеке ровно то, что нужно.
        FieldRole.DocType      => fieldType is "select" or "status",
        _                      => fieldType == "text",
    };

    /// <summary>Человеческое перечисление типов, при которых роль работает — для окон и ошибок.</summary>
    public static string RoleTypeNames(FieldRole role) => role switch
    {
        FieldRole.CalendarDate => "«Дата»",
        FieldRole.TaskExec     => "«Пользователь»",
        FieldRole.TaskAssigner => "«Пользователь»",
        FieldRole.TaskStatus   => "«Список» или «Статус»",
        FieldRole.DocFile      => "«Файл»",
        FieldRole.DocType      => "«Список» или «Статус»",
        _                      => "«Текст»",
    };

    /// <summary>
    /// Значения, которыми засевается список роли; null — роль засевать нечем и не нужно.
    ///
    /// Не нужно — это про «Тип документа»: три стадии задачи одинаковы у всех и составляют сам
    /// смысл раздела, а виды документов у каждого свои. Любой предложенный набор пришлось бы
    /// сперва вычистить, а забытый пункт разошелся бы по фильтру раздела.
    /// </summary>
    private static IReadOnlyList<string>? RoleDefaultOptions(FieldRole role) => role switch
    {
        FieldRole.TaskStatus => TaskStatusOptions,
        _                    => null,
    };

    private static bool RoleFlag(DbFieldEntity f, FieldRole role) => role switch
    {
        FieldRole.TaskAssigner => f.IsTaskAssigner,
        FieldRole.TaskStatus   => f.IsTaskStatus,
        FieldRole.CalendarDate => f.IsCalStart,
        FieldRole.TaskExec     => f.IsTaskExec,
        FieldRole.DocFile      => f.IsDocFile,
        FieldRole.DocType      => f.IsDocType,
        _                      => f.IsDescription,
    };

    // Носитель роли — флаг ВМЕСТЕ с типом: роль на неподходящем типе не работает, поэтому и
    // носителем считаться не должна (иначе роль выглядела бы занятой, а данные в раздел не шли).
    private static bool HasRole(DbFieldEntity f, FieldRole role) =>
        RoleFlag(f, role) && RoleAllowsType(role, f.FieldType);

    private static void SetRole(DbFieldEntity f, FieldRole role, bool on)
    {
        switch (role)
        {
            case FieldRole.TaskAssigner: f.IsTaskAssigner = on; break;
            case FieldRole.TaskStatus:   f.IsTaskStatus   = on; break;
            case FieldRole.CalendarDate: f.IsCalStart     = on; break;
            case FieldRole.TaskExec:     f.IsTaskExec     = on; break;
            case FieldRole.DocFile:      f.IsDocFile      = on; break;
            case FieldRole.DocType:      f.IsDocType      = on; break;
            default:                     f.IsDescription  = on; break;
        }
    }

    // Роли «Дата», «Исполнитель» и «Файл» не эксклюзивны: несколько полей-дат дают свои события,
    // несколько user-полей — свои задачи, несколько file-полей — свои документы. Остальные роли —
    // ровно одна на коллекцию, поэтому при переезде снимаются с прежнего носителя.
    private static bool IsExclusiveRole(FieldRole role) =>
        role is not (FieldRole.CalendarDate or FieldRole.TaskExec or FieldRole.DocFile);

    // Поле уже занято ДРУГОЙ ролью — вторую не вешаем: оба значения писались бы в один
    // FieldId и затирали друг друга (тот же дефект, что чинит RepairTaskFieldRoles).
    private static bool CarriesOtherRole(DbFieldEntity f, FieldRole role) =>
        (role != FieldRole.TaskAssigner && f.IsTaskAssigner) ||
        (role != FieldRole.TaskStatus   && f.IsTaskStatus)   ||
        (role != FieldRole.Description  && f.IsDescription)  ||
        (role != FieldRole.TaskExec     && f.IsTaskExec)     ||
        (role != FieldRole.DocFile      && f.IsDocFile)      ||
        (role != FieldRole.DocType      && f.IsDocType);

    private List<DbFieldEntity> CollectionFields(int collectionId) =>
        RoleOrder(db.DbFields.Where(f => f.CollectionId == collectionId).ToList()).ToList();

    // Носитель роли (первый по RoleOrder — то же правило, что и у читателей раздела).
    private static DbFieldEntity? RoleHolder(List<DbFieldEntity> fields, FieldRole role) =>
        fields.FirstOrDefault(f => HasRole(f, role));

    // Столбец, который называется как роль и может ее принять: имя (регистр не важен) ПЛЮС
    // подходящий тип (RoleAllowsType). Тип обязателен для всех ролей — подтянутый столбец
    // неподходящего типа дал бы неработающую привязку: «Статус» без списка статусов, «Дата»
    // без срока.
    private static DbFieldEntity? RoleCandidateField(List<DbFieldEntity> fields, FieldRole role, int excludeFieldId) =>
        fields.FirstOrDefault(f =>
            f.Id != excludeFieldId &&
            IsRoleName(f.Name, RoleName(role)) &&
            RoleAllowsType(role, f.FieldType) &&
            !HasRole(f, role) &&
            !CarriesOtherRole(f, role));

    /// <summary>
    /// Что даст разметка ролей: какие существующие столбцы можно подтянуть по имени и какие
    /// поля появятся, если от подтягивания отказаться. Ничего не меняет — только считает.
    /// <paramref name="excludeFieldId"/> — поле, на котором сейчас включают тумблер (оно уже
    /// несет свою роль: исполнитель у задач, дата у календаря).
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
    /// коллекции — да, ПЕРЕНОС задачи в постороннюю — нет (там заводится свое «Исполнитель»,
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
    // типа «Дата») либо ЛЮБОЕ поле-дата коллекции — включенное или нет. Тот же выбор, что
    // делает ResolveCalendarDateField перед записью: выключенной дате поднимут тумблер, новое
    // поле не заведется, — значит и план обещать его не должен.
    private int? ExistingCalendarDateField(int collectionId, int preferredFieldId)
    {
        if (preferredFieldId > 0 &&
            db.DbFields.Any(f => f.Id == preferredFieldId && f.CollectionId == collectionId &&
                                 f.FieldType == "date"))
            return preferredFieldId;
        return CalendarDateFieldId(collectionId)
            ?? db.DbFields.Where(f => f.CollectionId == collectionId && f.FieldType == "date")
                          .OrderBy(f => f.Position).ThenBy(f => f.Id)
                          .Select(f => (int?)f.Id)
                          .FirstOrDefault();
    }

    /// <summary>
    /// Доводит набор ролей коллекции до полного. <paramref name="adoptByName"/> = true —
    /// одноименные столбцы получают роль (данные из них поедут в раздел), false — они
    /// остаются нетронутыми, а для ролей заводятся отдельные поля. Роль без носителя
    /// создается в любом случае: раздел без «Статуса» или без срока работать не должен.
    /// Возвращает true, если состав или разметка полей изменились.
    ///
    /// Права спрашиваем ЗДЕСЬ, до первой записи: пометка чужого поля ролью и правка его
    /// опций — изменение схемы коллекции, а приходят сюда двумя путями (тумблер в диалоге
    /// свойства и «между делом» из AddTaskRecord, где проверен только CreateRecord). Отказ
    /// не должен оставить коллекцию наполовину размеченной, поэтому все — одной транзакцией.
    /// </summary>
    public bool EnsureRoles(int collectionId, IReadOnlyList<FieldRole> roles,
                            bool adoptByName, int excludeFieldId = 0)
    {
        // Обычно разметка уже полная (каждая правка события зовет сюда за «Описанием»): решаем по
        // выборке без отслеживания, а отслеживаемые поля грузим, только если есть что менять. На
        // коллекции в тысячу свойств отслеживание всех полей стоило 20–30 мс на каждый перенос.
        var probe = RoleOrder(db.DbFields.AsNoTracking().Where(f => f.CollectionId == collectionId).ToList()).ToList();
        if (roles.All(role => (adoptByName ? RoleCandidateField(probe, role, excludeFieldId) : null) is null
                              && RoleHolder(probe, role) is not null))
            return false;

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

        // Своя транзакция, если разметку зовут напрямую (тумблер роли в диалоге свойства);
        // при вызове «между делом» из AddTaskRecord — присоединяемся к его транзакции,
        // чтобы создание задачи было ОДНИМ коммитом, а не полутора десятком (см. BeginWrite).
        using var scope = BeginWrite();

        foreach (var (field, role) in adopt)
        {
            // Эксклюзивную роль снимаем с прежнего носителя: пользователь именно
            // переключает раздел на другой столбец (мог переименовать поля местами).
            if (IsExclusiveRole(role))
                foreach (var other in fields.Where(f => f.Id != field.Id && HasRole(f, role)))
                    SetRole(other, role, false);

            SetRole(field, role, true);
            // Одноименный список роли дополняем ее значениями по умолчанию («Статус» задачи —
            // тремя статусами, «Тип документа» — набором типов): подтянутый пустой список не
            // дал бы выбрать ничего, и привязка выглядела бы сломанной. Поле другого типа
            // (имя решает, тип нет) остается со своим конфигом.
            if (RoleDefaultOptions(role) is { } defaults && field.FieldType is "select" or "status")
                field.ConfigJson = MergeSelectOptions(field.ConfigJson, defaults);

            Log("update", "field", field.Name, CollectionNameOf(collectionId));
        }
        if (adopt.Count > 0) db.SaveChanges();

        foreach (var role in create) AddField(NewRoleField(collectionId, role));

        scope.Commit();
        return true;
    }

    /// <summary>
    /// Роли, которые поле ПОТЕРЯЕТ при смене типа на <paramref name="newType"/>. Смена типа —
    /// то же самое для раздела, что удаление столбца: читать данные ему станет неоткуда,
    /// поэтому UI обязан предупредить теми же словами, что и удаление, а не менять тип молча
    /// (окно свойства → <c>ConfirmRoleLossOnTypeChange</c>). Пустой список — терять нечего.
    /// </summary>
    public IReadOnlyList<FieldRole> RolesLostByTypeChange(int fieldId, string newType)
    {
        var f = db.DbFields.AsNoTracking().FirstOrDefault(x => x.Id == fieldId);
        if (f is null || f.FieldType == newType) return [];
        return AllRoles.Where(r => HasRole(f, r) && !RoleAllowsType(r, newType)).ToList();
    }

    // Снимает флаги ролей, которые новый тип поля не поддерживает. Зовется из UpdateField ПЕРЕД
    // записью: без этого флаг оставался жить на несовместимом типе — раздел данные не брал, а
    // окно свойства продолжало показывать плашку «привязано к разделу» (роль-призрак).
    private static void ClearRolesIncompatibleWithType(DbFieldEntity entity, string newType)
    {
        foreach (var role in AllRoles)
            if (RoleFlag(entity, role) && !RoleAllowsType(role, newType))
                SetRole(entity, role, false);
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
            FieldRole.DocFile      => "file",
            FieldRole.DocType      => "select",
            _                      => "text",
        },
        // «Тип документа» заводится ПУСТЫМ списком — пункты заводит человек (см. RoleDefaultOptions).
        ConfigJson     = role == FieldRole.TaskStatus ? TaskStatusConfigJson : "{}",
        IsTaskAssigner = role == FieldRole.TaskAssigner,
        IsTaskStatus   = role == FieldRole.TaskStatus,
        IsTaskExec     = role == FieldRole.TaskExec,
        IsCalStart     = role == FieldRole.CalendarDate,
        IsDescription  = role == FieldRole.Description,
        IsDocFile      = role == FieldRole.DocFile,
        IsDocType      = role == FieldRole.DocType,
        // Поле-дата раздела остается системным (как было у авто-«Даты»), остальные роли —
        // обычные поля: их можно переименовать, сменить тип и удалить.
        IsSystem       = role == FieldRole.CalendarDate,
    };
}
