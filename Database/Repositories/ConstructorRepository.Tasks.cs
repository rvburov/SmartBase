using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Database.Repositories;

// ── Раздел «Задачи» ───────────────────────────────────────────────────────────
// Полный аналог календаря, где источник события — date-поле с IsCalStart: здесь
// источник задачи — user-поле с IsTaskExec (исполнитель). Задача = пара
// (RecordId, exec-FieldId): запись видна в разделе раз на каждое включенное
// user-поле с непустым значением. Роли «Назначил» (user, IsTaskAssigner) и
// «Статус» (select, IsTaskStatus) — обычные поля коллекции, авто-создаются при
// первом включении задач. Срок задачи НЕ настраивается отдельно — берется из
// календарных дат записи (первая по началу строка calendar_index видимого поля).
public partial class ConstructorRepository
{
    public const string TaskStatusPlanned = "Запланировано";
    public const string TaskStatusActive  = "В работе";
    public const string TaskStatusDone    = "Выполнено";
    public static readonly string[] TaskStatusOptions = [TaskStatusPlanned, TaskStatusActive, TaskStatusDone];

    private const string FieldExecutor = "Исполнитель";
    private const string FieldAssigner = "Назначил";
    private const string FieldStatus   = "Статус";

    // Имена ролей сравниваются без учета регистра — как занятые имена в FreeFieldName.
    // Иначе «назначил» с маленькой буквы считался бы обычным полем: его взяли бы в
    // исполнители, а разметка ролей все равно нашла бы его и добавила вторую роль.
    private static bool IsRoleName(string? name, string role) =>
        string.Equals(name, role, StringComparison.OrdinalIgnoreCase);

    private static readonly string TaskStatusConfigJson =
        JsonSerializer.Serialize(new { options = TaskStatusOptions });

    // Роль ищется ПО ФЛАГУ, но флаг может оказаться сразу у двух полей коллекции
    // (дубль коллекции, ручная правка, старые базы). Без явного порядка SQLite волен
    // отдать любую строку, и раздел показывает то одно поле, то другое — а запись
    // значения и его чтение могут разойтись по разным полям. Правило одно на весь
    // модуль: «первое по (Position, Id)». Ровно его повторяет ORDER BY в подзапросах
    // GetTaskRecords — иначе прочитали бы не то поле, в которое писали.
    private static IQueryable<DbFieldEntity> RoleOrder(IQueryable<DbFieldEntity> fields) =>
        fields.OrderBy(f => f.Position).ThenBy(f => f.Id);

    private static IEnumerable<DbFieldEntity> RoleOrder(IEnumerable<DbFieldEntity> fields) =>
        fields.OrderBy(f => f.Position).ThenBy(f => f.Id);

    // ── Включение задач ───────────────────────────────────────────────────────

    /// <summary>
    /// Включает/выключает участие user-поля в разделе задач (флаг <c>IsTaskExec</c>).
    /// Исполнитель у коллекции ОДИН: пока тумблер стоит на одном user-поле, включить второе
    /// нельзя (см. <see cref="TaskExecFieldId"/>). Включение — read-time фильтр (никаких
    /// проекций), плюс коллекция доводится до полного набора ролей задачи: «Назначил», «Статус»,
    /// «Дата» (срок), «Описание». <paramref name="adoptByName"/> = false — одноименные столбцы не
    /// трогаем и заводим для ролей отдельные поля (ответ пользователя в окне подтверждения; план
    /// для него считает <see cref="PlanRoles"/>). Возвращает true, если состав или разметка
    /// полей коллекции изменились.
    /// </summary>
    public bool SetUserFieldTaskExec(int fieldId, bool enabled, bool adoptByName = true)
    {
        Demand(PermAction.EditField, CollectionOfField(fieldId));
        var field = db.DbFields.Find(fieldId);
        if (field is null || field.FieldType != "user" || field.IsTaskExec == enabled) return false;

        // Второй исполнитель коллекции запрещен. Раньше их могло быть сколько угодно, и каждая
        // запись давала карточку на КАЖДОЕ включенное поле: один и тот же пункт списка дублировался
        // столько раз, сколько в коллекции user-полей, а «своей» карточки у записи не было.
        // Одно поле — один список дел; чтобы сменить носителя, тумблер сначала снимают со старого.
        if (enabled && TaskExecFieldId(field.CollectionId) is int busy && busy != fieldId)
            throw new InvalidOperationException(
                $"В коллекции уже есть свойство-исполнитель — «{FieldName(busy)}». " +
                "Исполнитель может быть только один: снимите переключатель с него, " +
                "чтобы назначить исполнителем это свойство.");

        // Чужая роль задач исполнителем быть не может: два значения писались бы в один
        // FieldId и затирали друг друга. «Назначил» отсекаем и по флагу, и по имени —
        // у еще не размеченного поля флага нет, но разметка ролей найдет его
        // ПО ИМЕНИ и сделает назначившим. «Статус» — только по флагу: по имени его
        // усыновляют лишь среди select/status-полей, а сюда доходят одни user-поля,
        // так что флаг у user-поля бывает только от ручной пометки или копирования.
        if (enabled && (field.IsTaskAssigner || IsRoleName(field.Name, FieldAssigner)))
            throw new InvalidOperationException(
                $"Свойство «{field.Name}» — это роль назначившего задачу, исполнителем оно быть не может. " +
                "Заведите для исполнителя отдельное свойство типа «Пользователь».");
        if (enabled && field.IsTaskStatus)
            throw new InvalidOperationException(
                $"Свойство «{field.Name}» — это роль статуса задачи, исполнителем оно быть не может. " +
                "Заведите для исполнителя отдельное свойство типа «Пользователь».");

        // Авто-поля потребуют права создания — проверяем ДО записи флага, чтобы
        // отказ не оставил коллекцию наполовину включенной.
        if (enabled && TaskRolesIncomplete(field.CollectionId, fieldId))
            Demand(PermAction.CreateField, field.CollectionId);

        field.IsTaskExec = enabled;
        Log("update", "field", field.Name, CollectionNameOf(field.CollectionId));
        db.SaveChanges();

        return enabled && EnsureTaskRoles(field.CollectionId, fieldId, adoptByName);
    }

