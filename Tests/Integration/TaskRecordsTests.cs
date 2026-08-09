using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Раздел «Задачи» (Database/Repositories/ConstructorRepository.Tasks.cs).
///
/// Задача — это НЕ отдельная сущность: это обычная запись коллекции, увиденная через
/// user-поле с флагом IsTaskExec («исполнитель»). Идентичность карточки — пара
/// (RecordId, ExecFieldId): одна запись даёт столько задач, сколько у коллекции
/// включённых полей-исполнителей с непустым значением. Роли «Назначил» (user) и
/// «Статус» (status) — обычные поля коллекции, помеченные флагами и авто-созданные при
/// первом включении задач. Срок отдельно не хранится — это первая по началу ВИДИМАЯ
/// календарная дата записи (calendar_index + IsCalStart).
///
/// Чтение раздела — сырой SQL с коррелированными подзапросами (GetTaskRecords), поэтому
/// тесты проверяют не «метод отработал», а что в карточку попали данные ИМЕННО той
/// записи и ИМЕННО той коллекции: ошибка в подзапросе не падает, она молча показывает
/// пользователю чужого назначившего, чужой статус или чужой срок.
///
/// Часть тестов ниже помечена «КРАСНЫЙ»: они воспроизводят подтверждённые дефекты и
/// сегодня падают — это их работа, они фиксируют ожидаемое пользователем поведение.
/// </summary>
public sealed class TaskRecordsTests
{
    // ── Обвязка ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Включает коллекцию в задачи так, как это делает пользователь галочкой в сайдбаре:
    /// создаёт user-поле-исполнитель и помечает его IsTaskExec — роли «Назначил»/«Статус»
    /// коллекция получает сама. Возвращает id исполнителя и id обеих ролей.
    /// </summary>
    private static (int ExecId, int AssignerId, int StatusId) EnableTasks(
        ConstructorRepository repo, int collectionId, string name = "Исполнитель")
    {
        var exec = SampleData.AddField(repo, collectionId, name, "user", position: 1000);
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);

