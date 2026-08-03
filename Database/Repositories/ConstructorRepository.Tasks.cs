using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Database.Repositories;

// ── Раздел «Задачи» ───────────────────────────────────────────────────────────
// Полный аналог календаря, где источник события — date-поле с IsCalStart: здесь
// источник задачи — user-поле с IsTaskExec (исполнитель). Задача = пара
// (RecordId, exec-FieldId): запись видна в разделе раз на каждое включённое
// user-поле с непустым значением. Роли «Назначил» (user, IsTaskAssigner) и
// «Статус» (select, IsTaskStatus) — обычные поля коллекции, авто-создаются при
// первом включении задач. Срок задачи НЕ настраивается отдельно — берётся из
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

    // Имена ролей сравниваются без учёта регистра — как занятые имена в FreeFieldName.
    // Иначе «назначил» с маленькой буквы считался бы обычным полем: его взяли бы в
    // исполнители, а разметка ролей всё равно нашла бы его и добавила вторую роль.
    private static bool IsRoleName(string? name, string role) =>
        string.Equals(name, role, StringComparison.OrdinalIgnoreCase);

    private static readonly string TaskStatusConfigJson =
        JsonSerializer.Serialize(new { options = TaskStatusOptions });

    // ── Включение задач ───────────────────────────────────────────────────────

    /// <summary>
    /// Включает/выключает участие user-поля в разделе задач (флаг <c>IsTaskExec</c>).
    /// Несколько user-полей коллекции могут быть включены одновременно — каждое даёт
    /// свои задачи. Включение — read-time фильтр (никаких проекций), плюс при первом
    /// включении в коллекции авто-создаются недостающие поля «Назначил» и «Статус».
    /// Возвращает true, если состав полей коллекции изменился (создались авто-поля).
    /// </summary>
    public bool SetUserFieldTaskExec(int fieldId, bool enabled)
    {
        Demand(PermAction.EditField, CollectionOfField(fieldId));
        var field = db.DbFields.Find(fieldId);
        if (field is null || field.FieldType != "user" || field.IsTaskExec == enabled) return false;

        // Роль «Назначил» исполнителем быть не может: оба значения писались бы в один
        // FieldId и затирали друг друга. Отсекаем и помеченное флагом поле, и ещё не
        // размеченное — его EnsureTaskSupportFields найдёт ПО ИМЕНИ и сделает назначившим.
        if (enabled && (field.IsTaskAssigner || IsRoleName(field.Name, FieldAssigner)))
            throw new InvalidOperationException(
                $"Свойство «{field.Name}» — это роль назначившего задачу, исполнителем оно быть не может. " +
                "Заведите для исполнителя отдельное свойство типа «Пользователь».");

        // Авто-поля потребуют права создания — проверяем ДО записи флага, чтобы
        // отказ не оставил коллекцию наполовину включённой.
        if (enabled && TaskSupportFieldsMissing(field.CollectionId))
            Demand(PermAction.CreateField, field.CollectionId);

        field.IsTaskExec = enabled;
        Log("update", "field", field.Name, CollectionNameOf(field.CollectionId));
        db.SaveChanges();

        return enabled && EnsureTaskSupportFields(field.CollectionId);
    }

    /// <summary>Есть ли у коллекции включённое поле-исполнитель (участвует в задачах).</summary>
    public bool HasTaskExecField(int collectionId) =>
        db.DbFields.Any(f => f.CollectionId == collectionId && f.FieldType == "user" && f.IsTaskExec);

    /// <summary>
    /// Гарантирует коллекции полный набор полей задач: включённое user-поле «исполнитель»
    /// (существующее помечается, иначе создаётся «Исполнитель») + «Назначил»/«Статус».
    /// Возвращает id exec-поля. Для быстрого добавления задач в коллекцию без задач.
    /// </summary>
    /// <param name="adoptExisting">
    /// Разрешено ли «усыновить» под исполнителя уже существующее user-поле коллекции.
    /// Так работает включение задач в коллекции, которую пользователь для этого и открыл
    /// (быстрое добавление задачи). Но для ПЕРЕНОСА задачи в постороннюю коллекцию это
    /// недопустимо: там своё user-поле («Менеджер», «Согласующий») с сотнями заполненных
    /// значений молча стало бы полем задач и залило раздел чужими карточками —
    /// в таком случае передаётся false и заводится собственное поле «Исполнитель».
    /// </param>
    public int EnsureTaskCollection(int collectionId, bool adoptExisting = true)
    {
        // Поля коллекции — в память: имена ролей сравниваются БЕЗ учёта регистра
        // (как в FreeFieldName), а SQL-сравнение строк регистрозависимо и мимо кириллицы
        // lower() в SQLite всё равно не работает.
        var fields = db.DbFields.Where(f => f.CollectionId == collectionId).ToList();

        var exec = fields.FirstOrDefault(f => f.FieldType == "user" && f.IsTaskExec);
        if (exec is not null)
        {
            EnsureTaskSupportFields(collectionId);
            return exec.Id;
        }

        // Помечаем существующее user-поле (кроме «Назначил») либо создаём «Исполнитель».
        // Роль «Назначил» отсекаем И по флагу, И по имени: у поля, которое пользователь
        // завёл руками до включения задач, флага ещё нет, а ниже EnsureTaskSupportFields
        // найдёт его ПО ИМЕНИ и пометит ролью — одно поле получило бы обе роли, и
        // AddTaskRecord затёр бы исполнителя назначившим (оба пишутся в один FieldId).
        var candidate = adoptExisting
            ? fields.FirstOrDefault(f => f.FieldType == "user" &&
                                         !f.IsTaskAssigner && !IsRoleName(f.Name, FieldAssigner))
            : null;
        if (candidate is not null)
        {
            Demand(PermAction.EditField, collectionId);
            if (TaskSupportFieldsMissing(collectionId))
                Demand(PermAction.CreateField, collectionId);
            candidate.IsTaskExec = true;
            Log("update", "field", candidate.Name, CollectionNameOf(collectionId));
            db.SaveChanges();
            EnsureTaskSupportFields(collectionId);
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
        EnsureTaskSupportFields(collectionId);
        return created.Id!.Value;
    }

    private bool TaskSupportFieldsMissing(int collectionId) =>
        !db.DbFields.Any(f => f.CollectionId == collectionId && f.IsTaskAssigner) ||
        !db.DbFields.Any(f => f.CollectionId == collectionId && f.IsTaskStatus);

    // Авто-создаёт недостающие поля «Назначил» (user) и «Статус» (select с фиксированными
    // опциями). Роли ищутся ПО ФЛАГУ, не по имени; одноимённое поле подходящего типа
    // просто помечается флагом, неподходящего — имя новой роли получает суффикс « 2».
    // Возвращает true, если состав полей изменился (создание/пометка).
    //
    // Права спрашиваем ЗДЕСЬ, а не у вызывающих: пометка ЧУЖОГО поля ролью и правка его
    // опций — изменение схемы коллекции, а приходят сюда двумя путями (тумблер задач в
    // сайдбаре и «между делом» из AddTaskRecord, где проверен только CreateRecord). Обе
    // проверки идут ДО первой записи: отказ не должен оставить коллекцию наполовину
    // включённой. Пометка существующего поля = EditField, создание недостающего =
    // CreateField (AddField проверит его и сам, но нам нужен отказ ДО правки чужих опций).
    private bool EnsureTaskSupportFields(int collectionId)
    {
        var needAssigner = !db.DbFields.Any(f => f.CollectionId == collectionId && f.IsTaskAssigner);
        var needStatus   = !db.DbFields.Any(f => f.CollectionId == collectionId && f.IsTaskStatus);
        if (!needAssigner && !needStatus) return false;

        // Поля — в память: имя роли ищем БЕЗ учёта регистра (SQL-сравнение строк
        // регистрозависимо, а lower() в SQLite кириллицу не трогает), иначе «назначил»
        // с маленькой буквы проходил бы мимо роли и получал вторую.
        var fields = db.DbFields.Where(f => f.CollectionId == collectionId).ToList();

        var assigner = needAssigner
            ? fields.FirstOrDefault(f => f.FieldType == "user" && IsRoleName(f.Name, FieldAssigner))
            : null;
        // Одноимённое поле-список («Список» ИЛИ новый «Статус») — помечаем флагом и
        // дополняем опциями. Иначе создаём поле нового типа «status» (раньше — select).
        var status = needStatus
            ? fields.FirstOrDefault(f => (f.FieldType == "select" || f.FieldType == "status") &&
                                         IsRoleName(f.Name, FieldStatus))
            : null;

        if (assigner is not null || status is not null)
            Demand(PermAction.EditField, collectionId);
        if ((needAssigner && assigner is null) || (needStatus && status is null))
            Demand(PermAction.CreateField, collectionId);

        if (needAssigner)
        {
            if (assigner is not null)
            {
                assigner.IsTaskAssigner = true;
                Log("update", "field", assigner.Name, CollectionNameOf(collectionId));
                db.SaveChanges();
            }
            else
            {
                AddField(new DbField
                {
                    CollectionId   = collectionId,
                    Name           = FreeFieldName(collectionId, FieldAssigner),
                    FieldType      = "user",
                    Position       = NextFieldPosition(collectionId),
                    IsTaskAssigner = true,
                });
            }
        }

        if (needStatus)
        {
            if (status is not null)
            {
                status.IsTaskStatus = true;
                status.ConfigJson   = MergeSelectOptions(status.ConfigJson, TaskStatusOptions);
                Log("update", "field", status.Name, CollectionNameOf(collectionId));
                db.SaveChanges();
            }
            else
            {
                AddField(new DbField
                {
                    CollectionId = collectionId,
                    Name         = FreeFieldName(collectionId, FieldStatus),
                    FieldType    = "status",
                    ConfigJson   = TaskStatusConfigJson,
                    Position     = NextFieldPosition(collectionId),
                    IsTaskStatus = true,
                });
            }
        }

        return true;
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

    // Дополняет ConfigJson select-поля недостающими опциями (существующие — как есть).
    private static string MergeSelectOptions(string configJson, IReadOnlyList<string> required)
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
        catch (JsonException) { /* нечитаемый конфиг — просто перезапишем опциями статусов */ }

        foreach (var r in required)
            if (!options.Contains(r))
                options.Add(r);
        return JsonSerializer.Serialize(new { options });
    }

    // ── Чтение задач ─────────────────────────────────────────────────────────

    /// <summary>
    /// Задачи по активным коллекциям (null — все коллекции базы, ПУСТОЙ набор — ни одной,
    /// как у календаря). Прямой запрос по db_field_values (индекс FieldId,Value) без
    /// денормализованной проекции: exec-полей мало, «Назначил»/«Статус»/срок добираются
    /// коррелированными подзапросами по PK-префиксам. Сортировку и поиск делает контроллер
    /// (агрегат нескольких БД сортируется после слияния). limit — грубая крышка v1.
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
            cmd.CommandText = $@"
                SELECT v.RecordId, f.Id, f.CollectionId, c.Name, c.Color,
                       r.Title, r.Description, v.Value,
                       (SELECT va.Value FROM db_field_values va
                          JOIN db_fields fa ON fa.Id = va.FieldId
                         WHERE va.RecordId = v.RecordId AND fa.CollectionId = f.CollectionId
                           AND fa.IsTaskAssigner = 1 LIMIT 1),
                       (SELECT vs.Value FROM db_field_values vs
                          JOIN db_fields fs ON fs.Id = vs.FieldId
                         WHERE vs.RecordId = v.RecordId AND fs.CollectionId = f.CollectionId
                           AND fs.IsTaskStatus = 1 LIMIT 1),
                       (SELECT ci.FieldId || '|' || ci.StartIso || '|' || ci.EndIso || '|' || ci.AllDay
                          FROM calendar_index ci
                          JOIN db_fields fd ON fd.Id = ci.FieldId
                         WHERE ci.RecordId = v.RecordId AND fd.IsCalStart = 1 AND fd.FieldType = 'date'
                         ORDER BY ci.StartIso LIMIT 1)
                FROM db_field_values v
                JOIN db_fields f      ON f.Id = v.FieldId AND f.FieldType = 'user' AND f.IsTaskExec = 1
                JOIN db_records r     ON r.Id = v.RecordId
                JOIN db_collections c ON c.Id = f.CollectionId
                WHERE v.Value <> ''{colFilter}
                ORDER BY v.RecordId, f.Id
                LIMIT {Math.Max(1, limit)}";

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
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
                if (!rdr.IsDBNull(10))
                    ApplyDuePacked(t, rdr.GetString(10));
                result.Add(t);
            }
        }
        finally { if (opened) conn.Close(); }
        return result;
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

    // ── CRUD задач ───────────────────────────────────────────────────────────

    /// <summary>
    /// Создаёт задачу: с датой — через календарный путь (шапка + поле-дата + calendar_index),
    /// без даты — обычная запись. Затем проставляет исполнителя/назначившего/статус.
    /// Коллекция при необходимости включается в задачи (EnsureTaskCollection);
    /// null — как в календаре, авто-коллекция «Без названия».
    /// </summary>
    public TaskRecord AddTaskRecord(int? collection, string title, string executor,
                                    string assigner, string status, DateValue? due)
    {
        Demand(PermAction.CreateRecord, collection);
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
    /// напрямую + очистка прежнего поля-даты (смену коллекции без даты диалог не даёт).
    /// Исполнитель/назначил/статус — обычные SetFieldValue (сами ведут журнал и индекс).
    /// </summary>
    public void UpdateTaskRecord(TaskRecord t)
    {
        if (t.Id is not int recordId) return;

        // Роли ЦЕЛИ готовим ДО переноса. Смена коллекции пересоздаёт запись календарным
        // путём (AddCalendarRecord + копия значений + удаление исходной, каждый со своим
        // SaveChanges и без общей транзакции). Если спросить права после него, отказ
        // застанет перенос уже закоммиченным: запись в цели, а исполнителя/назначившего
        // и статуса нет — задача исчезла из раздела вместе со значениями. Поля,
        // созданные заранее, при отказе остаются пустыми — это безобидно.
        // Только когда перенос ДЕЙСТВИТЕЛЬНО будет: коллекцию меняет календарный путь, а у
        // задачи без даты его нет (диалог гасит комбо, но выбранное значение всё равно
        // приезжает в кандидате). Иначе запись осталась бы в своей коллекции, а исполнитель
        // ушёл бы в поле чужой — ровно та порча, которую этот метод и чинит.
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
            t.Id         = cal.Id;          // смена коллекции пересоздаёт запись
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
        // что задача = «запись × ЕЁ user-поле с IsTaskExec». Берём exec-поле цели,
        // подготовленное выше; второй случай — exec-поле удалили, пока диалог был открыт
        // (CollectionOfField вернёт null), тогда заводим исполнителя здесь.
        if (targetExecFieldId is int prepared)              t.ExecFieldId = prepared;
        else if (CollectionOfField(t.ExecFieldId) != cid)   t.ExecFieldId = EnsureTaskCollection(cid, adoptExisting: false);

        SetFieldValue(recordId, t.ExecFieldId, t.Executor);
        var roles = TaskRoleFields(cid);
        if (roles.AssignerId is int aid) SetFieldValue(recordId, aid, t.Assigner);
        if (roles.StatusId   is int sid) SetFieldValue(recordId, sid, t.Status);
    }

    /// <summary>Id полей-ролей задач коллекции («Назначил», «Статус») — по флагам.</summary>
    public (int? AssignerId, int? StatusId) TaskRoleFields(int collectionId)
    {
        int? assigner = db.DbFields.Where(f => f.CollectionId == collectionId && f.IsTaskAssigner)
                                   .Select(f => (int?)f.Id).FirstOrDefault();
        int? status   = db.DbFields.Where(f => f.CollectionId == collectionId && f.IsTaskStatus)
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
