using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Границы ролей задач: за какие поля раздел «Задачи» браться НЕ имеет права.
///
/// Проверки этого файла родились из разбора исправлений (коммит b38bcfb): первая версия
/// закрыла дефекты только на одном пути, а соседние остались открыты — поле «Назначил»
/// по-прежнему включалось исполнителем тумблером в сайдбаре, имя роли сравнивалось с
/// учетом регистра, а перенос задачи в постороннюю коллекцию «усыновлял» ее чужое
/// user-поле. Все это молчаливые порчи данных: пользователь не видит ошибки, он видит
/// затертого исполнителя или раздел задач, залитый чужими записями.
/// </summary>
public sealed class TaskRoleGuardTests
{
    private static string ValueOf(ConstructorRepository repo, int recordId, int fieldId) =>
        repo.GetFieldValuesBatch([recordId]).TryGetValue(recordId, out var values)
            ? values.GetValueOrDefault(fieldId, "")
            : "";

    // Понижает до допущенного не-администратора без прав на свойства: маски начинают
    // действовать только для не-админа (администратору разрешено все).
    private static void RevokeFieldRights(TempDatabase db)
    {
        db.Permissions.SetMask(db.Permissions.CurrentUserId, null,
                               (int)(PermAction.All & ~PermAction.CreateField & ~PermAction.EditField));
        var me = db.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key);
        me.IsAdmin    = false;
        me.IsApproved = true;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);
    }

    [Theory]
    [InlineData("Назначил")]
    [InlineData("назначил")]   // регистр набирает пользователь, а не код
    [InlineData("НАЗНАЧИЛ")]
    public void Assigner_field_cannot_be_switched_into_executor(string name)
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Поручения").Id!.Value;
        var field = SampleData.AddField(repo, col, name, "user", position: 1000);

        // Тумблер «участвует в задачах» в сайдбаре — основной путь включения.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            repo.SetUserFieldTaskExec(field.Id!.Value, true));
        Assert.Contains("исполнителем", ex.Message);

        // Флаг не поставлен: иначе разметка ролей нашла бы это же поле ПО ИМЕНИ,
        // и один FieldId держал бы обе роли — исполнитель затирался бы назначившим.
        Assert.False(repo.GetFields(col).Single(f => f.Id == field.Id).IsTaskExec);
    }

    [Theory]
    [InlineData("Назначил")]
    [InlineData("назначил")]
    public void Quick_add_never_gives_both_roles_to_one_field(string name)
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Поручения").Id!.Value;
        SampleData.AddField(repo, col, name, "user", position: 1000);

        repo.AddTaskRecord(col, "Проверить насос", "Иванов Иван", "Петров Петр",
                           ConstructorRepository.TaskStatusPlanned, due: null);

        var fields   = repo.GetFields(col);
        var assigner = fields.Single(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        var exec     = Assert.Single(fields, f => f.IsTaskExec);

        Assert.True(assigner.IsTaskAssigner);
        Assert.False(assigner.IsTaskExec);
        Assert.NotEqual(assigner.Id, exec.Id);

        var task = Assert.Single(repo.GetTaskRecords());
        Assert.Equal("Иванов Иван", task.Executor);
        Assert.Equal("Петров Петр", task.Assigner);
        Assert.Equal("Иванов Иван", ValueOf(repo, task.Id!.Value, exec.Id!.Value));
    }

    [Fact]
    public void Status_role_field_cannot_be_switched_into_executor()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Поручения").Id!.Value;
        var field = SampleData.AddField(repo, col, "Состояние", "user", position: 1000).Id!.Value;

        // Роль «Статус» на user-поле бывает от ручной пометки или копирования коллекции:
        // по имени такое поле не усыновляют (там ищут select/status), а по флагу — брали.
        db.Context.DbFields.Single(f => f.Id == field).IsTaskStatus = true;
        db.Context.SaveChanges();

        var ex = Assert.Throws<InvalidOperationException>(() => repo.SetUserFieldTaskExec(field, true));
        Assert.Contains("исполнителем", ex.Message);
        Assert.False(repo.GetFields(col).Single(f => f.Id == field).IsTaskExec);
    }

    [Fact]
    public void Quick_add_skips_status_role_field_and_creates_its_own_executor()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Поручения").Id!.Value;
        var field = SampleData.AddField(repo, col, "Состояние", "user", position: 1000).Id!.Value;
        db.Context.DbFields.Single(f => f.Id == field).IsTaskStatus = true;
        db.Context.SaveChanges();

        repo.AddTaskRecord(col, "Проверить насос", "Иванов Иван", "Петров Петр",
                           ConstructorRepository.TaskStatusPlanned, due: null);

        // Единственное user-поле коллекции занято ролью статуса — усыновлять его нельзя,
        // заводится собственный «Исполнитель».
        var exec = Assert.Single(repo.GetFields(col), f => f.IsTaskExec);
        Assert.NotEqual(field, exec.Id);
        Assert.Equal("Иванов Иван", ValueOf(repo, Assert.Single(repo.GetTaskRecords()).Id!.Value, exec.Id!.Value));
    }

    [Fact]
    public void Two_fields_with_the_same_role_flag_resolve_to_the_same_one_everywhere()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Поручения").Id!.Value;
        var exec = SampleData.AddField(repo, col, "Исполнитель", "user", position: 1000).Id!.Value;
        repo.SetUserFieldTaskExec(exec, true);

        // Два поля с одним флагом — так выглядит коллекция после копирования или ручной
        // правки. Раньше выбор шел через FirstOrDefault/LIMIT 1 без порядка: запись значения
        // и его чтение могли разойтись по разным полям, и раздел показывал то одно, то другое.
        var auto  = db.Context.DbFields.Single(f => f.CollectionId == col && f.IsTaskAssigner);
        var extra = SampleData.AddField(repo, col, "Назначил 2", "user", position: auto.Position - 100).Id!.Value;
        db.Context.DbFields.Single(f => f.Id == extra).IsTaskAssigner = true;
        db.Context.SaveChanges();

        // Правило одно на весь модуль — первое по (Position, Id).
        Assert.Equal(extra, repo.TaskRoleFields(col).AssignerId);

        repo.AddTaskRecord(col, "Проверить насос", "Иванов Иван", "Петров Петр",
                           ConstructorRepository.TaskStatusPlanned, due: null);

        var task = Assert.Single(repo.GetTaskRecords());
        Assert.Equal("Петров Петр", task.Assigner);                       // чтение раздела…
        Assert.Equal("Петров Петр", ValueOf(repo, task.Id!.Value, extra)); // …и запись сошлись
        // Во второе поле с тем же флагом назначивший не попал (там свое значение —
        // AddRecord заполняет user-поля текущим пользователем).
        Assert.NotEqual("Петров Петр", ValueOf(repo, task.Id!.Value, auto.Id));
    }

    [Fact]
    public void Moving_task_records_does_not_enroll_target_collection_into_tasks()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var tasks = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var exec  = SampleData.AddField(repo, tasks, "Исполнитель", "user", position: 1000).Id!.Value;
        repo.SetUserFieldTaskExec(exec, true);
        repo.AddTaskRecord(tasks, "Течь в подвале", "Иванов Иван", "Петров Петр",
                           ConstructorRepository.TaskStatusActive, due: null);

        var archive = SampleData.AddCollection(repo, "Архив").Id!.Value;
        var rec     = Assert.Single(repo.GetTaskRecords()).Id!.Value;

        repo.MoveRecordsToCollectionIsolated(db.Path, tasks, [rec], archive,
                                             createMissingFields: true, sourceDbId: 1, targetDbId: 1);

        // Свойства созданы и значения приехали, но «Архив» — обычная коллекция: перенос
        // записей не включает приемник ни в раздел задач, ни в календарь (раньше флаги ролей
        // копировались, и коллекция внезапно оказывалась там со всеми своими записями).
        var fields = repo.GetFields(archive);
        Assert.Contains(fields, f => f.Name == "Исполнитель");
        Assert.All(fields, f => Assert.False(f.IsTaskExec || f.IsTaskAssigner || f.IsTaskStatus));
        Assert.Contains(fields, f => f.FieldType == "date");       // срок задачи приехал столбцом…
        Assert.All(fields, f => Assert.False(f.IsCalStart));       // …но событием календаря не стал
        Assert.Empty(repo.GetTaskRecords());
    }

    [Fact]
    public void Auto_collection_is_not_created_without_the_collection_right()
    {
        using var db = new TempDatabase();

        // Допущенный не-админ: записи создавать можно, коллекции — нет.
        db.Permissions.SetMask(db.Permissions.CurrentUserId, null,
                               (int)(PermAction.All & ~PermAction.CreateCollection));
        var me = db.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key);
        me.IsAdmin    = false;
        me.IsApproved = true;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);

        // Задача «без коллекции» заводила «Без названия» в обход прав — дыра того же класса,
        // что закрыта в EnsureTaskSupportFields.
        Assert.Throws<PermissionDeniedException>(() =>
            db.Constructor.AddTaskRecord(null, "Задача из ниоткуда", "Иванов Иван", "Петров Петр",
                                         ConstructorRepository.TaskStatusPlanned, due: null));

        Assert.DoesNotContain(db.Constructor.GetAllCollections(), c => c.Name == "Без названия");
    }

    [Fact]
    public void Mounting_heals_a_field_that_carries_both_roles()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            int col, exec;
            using (var old = DatabaseManager.Open(1, "старая", path))
            {
                var repo = old.Constructor;
                col  = SampleData.AddCollection(repo, "Поручения").Id!.Value;
                exec = SampleData.AddField(repo, col, "Исполнитель", "user", position: 1000).Id!.Value;
                repo.SetUserFieldTaskExec(exec, true);

                // Состояние базы, открытой ДО запрета: отдельного «Назначил» нет, обе роли
                // висят на одном поле — исполнитель и назначивший затирали друг друга.
                var assigner = old.Context.DbFields.Single(f => f.CollectionId == col && f.IsTaskAssigner);
                old.Context.DbFields.Remove(assigner);
                old.Context.DbFields.Single(f => f.Id == exec).IsTaskAssigner = true;
                old.Context.SaveChanges();
            }

            using (var healed = DatabaseManager.Open(1, "старая", path))
            {
                var fields = healed.Constructor.GetFields(col);

                // Исполнитель остался исполнителем и больше не несет чужую роль…
                var execField = fields.Single(f => f.Id == exec);
                Assert.True(execField.IsTaskExec);
                Assert.False(execField.IsTaskAssigner);

                // …а «Назначил» — снова отдельное поле.
                var assigner = Assert.Single(fields, f => f.IsTaskAssigner);
                Assert.NotEqual(exec, assigner.Id);
                Assert.Equal("Назначил", assigner.Name);
                Assert.Equal("user", assigner.FieldType);

                // Повторное монтирование уже ничего не чинит.
                Assert.Equal(0, healed.Constructor.RepairTaskFieldRoles());
            }
        }
        finally { TempDatabase.Cleanup(path); }
    }

    /// <summary>
    /// База, где исполнителей несколько (раньше это было штатно) и где роль осталась на поле
    /// несовместимого типа (столбец «Статус» переключили на «Текст» до появления запрета).
    /// Монтирование приводит разметку к нынешним правилам: исполнитель один, роли — только на
    /// своих типах, недостающие роли заводятся заново.
    /// </summary>
    [Fact]
    public void Mounting_heals_extra_executors_and_roles_left_on_wrong_types()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            int col, first, second, status;
            using (var old = DatabaseManager.Open(1, "старая", path))
            {
                var repo = old.Constructor;
                col    = SampleData.AddCollection(repo, "Поручения").Id!.Value;
                first  = SampleData.AddField(repo, col, "Исполнитель", "user", position: 1000).Id!.Value;
                repo.SetUserFieldTaskExec(first, true);

                second = SampleData.AddField(repo, col, "Проверяющий", "user", position: 5000).Id!.Value;
                status = old.Context.DbFields.Single(f => f.CollectionId == col && f.IsTaskStatus).Id;

                // Состояние базы, открытой ДО нынешних правил: второй исполнитель и «Статус»,
                // переключенный на текст (флаг роли при этом остался висеть).
                old.Context.DbFields.Single(f => f.Id == second).IsTaskExec = true;
                old.Context.DbFields.Single(f => f.Id == status).FieldType  = "text";
                old.Context.SaveChanges();
            }

            using (var healed = DatabaseManager.Open(1, "старая", path))
            {
                var fields = healed.Constructor.GetFields(col);

                // Исполнитель — первый по позиции; со второго флаг снят, само поле цело.
                Assert.Equal(first, healed.Constructor.TaskExecFieldId(col));
                Assert.False(fields.First(f => f.Id == second).IsTaskExec);
                Assert.Equal("user", fields.First(f => f.Id == second).FieldType);

                // Текстовый «Статус» роль потерял и остался обычным столбцом…
                var textStatus = fields.First(f => f.Id == status);
                Assert.False(textStatus.IsTaskStatus);
                Assert.Equal("text", textStatus.FieldType);

                // …а роль ушла новому полю своего типа.
                var role = Assert.Single(fields, f => f.IsTaskStatus);
                Assert.Equal("status", role.FieldType);
                Assert.NotEqual(status, role.Id);

                // Повторное монтирование уже ничего не чинит.
                Assert.Equal(0, healed.Constructor.RepairTaskFieldRoles());
            }
        }
        finally { TempDatabase.Cleanup(path); }
    }

    [Fact]
    public void Moving_task_into_foreign_collection_does_not_hijack_its_user_field()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var tasks = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var exec  = SampleData.AddField(repo, tasks, "Исполнитель", "user", position: 1000);
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);

        // Обычная коллекция: в задачах не участвует, но свое user-поле у нее есть
        // и оно заполнено — именно его прежняя версия переноса делала полем задач.
        var clients  = SampleData.AddCollection(repo, "Клиенты").Id!.Value;
        var manager  = SampleData.AddField(repo, clients, "Менеджер", "user", position: 1000).Id!.Value;
        var client   = SampleData.AddRecord(repo, clients, "ООО «Ромашка»");
        repo.SetFieldValue(client.Id!.Value, manager, "Сидоров Семен");

        repo.AddTaskRecord(tasks, "Течь в подвале", "Иванов Иван", "Петров Петр",
                           ConstructorRepository.TaskStatusActive,
                           due: new DateValue(new DateTime(2026, 4, 1, 9, 0, 0),
                                              new DateTime(2026, 4, 1, 10, 0, 0), false));

        var task = Assert.Single(repo.GetTaskRecords());
        task.CollectionId = clients;
        repo.UpdateTaskRecord(task);

        // «Менеджер» остался обычным свойством: раздел не забрал его значения себе — «Сидоров
        // Семен» нигде не показан исполнителем.
        Assert.False(repo.GetFields(clients).Single(f => f.Id == manager).IsTaskExec);
        Assert.DoesNotContain(repo.GetTaskRecords(), t => t.Executor == "Сидоров Семен");

        // Зато сама коллекция теперь в разделе (у нее появилось поле-исполнитель), поэтому ее
        // запись видна как нераспределенная задача — с пустым исполнителем, а не с «Менеджером».
        var clientCard = repo.GetTaskRecords().Single(t => t.Title == "ООО «Ромашка»");
        Assert.Equal("", clientCard.Executor);

        // Задача переехала целиком и лежит в собственном поле цели.
        var moved = repo.GetTaskRecords().Single(t => t.Title == "Течь в подвале");
        Assert.Equal(clients, moved.CollectionId);
        Assert.Equal("Иванов Иван", moved.Executor);
        Assert.Equal("Петров Петр", moved.Assigner);
        Assert.Equal(ConstructorRepository.TaskStatusActive, moved.Status);
        Assert.NotEqual(manager, moved.ExecFieldId);
    }

    [Fact]
    public void Dateless_task_ignores_collection_change_and_touches_nothing_in_target()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var source = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var exec   = SampleData.AddField(repo, source, "Исполнитель", "user", position: 1000);
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);
        repo.AddTaskRecord(source, "Разобрать архив", "Иванов Иван", "Петров Петр",
                           ConstructorRepository.TaskStatusPlanned, due: null);

        var target       = SampleData.AddCollection(repo, "Ремонты").Id!.Value;
        var fieldsBefore = repo.GetFields(target).Count;

        // Диалог гасит выбор коллекции у задачи «без даты», но выбранное значение все равно
        // приезжает в кандидате (пользователь мог выбрать коллекцию, а потом снять срок).
        // Переносить нечем — календарного пути нет, и трогать цель мы не имеем права.
        var task = Assert.Single(repo.GetTaskRecords());
        task.CollectionId = target;
        repo.UpdateTaskRecord(task);

        Assert.Equal(1, repo.CountRecords(source));
        Assert.Equal(0, repo.CountRecords(target));
        Assert.Equal(fieldsBefore, repo.GetFields(target).Count);   // в цели не создано ни одного поля

        // Значения остались на полях СВОЕЙ коллекции — задача цела и видна в разделе.
        var after = Assert.Single(repo.GetTaskRecords());
        Assert.Equal(source, after.CollectionId);
        Assert.Equal(exec.Id!.Value, after.ExecFieldId);
        Assert.Equal("Иванов Иван", after.Executor);
        Assert.Equal("Петров Петр", after.Assigner);
    }

    [Fact]
    public void Move_is_refused_before_the_record_leaves_its_collection()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var source = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var exec   = SampleData.AddField(repo, source, "Исполнитель", "user", position: 1000);
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);
        repo.AddTaskRecord(source, "Течь в подвале", "Иванов Иван", "Петров Петр",
                           ConstructorRepository.TaskStatusActive,
                           due: new DateValue(new DateTime(2026, 4, 1, 9, 0, 0),
                                              new DateTime(2026, 4, 1, 10, 0, 0), false));

        var target = SampleData.AddCollection(repo, "Ремонты").Id!.Value;
        var task   = Assert.Single(repo.GetTaskRecords());

        RevokeFieldRights(db);   // права на записи есть, на свойства — нет

        task.CollectionId = target;
        Assert.Throws<PermissionDeniedException>(() => repo.UpdateTaskRecord(task));

        // Ключевое: отказ случился ДО переноса. Раньше запись успевала пересоздаться
        // в цели и потерять исполнителя — задача исчезала из раздела вместе со значениями.
        Assert.Equal(1, repo.CountRecords(source));
        Assert.Equal(0, repo.CountRecords(target));

        var intact = Assert.Single(repo.GetTaskRecords());
        Assert.Equal(source, intact.CollectionId);
        Assert.Equal("Иванов Иван", intact.Executor);
        Assert.Equal("Петров Петр", intact.Assigner);
        Assert.Equal(ConstructorRepository.TaskStatusActive, intact.Status);
    }
}
