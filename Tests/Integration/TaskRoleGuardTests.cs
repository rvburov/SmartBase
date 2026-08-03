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
/// учётом регистра, а перенос задачи в постороннюю коллекцию «усыновлял» её чужое
/// user-поле. Всё это молчаливые порчи данных: пользователь не видит ошибки, он видит
/// затёртого исполнителя или раздел задач, залитый чужими записями.
/// </summary>
public sealed class TaskRoleGuardTests
{
    private static string ValueOf(ConstructorRepository repo, int recordId, int fieldId) =>
        repo.GetFieldValuesBatch([recordId]).TryGetValue(recordId, out var values)
            ? values.GetValueOrDefault(fieldId, "")
            : "";

    // Понижает до допущенного не-администратора без прав на свойства: маски начинают
    // действовать только для не-админа (администратору разрешено всё).
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

        repo.AddTaskRecord(col, "Проверить насос", "Иванов Иван", "Петров Пётр",
                           ConstructorRepository.TaskStatusPlanned, due: null);

        var fields   = repo.GetFields(col);
        var assigner = fields.Single(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        var exec     = Assert.Single(fields.Where(f => f.IsTaskExec));

        Assert.True(assigner.IsTaskAssigner);
        Assert.False(assigner.IsTaskExec);
        Assert.NotEqual(assigner.Id, exec.Id);

        var task = Assert.Single(repo.GetTaskRecords());
        Assert.Equal("Иванов Иван", task.Executor);
        Assert.Equal("Петров Пётр", task.Assigner);
        Assert.Equal("Иванов Иван", ValueOf(repo, task.Id!.Value, exec.Id!.Value));
    }

    [Fact]
    public void Moving_task_into_foreign_collection_does_not_hijack_its_user_field()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var tasks = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var exec  = SampleData.AddField(repo, tasks, "Исполнитель", "user", position: 1000);
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);

        // Обычная коллекция: в задачах не участвует, но своё user-поле у неё есть
        // и оно заполнено — именно его прежняя версия переноса делала полем задач.
        var clients  = SampleData.AddCollection(repo, "Клиенты").Id!.Value;
        var manager  = SampleData.AddField(repo, clients, "Менеджер", "user", position: 1000).Id!.Value;
        var client   = SampleData.AddRecord(repo, clients, "ООО «Ромашка»");
        repo.SetFieldValue(client.Id!.Value, manager, "Сидоров Семён");

        repo.AddTaskRecord(tasks, "Течь в подвале", "Иванов Иван", "Петров Пётр",
                           ConstructorRepository.TaskStatusActive,
                           due: new DateValue(new DateTime(2026, 4, 1, 9, 0, 0),
                                              new DateTime(2026, 4, 1, 10, 0, 0), false));

        var task = Assert.Single(repo.GetTaskRecords());
        task.CollectionId = clients;
        repo.UpdateTaskRecord(task);

        // «Менеджер» остался обычным свойством: записи клиентов не превратились в задачи.
        Assert.False(repo.GetFields(clients).Single(f => f.Id == manager).IsTaskExec);
        Assert.DoesNotContain(repo.GetTaskRecords(), t => t.Executor == "Сидоров Семён");

        // Задача переехала целиком и лежит в собственном поле цели.
        var moved = Assert.Single(repo.GetTaskRecords());
        Assert.Equal(clients, moved.CollectionId);
        Assert.Equal("Иванов Иван", moved.Executor);
        Assert.Equal("Петров Пётр", moved.Assigner);
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
        repo.AddTaskRecord(source, "Разобрать архив", "Иванов Иван", "Петров Пётр",
                           ConstructorRepository.TaskStatusPlanned, due: null);

        var target       = SampleData.AddCollection(repo, "Ремонты").Id!.Value;
        var fieldsBefore = repo.GetFields(target).Count;

        // Диалог гасит выбор коллекции у задачи «без даты», но выбранное значение всё равно
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
        Assert.Equal("Петров Пётр", after.Assigner);
    }

    [Fact]
    public void Move_is_refused_before_the_record_leaves_its_collection()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var source = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var exec   = SampleData.AddField(repo, source, "Исполнитель", "user", position: 1000);
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);
        repo.AddTaskRecord(source, "Течь в подвале", "Иванов Иван", "Петров Пётр",
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
        Assert.Equal("Петров Пётр", intact.Assigner);
        Assert.Equal(ConstructorRepository.TaskStatusActive, intact.Status);
    }
}