    /// <summary>
    /// Поле-исполнитель коллекции (null — задачи для нее не включены). Исполнитель один на
    /// коллекцию; порядок <see cref="RoleOrder"/> нужен старым базам, где их успело накопиться
    /// несколько, — до того, как их починит <see cref="RepairTaskFieldRoles"/>.
    /// </summary>
    public int? TaskExecFieldId(int collectionId) =>
        RoleOrder(db.DbFields.Where(f => f.CollectionId == collectionId &&
                                         f.FieldType == "user" && f.IsTaskExec))
        .Select(f => (int?)f.Id).FirstOrDefault();

    /// <summary>Есть ли у коллекции включенное поле-исполнитель (участвует в задачах).</summary>
    public bool HasTaskExecField(int collectionId) => TaskExecFieldId(collectionId) is not null;

    // ── Стадии ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Стадии задач раздела — пункты поля «Статус» (роль <c>IsTaskStatus</c>, тип
    /// <c>status</c>/<c>select</c>) всех коллекций с включенными задачами, объединенные без
    /// повторов в порядке полей. Семантика <paramref name="activeIds"/> — как везде в разделе:
    /// null = все коллекции базы, ПУСТОЙ набор = ни одной. Коллекция без исполнителя стадий не
    /// дает: ее записей в разделе нет. Поле-статус коллекции берется по <see cref="RoleOrder"/>,
    /// как и при чтении задач, — иначе список стадий и значения карточек разошлись бы по полям.
    /// </summary>
    public List<string> GetTaskStages(ISet<int>? activeIds = null)
    {
        if (activeIds is { Count: 0 }) return [];

        var withExec = db.DbFields.AsNoTracking()
            .Where(f => f.FieldType == "user" && f.IsTaskExec)
            .Select(f => f.CollectionId)
            .Distinct()
            .ToHashSet();

        var statusFields = RoleOrder(db.DbFields.AsNoTracking()
                .Where(f => f.IsTaskStatus && (f.FieldType == "status" || f.FieldType == "select")))
            .Select(f => new { f.CollectionId, f.ConfigJson })
            .ToList();

        var result = new List<string>();
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in statusFields.GroupBy(f => f.CollectionId))
        {
            if (!withExec.Contains(group.Key)) continue;
            if (activeIds is not null && !activeIds.Contains(group.Key)) continue;
            foreach (var option in ReadOptions(group.First().ConfigJson))
                if (seen.Add(option)) result.Add(option);
        }
        return result;
    }

    /// <summary>Пункты поля «Статус» одной коллекции — для окна задачи. Поля нет или у него
    /// нет пунктов — три стандартные стадии (их и заведет <c>EnsureTaskCollection</c>).</summary>
    public List<string> GetTaskStageOptions(int collectionId)
    {
        var config = RoleOrder(db.DbFields.AsNoTracking()
                .Where(f => f.CollectionId == collectionId && f.IsTaskStatus &&
                            (f.FieldType == "status" || f.FieldType == "select")))
            .Select(f => f.ConfigJson)
            .FirstOrDefault();
        var options = ReadOptions(config);
        return options.Count > 0 ? options : [.. TaskStatusOptions];
    }

    /// <summary>Имя свойства по id ("" — свойства нет) — для окон, называющих носителя роли.</summary>
    public string FieldName(int fieldId) =>
        db.DbFields.Where(f => f.Id == fieldId).Select(f => f.Name).FirstOrDefault() ?? "";

    /// <summary>
    /// Гарантирует коллекции полный набор полей задач: включенное user-поле «исполнитель»
    /// (существующее помечается, иначе создается «Исполнитель») + «Назначил»/«Статус».
    /// Возвращает id exec-поля. Для быстрого добавления задач в коллекцию без задач.
    /// </summary>
    /// <param name="adoptExisting">
    /// Разрешено ли «усыновить» под исполнителя уже существующее user-поле коллекции.
    /// Так работает включение задач в коллекции, которую пользователь для этого и открыл
    /// (быстрое добавление задачи). Но для ПЕРЕНОСА задачи в постороннюю коллекцию это
    /// недопустимо: там свое user-поле («Менеджер», «Согласующий») с сотнями заполненных
    /// значений молча стало бы полем задач и залило раздел чужими карточками —
    /// в таком случае передается false и заводится собственное поле «Исполнитель».
    /// </param>
    public int EnsureTaskCollection(int collectionId, bool adoptExisting = true)
    {
        // Поля коллекции — в память: имена ролей сравниваются БЕЗ учета регистра
        // (как в FreeFieldName), а SQL-сравнение строк регистрозависимо и мимо кириллицы
        // lower() в SQLite все равно не работает. Порядок — RoleOrder: при двух полях
        // с одним флагом берем то же самое, что и остальной модуль.
        var fields = RoleOrder(db.DbFields.Where(f => f.CollectionId == collectionId).ToList()).ToList();

        var exec = fields.FirstOrDefault(f => f.FieldType == "user" && f.IsTaskExec);
        if (exec is not null)
        {
            EnsureTaskRoles(collectionId, exec.Id, adoptByName: true);
            return exec.Id;
        }

        // Помечаем существующее user-поле (кроме чужих ролей) либо создаем «Исполнитель».
        // Роль «Назначил» отсекаем И по флагу, И по имени: у поля, которое пользователь
        // завел руками до включения задач, флага еще нет, а ниже разметка ролей
        // найдет его ПО ИМЕНИ и пометит ролью — одно поле получило бы обе роли, и
        // AddTaskRecord затер бы исполнителя назначившим (оба пишутся в один FieldId).
        // «Статус» на user-поле — только по флагу (ручная пометка/копирование), тот же
        // запрет, что и в SetUserFieldTaskExec.
        var candidate = adoptExisting
            ? fields.FirstOrDefault(f => f.FieldType == "user" && !f.IsTaskStatus &&
                                         !f.IsTaskAssigner && !IsRoleName(f.Name, FieldAssigner))
            : null;
        if (candidate is not null)
        {
            Demand(PermAction.EditField, collectionId);
            if (TaskRolesIncomplete(collectionId, candidate.Id))
                Demand(PermAction.CreateField, collectionId);
            candidate.IsTaskExec = true;
            Log("update", "field", candidate.Name, CollectionNameOf(collectionId));
            db.SaveChanges();
            EnsureTaskRoles(collectionId, candidate.Id, adoptByName: true);
            return candidate.Id;
        }

        var created = AddField(new DbField
        {
            CollectionId = collectionId,
            Name         = FreeFieldName(collectionId, FieldExecutor),
            FieldType    = "user",
            Position     = NextFieldPosition(collectionId),
            IsTaskExec   = true,
        });
        EnsureTaskRoles(collectionId, created.Id!.Value, adoptByName: true);
        return created.Id!.Value;
    }

    // Набор ролей задач неполон — значит включение потребует создания полей (проверка прав
    // ДО записи флага исполнителя, чтобы отказ не оставил коллекцию наполовину включенной).
    private bool TaskRolesIncomplete(int collectionId, int execFieldId) =>
        PlanRoles(collectionId, TaskRoles, execFieldId).FieldsToCreateIfDeclined.Count > 0;

    // Доводит коллекцию до полного набора ролей задачи: «Назначил», «Статус», «Дата» (срок)
    // и «Описание». Исполнитель — само поле с тумблером, поэтому исключается из кандидатов.
    // adoptByName=false — одноименные столбцы не трогаем, роли получают отдельные поля
    // (пользователь так ответил в окне подтверждения). Возвращает true, если что-то изменилось.
    private bool EnsureTaskRoles(int collectionId, int execFieldId, bool adoptByName) =>
        EnsureRoles(collectionId, TaskRoles, adoptByName, execFieldId);

    /// <summary>
    /// Разовая починка разметки ролей при монтировании — три дефекта, которые запреты нового кода
    /// сами по себе не лечат (база уже в таком состоянии, а пути включения уходят в ранний возврат):
    ///
    /// 1. <b>Две роли на одном поле.</b> Наследие баз, открытых до запрета: роль «Назначил»
    ///    усыновлялась исполнителем, оба значения писались в один <c>FieldId</c> и затирали друг
    ///    друга. Правило починки — «исполнитель остается, чужая роль снимается»: раздел построен
    ///    вокруг exec-поля, и его значения пользователь видит именно как исполнителя. Затертых
    ///    исполнителей не вернуть — их перезаписали значения второй роли.
    /// 2. <b>Несколько исполнителей в коллекции.</b> Раньше это было штатно (каждое поле давало
    ///    свои карточки), теперь исполнитель один: оставляем первого по <see cref="RoleOrder"/>,
    ///    с остальных снимаем флаг. Значения в столбцах не трогаем — это обычные user-поля.
    /// 3. <b>Роль на несовместимом типе.</b> Текстовый «Статус», чекбокс-«Описание»: раздел такие
    ///    данные не читает (<see cref="RoleAllowsType"/>), но флаг продолжал висеть и показывать
    ///    плашку «привязано к разделу» над неработающей привязкой.
    ///
    /// Освободившиеся роли заводятся заново обычным путем (<see cref="EnsureTaskRoles"/>) — только
    /// в коллекциях, где остался исполнитель: без него раздел все равно молчит, и плодить там
    /// «Статус 2» не за чем.
    ///
    /// Возвращает число вылеченных коллекций. Монтирование не срывает: у пользователя без
    /// прав на схему починка просто не происходит (ее сделает следующий, у кого права есть).
    /// </summary>
    public int RepairTaskFieldRoles()
    {
        // Быстрый путь обычной базы — один запрос, ни одной записи. Роль на несовместимом типе
        // ловится сравнением с допустимыми типами прямо в SQL (список короткий и статичный).
        var suspects = db.DbFields
            .Where(f => (f.IsTaskExec && (f.IsTaskAssigner || f.IsTaskStatus)) ||
                        (f.IsTaskExec     && f.FieldType != "user") ||
                        (f.IsTaskAssigner && f.FieldType != "user") ||
                        (f.IsTaskStatus   && f.FieldType != "status" && f.FieldType != "select") ||
                        (f.IsCalStart     && f.FieldType != "date") ||
                        (f.IsDescription  && f.FieldType != "text"))
            .Select(f => f.CollectionId)
            .Distinct()
            .ToList();

        // Коллекции с двумя и более исполнителями — отдельным запросом: по одному полю дефект
        // не виден, он в их количестве.
        var multiExec = db.DbFields
            .Where(f => f.IsTaskExec && f.FieldType == "user")
            .GroupBy(f => f.CollectionId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        var collections = suspects.Concat(multiExec).Distinct().ToList();
        if (collections.Count == 0) return 0;

        var healed = 0;
        foreach (var collectionId in collections)
        {
            try
            {
                var fields = CollectionFields(collectionId);

                // (2) Исполнитель — первый по RoleOrder; CollectionFields уже отсортирован.
                var keepExec = fields.FirstOrDefault(f => f.IsTaskExec && f.FieldType == "user");
                foreach (var f in fields)
                {
                    var changed = false;

                    if (f.IsTaskExec && !ReferenceEquals(f, keepExec)) { f.IsTaskExec = false; changed = true; }

                    // (1) Чужие роли задач на исполнителе.
                    if (ReferenceEquals(f, keepExec) && (f.IsTaskAssigner || f.IsTaskStatus))
                    {
                        f.IsTaskAssigner = false;
                        f.IsTaskStatus   = false;
                        changed = true;
                    }

                    // (3) Роли, которым не подходит тип поля.
                    foreach (var role in AllRoles)
                        if (RoleFlag(f, role) && !RoleAllowsType(role, f.FieldType))
                        {
                            SetRole(f, role, false);
                            changed = true;
                        }

                    if (changed) Log("update", "field", f.Name, CollectionNameOf(collectionId));
                }
                db.SaveChanges();

                // Освободившиеся роли заводятся заново — обычным путем, со своими правами
                // и транзакцией. Упадет по правам — коллекция остается без роли, но уже
                // БЕЗ поломанной: следующее действие с задачами доведет набор до полного.
                if (keepExec is not null)
                    EnsureTaskRoles(collectionId, keepExec.Id, adoptByName: true);
                healed++;
            }
            catch (Exception ex) when (ex is PermissionDeniedException or DbUpdateException)
            {
                db.ChangeTracker.Clear();   // несохраненные флаги не должны утечь в следующие чтения
            }
        }
        return healed;
    }

    private int NextFieldPosition(int collectionId) =>
        (db.DbFields.Where(f => f.CollectionId == collectionId)
                    .Select(f => (int?)f.Position).Max() ?? 0) + 1000;

    // Свободное имя поля: «Имя», занято — «Имя 2», «Имя 3», …
    private string FreeFieldName(int collectionId, string baseName)
    {
        var taken = db.DbFields.Where(f => f.CollectionId == collectionId)
                               .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{baseName} {i}"))
                return $"{baseName} {i}";
    }

    /// <summary>Пункты поля-списка из его ConfigJson (<c>{"options":[…]}</c>) в порядке поля;
    /// нечитаемый или пустой конфиг — пусто.</summary>
    public static List<string> ReadOptions(string? configJson)
    {
        var options = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson);
            if (doc.RootElement.TryGetProperty("options", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
                        options.Add(s);
        }
        catch (JsonException) { /* нечитаемый конфиг — как пустой */ }
        return options;
    }

    // Дополняет ConfigJson select-поля недостающими опциями (существующие — как есть;
    // нечитаемый конфиг просто перезаписывается опциями статусов).
    private static string MergeSelectOptions(string configJson, IReadOnlyList<string> required)
    {
        var options = ReadOptions(configJson);
        foreach (var r in required)
            if (!options.Contains(r))
                options.Add(r);
        return JsonSerializer.Serialize(new { options });
    }

    // ── Чтение задач ─────────────────────────────────────────────────────────

    /// <summary>
    /// Задачи по активным коллекциям (null — все коллекции базы, ПУСТОЙ набор — ни одной,
    /// как у календаря). Задачей считается КАЖДАЯ запись коллекции, у которой есть поле-исполнитель,
    /// — заполнено оно или нет. Раньше условие было `v.Value <> ''`, и запись без исполнителя в
    /// раздел не попадала: список молчал о делах, которые никому не розданы, — а именно их и нужно
    /// раздать. Пустые исполнитель и «Назначил» показываются прочерком (карточка), пустой срок —
    /// «без даты», как и прежде.
    ///
    /// Цена решения: раздел показывает ВСЕ записи коллекции с включенными задачами, поэтому крышка
    /// <c>limit</c> (1000 на базу) теперь упирается в размер коллекции, а не в число розданных дел.
    /// Чтение идет от db_records по покрывающему индексу (CollectionId, Position); значение
    /// исполнителя добирается LEFT JOIN'ом сиком по PK (RecordId, FieldId), «Назначил»/«Статус»/срок —
    /// коррелированными подзапросами, как и раньше. Сортировку и поиск делает контроллер
    /// (агрегат нескольких БД сортируется после слияния).
    /// </summary>
    public List<TaskRecord> GetTaskRecords(ISet<int>? activeIds = null, int limit = 1000)
    {
        var result = new List<TaskRecord>();
        if (activeIds is { Count: 0 }) return result;

        var colFilter = activeIds is null
            ? ""
            : $" AND f.CollectionId IN ({string.Join(",", activeIds)})";

        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            // Срок — одной строкой «FieldId|StartIso|EndIso|AllDay» (в ISO-датах «|» не
            // бывает): первая по началу календарная дата записи. Фильтр fd.IsCalStart = 1
            // обязателен — проекция полная, скрытая дата сроком быть не должна.
            // ORDER BY у ролей повторяет RoleOrder: при двух полях с одним флагом читаем
            // ровно то поле, в которое пишет TaskRoleFields (без порядка SQLite отдавал
            // произвольное — раздел показывал то одно значение, то другое).
            // Ведущая таблица — db_records (все записи коллекции), значение исполнителя
            // приезжает LEFT JOIN'ом: NULL/'' = задача без исполнителя, карточка ставит прочерк.
            cmd.CommandText = $@"
                SELECT r.Id, f.Id, f.CollectionId, c.Name, c.Color,
                       r.Title, {DescExpr}, v.Value,
                       (SELECT va.Value FROM db_field_values va
                          JOIN db_fields fa ON fa.Id = va.FieldId
                         WHERE va.RecordId = r.Id AND fa.CollectionId = f.CollectionId
                           AND fa.IsTaskAssigner = 1
                         ORDER BY fa.Position, fa.Id LIMIT 1),
                       (SELECT vs.Value FROM db_field_values vs
                          JOIN db_fields fs ON fs.Id = vs.FieldId
                         WHERE vs.RecordId = r.Id AND fs.CollectionId = f.CollectionId
                           AND fs.IsTaskStatus = 1
                         ORDER BY fs.Position, fs.Id LIMIT 1),
                       (SELECT ci.FieldId || '|' || ci.StartIso || '|' || ci.EndIso || '|' || ci.AllDay
                          FROM calendar_index ci
                          JOIN db_fields fd ON fd.Id = ci.FieldId
                         WHERE ci.RecordId = r.Id AND fd.IsCalStart = 1 AND fd.FieldType = 'date'
                         ORDER BY ci.StartIso LIMIT 1)
                FROM db_fields f
                JOIN db_collections c ON c.Id = f.CollectionId
                JOIN db_records r     ON r.CollectionId = f.CollectionId
                LEFT JOIN db_field_values v ON v.RecordId = r.Id AND v.FieldId = f.Id
                WHERE f.FieldType = 'user' AND f.IsTaskExec = 1{colFilter}
                ORDER BY f.CollectionId, r.Position, r.Id
                LIMIT {Math.Max(1, limit)}";

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) result.Add(ReadTaskRow(rdr));
        }
        finally { if (opened) conn.Close(); }
        return result;
    }

    /// <summary>Строка карточки из читателя общей проекции задач. Вынесено, потому что ту же
    /// проекцию читает постраничный путь (<see cref="GetTaskRecordsByIds"/>): разъехавшись,
    /// два режима раздела показывали бы разное.</summary>
    private static TaskRecord ReadTaskRow(System.Data.Common.DbDataReader rdr)
    {
        var t = new TaskRecord
        {
            Id             = rdr.GetInt32(0),
            ExecFieldId    = rdr.GetInt32(1),
            CollectionId   = rdr.GetInt32(2),
            CollectionName = rdr.IsDBNull(3) ? "" : rdr.GetString(3),
            Color          = rdr.IsDBNull(4) ? "#007AFF" : rdr.GetString(4),
            Title          = rdr.IsDBNull(5) ? "" : rdr.GetString(5),
            Description    = rdr.IsDBNull(6) ? "" : rdr.GetString(6),
            Executor       = rdr.IsDBNull(7) ? "" : rdr.GetString(7),
            Assigner       = rdr.IsDBNull(8) ? "" : rdr.GetString(8),
            Status         = rdr.IsDBNull(9) ? "" : rdr.GetString(9),
        };
        if (!rdr.IsDBNull(10)) ApplyDuePacked(t, rdr.GetString(10));
        return t;
    }

    private static void ApplyDuePacked(TaskRecord t, string packed)
    {
        var parts = packed.Split('|');
        if (parts.Length != 4) return;
        if (!int.TryParse(parts[0], out var fid)) return;
        if (!DateTime.TryParse(parts[1], out var s) || !DateTime.TryParse(parts[2], out var e)) return;
        t.DueFieldId = fid;
        t.DueStart   = s;
        t.DueEnd     = e;
        t.AllDay     = parts[3] == "1";
    }

    /// <summary>Фоновая загрузка задач на ОТДЕЛЬНОМ подключении (общий контекст однопоточный).</summary>
    public Task<List<TaskRecord>> LoadTasksAsync(ISet<int>? activeIds = null, int limit = 1000) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).GetTaskRecords(activeIds, limit);
        });

    /// <summary>
    /// Точный размер раздела по коллекциям: id коллекции → (всего задач, из них выполненных).
    /// Считается ПО БАЗЕ, а не по загруженному, поэтому не зависит от крышки чтения: заголовок
    /// группы обязан называть настоящее число дел коллекции, иначе он повторял бы то, что и так
    /// видно на экране, и молчал бы о недочитанном хвосте.
    ///
    /// Задача = запись коллекции с включенным полем-исполнителем (исполнитель один, см.
    /// <see cref="TaskExecFieldId"/>), поэтому «всего» — это обычный COUNT по индексу
    /// db_records(CollectionId, Position). Выполненные добираются JOIN'ом по PK
    /// (RecordId, FieldId) к ОДНОМУ полю-статусу коллекции — тому же, что читает
    /// <see cref="GetTaskRecords"/>: подзапрос повторяет <see cref="RoleOrder"/>, иначе на старой
    /// базе с двумя статусами счетчик спорил бы с чипами карточек. Коррелирован он только по
    /// коллекции (не по записи), поэтому это одна дешевая выборка на группу, а не на строку.
    /// </summary>
    public Dictionary<int, (int Total, int Done)> CountTasksByCollection(ISet<int>? activeIds = null)
    {
        var result = new Dictionary<int, (int Total, int Done)>();
        if (activeIds is { Count: 0 }) return result;

        // Задачные коллекции и их поля-статусы — через EF: db_fields мала, а знать это заранее
        // важно. Раньше поле-статус искал коррелированный подзапрос ВНУТРИ счета, и SQLite
        // выполнял его на КАЖДУЮ запись — на базе в полмиллиона это полмиллиона лишних выборок
        // по db_fields, то есть секунды на ровном месте при каждом открытии раздела и каждой
        // правке задачи. Теперь связь «коллекция → ее статус» приезжает готовой картой.
        var statusByCollection = RoleOrder(db.DbFields.Where(f => f.IsTaskStatus))
            .Select(f => new { f.CollectionId, f.Id })
            .ToList()
            .GroupBy(x => x.CollectionId)
            .ToDictionary(g => g.Key, g => g.First().Id);

        var taskCollections = db.DbFields
            .Where(f => f.FieldType == "user" && f.IsTaskExec)
            .Select(f => f.CollectionId)
            .Distinct()
            .ToList();
        if (activeIds is not null) taskCollections.RemoveAll(c => !activeIds.Contains(c));
        if (taskCollections.Count == 0) return result;

        // Поле-статус подставляется прямо в условие соединения: CASE по коллекции строки. Это
        // сик по PK (RecordId, FieldId), а не поиск поля. Коллекции без статуса достается −1 —
        // совпадений нет, выполненных ноль.
        var statusCase = string.Join(" ", taskCollections
            .Where(statusByCollection.ContainsKey)
            .Select(c => $"WHEN {c} THEN {statusByCollection[c]}"));
        var statusExpr = statusCase.Length > 0 ? $"CASE r.CollectionId {statusCase} ELSE -1 END" : "-1";

        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT r.CollectionId, COUNT(*),
                       SUM(CASE WHEN vs.Value = @done THEN 1 ELSE 0 END)
                FROM db_records r
                LEFT JOIN db_field_values vs
                       ON vs.RecordId = r.Id AND vs.FieldId = {statusExpr}
                WHERE r.CollectionId IN ({string.Join(",", taskCollections)})
                GROUP BY r.CollectionId";
            AddParam(cmd, "@done").Value = TaskStatusDone;

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                result[rdr.GetInt32(0)] = (rdr.GetInt32(1), rdr.IsDBNull(2) ? 0 : rdr.GetInt32(2));
        }
        finally { if (opened) conn.Close(); }
        return result;
    }

    /// <summary>Фоновый <see cref="CountTasksByCollection"/> на ОТДЕЛЬНОМ подключении: идет рядом
    /// с чтением задач, а общий контекст однопоточный.</summary>
    public Task<Dictionary<int, (int Total, int Done)>> CountTasksByCollectionAsync(ISet<int>? activeIds = null) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).CountTasksByCollection(activeIds);
        });

    // ── CRUD задач ───────────────────────────────────────────────────────────

    /// <summary>
    /// Создает задачу: с датой — через календарный путь (шапка + поле-дата + calendar_index),
    /// без даты — обычная запись. Затем проставляет исполнителя/назначившего/статус.
    /// Коллекция при необходимости включается в задачи (EnsureTaskCollection);
    /// null — как в календаре, авто-коллекция «Без названия».
    /// </summary>
    public TaskRecord AddTaskRecord(int? collection, string title, string executor,
                                    string assigner, string status, DateValue? due)
    {
        Demand(PermAction.CreateRecord, collection);

        // Вся задача — ОДНОЙ транзакцией (см. BeginWrite). Раньше это были 9–11 независимых
        // коммитов (на первой задаче в коллекции — до 15–20), и каждый на сетевой папке
        // отдельно запирал файл: чужое чтение в любом из промежутков ловило SQLITE_BUSY.
        // Заодно задача перестала быть «наполовину созданной» при обрыве.
        using var scope = BeginWrite();

        var collectionId = collection ?? GetOrCreateDefaultCollection();
        var execFieldId  = EnsureTaskCollection(collectionId);

        int recordId;
        int? dueFieldId = null;
        if (due is { } d)
        {
            var cal = new CalendarRecord
            {
                CollectionId = collectionId, Title = title,
                StartDt = d.Start, EndDt = d.End, AllDay = d.AllDay,
            };
            AddCalendarRecord(cal);
            recordId   = cal.Id!.Value;
            dueFieldId = cal.FieldId;
        }
        else
        {
            var rec = AddRecord(new DbRecord
            {
                CollectionId = collectionId, Title = title,
                Position     = (db.DbRecords.Where(r => r.CollectionId == collectionId)
                                            .Select(r => (int?)r.Position).Max() ?? 0) + 1000,
            });
            recordId = rec.Id!.Value;
        }

        SetFieldValue(recordId, execFieldId, executor);
        var roles = TaskRoleFields(collectionId);
        if (roles.AssignerId is int aid) SetFieldValue(recordId, aid, assigner);
        if (roles.StatusId   is int sid) SetFieldValue(recordId, sid, status);

        scope.Commit();

        return new TaskRecord
        {
            Id = recordId, ExecFieldId = execFieldId, CollectionId = collectionId,
            CollectionName = CollectionNameOf(collectionId) ?? "",
            Title = title, Executor = executor, Assigner = assigner, Status = status,
            DueStart = due?.Start, DueEnd = due?.End, AllDay = due?.AllDay ?? false,
            DueFieldId = dueFieldId,
        };
    }

    /// <summary>
    /// Сохраняет правки задачи. С датой — календарный путь UpdateCalendarRecord (умеет и
    /// смену коллекции: Id/DueFieldId в t обновляются). Без даты — заголовок/описание
    /// напрямую + очистка прежнего поля-даты (смену коллекции без даты диалог не дает).
    /// Исполнитель/назначил/статус — обычные SetFieldValue (сами ведут журнал и индекс).
    /// </summary>
    public void UpdateTaskRecord(TaskRecord t)
    {
        if (t.Id is not int recordId) return;

        // Роли ЦЕЛИ готовим ДО переноса. Смена коллекции пересоздает запись календарным
        // путем (AddCalendarRecord + копия значений + удаление исходной, каждый со своим
        // SaveChanges и без общей транзакции). Если спросить права после него, отказ
        // застанет перенос уже закоммиченным: запись в цели, а исполнителя/назначившего
        // и статуса нет — задача исчезла из раздела вместе со значениями. Поля,
        // созданные заранее, при отказе остаются пустыми — это безобидно.
        // Только когда перенос ДЕЙСТВИТЕЛЬНО будет: коллекцию меняет календарный путь, а у
        // задачи без даты его нет (диалог гасит комбо, но выбранное значение все равно
        // приезжает в кандидате). Иначе запись осталась бы в своей коллекции, а исполнитель
        // ушел бы в поле чужой — ровно та порча, которую этот метод и чинит.
        int? targetExecFieldId = null;
        if (t.DueStart is not null &&
            t.CollectionId is int target && target != CollectionOfRecord(recordId))
            targetExecFieldId = EnsureTaskCollection(target, adoptExisting: false);

        if (t.DueStart is DateTime start)
        {
            var cal = new CalendarRecord
            {
                Id = recordId, CollectionId = t.CollectionId, FieldId = t.DueFieldId ?? 0,
                Title = t.Title, Description = t.Description,
                StartDt = start, EndDt = t.DueEnd ?? start, AllDay = t.AllDay,
            };
            UpdateCalendarRecord(cal);
            t.Id         = cal.Id;          // смена коллекции пересоздает запись
            t.DueFieldId = cal.FieldId;
            recordId     = cal.Id!.Value;
        }
        else
        {
            SetRecordTitle(recordId, t.Title);
            SetRecordDescription(recordId, t.Description);
            if (t.DueFieldId is int oldDateField)
            {
                SetFieldValue(recordId, oldDateField, "");   // срок убрали — чистим дату и индекс
                t.DueFieldId = null;
            }
        }

        var colId = CollectionOfRecord(recordId);
        if (colId is not int cid) return;

        // Смена коллекции пересоздала запись в цели, а t.ExecFieldId остался полем ИСХОДНОЙ
        // коллекции: значение легло бы на чужое поле (в таблице и панели записи такой ячейки
        // не видно — данные есть, но их не достать), а задача пропала бы из раздела, потому
        // что задача = «запись × ЕЕ user-поле с IsTaskExec». Берем exec-поле цели,
        // подготовленное выше; второй случай — exec-поле удалили, пока диалог был открыт
        // (CollectionOfField вернет null), тогда заводим исполнителя здесь.
        if (targetExecFieldId is int prepared)              t.ExecFieldId = prepared;
        else if (CollectionOfField(t.ExecFieldId) != cid)   t.ExecFieldId = EnsureTaskCollection(cid, adoptExisting: false);

        SetFieldValue(recordId, t.ExecFieldId, t.Executor);
        var roles = TaskRoleFields(cid);
        if (roles.AssignerId is int aid) SetFieldValue(recordId, aid, t.Assigner);
        if (roles.StatusId   is int sid) SetFieldValue(recordId, sid, t.Status);
    }

    /// <summary>
    /// Id полей-ролей задач коллекции («Назначил», «Статус») — по флагам, в порядке
    /// <see cref="RoleOrder"/> (первое по (Position, Id)).
    /// </summary>
    public (int? AssignerId, int? StatusId) TaskRoleFields(int collectionId)
    {
        int? assigner = RoleOrder(db.DbFields.Where(f => f.CollectionId == collectionId && f.IsTaskAssigner))
                        .Select(f => (int?)f.Id).FirstOrDefault();
        int? status   = RoleOrder(db.DbFields.Where(f => f.CollectionId == collectionId && f.IsTaskStatus))
                        .Select(f => (int?)f.Id).FirstOrDefault();
        return (assigner, status);
    }

    /// <summary>User-поля коллекции с признаком участия в задачах — для панели сайдбара.</summary>
    public List<(int Id, string Name, bool IsTaskExec)> GetUserFieldsWithTaskState(int collectionId) =>
        db.DbFields.Where(f => f.CollectionId == collectionId && f.FieldType == "user")
                   .OrderBy(f => f.Position)
                   .Select(f => new ValueTuple<int, string, bool>(f.Id, f.Name, f.IsTaskExec))
                   .ToList();
}