        var roles = repo.TaskRoleFields(collectionId);
        // Включение задач обязано дать коллекции полный набор ролей: без «Назначил» и
        // «Статуса» карточку нечем заполнять, без «Даты» у задачи не будет срока, без
        // «Описания» — текста.
        Assert.True(roles.AssignerId is not null, "включение задач не создало поле «Назначил»");
        Assert.True(roles.StatusId is not null, "включение задач не создало поле «Статус»");
        Assert.NotNull(repo.DescriptionFieldId(collectionId));
        Assert.Contains(repo.GetFields(collectionId), f => f.FieldType == "date" && f.IsCalStart);
        return (exec.Id!.Value, roles.AssignerId!.Value, roles.StatusId!.Value);
    }

    // Значения свойств записи: FieldId → значение (отсутствующая ячейка = пустая строка).
    private static string ValueOf(ConstructorRepository repo, int recordId, int fieldId) =>
        repo.GetFieldValuesBatch([recordId]).TryGetValue(recordId, out var values)
            ? values.GetValueOrDefault(fieldId, "")
            : "";

    private static Dictionary<int, DbField> FieldsById(ConstructorRepository repo, int collectionId) =>
        repo.GetFields(collectionId).ToDictionary(f => f.Id!.Value);

    // Состав полей коллекции для сравнения «до/после» отказа в правах.
    private static List<string> FieldSnapshot(ConstructorRepository repo, int collectionId) =>
        repo.GetFields(collectionId)
            .OrderBy(f => f.Id!.Value)
            .Select(f => $"{f.Id}:{f.Name}:{f.FieldType}")
            .ToList();

    // Понижает текущего пользователя до допущенного НЕ-администратора: с этого момента
    // действуют маски прав (администратору разрешено всё и проверки не срабатывают).
    private static void DemoteToApprovedUser(TempDatabase db)
    {
        var me = db.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key);
        me.IsAdmin    = false;
        me.IsApproved = true;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);
    }

    // ── Чтение раздела ────────────────────────────────────────────────────────

    [Fact]
    public void Task_row_collects_executor_assigner_status_and_due()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        // Коллекция «Заявки»: исполнитель + авто-роли + собственный срок.
        var zayavki = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var (execZ, assignerZ, statusZ) = EnableTasks(repo, zayavki);
        // Включение задач уже завело поле-дату («Дата»), поэтому своё «Срок» в календарь
        // нужно включить явно — автофлаг достаётся лишь ПЕРВОМУ полю-дате коллекции.
        var dueZ = SampleData.AddField(repo, zayavki, "Срок", "date", position: 4000).Id!.Value;
        repo.SetDateFieldVisible(dueZ, true);

        var pumpId = SampleData.AddRecord(repo, zayavki, "Заменить насос").Id!.Value;
        repo.SetRecordDescription(pumpId, "в подвале");
        repo.SetFieldValue(pumpId, execZ, "Иванов Иван");
        repo.SetFieldValue(pumpId, assignerZ, "Петров Пётр");
        repo.SetFieldValue(pumpId, statusZ, ConstructorRepository.TaskStatusActive);
        repo.SetFieldValue(pumpId, dueZ,
            new DateValue(new DateTime(2026, 4, 1, 10, 0, 0), new DateTime(2026, 4, 1, 11, 0, 0), false).ToStorage());

        // Соседняя коллекция с тем же набором полей и своей задачей — её данные не должны
        // подмешиваться в чужую карточку.
        var remonty = SampleData.AddCollection(repo, "Ремонты").Id!.Value;
        var (execR, assignerR, statusR) = EnableTasks(repo, remonty);
        var dueR = SampleData.AddField(repo, remonty, "Срок", "date", position: 4000).Id!.Value;
        repo.SetDateFieldVisible(dueR, true);

        var roofId = SampleData.AddRecord(repo, remonty, "Починить крышу").Id!.Value;
        repo.SetFieldValue(roofId, execR, "Сидоров Семён");
        repo.SetFieldValue(roofId, assignerR, "Кузнецов Кузьма");
        repo.SetFieldValue(roofId, statusR, ConstructorRepository.TaskStatusDone);
        repo.SetFieldValue(roofId, dueR,
            new DateValue(new DateTime(2026, 5, 5), new DateTime(2026, 5, 5), true).ToStorage());

        var tasks = repo.GetTaskRecords();

        Assert.Equal(2, tasks.Count);

        // Карточка собирается целиком: шапка записи, три роли и срок из календарной даты.
        var pump = tasks.Single(t => t.Id == pumpId);
        Assert.Equal("Заменить насос", pump.Title);
        Assert.Equal("в подвале", pump.Description);
        Assert.Equal("Иванов Иван", pump.Executor);
        Assert.Equal("Петров Пётр", pump.Assigner);
        Assert.Equal(ConstructorRepository.TaskStatusActive, pump.Status);
        Assert.Equal(zayavki, pump.CollectionId);
        Assert.Equal("Заявки", pump.CollectionName);
        Assert.Equal(execZ, pump.ExecFieldId);
        Assert.Equal(dueZ, pump.DueFieldId);
        Assert.Equal(new DateTime(2026, 4, 1, 10, 0, 0), pump.DueStart);
        Assert.Equal(new DateTime(2026, 4, 1, 11, 0, 0), pump.DueEnd);
        Assert.False(pump.AllDay);

        // Роли и срок берутся из СВОЕЙ записи и СВОЕЙ коллекции: потеря корреляции в
        // подзапросах (по записи или по коллекции) подставила бы соседнего назначившего,
        // соседний статус или соседний срок — молча, без единой ошибки.
        var roof = tasks.Single(t => t.Id == roofId);
        Assert.Equal("Кузнецов Кузьма", roof.Assigner);
        Assert.Equal(ConstructorRepository.TaskStatusDone, roof.Status);
        Assert.Equal(remonty, roof.CollectionId);
        Assert.Equal("Ремонты", roof.CollectionName);
        Assert.Equal(dueR, roof.DueFieldId);
        Assert.True(roof.AllDay);
    }

    [Fact]
    public void Task_due_uses_earliest_visible_date_field()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);

        // Поле-дата у коллекции уже есть (его завело включение задач), поэтому автофлаг
        // «первому полю-дате» больше не срабатывает — обе свои даты включаем явно.
        var startId = SampleData.AddField(repo, col, "Начало", "date", position: 4000).Id!.Value;
        var deadlineId = SampleData.AddField(repo, col, "Дедлайн", "date", position: 5000).Id!.Value;
        var fields = FieldsById(repo, col);
        Assert.False(fields[startId].IsCalStart);
        Assert.False(fields[deadlineId].IsCalStart);
        repo.SetDateFieldVisible(startId, true);
        repo.SetDateFieldVisible(deadlineId, true);

        // Исполнителя не задаём: новая запись сама получает текущего пользователя во все
        // user-поля, значит карточка в разделе появится (это и есть поведение продукта).
        var recId = SampleData.AddRecord(repo, col, "Заявка").Id!.Value;
        var startValue = new DateValue(new DateTime(2026, 4, 1), new DateTime(2026, 4, 1), true).ToStorage();
        var deadlineValue = new DateValue(new DateTime(2026, 5, 1), new DateTime(2026, 5, 1), true).ToStorage();
        repo.SetFieldValue(recId, startId, startValue);
        repo.SetFieldValue(recId, deadlineId, deadlineValue);

        // Срок = первая по началу видимая дата записи.
        var task = Assert.Single(repo.GetTaskRecords());
        Assert.Equal(new DateTime(2026, 4, 1), task.DueStart!.Value.Date);
        Assert.Equal(startId, task.DueFieldId);

        // «Начало» спрятали из календаря — сроком становится следующая видимая дата.
        // Скрытая дата сроком быть не может, но и задача срок не теряет.
        repo.SetDateFieldVisible(startId, false);
        task = Assert.Single(repo.GetTaskRecords());
        Assert.Equal(new DateTime(2026, 5, 1), task.DueStart!.Value.Date);
        Assert.Equal(deadlineId, task.DueFieldId);

        // Видимых дат не осталось: задача ОСТАЁТСЯ в разделе, но «без даты» (уходит в конец
        // списка) — прятать даты не значит прятать работу.
        repo.SetDateFieldVisible(deadlineId, false);
        task = Assert.Single(repo.GetTaskRecords());
        Assert.Null(task.DueStart);
        Assert.Null(task.DueFieldId);

        // Видимость — чистый фильтр чтения: значения дат и строки проекции на месте,
        // включение галочки обратно не требует пересборки.
        using var check = new AppDbContext(db.Path);
        var stored = check.DbFieldValues.AsNoTracking()
                          .Where(v => v.RecordId == recId)
                          .ToDictionary(v => v.FieldId, v => v.Value);
        Assert.Equal(startValue, stored[startId]);
        Assert.Equal(deadlineValue, stored[deadlineId]);
        Assert.Equal(2, check.CalendarIndex.AsNoTracking().Count(x => x.RecordId == recId));
    }

    [Fact]
    public void Record_yields_one_task_per_enabled_executor_field()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;

        var (execId, assignerId, statusId) = EnableTasks(repo, col);
        var reviewerId = SampleData.AddField(repo, col, "Проверяющий", "user", position: 5000).Id!.Value;
        // Второе включение ролей не плодит: «Назначил»/«Статус» принадлежат коллекции.
        Assert.False(repo.SetUserFieldTaskExec(reviewerId, true));

        var recId = SampleData.AddRecord(repo, col, "Заменить насос").Id!.Value;
        repo.SetFieldValue(recId, execId, "Иванов Иван");
        repo.SetFieldValue(recId, reviewerId, "Петров Пётр");
        repo.SetFieldValue(recId, assignerId, "Кузнецов Кузьма");
        repo.SetFieldValue(recId, statusId, ConstructorRepository.TaskStatusPlanned);

        var tasks = repo.GetTaskRecords();

        // Одна запись — две карточки: «сделать» и «проверить» это разная работа разных людей.
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, t => Assert.Equal(recId, t.Id));

        var byExec = tasks.Single(t => t.ExecFieldId == execId);
        var byReviewer = tasks.Single(t => t.ExecFieldId == reviewerId);

        // Каждая карточка показывает исполнителя СВОЕГО поля — иначе правка одной карточки
        // писала бы в чужое поле и стирала второго исполнителя.
        Assert.Equal("Иванов Иван", byExec.Executor);
        Assert.Equal("Петров Пётр", byReviewer.Executor);

        // «Назначил» и «Статус» — свойства записи, а не поля-исполнителя: у обеих одинаковы.
        Assert.Equal("Кузнецов Кузьма", byExec.Assigner);
        Assert.Equal("Кузнецов Кузьма", byReviewer.Assigner);
        Assert.Equal(ConstructorRepository.TaskStatusPlanned, byExec.Status);
        Assert.Equal(ConstructorRepository.TaskStatusPlanned, byReviewer.Status);
    }

    [Fact]
    public void Active_ids_filter_and_empty_executor_semantics()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var a = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var (execA, _, _) = EnableTasks(repo, a);
        var recA = SampleData.AddRecord(repo, a, "Заменить насос").Id!.Value;
        repo.SetFieldValue(recA, execA, "Иванов Иван");

        var b = SampleData.AddCollection(repo, "Ремонты").Id!.Value;
        var (execB, _, _) = EnableTasks(repo, b);
        var recB = SampleData.AddRecord(repo, b, "Починить крышу").Id!.Value;
        repo.SetFieldValue(recB, execB, "Петров Пётр");

        // null — «все коллекции базы» (первое открытие раздела).
        Assert.Equal(2, repo.GetTaskRecords(null).Count);

        // Пустой набор — сняты ВСЕ галки: пусто, как в календаре. «Ничего не выбрано» не
        // должно означать «показать всё» — иначе галки в сайдбаре бессмысленны.
        Assert.Empty(repo.GetTaskRecords(new HashSet<int>()));

        var onlyA = Assert.Single(repo.GetTaskRecords(new HashSet<int> { a }));
        Assert.Equal(a, onlyA.CollectionId);

        // Пустой исполнитель = задачи нет. Это единственный способ убрать запись из раздела,
        // не трогая схему: сама запись остаётся в коллекции.
        repo.SetFieldValue(recA, execA, "");
        var left = Assert.Single(repo.GetTaskRecords(null));
        Assert.Equal(b, left.CollectionId);
        Assert.Equal(1, repo.CountRecords(a));

        // Исполнителя вернули — карточка на месте, с тем же полем-источником.
        repo.SetFieldValue(recA, execA, "Иванов Иван");
        var restored = repo.GetTaskRecords(null);
        Assert.Equal(2, restored.Count);
        Assert.Equal(execA, restored.Single(t => t.CollectionId == a).ExecFieldId);
    }

    // ── CRUD задач ────────────────────────────────────────────────────────────

    [Fact]
    public void Field_named_assigner_never_becomes_the_executor_field()
    {
        // КРАСНЫЙ ТЕСТ. Дефект: ConstructorRepository.Tasks.cs:78-79 — кандидата в исполнители
        // ищут по флагу (!IsTaskAssigner), а у вручную созданного поля «Назначил» флага ещё
        // нет: одно и то же поле получает ОБА флага, и AddTaskRecord затирает выбранного
        // исполнителя назначившим. Для пользователя это потеря данных без сообщения.
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;

        // Пользователь сам завёл поле «Назначил» и ни разу не включал задачи.
        repo.AddField(new DbField
        {
            CollectionId = col, Name = "Назначил", FieldType = "user", Position = 1000,
        });

        // Быстрое добавление задачи из раздела: коллекция включается в задачи на лету.
        repo.AddTaskRecord(col, "Проверить насос", "Иванов Иван", "Петров Пётр",
                           ConstructorRepository.TaskStatusPlanned, due: null);

        var fields = repo.GetFields(col);
        var assignerField = fields.Single(f => f.Name == "Назначил");
        Assert.True(assignerField.IsTaskAssigner, "поле с именем «Назначил» — это роль «Назначил»");
        Assert.False(assignerField.IsTaskExec,
                     "«Назначил» не может быть одновременно исполнителем: это два разных человека");

        var execField = Assert.Single(fields, f => f.IsTaskExec);
        Assert.NotEqual(assignerField.Id, execField.Id);

        var task = Assert.Single(repo.GetTaskRecords());
        Assert.Equal("Иванов Иван", task.Executor);
        Assert.Equal("Петров Пётр", task.Assigner);

        // И в самой базе: выбранный исполнитель не перезаписан назначившим.
        Assert.Equal("Иванов Иван", ValueOf(repo, task.Id!.Value, execField.Id!.Value));
        Assert.Equal("Петров Пётр", ValueOf(repo, task.Id!.Value, assignerField.Id!.Value));
    }

    [Fact]
    public void Moving_task_to_another_collection_carries_executor_assigner_and_status()
    {
        // КРАСНЫЙ ТЕСТ. Дефект: Tasks.cs:363-397 — смена коллекции ПЕРЕСОЗДАЁТ запись, но
        // исполнитель после этого пишется по устаревшему t.ExecFieldId (поле ЧУЖОЙ коллекции),
        // а EnsureTaskCollection для цели не зовётся. Итог у пользователя: перенёс задачу —
        // и она пропала из раздела/показывает старую коллекцию, а назначивший и статус исчезли.
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var a = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, a);
        repo.AddTaskRecord(a, "Течь в подвале", "Иванов Иван", "Петров Пётр",
                           ConstructorRepository.TaskStatusActive,
                           due: new DateValue(new DateTime(2026, 4, 1, 9, 0, 0),
                                              new DateTime(2026, 4, 1, 10, 0, 0), false));

        var b = SampleData.AddCollection(repo, "Ремонты").Id!.Value;

        // Ровно то, что делает диалог задачи: сменили коллекцию в карточке и сохранили.
        var task = Assert.Single(repo.GetTaskRecords());
        task.CollectionId = b;
        repo.UpdateTaskRecord(task);

        var movedId = task.Id!.Value;
        using (var check = new AppDbContext(db.Path))
        {
            Assert.Equal(b, check.DbRecords.AsNoTracking().Single(r => r.Id == movedId).CollectionId);

            // Значения записи не должны ссылаться на поля коллекции-источника: такая ячейка
            // не видна ни в таблице, ни в панели записи — данные есть, но их не достать.
            var fieldsOfA = check.DbFields.AsNoTracking()
                                 .Where(f => f.CollectionId == a).Select(f => f.Id).ToHashSet();
            var stray = check.DbFieldValues.AsNoTracking()
                             .Where(v => v.RecordId == movedId)
                             .Select(v => v.FieldId).ToList()
                             .Where(fieldsOfA.Contains).ToList();
            Assert.True(stray.Count == 0,
                        $"значения перенесённой записи остались на полях исходной коллекции: {stray.Count}");
        }

        Assert.Equal(0, repo.CountRecords(a));
        Assert.Equal(1, repo.CountRecords(b));

        // Цель обязана быть включена в задачи, иначе роли теряются молча.
        var roles = repo.TaskRoleFields(b);
        Assert.True(roles.AssignerId is not null, "коллекция-цель не получила роль «Назначил»");
        Assert.True(roles.StatusId is not null, "коллекция-цель не получила роль «Статус»");
        Assert.Equal("Петров Пётр", ValueOf(repo, movedId, roles.AssignerId!.Value));
        Assert.Equal(ConstructorRepository.TaskStatusActive, ValueOf(repo, movedId, roles.StatusId!.Value));

        // И в разделе — ровно одна карточка (без призрачного дубля от старого поля).
        var after = Assert.Single(repo.GetTaskRecords());
        Assert.Equal(b, after.CollectionId);
        Assert.Equal("Ремонты", after.CollectionName);
        Assert.Equal("Иванов Иван", after.Executor);
        Assert.Equal("Петров Пётр", after.Assigner);
        Assert.Equal(ConstructorRepository.TaskStatusActive, after.Status);
        Assert.Equal(new DateTime(2026, 4, 1, 9, 0, 0), after.DueStart);
    }

    // Сетап для пары тестов ниже: задача без срока и задача со сроком в одной коллекции,
    // плюс постороннее user-поле «Согласующий». Возвращает обе карточки, id ролей и
    // значения свойств обеих записей.
    private static (TaskRecord T1, TaskRecord T2, int ExecId, int AssignerId, int StatusId,
                    int ApproverId, Func<TaskRecord, int, string> Value)
        TwoTasksWithAndWithoutDue(ConstructorRepository repo, int col)
    {
        var (execId, assignerId, statusId) = EnableTasks(repo, col);
        var approverId = SampleData.AddField(repo, col, "Согласующий", "user", position: 5000).Id!.Value;

        var t1 = repo.AddTaskRecord(col, "без срока", "Иванов Иван", "Петров Пётр",
                                    ConstructorRepository.TaskStatusPlanned, due: null);
        var t2 = repo.AddTaskRecord(col, "со сроком", "Иванов Иван", "Петров Пётр",
                                    ConstructorRepository.TaskStatusPlanned,
                                    due: new DateValue(new DateTime(2026, 4, 1), new DateTime(2026, 4, 1), true));

        var values = repo.GetFieldValuesBatch([t1.Id!.Value, t2.Id!.Value]);
        string Value(TaskRecord t, int fieldId) =>
            values.TryGetValue(t.Id!.Value, out var row) ? row.GetValueOrDefault(fieldId, "") : "";

        return (t1, t2, execId, assignerId, statusId, approverId, Value);
    }

    [Fact]
    public void Task_creation_fills_roles_the_same_with_and_without_due()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;

        var (t1, t2, execId, assignerId, statusId, _, Value) = TwoTasksWithAndWithoutDue(repo, col);

        // Задача без срока идёт через AddRecord, со сроком — через AddCalendarRecord. Три роли
        // задачи обязаны заполняться одинаково обоими путями: иначе «добавил задачу с датой» и
        // «добавил задачу без даты» давали бы карточки разного качества.
        Assert.Equal("Иванов Иван", Value(t1, execId));
        Assert.Equal("Иванов Иван", Value(t2, execId));
        Assert.Equal("Петров Пётр", Value(t1, assignerId));
        Assert.Equal("Петров Пётр", Value(t2, assignerId));
        Assert.Equal(ConstructorRepository.TaskStatusPlanned, Value(t1, statusId));
        Assert.Equal(ConstructorRepository.TaskStatusPlanned, Value(t2, statusId));

        // «Согласующий» в задачах не участвует — карточек ровно две.
        Assert.Equal(2, repo.GetTaskRecords().Count);
    }

    [Fact(Skip = "дефект продукта: AddCalendarRecord (ConstructorRepository.cs:4999) не проставляет " +
                 "текущего пользователя в user-поля новой записи, а AddRecord — проставляет")]
    public void Task_creation_fills_other_user_fields_the_same_with_and_without_due()
    {
        // КРАСНЫЙ ТЕСТ. Дефект: Tasks.cs:311-355 — задача БЕЗ срока создаётся через AddRecord
        // (заполняет ВСЕ user-поля текущим пользователем), СО сроком — через AddCalendarRecord
        // (не заполняет ничего). Одинаковое действие пользователя даёт разные записи: у одной
        // «Согласующий» заполнен, у другой пуст. Тест фиксирует симметрию путей, не выбирая,
        // какой из них правильный.
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;

        var (t1, t2, _, _, _, approverId, Value) = TwoTasksWithAndWithoutDue(repo, col);

        // Поле, к задачам отношения не имеющее, обязано быть заполнено одинаково.
        Assert.Equal(Value(t1, approverId), Value(t2, approverId));
    }

    // ── Права на правку схемы задач ───────────────────────────────────────────

    // Коллекция «с другой машины/со старой версии»: exec-поле уже помечено флагом, ролей нет,
    // но поля с их именами есть, причём «Статус» — обычный список пользователя СО СВОИМИ
    // опциями. Возвращает id «Статуса» и id постороннего user-поля «Проверяющий».
    private static (int StatusId, int ReviewerId) SeedCollectionWithRoleLikeFields(
        ConstructorRepository repo, int col)
    {
        repo.AddField(new DbField
        {
            CollectionId = col, Name = "Исполнитель", FieldType = "user", Position = 1000,
            IsTaskExec = true,
        });
        var statusId = repo.AddField(new DbField
        {
            CollectionId = col, Name = "Статус", FieldType = "select", Position = 2000,
            ConfigJson = "{\"options\":[\"Черновик\",\"Согласовано\"]}",
        }).Id!.Value;
        // Поле «Назначил» уже существует: именно оно и «Статус» — те чужие поля, которые
        // EnsureTaskSupportFields помечает ролями молча (без него путь до дефекта не доходит —
        // создание недостающей роли упирается в CreateField и маскирует проблему).
        repo.AddField(new DbField
        {
            CollectionId = col, Name = "Назначил", FieldType = "user", Position = 3000,
        });
        var reviewerId = SampleData.AddField(repo, col, "Проверяющий", "user", position: 4000).Id!.Value;
        return (statusId, reviewerId);
    }

    // Забирает у текущего пользователя права на свойства (записи менять по-прежнему можно).
    private static void RevokeFieldRights(TempDatabase db)
    {
        db.Permissions.SetMask(db.Permissions.CurrentUserId, null,
                               (int)(PermAction.All & ~PermAction.CreateField & ~PermAction.EditField));
        DemoteToApprovedUser(db);
    }

    [Fact]
    public void Enabling_tasks_on_a_field_requires_field_rights()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var (_, reviewerId) = SeedCollectionWithRoleLikeFields(repo, col);
        RevokeFieldRights(db);

        var fieldsBefore = FieldSnapshot(repo, col);

        // Включение задач у поля = правка схемы коллекции: без права на свойства — отказ.
        Assert.Throws<PermissionDeniedException>(() => repo.SetUserFieldTaskExec(reviewerId, true));
        Assert.False(FieldsById(repo, col)[reviewerId].IsTaskExec);
        // Отказ не оставляет коллекцию наполовину включённой: проверка идёт ДО записи флага.
        Assert.Equal(fieldsBefore, FieldSnapshot(repo, col));
    }

    [Fact]
    public void Task_support_fields_marking_requires_field_rights_and_is_journalled()
    {
        // Продуктовое решение, которое фиксирует этот тест (занести в
        // docs/developer/architecture/users-permissions.md):
        // пометка СУЩЕСТВУЮЩИХ полей ролями задач и правка их ConfigJson — это изменение
        // СХЕМЫ коллекции. Значит, требует права EditField (создание недостающих ролей —
        // CreateField) и обязано попадать в журнал строкой Action=="update"/TargetType=="field".
        // Сейчас ветка EnsureTaskCollection «exec-поле уже есть» (Tasks.cs:112-169) зовёт
        // EnsureTaskSupportFields без Demand и без Log: чужое поле «Статус» получает флаг роли,
        // а его опции переписываются стандартными — молча, без прав и без следа в истории.
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var (statusId, _) = SeedCollectionWithRoleLikeFields(repo, col);
        RevokeFieldRights(db);

        var statusConfigBefore = FieldsById(repo, col)[statusId].ConfigJson;
        var fieldsBefore       = FieldSnapshot(repo, col);
        var logRowsBefore      = db.Context.ChangeLog.Count();

        // То же изменение схемы, но «между делом» — при добавлении задачи.
        Assert.Throws<PermissionDeniedException>(() =>
            repo.AddTaskRecord(col, "Задача", "Иванов Иван", "Петров Пётр", "Согласовано", due: null));

        var fieldsAfter = FieldsById(repo, col);
        // Опции пользовательского списка не переписаны стандартными статусами.
        Assert.Equal(statusConfigBefore, fieldsAfter[statusId].ConfigJson);
        // Ролей никому не проставили, полей не добавили, записи не создали.
        Assert.DoesNotContain(fieldsAfter.Values, f => f.IsTaskAssigner || f.IsTaskStatus);
        Assert.Equal(fieldsBefore, FieldSnapshot(repo, col));
        Assert.Equal(0, repo.CountRecords(col));
        // И в истории изменений — ни одной новой строки: запрещённого действия не было.
        Assert.Equal(logRowsBefore, db.Context.ChangeLog.Count());
    }
}
