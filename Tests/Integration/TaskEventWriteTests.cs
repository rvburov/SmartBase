using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Запись очереди уведомлений по задачам (task_events, ConstructorRepository.TaskEvents.cs).
///
/// Событие рождается в ЕДИНОЙ точке правки значения — SetFieldValue, — поэтому проверять надо
/// не «метод отработал», а границы: правка обычного свойства молчит, свое назначение молчит,
/// первичная простановка стадии молчит, массовые пути молчат. Ложное уведомление здесь хуже
/// пропущенного: баннер на каждое сохранение ячейки сделал бы фичу невыносимой.
/// </summary>
public sealed class TaskEventWriteTests
{
    // ── Обвязка ───────────────────────────────────────────────────────────────

    // Включает коллекцию в задачи так же, как галочка в сайдбаре: поле-исполнитель + авто-роли.
    private static (int ExecId, int AssignerId, int StatusId) EnableTasks(
        ConstructorRepository repo, int collectionId, string name = "Исполнитель")
    {
        var exec = SampleData.AddField(repo, collectionId, name, "user", position: 1000);
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);
        var roles = repo.TaskRoleFields(collectionId);
        return (exec.Id!.Value, roles.AssignerId!.Value, roles.StatusId!.Value);
    }

    // Регистрация соседа в базе — как UserDirectory при монтировании на его машине.
    private static void JoinUser(string path, string key, string name, bool approved = true)
    {
        using var neighbour = new AppDbContext(path);
        neighbour.DbUsers.Add(new DbUserEntity
        {
            UserKey = key, DisplayName = name, IsApproved = approved,
            CreatedAt = DateTime.UtcNow.ToString("o"),
        });
        neighbour.SaveChanges();
    }

    private const string Neighbour = "Петров Петр";

    private static List<TaskEventEntity> Events(TempDatabase db) =>
        db.Context.TaskEvents.AsNoTracking().OrderBy(e => e.Id).ToList();

    // ── Назначение исполнителя ────────────────────────────────────────────────

    [Fact]
    public void Assigning_a_neighbour_queues_one_event_addressed_to_them()
    {
        using var db = new TempDatabase();
        JoinUser(db.Path, "petrov@pc2", Neighbour);
        var repo = db.Constructor;

        var col  = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var (execId, _, _) = EnableTasks(repo, col);
        var rec = SampleData.AddRecord(repo, col, "Заменить насос").Id!.Value;

        repo.SetFieldValue(rec, execId, Neighbour);

        var e = Assert.Single(Events(db));
        Assert.Equal(TaskEventKinds.Assigned, e.Kind);
        Assert.Equal("petrov@pc2", e.RecipientKey);       // имя разрешилось в ключ по db_users
        Assert.Equal(Neighbour,    e.RecipientName);
        Assert.Equal(CurrentUser.Key, e.ActorKey);
        Assert.Equal(rec,     e.RecordId);
        Assert.Equal(col,     e.CollectionId);
        Assert.Equal(execId,  e.FieldId);
        Assert.Equal(execId,  e.ExecFieldId);             // по нему клик откроет карточку
        Assert.Equal("Заменить насос", e.TaskTitle);
        // Новая запись автозаполняет user-поля текущим пользователем (AddRecord), поэтому
        // «было» — это я: исполнителя сменили с меня на соседа.
        Assert.Equal(CurrentUser.DisplayName, e.OldValue);
        Assert.Equal(Neighbour, e.NewValue);
    }

    [Fact]
    public void Assigning_a_task_to_myself_queues_nothing()
    {
        using var db = new TempDatabase();
        JoinUser(db.Path, "petrov@pc2", Neighbour);
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Мои дела").Id!.Value;
        var (execId, _, _) = EnableTasks(repo, col);
        var rec = SampleData.AddRecord(repo, col, "Позвонить").Id!.Value;

        repo.SetFieldValue(rec, execId, Neighbour);            // соседу — событие есть
        repo.SetFieldValue(rec, execId, CurrentUser.DisplayName);   // себе — молчим

        // Баннер о задаче, которую сам себе и забрал, — чистый шум.
        var e = Assert.Single(Events(db));
        Assert.Equal("petrov@pc2", e.RecipientKey);
    }

    [Fact]
    public void Unknown_executor_name_is_queued_without_a_key()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var (execId, _, _) = EnableTasks(repo, col);
        var rec = SampleData.AddRecord(repo, col, "Задача").Id!.Value;

        // Имя вписали руками: такого пользователя база еще не знает. Событие все равно
        // кладем — человек опознает себя по имени, когда откроет базу.
        repo.SetFieldValue(rec, execId, "Сидоров Семен");

        var e = Assert.Single(Events(db));
        Assert.Equal("", e.RecipientKey);
        Assert.Equal("Сидоров Семен", e.RecipientName);
    }

    [Fact]
    public void Clearing_the_executor_queues_nothing()
    {
        using var db = new TempDatabase();
        JoinUser(db.Path, "petrov@pc2", Neighbour);
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var (execId, _, _) = EnableTasks(repo, col);
        var rec = SampleData.AddRecord(repo, col, "Задача").Id!.Value;
        repo.SetFieldValue(rec, execId, Neighbour);

        repo.SetFieldValue(rec, execId, "");   // исполнителя сняли — уведомлять некого

        Assert.Single(Events(db));
    }

    // ── Смена стадии ──────────────────────────────────────────────────────────

    [Fact]
    public void Status_change_goes_to_the_assigner_even_when_they_change_it_themselves()
    {
        using var db = new TempDatabase();
        JoinUser(db.Path, "petrov@pc2", Neighbour);
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var (execId, assignerId, statusId) = EnableTasks(repo, col);
        var rec = SampleData.AddRecord(repo, col, "Заменить насос").Id!.Value;

        repo.SetFieldValue(rec, execId,     Neighbour);
        repo.SetFieldValue(rec, assignerId, CurrentUser.DisplayName);
        repo.SetFieldValue(rec, statusId,   ConstructorRepository.TaskStatusPlanned);

        // Стадию двигает сам назначивший: он ждет сигнала о движении задачи и в этом случае.
        repo.SetFieldValue(rec, statusId, ConstructorRepository.TaskStatusActive);

        var status = Events(db).Single(e => e.Kind == TaskEventKinds.Status);
        Assert.Equal(CurrentUser.Key, status.RecipientKey);
        Assert.Equal(ConstructorRepository.TaskStatusPlanned, status.OldValue);
        Assert.Equal(ConstructorRepository.TaskStatusActive,  status.NewValue);
        Assert.Equal(statusId, status.FieldId);
        Assert.Equal(execId,   status.ExecFieldId);   // карточку откроет поле-исполнитель
    }

    [Fact]
    public void First_status_value_is_creation_and_not_a_change()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var (_, assignerId, statusId) = EnableTasks(repo, col);
        var rec = SampleData.AddRecord(repo, col, "Задача").Id!.Value;
        repo.SetFieldValue(rec, assignerId, CurrentUser.DisplayName);

        repo.SetFieldValue(rec, statusId, ConstructorRepository.TaskStatusPlanned);

        // Иначе каждое создание задачи сразу давало бы баннер «стадия: Запланировано».
        Assert.Empty(Events(db));
    }

    [Fact]
    public void Status_change_without_an_assigner_queues_nothing()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var (_, assignerId, statusId) = EnableTasks(repo, col);
        var rec = SampleData.AddRecord(repo, col, "Ничья задача").Id!.Value;
        // Новая запись автозаполняет «Назначил» текущим пользователем — чистим, чтобы
        // получилась именно ничья задача.
        repo.SetFieldValue(rec, assignerId, "");

        repo.SetFieldValue(rec, statusId, ConstructorRepository.TaskStatusPlanned);
        repo.SetFieldValue(rec, statusId, ConstructorRepository.TaskStatusDone);

        Assert.Empty(Events(db));   // задачу никто не назначал — адресата нет
    }

    // ── Границы ───────────────────────────────────────────────────────────────

    [Fact]
    public void Editing_ordinary_fields_queues_nothing()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);
        var text   = SampleData.AddField(repo, col, "Комментарий", "text",   position: 2000);
        var number = SampleData.AddField(repo, col, "Цена",        "number", position: 3000);
        var rec = SampleData.AddRecord(repo, col, "Задача").Id!.Value;

        repo.SetFieldValue(rec, text.Id!.Value,   "любой текст");
        repo.SetFieldValue(rec, number.Id!.Value, "10.5");
        repo.SetRecordTitle(rec, "Другое название");

        // Гарантия «обычная работа с таблицей ничего не стоит»: очередь пуста.
        Assert.Empty(Events(db));
    }

    [Fact]
    public void Role_flag_on_an_incompatible_type_does_not_fire()
    {
        using var db = new TempDatabase();
        JoinUser(db.Path, "petrov@pc2", Neighbour);
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var (execId, _, _) = EnableTasks(repo, col);

        // Тип поля-исполнителя сменили на текст: роль по правилу HasRole больше не действует
        // (флаг мог остаться на старых базах) — событий быть не должно.
        var exec = repo.GetFields(col).First(f => f.Id == execId);
        exec.FieldType = "text";
        repo.UpdateField(exec);

        var rec = SampleData.AddRecord(repo, col, "Задача").Id!.Value;
        repo.SetFieldValue(rec, execId, Neighbour);

        Assert.Empty(Events(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Quick_add_notifies_the_executor_once_and_says_nothing_about_the_initial_status(bool withDue)
    {
        using var db = new TempDatabase();
        JoinUser(db.Path, "petrov@pc2", Neighbour);
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);

        // Путь быстрого добавления раздела «Задачи»: исполнитель — сосед, назначил — я.
        repo.AddTaskRecord(col, "Проверить стенд", Neighbour, CurrentUser.DisplayName,
                           ConstructorRepository.TaskStatusPlanned, due: withDue
                               ? new DateValue(new DateTime(2026, 10, 28), new DateTime(2026, 10, 28), true) : null);

        var e = Assert.Single(Events(db));
        Assert.Equal(TaskEventKinds.Assigned, e.Kind);
        Assert.Equal("petrov@pc2", e.RecipientKey);
        Assert.Equal(CurrentUser.DisplayName, e.OldValue);
        Assert.Equal(Neighbour, e.NewValue);
    }

    [Fact]
    public void Duplicating_a_task_queues_nothing()
    {
        using var db = new TempDatabase();
        JoinUser(db.Path, "petrov@pc2", Neighbour);
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, col);
        repo.AddTaskRecord(col, "Задача", Neighbour, CurrentUser.DisplayName,
                           ConstructorRepository.TaskStatusPlanned, due: null);
        var before = Events(db).Count;

        var original = repo.GetRecords(col).First();
        repo.DuplicateRecord(original.Id!.Value);

        // Копия — не назначение: массовые пути пишут значения мимо SetFieldValue намеренно.
        Assert.Equal(before, Events(db).Count);
    }

    /// <summary>Вставка исполнителя из буфера — это назначение, как правка одной ячейки: сосед
    /// получает уведомление. Пакетная запись дает его, пока правок ролей немного.</summary>
    [Fact]
    public void Pasting_an_executor_queues_the_same_event_as_a_single_edit()
    {
        using var db = new TempDatabase();
        JoinUser(db.Path, "petrov@pc2", Neighbour);
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        var (execId, _, _) = EnableTasks(repo, col);
        var rec = SampleData.AddRecord(repo, col, "Заменить насос").Id!.Value;
        var before = Events(db).Count;

        repo.SetCellsBulk(col, [new ConstructorRepository.CellWrite(rec, execId, Neighbour)]);

        var e = Assert.Single(Events(db).Skip(before));
        Assert.Equal(TaskEventKinds.Assigned, e.Kind);
        Assert.Equal("petrov@pc2", e.RecipientKey);
        Assert.Equal("Заменить насос", e.TaskTitle);
    }

    [Fact]
    public void Moving_a_record_to_another_collection_queues_nothing()
    {
        using var db = new TempDatabase();
        JoinUser(db.Path, "petrov@pc2", Neighbour);
        var repo = db.Constructor;

        var src = SampleData.AddCollection(repo, "Исходная").Id!.Value;
        var dst = SampleData.AddCollection(repo, "Целевая").Id!.Value;
        var (execId, _, _) = EnableTasks(repo, src);
        EnableTasks(repo, dst);

        var rec = SampleData.AddRecord(repo, src, "Задача").Id!.Value;
        repo.SetFieldValue(rec, execId, Neighbour);
        var before = Events(db).Count;

        var moved = SampleData.AddRecord(repo, dst, "Задача").Id!.Value;
        repo.CopyFieldValuesByName(repo, rec, src, moved, dst, createMissingFields: true);

        // Значения переехали в ДРУГУЮ запись — исполнитель у задачи не менялся.
        Assert.Equal(before, Events(db).Count);
    }

}
